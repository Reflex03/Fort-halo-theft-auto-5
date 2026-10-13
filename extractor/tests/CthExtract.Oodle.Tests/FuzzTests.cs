using System.Diagnostics;
using CthExtract.Oodle.Tests.Synth;
using Xunit;

namespace CthExtract.Oodle.Tests;

/// <summary>
/// Corrupt input must never crash the decoder, hang it, or make it touch memory outside its buffers. Inputs are our
/// synthetic streams (plus external vectors when available), mutated; on Linux every buffer sits against a guard
/// page. Duration: CTH_OOZ_FUZZ_SECONDS (default 15 s per test).
/// </summary>
public class FuzzTests
{
    readonly ITestOutputHelper _out;
    public FuzzTests(ITestOutputHelper output) => _out = output;

    static List<Seed> SyntheticSeeds()
    {
        var seeds = new List<Seed>();
        foreach (var c in SynthMatrix.Cases(90, 99, maxSize: 300_000))
            seeds.Add(new Seed(c.Name, SynthOodle.Compress(c.Data, c.Options), c.Data.Length));
        return seeds;
    }

    static List<Seed> ExternalSeeds(int maxRaw)
    {
        var seeds = new List<Seed>();
        foreach (var env in new[] { "CTH_OOZ_UE_BLOCKS", "CTH_OOZ_VECTORS" })
        {
            var dir = TestData.Dir(env);
            if (dir is null) continue;
            foreach (var f in Directory.GetFiles(dir, "*.ooz"))
            {
                var (stream, raw) = TestData.ReadOoz(f);
                if (raw <= maxRaw) seeds.Add(new Seed(Path.GetFileName(f), stream, raw));
            }
        }
        return seeds;
    }

    void Run(List<Seed> seeds, int seconds, int rngSeed)
    {
        var rng = new Random(rngSeed);
        int maxRaw = seeds.Max(s => s.RawSize) + 0x10000;
        int maxIn = seeds.Max(s => s.Stream.Length) + 4096;
        using var fz = new OozFuzzer(maxIn, maxRaw);
        // the unmutated seeds must decode (and exactly fill the guarded output)
        foreach (var s in seeds)
            Assert.True(fz.Decode(s.Stream, s.RawSize, rng.Next(2) == 0, out var e) == s.RawSize, $"{s.Name}: {e}");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            byte[] input; int dstLen;
            if (rng.Next(10) == 0)
            {
                var s = OozFuzzer.RandomStructured(rng);
                input = s.Stream; dstLen = s.RawSize;
            }
            else
            {
                var s = seeds[rng.Next(seeds.Count)];
                input = OozFuzzer.Mutate(rng, s.Stream, seeds);
                dstLen = rng.Next(16) switch
                {
                    0 => Math.Max(1, s.RawSize - rng.Next(1, 300)),
                    1 => s.RawSize + rng.Next(1, 300),
                    _ => s.RawSize,
                };
            }
            fz.Decode(input, dstLen, rng.Next(2) == 0, out _);
            if ((fz.Iterations & 31) == 0)
            {
                // the public API must never throw and never report an internal error
                var dst = new byte[Math.Min(dstLen, maxRaw)];
                Ooz.Decompress(input, dst, out var err);
                Assert.NotEqual(OozErrorCode.InternalError, err.Code);
            }
        }
        _out.WriteLine($"guard pages: {fz.Guarded}; {fz.Iterations} iterations ({fz.Iterations / sw.Elapsed.TotalSeconds:0}/s), " +
                       $"{fz.Decoded} decoded, {fz.Rejected} rejected; slowest {fz.MaxSeconds * 1000:0.0} ms ({fz.SlowestInput})");
        _out.WriteLine(string.Join(", ", fz.Errors.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
        Assert.True(fz.MaxSeconds < 2.0, $"a single decode took {fz.MaxSeconds:0.00} s");
        Assert.True(fz.Iterations > 100);
    }

    [Fact]
    public void MutatedSyntheticStreams_NeverCrash() => Run(SyntheticSeeds(), TestData.FuzzSeconds(15), 1);

    [Fact]
    public void MutatedExternalStreams_NeverCrash()
    {
        var seeds = ExternalSeeds(300_000);
        Assert.SkipWhen(seeds.Count == 0, "set CTH_OOZ_UE_BLOCKS and/or CTH_OOZ_VECTORS for external fuzz seeds");
        Run(seeds, TestData.FuzzSeconds(15), 2);
    }

    [Fact]
    public void RandomBytes_AreRejected()
    {
        var rng = new Random(3);
        for (int i = 0; i < 20000; i++)
        {
            var src = new byte[rng.Next(0, 300)];
            rng.NextBytes(src);
            if (src.Length >= 2 && rng.Next(2) == 0) { src[0] = 0x8C; src[1] = (byte)new[] { 6, 10, 12 }[rng.Next(3)]; }
            var dst = new byte[rng.Next(1, 5000)];
            int r = Ooz.Decompress(src, dst, out var err);
            Assert.True(r == -1 || r == dst.Length);
            Assert.NotEqual(OozErrorCode.InternalError, err.Code);
        }
    }

    /// <summary>Every truncation of a valid stream fails cleanly (or, never, decodes).</summary>
    [Fact]
    public void EveryTruncation_IsRejected()
    {
        foreach (int codec in new[] { 6, 10, 12 })
        {
            var data = SynthData.Make("mixed", 20_000, codec);
            var stream = SynthOodle.Compress(data, new SynthOptions { Codec = codec, LzMode = codec == 12 ? 2 : 0 });
            var dst = new byte[data.Length];
            for (int len = 0; len < stream.Length; len++)
            {
                int r = Ooz.Decompress(stream.AsSpan(0, len), dst, out var err);
                Assert.Equal(-1, r);
                Assert.NotEqual(OozErrorCode.InternalError, err.Code);
            }
        }
    }
}
