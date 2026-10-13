using Reclaimer.Geometry;
using Xunit;

namespace CthExtract.Halo.Tests;

public class TextureTests
{
    [Fact]
    public void OpaqueMaterialsLoseTheirSpecularMaskAlpha()
    {
        var notes = new List<string>();
        var mat = SceneFactory.MakeMaterial(1, "body", AlphaMode.Opaque, SceneFactory.Bgra.Solid(8, 4, 10, 20, 30, 0));
        var img = TextureExport.Diffuse(mat, notes);
        Assert.Equal((8, 4), (img.Width, img.Height));
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, img.Pixels[..4]); // BGRA -> RGBA, alpha forced
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData(AlphaMode.Clip)]
    [InlineData(AlphaMode.Blend)]
    public void CutoutAndBlendedMaterialsKeepAlpha(string mode)
    {
        var img = TextureExport.Diffuse(SceneFactory.MakeMaterial(1, "glass", mode, SceneFactory.Bgra.Solid(4, 4, 1, 2, 3, 77)), new());
        Assert.Equal(77, img.Pixels[3]);
    }

    [Fact]
    public void CompressedBitmapsAreDecodedByReclaimer()
    {
        // BC1 block with colour0 = pure red (RGB565 0xF800), all indices 0
        var img = TextureExport.Diffuse(SceneFactory.MakeMaterial(1, "bc1", AlphaMode.Opaque, SceneFactory.Bc1.Solid(8, 8, 0xF800)), new());
        Assert.Equal((8, 8), (img.Width, img.Height));
        for (int i = 0; i < img.Pixels.Length; i += 4)
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, img.Pixels[i..(i + 4)]);
    }

    [Fact]
    public void MissingOrBrokenDiffuseGivesAFlatSwatchAndANote()
    {
        var notes = new List<string>();
        var none = TextureExport.Diffuse(SceneFactory.MakeMaterial(1, "plain", AlphaMode.Opaque, null), notes);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, none.Pixels[..4]);
        var tinted = SceneFactory.MakeMaterial(2, "tinted", AlphaMode.Opaque, null);
        tinted.Tints.Add(new MaterialTint { Usage = TintUsage.Albedo, Color = System.Drawing.Color.FromArgb(255, 200, 100, 50) });
        Assert.Equal(new byte[] { 200, 100, 50, 255 }, TextureExport.Diffuse(tinted, notes).Pixels[..4]);
        var broken = TextureExport.Diffuse(SceneFactory.MakeMaterial(3, "broken", AlphaMode.Opaque, new SceneFactory.Broken()), notes);
        Assert.Equal(4, broken.Width);
        Assert.Contains(notes, n => n.Contains("failed to decode"));
        Assert.Contains(notes, n => n.Contains("no diffuse map"));
        Assert.Equal(128, TextureExport.Diffuse(null, notes).Pixels[0]);
    }
}
