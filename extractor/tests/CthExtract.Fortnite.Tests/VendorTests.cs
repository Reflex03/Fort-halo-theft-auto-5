using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using CthExtract.Fortnite.Unreal;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>
/// Guards on the vendored CUE4Parse copy and the module: nothing can download or unpack a native library
/// (Oodle, zlib-ng, unluac, Detex, ProSpi...), no stripped resource came back, and the module only talks to the key
/// services (see also KeyServiceTests' HttpGuard cases).
/// </summary>
public class VendorTests
{
    private static string? RepoExtractorDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "vendor", "CUE4Parse"))) d = d.Parent;
        return d?.FullName;
    }

    private static readonly Assembly[] Vendored =
    [
        typeof(CUE4Parse.Compression.Compression).Assembly,          // CUE4Parse
        typeof(CUE4Parse_Conversion.Textures.TextureDecoder).Assembly, // CUE4Parse-Conversion
        typeof(VgmstreamBinka.UebaReader).Assembly,
        typeof(FortniteModule).Assembly,
    ];

    private static IEnumerable<string> TypeRefs(Assembly a)
    {
        using var pe = new PEReader(File.OpenRead(a.Location));
        var md = pe.GetMetadataReader();
        foreach (var h in md.TypeReferences)
        {
            var t = md.GetTypeReference(h);
            yield return md.GetString(t.Namespace) + "." + md.GetString(t.Name);
        }
    }

    [Fact]
    public void Vendored_parser_has_no_network_code_at_all()
    {
        foreach (var a in Vendored.Take(3))
        {
            var net = TypeRefs(a).Where(n => n.StartsWith("System.Net.", StringComparison.Ordinal)).ToList();
            Assert.True(net.Count == 0, $"{a.GetName().Name} references {string.Join(", ", net)}");
        }
        // the module itself uses HttpClient, but only behind HttpGuard (allow-listed key services, no libraries)
        Assert.Contains("System.Net.Http.DelegatingHandler", TypeRefs(typeof(FortniteModule).Assembly));
    }

    [Fact]
    public void Download_and_native_helpers_are_gone()
    {
        var c4p = typeof(CUE4Parse.Compression.Compression).Assembly;
        foreach (var t in new[] { "CUE4Parse.Compression.OodleHelper", "CUE4Parse.Compression.ZlibHelper", "CUE4Parse.Utils.HttpUtils",
                     "CUE4Parse.UE4.Lua.unluac.UnluacHelper", "CUE4Parse.Utils.CRC32" })
            Assert.Null(c4p.GetType(t));
        var comp = typeof(CUE4Parse.Compression.Compression);
        Assert.Null(comp.GetMethod("UseNativeOodle"));
        Assert.Null(comp.GetMethod("UseNativeZlib"));
        Assert.NotNull(comp.GetMethod("UseOodle"));
        foreach (var a in Vendored)
        {
            var refs = a.GetReferencedAssemblies().Select(n => n.Name).ToList();
            foreach (var bad in new[] { "OodleSharp", "Oodle.NET", "Zlib-ng.NET", "OodleDotNet", "ZlibngDotNet" })
                Assert.DoesNotContain(bad, refs);
        }
    }

    [Fact]
    public void No_native_or_key_resources_are_embedded()
    {
        foreach (var a in Vendored)
            foreach (var r in a.GetManifestResourceNames())
            {
                var l = r.ToLowerInvariant();
                Assert.False(l.EndsWith(".dll") || l.EndsWith(".gz") || l.EndsWith(".bin") || l.EndsWith(".so") || l.EndsWith(".exe"), $"{a.GetName().Name} embeds {r}");
                Assert.DoesNotContain("hashmap", l);
                Assert.DoesNotContain("shaderhashednames", l);
                Assert.DoesNotContain("soundtagsmap", l);
            }
    }

    [Fact]
    public void No_oodle_zlib_or_skia_library_ships_next_to_the_code()
    {
        var files = Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        foreach (var f in files)
        {
            var l = f!.ToLowerInvariant();
            Assert.False(l.Contains("oodle") && !l.StartsWith("cthextract.oodle"), $"found {f}");
            Assert.False(l.Contains("oo2core") || l.Contains("zlib-ng") || l.Contains("libskiasharp") || l.Contains("detex") || l.Contains("prospi"), $"found {f}");
        }
    }

    [Fact]
    public void Sources_never_initialise_or_download_native_helpers()
    {
        var root = RepoExtractorDir();
        Assert.SkipWhen(root is null, "extractor sources not found next to the tests");
        var forbiddenEverywhere = new[] { "OodleHelper.Initialize", "ZlibHelper.Initialize", "DownloadOodleDll", "DownloadDllAsync", "UnluacHelper",
            "HttpUtils.", "WorkingRobot/OodleUE", "zlib-ng2.dll", "UseNativeOodle", "UseNativeZlib", "new HttpClient(" };
        var dirs = new[] { Path.Combine(root!, "vendor", "CUE4Parse"), Path.Combine(root!, "vendor", "vgmstream-binka"), Path.Combine(root!, "src", "CthExtract.Fortnite") };
        foreach (var dir in dirs)
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                var text = File.ReadAllText(f);
                foreach (var bad in forbiddenEverywhere)
                    Assert.False(text.Contains(bad, StringComparison.Ordinal) && !(bad == "new HttpClient(" && f.EndsWith("HttpGuard.cs")), $"{f} contains '{bad}'");
            }
        // our module never loads a native library itself
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root!, "src", "CthExtract.Fortnite"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("NativeLibrary.", text);
            Assert.DoesNotContain("DllImport", text);
        }
    }

    [Fact]
    public void Stripped_files_stay_stripped_and_licences_are_present()
    {
        var root = RepoExtractorDir();
        Assert.SkipWhen(root is null, "extractor sources not found next to the tests");
        var c4p = Path.Combine(root!, "vendor", "CUE4Parse");
        foreach (var rel in new[] { "CUE4Parse/Resources/ACE7Key.bin", "CUE4Parse/Resources/PUBGNameHashMap.json", "CUE4Parse/Resources/BL4SoundTagsMap.json",
                     "CUE4Parse/Resources/ProSpiDecryptor.dll.gz", "CUE4Parse/Resources/ShaderHashedNames.json", "CUE4Parse-Conversion/Resources/Detex.dll",
                     "CUE4Parse-Conversion/Resources/crunch.dll", "CUE4Parse-Conversion/Resources/tegra_swizzle_x64.dll", "CUE4Parse/Utils/CRC32.cs",
                     "CUE4Parse/Compression/OodleHelper.cs", "CUE4Parse/Compression/ZlibHelper.cs" })
            Assert.False(File.Exists(Path.Combine(c4p, rel)), $"{rel} is back");
        Assert.Empty(Directory.EnumerateFiles(c4p, "*.dll", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")));
        foreach (var v in new[] { "CUE4Parse", "vgmstream-binka" })
        {
            Assert.True(File.Exists(Path.Combine(root!, "vendor", v, "LICENSE")), $"{v}/LICENSE");
            Assert.True(File.Exists(Path.Combine(root!, "vendor", v, "VENDORED.md")), $"{v}/VENDORED.md");
        }
        Assert.True(File.Exists(Path.Combine(c4p, "NOTICE")));
        // the MS-PL CRC32 is not mentioned as a component of the code any more (NOTICE keeps upstream's text)
        Assert.DoesNotContain("Dino Chiesa", File.ReadAllText(Path.Combine(c4p, "CUE4Parse", "GameTypes", "FN", "Assets", "Exports", "ULevelSaveRecord.cs")));
    }

    private static readonly System.Text.RegularExpressions.Regex LongHex = new(@"^(0[xX])?[0-9A-Fa-f]{32,}$");

    /// <summary>No game key ships in the exe: no user string of CUE4Parse / CUE4Parse-Conversion is a non-zero hex key
    /// (upstream carried AES keys of other games as string literals; vendor.py step 4 zeroes them).</summary>
    [Fact]
    public void Compiled_parser_carries_no_hex_key_strings()
    {
        foreach (var a in Vendored.Take(2))
        {
            using var pe = new PEReader(File.OpenRead(a.Location));
            var md = pe.GetMetadataReader();
            var found = new List<string>();
            var h = md.GetHeapSize(HeapIndex.UserString) > 1 ? MetadataTokens.UserStringHandle(1) : default;
            int n = 0;
            while (!h.IsNil)
            {
                var s = md.GetUserString(h);
                if (LongHex.IsMatch(s) && s.TrimStart('0', 'x', 'X').Trim('0').Length > 0) found.Add(s[..12] + "…");
                h = md.GetNextHandle(h);
                n++;
            }
            Assert.True(n > 100, $"{a.GetName().Name}: only {n} user strings read");
            Assert.True(found.Count == 0, $"{a.GetName().Name} has hex key strings: {string.Join(", ", found)}");
        }
    }

    /// <summary>The vendored sources and cthextract.patch hold no game key material: no non-zero list of 16+ numbers or
    /// 32+ hex digit key literal in the other games' code, and no FMOD key list (vendor.py step 4, VENDORED.md).</summary>
    [Fact]
    public void Vendored_sources_and_patch_hold_no_game_keys()
    {
        var root = RepoExtractorDir();
        Assert.SkipWhen(root is null, "extractor sources not found next to the tests");
        var c4p = Path.Combine(root!, "vendor", "CUE4Parse");
        var sep = Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(Path.Combine(c4p, "CUE4Parse", "GameTypes"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}GameTypes{sep}FN{sep}") && !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .Concat(new[] { "UE4/IO/Objects/FIoStoreTocResource.cs", "UE4/Pak/Objects/FPakInfo.cs", "UE4/CriWare/CriWareProvider.cs", "UE4/FMod/Fsb5Decryption.cs" }
                .Select(r => Path.Combine(c4p, "CUE4Parse", r.Replace('/', sep))))
            .Append(Path.Combine(c4p, "cthextract.patch"))
            .ToList();
        Assert.True(files.Count > 200, $"only {files.Count} files scanned");
        var num = @"(?:0[xX][0-9A-Fa-f]+|\d+)[uUlL]*";
        var list = new System.Text.RegularExpressions.Regex(@"[\[{(]\s*((?:" + num + @"\s*,\s*(?://[^\n]*\n\s*)?){15,}" + num + @")\s*,?\s*[\]})]");
        var hexKey = new System.Text.RegularExpressions.Regex(@"(?<![0-9A-Fa-f])(?:0[xX])?([0-9A-Fa-f]{32,})(?![0-9A-Fa-f])");
        var problems = new List<string>();
        foreach (var f in files)
        {
            var t = File.ReadAllText(f);
            // (Fsb5Decryption's 256-entry table is the standard bit-reversal table, not key material)
            foreach (System.Text.RegularExpressions.Match m in list.Matches(t))
                if (!f.EndsWith("Fsb5Decryption.cs", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.Matches(m.Groups[1].Value, num).Any(n => n.Value.TrimStart('0', 'x', 'X').TrimEnd('u', 'U', 'l', 'L').Length > 0))
                    problems.Add($"{Path.GetFileName(f)}: non-zero number list at {m.Index}");
            foreach (System.Text.RegularExpressions.Match m in hexKey.Matches(t))
                if (m.Groups[1].Value.Trim('0').Length > 0 && !InUrlOrName(t, m))
                    problems.Add($"{Path.GetFileName(f)}: hex key literal {m.Value[..12]}…");
        }
        var fsb = File.ReadAllText(Path.Combine(c4p, "CUE4Parse", "UE4", "FMod", "Fsb5Decryption.cs"));
        Assert.Matches(@"/// - Godbreakers: \(key removed", fsb);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));

        // a commit / gist id inside a URL, or a GUID suffix of a property name ("Position_10_4C15...") is not a key
        static bool InUrlOrName(string t, System.Text.RegularExpressions.Match m) =>
            m.Index > 0 && (t[m.Index - 1] == '/' || t[m.Index - 1] == '_');
    }

    /// <summary>The replacement for DotNetZip's CRC32 in ULevelSaveRecord is the same function: DotNetZip's default
    /// (polynomial 0xEDB88320, not bit-reversed, register 0xFFFFFFFF, result inverted) re-implemented here from the
    /// algorithm description.</summary>
    [Fact]
    public void System_io_hashing_crc32_equals_dotnetzip_default_crc()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        var r = new Synth.Prng(42);
        for (int len = 0; len < 2000; len += 97)
        {
            var data = new byte[len];
            for (int i = 0; i < len; i++) data[i] = (byte)r.Next();
            uint reg = 0xFFFFFFFF;
            foreach (var b in data) reg = (reg >> 8) ^ table[(reg & 0xFF) ^ b];
            Assert.Equal(~reg, System.IO.Hashing.Crc32.HashToUInt32(data));
        }
        Assert.Equal(0xCBF43926u, System.IO.Hashing.Crc32.HashToUInt32("123456789"u8));
    }

    [Fact]
    public void Oodle_blocks_without_a_decoder_fail_with_a_clear_message()
    {
        Assert.SkipWhen(OodleBridge.Available, "this build has the managed Oodle decoder (covered by the Oodle fixture tests)");
        var e = Assert.ThrowsAny<Exception>(() => CUE4Parse.Compression.Compression.Decompress(new byte[] { 0x8C, 0x06, 0, 0 }, 16, CUE4Parse.Compression.CompressionMethod.Oodle));
        Assert.Contains("Oodle", e.Message);
    }

    [Fact]
    public void Texture_decoding_defaults_to_the_managed_decoder()
    {
        Assert.True(CUE4Parse_Conversion.Textures.TextureDecoder.UseAssetRipperTextureDecoder);
    }
}
