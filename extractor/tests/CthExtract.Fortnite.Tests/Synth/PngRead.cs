using System.Buffers.Binary;
using System.IO.Compression;
using CthExtract.Core;

namespace CthExtract.Fortnite.Tests.Synth;

/// <summary>Minimal PNG reader (8-bit grey/grey+alpha/RGB/RGBA/palette, non-interlaced) for comparing outputs.</summary>
public static class PngRead
{
    public static Rgba Read(byte[] png)
    {
        if (png.Length < 8 || png[0] != 0x89 || png[1] != (byte)'P') throw new InvalidDataException("not a PNG");
        int pos = 8, w = 0, h = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, trns = null;
        var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var t = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var body = png.AsSpan(pos + 8, len);
            switch (t)
            {
                case "IHDR":
                    w = BinaryPrimitives.ReadInt32BigEndian(body); h = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8]; type = body[9]; interlace = body[12];
                    break;
                case "PLTE": palette = body.ToArray(); break;
                case "tRNS": trns = body.ToArray(); break;
                case "IDAT": idat.Write(body); break;
            }
            pos += 12 + len;
            if (t == "IEND") break;
        }
        if (depth != 8 || interlace != 0) throw new NotSupportedException($"PNG depth {depth} interlace {interlace}");
        int bpp = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG type {type}") };
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw);
        var d = raw.ToArray();
        int stride = w * bpp;
        var img = new byte[h * stride];
        var prev = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int f = d[y * (stride + 1)];
            var line = d.AsSpan(y * (stride + 1) + 1, stride).ToArray();
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? line[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0;
                int pred = f switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"filter {f}"),
                };
                line[x] = (byte)(line[x] + pred);
            }
            line.CopyTo(img, y * stride);
            prev = line;
        }
        var o = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            switch (type)
            {
                case 0: o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = img[i]; o[i * 4 + 3] = 255; break;
                case 4: o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = img[i * 2]; o[i * 4 + 3] = img[i * 2 + 1]; break;
                case 2: o[i * 4] = img[i * 3]; o[i * 4 + 1] = img[i * 3 + 1]; o[i * 4 + 2] = img[i * 3 + 2]; o[i * 4 + 3] = 255; break;
                case 6: Array.Copy(img, i * 4, o, i * 4, 4); break;
                case 3:
                    int k = img[i];
                    o[i * 4] = palette![k * 3]; o[i * 4 + 1] = palette[k * 3 + 1]; o[i * 4 + 2] = palette[k * 3 + 2];
                    o[i * 4 + 3] = trns != null && k < trns.Length ? trns[k] : (byte)255;
                    break;
            }
        }
        return new Rgba(w, h, o);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
