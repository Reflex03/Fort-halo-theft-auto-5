using System.Text.RegularExpressions;
using Reclaimer.Blam.Common;
using Xunit;

namespace CthExtract.Halo.Tests;

/// <summary>Guards on the vendored sources: never write into a game folder, never ship the proprietary DLLs.</summary>
public class VendorTests
{
    private static string? Vendor(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var v = Path.Combine(d.FullName, "vendor", name);
            if (Directory.Exists(v)) return v;
            v = Path.Combine(d.FullName, "extractor", "vendor", name);
            if (Directory.Exists(v)) return v;
        }
        return null;
    }

    private static IEnumerable<string> Sources(string dir) =>
        Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void VendoredReclaimerOnlyOpensFilesReadOnlyAndShared()
    {
        var dir = Vendor("Reclaimer");
        Assert.SkipWhen(dir is null, "vendor/Reclaimer not found next to the tests");
        var bad = new List<string>();
        foreach (var f in Sources(dir!))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l.Contains("FileAccess.ReadWrite")) bad.Add($"{f}:{i + 1}: {l.Trim()}");
                if (Regex.IsMatch(l, @"FileMode\.Open\b") && !l.Contains("FileShare.ReadWrite | FileShare.Delete")) bad.Add($"{f}:{i + 1}: {l.Trim()}");
                if (Regex.IsMatch(l, @"File\.Open(Write)?\(|File\.Create\(")) bad.Add($"{f}:{i + 1}: {l.Trim()}");
            }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void ProprietaryBinariesAndKeysAreNotVendored()
    {
        var dir = Vendor("Reclaimer");
        Assert.SkipWhen(dir is null, "vendor/Reclaimer not found next to the tests");
        var files = Directory.EnumerateFiles(dir!, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(files, f => f!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(files, f => f!.EndsWith(".snk", StringComparison.OrdinalIgnoreCase));
        // binary lookup tables of unclear provenance (Xbox P8 bump palettes) are stripped by vendor.py
        Assert.DoesNotContain(files, f => f!.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("LICENSE", files);
        Assert.Contains("VENDORED.md", files);
        Assert.True(File.Exists(Path.Combine(Vendor("Fmod5Sharp")!, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(Vendor("Fmod5Sharp")!, "VENDORED.md")));

        // no game key is committed or shipped: vendor.py zeroes the Halo: Reach / Halo 4 cache AES key strings
        foreach (var t in new[] { typeof(Reclaimer.Blam.HaloReach.CacheFile), typeof(Reclaimer.Blam.Halo4.CacheFile) })
            foreach (var fi in t.GetFields().Where(f => f.IsLiteral && f.Name.EndsWith("Key", StringComparison.Ordinal)))
                Assert.Matches("^0+$", (string)fi.GetRawConstantValue()!);
        foreach (var f in Sources(dir!))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"const string \w*Key = ""([^""]*)"""))
                Assert.True(Regex.IsMatch(m.Groups[1].Value, "^0*$"), $"{f}: {m.Value[..Math.Min(30, m.Value.Length)]}");
    }

    [Fact]
    public void ReclaimerRejectsAJunkMapWithAReadableError()
    {
        using var t = new TempDir();
        var path = t.File(@"halo3\maps\riverworld.map", new byte[0x4000]);
        var e = Assert.ThrowsAny<Exception>(() => ReclaimerMap.Open(path));
        Assert.Contains("not a valid map", e.Message, StringComparison.OrdinalIgnoreCase);
        // the file is not held open after the failure
        File.Delete(path);
    }

    [Fact]
    public void ReclaimerEmbeddedMccResourcesLoad()
    {
        // MCC Halo 3 string-id layout and vertex declarations are embedded resources of the vendored assembly
        Assert.True(typeof(CacheFactory).Assembly.GetManifestResourceNames().Length > 0);
        var res = typeof(CacheFactory).Assembly.GetType("Reclaimer.Blam.Properties.Resources")!;
        var strings = (string)res.GetProperty("MccHalo3Strings", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(null)!;
        var vb = (string)res.GetProperty("MccHalo3VertexBuffer", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(null)!;
        Assert.Contains("<", strings);
        Assert.Contains("<", vb);
    }
}
