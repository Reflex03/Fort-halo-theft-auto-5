package dev.crafttheft.config;

import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.Locale;
import java.util.Properties;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import org.jetbrains.annotations.Nullable;

import net.fabricmc.loader.api.FabricLoader;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.gen.Sheets;

/**
 * config/crafttheft.properties, written by Melty before every Play (docs/CONTRACTS.md "Settings Melty writes"):
 * halo_dir, gta_dir, fortnite_dir, cache_dir, extractor. Read as UTF-8 with java.util.Properties (escapes, \\uXXXX).
 * Empty values and unexpanded Melty placeholders ({game:...}, {localappdata}) count as "not given".
 */
public final class ModConfig {
	public static final String FILE_NAME = "crafttheft.properties";
	private static final Pattern PLACEHOLDER = Pattern.compile("\\{[a-zA-Z_:.-]+\\}");
	private static final Pattern ENV = Pattern.compile("%([A-Za-z_][A-Za-z0-9_]*)%");

	private static volatile ModConfig current;

	public final Path file;
	public final boolean fileExists;
	@Nullable public final String haloDir, gtaDir, fortniteDir, extractor;
	public final Path cacheDir;
	public final String cacheDirSource;

	private ModConfig(Path file, Properties p, boolean exists) {
		this.file = file;
		this.fileExists = exists;
		this.haloDir = value(p, "halo_dir");
		this.gtaDir = value(p, "gta_dir");
		this.fortniteDir = value(p, "fortnite_dir");
		this.extractor = value(p, "extractor");
		String cache = System.getProperty("crafttheft.cacheDir");
		String src = "-Dcrafttheft.cacheDir";
		if (blank(cache)) { cache = value(p, "cache_dir"); src = FILE_NAME + " cache_dir"; }
		if (blank(cache)) { cache = expandEnv(Sheets.EXTRACTOR_CACHE_DIR); src = "default " + Sheets.EXTRACTOR_CACHE_DIR; }
		if (blank(cache)) {
			cache = Paths.get(System.getProperty("user.home"), ".local", "share", "CraftTheftHalo", "cache").toString();
			src = "fallback (no LOCALAPPDATA)";
		}
		this.cacheDir = Paths.get(cache).toAbsolutePath().normalize();
		this.cacheDirSource = src;
	}

	public static Path configFile() {
		return FabricLoader.getInstance().getConfigDir().resolve(FILE_NAME);
	}

	/** Re-reads the file (cheap; called on every pack reload so a re-written config is picked up). */
	public static ModConfig reload() {
		Path f = configFile();
		Properties p = new Properties();
		boolean exists = Files.isRegularFile(f);
		if (exists) {
			try (Reader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
				p.load(r);
			} catch (Exception e) {
				CraftTheft.LOG.warn("[config] cannot read {}: {}", f, e.toString());
			}
		}
		ModConfig c = new ModConfig(f, p, exists);
		ModConfig old = current;
		current = c;
		if (old == null || !old.cacheDir.equals(c.cacheDir))
			CraftTheft.LOG.info("[config] {} {}; cache dir {} (from {}); extractor {}", f, exists ? "read" : "missing", c.cacheDir, c.cacheDirSource, c.extractor);
		return c;
	}

	public static ModConfig get() {
		ModConfig c = current;
		return c != null ? c : reload();
	}

	@Nullable
	static String value(Properties p, String key) {
		String v = p.getProperty(key);
		if (v == null) return null;
		v = v.trim();
		// strip one pair of surrounding quotes (hand-edited files)
		if (v.length() >= 2 && (v.startsWith("\"") && v.endsWith("\""))) v = v.substring(1, v.length() - 1).trim();
		if (v.isEmpty() || PLACEHOLDER.matcher(v).find()) return null;
		return expandEnv(v);
	}

	/** Expands %VAR% from the environment; returns null when a variable is missing. */
	@Nullable
	public static String expandEnv(String s) {
		Matcher m = ENV.matcher(s);
		StringBuilder b = new StringBuilder();
		while (m.find()) {
			String env = System.getenv(m.group(1));
			if (env == null) env = System.getenv(m.group(1).toUpperCase(Locale.ROOT));
			if (env == null || env.isBlank()) return null;
			m.appendReplacement(b, Matcher.quoteReplacement(env));
		}
		m.appendTail(b);
		return b.toString();
	}

	static boolean blank(@Nullable String s) { return s == null || s.isBlank(); }

	/** The properties file as written for the extractor's --config (the extractor reads the same keys). */
	public Path propertiesFile() { return file; }
}
