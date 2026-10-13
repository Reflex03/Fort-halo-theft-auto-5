using CthExtract.Core;

namespace CthExtract.Gta.Audio;

/// <summary>
/// PCM16 -> mono Ogg Vorbis for AWC sounds. The encoder itself (head padding for OggVorbisEncoder's dropped lead-in,
/// the attempt ladder and the NVorbis decode check) moved to <see cref="OggVorbis"/> in Core, so Halo, GTA and Fortnite
/// share one verified implementation.
/// </summary>
public static class OggEncode
{
    public static IEnumerable<OggVorbis.Attempt> Plan(int rate) => OggVorbis.MonoPlan(rate);

    public static byte[]? Encode(short[] pcm, int rate, Log log, string what, out string detail) =>
        OggVorbis.EncodeMono(OggVorbis.ToFloat(pcm), rate, log, $"[gta] {what}", out detail);
}
