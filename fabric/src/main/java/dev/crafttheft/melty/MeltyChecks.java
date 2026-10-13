package dev.crafttheft.melty;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import dev.crafttheft.CraftTheft;

/**
 * The checks this mod reports to Melty's test mode (names listed in docs/requests/mod.md for the recipe's
 * test.checks). Each is reported once per game session, at the moment it actually happens in normal play, through
 * {@link MeltyTest#report}. Also logged as "[melty] check NAME: pass|fail (why)" so a log shows them without Melty.
 */
public final class MeltyChecks {
	public static final String MOD_LOADS = "mod-loads", CACHE_READY = "cache-ready", HALO_CONTENT = "halo-content",
			GTA_CONTENT = "gta-content", FORTNITE_CONTENT = "fortnite-content", BUS_RIDE = "bus-ride", LANDED = "landed",
			CHEST_OPENED = "chest-opened", WEAPON_FIRED = "weapon-fired", CAR_DRIVEN = "car-driven",
			WANTED_STARS = "wanted-stars", POLICE_CHASE = "police-chase";
	public static final List<String> ALL = List.of(MOD_LOADS, CACHE_READY, HALO_CONTENT, GTA_CONTENT, FORTNITE_CONTENT,
			BUS_RIDE, LANDED, CHEST_OPENED, WEAPON_FIRED, CAR_DRIVEN, WANTED_STARS, POLICE_CHASE);

	private static final Map<String, String> RESULTS = new LinkedHashMap<>();

	private MeltyChecks() {}

	/** From ModInitializer.onInitialize: Melty's start() reports mod-loads when a test run is active. */
	public static synchronized void start() {
		MeltyTest.start();
		dev.crafttheft.Hooks.mark("test_mode", "MeltyChecks.start -> MeltyTest.start; MeltyChecks.report -> MeltyTest.report");
		RESULTS.put(MOD_LOADS, "pass");
		CraftTheft.LOG.info("[melty] check {}: pass (mod initialised)", MOD_LOADS);
	}

	public static void pass(String name, String detail) { report(name, true, detail); }

	public static void fail(String name, String why) { report(name, false, why); }

	public static synchronized void report(String name, boolean passed, String why) {
		if (RESULTS.containsKey(name)) return;
		RESULTS.put(name, passed ? "pass" : "fail: " + why);
		CraftTheft.LOG.info("[melty] check {}: {} ({})", name, passed ? "pass" : "fail", why);
		MeltyTest.report(name, passed, why);
	}

	public static synchronized boolean reported(String name) { return RESULTS.containsKey(name); }

	public static synchronized Map<String, String> results() { return new LinkedHashMap<>(RESULTS); }
}
