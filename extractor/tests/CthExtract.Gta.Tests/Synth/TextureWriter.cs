using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CthExtract.Core;
using CthExtract.Gta.Resources;

namespace CthExtract.Gta.Tests.Synth;

/// <summary>A source image plus the format to store it in.</summary>
public sealed record SynthTexture(string Name, Rgba Image, uint Format);

/// <summary>Writes gen8 texture dictionaries (YTD root blocks and embedded dictionaries) with our ResourceBuilder.
/// Block-compressed data is produced with BCnEncoder.Net's encoder (MIT).</summary>
public static class TextureWriter
{
    public static Rgba Pattern(int w, int h, int seed, bool alpha = false)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 255 / Math.Max(1, w - 1));
                px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1));
                px[i + 2] = (byte)((seed * 53 + ((x / 8 + y / 8) % 2) * 120) & 0xFF);
                px[i + 3] = alpha ? (byte)(((x / 4 + y / 4) % 2) == 0 ? 255 : 0) : (byte)255;
            }
        return new Rgba(w, h, px);
    }

    public static Rgba Solid(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
        return new Rgba(w, h, px);
    }

    public static (byte[] data, int stride) Encode(SynthTexture t)
    {
        var img = t.Image;
        CompressionFormat? bc = t.Format switch
        {
            TextureFormats.DXT1 => CompressionFormat.Bc1,
            TextureFormats.DXT3 => CompressionFormat.Bc2,
            TextureFormats.DXT5 => CompressionFormat.Bc3,
            TextureFormats.ATI1 => CompressionFormat.Bc4,
            TextureFormats.ATI2 => CompressionFormat.Bc5,
            TextureFormats.BC7 => CompressionFormat.Bc7,
            _ => null,
        };
        if (bc is not null)
        {
            var enc = new BcEncoder(bc.Value);
            enc.OutputOptions.GenerateMipMaps = false;
            enc.OutputOptions.Quality = CompressionQuality.Fast;
            var data = enc.EncodeToRawBytes(img.Pixels, img.Width, img.Height, PixelFormat.Rgba32, 0, out _, out _);
            int blockBytes = TextureFormats.BlockBytes(t.Format);
            return (data, Math.Max(1, (img.Width + 3) / 4) * blockBytes / 4 * 1); // bytes per pixel row (blocks/4 rows)
        }
        int bpp = TextureFormats.BytesPerPixel(t.Format);
        var o = new byte[img.Width * img.Height * bpp];
        for (int i = 0, p = 0; p < img.Pixels.Length; p += 4, i += bpp)
        {
            byte r = img.Pixels[p], g = img.Pixels[p + 1], b = img.Pixels[p + 2], a = img.Pixels[p + 3];
            switch (t.Format)
            {
                case TextureFormats.A8R8G8B8: o[i] = b; o[i + 1] = g; o[i + 2] = r; o[i + 3] = a; break;
                case TextureFormats.X8R8G8B8: o[i] = b; o[i + 1] = g; o[i + 2] = r; o[i + 3] = 0; break;
                case TextureFormats.A8B8G8R8: o[i] = r; o[i + 1] = g; o[i + 2] = b; o[i + 3] = a; break;
                case TextureFormats.L8: o[i] = r; break;
                case TextureFormats.A8: o[i] = a; break;
                case TextureFormats.A1R5G5B5:
                {
                    int v = (a >= 128 ? 0x8000 : 0) | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    o[i] = (byte)v; o[i + 1] = (byte)(v >> 8); break;
                }
                default: throw new NotSupportedException();
            }
        }
        return (o, img.Width * bpp);
    }

    /// <summary>Write a texture block (0x90) whose pixel data lives in the graphics segment.</summary>
    public static ResourceBuilder.Ref WriteTexture(ResourceBuilder rb, SynthTexture t)
    {
        var (data, stride) = Encode(t);
        var name = rb.String(t.Name);
        var pixels = rb.Add(data, graphics: true);
        var tex = new Blk(0x90)
            .U32(0, 0x1234).U32(4, 1)
            .U64(0x28, name.Ptr)
            .U16(0x50, t.Image.Width).U16(0x52, t.Image.Height).U16(0x54, 1).U16(0x56, stride)
            .U32(0x58, t.Format).U8(0x5D, 1)
            .U64(0x70, pixels.Ptr);
        return rb.Add(tex.B);
    }

    /// <summary>Write a dictionary block (0x40) at a reserved ref (the root for a .ytd).</summary>
    public static void WriteDictionary(ResourceBuilder rb, ResourceBuilder.Ref dict, IEnumerable<SynthTexture> textures)
    {
        var list = textures.ToList();
        var refs = list.Select(t => WriteTexture(rb, t)).ToList();
        var hashes = new Blk(Math.Max(4, list.Count * 4));
        for (int i = 0; i < list.Count; i++) hashes.U32(i * 4, CthExtract.Gta.Hashes.Joaat(list[i].Name));
        var hashRef = rb.Add(hashes.B);
        var ptrs = new Blk(Math.Max(8, list.Count * 8));
        for (int i = 0; i < refs.Count; i++) ptrs.U64(i * 8, refs[i].Ptr);
        var ptrRef = rb.Add(ptrs.B);
        var d = new Blk(0x40).U32(0, 0x5678).U32(0x18, 1)
            .U64(0x20, hashRef.Ptr).U16(0x28, list.Count).U16(0x2A, list.Count)
            .U64(0x30, ptrRef.Ptr).U16(0x38, list.Count).U16(0x3A, list.Count);
        rb.Put(dict, d.B);
    }

    public static byte[] Ytd(IEnumerable<SynthTexture> textures, int version = 13)
    {
        var rb = new ResourceBuilder();
        var root = rb.Alloc(0x40);
        WriteDictionary(rb, root, textures);
        return rb.Build(version);
    }

    public static double Psnr(Rgba a, Rgba b, int channels = 3)
    {
        double se = 0; long n = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            for (int c = 0; c < channels; c++) { double d = a.Pixels[i + c] - b.Pixels[i + c]; se += d * d; n++; }
        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));
    }
}
