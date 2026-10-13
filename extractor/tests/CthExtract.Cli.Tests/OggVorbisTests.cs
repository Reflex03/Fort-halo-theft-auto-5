using CthExtract.Core;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>The shared encoder (Core OggVorbis) keeps a sound's attack and timing: synthetic noise bursts only.</summary>
public class OggVorbisTests
{
    /// <summary>Silence, then a noise burst starting at <paramref name="onset"/> (non-periodic, so the lag is unambiguous).</summary>
    private static float[] Burst(int rate, int onset, int seed = 7)
    {
        var rng = new Random(seed);
        var x = new float[rate];
        for (int i = onset; i < x.Length; i++) x[i] = 0.15f * (float)(rng.NextDouble() * 2 - 1) + 0.15f * MathF.Sin(2 * MathF.PI * 300 * i / rate);
        return x;
    }

    private static float[] DecodeChannel0(byte[] ogg, out int channels, out int rate)
    {
        using var r = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        channels = r.Channels; rate = r.SampleRate;
        var all = new List<float>();
        var buf = new float[4096 * channels];
        int n;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i += channels) all.Add(buf[i]);
        return all.ToArray();
    }

    /// <summary>Lag (decoded index - source index) with the highest correlation.</summary>
    private static int BestLag(float[] src, float[] dec, int maxLag)
    {
        int best = 0; double bestC = double.MinValue;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 4000; i < src.Length - maxLag - 1; i += 2)
            {
                int j = i + lag;
                if (j < 0 || j >= dec.Length) continue;
                xy += src[i] * dec[j]; xx += src[i] * src[i]; yy += dec[j] * dec[j];
            }
            double c = xy / Math.Sqrt(xx * yy + 1e-20);
            if (c > bestC) { bestC = c; best = lag; }
        }
        return best;
    }

    [Theory]
    [InlineData(22050)]
    [InlineData(32000)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Mono_output_is_sample_aligned_and_keeps_the_attack(int rate)
    {
        const int onset = 1500;
        var src = Burst(rate, onset);
        var ogg = OggVorbis.EncodeMono(src, rate, null, "test", out var detail);
        Assert.True(ogg != null, detail);
        Assert.StartsWith("mono", detail);
        var dec = DecodeChannel0(ogg!, out int ch, out int sr);
        Assert.Equal((1, rate), (ch, sr));
        Assert.Equal(0, BestLag(src, dec, 2100));
        // silence before the onset stays silent (apart from transform pre-echo within half a long block), and the
        // burst starts where it did; without the lead-in padding it would start ~1024 samples early
        Assert.True(dec.Take(onset - 600).All(v => Math.Abs(v) < 0.02f), "energy before the onset");
        Assert.Contains(dec.Skip(onset).Take(256), v => Math.Abs(v) > 0.05f);
        // whole length plus at most a few ms of encoder padding
        Assert.InRange(dec.Length, src.Length, src.Length + 1024);
    }

    [Fact]
    public void The_raw_encoder_drops_its_lead_in_which_is_why_we_pad()
    {
        // documents the measurement behind the padding: without it, the decoded audio starts BlockSizes[1]/2 late
        const int rate = 44100;
        var src = Burst(rate, 300);
        int lead = OggVorbis.LeadIn(1, rate, 0.1f);
        Assert.Equal(1024, lead);
        var raw = DecodeChannel0(OggVorbis.EncodeFloat([src], rate, 0.1f, padLeadIn: false), out _, out _);
        Assert.Equal(-lead, BestLag(src, raw, 2100));
        Assert.Equal(src.Length - lead, raw.Length);
        var padded = DecodeChannel0(OggVorbis.EncodeFloat([src], rate, 0.1f), out _, out _);
        Assert.Equal(0, BestLag(src, padded, 2100));
    }

    [Theory]
    [InlineData(22050)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Keep_rate_keeps_the_sample_rate_and_falls_back_to_mono_for_stereo(int rate)
    {
        var l = Burst(rate, 300, 1);
        var r = Burst(rate, 300, 2);
        var ogg = OggVorbis.EncodeKeepRate([l, r], rate, null, "test", out var detail, out int outCh);
        Assert.True(ogg != null, detail);
        var dec = DecodeChannel0(ogg!, out int ch, out int sr);
        Assert.Equal((outCh, rate), (ch, sr));
        Assert.InRange(outCh, 1, 2);
        var reference = outCh == 1 ? OggVorbis.DownMix([l, r]) : l;
        Assert.Equal(0, BestLag(reference, dec, 2100));
    }

    [Fact]
    public void Nothing_to_encode_is_reported_not_thrown()
    {
        Assert.Null(OggVorbis.EncodeMono([], 44100, null, "t", out var d1));
        Assert.Equal("no samples", d1);
        Assert.Null(OggVorbis.EncodeKeepRate([new float[10], new float[9]], 44100, null, "t", out var d2, out _));
        Assert.Contains("differ", d2);
    }

    [Fact]
    public void Verify_rejects_a_stream_that_does_not_match_the_source()
    {
        const int rate = 32000;
        var a = Burst(rate, 300, 3);
        var b = Burst(rate, 300, 4);
        var ogg = OggVorbis.EncodeMono(a, rate, null, "t", out _)!;
        Assert.Null(OggVorbis.Verify(ogg, [a], rate));
        Assert.Contains("does not match", OggVorbis.Verify(ogg, [b], rate));
        Assert.Contains("decoder sees", OggVorbis.Verify(ogg, [a], 44100));
    }
}
