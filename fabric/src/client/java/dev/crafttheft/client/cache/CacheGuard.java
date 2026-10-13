package dev.crafttheft.client.cache;

import java.io.File;
import java.io.Reader;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.List;
import java.util.Properties;

import org.jetbrains.annotations.Nullable;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import net.fabricmc.loader.api.FabricLoader;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.network.chat.Component;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.config.CacheManifest;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.melty.MeltyChecks;

/**
 * hooks row client_started. On client start: no usable manifest.json (missing, unreadable, cacheSchema &lt; 1) ->
 * run the extractor ("extractor --config &lt;crafttheft.properties&gt;") and block play behind a progress screen that
 * reads progress.json; a game whose manifest status is "failed" -> re-run just that game in the background
 * ("--only &lt;game&gt;", at most {@link #MAX_RETRIES} consecutive times per game). Each run that finishes reloads the
 * resource packs so the new content appears. Exit code 4 (another extractor holds the cache lock) means: wait for
 * that one instead (progress.json / manifest.json).
 */
public final class CacheGuard {
	public enum Mode { IDLE, BLOCKING, BACKGROUND, WAITING_OTHER }

	public static final int MAX_RETRIES = 3;
	public static volatile Mode mode = Mode.IDLE;
	public static volatile boolean skipped, blockingFinished;
	public static volatile String lastMessage = "";
	public static volatile int lastExitCode = Integer.MIN_VALUE, runs, reloadsDone;
	public static volatile long startedAtMs;
	@Nullable private static volatile Process process;
	private static final Deque<String> BACKGROUND_QUEUE = new ArrayDeque<>();
	private static final List<String> HISTORY = new ArrayList<>();
	private static boolean cacheReadyReported;
	private static int pollTicks;

	private CacheGuard() {}

	public static void onClientStarted(Minecraft mc) {
		Hooks.mark("client_started", "CacheGuard.onClientStarted");
		ModConfig cfg = ModConfig.reload();
		CacheManifest m = CacheManifest.read(cfg.cacheDir);
		if (!m.usable()) {
			CraftTheft.LOG.info("[cache-guard] {}: a full extraction is needed", m.problem);
			Path exe = extractor(cfg);
			if (exe == null) {
				lastMessage = "no extractor configured or found (" + cfg.extractor + "): playing with placeholders";
				CraftTheft.LOG.warn("[cache-guard] {}", lastMessage);
				return;
			}
			if (start(mc, exe, cfg, List.of(), Mode.BLOCKING)) {
				if (mc.screen instanceof TitleScreen) mc.setScreen(new ExtractProgressScreen());
			}
			return;
		}
		CraftTheft.LOG.info("[cache-guard] manifest {} (finished {}), games {}", m.status, m.finishedUtc, m.games);
		Properties retries = loadRetries();
		for (var e : m.games.entrySet()) {
			String game = e.getKey();
			if ("failed".equals(e.getValue().status())) {
				int n = retryCount(retries, game);
				if (n >= MAX_RETRIES) {
					CraftTheft.LOG.info("[cache-guard] {} failed again ({}), already retried {} times in a row: not retrying (error: {})", game, e.getValue().status(), n, e.getValue().error());
					continue;
				}
				retries.setProperty(game, Integer.toString(n + 1));
				synchronized (BACKGROUND_QUEUE) { BACKGROUND_QUEUE.add(game); }
				CraftTheft.LOG.info("[cache-guard] {} failed last time ({}): re-reading it in the background (attempt {})", game, e.getValue().error(), n + 1);
			} else retries.remove(game);
		}
		saveRetries(retries);
		startNextBackground(mc);
	}

	/** The extractor executable from crafttheft.properties, if it exists. */
	@Nullable
	public static Path extractor(ModConfig cfg) {
		if (cfg.extractor == null) return null;
		Path p = Paths.get(cfg.extractor);
		if (!p.isAbsolute()) p = FabricLoader.getInstance().getGameDir().resolve(p).normalize();
		if (!Files.isRegularFile(p)) {
			CraftTheft.LOG.warn("[cache-guard] extractor {} not found", p);
			return null;
		}
		return p;
	}

	private static void startNextBackground(Minecraft mc) {
		String game;
		synchronized (BACKGROUND_QUEUE) { game = BACKGROUND_QUEUE.poll(); }
		if (game == null) return;
		ModConfig cfg = ModConfig.get();
		Path exe = extractor(cfg);
		if (exe == null) return;
		start(mc, exe, cfg, List.of("--only", game), Mode.BACKGROUND);
	}

	private static boolean start(Minecraft mc, Path exe, ModConfig cfg, List<String> extra, Mode newMode) {
		List<String> cmd = new ArrayList<>();
		cmd.add(exe.toString());
		cmd.add("--config");
		cmd.add(cfg.propertiesFile().toString());
		cmd.addAll(extra);
		try {
			Path log = FabricLoader.getInstance().getGameDir().resolve("logs").resolve("crafttheft-extractor.log");
			Files.createDirectories(log.getParent());
			ProcessBuilder pb = new ProcessBuilder(cmd).directory(exe.getParent().toFile()).redirectErrorStream(true)
					.redirectOutput(ProcessBuilder.Redirect.appendTo(log.toFile()));
			Process proc = pb.start();
			process = proc;
			mode = newMode;
			startedAtMs = System.currentTimeMillis();
			runs++;
			HISTORY.add(String.join(" ", extra.isEmpty() ? List.of("full") : extra));
			lastMessage = "running " + cmd;
			CraftTheft.LOG.info("[cache-guard] started extractor ({}) pid {}: {} (output: {})", newMode, proc.pid(), cmd, log);
			proc.onExit().thenAccept(p -> mc.execute(() -> finished(mc, p.exitValue(), newMode, extra)));
			return true;
		} catch (Exception e) {
			lastMessage = "could not start the extractor: " + e;
			CraftTheft.LOG.error("[cache-guard] could not start {}: {}", cmd, e.toString());
			mode = Mode.IDLE;
			return false;
		}
	}

	private static void finished(Minecraft mc, int code, Mode was, List<String> extra) {
		lastExitCode = code;
		process = null;
		CraftTheft.LOG.info("[cache-guard] extractor ({} {}) exited with code {} after {} s", was, extra, code, (System.currentTimeMillis() - startedAtMs) / 1000);
		if (code == 4) {
			// another extractor (e.g. started by Melty) holds the lock: wait for its manifest instead of fighting it
			mode = Mode.WAITING_OTHER;
			lastMessage = "another extractor is running; waiting for it";
			return;
		}
		if (code == 3) CraftTheft.LOG.error("[cache-guard] the extractor rejected its arguments (exit 3); check {}", ModConfig.get().file);
		reload(mc, was, extra);
	}

	private static void reload(Minecraft mc, Mode was, List<String> extra) {
		mode = Mode.IDLE;
		CacheManifest m = CacheManifest.read(ModConfig.get().cacheDir);
		CraftTheft.LOG.info("[cache-guard] reloading resources: manifest {} {}", m.status, m.problem == null ? m.games : m.problem);
		if (was == Mode.BLOCKING) blockingFinished = true;
		mc.reloadResourcePacks().thenRun(() -> mc.execute(() -> {
			reloadsDone++;
			if (mc.screen instanceof ExtractProgressScreen) mc.setScreen(new TitleScreen());
			if (was == Mode.BACKGROUND && mc.player != null)
				mc.player.displayClientMessage(Component.translatable("crafttheft.extract.updated", String.join(" ", extra)), false);
			if (was == Mode.BACKGROUND) startNextBackground(mc);
			else {
				// a full run can itself report failed games: those get their background retry too
				Properties retries = loadRetries();
				for (var e : m.games.entrySet())
					if ("failed".equals(e.getValue().status()) && retryCount(retries, e.getKey()) < MAX_RETRIES) {
						retries.setProperty(e.getKey(), Integer.toString(retryCount(retries, e.getKey()) + 1));
						synchronized (BACKGROUND_QUEUE) { BACKGROUND_QUEUE.add(e.getKey()); }
					}
				saveRetries(retries);
				startNextBackground(mc);
			}
		}));
	}

	/** Polls while another extractor runs (called from the client tick). */
	public static void tick(Minecraft mc) {
		if (mode != Mode.WAITING_OTHER || ++pollTicks % 20 != 0) return;
		JsonObject prog = readProgress();
		boolean done = prog != null && prog.get("finished") instanceof com.google.gson.JsonPrimitive f && f.isBoolean() && f.getAsBoolean();
		if (done || (System.currentTimeMillis() - startedAtMs > 30 * 60_000L)) {
			Mode was = skipped ? Mode.BACKGROUND : Mode.BLOCKING;
			reload(mc, was, List.of("(other extractor)"));
		}
	}

	/** Melty check cache-ready: evaluated once, the first time the title screen shows with no blocking run left. */
	public static void evaluateCacheReady(Minecraft mc) {
		if (cacheReadyReported || mode == Mode.BLOCKING || mode == Mode.WAITING_OTHER) return;
		cacheReadyReported = true;
		ModConfig cfg = ModConfig.get();
		CacheManifest m = CacheManifest.read(cfg.cacheDir);
		List<String> mounted = mc.getResourcePackRepository().getSelectedIds().stream().filter(id -> id.startsWith(CachePacks.PREFIX)).toList();
		if (m.problem == null && !mounted.isEmpty())
			MeltyChecks.pass(MeltyChecks.CACHE_READY, "manifest " + m.status + ", packs " + mounted);
		else
			MeltyChecks.fail(MeltyChecks.CACHE_READY, (m.problem != null ? m.problem : "manifest " + m.status) + (mounted.isEmpty() ? "; no cache pack mounted from " + cfg.cacheDir.resolve("packs") : "") + (lastMessage.isEmpty() ? "" : "; " + lastMessage));
	}

	@Nullable
	public static JsonObject readProgress() {
		Path f = ModConfig.get().cacheDir.resolve("progress.json");
		if (!Files.isRegularFile(f)) return null;
		try (Reader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
			var e = JsonParser.parseReader(r);
			return e.isJsonObject() ? e.getAsJsonObject() : null;
		} catch (Exception e) {
			return null; // being rewritten right now
		}
	}

	public static boolean running() { Process p = process; return p != null && p.isAlive(); }

	public static List<String> history() { return List.copyOf(HISTORY); }

	public static boolean backgroundPending() { synchronized (BACKGROUND_QUEUE) { return !BACKGROUND_QUEUE.isEmpty(); } }

	/** Retry count of a game; a hand-edited or damaged value counts as 0 (Integer.parseInt here used to throw out of
	 * CLIENT_STARTED and crash the game at startup). */
	static int retryCount(Properties retries, String game) {
		try {
			return Math.max(0, Integer.parseInt(retries.getProperty(game, "0").trim()));
		} catch (NumberFormatException e) {
			CraftTheft.LOG.warn("[cache-guard] {}: unreadable retry count '{}' for {}, counting 0", retriesFile(), retries.getProperty(game), game);
			return 0;
		}
	}

	private static Path retriesFile() { return FabricLoader.getInstance().getConfigDir().resolve("crafttheft-guard.properties"); }

	private static Properties loadRetries() {
		Properties p = new Properties();
		File f = retriesFile().toFile();
		if (f.isFile()) try (Reader r = Files.newBufferedReader(f.toPath(), StandardCharsets.UTF_8)) { p.load(r); } catch (Exception ignored) { }
		return p;
	}

	private static void saveRetries(Properties p) {
		try (Writer w = Files.newBufferedWriter(retriesFile(), StandardCharsets.UTF_8)) {
			p.store(w, "Craft Theft Halo: consecutive background re-extractions per failed game (reset when the game is no longer failed)");
		} catch (Exception e) {
			CraftTheft.LOG.warn("[cache-guard] cannot write {}: {}", retriesFile(), e.toString());
		}
	}
}
