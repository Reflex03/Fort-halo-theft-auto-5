using OggVorbisEncoder;

namespace CthExtract.Core;

/// <summary>
/// The one PCM -> Ogg Vorbis encoder of the extractor (OggVorbisEncoder 1.2.2, MIT C# port of libvorbis), shared by the
/// Halo PCM-bank fallback, the GTA AWC sounds and the Fortnite Bink/PCM/ADPCM sounds. Every result is decoded again
/// with NVorbis 0.10.5 (MIT) and compared with the source at lag 0 before it is accepted.
/// Measured on synthetic audio (synthetic-test, extractor/tests/CthExtract.Cli.Tests/OggVorbisTests.cs):
/// <list type="bullet">
/// <item>the encoder drops the first BlockSizes[1]/2 input samples (1024 at 32-48 kHz q0.1, 2048 at q-0.1, 512 at
/// 22.05 kHz), i.e. the attack of a gun sound: the input is padded with exactly that much silence at the start, and
/// a quarter of it at the end, so the decoded sound starts sample-accurately and ends with at most a few ms of
/// silence;</item>
/// <item>stereo throws DivideByZeroException at 32-48 kHz for every quality, and some mono q &gt;= 0.2 and q &lt;= 0.0
/// settings produce streams NVorbis rejects or that decode to unrelated noise: hence a ladder of attempts, each
/// decode-verified.</item>
/// </list>
/// Minecraft positions mono sounds only, so mono is preferred wherever the caller allows it.
/// </summary>
public static class OggVorbis
{
    /// <summary>One encoder attempt: output channel count, sample rate and VBR quality.</summary>
    public sealed record Attempt(int Channels, int Rate, float Quality);

    /// <summary>Attempts for a mono source (GTA, Fortnite): mono at the source rate, mono at 22.05 kHz, then the mono
    /// signal duplicated into stereo as the last resort.</summary>
    public static IEnumerable<Attempt> MonoPlan(int rate)
    {
        foreach (var q in new[] { 0.1f, 0.0f, 0.2f, -0.1f }) yield return new Attempt(1, rate, q);
        if (rate != 22050) foreach (var q in new[] { 0.1f, 0.3f }) yield return new Attempt(1, 22050, q);
        foreach (var q in new[] { 0.1f, 0.3f }) yield return new Attempt(2, rate, q);
    }

    /// <summary>Attempts for a multi-channel source that must keep its sample rate (Halo PCM banks): the source channel
    /// count first, then a mono down-mix at the same rate.</summary>
    public static IEnumerable<Attempt> KeepRatePlan(int channels, int rate)
    {
        if (channels > 1) foreach (var q in new[] { 0.1f, 0.0f, -0.1f, 0.3f }) yield return new Attempt(channels, rate, q);
        foreach (var q in new[] { 0.1f, 0.0f, 0.2f, -0.1f }) yield return new Attempt(1, rate, q);
    }

    /// <summary>Encode a mono signal in [-1, 1]. Null (with the reasons in <paramref name="detail"/>) when every attempt
    /// failed; <paramref name="detail"/> otherwise names the attempt that was used.</summary>
    public static byte[]? EncodeMono(float[] mono, int rate, Log? log, string what, out string detail) =>
        Run([mono], rate, MonoPlan(rate), log, what, out detail, out _);

    /// <summary>Encode planar channels in [-1, 1] at their own sample rate (see <see cref="KeepRatePlan"/>).
    /// <paramref name="outChannels"/> is the channel count of the result (the source's, or 1 after a down-mix).</summary>
    public static byte[]? EncodeKeepRate(float[][] channels, int rate, Log? log, string what, out string detail, out int outChannels) =>
        Run(channels, rate, KeepRatePlan(channels.Length, rate), log, what, out detail, out outChannels);

    private static byte[]? Run(float[][] channels, int rate, IEnumerable<Attempt> plan, Log? log, string what, out string detail, out int outChannels)
    {
        outChannels = 0;
        if (channels.Length == 0 || channels[0].Length == 0 || rate <= 0) { detail = "no samples"; return null; }
        if (channels.Any(c => c.Length != channels[0].Length)) { detail = "channels differ in length"; return null; }
        float[]? mono = null;
        var failures = new List<string>();
        foreach (var a in plan)
        {
            string tag = $"{a.Channels}ch {a.Rate} Hz q{a.Quality:0.0}";
            try
            {
                float[][] src;
                if (a.Channels == channels.Length && a.Rate == rate) src = channels;
                else
                {
                    mono ??= channels.Length == 1 ? channels[0] : DownMix(channels);
                    var m = a.Rate == rate ? mono : Resample(mono, rate, a.Rate);
                    src = Enumerable.Repeat(m, a.Channels).ToArray();
                }
                var ogg = EncodeFloat(src, a.Rate, a.Quality);
                if (!Ogg.LooksLikeVorbis(ogg)) { failures.Add($"{tag}: output not Vorbis"); continue; }
                var check = Verify(ogg, src, a.Rate);
                if (check is not null) { failures.Add($"{tag}: {check}"); continue; }
                string layout = a.Channels == channels.Length ? (a.Channels == 1 ? "mono" : $"{a.Channels}ch")
                    : a.Channels == 1 ? $"mono (down-mixed from {channels.Length}ch)" : $"{a.Channels}ch (mono encodes failed)";
                detail = $"{layout} {a.Rate} Hz q{a.Quality:0.0}, {channels[0].Length / (double)rate:F2} s, decode-verified";
                if (failures.Count > 0) log?.Warn($"{what}: encoder retries: {string.Join("; ", failures)}");
                outChannels = a.Channels;
                return ogg;
            }
            catch (Exception e) when (e is DivideByZeroException or IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                          or NullReferenceException or OverflowException or InvalidDataException)
            {
                failures.Add($"{tag}: {e.GetType().Name}");
            }
        }
        detail = "Vorbis encoding failed: " + string.Join("; ", failures);
        return null;
    }

    /// <summary>Decode with NVorbis and compare channel 0 with the source at lag 0: null when it matches, else why.</summary>
    public static string? Verify(byte[] ogg, float[][] src, int rate)
    {
        int channels = src.Length;
        var s0 = src[0];
        float[] dec;
        try
        {
            using var r = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
            if (r.Channels != channels || r.SampleRate != rate) return $"decoder sees {r.Channels}ch {r.SampleRate} Hz";
            var inter = new float[(s0.Length + 8192) * channels];
            int got = 0, n;
            while (got < inter.Length && (n = r.ReadSamples(inter, got, inter.Length - got)) > 0) got += n;
            dec = new float[got / channels];
            for (int i = 0; i < dec.Length; i++) dec[i] = inter[i * channels];
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                      or NullReferenceException or DivideByZeroException or OverflowException)
        {
            return $"does not decode ({e.GetType().Name})";
        }
        foreach (var f in dec) if (!float.IsFinite(f)) return "decodes to non-finite samples";
        if (dec.Length < s0.Length * 0.98 - 64) return $"decodes to {dec.Length} of {s0.Length} samples";
        double energy = 0;
        foreach (var s in s0) energy += s * s;
        if (energy / Math.Max(1, s0.Length) < 1e-7) return null; // (near) silence: nothing to correlate
        int len = Math.Min(s0.Length, dec.Length);
        double xy = 0, xx = 0, yy = 0;
        for (int i = 0; i < len; i++) { xy += s0[i] * dec[i]; xx += s0[i] * s0[i]; yy += dec[i] * dec[i]; }
        double c = xy / Math.Sqrt(xx * yy + 1e-20);
        return c >= 0.85 ? null : $"decoded audio does not match the source (correlation {c:F2})";
    }

    /// <summary>Average of the channels.</summary>
    public static float[] DownMix(float[][] channels)
    {
        var o = new float[channels[0].Length];
        foreach (var c in channels)
            for (int i = 0; i < o.Length; i++) o[i] += c[i];
        float k = 1f / channels.Length;
        for (int i = 0; i < o.Length; i++) o[i] *= k;
        return o;
    }

    public static float[] ToFloat(short[] pcm)
    {
        var f = new float[pcm.Length];
        for (int i = 0; i < f.Length; i++) f[i] = pcm[i] / 32768f;
        return f;
    }

    /// <summary>Linear-interpolation resampler (only used for the 22.05 kHz retry).</summary>
    public static float[] Resample(float[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        int n = (int)((long)src.Length * to / from);
        var o = new float[Math.Max(1, n)];
        double step = (double)from / to;
        for (int i = 0; i < o.Length; i++)
        {
            double p = i * step;
            int k = (int)p;
            float t = (float)(p - k);
            float a = src[Math.Min(k, src.Length - 1)], b = src[Math.Min(k + 1, src.Length - 1)];
            o[i] = a + (b - a) * t;
        }
        return o;
    }

    /// <summary>Lead-in the encoder drops (BlockSizes[1]/2) for these settings.</summary>
    public static int LeadIn(int channels, int rate, float quality)
    {
        var info = VorbisInfo.InitVariableBitRate(channels, rate, quality);
        return info.CodecSetup?.BlockSizes is { Length: > 1 } bs ? bs[1] / 2 : 1024;
    }

    /// <summary>Encode planar channels as given (no verification). The input is padded with BlockSizes[1]/2 samples of
    /// silence at the start (the encoder drops that much) and a quarter of that at the end.
    /// <paramref name="padLeadIn"/> = false only exists to measure the encoder's behaviour in tests.</summary>
    public static byte[] EncodeFloat(float[][] channels, int rate, float quality, bool padLeadIn = true)
    {
        int ch = channels.Length;
        using var output = new MemoryStream();
        var info = VorbisInfo.InitVariableBitRate(ch, rate, quality);
        int lead = !padLeadIn ? 0 : info.CodecSetup?.BlockSizes is { Length: > 1 } bs ? bs[1] / 2 : 1024;
        int frames = channels[0].Length;
        var data = new float[ch][];
        for (int c = 0; c < ch; c++)
        {
            data[c] = new float[lead + frames + lead / 4];
            Array.Copy(channels[c], 0, data[c], lead, frames);
        }

        var stream = new OggStream(unchecked((int)(0x43544800u ^ (uint)(rate * 31 + frames * 7 + ch))));
        var comments = new Comments();
        comments.AddTag("ENCODER", "cthextract (OggVorbisEncoder)");
        stream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        stream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        stream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        Flush(stream, output, force: true);

        var state = ProcessingState.Create(info);
        const int Chunk = 1024;
        int total = data[0].Length;
        for (int pos = 0; pos < total; pos += Chunk)
        {
            state.WriteData(data, Math.Min(Chunk, total - pos), pos);
            Drain(state, stream, output);
        }
        state.WriteEndOfStream();
        Drain(state, stream, output);
        Flush(stream, output, force: true);
        return output.ToArray();
    }

    private static void Drain(ProcessingState state, OggStream stream, Stream output)
    {
        while (!stream.Finished && state.PacketOut(out var packet))
        {
            stream.PacketIn(packet);
            Flush(stream, output, force: false);
        }
    }

    private static void Flush(OggStream stream, Stream output, bool force)
    {
        while (stream.PageOut(out var page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }
}
