package dev.crafttheft.client.render;

import java.io.IOException;
import java.nio.BufferUnderflowException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;

import net.minecraft.resources.ResourceLocation;

/**
 * A .cthm v1 mesh in memory (docs/CONTRACTS.md): metres = blocks, +Y up, front +Z, little-endian.
 * <pre>
 *  "CTHM" u32 version=1
 *  u32 nodeCount  { str name; f32 x,y,z }
 *  u32 subCount   { str material; i32 node; u32 vertexCount; u32 indexCount; f32[vc*8] (px py pz nx ny nz u v); u32[ic] }
 *  str = u16 byteLength + UTF-8
 * </pre>
 * The reader validates everything (magic, version, counts, index range, finite numbers), so a truncated or corrupt
 * cache file is rejected with a message instead of breaking the render thread.
 */
public final class RuntimeMesh {
	public record Node(String name, float px, float py, float pz) {}

	public static final class Submesh {
		public final String material;
		public final int node;
		public final float[] verts;
		public final int[] indices;
		/** Resolved by MeshCache: crafttheft:textures/&lt;material&gt;.png, or the placeholder when that is missing. */
		public ResourceLocation texture;

		Submesh(String material, int node, float[] verts, int[] indices) {
			this.material = material; this.node = node; this.verts = verts; this.indices = indices;
		}
	}

	public final List<Node> nodes = new ArrayList<>();
	public final List<Submesh> submeshes = new ArrayList<>();
	public float minX = Float.MAX_VALUE, minY = Float.MAX_VALUE, minZ = Float.MAX_VALUE;
	public float maxX = -Float.MAX_VALUE, maxY = -Float.MAX_VALUE, maxZ = -Float.MAX_VALUE;
	public int triangleCount;
	/** Resource pack the file came from (crafttheft = the jar placeholder, crafttheft_cache_* = the player's cache). */
	public String sourcePack = "code";
	public String key = "";

	public static final int MAX_VERTS = 2_000_000, MAX_INDICES = 6_000_000, MAX_NODES = 4096, MAX_SUBS = 4096;

	public boolean fromCache() { return sourcePack.startsWith("crafttheft_cache"); }

	public float sizeX() { return maxX - minX; }
	public float sizeY() { return maxY - minY; }
	public float sizeZ() { return maxZ - minZ; }

	public static RuntimeMesh read(byte[] all) throws IOException {
		try {
			ByteBuffer b = ByteBuffer.wrap(all).order(ByteOrder.LITTLE_ENDIAN);
			if (b.remaining() < 8 || b.get() != 'C' || b.get() != 'T' || b.get() != 'H' || b.get() != 'M') throw new IOException("not a CTHM file");
			int version = b.getInt();
			if (version != 1) throw new IOException("unsupported CTHM version " + version);
			RuntimeMesh m = new RuntimeMesh();
			int nodeCount = b.getInt();
			if (nodeCount < 0 || nodeCount > MAX_NODES) throw new IOException("bad node count " + nodeCount);
			for (int i = 0; i < nodeCount; i++) m.nodes.add(new Node(str(b), fin(b.getFloat()), fin(b.getFloat()), fin(b.getFloat())));
			int subCount = b.getInt();
			if (subCount < 0 || subCount > MAX_SUBS) throw new IOException("bad submesh count " + subCount);
			long totalVerts = 0;
			for (int s = 0; s < subCount; s++) {
				String mat = str(b);
				int node = b.getInt();
				int vc = b.getInt(), ic = b.getInt();
				totalVerts += vc;
				if (vc < 0 || vc > MAX_VERTS || totalVerts > MAX_VERTS || ic < 0 || ic > MAX_INDICES || ic % 3 != 0)
					throw new IOException("bad counts " + vc + "/" + ic + " in submesh " + s);
				if ((long) vc * 32 + (long) ic * 4 > b.remaining()) throw new IOException("truncated submesh " + s);
				float[] v = new float[vc * 8];
				for (int i = 0; i < v.length; i++) v[i] = fin(b.getFloat());
				int[] idx = new int[ic];
				for (int i = 0; i < ic; i++) {
					idx[i] = b.getInt();
					if (idx[i] < 0 || idx[i] >= vc) throw new IOException("index " + idx[i] + " out of range in submesh " + s);
				}
				for (int i = 0; i < vc; i++) {
					float x = v[i * 8], y = v[i * 8 + 1], z = v[i * 8 + 2];
					m.minX = Math.min(m.minX, x); m.minY = Math.min(m.minY, y); m.minZ = Math.min(m.minZ, z);
					m.maxX = Math.max(m.maxX, x); m.maxY = Math.max(m.maxY, y); m.maxZ = Math.max(m.maxZ, z);
				}
				m.triangleCount += ic / 3;
				m.submeshes.add(new Submesh(mat, node >= 0 && node < nodeCount ? node : -1, v, idx));
			}
			if (m.triangleCount == 0) throw new IOException("mesh has no triangles");
			return m;
		} catch (BufferUnderflowException e) {
			throw new IOException("truncated file");
		}
	}

	private static float fin(float f) throws IOException {
		if (!Float.isFinite(f)) throw new IOException("non-finite number");
		return f;
	}

	private static String str(ByteBuffer b) throws IOException {
		int len = Short.toUnsignedInt(b.getShort());
		if (len > b.remaining()) throw new IOException("truncated string");
		byte[] s = new byte[len];
		b.get(s);
		return new String(s, StandardCharsets.UTF_8);
	}

	/** Code-built box: the last resort when neither the cache nor the jar has a mesh. */
	public static RuntimeMesh box(float w, float h, float d, ResourceLocation texture) {
		RuntimeMesh m = new RuntimeMesh();
		float x0 = -w / 2, x1 = w / 2, z0 = -d / 2, z1 = d / 2;
		float[][] faces = {
				{x0, 0, z1, x1, 0, z1, x1, h, z1, x0, h, z1, 0, 0, 1},
				{x1, 0, z0, x0, 0, z0, x0, h, z0, x1, h, z0, 0, 0, -1},
				{x0, 0, z0, x0, 0, z1, x0, h, z1, x0, h, z0, -1, 0, 0},
				{x1, 0, z1, x1, 0, z0, x1, h, z0, x1, h, z1, 1, 0, 0},
				{x0, h, z1, x1, h, z1, x1, h, z0, x0, h, z0, 0, 1, 0},
				{x0, 0, z0, x1, 0, z0, x1, 0, z1, x0, 0, z1, 0, -1, 0},
		};
		float[] v = new float[6 * 4 * 8];
		int[] idx = new int[36];
		float[][] uv = {{0, 1}, {1, 1}, {1, 0}, {0, 0}};
		for (int f = 0; f < 6; f++) {
			for (int k = 0; k < 4; k++) {
				int o = (f * 4 + k) * 8;
				v[o] = faces[f][k * 3]; v[o + 1] = faces[f][k * 3 + 1]; v[o + 2] = faces[f][k * 3 + 2];
				v[o + 3] = faces[f][12]; v[o + 4] = faces[f][13]; v[o + 5] = faces[f][14];
				v[o + 6] = uv[k][0]; v[o + 7] = uv[k][1];
			}
			int b = f * 4;
			System.arraycopy(new int[]{b, b + 1, b + 2, b, b + 2, b + 3}, 0, idx, f * 6, 6);
		}
		Submesh s = new Submesh("placeholder/generic", -1, v, idx);
		s.texture = texture;
		m.submeshes.add(s);
		m.minX = x0; m.maxX = x1; m.minY = 0; m.maxY = h; m.minZ = z0; m.maxZ = z1;
		m.triangleCount = 12;
		return m;
	}
}
