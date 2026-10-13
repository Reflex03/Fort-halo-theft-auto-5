using System.Drawing;
using CthExtract.Core;
using Reclaimer.Drawing;
using Reclaimer.Geometry;

namespace CthExtract.Halo;

/// <summary>Material → RGBA diffuse image for a .cthm material.</summary>
public static class TextureExport
{
    public const int MaxSide = 8192;

    /// <summary>
    /// The material's diffuse map as RGBA. Opaque materials (and materials with no blend mode) get alpha 255: Halo stores
    /// a specular mask in the diffuse alpha. Alpha-tested ("clip") and blended materials keep their alpha.
    /// Without a readable diffuse map: a 4x4 swatch of the material's albedo tint, else mid grey (noted).
    /// </summary>
    public static Rgba Diffuse(Material? material, List<string> notes)
    {
        var mapping = MeshBuilder.Diffuse(material);
        if (mapping?.Texture != null)
        {
            try
            {
                var dds = mapping.Texture.GetDds();
                if (dds != null)
                {
                    var rgba = FromBgra(dds.AsUncompressed(), out var why);
                    if (rgba != null)
                    {
                        bool keepAlpha = material?.AlphaMode is AlphaMode.Clip or AlphaMode.Blend or AlphaMode.PreMultiplied;
                        return keepAlpha ? rgba : Images.Opaque(rgba);
                    }
                    notes.Add($"texture '{mapping.Texture.Name}': {why}");
                }
                else notes.Add($"texture '{mapping.Texture.Name}': no image");
            }
            catch (Exception e)
            {
                notes.Add($"texture '{mapping.Texture.Name}' failed to decode: {e.GetType().Name}: {e.Message}");
            }
        }
        else notes.Add($"material '{material?.Name ?? "<none>"}' has no diffuse map");

        var tint = material?.Tints.FirstOrDefault(t => t.Usage == TintUsage.Albedo)?.Color;
        return Solid(tint is { } c && (c.R | c.G | c.B) != 0 ? c : Color.FromArgb(255, 128, 128, 128));
    }

    /// <summary>B8G8R8A8 DdsImage → RGBA8, validated.</summary>
    public static Rgba? FromBgra(DdsImage dds, out string? why)
    {
        why = null;
        int w = dds.Width, h = dds.Height;
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { why = $"bad size {w}x{h}"; return null; }
        var px = dds.CopyPixelData();
        long need = (long)w * h * 4;
        if (px.Length < need) { why = $"pixel data {px.Length} bytes < {need}"; return null; }
        return FromBgra(w, h, px);
    }

    public static Rgba FromBgra(int w, int h, byte[] bgra)
    {
        var o = new byte[w * h * 4];
        for (int i = 0; i < o.Length; i += 4)
        {
            o[i] = bgra[i + 2];
            o[i + 1] = bgra[i + 1];
            o[i + 2] = bgra[i];
            o[i + 3] = bgra[i + 3];
        }
        return new Rgba(w, h, o);
    }

    public static Rgba Solid(Color c, int size = 4)
    {
        var p = new byte[size * size * 4];
        for (int i = 0; i < p.Length; i += 4) { p[i] = c.R; p[i + 1] = c.G; p[i + 2] = c.B; p[i + 3] = 255; }
        return new Rgba(size, size, p);
    }
}
