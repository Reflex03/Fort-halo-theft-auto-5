using System.Runtime.InteropServices;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using CthExtract.Core;

namespace CthExtract.Gta.Resources;

/// <summary>One texture of a texture dictionary: header fields + the bytes of mip level 0.</summary>
public sealed class TextureEntry
{
    public required string Name { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Levels { get; init; }
    public int Stride { get; init; }
    public uint Format { get; init; }
    public required byte[] Mip0 { get; init; }
    public override string ToString() => $"{Name} {Width}x{Height} {TextureFormats.Name(Format)}";
}

/// <summary>Legacy (gen8) PC texture formats: D3DFMT values / FourCC codes.</summary>
public static class TextureFormats
{
    public const uint DXT1 = 0x31545844, DXT3 = 0x33545844, DXT5 = 0x35545844, ATI1 = 0x31495441, ATI2 = 0x32495441, BC7 = 0x20374342;
    public const uint A8R8G8B8 = 21, X8R8G8B8 = 22, A1R5G5B5 = 25, A8 = 28, A8B8G8R8 = 32, L8 = 50;

    public static string Name(uint f) => f switch
    {
        DXT1 => "DXT1", DXT3 => "DXT3", DXT5 => "DXT5", ATI1 => "ATI1", ATI2 => "ATI2", BC7 => "BC7",
        A8R8G8B8 => "A8R8G8B8", X8R8G8B8 => "X8R8G8B8", A1R5G5B5 => "A1R5G5B5", A8 => "A8", A8B8G8R8 => "A8B8G8R8", L8 => "L8",
        _ => $"0x{f:X}",
    };

    /// <summary>Bytes per 4x4 block for block-compressed formats, 0 otherwise.</summary>
    public static int BlockBytes(uint f) => f switch
    {
        DXT1 or ATI1 => 8,
        DXT3 or DXT5 or ATI2 or BC7 => 16,
        _ => 0,
    };

    public static int BytesPerPixel(uint f) => f switch
    {
        A8R8G8B8 or X8R8G8B8 or A8B8G8R8 => 4,
        A1R5G5B5 => 2,
        A8 or L8 => 1,
        _ => 0,
    };

    public static bool Supported(uint f) => BlockBytes(f) > 0 || BytesPerPixel(f) > 0;

    public static long Mip0Size(uint f, int w, int h, int stride)
    {
        int bb = BlockBytes(f);
        if (bb > 0) return (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * bb;
        int bpp = BytesPerPixel(f);
        if (bpp == 0) return 0;
        int pitch = stride >= w * bpp ? stride : w * bpp;
        return (long)pitch * h;
    }
}

/// <summary>Reads a texture dictionary (YTD root block, or a drawable's embedded dictionary).</summary>
public static class TextureDictionaryReader
{
    public const int Gen8YtdVersion = 13;

    /// <summary>Textures of the dictionary block at offset dict (0 for a .ytd root). Unsupported entries are skipped
    /// with a reason in <paramref name="problems"/>.</summary>
    public static List<TextureEntry> Read(Resource r, long dict, List<string>? problems = null)
    {
        var result = new List<TextureEntry>();
        var (arr, count) = r.List(dict + 0x30);
        if (count > 4096) throw new InvalidDataException($"{r.What}: implausible texture count {count}");
        for (int i = 0; i < count; i++)
        {
            long t = r.Ptr(arr + i * 8);
            if (t < 0) continue;
            var name = r.CString(r.U64(t + 0x28)) ?? $"#{i}";
            int w = r.U16(t + 0x50), h = r.U16(t + 0x52), stride = r.U16(t + 0x56), levels = r.U8(t + 0x5D);
            uint fmt = r.U32(t + 0x58);
            if (w == 0 || h == 0 || w > 16384 || h > 16384) { problems?.Add($"{name}: bad size {w}x{h}"); continue; }
            if (!TextureFormats.Supported(fmt)) { problems?.Add($"{name}: unsupported format {TextureFormats.Name(fmt)}"); continue; }
            long len = TextureFormats.Mip0Size(fmt, w, h, stride);
            long data = r.Ptr(t + 0x70);
            if (data < 0 || data + len > r.Data.Length) { problems?.Add($"{name}: texture data out of range"); continue; }
            result.Add(new TextureEntry
            {
                Name = name, Width = w, Height = h, Stride = stride, Levels = levels, Format = fmt,
                Mip0 = r.Data.AsSpan((int)data, (int)len).ToArray(),
            });
        }
        return result;
    }
}

/// <summary>Decodes mip 0 to RGBA8 (BCn through BCnEncoder.Net, MIT; uncompressed formats by hand).</summary>
public static class TextureDecoder
{
    [ThreadStatic] private static BcDecoder? _dec;

    public static Rgba Decode(TextureEntry t)
    {
        int w = t.Width, h = t.Height;
        var px = new byte[w * h * 4];
        CompressionFormat? bc = t.Format switch
        {
            TextureFormats.DXT1 => CompressionFormat.Bc1WithAlpha,
            TextureFormats.DXT3 => CompressionFormat.Bc2,
            TextureFormats.DXT5 => CompressionFormat.Bc3,
            TextureFormats.ATI1 => CompressionFormat.Bc4,
            TextureFormats.ATI2 => CompressionFormat.Bc5,
            TextureFormats.BC7 => CompressionFormat.Bc7,
            _ => null,
        };
        if (bc is not null)
        {
            _dec ??= new BcDecoder();
            var colors = _dec.DecodeRaw(t.Mip0, w, h, bc.Value);
            MemoryMarshal.AsBytes(colors.AsSpan()).Slice(0, px.Length).CopyTo(px);
            if (t.Format == TextureFormats.ATI1)
                for (int i = 0; i < px.Length; i += 4) { px[i + 1] = px[i]; px[i + 2] = px[i]; px[i + 3] = 255; }
            if (t.Format == TextureFormats.ATI2)
                for (int i = 0; i < px.Length; i += 4)
                {
                    float x = px[i] / 127.5f - 1, y = px[i + 1] / 127.5f - 1;
                    px[i + 2] = (byte)Math.Clamp((MathF.Sqrt(MathF.Max(0, 1 - x * x - y * y)) * 0.5f + 0.5f) * 255, 0, 255);
                    px[i + 3] = 255;
                }
            return new Rgba(w, h, px);
        }
        int bpp = TextureFormats.BytesPerPixel(t.Format);
        if (bpp == 0) throw new NotSupportedException($"texture format {TextureFormats.Name(t.Format)}");
        int pitch = t.Stride >= w * bpp ? t.Stride : w * bpp;
        var s = t.Mip0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * pitch + x * bpp, o = (y * w + x) * 4;
                switch (t.Format)
                {
                    case TextureFormats.A8R8G8B8: // memory B G R A
                        px[o] = s[i + 2]; px[o + 1] = s[i + 1]; px[o + 2] = s[i]; px[o + 3] = s[i + 3]; break;
                    case TextureFormats.X8R8G8B8:
                        px[o] = s[i + 2]; px[o + 1] = s[i + 1]; px[o + 2] = s[i]; px[o + 3] = 255; break;
                    case TextureFormats.A8B8G8R8: // memory R G B A
                        px[o] = s[i]; px[o + 1] = s[i + 1]; px[o + 2] = s[i + 2]; px[o + 3] = s[i + 3]; break;
                    case TextureFormats.A1R5G5B5:
                    {
                        int v = s[i] | (s[i + 1] << 8);
                        px[o] = (byte)(((v >> 10) & 31) * 255 / 31); px[o + 1] = (byte)(((v >> 5) & 31) * 255 / 31);
                        px[o + 2] = (byte)((v & 31) * 255 / 31); px[o + 3] = (byte)((v >> 15) != 0 ? 255 : 0); break;
                    }
                    case TextureFormats.A8:
                        px[o] = px[o + 1] = px[o + 2] = 255; px[o + 3] = s[i]; break;
                    case TextureFormats.L8:
                        px[o] = px[o + 1] = px[o + 2] = s[i]; px[o + 3] = 255; break;
                }
            }
        return new Rgba(w, h, px);
    }
}
