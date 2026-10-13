using CthExtract.Oodle.Tests.Synth;
using Xunit;

namespace CthExtract.Oodle.Tests;

public class ApiTests
{
    static byte[] Sample(out byte[] data, int size = 300_000, int codec = 6)
    {
        data = SynthData.Make("text", size, 42);
        return SynthOodle.Compress(data, new SynthOptions { Codec = codec });
    }

    [Fact]
    public void EmptyOutput_NeedsEmptyInput()
    {
        Assert.Equal(0, Ooz.Decompress(ReadOnlySpan<byte>.Empty, Span<byte>.Empty));
        Assert.Equal(-1, Ooz.Decompress(new byte[] { 0x8C, 0x06 }, Span<byte>.Empty, out var err));
        Assert.Equal(OozErrorCode.TrailingInput, err.Code);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x8C })]
    public void TooShortInput_IsTruncated(byte[] src)
    {
        Assert.Equal(-1, Ooz.Decompress(src, new byte[10], out var err));
        Assert.Equal(OozErrorCode.TruncatedInput, err.Code);
    }

    [Theory]
    [InlineData(0x00, 0x06)] // low nibble must be 0xC
    [InlineData(0x9C, 0x06)] // bits 4-5 must be zero
    [InlineData(0x8C, 0x07)] // unknown decoder type
    public void BadBlockHeader_IsReported(byte b0, byte b1)
    {
        Assert.Equal(-1, Ooz.Decompress(new byte[] { b0, b1, 0, 0, 0, 0 }, new byte[10], out var err));
        Assert.Equal(OozErrorCode.BadBlockHeader, err.Code);
        Assert.Equal(OozCodec.Unknown, Ooz.DetectCodec(new byte[] { b0, b1 }));
    }

    [Theory]
    [InlineData(0x05, OozCodec.Lzna)]
    [InlineData(0x0B, OozCodec.BitKnit)]
    public void LznaAndBitKnit_AreRecognisedButUnsupported(byte type, OozCodec codec)
    {
        var src = new byte[] { 0x8C, type, 0x00, 0x00, 0x10, 1, 2, 3 };
        Assert.Equal(codec, Ooz.DetectCodec(src));
        Assert.Equal(-1, Ooz.Decompress(src, new byte[100], out var err));
        Assert.Equal(OozErrorCode.UnsupportedCodec, err.Code);
        Assert.Equal(codec, err.Codec);
    }

    [Theory]
    [InlineData(0x06, OozCodec.Kraken)]
    [InlineData(0x0A, OozCodec.Mermaid)]
    [InlineData(0x0C, OozCodec.Leviathan)]
    [InlineData(0x86, OozCodec.Kraken)] // checksum flag
    public void DetectCodec_ReadsTheBlockHeader(byte type, OozCodec codec)
    {
        Assert.Equal(codec, Ooz.DetectCodec(new byte[] { 0x8C, type }));
        Assert.Equal(codec, Ooz.DetectCodec(new byte[] { 0xCC, type })); // uncompressed block
    }

    [Fact]
    public void WrongSizes_AreRejectedWithAReason()
    {
        var stream = Sample(out var data);
        Assert.Equal(-1, Ooz.Decompress(stream, new byte[data.Length + 1], out var tooBig));
        Assert.True(tooBig.IsError);
        Assert.Equal(-1, Ooz.Decompress(stream, new byte[data.Length - 1], out var tooSmall));
        Assert.True(tooSmall.IsError);
        Assert.Equal(-1, Ooz.Decompress(stream.AsSpan(0, stream.Length - 1), new byte[data.Length], out var cut));
        Assert.True(cut.IsError);
        var longer = stream.Concat(new byte[] { 0 }).ToArray();
        Assert.Equal(-1, Ooz.Decompress(longer, new byte[data.Length], out var trailing));
        Assert.Equal(OozErrorCode.TrailingInput, trailing.Code);
        Assert.Contains("TrailingInput", trailing.ToString());
    }

    /// <summary>A Huffman block whose table has a single symbol: ooz returns src - src_end instead of the block size,
    /// so the block is always rejected (native ooz: "decompress error"). Kept as is.</summary>
    [Fact]
    public void SingleSymbolHuffmanBlock_IsRejectedLikeOoz()
    {
        // quantum header (size 8), then an entropy-only chunk: type-2 block, long header, dst 100, src 3:
        // bits 0 (old lengths), 0 (sparse), 00000001 (1 symbol), 01000001 ('A')
        var stream = Convert.FromHexString("8C060000072001" + "8C0003" + "005040");
        Assert.Equal(-1, Ooz.Decompress(stream, new byte[100], out var err));
        Assert.Equal(OozErrorCode.CorruptData, err.Code);
    }

    [Fact]
    public void TryDecompress_MatchesCue4ParseDelegateContract()
    {
        var stream = Sample(out var data);
        var dst = new byte[data.Length];
        Assert.True(Ooz.TryDecompress(stream, dst, out int written));
        Assert.Equal(data.Length, written);
        Assert.True(dst.AsSpan().SequenceEqual(data));
        Assert.False(Ooz.TryDecompress(stream, new byte[data.Length + 5], out written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void FailedDecode_LeavesDestinationUntouched()
    {
        var stream = Sample(out var data);
        stream[stream.Length / 2] ^= 0xFF;
        var dst = new byte[data.Length];
        Array.Fill(dst, (byte)0x5A);
        if (Ooz.Decompress(stream, dst) < 0)
            Assert.All(dst, b => Assert.Equal(0x5A, b));
    }

    [Fact]
    public void ParallelDecodes_AreIndependent()
    {
        var inputs = Enumerable.Range(0, 12).Select(i =>
        {
            var data = SynthData.Make(SynthData.Kinds[i % SynthData.Kinds.Length], 100_000 + i * 1000, i);
            var o = new SynthOptions { Codec = new[] { 6, 10, 12 }[i % 3], LzMode = i % 2, Seed = i };
            return (data, stream: SynthOodle.Compress(data, o));
        }).ToArray();
        Parallel.For(0, 96, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
        {
            var (data, stream) = inputs[k % inputs.Length];
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got));
            Assert.True(got.AsSpan().SequenceEqual(data));
        });
    }

    [Fact]
    public void LargeMultiQuantumOutput_RoundTrips()
    {
        var data = SynthData.Make("mixed", 3_300_000, 77);
        foreach (int codec in new[] { 6, 10, 12 })
        {
            var stream = SynthOodle.Compress(data, new SynthOptions { Codec = codec, LzMode = codec == 12 ? 0 : 1 });
            var got = new byte[data.Length];
            Assert.Equal(data.Length, Ooz.Decompress(stream, got));
            Assert.True(got.AsSpan().SequenceEqual(data));
        }
    }
}
