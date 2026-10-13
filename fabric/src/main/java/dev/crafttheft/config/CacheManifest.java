package dev.crafttheft.config;

import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.LinkedHashMap;
import java.util.Map;

import org.jetbrains.annotations.Nullable;

import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

/**
 * Read-only view of cache/manifest.json (written last by the extractor; docs/CONTRACTS.md and
 * docs/requests/extractor.md). Never throws: a missing or unreadable file yields {@link #problem}.
 */
public final class CacheManifest {
	public record Game(String status, @Nullable String error, @Nullable String dir) {}
	public record Asset(String status, @Nullable String detail, @Nullable String out) {}

	public final int cacheSchema;
	public final String status;
	@Nullable public final String finishedUtc;
	public final Map<String, Game> games = new LinkedHashMap<>();
	public final Map<String, Asset> assets = new LinkedHashMap<>();
	@Nullable public final String problem;

	private CacheManifest(int schema, String status, @Nullable String finished, @Nullable String problem) {
		this.cacheSchema = schema; this.status = status; this.finishedUtc = finished; this.problem = problem;
	}

	public boolean usable() { return problem == null && cacheSchema >= 1; }

	public static Path path(Path cacheDir) { return cacheDir.resolve("manifest.json"); }

	public static CacheManifest read(Path cacheDir) {
		Path f = path(cacheDir);
		if (!Files.isRegularFile(f)) return new CacheManifest(0, "missing", null, "no manifest.json in " + cacheDir);
		try (Reader r = Files.newBufferedReader(f, StandardCharsets.UTF_8)) {
			JsonElement root = JsonParser.parseReader(r);
			if (!root.isJsonObject()) return new CacheManifest(0, "failed", null, "manifest.json is not a JSON object");
			JsonObject o = root.getAsJsonObject();
			int schema = o.has("cacheSchema") && o.get("cacheSchema").isJsonPrimitive() ? o.get("cacheSchema").getAsInt() : 0;
			CacheManifest m = new CacheManifest(schema, str(o, "status", "failed"), str(o, "finishedUtc", null),
					schema < 1 ? "manifest.json has cacheSchema " + schema + " (need >= 1)" : null);
			if (o.has("games") && o.get("games").isJsonObject())
				for (var e : o.getAsJsonObject("games").entrySet())
					if (e.getValue().isJsonObject()) {
						JsonObject g = e.getValue().getAsJsonObject();
						m.games.put(e.getKey(), new Game(str(g, "status", "missing"), str(g, "error", null), str(g, "dir", null)));
					}
			if (o.has("assets") && o.get("assets").isJsonObject())
				for (var e : o.getAsJsonObject("assets").entrySet())
					if (e.getValue().isJsonObject()) {
						JsonObject a = e.getValue().getAsJsonObject();
						m.assets.put(e.getKey(), new Asset(str(a, "status", "missing"), str(a, "detail", null), str(a, "out", null)));
					}
			return m;
		} catch (Exception e) {
			return new CacheManifest(0, "failed", null, "manifest.json unreadable: " + e);
		}
	}

	/** Why a sources row shows its placeholder, for logs and failed checks. */
	public String explain(String sourceId, String gameShort) {
		if (problem != null) return problem;
		Asset a = assets.get(sourceId);
		Game g = games.get(gameShort);
		String s = "manifest: " + gameShort + " " + (g == null ? "not listed" : g.status() + (g.error() == null ? "" : " (" + g.error() + ")"));
		if (a != null) s += "; " + sourceId + " " + a.status() + (a.detail() == null ? "" : " (" + a.detail() + ")");
		return s;
	}

	@Nullable
	private static String str(JsonObject o, String k, @Nullable String def) {
		JsonElement e = o.get(k);
		return e != null && e.isJsonPrimitive() ? e.getAsString() : def;
	}
}
