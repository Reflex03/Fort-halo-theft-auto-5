using System.Text.Json;
using Xunit;

namespace CthExtract.Oodle.Tests;

/// <summary>Bit-exactness against data produced by real Oodle and by an Oodle-compatible compressor, checked against
/// the original files and against the native ooz decoder. Skips when the data is absent (see <see cref="TestData"/>).</summary>
public class ExternalVectorTests
{
    readonly ITestOutputHelper _out;
    public ExternalVectorTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void OozTestdata_MadeWithRealOodle_DecodeToSilesiaOriginals()
    {
        var dir = TestData.Dir("CTH_OOZ_TESTDATA", "ooz/testdata");
        var orig = TestData.Dir("CTH_SILESIA");
        Assert.SkipWhen(dir is null, "set CTH_OOZ_TESTDATA (or CTH_REFS) to ooz's testdata folder");
        Assert.SkipWhen(orig is null, "set CTH_SILESIA to the uncompressed Silesia corpus");

        int n = 0;
        var failures = new List<string>();
        foreach (var f in Directory.GetFiles(dir!).OrderBy(x => x, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(f);
            if (ext is not (".kraken" or ".mermaid" or ".selkie" or ".leviathan"))
                continue;
            var original = Path.Combine(orig!, Path.GetFileNameWithoutExtension(f));
            if (!File.Exists(original))
                continue;
            var (stream, raw) = TestData.ReadOoz(f);
            var want = File.ReadAllBytes(original);
            var got = new byte[raw];
            int r = Ooz.Decompress(stream, got, out var err);
            if (r != raw || !got.AsSpan().SequenceEqual(want))
                failures.Add($"{Path.GetFileName(f)}: {err}");
            n++;
        }
        _out.WriteLine($"{n} real-Oodle vectors checked, {failures.Count} failures");
        Assert.True(n > 0, "no vectors found");
        Assert.Empty(failures);
    }

    [Fact]
    public void OozTestdata_LznaAndBitKnit_AreReportedUnsupported()
    {
        var dir = TestData.Dir("CTH_OOZ_TESTDATA", "ooz/testdata");
        Assert.SkipWhen(dir is null, "set CTH_OOZ_TESTDATA (or CTH_REFS) to ooz's testdata folder");
        foreach (var f in Directory.GetFiles(dir!, "xml.*"))
        {
            var ext = Path.GetExtension(f);
            if (ext is not (".lzna" or ".bitknit"))
                continue;
            var (stream, raw) = TestData.ReadOoz(f);
            Assert.Equal(-1, Ooz.Decompress(stream, new byte[raw], out var err));
            Assert.Equal(OozErrorCode.UnsupportedCodec, err.Code);
            Assert.Equal(ext == ".lzna" ? OozCodec.Lzna : OozCodec.BitKnit, err.Codec);
        }
    }

    [Fact]
    public void GeneratedVectors_AllCodecsAndLevels_MatchOriginals()
    {
        var dir = TestData.Dir("CTH_OOZ_VECTORS");
        Assert.SkipWhen(dir is null, "set CTH_OOZ_VECTORS to a folder with manifest.json and *.ooz vectors");
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "manifest.json")));
        int n = 0;
        var failures = new List<string>();
        var perCodecLevel = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.GetProperty("compress_ok").GetBoolean() || !e.TryGetProperty("native_roundtrip", out var nr) || !nr.GetBoolean())
                continue;
            var name = e.GetProperty("vector").GetString()!;
            var (stream, raw) = TestData.ReadOoz(Path.Combine(dir!, name));
            var got = new byte[raw];
            int r = Ooz.Decompress(stream, got, out var err);
            if (r != raw || TestData.Sha256(got) != e.GetProperty("sha256").GetString())
                failures.Add($"{name}: {err}");
            var key = $"{e.GetProperty("codec").GetString()}{e.GetProperty("level").GetInt32()}";
            perCodecLevel[key] = perCodecLevel.GetValueOrDefault(key) + 1;
            n++;
        }
        _out.WriteLine($"{n} vectors checked: " + string.Join(", ", perCodecLevel.Select(kv => $"{kv.Key}={kv.Value}")));
        foreach (var f in failures.Take(20))
            _out.WriteLine("FAIL " + f);
        Assert.True(n > 0, "manifest lists no usable vectors");
        Assert.Empty(failures);
    }

    [Fact]
    public void UnrealEngineBlocks_DecodeLikeNativeOoz()
    {
        var dir = TestData.Dir("CTH_OOZ_UE_BLOCKS");
        Assert.SkipWhen(dir is null, "set CTH_OOZ_UE_BLOCKS to a folder of *.ooz Unreal Engine Oodle blocks");
        var native = new Dictionary<string, string>(StringComparer.Ordinal);
        var shaFile = Path.Combine(dir!, "native-ooz.sha256");
        if (File.Exists(shaFile))
        {
            foreach (var line in File.ReadAllLines(shaFile))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) native[parts[0]] = parts[1];
            }
        }
        int n = 0, compared = 0;
        var failures = new List<string>();
        foreach (var f in Directory.GetFiles(dir!, "*.ooz").OrderBy(x => x, StringComparer.Ordinal))
        {
            var (stream, raw) = TestData.ReadOoz(f);
            var got = new byte[raw];
            int r = Ooz.Decompress(stream, got, out var err);
            var name = Path.GetFileName(f);
            if (r != raw)
                failures.Add($"{name}: {err}");
            else if (native.TryGetValue(name, out var want))
            {
                compared++;
                if (TestData.Sha256(got) != want)
                    failures.Add($"{name}: differs from native ooz");
            }
            n++;
        }
        _out.WriteLine($"{n} Unreal Engine blocks decoded, {compared} compared with native ooz, {failures.Count} failures");
        Assert.True(n > 0, "no *.ooz files");
        Assert.Empty(failures);
    }
}
