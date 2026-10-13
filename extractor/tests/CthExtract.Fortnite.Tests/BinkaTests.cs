using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CthExtract.Fortnite.Tests.Synth;
using VgmstreamBinka;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>The C# port of vgmstream's Bink Audio decoder (vendor/vgmstream-binka).</summary>
public class BinkaTests
{
    /// <summary>(seed, rate, channels, SEEK chunk after packet, last-packet samples, SHA-256 of vgmstream-cli's PCM16 output).
    /// The hashes were produced by vgmstream-cli built from source at 7c520e8f (all external libs off) on the same
    /// synthetic streams; the C# decoder matched them sample for sample (40/40 configurations, 2026-10-13).</summary>
    public static readonly TheoryData<ulong, int, int, int, int, string> OracleStreams = new()
    {
        { 2, 8000, 2, 3, 0, "CC848CA3D7C247174BF3AFB3DEE5DF2C8CD6B66C1C1164A9C017B21D5270D093" },
        { 8, 16000, 3, -1, 100, "5845E80BA136F17932E9EEFDE84D7D117AB9C9F52FF4A9917DF4DFAA9F4B363D" },
        { 11, 22050, 1, -1, 0, "8E88A8BD0EA4BE97DF5AF25FED67FDBA9DB11DA2AD3846029C6093327DA09137" },
        { 24, 32000, 6, -1, 0, "D2AC8ABCEFDC6DBADE92EC07D88B16BD91B792119D96A678949A62B6533C04B9" },
        { 27, 44100, 2, 3, 0, "D06D29C53D7985263B736031FA9CC6C7DD605214E0ACE46D8F16E68B5E464B85" },
        { 31, 48000, 1, -1, 0, "1983ED1A40E10FF517FD6FE4A814313F4D959B4C6EA08414BD68ADE062388EA0" },
        { 35, 48000, 8, -1, 0, "68F2C447AA33C501E53B2C42F5C2FD7EFF2D6064EAA776CB6E0B3C2F73153244" },
        { 36, 96000, 1, -1, 0, "E302033458EC363697784814BD788E46F5C0FB352BA8F0F6073DAB47F9F1F20C" },
    };

    private static string Sha(short[] pcm) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan())));

    [Theory]
    [MemberData(nameof(OracleStreams))]
    public void Synthetic_streams_decode_exactly_like_vgmstream(ulong seed, int rate, int channels, int seekAfter, int lastSamples, string sha)
    {
        var data = SynthUeba.RandomStream(seed, rate, channels, 7, seekAfter, lastSamples);
        var audio = UebaReader.Decode(data);
        Assert.Null(audio.Error);
        Assert.Equal(channels, audio.Channels);
        Assert.Equal(rate, audio.SampleRate);
        Assert.Equal(audio.Samples, audio.DecodedSamples);
        Assert.Equal(7, audio.Packets);
        Assert.Equal(sha, Sha(audio.ToPcm16()));
    }

    /// <summary>Live oracle: when CTH_VGMSTREAM_CLI points at a vgmstream-cli built from source, compare directly.</summary>
    [Fact]
    public void Matches_a_live_vgmstream_cli_when_available()
    {
        var cli = Environment.GetEnvironmentVariable("CTH_VGMSTREAM_CLI");
        Assert.SkipWhen(string.IsNullOrEmpty(cli) || !File.Exists(cli), "set CTH_VGMSTREAM_CLI to a vgmstream-cli binary built from source");
        using var t = new TempDir();
        ulong seed = 100;
        foreach (var rate in new[] { 11025, 24000, 44100, 48000 })
            foreach (var ch in new[] { 1, 2, 5 })
            {
                var data = SynthUeba.RandomStream(seed++, rate, ch, 5, ch == 2 ? 2 : -1, ch == 5 ? 333 : 0);
                var f = Path.Combine(t.Path, $"s{seed}.binka");
                File.WriteAllBytes(f, data);
                using (var p = Process.Start(new ProcessStartInfo(cli!, $"-o \"{f}.wav\" \"{f}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!)
                    p.WaitForExit();
                var expected = SynthWav.ReadPcm16(File.ReadAllBytes(f + ".wav"));
                Assert.Equal(expected, UebaReader.Decode(data).ToPcm16());
            }
    }

    [Fact]
    public void Dc_only_first_frame_is_a_constant_of_dc_times_scale()
    {
        foreach (var rate in new[] { 16000, 32000, 48000 })
        {
            int fs = SynthUeba.FrameSamples(rate);
            var ch = new SynthUeba.ChannelSpec
            {
                Dc0 = SynthUeba.Float29(1000, 23, false),
                Dc1 = SynthUeba.Float29(0, 23, false),
                ScaleIndex = new int[SynthUeba.BandCount(rate)],
            };
            var pkt = SynthUeba.Packet([ch], rate, new Prng(1));
            var h = BinkaHandle.Init(rate, 1, BinkaMode.Ueba)!;
            var outp = new float[h.FrameSamples];
            Assert.Equal(h.FrameSamples, h.Decode(pkt, outp));
            float expected = 1000f * (float)(2.0 / Math.Sqrt(fs));
            foreach (var s in outp) Assert.Equal(expected, s, 2);
        }
    }

    /// <summary>The ported over-optimised inverse transform equals a plain DCT-III (vgmstream's own reference formula):
    /// x[n] = scale * (c0 + sum_k c_k cos(pi k (2n+1) / 2N)).</summary>
    [Theory]
    [InlineData(48000, 1, 0)]
    [InlineData(48000, 0, 37)]
    [InlineData(48000, 0, 1500)]
    [InlineData(24000, 0, 300)]
    [InlineData(16000, 0, 77)]
    public void Single_coefficient_matches_a_plain_dct_iii(int rate, int dc1, int k)
    {
        int fs = SynthUeba.FrameSamples(rate);
        var ch = new SynthUeba.ChannelSpec
        {
            Dc0 = SynthUeba.Float29(0, 23, false),
            Dc1 = SynthUeba.Float29(dc1 * 700, 23, false),
            ScaleIndex = new int[SynthUeba.BandCount(rate)], // all scale factors 1.0
            Values = new int[fs],
            Negative = new bool[fs],
        };
        if (k >= 2) ch.Values[k] = 700;
        int kk = k >= 2 ? k : 1;
        var pkt = SynthUeba.Packet([ch], rate, new Prng(5));
        var h = BinkaHandle.Init(rate, 1, BinkaMode.Ueba)!;
        var outp = new float[h.FrameSamples];
        Assert.Equal(h.FrameSamples, h.Decode(pkt, outp));
        double scale = 2.0 / Math.Sqrt(fs);
        double maxErr = 0;
        for (int n = 0; n < h.FrameSamples; n++)
        {
            double expected = scale * 700 * Math.Cos(Math.PI * kk * (2 * n + 1) / (2.0 * fs));
            maxErr = Math.Max(maxErr, Math.Abs(outp[n] - expected));
        }
        Assert.True(maxErr < 0.01, $"max error {maxErr} (amplitude {700 * scale:0.##})");
    }

    [Fact]
    public void Truncated_stream_reports_how_far_it_got_and_pads_with_silence()
    {
        var data = SynthUeba.RandomStream(3, 48000, 1, 6);
        var cut = data.AsSpan(0, data.Length - 700).ToArray();
        var audio = UebaReader.Decode(cut);
        Assert.NotNull(audio.Error);
        Assert.Equal(5 * 1920, audio.DecodedSamples);
        Assert.Equal(6 * 1920, audio.Samples);
        Assert.All(audio.Interleaved.AsSpan(5 * 1920).ToArray(), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Bad_headers_are_rejected()
    {
        var good = SynthUeba.RandomStream(4, 48000, 2, 2);
        Assert.Throws<InvalidDataException>(() => UebaReader.Decode(good.AsSpan(0, 10)));
        var badMagic = (byte[])good.Clone(); badMagic[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => UebaReader.Decode(badMagic));
        var badVersion = (byte[])good.Clone(); badVersion[4] = 2;
        Assert.Throws<InvalidDataException>(() => UebaReader.Decode(badVersion));
        var badChannels = (byte[])good.Clone(); badChannels[5] = 0;
        Assert.Throws<InvalidDataException>(() => UebaReader.Decode(badChannels));
        Assert.Null(BinkaHandle.Init(200, 1, BinkaMode.Ueba));
        Assert.Null(BinkaHandle.Init(48000, 9, BinkaMode.Ueba));
        // garbage packet: decoder error, not an exception
        var h = BinkaHandle.Init(48000, 1, BinkaMode.Ueba)!;
        Assert.True(h.Decode(new byte[8], new float[1920]) < 0);
    }

    [Fact]
    public void Missing_sync_stops_decoding_with_a_reason()
    {
        var data = SynthUeba.RandomStream(6, 32000, 1, 3);
        data[0x1c] = 0x12; // first packet header's sync
        var audio = UebaReader.Decode(data);
        Assert.Contains("sync", audio.Error);
        Assert.Equal(0, audio.DecodedSamples);
    }
}
