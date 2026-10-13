package dev.crafttheft.config;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import org.junit.jupiter.api.Assumptions;
import org.junit.jupiter.api.Test;

import dev.crafttheft.client.render.RuntimeMesh;
import dev.crafttheft.gen.Sheets;

/**
 * The mod's readers on a cache written by the REAL extractor (extractor/tests/e2e.sh: cthextract with its real modules
 * on synthetic game folders). Skipped when that cache does not exist (run the e2e script first), or point
 * CTH_E2E_CACHE at another cache folder.
 */
class RealExtractorCacheTest {
	private static Path cache() {
		String env = System.getenv("CTH_E2E_CACHE");
		Path p = env != null && !env.isBlank() ? Paths.get(env) : Paths.get("..", "extractor", "bin", "e2e", "cache");
		Assumptions.assumeTrue(Files.isRegularFile(p.resolve("manifest.json")), "no real extractor cache at " + p.toAbsolutePath() + " (run extractor/tests/e2e.sh)");
		return p;
	}

	@Test
	void manifestRowsAndFilesReadTheWayTheModReadsThem() throws Exception {
		Path cache = cache();
		CacheManifest m = CacheManifest.read(cache);
		assertTrue(m.usable(), "problem=" + m.problem);
		assertEquals(1, m.cacheSchema);
		for (String g : new String[]{"halo", "gta", "fortnite"}) assertTrue(m.games.containsKey(g), g);
		int meshes = 0, sounds = 0;
		for (Sheets.Source s : Sheets.SOURCES) {
			CacheManifest.Asset a = m.assets.get(s.id());
			assertNotNull(a, "manifest lacks " + s.id());
			String g = switch (s.game()) { case "halo-mcc" -> "halo"; case "gta-v" -> "gta"; default -> "fortnite"; };
			if (!"ok".equals(a.status())) {
				assertFalse(m.explain(s.id(), g).isBlank(), s.id());
				continue;
			}
			assertEquals(s.out(), a.out(), s.id());
			Path assets = cache.resolve("packs").resolve(g).resolve("assets").resolve("crafttheft");
			if (s.kind().equals("mesh")) {
				RuntimeMesh r = RuntimeMesh.read(Files.readAllBytes(assets.resolve(s.out())));
				assertTrue(r.triangleCount > 0 && r.triangleCount <= s.maxTris(), s.id() + ": " + r.triangleCount + " tris");
				for (RuntimeMesh.Submesh sm : r.submeshes)
					assertTrue(Files.isRegularFile(assets.resolve("textures").resolve(sm.material + ".png")), s.id() + " material " + sm.material);
				if (s.id().startsWith("gta_mesh_")) assertEquals(4, r.nodes.size(), s.id() + " wheel nodes");
				meshes++;
			} else if (s.kind().equals("sound")) {
				JsonObject sj = JsonParser.parseString(Files.readString(assets.resolve("sounds.json"), StandardCharsets.UTF_8)).getAsJsonObject();
				String ev = s.out().substring("sounds/".length(), s.out().length() - ".ogg".length()).replace('/', '.');
				assertTrue(sj.has(ev) && sj.getAsJsonObject(ev).get("replace").getAsBoolean(), s.id() + " event " + ev);
				byte[] ogg = Files.readAllBytes(assets.resolve(s.out()));
				assertEquals("OggS", new String(ogg, 0, 4, StandardCharsets.US_ASCII), s.id());
				sounds++;
			}
			Path mcmeta = cache.resolve("packs").resolve(g).resolve("pack.mcmeta");
			assertEquals(34, JsonParser.parseString(Files.readString(mcmeta)).getAsJsonObject().getAsJsonObject("pack").get("pack_format").getAsInt());
		}
		assertTrue(meshes + sounds > 0, "the real cache has no ok mesh or sound row");
	}
}
