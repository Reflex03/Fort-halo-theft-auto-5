using System.Text;
using CthExtract.Core;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Resources;
using CthExtract.Gta.Rpf;
using CthExtract.Gta.Tests.Synth;
using Xunit;

namespace CthExtract.Gta.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cth-gta-" + Guid.NewGuid().ToString("N")[..10]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name, byte[] data)
    {
        var p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class RpfTests
{
    private static readonly byte[] Text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello rpf7 world ", 200)));

    [Theory]
    [InlineData(RpfEnc.Open)]
    [InlineData(RpfEnc.Aes)]
    [InlineData(RpfEnc.Ng)]
    public void Archive_roundtrip_with_nested_ng_archive(RpfEnc enc)
    {
        var keys = new SynthKeys(21);
        var nested = new RpfWriter(RpfEnc.Ng, keys)
            .AddBinary("deep/inner.txt", Text, compress: true, encrypt: true)
            .AddResource("thing.ytd", TextureWriter.Ytd(new[] { new SynthTexture("t", TextureWriter.Pattern(8, 8, 1), TextureFormats.A8R8G8B8) }));
        var w = new RpfWriter(enc, keys)
            .AddBinary("a.txt", Text)
            .AddBinary("dir/b.bin", Text, compress: true)
            .AddBinary("dir/c.bin", Text, compress: true, encrypt: enc != RpfEnc.Open)
            .AddNested("levels/gta5/vehicles.rpf", nested);
        using var tmp = new TempDir();
        var path = tmp.File("x64e.rpf", w.Build("x64e.rpf"));
        using var f = new RpfFile(path);
        var gk = new GtaKeys { Aes = keys.Aes, Ng = keys.Ng };
        var a = RpfArchive.Open(f, "x64e.rpf", gk);
        Assert.Equal(enc switch { RpfEnc.Open => "OPEN", RpfEnc.Aes => "AES", _ => "NG" }, a.EncryptionName);
        var files = a.Files.ToDictionary(e => e.Path);
        Assert.Equal(new[] { "a.txt", "dir/b.bin", "dir/c.bin", "levels/gta5/vehicles.rpf" }, files.Keys.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Text, a.ReadBinary(files["a.txt"]));
        Assert.Equal(Text, a.ReadBinary(files["dir/b.bin"]));
        Assert.Equal(Text, a.ReadBinary(files["dir/c.bin"]));
        var n = a.OpenNested(files["levels/gta5/vehicles.rpf"]);
        Assert.Equal("x64e.rpf/levels/gta5/vehicles.rpf", n.VirtualPath);
        var nf = n.Files.ToDictionary(e => e.Path);
        Assert.Equal(Text, n.ReadBinary(nf["deep/inner.txt"]));
        var ytd = nf["thing.ytd"];
        Assert.Equal(RpfEntryKind.Resource, ytd.Kind);
        Assert.Equal(13, ytd.ResourceVersion);
        var res = Resource.FromRaw(n.ReadResourceRaw(ytd), ytd.FullPath, ytd.SysFlags, ytd.GfxFlags);
        Assert.Single(TextureDictionaryReader.Read(res, 0));
    }

    [Fact]
    public void Missing_keys_fail_cleanly_with_a_reason()
    {
        var keys = new SynthKeys(22);
        using var tmp = new TempDir();
        var ng = tmp.File("x64e.rpf", new RpfWriter(RpfEnc.Ng, keys).AddBinary("a.txt", Text).Build("x64e.rpf"));
        var aes = tmp.File("update.rpf", new RpfWriter(RpfEnc.Aes, keys).AddBinary("a.txt", Text).Build("update.rpf"));
        using var f1 = new RpfFile(ng);
        using var f2 = new RpfFile(aes);
        var e1 = Assert.Throws<KeyUnavailableException>(() => RpfArchive.Open(f1, "x64e.rpf", new GtaKeys { Aes = keys.Aes, NgProblem = "fetch failed" }));
        Assert.Contains("NG-encrypted", e1.Message);
        Assert.Contains("fetch failed", e1.Message);
        Assert.Throws<KeyUnavailableException>(() => RpfArchive.Open(f2, "update/update.rpf", GtaKeys.None));
        // a wrong key is detected by the table-of-contents sanity check
        var other = new SynthKeys(23);
        Assert.Throws<InvalidDataException>(() => RpfArchive.Open(f1, "x64e.rpf", new GtaKeys { Aes = other.Aes, Ng = other.Ng }));
    }

    [Fact]
    public void Header_peek_needs_no_keys()
    {
        var keys = new SynthKeys(24);
        using var tmp = new TempDir();
        var p = tmp.File("x.rpf", new RpfWriter(RpfEnc.Ng, keys).AddBinary("a", Text).Build("x.rpf"));
        using var f = new RpfFile(p);
        var h = RpfArchive.PeekHeader(f);
        Assert.NotNull(h);
        Assert.Equal(RpfArchive.EncNg, h!.Value.encryption);
        var junk = tmp.File("junk.rpf", new byte[64]);
        using var fj = new RpfFile(junk);
        Assert.Null(RpfArchive.PeekHeader(fj));
    }

    [Fact]
    public void Huge_resource_size_marker_is_resolved_from_the_data_header()
    {
        var keys = new SynthKeys(25);
        var ytd = TextureWriter.Ytd(new[] { new SynthTexture("big", TextureWriter.Pattern(64, 64, 3), TextureFormats.DXT1) });
        using var tmp = new TempDir();
        var p = tmp.File("v.rpf", new RpfWriter(RpfEnc.Aes, keys).AddResource("big.ytd", ytd, hugeSizeMarker: true).Build("v.rpf"));
        using var f = new RpfFile(p);
        var a = RpfArchive.Open(f, "v.rpf", new GtaKeys { Aes = keys.Aes });
        var e = a.Files.Single();
        Assert.Equal((uint)ytd.Length, e.StoredSize);
        var r = Resource.FromRaw(a.ReadResourceRaw(e), e.FullPath, e.SysFlags, e.GfxFlags); // header magic is gone; flags come from the entry
        Assert.Equal(13, r.Version);
        Assert.Equal("big", TextureDictionaryReader.Read(r, 0).Single().Name);
    }

    [Fact]
    public void Files_are_opened_shared_so_a_running_game_or_launcher_does_not_block_us()
    {
        var keys = new SynthKeys(26);
        using var tmp = new TempDir();
        var p = tmp.File("x.rpf", new RpfWriter(RpfEnc.Open, keys).AddBinary("a", Text).Build("x.rpf"));
        using var writer = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var f = new RpfFile(p);
        Assert.Single(RpfArchive.Open(f, "x.rpf", GtaKeys.None).Files);
    }
}

public class ResourceTests
{
    [Theory]
    [InlineData(0x0)]
    [InlineData(0x10)]
    [InlineData(0x2000)]
    [InlineData(0x2001)]
    [InlineData(0x123456)]
    [InlineData(0x4000000)]
    public void Page_flags_cover_the_size(long size)
    {
        uint f = ResourceBuilder.FlagsFor(size, 0xA);
        long s = Resource.SizeFromFlags(f);
        Assert.True(s >= size);
        Assert.True(size == 0 || s < size * 2 + 0x4000);
        Assert.Equal(0xA, (int)(f >> 28));
    }

    [Fact]
    public void Page_flags_decode_all_fields_like_gta_toolkit()
    {
        // shift 1 (base 0x4000): div16+div8+div4+div2 (1 each) + mul1 3 + mul2 2 + mul4 1 + mul8 1 + mul16 1
        uint f = (1u << 27) | (1u << 26) | (1u << 25) | (1u << 24) | (3u << 17) | (2u << 11) | (1u << 7) | (1u << 5) | (1u << 4) | 1u;
        long b = 0x4000;
        long expected = b / 16 + b / 8 + b / 4 + b / 2 + b * (3 + 2 * 2 + 4 + 8 + 16);
        Assert.Equal(expected, Resource.SizeFromFlags(f));
    }

    [Fact]
    public void Pointers_resolve_into_system_and_graphics_and_bad_ones_throw()
    {
        var rb = new ResourceBuilder();
        var root = rb.Alloc(16);
        var g = rb.Add(new byte[] { 1, 2, 3, 4 }, graphics: true);
        rb.Put(root, new Blk(16).U64(0, g.Ptr).U64(8, 0x50000000UL + 0xFFFFFF).B);
        var r = Resource.FromRaw(rb.Build(13), "test");
        long o = r.Ptr(0);
        Assert.Equal(r.SystemSize, o);
        Assert.Equal(1, r.U8(o));
        Assert.Throws<InvalidDataException>(() => r.Ptr(8));
        Assert.Throws<InvalidDataException>(() => r.U32(r.Data.Length - 2));
        Assert.Equal(-1, r.Off(0));
    }

    [Fact]
    public void Truncated_resource_fails_with_invalid_data()
    {
        var bytes = TextureWriter.Ytd(new[] { new SynthTexture("t", TextureWriter.Pattern(64, 64, 1), TextureFormats.A8R8G8B8) });
        Assert.Throws<InvalidDataException>(() => Resource.FromRaw(bytes.AsSpan(0, 40).ToArray(), "cut"));
        Assert.Throws<InvalidDataException>(() => Resource.FromRaw(new byte[8], "tiny"));
    }
}

public class TextureTests
{
    [Theory]
    [InlineData(TextureFormats.DXT1, 30)]
    [InlineData(TextureFormats.DXT3, 30)]
    [InlineData(TextureFormats.DXT5, 30)]
    [InlineData(TextureFormats.BC7, 35)]
    [InlineData(TextureFormats.A8R8G8B8, 99)]
    [InlineData(TextureFormats.X8R8G8B8, 99)]
    [InlineData(TextureFormats.A8B8G8R8, 99)]
    [InlineData(TextureFormats.A1R5G5B5, 30)]
    public void Ytd_texture_decodes_close_to_source(uint format, double minPsnr)
    {
        var src = TextureWriter.Pattern(64, 32, 4);
        var ytd = TextureWriter.Ytd(new[] { new SynthTexture("tex_a", src, format), new SynthTexture("tex_b", TextureWriter.Pattern(16, 16, 5), format) });
        var r = Resource.FromRaw(ytd, "synthetic.ytd");
        Assert.Equal(TextureDictionaryReader.Gen8YtdVersion, r.Version);
        var list = TextureDictionaryReader.Read(r, 0);
        Assert.Equal(new[] { "tex_a", "tex_b" }, list.Select(t => t.Name));
        var img = TextureDecoder.Decode(list[0]);
        Assert.Equal((64, 32), (img.Width, img.Height));
        Assert.True(TextureWriter.Psnr(src, img) >= minPsnr, $"PSNR {TextureWriter.Psnr(src, img):F1} dB for {TextureFormats.Name(format)}");
    }

    [Fact]
    public void Single_channel_formats_decode()
    {
        var src = TextureWriter.Pattern(32, 32, 6);
        foreach (var fmt in new[] { TextureFormats.L8, TextureFormats.ATI1 })
        {
            var t = TextureDictionaryReader.Read(Resource.FromRaw(TextureWriter.Ytd(new[] { new SynthTexture("l", src, fmt) }), "l.ytd"), 0).Single();
            var img = TextureDecoder.Decode(t);
            for (int i = 0; i < img.Pixels.Length; i += 4)
            {
                Assert.Equal(img.Pixels[i], img.Pixels[i + 1]);
                Assert.True(Math.Abs(img.Pixels[i] - src.Pixels[i]) <= 12, $"{TextureFormats.Name(fmt)} R {img.Pixels[i]} vs {src.Pixels[i]}");
            }
        }
        var ati2 = TextureDictionaryReader.Read(Resource.FromRaw(TextureWriter.Ytd(new[] { new SynthTexture("n", src, TextureFormats.ATI2) }), "n.ytd"), 0).Single();
        Assert.Equal(32 * 32 * 4, TextureDecoder.Decode(ati2).Pixels.Length);
    }

    [Fact]
    public void Unsupported_format_is_skipped_with_a_reason()
    {
        var rb = new ResourceBuilder();
        var root = rb.Alloc(0x40);
        TextureWriter.WriteDictionary(rb, root, new[] { new SynthTexture("ok", TextureWriter.Pattern(8, 8, 1), TextureFormats.A8R8G8B8) });
        var r = Resource.FromRaw(rb.Build(13), "x.ytd");
        // patch the format of the only texture to an unknown value
        long tex = r.Ptr(r.List(0x30).array);
        BitConverter.GetBytes(0xDEADu).CopyTo(r.Data, (int)tex + 0x58);
        var problems = new List<string>();
        Assert.Empty(TextureDictionaryReader.Read(r, 0, problems));
        Assert.Contains(problems, p => p.Contains("unsupported format"));
    }
}
