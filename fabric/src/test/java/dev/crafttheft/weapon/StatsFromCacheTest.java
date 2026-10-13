package dev.crafttheft.weapon;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

import dev.crafttheft.config.ModConfig;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.shield.ShieldTuning;

/**
 * The stats rows of the sources sheet (halo_stats_weapons / halo_stats_shield): the files are found at the rows' out
 * paths under the cache root, keys are weapons-sheet columns in the sheet's units, out-of-range values keep the sheet
 * value, and the shield max stays on the Fortnite 0..100 scale the potions use.
 */
class StatsFromCacheTest {
	@AfterEach
	void reset() {
		System.clearProperty("crafttheft.cacheDir");
		ModConfig.reload();
		WeaponStats.reload();
		ShieldTuning.reload();
	}

	private static void useCache(Path dir) {
		System.setProperty("crafttheft.cacheDir", dir.toString());
		ModConfig.reload();
	}

	@Test
	void weaponStatsAreReadFromTheSourcesRowOutPath(@TempDir Path dir) throws Exception {
		useCache(dir);
		Path f = dir.resolve(SourceRows.get(WeaponStats.SOURCE_ROW).out());
		assertEquals(dir.resolve("stats").resolve("halo_weapons.json"), f);
		Files.createDirectories(f.getParent());
		Files.writeString(f, "{\"assault_rifle\":{\"damage\":6.5,\"clip\":36,\"fireIntervalTicks\":3,\"auto\":true},"
				+ "\"sniper_rifle\":{\"zoom\":0.001,\"damage\":\"80\"},"
				+ "\"rocket_launcher\":{\"blastRadius\":5.5,\"projectileSpeed\":3.0},"
				+ "\"magnum\":{\"homing\":true,\"projectileSpeed\":9},"
				+ "\"no_such_weapon\":{\"damage\":1}}", StandardCharsets.UTF_8);
		WeaponStats.reload();

		WeaponStats ar = WeaponStats.get("assault_rifle");
		assertEquals(6.5f, ar.damage);
		assertEquals(36, ar.clip);
		assertEquals(3, ar.fireIntervalTicks);
		assertEquals(6.5f * Sheets.COMBAT_HALO_POINT_TO_MC, ar.mcDamage(), 1e-6, "Halo points -> MC damage via systems.combat.haloPointToMc");

		WeaponStats sn = WeaponStats.get("sniper_rifle");
		assertEquals(0.2f, sn.zoom, 1e-6, "zoom outside 0.05..1 keeps the sheet value");
		assertEquals(80f, sn.damage, "a string is not a number: sheet value");

		WeaponStats rl = WeaponStats.get("rocket_launcher");
		assertEquals(5.5f, rl.blastRadius);
		assertEquals(3.0f, rl.projectileSpeed);

		WeaponStats mg = WeaponStats.get("magnum");
		assertFalse(mg.homing, "homing only applies to projectile weapons");
		assertEquals(0f, mg.projectileSpeed, "a hitscan weapon has no projectile speed");
	}

	@Test
	void unreadableStatsFallBackToTheSheet(@TempDir Path dir) throws Exception {
		useCache(dir);
		Path f = dir.resolve(SourceRows.get(WeaponStats.SOURCE_ROW).out());
		Files.createDirectories(f.getParent());
		Files.writeString(f, "{\"assault_rifle\":{\"damage\":", StandardCharsets.UTF_8);
		WeaponStats.reload();
		assertEquals(7.5f, WeaponStats.get("assault_rifle").damage);
	}

	@Test
	void shieldTimingFromTheCacheMaxStaysOnTheFortniteScale(@TempDir Path dir) throws Exception {
		useCache(dir);
		Path f = dir.resolve(SourceRows.get(ShieldTuning.SOURCE_ROW).out());
		assertEquals(dir.resolve("stats").resolve("halo_shield.json"), f);
		Files.createDirectories(f.getParent());
		Files.writeString(f, "{\"shield\":{\"max\":100,\"rechargeDelayTicks\":80,\"rechargePerTick\":1.6667}}", StandardCharsets.UTF_8);
		ShieldTuning t = ShieldTuning.reload();
		assertEquals(100f, t.max);
		assertEquals(80, t.rechargeDelayTicks);
		assertEquals(1.6667f, t.rechargePerTick, 1e-5);
		assertEquals(Sheets.SHIELD_RECHARGE_CAP, t.rechargeCap, 1e-6);
		assertEquals(Sheets.SHIELD_START, t.start, 1e-6);
		assertEquals(20f, t.maxMc(), 1e-6, "100 shield points / systems.shield.pointsPerMcDamage 5 = 20 absorption");
		// a bad value keeps the sheet's
		Files.writeString(f, "{\"shield\":{\"rechargeDelayTicks\":-5}}", StandardCharsets.UTF_8);
		assertEquals(Sheets.SHIELD_RECHARGE_DELAY_TICKS, ShieldTuning.reload().rechargeDelayTicks);
	}
}
