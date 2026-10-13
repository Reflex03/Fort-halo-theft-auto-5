using CthExtract.Core;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

namespace CthExtract.Fortnite.Conversion;

/// <summary>UTexture2D -> RGBA8 through CUE4Parse's managed decoders (AssetRipper.TextureDecoder for BCn).</summary>
public static class TextureConvert
{
    /// <summary>Decode the best locally available mip not larger than <paramref name="maxSize"/>.</summary>
    public static Rgba Decode(UTexture texture, int maxSize, out string detail)
    {
        TextureDecoder.UseAssetRipperTextureDecoder = true;
        var ct = texture.Decode(maxSize) ?? throw new InvalidDataException("texture has no decodable mip (streamed mips not installed?)");
        var img = ToRgba(ct);
        detail = $"{texture.Name} {texture.Format} -> {ct.PixelFormat} {ct.Width}x{ct.Height}";
        return img;
    }

    public static Rgba ToRgba(CTexture t)
    {
        int w = t.Width, h = t.Height, n = w * h;
        if (w <= 0 || h <= 0) throw new InvalidDataException($"texture size {w}x{h}");
        var d = t.Data;
        var o = new byte[n * 4];
        switch (t.PixelFormat)
        {
            case EPixelFormat.PF_R8G8B8A8:
                Need(d, n * 4);
                Buffer.BlockCopy(d, 0, o, 0, n * 4);
                break;
            case EPixelFormat.PF_B8G8R8A8:
                Need(d, n * 4);
                for (int i = 0; i < n; i++) { o[i * 4] = d[i * 4 + 2]; o[i * 4 + 1] = d[i * 4 + 1]; o[i * 4 + 2] = d[i * 4]; o[i * 4 + 3] = d[i * 4 + 3]; }
                break;
            case EPixelFormat.PF_G8:
                Need(d, n);
                for (int i = 0; i < n; i++) { o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = d[i]; o[i * 4 + 3] = 255; }
                break;
            case EPixelFormat.PF_G16:
                Need(d, n * 2);
                for (int i = 0; i < n; i++) { byte v = d[i * 2 + 1]; o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = v; o[i * 4 + 3] = 255; }
                break;
            case EPixelFormat.PF_FloatRGBA:
                Need(d, n * 8);
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < 4; c++)
                    {
                        float f = (float)BitConverter.ToHalf(d, i * 8 + c * 2);
                        o[i * 4 + c] = c == 3 ? Unorm(f) : LinearToSrgb(f);
                    }
                break;
            case EPixelFormat.PF_A32B32G32R32F:
                Need(d, n * 16);
                for (int i = 0; i < n; i++)
                    for (int c = 0; c < 4; c++)
                    {
                        float f = BitConverter.ToSingle(d, i * 16 + c * 4);
                        o[i * 4 + c] = c == 3 ? Unorm(f) : LinearToSrgb(f);
                    }
                break;
            case EPixelFormat.PF_R16F:
                Need(d, n * 2);
                for (int i = 0; i < n; i++) { var v = LinearToSrgb((float)BitConverter.ToHalf(d, i * 2)); o[i * 4] = o[i * 4 + 1] = o[i * 4 + 2] = v; o[i * 4 + 3] = 255; }
                break;
            default:
                throw new NotSupportedException($"decoded pixel format {t.PixelFormat} is not converted");
        }
        return new Rgba(w, h, o);
    }

    private static void Need(byte[] d, int bytes)
    {
        if (d.Length < bytes) throw new InvalidDataException($"texture data has {d.Length} bytes, expected {bytes}");
    }

    private static byte Unorm(float f) => (byte)Math.Clamp((int)MathF.Round((float.IsFinite(f) ? f : 0) * 255f), 0, 255);

    private static byte LinearToSrgb(float f)
    {
        if (!float.IsFinite(f) || f <= 0) return 0;
        if (f >= 1) return 255;
        float s = f <= 0.0031308f ? f * 12.92f : 1.055f * MathF.Pow(f, 1 / 2.4f) - 0.055f;
        return Unorm(s);
    }

    /// <summary>Basic checks that a decoded image is a real picture: not empty, not one flat colour, not invisible.</summary>
    public static bool LooksLikeImage(Rgba img, out string why)
    {
        if (img.Width < 4 || img.Height < 4) { why = $"{img.Width}x{img.Height} is too small"; return false; }
        var p = img.Pixels;
        int visible = 0;
        int r0 = p[0], g0 = p[1], b0 = p[2], a0 = p[3];
        bool varied = false;
        for (int i = 0; i < p.Length; i += 4)
        {
            if (p[i + 3] > 16) visible++;
            if (!varied && (Math.Abs(p[i] - r0) > 3 || Math.Abs(p[i + 1] - g0) > 3 || Math.Abs(p[i + 2] - b0) > 3 || Math.Abs(p[i + 3] - a0) > 3)) varied = true;
        }
        if (visible < p.Length / 4 / 100) { why = "fully transparent"; return false; }
        if (!varied) { why = "a single flat colour"; return false; }
        why = "";
        return true;
    }

    /// <summary>A small flat-colour texture for materials whose diffuse could not be read.</summary>
    public static Rgba Flat(byte r, byte g, byte b)
    {
        var px = new byte[4 * 4 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = 255; }
        return new Rgba(4, 4, px);
    }
}
