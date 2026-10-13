using CthExtract.Oodle.Tests.Synth;
using Xunit;

namespace CthExtract.Oodle.Tests;

/// <summary>
/// Round trips through our own Oodle-format encoder (<see cref="SynthOodle"/>), covering every codec, literal mode,
/// entropy coder, offset coding and framing variant it can emit. During development the same streams were also
/// decoded by a native build of ooz, which reproduced every input (except a few tiny Mermaid chunks using tANS or
/// Huffman, which only decode with zao/ooz's temp-buffer fix; this port includes it).
/// </summary>
public class SyntheticTests
{
    readonly ITestOutputHelper _out;
    public SyntheticTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1, 120)]
    [InlineData(2, 120)]
    [InlineData(3, 120)]
    public void Matrix_RoundTrips(int seed, int count)
    {
        var failures = new List<string>();
        int n = 0;
        long raw = 0, comp = 0;
        foreach (var c in SynthMatrix.Cases(count, seed))
        {
            var stream = SynthOodle.Compress(c.Data, c.Options);
            var got = new byte[c.Data.Length];
            int r = Ooz.Decompress(stream, got, out var err);
            if (r != c.Data.Length || !got.AsSpan().SequenceEqual(c.Data))
                failures.Add($"{c.Name}: r={r} {err}");
            n++;
            raw += c.Data.Length;
            comp += stream.Length;
        }
        _out.WriteLine($"{n} cases, {raw} -> {comp} bytes");
        Assert.Empty(failures);
    }

    public static TheoryData<int, int, int> CodecModes()
    {
        var d = new TheoryData<int, int, int>();
        foreach (var lit in new[] { 0, 1 }) { d.Add(6, lit, 0); d.Add(6, lit, 1); d.Add(10, lit, 0); d.Add(10, lit, 1); }
        for (int t = 0; t < 6; t++) { d.Add(12, t, 0); d.Add(12, t, 1); }
        return d;
    }

    /// <summary>Every LZ literal mode of every codec, single- and multi-quantum, through the LZ path only.</summary>
    [Theory]
    [MemberData(nameof(CodecModes))]
    public void EveryLzMode_RoundTrips(int codec, int mode, int variant)
    {
        foreach (var kind in new[] { "text", "records", "mixed", "sine16" })
        {
            var data = SynthData.Make(kind, variant == 0 ? 50_000 : 400_000, codec * 100 + mode);
            var o = new SynthOptions
            {
                Codec = codec,
                LzMode = mode,
                Literals = variant == 0 ? EntropyKind.Huff3 : EntropyKind.Huff6,
                Commands = variant == 0 ? EntropyKind.Raw : EntropyKind.Huff3,
                Other = variant == 0 ? EntropyKind.Rle : EntropyKind.Huff3Gamma,
                OffsetScale = codec == 6 && variant == 1 ? 4 : 0,
                MultiCmd = codec == 12 && variant == 1,
                Off16Entropy = codec == 10 && variant == 1,
                Seed = mode,
            };
            var stream = SynthOodle.Compress(data, o);
            Assert.True(stream.Length < data.Length, $"{kind}: the encoder did not compress");
            Assert.Equal(Ooz.DetectCodec(stream), codec switch { 6 => OozCodec.Kraken, 10 => OozCodec.Mermaid, _ => OozCodec.Leviathan });
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got, out var err));
            Assert.True(got.AsSpan().SequenceEqual(data), $"{kind}: output differs ({err})");
        }
    }

    /// <summary>Windows past 8 MiB (Kraken's long distance code) and 12 MiB (Mermaid's 4-byte far offsets), plus
    /// Mermaid half-chunks with more than 4095 far offsets.</summary>
    [Fact]
    public void LargeWindows_RoundTrip()
    {
        var x = SynthData.Make("random", 12_700_000, 21);
        var data = x.Concat(x.AsSpan(0, 300_000).ToArray()).ToArray();
        foreach (var o in new[]
        {
            new SynthOptions { Codec = 6, MaxDistance = 1 << 24, SearchDepth = 2, ChunkKind = 0, Literals = EntropyKind.Raw },
            new SynthOptions { Codec = 10, MaxDistance = 1 << 24, SearchDepth = 2, ChunkKind = 0, PreferFar = true, Literals = EntropyKind.Raw },
        })
        {
            var stream = SynthOodle.Compress(data, o);
            Assert.True(stream.Length < data.Length - 200_000, $"codec {o.Codec}: the repeat was not matched");
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got, out var err));
            Assert.True(got.AsSpan().SequenceEqual(data), err.ToString());
        }
        // > 4095 far offsets in one 64 KiB half: the second half is 8192 8-byte snippets of the (random) first half
        var r = SynthData.Make("random", 0x10000, 22);
        var rng = new Random(23);
        var t = new byte[0x20000];
        r.CopyTo(t, 0);
        for (int i = 0x10000; i < t.Length; i += 8)
            Array.Copy(r, rng.Next(0x10000 - 8), t, i, 8);
        var far = SynthOodle.Compress(t, new SynthOptions { Codec = 10, PreferFar = true, MinMatch = 8, SearchDepth = 64, Literals = EntropyKind.Raw });
        Assert.True(far.Length < 0x20000 - 0x8000, "the snippets were not matched");
        var got2 = new byte[t.Length];
        Assert.Equal(t.Length, Ooz.Decompress(far, got2));
        Assert.True(got2.AsSpan().SequenceEqual(t));
    }

    [Fact]
    public void MemsetUncompressedAndStoredQuanta_RoundTrip()
    {
        var zeros = new byte[700_000];
        var random = SynthData.Make("random", 600_000, 5);
        foreach (var (data, o) in new[]
        {
            (zeros, new SynthOptions { Memset = true }),
            (random, new SynthOptions { Uncompressed = true }),
            (random, new SynthOptions { Codec = 10 }), // incompressible: stored chunks / uncompressed quanta
            (SynthData.Make("random", 70_000, 6), new SynthOptions { Codec = 12, ChunkKind = 2 }),
        })
        {
            var stream = SynthOodle.Compress(data, o);
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got, out var err));
            Assert.True(got.AsSpan().SequenceEqual(data), err.ToString());
        }
    }

    /// <summary>ooz rejects every checksummed quantum (its CRC function is a stub returning 0) unless the stored
    /// checksum happens to be 0. The port skips the check, so both decode here.</summary>
    [Fact]
    public void ChecksummedStreams_Decode_ChecksumNotVerified()
    {
        var data = SynthData.Make("text", 300_000, 11);
        foreach (int crc in new[] { 0, 0xABCDEF })
        {
            var stream = SynthOodle.Compress(data, new SynthOptions { Checksum = crc });
            Assert.Equal(0x80, stream[1] & 0x80);
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got));
            Assert.True(got.AsSpan().SequenceEqual(data));
        }
    }

    /// <summary>Deliberate fix: an entropy-only chunk that is a raw (type 0) block is copied to the output. ooz
    /// leaves the output untouched there (it only points at the input).</summary>
    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    public void RawEntropyOnlyChunk_IsCopied(int codec)
    {
        // chunk 1: incompressible -> raw entropy-only chunk; chunk 2: text -> LZ, so the quantum stays compressed
        var data = SynthData.Make("random", 0x20000, 12).Concat(SynthData.Make("text", 100_000, 13)).ToArray();
        var stream = SynthOodle.Compress(data, new SynthOptions
        {
            Codec = codec, FirstChunkKind = 1, Literals = EntropyKind.Raw, AllowRawEntropyChunk = true, Memset = false,
        });
        Assert.Equal(0x8C, stream[0]);
        Assert.True((stream[5] & 0x80) == 0 && ((stream[5] >> 4) & 7) == 0, "first chunk is not a raw entropy block");
        var got = new byte[data.Length];
        Assert.Equal(data.Length, Ooz.Decompress(stream, got));
        Assert.True(got.AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void EntropyCoders_EachRoundTripThroughEntropyOnlyChunks()
    {
        foreach (var kind in Enum.GetValues<EntropyKind>())
        {
            foreach (var dk in new[] { "text", "runs", "alpha4", "sparse" })
            {
                var data = SynthData.Make(dk, 140_000, (int)kind);
                var stream = SynthOodle.Compress(data, new SynthOptions { ChunkKind = 1, Literals = kind, Seed = (int)kind });
                var got = new byte[data.Length];
                Assert.Equal(data.Length, Ooz.Decompress(stream, got, out var err));
                Assert.True(got.AsSpan().SequenceEqual(data), $"{kind}/{dk}: {err}");
            }
        }
    }
}
