package dev.crafttheft.harness;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.atomic.AtomicReference;
import java.util.function.BooleanSupplier;
import java.util.function.Supplier;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.mojang.blaze3d.platform.GlUtil;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.keybinding.v1.KeyBindingHelper;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.loader.api.FabricLoader;
import net.minecraft.client.CameraType;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.Minecraft;
import net.minecraft.client.Screenshot;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.client.gui.screens.inventory.ContainerScreen;
import net.minecraft.client.server.IntegratedServer;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.Difficulty;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.Mob;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.level.GameRules;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.Heightmap;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.Hooks;
import dev.crafttheft.bus.BattleBusEntity;
import dev.crafttheft.bus.Skydive;
import dev.crafttheft.client.ClientInput;
import dev.crafttheft.client.FirstRun;
import dev.crafttheft.client.cache.CacheGuard;
import dev.crafttheft.client.cache.CachePacks;
import dev.crafttheft.client.cache.ExtractProgressScreen;
import dev.crafttheft.client.render.MeshCache;
import dev.crafttheft.client.render.RuntimeMesh;
import dev.crafttheft.client.render.WeaponItemRenderer;
import dev.crafttheft.client.sound.SirenSound;
import dev.crafttheft.config.CacheManifest;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.content.ModAttachments;
import dev.crafttheft.content.ModBlocks;
import dev.crafttheft.content.ModEntities;
import dev.crafttheft.content.ModItems;
import dev.crafttheft.content.ModSounds;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.crime.CrimeSystem;
import dev.crafttheft.crime.PoliceDispatch;
import dev.crafttheft.crime.WantedSystem;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.heal.FortniteHealItem;
import dev.crafttheft.melty.MeltyChecks;
import dev.crafttheft.shield.ShieldSystem;
import dev.crafttheft.shield.ShieldTuning;
import dev.crafttheft.vehicle.CarEntity;
import dev.crafttheft.weapon.Explosions;
import dev.crafttheft.weapon.HaloGrenade;
import dev.crafttheft.weapon.HaloProjectile;
import dev.crafttheft.weapon.HaloWeaponItem;
import dev.crafttheft.weapon.WeaponStats;
import dev.crafttheft.weapon.WeaponSystem;
import dev.crafttheft.world.LootChestBlockEntity;
import dev.crafttheft.world.WorldSetup;
import dev.crafttheft.world.WorldState;

/**
 * Scripted end-to-end scenario for the crafttheft mod (a separate test mod, never shipped). Active with
 * -Dcrafttheft.harness=all|nocache. It plays the game the way a player would - title screen, first-start
 * extraction, Battle Bus, skydive, weapons, heals, crimes, cars, police, chests - through real input paths
 * (KeyMapping state, MultiPlayerGameMode attack/interact/useItemOn) and checks results on both sides.
 * Logs "[harness] CHECK name: OK|FAIL (detail)" and "[harness] RESULT pass=N fail=M", then stops the game.
 */
public final class Harness implements ClientModInitializer {
	static final String MODE = System.getProperty("crafttheft.harness", "");
	static boolean cache() { return !"nocache".equals(MODE); }

	interface StepFn { boolean run(int t); }

	private static final List<StepFn> steps = new ArrayList<>();
	private static final List<String> screens = Collections.synchronizedList(new ArrayList<>());
	private static final Map<String, String> sounds = Collections.synchronizedMap(new LinkedHashMap<>());
	private static int idx, stepTick, pass, fail;
	private static BlockPos origin;
	private static int y0;
	private static final AtomicReference<Object> scratch = new AtomicReference<>();
	private static double d0, d1, minVy;
	private static float f0, f1;
	private static int i0, i1;
	private static Vec3 v0;
	private static int carId = -1, targetId = -1;
	private static volatile BooleanSupplier releaseUseBeforeKeybinds;
	private static Object scratch2;

	@Override
	public void onInitializeClient() {
		if (MODE.isBlank()) return;
		CraftTheft.LOG.info("[harness] mode {}", MODE);
		ScreenEvents.AFTER_INIT.register((mc, screen, w, h) -> {
			String n = screen.getClass().getSimpleName();
			if (screens.isEmpty() || !screens.get(screens.size() - 1).equals(n)) screens.add(n);
		});
		// runs after the frame's packets but before Minecraft#handleKeybinds, so a held use key can be let go in the very
		// tick the server's "finished using" arrives (otherwise vanilla starts using the next item of the stack)
		ClientTickEvents.START_CLIENT_TICK.register(mc -> {
			BooleanSupplier f = releaseUseBeforeKeybinds;
			if (f != null && f.getAsBoolean()) { mc.options.keyUse.setDown(false); releaseUseBeforeKeybinds = null; }
		});
		ClientTickEvents.END_CLIENT_TICK.register(Harness::tick);
		buildScript();
	}

	// ------------------------------------------------------------------ helpers
	private static void add(StepFn f) { steps.add(f); }
	private static void waitTicks(int n) { add(t -> t >= n); }
	private static void run(Runnable r) { add(t -> { r.run(); return true; }); }
	private static void until(String check, int timeout, BooleanSupplier cond) {
		add(t -> {
			if (cond.getAsBoolean()) { check(check, true, "after " + t + " ticks"); return true; }
			if (t >= timeout) { check(check, false, "timed out after " + timeout + " ticks"); return true; }
			return false;
		});
	}
	private static void waitFor(int timeout, BooleanSupplier cond) { add(t -> cond.getAsBoolean() || t >= timeout); }
	private static void shot(String name) {
		run(() -> mc().getToasts().clear());
		waitTicks(2);
		run(() -> grab(name));
	}
	private static void grab(String name) {
		Minecraft mc = Minecraft.getInstance();
		Screenshot.grab(mc.gameDirectory, name + ".png", mc.getMainRenderTarget(), msg -> CraftTheft.LOG.info("[harness] screenshot {}: {}", name, msg.getString()));
	}
	static void check(String name, boolean ok, String detail) {
		if (ok) pass++; else fail++;
		CraftTheft.LOG.info("[harness] CHECK {}: {} ({})", name, ok ? "OK" : "FAIL", detail);
	}
	private static Minecraft mc() { return Minecraft.getInstance(); }
	private static IntegratedServer server() { return mc().getSingleplayerServer(); }
	private static ServerLevel sl() { return server().overworld(); }
	private static ServerPlayer sp() { return server().getPlayerList().getPlayers().get(0); }
	private static <T> T srv(Supplier<T> s) { return server().submit(s).join(); }
	private static void srvRun(Runnable r) { server().submit(r).join(); }
	private static void cmd(String c) { srvRun(() -> server().getCommands().performPrefixedCommand(server().createCommandSourceStack().withSuppressedOutput(), c)); }
	private static void look(Vec3 target) {
		var p = mc().player;
		Vec3 d = target.subtract(p.getEyePosition());
		float yaw = (float) (Math.atan2(d.z, d.x) * 180 / Math.PI) - 90f;
		float pitch = (float) -(Math.atan2(d.y, d.horizontalDistance()) * 180 / Math.PI);
		p.setYRot(yaw); p.setXRot(pitch); p.yRotO = yaw; p.xRotO = pitch;
		srvRun(() -> { sp().setYRot(yaw); sp().setXRot(pitch); });
	}
	private static float hp(int id) { return srv(() -> sl().getEntity(id) instanceof LivingEntity le && le.isAlive() ? le.getHealth() : 0f); }
	private static int spawnMob(EntityType<? extends Mob> type, double x, double y, double z) {
		return srv(() -> {
			Mob m = type.create(sl());
			m.moveTo(x, y, z, 180f, 0f);
			m.setNoAi(true);
			m.setPersistenceRequired();
			sl().addFreshEntity(m);
			return m.getId();
		});
	}
	private static void give(int slot, ItemStack st) {
		srvRun(() -> sp().getInventory().setItem(slot, st));
	}
	private static void select(int slot) {
		mc().player.getInventory().selected = slot;
		srvRun(() -> sp().getInventory().selected = slot);
	}
	private static void press(KeyMapping k) { KeyMapping.click(KeyBindingHelper.getBoundKeyOf(k)); }
	private static int stars() { return srv(() -> WantedSystem.stars(sp())); }
	private static Vec3 front(double dist, double dy) { return new Vec3(origin.getX() + 0.5, y0 + dy, origin.getZ() + 0.5 + dist); }

	/** Expected stars after a crime per the crimes sheet rule. */
	private static int expectStars(String crimeId, int before) {
		Sheets.Crime c = Sheets.CRIMES.stream().filter(x -> x.id().equals(crimeId)).findFirst().orElseThrow();
		return Math.min(WantedSystem.MAX_STARS, before > 0 ? Math.max(c.stars(), before + c.addStars()) : c.stars());
	}

	// ------------------------------------------------------------------ the scenario
	private static void buildScript() {
		// ---- A. first start: FirstRun, CacheGuard (blocking extraction + background retry of the failed game)
		add(t -> mc().screen != null && (mc().screen instanceof TitleScreen || mc().screen instanceof ExtractProgressScreen) || t > 3600);
		run(() -> {
			CraftTheft.LOG.info("[harness] GL vendor='{}' renderer='{}' version='{}'", GlUtil.getVendor(), GlUtil.getRenderer(), GlUtil.getOpenGLVersion());
			check("first start: FirstRun wrote options.txt before Minecraft read it", FirstRun.outcome.startsWith("wrote") && !mc().options.onboardAccessibility,
					"outcome='" + FirstRun.outcome + "' onboardAccessibility=" + mc().options.onboardAccessibility);
		});
		if (cache()) {
			until("CacheGuard showed the extraction progress screen (no manifest yet)", 400, () -> screens.contains("ExtractProgressScreen"));
			// wait for the loading overlay of the first resource load to go, then show the progress screen
			add(new StepFn() {
				int seen = -1;
				public boolean run(int t) {
					boolean visible = mc().getOverlay() == null && mc().screen instanceof ExtractProgressScreen && CacheGuard.readProgress() != null;
					if (visible && seen < 0) seen = t;
					if (visible && t >= seen + 6) { grab("00_extracting"); return true; }
					return !(mc().screen instanceof ExtractProgressScreen) || t > 1200;
				}
			});
			until("blocking extraction finished and the title screen came back", 2400, () -> CacheGuard.blockingFinished && mc().screen instanceof TitleScreen);
			run(() -> {
				CacheManifest m = CacheManifest.read(ModConfig.get().cacheDir);
				var sel = mc().getResourcePackRepository().getSelectedIds();
				check("manifest.json present after the run (status " + m.status + ")", m.usable(), "problem=" + m.problem + " games=" + m.games);
				check("halo and fortnite cache packs mounted as required top packs", sel.contains(CachePacks.PREFIX + "halo") && sel.contains(CachePacks.PREFIX + "fortnite"), "selected=" + sel);
				check("cache-ready reported pass", "pass".equals(MeltyChecks.results().get(MeltyChecks.CACHE_READY)), "results=" + MeltyChecks.results());
			});
			until("failed game (gta) re-extracted in the background (--only gta) and its pack mounted", 2400,
					() -> mc().getResourcePackRepository().getSelectedIds().contains(CachePacks.PREFIX + "gta") && !CacheGuard.running() && MeshCache.get("gta/infernus") != null && MeshCache.get("gta/infernus").fromCache());
			run(() -> {
				check("extractor runs: full then --only gta", CacheGuard.history().size() >= 2 && CacheGuard.history().get(1).contains("gta"), "history=" + CacheGuard.history());
				var sel = mc().getResourcePackRepository().getSelectedIds();
				var stack = mc().getResourceManager().getResourceStack(CraftTheft.id("sounds.json")).stream().map(r -> r.sourcePackId()).toList();
				check("cache packs sit above the mod's own resources", stack.indexOf(CachePacks.PREFIX + "halo") > stack.indexOf(CraftTheft.MODID)
						&& stack.indexOf(CachePacks.PREFIX + "gta") > stack.indexOf(CraftTheft.MODID) && stack.indexOf(CraftTheft.MODID) >= 0, "stack (bottom..top)=" + stack + " selected=" + sel);
				for (Sheets.Source s : Sheets.SOURCES) {
					if (!s.kind().equals("mesh")) continue;
					RuntimeMesh m = MeshCache.get(SourceRows.meshKey(s.id()));
					check("mesh " + s.id() + " from the cache pack", m != null && m.fromCache(), m == null ? "missing" : m.sourcePack + " " + m.triangleCount + " tris");
				}
				var rm = mc().getResourceManager();
				var star = rm.getResource(CraftTheft.id("textures/gui/gta/wanted_star.png"));
				check("cache texture overrides the jar placeholder (wanted star)", star.isPresent() && star.get().sourcePackId().startsWith(CachePacks.PREFIX), "winner=" + star.map(r -> r.sourcePackId()).orElse("none"));
				var sj = rm.getResourceStack(CraftTheft.id("sounds.json")).stream().map(r -> r.sourcePackId()).toList();
				check("sounds.json of the jar and of each cache pack are all read", sj.contains(CraftTheft.MODID) && sj.contains(CachePacks.PREFIX + "halo") && sj.contains(CachePacks.PREFIX + "gta") && sj.contains(CachePacks.PREFIX + "fortnite"), "stack=" + sj);
				check("mesh materials resolved to cache textures (no missing textures)", MeshCache.missingTextures == 0, "missing=" + MeshCache.missingTextures);
				WeaponStats ar = WeaponStats.get("assault_rifle"), sn = WeaponStats.get("sniper_rifle");
				check("weapon stats: cache value overrides the sheet (assault rifle damage 8.0)", ar.damage == 8.0f, "damage=" + ar.damage + " fromCache=" + ar.fromCache);
				check("weapon stats: out-of-range cache value rejected (sniper zoom stays at the sheet's 0.2)", Math.abs(sn.zoom - 0.2f) < 1e-6, "zoom=" + sn.zoom);
				check("shield timing from the cache (halo_shield.json)", ShieldTuning.get().rechargeDelayTicks == 90 && Math.abs(ShieldTuning.get().rechargePerTick - 1.25f) < 1e-6, ShieldTuning.get().source);
			});
		} else {
			waitFor(400, () -> mc().screen instanceof TitleScreen);
			run(() -> {
				var sel = mc().getResourcePackRepository().getSelectedIds();
				check("no cache: no cache pack mounted", sel.stream().noneMatch(s -> s.startsWith(CachePacks.PREFIX)), "selected=" + sel);
				check("no cache: every mesh is the jar placeholder", !MeshCache.all().isEmpty() && MeshCache.all().values().stream().noneMatch(RuntimeMesh::fromCache), "meshes=" + MeshCache.all().size());
				check("no cache: cache-ready reported fail", MeltyChecks.results().getOrDefault(MeltyChecks.CACHE_READY, "").startsWith("fail"), "results=" + MeltyChecks.results());
				check("no cache: weapon stats are the sheet defaults", WeaponStats.get("assault_rifle").damage == 7.5f, "damage=" + WeaponStats.get("assault_rifle").damage);
				check("no cache: no extractor run", CacheGuard.runs == 0, "runs=" + CacheGuard.runs + " msg=" + CacheGuard.lastMessage);
			});
		}
		run(() -> {
			check("no accessibility onboarding screen at first start", !screens.contains("AccessibilityOnboardingScreen"), "screens=" + screens);
			coverageChecks();
			Minecraft mc = mc();
			mc.options.pauseOnLostFocus = false;
			mc.options.renderDistance().set(6);
			mc.options.simulationDistance().set(6);
			mc.getToasts().clear();
			createWorld(mc);
		});

		// ---- B. first join: Battle Bus -> jump -> skydive -> glider -> landing
		until("first join boards the Battle Bus", 3000, () -> mc().player != null && mc().player.getVehicle() instanceof BattleBusEntity);
		run(() -> {
			v0 = mc().player.getVehicle().position();
			scratch.set(v0);
			RuntimeMesh bus = MeshCache.get(SourceRows.meshKey("fn_mesh_battle_bus"));
			check("bus drawn with the " + (cache() ? "cache" : "placeholder") + " mesh", bus != null && bus.fromCache() == cache(), bus == null ? "none" : bus.sourcePack);
			Vec3[] route = srv(() -> dev.crafttheft.bus.BattleBus.route(sl()));
			check("bus starts at the route start (altitude " + Sheets.BATTLE_BUS_ALTITUDE + ", half a route from spawn)",
					Math.abs(v0.y - Sheets.BATTLE_BUS_ALTITUDE) < 1.5 && v0.distanceTo(route[0]) < 40, "bus=" + v0 + " routeStart=" + route[0]);
		});
		// the bus (an entity) only moves once the server has generated the chunks under it
		until("the Battle Bus sets off once its chunks are loaded", 1200, () -> mc().player.getVehicle() != null && mc().player.getVehicle().position().distanceTo((Vec3) scratch.get()) > 1.0);
		// measured on the server, per server tick (the server may run below 20 TPS while it generates terrain)
		run(() -> {
			Object[] b = srv(() -> sp().getVehicle() instanceof BattleBusEntity bus ? new Object[]{bus.position(), bus.life()} : null);
			v0 = (Vec3) b[0];
			i0 = (Integer) b[1];
		});
		add(t -> t > 1200 || srv(() -> sp().getVehicle() instanceof BattleBusEntity bus ? bus.life() - i0 >= 40 : true));
		run(() -> {
			Object[] b = srv(() -> sp().getVehicle() instanceof BattleBusEntity bus ? new Object[]{bus.position(), bus.life()} : null);
			Vec3 now = (Vec3) b[0];
			int ticks = (Integer) b[1] - i0;
			double perTick = ticks <= 0 ? 0 : now.subtract(v0).horizontalDistance() / ticks;
			check("bus flies level along the route at systems.battle_bus.speed", Math.abs(now.y - v0.y) < 0.01 && Math.abs(perTick - Sheets.BATTLE_BUS_SPEED) < 0.02 && ticks > 10,
					String.format("%.3f blocks/tick over %d server ticks (sheet %.2f), dy %.2f", perTick, ticks, Sheets.BATTLE_BUS_SPEED, now.y - v0.y));
		});
		// the bus is drawn once the client has built the render section it is in (terrain streaming in)
		waitFor(600, () -> mc().player.getVehicle() != null && mc().levelRenderer.isSectionCompiled(mc().player.getVehicle().blockPosition()));
		waitTicks(4);
		shot("01_battle_bus");
		run(() -> f0 = srv(() -> sp().getHealth()));
		// hold jump like a player would until the bus lets go (the server may be busy generating terrain)
		add(t -> { mc().options.keyJump.setDown(true); return mc().player.getVehicle() == null || t > 200; });
		run(() -> mc().options.keyJump.setDown(false));
		until("jump dropped the player into a skydive", 600, () -> mc().player.getVehicle() == null && mc().player.getAttachedOrElse(ModAttachments.GLIDE, 0) == Skydive.SKYDIVE);
		run(() -> { minVy = 0; d0 = 0; mc().options.keyUp.setDown(true); });
		add(t -> {
			var p = mc().player;
			minVy = Math.min(minVy, p.getDeltaMovement().y);
			if (t > 25) d0 = Math.max(d0, p.getDeltaMovement().horizontalDistance());
			if (t == 30) grab("02_skydive");
			return p.getAttachedOrElse(ModAttachments.GLIDE, 0) == Skydive.GLIDE || t > 1200;
		});
		run(() -> {
			check("free fall reaches about systems.skydive.fallSpeed", minVy < -Sheets.SKYDIVE_FALL_SPEED * 0.7 && minVy > -Sheets.SKYDIVE_FALL_SPEED * 1.15, String.format("min vy %.3f (fallSpeed %.2f)", minVy, Sheets.SKYDIVE_FALL_SPEED));
			check("W steers the skydive at about systems.skydive.steerSpeed", d0 > Sheets.SKYDIVE_STEER_SPEED * 0.6 && d0 < Sheets.SKYDIVE_STEER_SPEED * 1.3, String.format("horizontal %.3f (steerSpeed %.2f)", d0, Sheets.SKYDIVE_STEER_SPEED));
			var p = mc().player;
			int ground = srv(() -> sl().getHeight(Heightmap.Types.MOTION_BLOCKING, p.getBlockX(), p.getBlockZ()));
			double agl = p.getY() - ground;
			check("glider opened by itself near systems.skydive.gliderAutoOpenAgl", agl < Sheets.SKYDIVE_GLIDER_AUTO_OPEN_AGL + 3 && agl > Sheets.SKYDIVE_GLIDER_AUTO_OPEN_AGL - 25, String.format("%.1f blocks above ground", agl));
		});
		run(() -> d1 = 0);
		add(t -> {
			// sample the glide speed while still in the air
			if (!mc().player.onGround() && t >= 8) d1 = Math.min(d1, mc().player.getDeltaMovement().y);
			if (t == 12) grab("03_glider");
			return t >= 20 || mc().player.onGround();
		});
		run(() -> {
			double vy = d1;
			check("glide descends at about systems.skydive.gliderSpeed", vy < -Sheets.SKYDIVE_GLIDER_SPEED * 0.6 && vy > -Sheets.SKYDIVE_GLIDER_SPEED * 1.3, String.format("vy %.3f (gliderSpeed %.2f)", vy, Sheets.SKYDIVE_GLIDER_SPEED));
			RuntimeMesh g = MeshCache.get(SourceRows.meshKey("fn_mesh_glider"));
			check("glider drawn with the " + (cache() ? "cache" : "placeholder") + " mesh", g != null && g.fromCache() == cache(), g == null ? "none" : g.sourcePack);
		});
		// let go of W as soon as the player is down (landing in water and swimming on would end in drowning)
		until("landed (skydive state cleared)", 3000, () -> {
			boolean landed = srv(() -> sp().getAttachedOrElse(ModAttachments.GLIDE, 0) == Skydive.NONE);
			if (landed || mc().player.onGround() || mc().player.isInWater()) mc().options.keyUp.setDown(false);
			return landed && mc().player.getAttachedOrElse(ModAttachments.GLIDE, 0) == Skydive.NONE;
		});
		run(() -> {
			mc().options.keyUp.setDown(false);
			float h = srv(() -> sp().getHealth()), a = srv(() -> sp().getAbsorptionAmount());
			check("no fall damage from the drop", h >= f0 - 0.01f, "health " + f0 + " -> " + h);
			check("shield starts at systems.shield.start on landing", Math.abs(a - ShieldTuning.toMc(ShieldTuning.get().start)) < 0.3f, "absorption " + a);
			check("the world remembers the drop (no second bus)", srv(() -> WorldState.get(sl()).hasDropped(sp().getUUID())), "");
		});

		// ---- C. arena at spawn
		run(() -> {
			BlockPos spawn = srv(() -> sl().getSharedSpawnPos());
			y0 = srv(() -> Math.max(sl().getHeight(Heightmap.Types.MOTION_BLOCKING_NO_LEAVES, spawn.getX(), spawn.getZ()), sl().getSeaLevel() + 1));
			origin = new BlockPos(spawn.getX(), y0, spawn.getZ());
			cmd(String.format("tp @p %d %d %d 0 10", origin.getX(), y0 + 2, origin.getZ()));
		});
		waitTicks(40);
		run(() -> {
			int x = origin.getX(), z = origin.getZ();
			cmd("time set 1000");
			cmd("gamerule doMobLoot false");
			cmd("gamerule announceAdvancements false");
			cmd("gamerule doDaylightCycle false");
			cmd("gamerule naturalRegeneration false");
			cmd("weather clear");
			for (int qx = -1; qx <= 0; qx++) for (int qz = -1; qz <= 0; qz++) {
				int ax = x + qx * 48, az = z + qz * 48;
				cmd(String.format("fill %d %d %d %d %d %d minecraft:smooth_stone", ax, y0 - 1, az, ax + 47, y0 - 1, az + 47));
				cmd(String.format("fill %d %d %d %d %d %d minecraft:air", ax, y0, az, ax + 47, y0 + 12, az + 47));
				cmd(String.format("fill %d %d %d %d %d %d minecraft:air", ax, y0 + 13, az, ax + 47, y0 + 25, az + 47));
			}
			cmd(String.format("tp @p %d %d %d 0 10", x, y0, z));
			PoliceDispatch.ENABLED = false;
		});
		waitTicks(20);
		buildWeaponScript();
		buildHealScript();
		buildCrimeScript();
		buildReviewScript();
		buildCarScript();
		buildPoliceScript();
		buildChestScript();
		run(() -> {
			Map<String, String> r = MeltyChecks.results();
			for (String name : MeltyChecks.ALL) {
				String v = r.get(name);
				boolean expectPass = cache() || !(name.equals(MeltyChecks.CACHE_READY) || name.endsWith("-content"));
				check("melty check " + name + " reported " + (expectPass ? "pass" : "fail (placeholders)"), v != null && v.startsWith("pass") == expectPass, String.valueOf(v));
			}
			meltyFile(r);
			for (String n : List.of("crafttheft:halo.assault_rifle_fire", "crafttheft:fortnite.chest_open", "crafttheft:gta.siren", "crafttheft:halo.shield_recharge", "crafttheft:fortnite.drink")) {
				String res = sounds.get(n);
				boolean ok = res != null && (cache() ? res.startsWith("crafttheft:") : res.startsWith("minecraft:"));
				check("sound " + n + " played from the " + (cache() ? "cache pack" : "placeholder vanilla sound"), ok, String.valueOf(res));
			}
			CraftTheft.LOG.info("[harness] sounds heard: {}", sounds);
			CraftTheft.LOG.info("[harness] RESULT pass={} fail={}", pass, fail);
			// quit standing on long-generated ground: once (1 of ~12 runs) the integrated server never finished
			// stopping when the game was quit while far-away chunks the last visits had asked for were still generating
			// (server thread spinning in ChunkMap.processUnloads, 9 ChunkGenerationTasks left)
			cmd(String.format("tp @p %d %d %d", origin.getX(), y0 + 1, origin.getZ()));
		});
		waitServerTicks(200);
		run(() -> { CraftTheft.LOG.info("[harness] stopping the game"); mc().stop(); });
	}

	private static void createWorld(Minecraft mc) {
		String name = "harness-" + System.currentTimeMillis();
		GameRules rules = new GameRules();
		rules.getRule(GameRules.RULE_DAYLIGHT).set(false, null);
		rules.getRule(GameRules.RULE_WEATHER_CYCLE).set(false, null);
		rules.getRule(GameRules.RULE_DOMOBSPAWNING).set(false, null);
		LevelSettings ls = new LevelSettings(name, GameType.SURVIVAL, false, Difficulty.NORMAL, true, rules, WorldDataConfiguration.DEFAULT);
		WorldOptions wo = new WorldOptions(20261012L, true, false);
		CraftTheft.LOG.info("[harness] creating world {} seed {}", name, wo.seed());
		mc.createWorldOpenFlows().createFreshLevel(name, ls, wo, WorldPresets::createNormalWorldDimensions, new TitleScreen());
	}

	// ------------------------------------------------------------------ weapons
	/** Holds a key every client tick until `done` (server-side condition) or the timeout, then releases it. */
	private static void holdUntil(Supplier<KeyMapping> key, int timeout, BooleanSupplier done) {
		add(t -> {
			if (done.getAsBoolean() || t >= timeout) { key.get().setDown(false); return true; }
			key.get().setDown(true);
			return false;
		});
	}

	/** Waits n server ticks (the integrated server may run slower than the client on a busy machine). */
	private static void waitServerTicks(int n) {
		long[] start = {-1};
		add(t -> {
			long now = srv(() -> sl().getGameTime());
			if (start[0] < 0) start[0] = now;
			return now - start[0] >= n || t > n * 20 + 600;
		});
	}

	private static List<Long> roundTimes() {
		synchronized (WeaponSystem.ROUND_TIMES) { return new ArrayList<>(WeaponSystem.ROUND_TIMES); }
	}

	private static void buildWeaponScript() {
		run(() -> {
			int s = 0;
			for (String id : List.of("assault_rifle", "battle_rifle", "shotgun", "needler", "plasma_rifle", "rocket_launcher", "frag_grenade", "sniper_rifle", "magnum")) {
				ItemStack st = ModItems.weapon(id).createLoaded();
				if (id.equals("frag_grenade")) st.setCount(3);
				give(s++, st);
			}
			select(0);
		});
		// a. assault rifle: automatic hitscan, rounds every fireIntervalTicks, ammo on the stack, synced, reload
		target(6.5, 0);
		run(() -> { f0 = hp(targetId); i0 = WeaponSystem.roundsFired; synchronized (WeaponSystem.ROUND_TIMES) { WeaponSystem.ROUND_TIMES.clear(); } });
		add(t -> { if (WeaponSystem.roundsFired - i0 >= 3 && scratch.get() != Boolean.TRUE) { grab("04_assault_rifle"); scratch.set(Boolean.TRUE); } mc().options.keyAttack.setDown(true); return WeaponSystem.roundsFired - i0 >= 6 || t > 600; });
		run(() -> { mc().options.keyAttack.setDown(false); scratch.set(null); });
		waitServerTicks(6);
		run(() -> {
			int shots = WeaponSystem.roundsFired - i0;
			WeaponStats s = WeaponStats.get("assault_rifle");
			List<Long> times = roundTimes();
			boolean regular = times.size() >= 2;
			for (int i = 1; i < times.size(); i++) if (times.get(i) - times.get(i - 1) != s.fireIntervalTicks) regular = false;
			check("assault rifle (auto) fires one round every fireIntervalTicks=" + s.fireIntervalTicks + " server ticks", regular && shots >= 6, "rounds " + shots + " at server ticks " + times);
			int sAmmo = srv(() -> HaloWeaponItem.ammo(sp().getMainHandItem()));
			check("ammo decremented on the server stack", sAmmo == s.clip - shots, "ammo " + sAmmo + " clip " + s.clip + " shots " + shots);
			check("weapon drawn with the " + (cache() ? "cache" : "placeholder") + " mesh", MeshCache.get("halo/assault_rifle") != null && MeshCache.get("halo/assault_rifle").fromCache() == cache(), String.valueOf(MeshCache.get("halo/assault_rifle") == null ? "none" : MeshCache.get("halo/assault_rifle").sourcePack));
		});
		until("assault rifle hitscan damaged the target", 400, () -> hp(targetId) < f0);
		until("ammo synced to the client stack", 400, () -> HaloWeaponItem.ammo(mc().player.getMainHandItem()) == srv(() -> HaloWeaponItem.ammo(sp().getMainHandItem())));
		run(() -> press(ClientInput.RELOAD));
		until("reload key refilled the clip from the reserve", 1200, () -> {
			ItemStack s = mc().player.getMainHandItem();
			WeaponStats ws = WeaponStats.get("assault_rifle");
			return HaloWeaponItem.ammo(s) == ws.clip && HaloWeaponItem.reserve(s) < ws.reserve;
		});
		run(() -> {
			cmd("kill @e[type=minecraft:husk]");
			cmd(String.format("summon armor_stand %d %d %d {ShowArms:1b,NoBasePlate:1b,Rotation:[-90f,0f],HandItems:[{id:\"crafttheft:assault_rifle\",count:1},{}]}", origin.getX(), y0, origin.getZ() + 3));
		});
		waitTicks(10);
		run(() -> look(front(3.0, 1.2)));
		waitTicks(6);
		shot("04b_thirdperson_gun_side");
		run(() -> { cmd("kill @e[type=minecraft:armor_stand]"); mc().options.setCameraType(CameraType.THIRD_PERSON_BACK); });
		waitTicks(6);
		shot("04d_thirdperson_back");
		run(() -> mc().options.setCameraType(CameraType.FIRST_PERSON));
		// b. battle rifle: one trigger pull = a burst of `burst` rounds
		run(() -> select(1));
		waitServerTicks(12);
		run(() -> i0 = WeaponSystem.roundsFired);
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired > i0);
		waitServerTicks(12);
		run(() -> check("battle rifle: one trigger pull fires a burst of " + WeaponStats.get("battle_rifle").burst, WeaponSystem.roundsFired - i0 == WeaponStats.get("battle_rifle").burst, "rounds " + (WeaponSystem.roundsFired - i0)));
		// c. shotgun: pellets
		run(() -> select(2));
		target(4.0, 0);
		run(() -> { i0 = WeaponSystem.roundsFired; i1 = WeaponSystem.pelletsHit; f0 = hp(targetId); });
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired > i0);
		waitServerTicks(3);
		run(() -> check("shotgun: several of its " + WeaponStats.get("shotgun").pellets + " pellets hit at 4 blocks", WeaponSystem.pelletsHit - i1 >= 4, "pellets hit " + (WeaponSystem.pelletsHit - i1) + ", health " + f0 + " -> " + hp(targetId)));
		// d. needler: homing projectiles curve onto a target off the aim line
		run(() -> select(3));
		target(10.0, 2.0);
		run(() -> {
			f0 = hp(targetId);
			look(front(10.0, 1.3).add(-0.5, 0, 0)); // aim about 14 degrees off the target
			i0 = WeaponSystem.roundsFired;
			scratch.set(null);
			scratch2 = null;
		});
		add(t -> {
			boolean homing = srv(() -> sl().getEntitiesOfClass(HaloProjectile.class, sp().getBoundingBox().inflate(30)).stream().anyMatch(p -> p.homingTarget() != null && p.homingTarget().getId() == targetId));
			if (homing) scratch.set(Boolean.TRUE);
			if (WeaponSystem.roundsFired - i0 >= 2 && scratch2 == null) { scratch2 = "shot"; grab("04c_needler"); }
			boolean done = WeaponSystem.roundsFired - i0 >= 5 || t > 600;
			mc().options.keyAttack.setDown(!done);
			return done;
		});
		add(t -> {
			boolean homing = srv(() -> sl().getEntitiesOfClass(HaloProjectile.class, sp().getBoundingBox().inflate(30)).stream().anyMatch(p -> p.homingTarget() != null && p.homingTarget().getId() == targetId));
			if (homing) scratch.set(Boolean.TRUE);
			return hp(targetId) < f0 || t > 600;
		});
		run(() -> {
			check("needler rounds home on the target", Boolean.TRUE.equals(scratch.get()), "");
			check("needler damaged a target it was not aimed at", hp(targetId) < f0, "health " + f0 + " -> " + hp(targetId));
		});
		// e. plasma rifle projectile
		run(() -> select(4));
		target(6.5, 0);
		run(() -> { f0 = hp(targetId); i0 = WeaponSystem.roundsFired; });
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired - i0 >= 3);
		until("plasma rifle bolts damaged the target", 600, () -> hp(targetId) < f0);
		// f. rocket launcher: blast
		run(() -> select(5));
		target(9.0, 0);
		run(() -> { f0 = hp(targetId); i0 = WeaponSystem.roundsFired; i1 = Explosions.blasts; });
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired > i0);
		until("rocket exploded", 600, () -> Explosions.blasts > i1);
		until("rocket blast damaged the target", 200, () -> hp(targetId) < f0);
		// g. frag grenade: thrown, fuse, blast; grenades are consumed from the stack
		run(() -> select(6));
		target(5.0, 0);
		run(() -> { f0 = hp(targetId); i0 = WeaponSystem.roundsFired; i1 = Explosions.blasts; look(front(5.0, 0.0)); });
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired > i0);
		until("frag grenade exploded after its fuse", 1200, () -> Explosions.blasts > i1);
		until("frag grenade damaged the target", 200, () -> hp(targetId) < f0);
		run(() -> check("a grenade was used from the stack", srv(() -> sp().getInventory().getItem(6).getCount()) == 2, "count " + srv(() -> sp().getInventory().getItem(6).getCount())));
		// h. sniper scope zoom (client side)
		run(() -> { select(7); cmd("kill @e[type=minecraft:husk]"); });
		waitTicks(5);
		run(() -> { f1 = mc().player.getFieldOfViewModifier(); mc().options.keyUse.setDown(true); });
		waitTicks(4);
		run(() -> {
			float fov = mc().player.getFieldOfViewModifier();
			check("sniper scope multiplies the FOV by its zoom " + WeaponStats.get("sniper_rifle").zoom, ClientInput.zoomed() && Math.abs(fov - f1 * WeaponStats.get("sniper_rifle").zoom) < 0.01f, "fov " + f1 + " -> " + fov);
		});
		shot("05_sniper_scope");
		run(() -> mc().options.keyUse.setDown(false));
		waitTicks(3);
		run(() -> check("scope off when use is released", !ClientInput.zoomed(), ""));
	}

	/** A fresh no-AI husk (does not burn in daylight) at `dist` in front of the arena origin, `side` blocks to the left; the player looks at it. */
	private static void target(double dist, double side) {
		run(() -> {
			cmd("kill @e[type=minecraft:husk]");
			cmd(String.format("tp @p %d %d %d 0 0", origin.getX(), y0, origin.getZ()));
		});
		waitTicks(3);
		run(() -> {
			Vec3 at = front(dist, 0).add(side, 0, 0);
			targetId = spawnMob(EntityType.HUSK, at.x, at.y, at.z);
		});
		waitTicks(4);
		run(() -> look(front(dist, 1.3).add(side, 0, 0)));
	}

	// ------------------------------------------------------------------ heals and the Halo shield
	private static void buildHealScript() {
		run(() -> {
			cmd("kill @e[type=minecraft:husk]");
			give(0, new ItemStack(ModItems.heal("small_shield_potion"), 6));
			give(1, new ItemStack(ModItems.heal("shield_potion"), 3));
			give(2, new ItemStack(ModItems.heal("bandages"), 5));
			give(3, new ItemStack(ModItems.heal("med_kit"), 1));
			give(4, new ItemStack(ModItems.heal("chug_jug"), 1));
			select(0);
			ShieldSystem.RECHARGE_ENABLED = false; // potion amounts are checked without natural recharge mixed in
			srvRun(() -> { sp().setAbsorptionAmount(0); sp().setHealth(20f); });
		});
		waitServerTicks(3);
		useHeal(0, "small_shield_potion");
		run(() -> {
			float a = srv(() -> sp().getAbsorptionAmount());
			check("small shield potion adds its shield points", Math.abs(a - ShieldTuning.toMc(25)) < 0.3f, "absorption " + a);
		});
		useHeal(0, "small_shield_potion");
		run(() -> {
			float a = srv(() -> sp().getAbsorptionAmount());
			i0 = srv(() -> sp().getInventory().getItem(0).getCount());
			check("small shield potions stop at their shieldCap", Math.abs(a - ShieldTuning.toMc(50)) < 0.3f, "absorption " + a);
		});
		holdUntil(() -> mc().options.keyUse, 10, () -> false);
		waitServerTicks(60);
		run(() -> {
			check("a third small potion at its cap is refused (not used up)", srv(() -> sp().getInventory().getItem(0).getCount()) == i0 && !srv(() -> sp().isUsingItem()), "count " + srv(() -> sp().getInventory().getItem(0).getCount()));
			select(1);
		});
		useHeal(1, "shield_potion");
		run(() -> {
			float a = srv(() -> sp().getAbsorptionAmount());
			check("shield potion fills to the full Halo shield", Math.abs(a - ShieldTuning.get().maxMc()) < 0.3f, "absorption " + a);
			f0 = srv(() -> sp().getHealth());
			i0 = ShieldSystem.depletedSounds; i1 = ShieldSystem.rechargeSounds;
			srvRun(() -> sp().hurt(sl().damageSources().generic(), 6f));
			float a2 = srv(() -> sp().getAbsorptionAmount()), h = srv(() -> sp().getHealth());
			check("shield absorbs damage before health", Math.abs(a2 - (ShieldTuning.get().maxMc() - 6f)) < 0.05f && Math.abs(h - f0) < 0.01f, "shield " + a2 + " health " + f0 + " -> " + h);
		});
		until("client sees the server's shield value", 400, () -> Math.abs(mc().player.getAbsorptionAmount() - srv(() -> sp().getAbsorptionAmount())) < 0.05f);
		run(() -> {
			srvRun(() -> sp().hurt(sl().damageSources().generic(), 25f));
			check("a big hit drains the shield, then health", srv(() -> sp().getAbsorptionAmount()) < 0.01f && srv(() -> sp().getHealth()) < f0, "shield " + srv(() -> sp().getAbsorptionAmount()) + " health " + srv(() -> sp().getHealth()));
			ShieldSystem.RECHARGE_ENABLED = true;
		});
		until("shield depleted sound played", 400, () -> ShieldSystem.depletedSounds > i0);
		add(new StepFn() {
			long start = -1;
			public boolean run(int t) {
				long now = srv(() -> sl().getGameTime());
				if (start < 0) start = now;
				return now - start >= ShieldTuning.get().rechargeDelayTicks - 20 || t > 4000;
			}
		});
		run(() -> check("no recharge before the delay", srv(() -> sp().getAbsorptionAmount()) < 0.01f, "shield " + srv(() -> sp().getAbsorptionAmount())));
		until("shield recharges after the delay up to systems.shield.rechargeCap", 2400, () -> srv(() -> sp().getAbsorptionAmount()) >= ShieldTuning.toMc(ShieldTuning.get().rechargeCap) - 0.01f);
		waitServerTicks(30);
		run(() -> {
			float a = srv(() -> sp().getAbsorptionAmount());
			check("natural recharge stops at rechargeCap (potions fill the rest)", Math.abs(a - ShieldTuning.toMc(ShieldTuning.get().rechargeCap)) < 0.05f, "shield " + a);
			check("shield recharge sound played", ShieldSystem.rechargeSounds > i1, "");
			select(2);
			srvRun(() -> sp().setHealth(8f)); // 40 Fortnite HP: well below the bandages' 75 cap
			f0 = 8f;
		});
		until("client sees the lowered health", 400, () -> Math.abs(mc().player.getHealth() - 8f) < 0.01f);
		shot("06_hud_shield");
		useHeal(2, "bandages");
		run(() -> {
			float h = srv(() -> sp().getHealth());
			float expect = Math.min(Math.max(f0, 75 * Sheets.COMBAT_FORTNITE_HP_TO_MC), f0 + 15 * Sheets.COMBAT_FORTNITE_HP_TO_MC);
			check("bandages heal 15 Fortnite HP (capped at 75)", Math.abs(h - expect) < 0.05f, "health " + f0 + " -> " + h + " expected " + expect);
			srvRun(() -> sp().setHealth(16f));
			i0 = srv(() -> sp().getInventory().getItem(2).getCount());
		});
		holdUntil(() -> mc().options.keyUse, 10, () -> false);
		waitServerTicks(20);
		run(() -> { check("bandages refused above their 75 HP cap", srv(() -> sp().getInventory().getItem(2).getCount()) == i0, "health 16 (=80 FN HP)"); select(3); f0 = srv(() -> sp().getHealth()); });
		useHeal(3, "med_kit");
		run(() -> {
			check("med kit heals to full", srv(() -> sp().getHealth()) >= 19.99f, "health " + f0 + " -> " + srv(() -> sp().getHealth()));
			for (Sheets.Consumable c : Sheets.CONSUMABLES) {
				FortniteHealItem it = ModItems.heal(c.id());
				check("consumables row " + c.id() + " -> item (use " + c.useTicks() + " ticks, stack " + c.maxStack() + ")",
						it.getUseDuration(new ItemStack(it), mc().player) == c.useTicks() && new ItemStack(it).getMaxStackSize() == c.maxStack(), "");
			}
		});
	}

	/**
	 * Uses the selected heal through the real input path: hold use until the client has finished using it (then let go
	 * at once so it is not used again), then wait until the server has finished too.
	 */
	private static void useHeal(int slot, String id) {
		run(() -> { select(slot); i1 = srv(() -> sp().getInventory().getItem(slot).getCount()); });
		add(new StepFn() {
			volatile boolean started;
			public boolean run(int t) {
				if (t == 0) {
					mc().options.keyUse.setDown(true);
					mc().gameMode.useItem(mc().player, InteractionHand.MAIN_HAND);
					releaseUseBeforeKeybinds = () -> started && !mc().player.isUsingItem();
					return false;
				}
				if (mc().player.isUsingItem()) started = true;
				if (t == 20 && id.equals("shield_potion")) grab("06b_drinking");
				if ((started && !mc().player.isUsingItem()) || t > ModItems.heal(id).row.useTicks() * 6 + 400) {
					mc().options.keyUse.setDown(false);
					releaseUseBeforeKeybinds = null;
					return true;
				}
				if (releaseUseBeforeKeybinds != null) mc().options.keyUse.setDown(true);
				return false;
			}
		});
		// the server counts its own use ticks; wait until it has consumed the item
		add(t -> srv(() -> !sp().isUsingItem() && sp().getInventory().getItem(slot).getCount() < i1) || t > ModItems.heal(id).row.useTicks() * 6 + 600);
		run(() -> {
			int used = i1 - srv(() -> sp().getInventory().getItem(slot).getCount());
			if (used != 1) CraftTheft.LOG.warn("[harness] {} use consumed {} items (expected 1)", id, used);
		});
		waitServerTicks(2);
	}

	// ------------------------------------------------------------------ crimes and the wanted level
	private static void buildCrimeScript() {
		run(() -> {
			give(8, ItemStack.EMPTY);
			select(8);
			srvRun(() -> WantedSystem.set(sp(), 0));
			cmd(String.format("tp @p %d %d %d 0 0", origin.getX(), y0, origin.getZ()));
			targetId = spawnMob(EntityType.VILLAGER, origin.getX() + 2.5, y0, origin.getZ() + 1.5);
		});
		waitFor(400, () -> mc().level.getEntity(targetId) != null);
		run(() -> { Entity v = mc().level.getEntity(targetId); look(v.position().add(0, 1.2, 0)); mc().gameMode.attack(mc().player, v); });
		until("crime assault_villager (punch, attack_entity) -> stars per the crimes sheet", 400, () -> stars() == expectStars("assault_villager", 0));
		run(() -> {
			srvRun(() -> WantedSystem.set(sp(), 0));
			cmd("kill @e[type=minecraft:villager]");
			targetId = spawnMob(EntityType.IRON_GOLEM, origin.getX() + 2.5, y0, origin.getZ() + 2.5);
		});
		waitFor(400, () -> mc().level.getEntity(targetId) != null);
		run(() -> { Entity g = mc().level.getEntity(targetId); look(g.position().add(0, 1.5, 0)); mc().gameMode.attack(mc().player, g); });
		until("crime assault_golem (attack_entity) -> stars per the crimes sheet", 400, () -> stars() == expectStars("assault_golem", 0));
		run(() -> {
			cmd("kill @e[type=minecraft:iron_golem]");
			srvRun(() -> WantedSystem.set(sp(), 1));
			targetId = spawnMob(EntityType.VILLAGER, origin.getX() + 2.5, y0, origin.getZ() + 1.5);
			srvRun(() -> sl().getEntity(targetId).hurt(sp().damageSources().playerAttack(sp()), 100f));
			check("crime kill_villager (entity_death) adds stars when already wanted", stars() == expectStars("kill_villager", 1), "stars " + stars());
			srvRun(() -> WantedSystem.set(sp(), 0));
			targetId = spawnMob(EntityType.VILLAGER, origin.getX() - 6.5, y0, origin.getZ() - 4.5);
			give(0, ModItems.weapon("magnum").createLoaded());
			select(0);
			mc().player.setXRot(-60); // shoot at the sky
			srvRun(() -> sp().setXRot(-60));
		});
		waitServerTicks(6);
		run(() -> i0 = WeaponSystem.roundsFired);
		holdUntil(() -> mc().options.keyAttack, 600, () -> WeaponSystem.roundsFired > i0);
		until("crime gunfire (weapon_fire near a villager) -> stars per the crimes sheet", 400, () -> stars() == expectStars("gunfire", 0));
		run(() -> {
			cmd("kill @e[type=minecraft:villager]");
			srvRun(() -> WantedSystem.set(sp(), 1));
			select(8);
			cmd(String.format("tp @p %d %d %d 0 0", origin.getX(), y0, origin.getZ()));
			carId = srv(() -> {
				CarEntity c = ModEntities.car("police").create(sl());
				c.moveTo(origin.getX() + 0.5, y0, origin.getZ() + 3.5, 90f, 0f);
				sl().addFreshEntity(c);
				return c.getId();
			});
		});
		waitFor(400, () -> mc().level.getEntity(carId) != null && mc().player.position().distanceTo(new Vec3(origin.getX() + 0.5, y0, origin.getZ() + 0.5)) < 1);
		run(() -> {
			Entity c = mc().level.getEntity(carId);
			look(c.position().add(0, 0.8, 0));
			mc().gameMode.attack(mc().player, c);
		});
		until("crime assault_police (car_damaged) -> stars per the crimes sheet", 400, () -> stars() == expectStars("assault_police", 1));
		shot("07_wanted_stars");
		run(() -> {
			srvRun(() -> WantedSystem.set(sp(), 0));
			cmd("kill @e[type=crafttheft:police]");
		});
	}

	// ------------------------------------------------------------------ correctness review (docs/requests/correctness-review.md)
	private static void buildReviewScript() {
		// a sniper hit on a villager 30 blocks away (beyond the 16-block gunfire radius) is assault_villager (attack_entity)
		run(() -> {
			scratch.set(PoliceDispatch.ENABLED); // the scenario decides when the police may come; restored below
			PoliceDispatch.ENABLED = false;
			srvRun(() -> WantedSystem.set(sp(), 0));
			int x = origin.getX(), z = origin.getZ();
			cmd(String.format("fill %d %d %d %d %d %d minecraft:stone", x - 1, y0 - 1, z - 34, x + 1, y0 - 1, z - 1));
			cmd(String.format("fill %d %d %d %d %d %d minecraft:air", x - 1, y0, z - 34, x + 1, y0 + 3, z - 1));
			cmd(String.format("tp @p %d %d %d 180 0", x, y0, z));
			targetId = spawnMob(EntityType.VILLAGER, x + 0.5, y0, z - 30 + 0.5);
			give(0, ModItems.weapon("sniper_rifle").createLoaded());
			select(0);
		});
		waitFor(400, () -> mc().level.getEntity(targetId) != null);
		run(() -> { look(mc().level.getEntity(targetId).position().add(0, 1.0, 0)); i0 = WeaponSystem.pelletsHit; });
		waitServerTicks(4);
		holdUntil(() -> mc().options.keyAttack, 200, () -> WeaponSystem.pelletsHit > i0);
		until("a sniper hit on a villager 30 blocks away is assault_villager (ranged attack_entity)", 200, () -> stars() == expectStars("assault_villager", 0));
		run(() -> {
			cmd("kill @e[type=minecraft:villager]");
			srvRun(() -> WantedSystem.set(sp(), 0));
			// a Halo blast damages a GTA car; on a police car that is assault_police
			int[] r = srv(() -> {
				CarEntity c = ModEntities.car("police").create(sl());
				c.moveTo(origin.getX() + 0.5, y0, origin.getZ() - 8.5, 0f, 0f);
				sl().addFreshEntity(c);
				float h0 = c.health();
				dev.crafttheft.weapon.Explosions.blast(sl(), c.position().add(0, 0.6, 1.4), null, sp(), 20f, 4f);
				return new int[]{c.getId(), Math.round(h0 * 100), Math.round(c.health() * 100)};
			});
			check("a Halo blast damages a GTA car (vehicles.health)", r[2] < r[1], "health " + r[1] / 100f + " -> " + r[2] / 100f);
			check("blasting a police car is assault_police", stars() == expectStars("assault_police", 0), "stars " + stars());
			srvRun(() -> WantedSystem.set(sp(), 0));
			// a dispatched police car keeps its target through save/load; only dispatched police cars are "police sight"
			boolean[] b = srv(() -> {
				CarEntity c = (CarEntity) sl().getEntity(r[0]);
				boolean parkedSees = WantedSystem.seenByPolice(sp());
				c.setDispatched(sp().getUUID());
				boolean dispatchedSees = WantedSystem.seenByPolice(sp());
				net.minecraft.nbt.CompoundTag t = new net.minecraft.nbt.CompoundTag();
				c.saveWithoutId(t);
				CarEntity c2 = ModEntities.car("police").create(sl());
				c2.load(t);
				c.discard();
				return new boolean[]{c2.isDispatched(), parkedSees, dispatchedSees};
			});
			check("a dispatched police car keeps its chase target through save/load", b[0], "");
			check("only dispatched police cars count as police sight (wanted.loseAfterTicks)", !b[1] && b[2], "parked sees " + b[1] + ", dispatched sees " + b[2]);
			// a hitscan ray never reaches into unloaded chunks (no synchronous chunk loads on the server thread)
			Vec3 eye = srv(() -> sp().getEyePosition());
			Vec3 end = srv(() -> WeaponSystem.loadedPart(sl(), eye, eye.add(5000, 0, 0)));
			boolean loaded = srv(() -> sl().hasChunkAt(BlockPos.containing(end)));
			check("a hitscan ray is cut where the loaded chunks end", end.distanceTo(eye) < 5000 && end.distanceTo(eye) > 16 && loaded,
					String.format("cut at %.0f blocks", end.distanceTo(eye)));
			// a rocket keeps the weapons sheet's projectileSpeed (no vanilla 0.99 drag)
			carId = srv(() -> {
				var rk = new dev.crafttheft.weapon.HaloRocket(ModEntities.ROCKET, sp(), sl());
				rk.setup("rocket_launcher", 1f, 1f, 400);
				rk.setPos(origin.getX() + 0.5, 300, origin.getZ() + 0.5);
				rk.shoot(1, 0, 0, Sheets.WEAPONS.stream().filter(w -> w.id().equals("rocket_launcher")).findFirst().orElseThrow().projectileSpeed(), 0f);
				sl().addFreshEntity(rk);
				return rk.getId();
			});
		});
		waitServerTicks(10);
		run(() -> {
			float want = Sheets.WEAPONS.stream().filter(w -> w.id().equals("rocket_launcher")).findFirst().orElseThrow().projectileSpeed();
			double v = srv(() -> { Entity e = sl().getEntity(carId); double sp = e == null ? -1 : e.getDeltaMovement().length(); if (e != null) e.discard(); return sp; });
			check("a rocket keeps the weapons sheet's projectileSpeed in flight", Math.abs(v - want) < 0.02, String.format("speed %.3f after 10 ticks (sheet %.2f)", v, want));
			PoliceDispatch.ENABLED = Boolean.TRUE.equals(scratch.get());
			scratch.set(null);
			srvRun(() -> WantedSystem.set(sp(), 0));
		});
	}

	// ------------------------------------------------------------------ driving
	private static void buildCarScript() {
		run(() -> {
			int x = origin.getX(), z = origin.getZ();
			cmd(String.format("fill %d %d %d %d %d %d minecraft:stone", x - 4, y0, z + 26, x + 4, y0, z + 46));
			int[] ids = srv(() -> {
				CarEntity car = ModEntities.car("sultan").create(sl());
				car.moveTo(x + 0.5, y0, z + 2.5, 0f, 0f);
				sl().addFreshEntity(car);
				Mob v = EntityType.VILLAGER.create(sl());
				v.moveTo(x + 0.5, y0, z + 16.5, 0f, 0f);
				v.setNoAi(true);
				v.setPersistenceRequired();
				sl().addFreshEntity(v);
				return new int[]{car.getId(), v.getId()};
			});
			carId = ids[0]; targetId = ids[1];
			select(8);
			cmd(String.format("tp @p %d %d %d 0 10", x, y0, z));
		});
		waitFor(400, () -> mc().level.getEntity(carId) != null && mc().level.getEntity(targetId) != null && mc().player.position().distanceTo(new Vec3(origin.getX() + 0.5, y0, origin.getZ() + 0.5)) < 1);
		run(() -> {
			Entity car = mc().level.getEntity(carId);
			look(car.position().add(0, 0.7, 0));
			mc().gameMode.interact(mc().player, car, InteractionHand.MAIN_HAND);
		});
		until("entered the car with a real right click", 400, () -> mc().player.getVehicle() instanceof CarEntity);
		until("crime steal_car (car_enter) -> stars per the crimes sheet", 400, () -> stars() == expectStars("steal_car", 0));
		run(() -> {
			v0 = mc().player.getVehicle().position();
			f0 = hp(targetId);
			mc().player.setYRot(0); mc().player.setXRot(10);
			mc().options.keyUp.setDown(true);
			d0 = 0;
			d1 = -1e9;
			i0 = SirenSound.started;
			scratch.set(sounds.get("crafttheft:gta.horn"));
			press(ClientInput.HORN);
		});
		add(t -> {
			if (mc().player.getVehicle() instanceof CarEntity c) { d0 = Math.max(d0, c.speed); d1 = Math.max(d1, c.getY()); }
			if (t == 18) mc().getToasts().clear();
			if (t == 26) grab("08_driving");
			return t >= 44;
		});
		run(() -> { mc().options.keyUp.setDown(false); mc().options.keyDown.setDown(true); });
		waitTicks(16);
		run(() -> mc().options.keyDown.setDown(false));
		waitTicks(10);
		run(() -> {
			Entity car = mc().player.getVehicle();
			Vec3 p = car.position();
			Sheets.Vehicle spec = ((CarEntity) car).spec;
			check("W drives the car forward", p.z - v0.z > 18, String.format("dz %.1f, max speed %.2f b/t (sheet %.2f)", p.z - v0.z, d0, spec.maxSpeed()));
			check("car speed stays within the sheet's maxSpeed", d0 <= spec.maxSpeed() + 1e-3, String.format("%.3f", d0));
			check("car steps up a 1-block ledge", d1 >= y0 + 0.99, String.format("max y %.2f base %d", d1, y0));
			check("car drawn with the " + (cache() ? "cache" : "placeholder") + " mesh", MeshCache.get("gta/sultan") != null && MeshCache.get("gta/sultan").fromCache() == cache(), "");
		});
		until("server agrees with the client's car position", 600, () -> mc().player.getVehicle() != null && srv(() -> sl().getEntity(carId).position()).distanceTo(mc().player.getVehicle().position()) < 1.5);
		until("ramming damaged a villager", 600, () -> hp(targetId) < f0);
		until("horn key honks (gta.horn)", 600, () -> sounds.containsKey("crafttheft:gta.horn"));
		holdUntil(() -> mc().options.keyShift, 400, () -> mc().player.getVehicle() == null);
		until("left the car", 40, () -> mc().player.getVehicle() == null);
		run(() -> {
			for (Sheets.Vehicle v : Sheets.VEHICLES) {
				var type = ModEntities.car(v.id());
				check("vehicles row " + v.id() + " -> entity type (" + v.width() + "x" + v.height() + ", " + v.role() + ")",
						Math.abs(type.getWidth() - v.width()) < 1e-4 && Math.abs(type.getHeight() - v.height()) < 1e-4, BuiltInRegistries.ENTITY_TYPE.getKey(type).toString());
			}
			srvRun(() -> WantedSystem.set(sp(), 0));
			cmd("kill @e[type=minecraft:villager]");
			cmd("kill @e[type=crafttheft:sultan]");
		});
	}

	// ------------------------------------------------------------------ police
	private static void buildPoliceScript() {
		run(() -> {
			cmd(String.format("tp @p %d %d %d 0 0", origin.getX(), y0, origin.getZ()));
			srvRun(() -> { sp().setHealth(20f); sp().setAbsorptionAmount(ShieldTuning.get().maxMc()); });
			PoliceDispatch.ENABLED = true;
			srvRun(() -> WantedSystem.set(sp(), 2));
			i0 = PoliceDispatch.dispatched;
			i1 = SirenSound.started;
			f0 = srv(() -> sp().getHealth() + sp().getAbsorptionAmount());
			d1 = Double.MAX_VALUE;
		});
		until("police dispatched for 2 stars", 1600, () -> PoliceDispatch.dispatched > i0);
		add(t -> {
			double d = srv(() -> PoliceDispatch.policeNear(sp(), 100).stream().mapToDouble(c -> c.distanceTo(sp())).min().orElse(999));
			d1 = Math.min(d1, d);
			if (d < 10 && scratch.get() != Boolean.FALSE) {
				var car = srv(() -> PoliceDispatch.policeNear(sp(), 100).get(0).position());
				look(car.add(0, 0.8, 0));
				scratch.set(Boolean.FALSE);
				return false;
			}
			if (scratch.get() == Boolean.FALSE) { grab("09_police"); scratch.set(null); }
			float hp = srv(() -> sp().getHealth() + sp().getAbsorptionAmount());
			return (hp < f0 - 0.5f && d1 < 12) || t >= 2400;
		});
		run(() -> {
			int cars = srv(() -> PoliceDispatch.policeNear(sp(), 160).size());
			Sheets.Wanted row = WantedSystem.row(2);
			check("police cars stay within the wanted row's maxPoliceCars", cars <= row.maxPoliceCars(), "cars " + cars + " max " + row.maxPoliceCars());
			check("police chased the player down", d1 < 12, String.format("closest %.1f", d1));
			check("police rammed the player", srv(() -> sp().getHealth() + sp().getAbsorptionAmount()) < f0, "hp+shield " + f0 + " -> " + srv(() -> sp().getHealth() + sp().getAbsorptionAmount()));
			check("siren loop started for the chasing police car", SirenSound.started > i1, "sirens " + (SirenSound.started - i1) + ", sound " + sounds.get("crafttheft:gta.siren"));
			check("police car drawn with the " + (cache() ? "cache" : "placeholder") + " mesh", MeshCache.get("gta/police") != null && MeshCache.get("gta/police").fromCache() == cache(), "");
			cmd("effect give @p minecraft:resistance 120 3");
			srvRun(() -> { WantedSystem.set(sp(), 3); sp().setHealth(20f); });
			i0 = PoliceDispatch.shots;
		});
		until("officers shoot at 3 stars (wanted row officersShoot)", 2400, () -> PoliceDispatch.shots > i0);
		run(() -> {
			PoliceDispatch.ENABLED = false;
			cmd("kill @e[type=crafttheft:police]");
			// the clock starts on the server tick that sets the star (WantedSystem.set also resets "last seen"); reading
			// the game time in a later client step made the measured time depend on client/server step timing
			d0 = srv(() -> { WantedSystem.set(sp(), 1); sp().setHealth(20f); return (double) sl().getGameTime(); });
		});
		until("out of police sight for loseAfterTicks drops a star", WantedSystem.row(1).loseAfterTicks() * 6 + 600, () -> stars() == 0);
		run(() -> {
			long took = srv(() -> sl().getGameTime()) - (long) d0;
			check("the star dropped after about the wanted row's loseAfterTicks (" + WantedSystem.row(1).loseAfterTicks() + " server ticks)",
					took >= WantedSystem.row(1).loseAfterTicks() - 5 && took <= WantedSystem.row(1).loseAfterTicks() + 80, "took " + took + " server ticks");
		});
	}

	// ------------------------------------------------------------------ loot chests and world setup
	private static void buildChestScript() {
		run(() -> {
			cmd("effect clear @p"); // effect particles (resistance is purple) would sit in front of the camera in screenshots
			WorldState st = srv(() -> WorldState.get(sl()));
			check("supply chests planned per systems.chests.count", st.chests.size() == Sheets.CHESTS_COUNT, "planned " + st.chests.size());
			check("parked cars planned per systems.cars.parkedCount", st.cars.size() == Sheets.CARS_PARKED_COUNT, "planned " + st.cars.size());
			int maxR = st.chests.stream().mapToInt(p -> (int) Math.hypot(p.x - origin.getX(), p.z - origin.getZ())).max().orElse(0);
			check("chests planned within systems.chests.radius", maxR <= Sheets.CHESTS_RADIUS + 1, "max radius " + maxR);
			scratch.set(null);
		});
		// a planned parked car materialises when its chunk loads (visit the nearest unplaced one)
		add(new StepFn() {
			int visits;
			public boolean run(int t) {
				if (WorldSetup.carsPlaced > 0) {
					check("a planned parked car was placed when its chunk loaded", true, WorldSetup.carsPlaced + " placed after " + visits + " visits");
					return true;
				}
				if (t % 160 == 0) {
					WorldState.Plan next = srv(() -> WorldState.get(sl()).cars.stream().filter(p -> !p.done && outsideArena(p))
							.min(java.util.Comparator.comparingDouble(p -> Math.hypot(p.x - origin.getX(), p.z - origin.getZ()))).orElse(null));
					if (next != null) {
						visits++;
						cmd(String.format("tp @p %d %d %d", next.x, 200, next.z));
						cmd("effect give @p minecraft:slow_falling 30 1");
					}
				}
				if (t > 2400) { check("a planned parked car was placed when its chunk loaded", false, "after " + visits + " visits"); return true; }
				return false;
			}
		});
		// a placed chest outside the cleared arena; else visit the nearest unplaced plan so its chunk loads
		add(new StepFn() {
			int visits;
			public boolean run(int t) {
				BlockPos found = srv(() -> {
					for (WorldState.Plan p : WorldState.get(sl()).chests)
						if (p.done && p.y != Integer.MIN_VALUE && outsideArena(p)) { BlockPos b = findChest(p); if (b != null) return b; }
					return null;
				});
				if (found != null) {
					scratch.set(found);
					check("a planned supply chest was placed when its chunk loaded", true, "at " + found.toShortString() + " after " + visits + " visits");
					cmd(String.format("tp @p %d %d %d", found.getX(), found.getY() + 1, found.getZ() + 2));
					return true;
				}
				if (t % 160 == 0) {
					WorldState.Plan next = srv(() -> WorldState.get(sl()).chests.stream().filter(p -> !p.done && outsideArena(p))
							.min(java.util.Comparator.comparingDouble(p -> Math.hypot(p.x - origin.getX(), p.z - origin.getZ()))).orElse(null));
					if (next != null) {
						visits++;
						cmd(String.format("tp @p %d %d %d", next.x, 200, next.z));
						cmd("effect give @p minecraft:slow_falling 30 1");
					}
				}
				if (t > 2400) { check("a planned supply chest was placed when its chunk loaded", false, "after " + visits + " visits"); return true; }
				return false;
			}
		});
		waitTicks(40);
		run(() -> {
			BlockPos pos = (BlockPos) scratch.get();
			if (pos == null) { check("found a placed chest", false, ""); return; }
			look(Vec3.atCenterOf(pos));
			mc().getToasts().clear();
		});
		waitTicks(5);
		run(() -> cmd("effect clear @p"));
		waitTicks(20);
		run(() -> CraftTheft.LOG.info("[harness] entities near the chest view: {}", srv(() -> sl().getEntities(sp(), sp().getBoundingBox().inflate(6)).stream()
				.map(e -> e.getType().toShortString() + (e instanceof net.minecraft.world.entity.item.ItemEntity ie ? "(" + ie.getItem() + ")" : "") + "@" + e.blockPosition().toShortString()).toList())));
		shot("10_supply_chest");
		run(() -> {
			BlockPos pos = (BlockPos) scratch.get();
			if (pos == null) return;
			i0 = LootChestBlockEntity.opened;
			mc().gameMode.useItemOn(mc().player, InteractionHand.MAIN_HAND, new BlockHitResult(Vec3.atCenterOf(pos).add(0, 0.4, 0), Direction.UP, pos, false));
		});
		until("supply chest opened with a real right click", 400, () -> mc().screen instanceof ContainerScreen);
		waitTicks(6);
		run(() -> {
			if (mc().screen instanceof ContainerScreen cs) {
				int n = 0;
				boolean allFromSheet = true, loaded = true;
				StringBuilder sb = new StringBuilder();
				for (int i = 0; i < 27; i++) {
					ItemStack s = cs.getMenu().getContainer().getItem(i);
					if (s.isEmpty()) continue;
					n++;
					String id = BuiltInRegistries.ITEM.getKey(s.getItem()).getPath();
					sb.append(id).append('x').append(s.getCount()).append(' ');
					if (Sheets.LOOT.stream().noneMatch(l -> l.item().equals(id) && s.getCount() >= l.countMin() && s.getCount() <= l.countMax())) allFromSheet = false;
					if (s.getItem() instanceof HaloWeaponItem w && !w.isGrenade() && HaloWeaponItem.ammo(s) != w.stats().clip) loaded = false;
				}
				check("chest rolled systems.chests.rolls entries", n == Sheets.CHESTS_ROLLS, sb.toString());
				check("every chest item and count comes from the loot sheet", allFromSheet, sb.toString());
				check("chest weapons come loaded", loaded, sb.toString());
				check("chest open sound (fortnite.chest_open)", sounds.containsKey("crafttheft:fortnite.chest_open"), String.valueOf(sounds.get("crafttheft:fortnite.chest_open")));
			}
		});
		shot("11_chest_loot");
		run(() -> {
			mc().setScreen(null);
			RuntimeMesh chest = MeshCache.get(SourceRows.meshKey("fn_mesh_chest"));
			check("chest drawn with the " + (cache() ? "Fortnite cache mesh" : "vanilla chest model placeholder"), cache() ? chest != null && chest.fromCache() : chest == null, chest == null ? "vanilla model" : chest.sourcePack);
			for (Sheets.Loot l : Sheets.LOOT) check("loot row " + l.pool() + "/" + l.item() + " -> registered item", ModItems.byRowId(l.item()) != null, "");
		});
		waitTicks(5);
	}

	private static boolean outsideArena(WorldState.Plan p) {
		return Math.abs(p.x - origin.getX()) >= 50 || Math.abs(p.z - origin.getZ()) >= 50;
	}

	private static BlockPos findChest(WorldState.Plan p) {
		for (int dx = -5; dx <= 5; dx++) for (int dz = -5; dz <= 5; dz++) for (int dy = -3; dy <= 3; dy++) {
			BlockPos b = new BlockPos(p.x + dx, (p.y == Integer.MIN_VALUE ? 64 : p.y) + dy, p.z + dz);
			if (sl().getBlockState(b).is(ModBlocks.LOOT_CHEST)) return b;
		}
		return null;
	}

	// ------------------------------------------------------------------ static coverage of the sheets and hooks
	private static void coverageChecks() {
		Set<String> hookIds = new LinkedHashSet<>();
		try {
			Path f = Paths.get(System.getProperty("crafttheft.sheetsDir", "../sheets")).resolve("hooks.json");
			JsonObject root = JsonParser.parseString(Files.readString(f, StandardCharsets.UTF_8)).getAsJsonObject();
			for (var r : root.getAsJsonArray("rows")) hookIds.add(r.getAsJsonObject().get("id").getAsString());
		} catch (Exception e) {
			check("hooks sheet readable", false, e.toString());
		}
		Map<String, String> reg = Hooks.registered();
		for (String id : hookIds) check("hooks row " + id + " registered", reg.containsKey(id), String.valueOf(reg.get(id)));
		for (String id : reg.keySet()) if (!hookIds.contains(id)) CraftTheft.LOG.info("[harness] INFO extra hook not in the sheet (requested in docs/requests/mod.md): {} = {}", id, reg.get(id));
		for (Sheets.Weapon w : Sheets.WEAPONS) {
			HaloWeaponItem it = ModItems.weapon(w.id());
			check("weapons row " + w.id() + " -> HaloWeaponItem", it != null && it.row == w && BuiltInRegistries.ITEM.getKey(it).getPath().equals(w.id()), "");
		}
		for (Sheets.Crime c : Sheets.CRIMES) check("crimes row " + c.id() + " has a rule for hook " + c.hook(), CrimeSystem.RULES.containsKey(c.id()) && hookIds.contains(c.hook()), "");
		for (Sheets.Wanted w : Sheets.WANTED) check("wanted row " + w.stars() + " police vehicle " + w.policeVehicle() + " exists", ModEntities.cars().containsKey(w.policeVehicle()), "");
		for (Sheets.Source s : Sheets.SOURCES) {
			if (s.kind().equals("sound")) check("sound row " + s.id() + " -> sound event", ModSounds.all().containsKey(s.id()), "");
			if (s.kind().equals("mesh")) {
				boolean jar = mc().getResourceManager().getResourceStack(CraftTheft.id(s.out())).stream().anyMatch(r -> r.sourcePackId().equals(CraftTheft.MODID));
				check("mesh row " + s.id() + " has its placeholder (" + s.placeholder() + ")", jar || s.placeholder().startsWith("vanilla"), jar ? "jar mesh" : "vanilla model");
			}
			if (s.kind().equals("icon")) {
				boolean jar = mc().getResourceManager().getResourceStack(CraftTheft.id(s.out())).stream().anyMatch(r -> r.sourcePackId().equals(CraftTheft.MODID));
				check("icon row " + s.id() + " has its jar placeholder", jar, s.out());
			}
		}
	}

	private static void meltyFile(Map<String, String> results) {
		String local = System.getenv("LOCALAPPDATA");
		if (local == null) { check("Melty test file present (LOCALAPPDATA set)", false, "no LOCALAPPDATA"); return; }
		try {
			Path test = Paths.get(local, "Melty", "test", "craft-theft-halo.json");
			String json = Files.readString(test, StandardCharsets.UTF_8);
			Path checks = Paths.get(JsonParser.parseString(json).getAsJsonObject().get("checks").getAsString());
			List<String> lines = Files.readAllLines(checks, StandardCharsets.UTF_8);
			for (String name : MeltyChecks.ALL) {
				long n = lines.stream().filter(l -> l.startsWith("pass " + name) && l.length() == ("pass " + name).length() || l.startsWith("fail " + name + ":")).count();
				check("Melty checks file has exactly one line for " + name, n == 1, lines.stream().filter(l -> l.contains(" " + name)).toList().toString());
			}
			check("Melty checks file has only our check names", lines.stream().allMatch(l -> MeltyChecks.ALL.contains(l.split("[ :]")[1])), lines.toString());
			CraftTheft.LOG.info("[harness] melty checks file {}:\n{}", checks, String.join("\n", lines));
		} catch (Exception e) {
			check("Melty checks file readable", false, e.toString());
		}
	}

	private static int joined;

	private static void tick(Minecraft mc) {
		if (sounds.isEmpty() && mc.getSoundManager() != null && joined == 0) {
			joined = 1;
			mc.getSoundManager().addListener((sound, events, range) -> {
				if (sound.getLocation().getNamespace().equals(CraftTheft.MODID)) {
					String res = sound.getSound() == null ? "unresolved" : sound.getSound().getLocation().toString();
					if (sounds.put(sound.getLocation().toString(), res) == null) CraftTheft.LOG.info("[harness] sound {} -> {}", sound.getLocation(), res);
				}
			});
		}
		if (idx >= steps.size()) return;
		try {
			if (steps.get(idx).run(stepTick++)) { idx++; stepTick = 0; }
		} catch (Throwable e) {
			check("step " + idx + " threw", false, e.toString());
			CraftTheft.LOG.error("[harness] step {} failed", idx, e);
			idx++; stepTick = 0;
		}
	}

	static { WeaponItemRenderer.class.getName(); Inventory.class.getName(); HaloGrenade.class.getName(); }
}
