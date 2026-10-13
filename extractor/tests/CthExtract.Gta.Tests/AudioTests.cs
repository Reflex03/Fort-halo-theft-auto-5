using CthExtract.Core;
using CthExtract.Gta.Audio;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Tests.Synth;
using Xunit;

namespace CthExtract.Gta.Tests;

public class AdpcmTests
{
    // Reference: an independent implementation of the published IMA/DVI ADPCM decoder (tools script in the PR notes)
    // run on this deterministic frame (step index 20, predictor -1234, then LCG bytes).
    private const string FrameHex = "14002efbdc0465aa1fad1d5adae5ac1b1e5f1370796cfd10ff19af601d04acb41d022b4678733af2df5faeb70859d1ee3910cb4895b5cc892911ff06b6622edf";
    private static readonly short[] Expected =
    {
        -1290,-1372,-1273,-1260,-1127,-896,-1053,-1196,-1587,-1419,-1980,-2353,-3101,-2803,-3255,-2351,-2952,-4156,-2394,-5445,-9187,-11703,
        -14905,-13659,-18573,-16565,-25696,-11339,2038,7249,8828,30364,21132,32767,-4095,32767,-12286,-32768,-28673,-17501,-32768,-32768,
        -32768,-21596,-32768,-32768,-29044,14970,-30083,-17797,15721,19816,-13702,-32768,750,-27919,-32768,-20482,-1861,1524,-20019,-6029,
        27039,32767,28672,32767,32767,32767,12289,32767,32767,-13399,-32768,-32768,-32768,12285,-32768,-32768,23095,-5574,-9298,-5913,-15145,
        15634,27920,-13046,-32768,-32768,-32768,-6699,-3314,5918,-13668,-32768,-32768,-7585,29657,17371,32767,4098,-29420,-32768,-32768,-32768,
        -32768,-17380,-8986,-1356,-32768,-32768,20477,24572,32767,4098,22719,32767,-20478,0,-32768,-32768,
    };

    [Fact]
    public void Decoder_matches_independent_reference_vectors()
    {
        var frame = Convert.FromHexString(FrameHex);
        var o = new short[Expected.Length];
        Assert.Equal(Expected.Length, ImaAdpcm.DecodeFrame(frame, o));
        Assert.Equal(Expected, o);
    }

    [Fact]
    public void Encode_decode_roundtrip_has_good_snr_across_frames()
    {
        var pcm = AwcWriter.Sweep(32000, 1.0, 300, 3000);
        var dec = ImaAdpcm.DecodeFrames(ImaEncoder.EncodeFrames(pcm), pcm.Length);
        Assert.Equal(pcm.Length, dec.Length);
        Assert.True(Snr(pcm, dec) > 20, $"SNR {Snr(pcm, dec):F1} dB");
    }

    public static double Snr(short[] a, short[] b)
    {
        double s = 0, n = 0;
        for (int i = 0; i < a.Length; i++) { s += (double)a[i] * a[i]; double d = a[i] - b[i]; n += d * d; }
        return 10 * Math.Log10(s / Math.Max(1, n));
    }
}

public class AwcTests
{
    private static readonly AwcWriter.Wave[] Waves =
    {
        new("siren_pa20a_wail", AwcWriter.Sweep(32000, 0.8, 700, 1500), 32000, 4, 0),
        new("horn_std_1", AwcWriter.Sweep(24000, 0.4, 440, 450), 24000, 4),
        new("pcm_thing", AwcWriter.Sweep(22050, 0.1, 100, 200), 22050, 0),
    };

    [Theory]
    [InlineData(false, false, 0u)]
    [InlineData(true, false, 0u)]
    [InlineData(false, true, 0x00020000u)] // CodeWalker's single-channel-encrypted bit
    [InlineData(true, true, 0x00080000u)]  // vgmstream's encrypted bit
    public void Sfx_pack_streams_decode(bool chunkIndices, bool encrypted, uint flag)
    {
        var key = new uint[] { 1, 2, 3, 4 };
        var bytes = AwcWriter.SfxPack(Waves, encrypted ? key : null, chunkIndices, flag);
        var awc = AwcFile.Parse(bytes, encrypted ? key : null);
        Assert.False(awc.Multichannel);
        Assert.Equal(encrypted, awc.ChunksEncrypted);
        Assert.Equal(3, awc.Streams.Count);
        var siren = awc.Streams.Single(s => s.Id == 0x0D329446);
        Assert.Equal(32000, siren.SampleRate);
        Assert.Equal("IMA-ADPCM", siren.CodecName);
        Assert.True(siren.Loops);
        Assert.True(AdpcmTests.Snr(Waves[0].Pcm, siren.DecodePcm()) > 20);
        Assert.Equal(encrypted ? "decrypted" : null, siren.Note);
        var pcm = awc.Streams.Single(s => s.Id == Hashes.AwcStreamId("pcm_thing"));
        Assert.Equal(Waves[2].Pcm, pcm.DecodePcm());
        Assert.False(pcm.Loops);
    }

    [Theory]
    [InlineData(0x00020000u)]
    [InlineData(0x00080000u)]
    public void Encryption_flag_on_plain_data_still_decodes(uint flag)
    {
        var key = new uint[] { 5, 6, 7, 8 };
        var awc = AwcFile.Parse(AwcWriter.SfxPack(Waves, key, encryptFlag: flag, setFlagOnly: true), key);
        foreach (var w in Waves)
        {
            var s = awc.Streams.Single(x => x.Id == Hashes.AwcStreamId(w.Name));
            Assert.True(AdpcmTests.Snr(w.Pcm, s.DecodePcm()) > 20, w.Name);
            Assert.Equal("flagged encrypted but decodes as plain audio", s.Note);
        }
        // and without a key too
        var noKey = AwcFile.Parse(AwcWriter.SfxPack(Waves, key, encryptFlag: flag, setFlagOnly: true), null);
        Assert.True(AdpcmTests.Snr(Waves[0].Pcm, noKey.Streams[0].DecodePcm()) > 20);
    }

    [Fact]
    public void Encrypted_data_without_key_is_reported_as_missing_key()
    {
        var bytes = AwcWriter.SfxPack(Waves, new uint[] { 9, 9, 9, 9 });
        var awc = AwcFile.Parse(bytes, null); // the header is readable; the encrypted data is not
        Assert.Throws<KeyUnavailableException>(() => awc.Streams.Single(s => s.Id == 0x0D329446).DecodePcm());
    }

    [Fact]
    public void Whole_file_encryption_is_undone_with_the_awc_key()
    {
        var key = new uint[] { 0xDEADBEEF, 0x12345678, 0x0BADF00D, 0xCAFEBABE };
        var bytes = AwcWriter.SfxPack(Waves);
        Array.Resize(ref bytes, (bytes.Length + 3) / 4 * 4);
        Xxtea.EncryptInPlace(bytes, AwcFile.EffectiveKey(key));
        var tmp = (byte[])bytes.Clone();
        Xxtea.DecryptInPlace(tmp, key); // plain XXTEA with the raw key (without Rockstar's XOR) must NOT work
        Assert.NotEqual(AwcFile.MagicLe, BitConverter.ToUInt32(tmp, 0));
        Assert.Throws<KeyUnavailableException>(() => AwcFile.Parse(bytes, null));
        Assert.Throws<InvalidDataException>(() => AwcFile.Parse(bytes, new uint[] { 1, 1, 1, 1 }));
        var awc = AwcFile.Parse(bytes, key);
        Assert.True(awc.WholeFileWasEncrypted);
        Assert.Equal(3, awc.Streams.Count);
    }

    [Fact]
    public void Multichannel_blocked_ima_decodes_each_channel()
    {
        var left = AwcWriter.Sweep(24000, 1.2, 300, 900);
        var right = AwcWriter.Sweep(24000, 1.2, 1200, 400);
        var awc = AwcFile.Parse(AwcWriter.Multichannel(new[] { new AwcWriter.Wave("music_l", left, 24000), new AwcWriter.Wave("music_r", right, 24000) }), null);
        Assert.True(awc.Multichannel);
        Assert.Equal(2, awc.Streams.Count);
        var l = awc.Streams[0].DecodePcm();
        var r = awc.Streams[1].DecodePcm();
        Assert.Equal(left.Length, l.Length);
        Assert.True(AdpcmTests.Snr(left, l) > 18, $"left SNR {AdpcmTests.Snr(left, l):F1}");
        Assert.True(AdpcmTests.Snr(right, r) > 18, $"right SNR {AdpcmTests.Snr(right, r):F1}");
    }

    [Fact]
    public void Garbage_is_rejected_cleanly()
    {
        Assert.Throws<InvalidDataException>(() => AwcFile.Parse(new byte[10], null));
        var b = AwcWriter.SfxPack(Waves);
        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(b, 8); // absurd stream count
        Assert.Throws<InvalidDataException>(() => AwcFile.Parse(b, null));
    }

    [Fact]
    public void Picker_follows_the_sheet_ids_then_siren_names_and_finds_horns()
    {
        var awc = AwcFile.Parse(AwcWriter.SfxPack(Waves), null);
        var siren = SoundPicker.PickSiren(awc);
        Assert.NotNull(siren);
        Assert.Contains("sheet id 0x0D329446 (siren_pa20a_wail)", siren!.Value.why);
        var horn = SoundPicker.PickHorn(awc);
        Assert.NotNull(horn);
        Assert.Contains("horn_std_1", horn!.Value.why);

        var other = AwcFile.Parse(AwcWriter.SfxPack(new[]
        {
            new AwcWriter.Wave("siren_wail_01", AwcWriter.Sweep(32000, 0.5, 600, 1200), 32000, 4, 0),
            new AwcWriter.Wave("siren_quick_01", AwcWriter.Sweep(32000, 0.9, 600, 1200), 32000, 4),
            new AwcWriter.Wave("unrelated", AwcWriter.Sweep(32000, 3, 600, 1200), 32000, 4, 0),
        }), null);
        var fallback = SoundPicker.PickSiren(other);
        Assert.NotNull(fallback);
        Assert.Contains("longest looping", fallback!.Value.why);
        Assert.Equal(Hashes.AwcStreamId("siren_wail_01"), fallback.Value.stream.Id);
        Assert.Null(SoundPicker.PickHorn(other));
        using var log = new Log(null);
        SoundPicker.Census(other, "test.awc", log); // must not throw
    }
}

public class OggTests
{
    [Theory]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(24000)]
    public void Siren_like_audio_encodes_to_mono_vorbis_that_decodes(int rate)
    {
        var pcm = AwcWriter.Sweep(rate, 1.0, 600, 1400);
        using var log = new Log(null);
        var ogg = OggEncode.Encode(pcm, rate, log, "test", out var detail);
        Assert.NotNull(ogg);
        Assert.True(Ogg.LooksLikeVorbis(ogg!));
        Assert.StartsWith("mono", detail);
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg!), true);
        Assert.Equal(1, reader.Channels);
        var buf = new float[reader.SampleRate * 2];
        int n = reader.ReadSamples(buf, 0, buf.Length);
        Assert.True(n > reader.SampleRate * 0.9, $"decoded {n} samples");
        // correlation with the source (same rate)
        if (reader.SampleRate == rate)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i < Math.Min(n, pcm.Length); i++) { double x = pcm[i] / 32768.0, y = buf[i]; xy += x * y; xx += x * x; yy += y * y; }
            Assert.True(xy / Math.Sqrt(xx * yy) > 0.9, $"correlation {xy / Math.Sqrt(xx * yy):F3}");
        }
    }

    [Fact]
    public void Encode_plan_tries_mono_first_and_stereo_last()
    {
        var plan = OggEncode.Plan(32000).ToList();
        Assert.Equal(1, plan[0].Channels);
        Assert.Equal(2, plan[^1].Channels);
        Assert.Contains(plan, a => a.Channels == 1 && a.Rate == 22050);
        using var log = new Log(null);
        Assert.Null(OggEncode.Encode(Array.Empty<short>(), 32000, log, "empty", out _));
    }
}
