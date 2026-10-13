using Fmod5Sharp;
using Fmod5Sharp.CodecRebuilders;
using Xunit;

namespace CthExtract.Halo.Tests;

/// <summary>The vendored Fmod5Sharp on synthetic FSB5 banks written by our own code.</summary>
public class FsbTests
{
    private static readonly SynthAudio.Encoded Mono = SynthAudio.Encode(1, 44100, 0.0f, 0.3);
    private static readonly SynthAudio.Encoded Stereo = SynthAudio.Encode(2, 44100, 0.4f, 0.2, 330);
    private static readonly SynthAudio.Encoded Mono48 = SynthAudio.Encode(1, 48000, 0.0f, 0.2, 550);

    private static void AssertMatchesReference(byte[] ogg, SynthAudio.Encoded e)
    {
        var got = SynthAudio.Decode(ogg, out var ch, out var rate);
        var exp = SynthAudio.Decode(e.ReferenceOgg, out _, out _);
        Assert.Equal(e.Channels, ch);
        Assert.Equal(e.Rate, rate);
        Assert.InRange(got.Length, exp.Length - 2048 * ch, exp.Length + 2048 * ch);
        int n = Math.Min(got.Length, exp.Length);
        double maxErr = 0;
        for (int i = 0; i < n; i++) maxErr = Math.Max(maxErr, Math.Abs(got[i] - exp[i]));
        Assert.True(maxErr < 1e-4, $"max error {maxErr}");
    }

    [Fact]
    public void LazyBankReadsOnlyHeadersUntilASampleIsUsed()
    {
        var items = new List<SynthAudio.FsbItem>();
        for (int i = 0; i < 40; i++) items.Add(new($"s{i}", i % 3 == 0 ? Mono : i % 3 == 1 ? Stereo : Mono48));
        var bytes = SynthAudio.BuildFsb5(items);
        using var counting = new CountingStream(new MemoryStream(bytes));
        using var bank = FsbLoader.LoadFsbFromStreamLazy(counting, leaveOpen: true);
        long afterLoad = counting.BytesRead;
        Assert.Equal(40, bank.Samples.Count);
        Assert.True(afterLoad < bytes.Length / 4, $"load read {afterLoad} of {bytes.Length} bytes");
        Assert.All(bank.Samples, s => Assert.True(s.IsLazy));
        Assert.Equal("s37", bank.Samples[37].Name);

        var sample = bank.Samples[36]; // 36 % 3 == 0: mono
        Assert.True(sample.RebuildAsStandardFileFormat(out var ogg, out var ext));
        Assert.Equal("ogg", ext);
        long used = counting.BytesRead - afterLoad;
        Assert.InRange(used, sample.DataLength, sample.DataLength + 64);
        AssertMatchesReference(ogg!, Mono);
        AssertMatchesReference(SoundRebuild(bank.Samples[1]), Stereo);
        AssertMatchesReference(SoundRebuild(bank.Samples[2]), Mono48);
    }

    private static byte[] SoundRebuild(Fmod5Sharp.FmodTypes.FmodSample s)
    {
        Assert.True(s.RebuildAsStandardFileFormat(out var d, out _));
        return d!;
    }

    [Fact]
    public void UpstreamEagerApiStillWorks()
    {
        var bytes = SynthAudio.BuildFsb5(new[] { new SynthAudio.FsbItem("ar_fire", Mono), new SynthAudio.FsbItem("br_fire", Stereo) });
        var bank = FsbLoader.LoadFsbFromByteArray(bytes);
        Assert.False(bank.Samples[0].IsLazy);
        Assert.Equal("br_fire", bank.Samples[1].Name);
        AssertMatchesReference(SoundRebuild(bank.Samples[0]), Mono);
        Assert.True(FsbLoader.TryLoadFsbFromByteArray(bytes, out _));
        Assert.False(FsbLoader.TryLoadFsbFromByteArray(new byte[100], out _));
    }

    [Fact]
    public void LazyFileBankIsSharedReadOnlyAndReleasedOnDispose()
    {
        using var t = new TempDir();
        var path = t.File("sfx.fsb", SynthAudio.BuildFsb5(new[] { new SynthAudio.FsbItem("a", Mono) }));
        var bank = FsbLoader.LoadFsbFromFileLazy(path);
        // another process may keep writing / deleting the game's file while we read it
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { }
        AssertMatchesReference(SoundRebuild(bank.Samples[0]), Mono);
        bank.Dispose();
        Assert.Throws<ObjectDisposedException>(() => bank.Samples[0].SampleBytes);
        File.Delete(path); // no handle left
    }

    [Fact]
    public void TruncatedBankIsRejected()
    {
        var bytes = SynthAudio.BuildFsb5(new[] { new SynthAudio.FsbItem("a", Mono), new SynthAudio.FsbItem("b", Stereo) });
        var cut = bytes.AsSpan(0, bytes.Length - 100).ToArray();
        Assert.Throws<InvalidDataException>(() => FsbLoader.LoadFsbFromStreamLazy(new MemoryStream(cut)));
        Assert.False(FsbLoader.TryLoadFsbFromStream(new MemoryStream(cut), out _));
    }

    [Fact]
    public void SetupHeaderCrcIsExposedAndMissingHeadersAreNamed()
    {
        var bytes = SynthAudio.BuildFsb5(new[] { new SynthAudio.FsbItem("a", Mono) });
        // swap the crc for one that is in no table
        int crcAt = 0x3C + 8 + 4;
        BitConverter.GetBytes(0x12345678u).CopyTo(bytes, crcAt);
        var bank = FsbLoader.LoadFsbFromByteArray(bytes);
        Assert.Equal(0x12345678u, FmodVorbisRebuilder.GetSetupHeaderCrc(bank.Samples[0]));
        Assert.False(FmodVorbisRebuilder.HasSetupHeader(0x12345678u));
        Assert.True(FmodVorbisRebuilder.HasSetupHeader(Mono.SetupCrc));
        var e = Assert.Throws<KeyNotFoundException>(() => bank.Samples[0].RebuildAsStandardFileFormat(out _, out _));
        Assert.Contains("0x12345678", e.Message);
    }

    [Fact]
    public void PcmFallbackEncodesAndDecodes()
    {
        var tone = new float[1][];
        tone[0] = Enumerable.Range(0, 22050).Select(i => 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 44100f)).ToArray();
        var ogg = OggEncode.FromPcm(tone, 44100);
        Assert.True(CthExtract.Core.Ogg.LooksLikeVorbis(ogg));
        var notes = new List<string>();
        SoundDecode.Verify(ogg, 1, 44100, 22050, notes);
        var pcm16 = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(pcm16, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(pcm16, 2);
        var f = SoundDecode.PcmFloat(pcm16, Fmod5Sharp.FmodTypes.FmodAudioType.PCM16, 2, 2);
        Assert.Equal(0.5f, f[0][0]); Assert.Equal(-0.5f, f[1][0]);
    }

    [Fact]
    public void VerifyRejectsWrongLengthAndFormat()
    {
        Assert.Throws<InvalidDataException>(() => SoundDecode.Verify(Mono.ReferenceOgg, 2, 44100, Mono.Frames, new()));
        Assert.Throws<InvalidDataException>(() => SoundDecode.Verify(Mono.ReferenceOgg, 1, 44100, Mono.Frames * 3L, new()));
        Assert.Throws<InvalidDataException>(() => SoundDecode.Verify(new byte[200], 1, 44100, 100, new()));
        SoundDecode.Verify(Mono.ReferenceOgg, 1, 44100, Mono.Frames, new());
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream inner;
        public long BytesRead;
        public CountingStream(Stream inner) => this.inner = inner;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
