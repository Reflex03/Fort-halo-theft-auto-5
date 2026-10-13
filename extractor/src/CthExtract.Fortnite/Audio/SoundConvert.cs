using CthExtract.Core;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse_Conversion.Sounds;
using VgmstreamBinka;

namespace CthExtract.Fortnite.Audio;

/// <summary>Outcome of turning one SoundWave into an .ogg for the pack.</summary>
public sealed record SoundResult(byte[]? Ogg, bool Unsupported, string Detail);

/// <summary>
/// SoundWave payload -> Ogg Vorbis. OGG is kept as it is; BINKA ("ABEU", Unreal's Bink Audio 2) is decoded with the
/// vgmstream port; PCM/ADPCM WAV is decoded; all decoded audio is down-mixed to mono and encoded. RADA (RAD Audio)
/// has no open decoder and OPUS (UEOP) is not handled: those are reported as unsupported so the mod keeps its
/// placeholder sound.
/// </summary>
public static class SoundConvert
{
    public const double MinSeconds = 0.05, MaxSeconds = 30;

    public static SoundResult FromWave(UObject wave)
    {
        string fmt;
        byte[]? data;
        try { wave.Decode(true, out fmt, out data); }
        catch (Exception e) { return new SoundResult(null, false, $"{wave.Name}: decode failed: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        return FromPayload(wave.Name, fmt, data);
    }

    public static SoundResult FromPayload(string name, string? fmt, byte[]? data)
    {
        if (data == null || data.Length == 0)
            return new SoundResult(null, false, $"{name}: no audio data (format '{fmt}'; streamed chunks not installed?)");
        var f = (fmt ?? "").ToUpperInvariant();
        var magic = data.Length >= 4 ? System.Text.Encoding.ASCII.GetString(data, 0, 4).Replace('\0', '.') : "";
        try
        {
            switch (f)
            {
                case "OGG":
                {
                    if (!Ogg.LooksLikeVorbis(data)) return new SoundResult(null, false, $"{name}: OGG payload is not Ogg Vorbis (magic '{magic}')");
                    var info = OggEncode.Verify(data, out var why);
                    if (info == null) return new SoundResult(null, false, $"{name}: OGG payload does not decode: {why}");
                    if (info.Value.Seconds is < MinSeconds or > MaxSeconds) return new SoundResult(null, false, $"{name}: {info.Value.Seconds:0.00} s is outside {MinSeconds}-{MaxSeconds} s");
                    return new SoundResult(data, false, $"{name}: OGG kept as-is ({info.Value.Channels}ch {info.Value.SampleRate} Hz {info.Value.Seconds:0.00} s{(info.Value.Channels > 1 ? "; stereo plays non-positionally in Minecraft" : "")})");
                }
                case "BINKA":
                {
                    if (!UebaReader.IsUeba(data)) return new SoundResult(null, false, $"{name}: BINKA payload without UEBA header (magic '{magic}')");
                    var audio = UebaReader.Decode(data);
                    if (audio.Error != null && audio.DecodedSamples < audio.Samples * 9 / 10)
                        return new SoundResult(null, false, $"{name}: Bink Audio decode stopped at {audio.DecodedSamples}/{audio.Samples} samples: {audio.Error}");
                    var pcm = ToPcm(audio);
                    return Encode(name, pcm, $"Bink Audio {audio.Channels}ch {audio.SampleRate} Hz, {audio.Packets} packets{(audio.Error != null ? $" (tail: {audio.Error})" : "")}");
                }
                case "WAV":
                case "PCM":
                case "ADPCM":
                {
                    var pcm = WavDecode.Decode(data);
                    return Encode(name, pcm, $"{f} {pcm.Channels.Length}ch {pcm.SampleRate} Hz");
                }
                case "RADA":
                    return new SoundResult(null, true, $"{name}: RAD Audio (RADA) has no open-source decoder; keeping the placeholder");
                case "OPUS":
                    return new SoundResult(null, true, $"{name}: Opus (UEOP) sound waves are not supported; keeping the placeholder");
                default:
                    return new SoundResult(null, true, $"{name}: audio format '{fmt}' (magic '{magic}') is not supported");
            }
        }
        catch (Exception e)
        {
            return new SoundResult(null, false, $"{name}: {f} conversion failed: {e.GetType().Name}: {e.Message}");
        }
    }

    public static Pcm ToPcm(UebaAudio audio)
    {
        int ch = audio.Channels, n = audio.Samples;
        var o = new float[ch][];
        for (int c = 0; c < ch; c++) o[c] = new float[n];
        var s = audio.Interleaved;
        for (int i = 0; i < n; i++)
            for (int c = 0; c < ch; c++)
                o[c][i] = Math.Clamp(s[i * ch + c] / 32768f, -1f, 1f);
        return new Pcm(o, audio.SampleRate);
    }

    private static SoundResult Encode(string name, Pcm pcm, string what)
    {
        if (pcm.Seconds is < MinSeconds or > MaxSeconds)
            return new SoundResult(null, false, $"{name}: {pcm.Seconds:0.00} s is outside {MinSeconds}-{MaxSeconds} s");
        var peak = pcm.Peak();
        if (peak < 1e-4f) return new SoundResult(null, false, $"{name}: decoded audio is silent");
        var mono = pcm.ToMono();
        var ogg = OggEncode.FromPcm(mono.Channels, mono.SampleRate, out var enc);
        return new SoundResult(ogg, false, $"{name}: {what}, {pcm.Seconds:0.00} s, peak {peak:0.00} -> mono {enc}");
    }
}
