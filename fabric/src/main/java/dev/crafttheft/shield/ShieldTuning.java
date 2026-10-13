package dev.crafttheft.shield;

import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import dev.crafttheft.CraftTheft;
import dev.crafttheft.config.ModConfig;
import dev.crafttheft.content.SourceRows;
import dev.crafttheft.gen.Sheets;

/**
 * Halo energy shield numbers: the systems sheet's shield rows, with the timing (rechargeDelayTicks,
 * rechargePerTick) and max overridden by cache/stats/halo_shield.json {"shield":{...}} when the player's own
 * MCC install provided them (sources row halo_stats_shield). Values are in shield points (Fortnite 0-100 scale);
 * one Minecraft damage point removes systems.shield.pointsPerMcDamage points.
 */
public final class ShieldTuning {
	public final float max, start, rechargePerTick, rechargeCap;
	public final int rechargeDelayTicks;
	public final String source;

	private ShieldTuning(float max, float start, int delay, float perTick, float cap, String source) {
		this.max = max; this.start = start; this.rechargeDelayTicks = delay; this.rechargePerTick = perTick; this.rechargeCap = cap; this.source = source;
	}

	public static float toMc(float points) { return points / Sheets.SHIELD_POINTS_PER_MC_DAMAGE; }

	public float maxMc() { return toMc(max); }

	/** The sources row whose out path (under the cache root) holds the shield timing read from the player's MCC. */
	public static final String SOURCE_ROW = "halo_stats_shield";
	private static volatile ShieldTuning current;

	public static ShieldTuning get() {
		ShieldTuning t = current;
		return t != null ? t : reload();
	}

	public static synchronized ShieldTuning reload() {
		float max = Sheets.SHIELD_MAX, perTick = Sheets.SHIELD_RECHARGE_PER_TICK;
		int delay = Sheets.SHIELD_RECHARGE_DELAY_TICKS;
		String source = "systems sheet";
		Path file = ModConfig.get().cacheDir.resolve(SourceRows.get(SOURCE_ROW).out()); // sources.out: stats/halo_shield.json
		if (Files.isRegularFile(file)) {
			try (Reader r = Files.newBufferedReader(file, StandardCharsets.UTF_8)) {
				JsonElement root = JsonParser.parseReader(r);
				JsonObject o = root.isJsonObject() && root.getAsJsonObject().has("shield") && root.getAsJsonObject().get("shield").isJsonObject()
						? root.getAsJsonObject().getAsJsonObject("shield") : null;
				if (o == null) throw new IllegalStateException("no \"shield\" object");
				StringBuilder used = new StringBuilder();
				float m = num(o, "max", max, 1, 1000, used);
				int d = Math.round(num(o, "rechargeDelayTicks", delay, 0, 2000, used));
				float p = num(o, "rechargePerTick", perTick, 0.01f, 100, used);
				max = m; delay = d; perTick = p;
				source = used.length() == 0 ? "systems sheet (" + file.getFileName() + " had no usable values)" : "Halo install: " + used;
			} catch (Exception e) {
				CraftTheft.LOG.warn("[shield] {} unreadable ({}): using the systems sheet", file, e.toString());
			}
		}
		float cap = Math.min(Sheets.SHIELD_RECHARGE_CAP, max), start = Math.min(Sheets.SHIELD_START, max);
		ShieldTuning t = new ShieldTuning(max, start, delay, perTick, cap, source);
		CraftTheft.LOG.info("[shield] max {} start {} rechargeCap {} delay {} ticks, {} points/tick ({})", max, start, cap, delay, perTick, source);
		current = t;
		return t;
	}

	private static float num(JsonObject o, String k, float def, float min, float max, StringBuilder used) {
		JsonElement e = o.get(k);
		if (e == null || !e.isJsonPrimitive() || !e.getAsJsonPrimitive().isNumber()) return def;
		float v = e.getAsFloat();
		if (!Float.isFinite(v) || v < min || v > max) {
			CraftTheft.LOG.warn("[shield] {} = {} outside {}..{}, keeping {}", k, v, min, max, def);
			return def;
		}
		used.append(used.length() == 0 ? "" : ", ").append(k).append('=').append(v);
		return v;
	}
}
