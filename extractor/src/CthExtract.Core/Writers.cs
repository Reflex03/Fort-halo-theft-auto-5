using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace CthExtract.Core;

/// <summary>Plain text log (extract.log in the cache) plus console.</summary>
public sealed class Log : IDisposable
{
    private readonly StreamWriter? _w;
    private readonly object _lock = new();
    public Log(string? path)
    {
        if (path != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            _w = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }
    public void Info(string m) => Write("INFO", m);
    public void Warn(string m) => Write("WARN", m);
    public void Error(string m) => Write("ERROR", m);
    private void Write(string lvl, string m)
    {
        var line = $"{DateTime.UtcNow:O} {lvl} {m}";
        lock (_lock) { _w?.WriteLine(line); try { Console.WriteLine(line); } catch { } }
    }
    public void Dispose() => _w?.Dispose();
}

/// <summary>.cthm v1 mesh file (little-endian). See docs/CONTRACTS.md.</summary>
public static class Cthm
{
    public static readonly byte[] Magic = "CTHM"u8.ToArray();

    public static void Write(string path, MeshData m)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(1u);
            w.Write((uint)m.Nodes.Count);
            foreach (var n in m.Nodes) { Str(w, n.Name); w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
            w.Write((uint)m.Submeshes.Count);
            foreach (var s in m.Submeshes)
            {
                Str(w, s.Material);
                w.Write(s.Node);
                w.Write((uint)s.VertexCount);
                w.Write((uint)s.Indices.Count);
                foreach (var f in s.Vertices) w.Write(f);
                foreach (var i in s.Indices) w.Write(i);
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static MeshData Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("not a .cthm file");
        if (r.ReadUInt32() != 1) throw new InvalidDataException("unsupported .cthm version");
        var m = new MeshData();
        uint nn = r.ReadUInt32();
        for (int i = 0; i < nn; i++) m.Nodes.Add(new MeshData.Node { Name = RStr(r), X = r.ReadSingle(), Y = r.ReadSingle(), Z = r.ReadSingle() });
        uint ns = r.ReadUInt32();
        for (int i = 0; i < ns; i++)
        {
            var s = new MeshData.Submesh { Material = RStr(r), Node = r.ReadInt32() };
            uint vc = r.ReadUInt32(), ic = r.ReadUInt32();
            for (int k = 0; k < vc * 8; k++) s.Vertices.Add(r.ReadSingle());
            for (int k = 0; k < ic; k++) s.Indices.Add(r.ReadUInt32());
            m.Submeshes.Add(s);
        }
        return m;
    }

    private static void Str(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ushort)b.Length); w.Write(b); }
    private static string RStr(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
}

/// <summary>Minimal RGBA8 PNG writer (zlib via System.IO.Compression, CRC via System.IO.Hashing).</summary>
public static class Png
{
    public static void Write(string path, Rgba img)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encode(img));
    }

    public static byte[] Encode(Rgba img)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), img.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = img.Width * 4;
                for (int y = 0; y < img.Height; y++)
                {
                    z.WriteByte(0);
                    z.Write(img.Pixels, y * stride, stride);
                }
            }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var td = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(td, 0);
        data.CopyTo(td, 4);
        s.Write(td);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(td));
        s.Write(crc);
    }
}

public static class Images
{
    /// <summary>Box-downscale to at most maxSize and resize to power-of-two sides (Minecraft atlases/mipmaps like them).</summary>
    public static Rgba FitPowerOfTwo(Rgba src, int maxSize)
    {
        int tw = PowerOfTwoAtMost(Math.Min(src.Width, maxSize)), th = PowerOfTwoAtMost(Math.Min(src.Height, maxSize));
        if (tw == src.Width && th == src.Height) return src;
        return Resize(src, tw, th);
    }

    public static int PowerOfTwoAtMost(int v) { int p = 1; while (p * 2 <= v) p *= 2; return Math.Max(1, p); }

    public static Rgba Resize(Rgba s, int w, int h)
    {
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int y0 = y * s.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * s.Height / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * s.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * s.Width / w);
                long r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * s.Width + xx) * 4;
                        r += s.Pixels[i]; g += s.Pixels[i + 1]; b += s.Pixels[i + 2]; a += s.Pixels[i + 3]; n++;
                    }
                int oi = (y * w + x) * 4;
                o[oi] = (byte)(r / n); o[oi + 1] = (byte)(g / n); o[oi + 2] = (byte)(b / n); o[oi + 3] = (byte)(a / n);
            }
        }
        return new Rgba(w, h, o);
    }

    /// <summary>Set alpha to 255 everywhere (game diffuse alpha is often a specular mask, not transparency).</summary>
    public static Rgba Opaque(Rgba s)
    {
        var p = (byte[])s.Pixels.Clone();
        for (int i = 3; i < p.Length; i += 4) p[i] = 255;
        return s with { Pixels = p };
    }
}

public static class Ogg
{
    /// <summary>True when the bytes start with an Ogg page whose first packet is a Vorbis identification header.</summary>
    public static bool LooksLikeVorbis(byte[] b) =>
        b.Length > 64 && b[0] == (byte)'O' && b[1] == (byte)'g' && b[2] == (byte)'g' && b[3] == (byte)'S'
        && b.AsSpan(0, Math.Min(b.Length, 64)).IndexOf("\u0001vorbis"u8) >= 0;
}

public static class Validate
{
    /// <summary>Throw on NaN/inf positions or out-of-range indices so a bad parse never reaches the game.</summary>
    public static void Mesh(MeshData m)
    {
        foreach (var s in m.Submeshes)
        {
            foreach (var f in s.Vertices) if (!float.IsFinite(f)) throw new InvalidDataException($"non-finite vertex in {s.Material}");
            int vc = s.VertexCount;
            foreach (var i in s.Indices) if (i >= vc) throw new InvalidDataException($"index {i} >= {vc} in {s.Material}");
            if (s.Indices.Count % 3 != 0) throw new InvalidDataException("index count not a multiple of 3");
            if (s.Node >= m.Nodes.Count) throw new InvalidDataException("submesh node out of range");
        }
    }
}
