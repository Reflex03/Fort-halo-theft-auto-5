using System.Buffers.Binary;
using System.Text;

namespace CthExtract.Fortnite.Tests.Synth;

/// <summary>Our own RIFF/WAVE writers (test-only): PCM16 and Microsoft ADPCM.</summary>
public static class SynthWav
{
    public static short[] Sine(int samples, int rate, double hz, double amp = 12000, int channels = 1)
    {
        var o = new short[samples * channels];
        for (int i = 0; i < samples; i++)
            for (int c = 0; c < channels; c++)
                o[i * channels + c] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * hz * (c + 1) * i / rate));
        return o;
    }

    public static byte[] Pcm16(short[] interleaved, int channels, int rate)
    {
        var data = new byte[interleaved.Length * 2];
        for (int i = 0; i < interleaved.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), interleaved[i]);
        var fmt = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), rate * channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        return Riff(fmt, data);
    }

    private static readonly short[][] Coefs = [[256, 0], [512, -256], [0, 0], [192, 64], [240, 0], [460, -208], [392, -232]];

    /// <summary>MS ADPCM stream with random block headers and nibbles (any nibble stream is valid), deterministic per seed.</summary>
    public static byte[] RandomMsAdpcm(ulong seed, int channels, int rate, int blockAlign, int blocks, int tailBytes = 0)
    {
        var r = new Prng(seed);
        int samplesPerBlock = (blockAlign - 7 * channels) * 2 / channels + 2;
        var data = new byte[blocks * blockAlign + tailBytes];
        for (int b = 0; b <= blocks; b++)
        {
            int start = b * blockAlign;
            int size = b < blocks ? blockAlign : tailBytes;
            if (size < 7 * channels) break;
            int p = start;
            for (int c = 0; c < channels; c++) data[p++] = (byte)r.Below(7);
            for (int c = 0; c < channels; c++) { BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(p), (short)(16 + r.Below(400))); p += 2; }
            for (int c = 0; c < channels; c++) { BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(p), (short)(r.Below(8000) - 4000)); p += 2; }
            for (int c = 0; c < channels; c++) { BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(p), (short)(r.Below(8000) - 4000)); p += 2; }
            for (; p < start + size; p++) data[p] = (byte)r.Below(256);
        }
        var fmt = new byte[20 + 2 + 2 + Coefs.Length * 4];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, 2);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), rate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), rate * blockAlign / samplesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 32); // cbSize
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), (ushort)samplesPerBlock);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(20), (ushort)Coefs.Length);
        for (int i = 0; i < Coefs.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(22 + i * 4), Coefs[i][0]);
            BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(24 + i * 4), Coefs[i][1]);
        }
        return Riff(fmt, data);
    }

    private static byte[] Riff(byte[] fmt, byte[] data)
    {
        var ms = new MemoryStream();
        void Chunk(string id, byte[] b)
        {
            ms.Write(Encoding.ASCII.GetBytes(id));
            Span<byte> l = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(l, b.Length);
            ms.Write(l);
            ms.Write(b);
            if ((b.Length & 1) != 0) ms.WriteByte(0);
        }
        ms.Write("RIFF\0\0\0\0WAVE"u8);
        Chunk("fmt ", fmt);
        Chunk("data", data);
        var o = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), o.Length - 8);
        return o;
    }

    /// <summary>Samples of the "data" chunk of a PCM16 WAV (to read vgmstream-cli output).</summary>
    public static short[] ReadPcm16(byte[] wav)
    {
        int p = 12;
        while (p + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, p, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(p + 4));
            if (id == "data")
            {
                var o = new short[len / 2];
                for (int i = 0; i < o.Length; i++) o[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(p + 8 + i * 2));
                return o;
            }
            p += 8 + len + (len & 1);
        }
        throw new InvalidDataException("no data chunk");
    }
}
