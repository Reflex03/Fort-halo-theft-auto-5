package dev.crafttheft.tools;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * Builds simple original shapes (boxes, prisms, spheres) and writes them as .cthm v1 (docs/CONTRACTS.md):
 * "CTHM" u32 version=1, nodes {str name; f32 x,y,z}, submeshes {str material; i32 node; u32 vc; u32 ic;
 * f32[vc*8] (px py pz nx ny nz u v); u32[ic]}, str = u16 byte length + UTF-8, little-endian.
 */
public final class MeshWriter {
	public record Node(String name, float x, float y, float z) {}

	static final class Sub {
		final String material;
		final int node;
		final List<float[]> verts = new ArrayList<>();
		final List<Integer> idx = new ArrayList<>();
		Sub(String material, int node) { this.material = material; this.node = node; }
	}

	private final List<Node> nodes = new ArrayList<>();
	private final Map<String, Sub> subs = new LinkedHashMap<>();

	public int node(String name, float x, float y, float z) {
		nodes.add(new Node(name, x, y, z));
		return nodes.size() - 1;
	}

	private Sub sub(String material, int node) {
		return subs.computeIfAbsent(material + "#" + node, k -> new Sub(material, node));
	}

	private void quad(Sub s, float[] a, float[] b, float[] c, float[] d, float nx, float ny, float nz) {
		int base = s.verts.size();
		s.verts.add(new float[]{a[0], a[1], a[2], nx, ny, nz, 0, 1});
		s.verts.add(new float[]{b[0], b[1], b[2], nx, ny, nz, 1, 1});
		s.verts.add(new float[]{c[0], c[1], c[2], nx, ny, nz, 1, 0});
		s.verts.add(new float[]{d[0], d[1], d[2], nx, ny, nz, 0, 0});
		s.idx.add(base); s.idx.add(base + 1); s.idx.add(base + 2);
		s.idx.add(base); s.idx.add(base + 2); s.idx.add(base + 3);
	}

	/** Axis-aligned box, counter-clockwise outward faces, each face UV 0..1. */
	public MeshWriter box(String material, int node, float x0, float y0, float z0, float x1, float y1, float z1) {
		Sub s = sub(material, node);
		float[] p000 = {x0, y0, z0}, p100 = {x1, y0, z0}, p010 = {x0, y1, z0}, p110 = {x1, y1, z0};
		float[] p001 = {x0, y0, z1}, p101 = {x1, y0, z1}, p011 = {x0, y1, z1}, p111 = {x1, y1, z1};
		quad(s, p001, p101, p111, p011, 0, 0, 1);   // +Z (front)
		quad(s, p100, p000, p010, p110, 0, 0, -1);  // -Z
		quad(s, p000, p001, p011, p010, -1, 0, 0);  // -X
		quad(s, p101, p100, p110, p111, 1, 0, 0);   // +X
		quad(s, p011, p111, p110, p010, 0, 1, 0);   // +Y
		quad(s, p000, p100, p101, p001, 0, -1, 0);  // -Y
		return this;
	}

	/** Prism (wheel) along the X axis centred at (cx, cy, cz). */
	public MeshWriter wheel(String material, int node, float cx, float cy, float cz, float radius, float halfWidth, int segments) {
		Sub s = sub(material, node);
		float xl = cx - halfWidth, xr = cx + halfWidth;
		for (int i = 0; i < segments; i++) {
			double a0 = 2 * Math.PI * i / segments, a1 = 2 * Math.PI * (i + 1) / segments;
			float y0 = cy + (float) (Math.sin(a0) * radius), z0 = cz + (float) (Math.cos(a0) * radius);
			float y1 = cy + (float) (Math.sin(a1) * radius), z1 = cz + (float) (Math.cos(a1) * radius);
			double am = (a0 + a1) / 2;
			quad(s, new float[]{xl, y0, z0}, new float[]{xl, y1, z1}, new float[]{xr, y1, z1}, new float[]{xr, y0, z0},
					0, (float) Math.sin(am), (float) Math.cos(am));
			// caps (triangle fans written as degenerate quads keep this simple)
			int b = s.verts.size();
			s.verts.add(new float[]{xr, cy, cz, 1, 0, 0, 0.5f, 0.5f});
			s.verts.add(new float[]{xr, y0, z0, 1, 0, 0, 0.5f + (float) Math.cos(a0) / 2, 0.5f + (float) Math.sin(a0) / 2});
			s.verts.add(new float[]{xr, y1, z1, 1, 0, 0, 0.5f + (float) Math.cos(a1) / 2, 0.5f + (float) Math.sin(a1) / 2});
			s.idx.add(b); s.idx.add(b + 2); s.idx.add(b + 1);
			b = s.verts.size();
			s.verts.add(new float[]{xl, cy, cz, -1, 0, 0, 0.5f, 0.5f});
			s.verts.add(new float[]{xl, y0, z0, -1, 0, 0, 0.5f + (float) Math.cos(a0) / 2, 0.5f + (float) Math.sin(a0) / 2});
			s.verts.add(new float[]{xl, y1, z1, -1, 0, 0, 0.5f + (float) Math.cos(a1) / 2, 0.5f + (float) Math.sin(a1) / 2});
			s.idx.add(b); s.idx.add(b + 1); s.idx.add(b + 2);
		}
		return this;
	}

	/** UV sphere. */
	public MeshWriter sphere(String material, int node, float cx, float cy, float cz, float rx, float ry, float rz, int rings, int segments) {
		Sub s = sub(material, node);
		int base = s.verts.size();
		for (int r = 0; r <= rings; r++) {
			double phi = Math.PI * r / rings;
			for (int g = 0; g <= segments; g++) {
				double th = 2 * Math.PI * g / segments;
				float nx = (float) (Math.sin(phi) * Math.cos(th)), ny = (float) Math.cos(phi), nz = (float) (Math.sin(phi) * Math.sin(th));
				s.verts.add(new float[]{cx + nx * rx, cy + ny * ry, cz + nz * rz, nx, ny, nz, (float) g / segments, (float) r / rings});
			}
		}
		for (int r = 0; r < rings; r++)
			for (int g = 0; g < segments; g++) {
				int a = base + r * (segments + 1) + g, b = a + segments + 1;
				s.idx.add(a); s.idx.add(a + 1); s.idx.add(b);
				s.idx.add(a + 1); s.idx.add(b + 1); s.idx.add(b);
			}
		return this;
	}

	public int triangles() {
		int n = 0;
		for (Sub s : subs.values()) n += s.idx.size() / 3;
		return n;
	}

	public byte[] toBytes() {
		try {
			ByteArrayOutputStream out = new ByteArrayOutputStream();
			out.write(new byte[]{'C', 'T', 'H', 'M'});
			u32(out, 1);
			u32(out, nodes.size());
			for (Node n : nodes) { str(out, n.name()); f32(out, n.x()); f32(out, n.y()); f32(out, n.z()); }
			u32(out, subs.size());
			for (Sub s : subs.values()) {
				str(out, s.material);
				u32(out, s.node);
				u32(out, s.verts.size());
				u32(out, s.idx.size());
				for (float[] v : s.verts) for (float f : v) f32(out, f);
				for (int i : s.idx) u32(out, i);
			}
			return out.toByteArray();
		} catch (IOException e) {
			throw new RuntimeException(e);
		}
	}

	public void write(Path file) throws IOException {
		Files.createDirectories(file.getParent());
		Files.write(file, toBytes());
	}

	private static void u32(ByteArrayOutputStream o, int v) {
		o.writeBytes(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(v).array());
	}

	private static void f32(ByteArrayOutputStream o, float v) {
		o.writeBytes(ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putFloat(v).array());
	}

	private static void str(ByteArrayOutputStream o, String s) {
		byte[] b = s.getBytes(StandardCharsets.UTF_8);
		o.writeBytes(ByteBuffer.allocate(2).order(ByteOrder.LITTLE_ENDIAN).putShort((short) b.length).array());
		o.writeBytes(b);
	}
}
