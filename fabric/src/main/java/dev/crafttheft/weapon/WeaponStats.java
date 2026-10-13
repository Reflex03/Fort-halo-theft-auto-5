package dev.crafttheft.weapon;

import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.gen.Sheets;

/**
 * Effective weapon stats: the weapons sheet row, overridden field by field by cache/stats/halo_weapons.json
 * (sources row halo_stats_weapons; keys are the sheet's column names, values in the sheet's units). Every
 * override is range-checked; a bad value is logged and the sheet value kept.
 */
public final class WeaponStats {
	public final String id, kind;
	public float damage, spreadDeg, range, projectileSpeed, blastRadius, zoom;
	public int pellets, burst, fireIntervalTicks, clip, reserve, reloadTicks;
	public boolean auto, homing;
	/** Fields that came from the player's own Halo install. */
	public final List<String> fromCache = new ArrayList<>();

	private WeaponStats(Sheets.Weapon w) {
		id = w.id(); kind = w.kind();
		damage = w.damage(); pellets = w.pellets(); burst = w.burst(); fireIntervalTicks = w.fireIntervalTicks(); auto = w.auto();
		clip = w.clip(); reserve = w.reserve(); reloadTicks = w.reloadTicks(); spreadDeg = w.spreadDeg(); range = w.range();
		projectileSpeed = w.projectileSpeed(); homing = w.homing(); blastRadius = w.blastRadius(); zoom = w.zoom();
	}

	/** Minecraft damage per round/pellet/impact. */
	public float mcDamage() { return damage * Sheets.COMBAT_HALO_POINT_TO_MC; }

	public boolean usesAmmo() { return !kind.equals("grenade"); }

	/** The sources row whose out path (under the cache root) holds the weapon stats read from the player's MCC. */
	public static final String SOURCE_ROW = "halo_stats_weapons";
	private static volatile Map<String, WeaponStats> current = Map.of();
	private static volatile Path loadedFrom;

	public static WeaponStats get(String id) {
		Map<String, WeaponStats> m = current;
		if (m.isEmpty()) m = reload();
		WeaponStats s = m.get(id);
		if (s == null) throw new IllegalArgumentException("no weapons row " + id);
		return s;
	}

	/** (Re)reads the stats file from the configured cache. Called at server start and on client resource reload. */
	public static synchronized Map<String, WeaponStats> reload() {
		Map<String, WeaponStats> m = new HashMap<>();
		for (Sheets.Weapon w : Sheets.WEAPONS) m.put(w.id(), new WeaponStats(w));
		Path file = ModConfig.get().cacheDir.resolve(SourceRows.get(SOURCE_ROW).out()); // sources.out: stats/halo_weapons.json
		if (!Files.isRegularFile(file)) {
			CraftTheft.LOG.info("[stats] {} not present: weapons use the sheet defaults", file);
		} else {
			try (Reader r = Files.newBufferedReader(file, StandardCharsets.UTF_8)) {
				JsonElement root = JsonParser.parseReader(r);
				if (!root.isJsonObject()) throw new IllegalStateException("not a JSON object");
				for (var e : root.getAsJsonObject().entrySet()) {
					WeaponStats s = m.get(e.getKey());
					if (s == null) { CraftTheft.LOG.warn("[stats] {}: '{}' is not a weapons row, ignored", file.getFileName(), e.getKey()); continue; }
					if (!e.getValue().isJsonObject()) continue;
					s.apply(e.getValue().getAsJsonObject());
					CraftTheft.LOG.info("[stats] {} from the Halo install: {}", s.id, s.fromCache.isEmpty() ? "nothing usable (sheet defaults)" : String.join(", ", s.fromCache));
				}
			} catch (Exception ex) {
				CraftTheft.LOG.error("[stats] {} unreadable ({}): weapons use the sheet defaults", file, ex.toString());
				m.clear();
				for (Sheets.Weapon w : Sheets.WEAPONS) m.put(w.id(), new WeaponStats(w));
			}
		}
		current = m;
		loadedFrom = file;
		return m;
	}

	private void apply(JsonObject o) {
		damage = f(o, "damage", damage, 0f, 5000f);
		spreadDeg = f(o, "spreadDeg", spreadDeg, 0f, 45f);
		range = f(o, "range", range, 1f, 512f);
		if (!kind.equals("hitscan")) projectileSpeed = f(o, "projectileSpeed", projectileSpeed, 0.05f, 20f);
		if (kind.equals("rocket") || kind.equals("grenade")) blastRadius = f(o, "blastRadius", blastRadius, 0.5f, 16f);
		zoom = f(o, "zoom", zoom, 0.05f, 1f);
		pellets = i(o, "pellets", pellets, 1, 64);
		burst = i(o, "burst", burst, 1, 16);
		fireIntervalTicks = i(o, "fireIntervalTicks", fireIntervalTicks, 1, 200);
		clip = i(o, "clip", clip, 1, 1000);
		reserve = i(o, "reserve", reserve, 0, 5000);
		reloadTicks = i(o, "reloadTicks", reloadTicks, 0, 400);
		auto = b(o, "auto", auto);
		if (kind.equals("projectile")) homing = b(o, "homing", homing);
	}

	private float f(JsonObject o, String k, float def, float min, float max) {
		JsonElement e = o.get(k);
		if (e == null || !e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) return def;
		float v = e.getAsFloat();
		if (!Float.isFinite(v) || v < min || v > max) {
			CraftTheft.LOG.warn("[stats] {}.{} = {} is outside {}..{}, keeping the sheet value {}", id, k, v, min, max, def);
			return def;
		}
		if (v != def) fromCache.add(k + " " + def + "->" + v);
		return v;
	}

	private int i(JsonObject o, String k, int def, int min, int max) {
		JsonElement e = o.get(k);
		if (e == null || !e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) return def;
		double d = e.getAsDouble();
		int v = (int) Math.round(d);
		if (!Double.isFinite(d) || v < min || v > max) {
			CraftTheft.LOG.warn("[stats] {}.{} = {} is outside {}..{}, keeping the sheet value {}", id, k, d, min, max, def);
			return def;
		}
		if (v != def) fromCache.add(k + " " + def + "->" + v);
		return v;
	}

	private boolean b(JsonObject o, String k, boolean def) {
		JsonElement e = o.get(k);
		if (e == null || !e.isJsonPrimitive() || !e.getAsJsonPrimitive().isBoolean()) return def;
		boolean v = e.getAsBoolean();
		if (v != def) fromCache.add(k + " " + def + "->" + v);
		return v;
	}

	public static Path loadedFrom() { return loadedFrom; }
}
