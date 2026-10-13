package dev.crafttheft;

import static org.junit.jupiter.api.Assertions.*;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;

import org.junit.jupiter.api.Test;

import dev.crafttheft.client.render.RuntimeMesh;
import dev.crafttheft.gen.Sheets;
import dev.crafttheft.tools.MeshWriter;

/** The jar's placeholder writer and the mod's .cthm reader agree, and the reader rejects damaged files. */
class MeshFormatTest {
	private static byte[] car() {
		MeshWriter m = new MeshWriter();
		int lf = m.node("wheel_lf", 0.8f, 0.3f, 1.2f);
		m.box("placeholder/car_sultan", -1, -1f, 0.2f, -2f, 1f, 1.2f, 2f);
		m.wheel("placeholder/tyre", lf, 0.8f, 0.3f, 1.2f, 0.3f, 0.1f, 8);
		return m.toBytes();
	}

	@Test
	void roundTrip() throws IOException {
		RuntimeMesh r = RuntimeMesh.read(car());
		assertEquals(1, r.nodes.size());
		assertEquals("wheel_lf", r.nodes.get(0).name());
		assertEquals(2, r.submeshes.size());
		assertEquals(0, r.submeshes.get(1).node);
		assertEquals(12 + 8 * 4, r.triangleCount);
		assertEquals(-2f, r.minZ, 1e-6);
		assertEquals(2f, r.maxZ, 1e-6);
		assertEquals("placeholder/car_sultan", r.submeshes.get(0).material);
	}

	@Test
	void rejectsTruncatedAndCorrupt() {
		byte[] ok = car();
		for (int cut : new int[]{3, 8, 20, ok.length / 2, ok.length - 1})
			assertThrows(IOException.class, () -> RuntimeMesh.read(Arrays.copyOf(ok, cut)), "cut at " + cut);
		byte[] magic = ok.clone();
		magic[0] = 'X';
		assertThrows(IOException.class, () -> RuntimeMesh.read(magic));
		byte[] version = ok.clone();
		ByteBuffer.wrap(version).order(ByteOrder.LITTLE_ENDIAN).putInt(4, 2);
		assertThrows(IOException.class, () -> RuntimeMesh.read(version));
		byte[] index = ok.clone();
		ByteBuffer.wrap(index).order(ByteOrder.LITTLE_ENDIAN).putInt(index.length - 4, 1_000_000); // last index out of range
		assertThrows(IOException.class, () -> RuntimeMesh.read(index));
		byte[] nan = ok.clone();
		int firstFloat = 4 + 4 + 4 + (2 + "wheel_lf".length()); // node pivot x
		ByteBuffer.wrap(nan).order(ByteOrder.LITTLE_ENDIAN).putFloat(firstFloat, Float.NaN);
		assertThrows(IOException.class, () -> RuntimeMesh.read(nan));
	}

	@Test
	void soundEventRuleMatchesTheExtractor() {
		for (Sheets.Source s : Sheets.SOURCES) {
			if (!s.kind().equals("sound")) continue;
			assertEquals(dev.crafttheft.tools.GenResources.soundEvent(s), dev.crafttheft.content.SourceRows.soundEvent(s.id()));
			assertTrue(dev.crafttheft.tools.GenResources.vanillaEvent(s).startsWith("minecraft:"), s.id());
		}
		assertEquals("halo.assault_rifle_fire", dev.crafttheft.content.SourceRows.soundEvent("halo_snd_assault_rifle_fire"));
		assertEquals("halo/assault_rifle", dev.crafttheft.content.SourceRows.meshKey("halo_mesh_assault_rifle"));
	}
}
