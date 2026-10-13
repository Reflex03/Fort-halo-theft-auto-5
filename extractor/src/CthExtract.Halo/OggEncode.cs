namespace CthExtract.Halo;

/// <summary>
/// PCM -> Ogg Vorbis, only for banks that are not already Vorbis (MCC's sfx.fsb is Vorbis and is remuxed losslessly
/// instead). Uses the shared, decode-verified encoder in Core (<see cref="CthExtract.Core.OggVorbis"/>): it pads the
/// encoder's dropped lead-in (so the attack of a gun sound survives), keeps the bank's sample rate and channel count
/// when OggVorbisEncoder manages them (it throws for stereo at 32-48 kHz) and otherwise down-mixes to mono.
/// </summary>
public static class OggEncode
{
    public static byte[] FromPcm(float[][] channels, int sampleRate) => FromPcm(channels, sampleRate, out _, out _);

    public static byte[] FromPcm(float[][] channels, int sampleRate, out int outChannels, out string detail)
    {
        if (channels.Length == 0 || channels[0].Length == 0) throw new ArgumentException("no samples");
        var ogg = CthExtract.Core.OggVorbis.EncodeKeepRate(channels, sampleRate, null, "[halo]", out detail, out outChannels);
        return ogg ?? throw new InvalidOperationException(detail);
    }
}
