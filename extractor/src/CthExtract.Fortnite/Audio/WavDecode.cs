using System.Buffers.Binary;
using System.Text;

namespace CthExtract.Fortnite.Audio;

/// <summary>Planar PCM in [-1, 1].</summary>
public sealed record Pcm(float[][] Channels, int SampleRate)
{
    public int Samples => Channels.Length == 0 ? 0 : Channels[0].Length;
    public double Seconds => SampleRate == 0 ? 0 : Samples / (double)SampleRate;

    /// <summary>Average all channels into one (Minecraft plays only mono sounds positionally).</summary>
    public Pcm ToMono()
    {
        if (Channels.Length <= 1) return this;
        var m = new float[Samples];
        for (int c = 0; c < Channels.Length; c++)
        {
            var ch = Channels[c];
            for (int i = 0; i < m.Length; i++) m[i] += ch[i];
        }
        float k = 1f / Channels.Length;
        for (int i = 0; i < m.Length; i++) m[i] *= k;
        return new Pcm([m], SampleRate);
    }

    public float Peak()
    {
        float p = 0;
        foreach (var ch in Channels) foreach (var s in ch) p = Math.Max(p, Math.Abs(s));
        return p;
    }
}

/// <summary>RIFF/WAVE reader for what UE's SoundWave.Decode returns on PC: PCM (8/16/24/32-bit, float) and
/// Microsoft ADPCM (UE's "ADPCM" format, WAVE_FORMAT_ADPCM = 2).</summary>
public static class WavDecode
{
    private static readonly int[] AdaptationTable = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];
    private static readonly short[][] DefaultCoefs = [[256, 0], [512, -256], [0, 0], [192, 64], [240, 0], [460, -208], [392, -232]];

    public static Pcm Decode(byte[] wav)
    {
        if (wav.Length < 12 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE")
            throw new InvalidDataException("not a RIFF/WAVE file");
        int pos = 12;
        int format = 0, channels = 0, rate = 0, blockAlign = 0, bits = 0, samplesPerBlock = 0;
        short[][]? coefs = null;
        int dataOff = -1, dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
            int body = pos + 8;
            if (len < 0 || body + (long)len > wav.Length) len = wav.Length - body; // tolerate a too-large size field
            if (id == "fmt ")
            {
                if (len < 16) throw new InvalidDataException("fmt chunk too small");
                var f = wav.AsSpan(body);
                format = BinaryPrimitives.ReadUInt16LittleEndian(f);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(f[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
                if (format == 0xFFFE && len >= 40) format = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]); // WAVE_FORMAT_EXTENSIBLE
                if (format == 2 && len >= 22)
                {
                    samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(f[18..]);
                    int numCoef = len >= 24 ? BinaryPrimitives.ReadUInt16LittleEndian(f[20..]) : 0;
                    if (numCoef > 0 && len >= 22 + 2 + numCoef * 4)
                    {
                        coefs = new short[numCoef][];
                        for (int i = 0; i < numCoef; i++)
                            coefs[i] = [BinaryPrimitives.ReadInt16LittleEndian(f[(22 + i * 4)..]), BinaryPrimitives.ReadInt16LittleEndian(f[(24 + i * 4)..])];
                    }
                }
            }
            else if (id == "data") { dataOff = body; dataLen = len; break; }
            pos = body + len + (len & 1);
        }
        if (dataOff < 0) throw new InvalidDataException("no data chunk");
        if (channels < 1 || channels > 8) throw new InvalidDataException($"{channels} channels");
        if (rate < 1000 || rate > 384000) throw new InvalidDataException($"sample rate {rate}");
        var data = wav.AsSpan(dataOff, dataLen);
        return format switch
        {
            1 => Lpcm(data, channels, rate, bits),
            3 => Float(data, channels, rate, bits),
            2 => MsAdpcm(data, channels, rate, blockAlign, samplesPerBlock, coefs ?? DefaultCoefs),
            _ => throw new NotSupportedException($"WAV format 0x{format:X4} is not supported"),
        };
    }

    private static Pcm Lpcm(ReadOnlySpan<byte> d, int ch, int rate, int bits)
    {
        int bps = bits / 8;
        if (bps is < 1 or > 4 || bits % 8 != 0) throw new NotSupportedException($"{bits}-bit PCM");
        int frames = d.Length / (bps * ch);
        var o = Alloc(ch, frames);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
            {
                var s = d.Slice((i * ch + c) * bps, bps);
                o[c][i] = bps switch
                {
                    1 => (s[0] - 128) / 128f,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    3 => ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
                };
            }
        return new Pcm(o, rate);
    }

    private static Pcm Float(ReadOnlySpan<byte> d, int ch, int rate, int bits)
    {
        if (bits != 32 && bits != 64) throw new NotSupportedException($"{bits}-bit float WAV");
        int bps = bits / 8, frames = d.Length / (bps * ch);
        var o = Alloc(ch, frames);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < ch; c++)
            {
                var s = d.Slice((i * ch + c) * bps, bps);
                float v = bps == 4 ? BinaryPrimitives.ReadSingleLittleEndian(s) : (float)BinaryPrimitives.ReadDoubleLittleEndian(s);
                o[c][i] = float.IsFinite(v) ? Math.Clamp(v, -1f, 1f) : 0f;
            }
        return new Pcm(o, rate);
    }

    /// <summary>Microsoft ADPCM (public format: 7-byte-per-channel block header, 4-bit nibbles, high nibble first).</summary>
    internal static Pcm MsAdpcm(ReadOnlySpan<byte> d, int ch, int rate, int blockAlign, int samplesPerBlock, short[][] coefs)
    {
        if (blockAlign < 7 * ch) throw new InvalidDataException($"ADPCM block align {blockAlign}");
        int expectedSpb = (blockAlign - 7 * ch) * 2 / ch + 2;
        if (samplesPerBlock <= 0 || samplesPerBlock > expectedSpb) samplesPerBlock = expectedSpb;
        int blocks = d.Length / blockAlign;
        int tail = d.Length - blocks * blockAlign;
        var outCh = new List<float>[ch];
        for (int c = 0; c < ch; c++) outCh[c] = new List<float>(blocks * samplesPerBlock);

        var pred = new int[ch]; var delta = new int[ch]; var s1 = new int[ch]; var s2 = new int[ch];
        for (int b = 0; b <= blocks; b++)
        {
            int start = b * blockAlign;
            int size = b < blocks ? blockAlign : tail;
            if (size < 7 * ch) break;
            var blk = d.Slice(start, size);
            int p = 0;
            for (int c = 0; c < ch; c++) { pred[c] = Math.Min(blk[p++], coefs.Length - 1); }
            for (int c = 0; c < ch; c++) { delta[c] = BinaryPrimitives.ReadInt16LittleEndian(blk[p..]); p += 2; }
            for (int c = 0; c < ch; c++) { s1[c] = BinaryPrimitives.ReadInt16LittleEndian(blk[p..]); p += 2; }
            for (int c = 0; c < ch; c++) { s2[c] = BinaryPrimitives.ReadInt16LittleEndian(blk[p..]); p += 2; }
            for (int c = 0; c < ch; c++) { outCh[c].Add(s2[c] / 32768f); outCh[c].Add(s1[c] / 32768f); }
            int produced = 2;
            int c2 = 0;
            for (; p < blk.Length && produced < samplesPerBlock; p++)
            {
                int v = blk[p];
                foreach (int nib in new[] { v >> 4, v & 0x0F })
                {
                    Nibble(c2, nib);
                    c2++;
                    if (c2 == ch) { c2 = 0; produced++; if (produced >= samplesPerBlock) break; }
                }
            }
        }
        var o = new float[ch][];
        int frames = outCh.Min(l => l.Count);
        for (int c = 0; c < ch; c++) o[c] = outCh[c].GetRange(0, frames).ToArray();
        return new Pcm(o, rate);

        void Nibble(int c, int nib)
        {
            int signedNib = nib >= 8 ? nib - 16 : nib;
            var cf = coefs[pred[c]];
            int predicted = ((s1[c] * cf[0]) + (s2[c] * cf[1])) >> 8;
            predicted += signedNib * delta[c];
            predicted = Math.Clamp(predicted, short.MinValue, short.MaxValue);
            s2[c] = s1[c];
            s1[c] = predicted;
            delta[c] = (AdaptationTable[nib] * delta[c]) >> 8;
            if (delta[c] < 16) delta[c] = 16;
            outCh[c].Add(predicted / 32768f);
        }
    }

    private static float[][] Alloc(int ch, int frames)
    {
        var o = new float[ch][];
        for (int c = 0; c < ch; c++) o[c] = new float[frames];
        return o;
    }
}
