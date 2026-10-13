using NVorbis;

namespace CthExtract.Fortnite.Audio;

/// <summary>
/// PCM -> Ogg Vorbis through the shared, decode-verified encoder in Core (<see cref="CthExtract.Core.OggVorbis"/>:
/// OggVorbisEncoder drops the first BlockSizes[1]/2 samples, so the input is padded at the start; every result is
/// decoded with NVorbis and compared with the source). <see cref="Verify"/> checks a finished file.
/// </summary>
public static class OggEncode
{
    /// <summary>Upper bound of the silence the encoder adds at the end (samples per channel).</summary>
    public const int TailPad = 2048;

    /// <summary>Encode planar float channels in [-1, 1] at their sample rate (mono stays mono; more channels are kept
    /// when the encoder manages them, else down-mixed to mono).</summary>
    public static byte[] FromPcm(float[][] channels, int sampleRate, out string detail)
    {
        if (channels.Length == 0 || channels[0].Length == 0) throw new ArgumentException("no samples");
        if (sampleRate < 8000 || sampleRate > 192000) throw new ArgumentException($"sample rate {sampleRate}");
        var ogg = channels.Length == 1
            ? CthExtract.Core.OggVorbis.EncodeMono(channels[0], sampleRate, null, "[fortnite]", out var why)
            : CthExtract.Core.OggVorbis.EncodeKeepRate(channels, sampleRate, null, "[fortnite]", out why, out _);
        if (ogg is null) throw new InvalidOperationException(why);
        detail = "Vorbis " + why;
        return ogg;
    }

    public readonly record struct OggInfo(int Channels, int SampleRate, long Samples, double Seconds, float Peak);

    /// <summary>Decode an Ogg Vorbis file completely; null (with the reason) when it is not playable.</summary>
    public static OggInfo? Verify(byte[] ogg, out string why)
    {
        try
        {
            using var r = new VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
            if (r.Channels < 1 || r.SampleRate < 1000) { why = $"{r.Channels} channels, {r.SampleRate} Hz"; return null; }
            var buf = new float[4096 * r.Channels];
            long total = 0; float peak = 0;
            int n;
            while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    float a = Math.Abs(buf[i]);
                    if (!float.IsFinite(a)) { why = "non-finite samples"; return null; }
                    if (a > peak) peak = a;
                }
                total += n / r.Channels;
                if (total > (long)r.SampleRate * 600) { why = "longer than 10 minutes"; return null; }
            }
            if (total == 0) { why = "no samples"; return null; }
            why = "";
            return new OggInfo(r.Channels, r.SampleRate, total, total / (double)r.SampleRate, peak);
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }
}
