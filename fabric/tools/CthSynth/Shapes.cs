using CthExtract.Core;

namespace CthSynth;

/// <summary>Original synthetic shapes (boxes, wheels, spheres) assembled into MeshData in Minecraft space.</summary>
public sealed class Shapes
{
    public readonly MeshData Mesh = new();
    private readonly Dictionary<(string, int), MeshData.Submesh> _subs = new();

    public int Node(string name, float x, float y, float z)
    {
        Mesh.Nodes.Add(new MeshData.Node { Name = name, X = x, Y = y, Z = z });
        return Mesh.Nodes.Count - 1;
    }

    private MeshData.Submesh Sub(string material, int node)
    {
        if (!_subs.TryGetValue((material, node), out var s))
        {
            s = new MeshData.Submesh { Material = material, Node = node };
            _subs[(material, node)] = s;
            Mesh.Submeshes.Add(s);
        }
        return s;
    }

    private static void Quad(MeshData.Submesh s, float[] a, float[] b, float[] c, float[] d, float nx, float ny, float nz, float u0 = 0, float v0 = 0, float u1 = 1, float v1 = 1)
    {
        uint i = (uint)s.VertexCount;
        s.Vertices.AddRange(new[] { a[0], a[1], a[2], nx, ny, nz, u0, v1 });
        s.Vertices.AddRange(new[] { b[0], b[1], b[2], nx, ny, nz, u1, v1 });
        s.Vertices.AddRange(new[] { c[0], c[1], c[2], nx, ny, nz, u1, v0 });
        s.Vertices.AddRange(new[] { d[0], d[1], d[2], nx, ny, nz, u0, v0 });
        s.Indices.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
    }

    public Shapes Box(string material, float x0, float y0, float z0, float x1, float y1, float z1, int node = -1)
    {
        var s = Sub(material, node);
        float[] p000 = { x0, y0, z0 }, p100 = { x1, y0, z0 }, p010 = { x0, y1, z0 }, p110 = { x1, y1, z0 };
        float[] p001 = { x0, y0, z1 }, p101 = { x1, y0, z1 }, p011 = { x0, y1, z1 }, p111 = { x1, y1, z1 };
        Quad(s, p001, p101, p111, p011, 0, 0, 1);
        Quad(s, p100, p000, p010, p110, 0, 0, -1);
        Quad(s, p000, p001, p011, p010, -1, 0, 0);
        Quad(s, p101, p100, p110, p111, 1, 0, 0);
        Quad(s, p011, p111, p110, p010, 0, 1, 0);
        Quad(s, p000, p100, p101, p001, 0, -1, 0);
        return this;
    }

    /// <summary>A wedge: a box whose top face slopes from y1Back (at z0) to y1Front (at z1).</summary>
    public Shapes Wedge(string material, float x0, float y0, float z0, float x1, float y1Back, float y1Front, float z1, int node = -1)
    {
        var s = Sub(material, node);
        float[] p000 = { x0, y0, z0 }, p100 = { x1, y0, z0 }, p010 = { x0, y1Back, z0 }, p110 = { x1, y1Back, z0 };
        float[] p001 = { x0, y0, z1 }, p101 = { x1, y0, z1 }, p011 = { x0, y1Front, z1 }, p111 = { x1, y1Front, z1 };
        float dz = z1 - z0, dy = y1Front - y1Back, len = MathF.Sqrt(dz * dz + dy * dy);
        Quad(s, p001, p101, p111, p011, 0, 0, 1);
        Quad(s, p100, p000, p010, p110, 0, 0, -1);
        Quad(s, p000, p001, p011, p010, -1, 0, 0);
        Quad(s, p101, p100, p110, p111, 1, 0, 0);
        Quad(s, p011, p111, p110, p010, 0, dz / len, -dy / len);
        Quad(s, p000, p100, p101, p001, 0, -1, 0);
        return this;
    }

    public Shapes Wheel(string material, int node, float cx, float cy, float cz, float r, float halfWidth, int seg = 16)
    {
        var s = Sub(material, node);
        float xl = cx - halfWidth, xr = cx + halfWidth;
        for (int i = 0; i < seg; i++)
        {
            double a0 = 2 * Math.PI * i / seg, a1 = 2 * Math.PI * (i + 1) / seg, am = (a0 + a1) / 2;
            float y0 = cy + (float)(Math.Sin(a0) * r), z0 = cz + (float)(Math.Cos(a0) * r);
            float y1 = cy + (float)(Math.Sin(a1) * r), z1 = cz + (float)(Math.Cos(a1) * r);
            Quad(s, new[] { xl, y0, z0 }, new[] { xl, y1, z1 }, new[] { xr, y1, z1 }, new[] { xr, y0, z0 }, 0, (float)Math.Sin(am), (float)Math.Cos(am), 0, 0, 0.1f, 1);
            foreach (var (x, nx) in new[] { (xr, 1f), (xl, -1f) })
            {
                uint b = (uint)s.VertexCount;
                s.Vertices.AddRange(new[] { x, cy, cz, nx, 0, 0, 0.5f, 0.5f });
                s.Vertices.AddRange(new[] { x, y0, z0, nx, 0, 0, 0.5f + (float)Math.Cos(a0) / 2, 0.5f + (float)Math.Sin(a0) / 2 });
                s.Vertices.AddRange(new[] { x, y1, z1, nx, 0, 0, 0.5f + (float)Math.Cos(a1) / 2, 0.5f + (float)Math.Sin(a1) / 2 });
                if (nx > 0) s.Indices.AddRange(new[] { b, b + 2, b + 1 }); else s.Indices.AddRange(new[] { b, b + 1, b + 2 });
            }
        }
        return this;
    }

    public Shapes Sphere(string material, float cx, float cy, float cz, float rx, float ry, float rz, int rings = 12, int seg = 20, int node = -1)
    {
        var s = Sub(material, node);
        uint b = (uint)s.VertexCount;
        for (int r = 0; r <= rings; r++)
        {
            double phi = Math.PI * r / rings;
            for (int g = 0; g <= seg; g++)
            {
                double th = 2 * Math.PI * g / seg;
                float nx = (float)(Math.Sin(phi) * Math.Cos(th)), ny = (float)Math.Cos(phi), nz = (float)(Math.Sin(phi) * Math.Sin(th));
                s.Vertices.AddRange(new[] { cx + nx * rx, cy + ny * ry, cz + nz * rz, nx, ny, nz, (float)g / seg, (float)r / rings });
            }
        }
        for (int r = 0; r < rings; r++)
            for (int g = 0; g < seg; g++)
            {
                uint a = b + (uint)(r * (seg + 1) + g), c = a + (uint)seg + 1;
                s.Indices.AddRange(new[] { a, a + 1, c, a + 1, c + 1, c });
            }
        return this;
    }
}

/// <summary>RGBA pixel art for synthetic textures and icons.</summary>
public sealed class Pixels
{
    public readonly int W, H;
    public readonly byte[] P;

    public Pixels(int w, int h) { W = w; H = h; P = new byte[w * h * 4]; }

    public Pixels Set(int x, int y, uint argb)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return this;
        int i = (y * W + x) * 4;
        P[i] = (byte)(argb >> 16); P[i + 1] = (byte)(argb >> 8); P[i + 2] = (byte)argb; P[i + 3] = (byte)(argb >> 24);
        return this;
    }

    public Pixels Fill(int x0, int y0, int x1, int y1, uint c) { for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) Set(x, y, c); return this; }
    public Pixels All(uint c) => Fill(0, 0, W - 1, H - 1, c);
    public Pixels Checker(int cell, uint a, uint b) { for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) Set(x, y, ((x / cell) + (y / cell)) % 2 == 0 ? a : b); return this; }
    public Pixels Disc(float cx, float cy, float r, uint c) { for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { float dx = x + 0.5f - cx, dy = y + 0.5f - cy; if (dx * dx + dy * dy <= r * r) Set(x, y, c); } return this; }

    public Pixels Star(float cx, float cy, float ro, float ri, uint c)
    {
        var xs = new float[10]; var ys = new float[10];
        for (int i = 0; i < 10; i++) { double a = -Math.PI / 2 + i * Math.PI / 5; float r = i % 2 == 0 ? ro : ri; xs[i] = cx + (float)Math.Cos(a) * r; ys[i] = cy + (float)Math.Sin(a) * r; }
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float px = x + 0.5f, py = y + 0.5f; bool inside = false;
                for (int i = 0, j = 9; i < 10; j = i++)
                    if ((ys[i] > py) != (ys[j] > py) && px < (xs[j] - xs[i]) * (py - ys[i]) / (ys[j] - ys[i]) + xs[i]) inside = !inside;
                if (inside) Set(x, y, c);
            }
        return this;
    }

    /// <summary>A small "S" mark (synthetic) in the bottom-right corner so test screenshots show where art came from.</summary>
    public Pixels Mark(uint c)
    {
        int s = Math.Max(1, W / 16), x0 = W - 4 * s - 1, y0 = H - 6 * s - 1;
        int[,] glyph = { { 1, 1, 1 }, { 1, 0, 0 }, { 1, 1, 1 }, { 0, 0, 1 }, { 1, 1, 1 } };
        for (int gy = 0; gy < 5; gy++) for (int gx = 0; gx < 3; gx++) if (glyph[gy, gx] == 1) Fill(x0 + gx * s, y0 + gy * s, x0 + gx * s + s - 1, y0 + gy * s + s - 1, c);
        return this;
    }

    public Rgba ToRgba() => new(W, H, P);
}
