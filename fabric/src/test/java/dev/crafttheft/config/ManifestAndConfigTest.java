package dev.crafttheft.config;

import static org.junit.jupiter.api.Assertions.*;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Properties;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** manifest.json as the extractor writes it, and crafttheft.properties values as Melty writes them. */
class ManifestAndConfigTest {
	@Test
	void readsTheExtractorsManifest(@TempDir Path dir) throws Exception {
		assertFalse(CacheManifest.read(dir).usable());
		assertTrue(CacheManifest.read(dir).problem.contains("no manifest.json"));
		Files.writeString(dir.resolve("manifest.json"), "{\"cacheSchema\":1,\"extractor\":\"0.1.0\",\"status\":\"partial\",\"finishedUtc\":\"x\","
				+ "\"games\":{\"halo\":{\"status\":\"ok\",\"error\":null},\"gta\":{\"status\":\"failed\",\"error\":\"boom\"}},"
				+ "\"assets\":{\"gta_mesh_infernus\":{\"status\":\"failed\",\"detail\":\"bad fragment\",\"out\":null}}}", StandardCharsets.UTF_8);
		CacheManifest m = CacheManifest.read(dir);
		assertTrue(m.usable());
		assertEquals("partial", m.status);
		assertEquals("failed", m.games.get("gta").status());
		assertEquals("boom", m.games.get("gta").error());
		assertTrue(m.explain("gta_mesh_infernus", "gta").contains("bad fragment"));
		Files.writeString(dir.resolve("manifest.json"), "{\"cacheSchema\":0}", StandardCharsets.UTF_8);
		assertFalse(CacheManifest.read(dir).usable());
		Files.writeString(dir.resolve("manifest.json"), "{\"cacheSch", StandardCharsets.UTF_8);
		assertFalse(CacheManifest.read(dir).usable());
	}

	@Test
	void propertiesValues() throws Exception {
		Properties p = new Properties();
		p.load(new java.io.StringReader("halo_dir=C\\:\\\\Games\\\\MCC\ngta_dir={game:gta-v}\nfortnite_dir=\ncache_dir=\"/tmp/x y\"\n"));
		assertEquals("C:\\Games\\MCC", ModConfig.value(p, "halo_dir"));
		assertNull(ModConfig.value(p, "gta_dir"), "an unexpanded Melty placeholder counts as not given");
		assertNull(ModConfig.value(p, "fortnite_dir"));
		assertEquals("/tmp/x y", ModConfig.value(p, "cache_dir"));
		assertNull(ModConfig.expandEnv("%CRAFTTHEFT_SURELY_UNSET_VAR%/cache"));
		assertEquals(System.getenv("PATH") + "/x", ModConfig.expandEnv("%PATH%/x"));
	}
}
