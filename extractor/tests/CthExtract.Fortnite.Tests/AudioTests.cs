using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CthExtract.Fortnite.Audio;
using CthExtract.Fortnite.Tests.Synth;
using Xunit;

namespace CthExtract.Fortnite.Tests;

public class AudioTests
{
    private static short[] Interleave(Pcm p)
    {
        int ch = p.Channels.Length;
        var o = new short[p.Samples * ch];
        for (int i = 0; i < p.Samples; i++)
            for (int c = 0; c < ch; c++)
                o[i * ch + c] = (short)Math.Round(p.Channels[c][i] * 32768f);
        return o;
    }

    [Fact]
    public void Pcm16_wav_round_trips()
    {
        var pcm = SynthWav.Sine(4410, 44100, 440, channels: 2);
        var decoded = WavDecode.Decode(SynthWav.Pcm16(pcm, 2, 44100));
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels.Length);
        Assert.Equal(pcm, Interleave(decoded));
    }

    /// <summary>(seed, channels, rate, block align, blocks, tail bytes, SHA-256 of vgmstream-cli's PCM16 output for the
    /// same random MS-ADPCM stream). Our decoder matched vgmstream (SHR predictor, like Microsoft's msadpcm.c) exactly.</summary>
    public static readonly TheoryData<ulong, int, int, int, int, int, string> AdpcmOracle = new()
    {
        { 1, 1, 22050, 512, 6, 0, "C9615BD8008DA87040BC2B3C20B0E67E01967B2F12B06CCEF819B91B342B2C50" },
        { 2, 2, 44100, 1024, 5, 300, "377062E9634F34DB4B89052D9D7C6BC757B18A93B741734EAC1DD78860FDBDD8" },
        { 3, 1, 48000, 36, 40, 0, "384DABCFAE754D569DEF05346025185DAB00903EF951B82111D4742CD99021A5" },
        { 4, 2, 16000, 2048, 3, 77, "BA9D23290401EDF359EA4AF7323823E4CBC96C79B84379D9911733AA2099CC33" },
    };

    [Theory]
    [MemberData(nameof(AdpcmOracle))]
    public void Ms_adpcm_decodes_exactly_like_vgmstream(ulong seed, int ch, int rate, int align, int blocks, int tail, string sha)
    {
        var wav = SynthWav.RandomMsAdpcm(seed, ch, rate, align, blocks, tail);
        var pcm = WavDecode.Decode(wav);
        Assert.Equal(ch, pcm.Channels.Length);
        Assert.Equal(rate, pcm.SampleRate);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(Interleave(pcm).AsSpan()))));
    }

    [Fact]
    public void Broken_wavs_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => WavDecode.Decode(new byte[20]));
        var w = SynthWav.Pcm16(new short[100], 1, 8000);
        w[20] = 0x55; // unknown format tag
        Assert.Throws<NotSupportedException>(() => WavDecode.Decode(w));
    }

    [Fact]
    public void Mono_downmix_averages_channels()
    {
        var p = new Pcm([[1f, 0.5f], [0f, -0.5f]], 8000).ToMono();
        Assert.Single(p.Channels);
        Assert.Equal([0.5f, 0f], p.Channels[0]);
    }

    [Theory]
    [InlineData(1, 48000)]
    [InlineData(1, 44100)]
    [InlineData(2, 22050)]
    public void Ogg_encoder_output_decodes_back_to_the_same_length(int channels, int rate)
    {
        int n = rate; // 1 s
        var ch = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            ch[c] = new float[n];
            for (int i = 0; i < n; i++) ch[c][i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * (c + 1) * i / rate);
        }
        var ogg = OggEncode.FromPcm(ch, rate, out var detail);
        Assert.True(CthExtract.Core.Ogg.LooksLikeVorbis(ogg));
        var info = OggEncode.Verify(ogg, out var why);
        Assert.True(info != null, why);
        Assert.Equal(channels, info!.Value.Channels);
        Assert.Equal(rate, info.Value.SampleRate);
        Assert.InRange(info.Value.Samples, n, n + OggEncode.TailPad + 4096); // the whole sound plus a short silent tail
        Assert.InRange(info.Value.Peak, 0.35f, 0.75f);
        Assert.Contains("Vorbis", detail);
    }

    [Fact]
    public void Sound_payloads_are_converted_or_reported()
    {
        // BINKA -> mono ogg
        var binka = SynthUeba.RandomStream(9, 48000, 2, 30);
        var r = SoundConvert.FromPayload("SW_Test", "BINKA", binka);
        Assert.True(r.Ogg != null, r.Detail);
        Assert.Equal(1, OggEncode.Verify(r.Ogg!, out _)!.Value.Channels);
        Assert.Contains("Bink Audio 2ch 48000 Hz", r.Detail);

        // OGG kept byte for byte
        var ogg = r.Ogg!;
        var kept = SoundConvert.FromPayload("SW_Ogg", "OGG", ogg);
        Assert.Same(ogg, kept.Ogg);
        Assert.Contains("kept as-is", kept.Detail);

        // WAV / ADPCM
        var wav = SoundConvert.FromPayload("SW_Wav", "WAV", SynthWav.Pcm16(SynthWav.Sine(8000, 16000, 300), 1, 16000));
        Assert.NotNull(wav.Ogg);
        var adpcm = SoundConvert.FromPayload("SW_Adpcm", "ADPCM", SynthWav.RandomMsAdpcm(7, 1, 22050, 512, 20));
        Assert.NotNull(adpcm.Ogg);

        // no decoder: unsupported (-> Missing), not a failure
        var rada = SoundConvert.FromPayload("SW_Rada", "RADA", [0x41, 0x44, 0x41, 0x52, 1, 2, 3, 4]);
        Assert.Null(rada.Ogg);
        Assert.True(rada.Unsupported);
        Assert.Contains("RAD Audio", rada.Detail);
        Assert.True(SoundConvert.FromPayload("SW_Opus", "OPUS", [0x55, 0x45, 0x4F, 0x50]).Unsupported);

        // garbage: failure with a reason, never an exception
        var bad = SoundConvert.FromPayload("SW_Bad", "OGG", new byte[100]);
        Assert.Null(bad.Ogg);
        Assert.False(bad.Unsupported);
        Assert.Contains("not Ogg Vorbis", bad.Detail);
        Assert.False(SoundConvert.FromPayload("SW_Empty", "BINKA", null).Unsupported);
        var silent = SoundConvert.FromPayload("SW_Silent", "WAV", SynthWav.Pcm16(new short[16000], 1, 16000));
        Assert.Contains("silent", silent.Detail);
        var tooShort = SoundConvert.FromPayload("SW_Short", "WAV", SynthWav.Pcm16(SynthWav.Sine(100, 16000, 300), 1, 16000));
        Assert.Contains("outside", tooShort.Detail);
    }
}
