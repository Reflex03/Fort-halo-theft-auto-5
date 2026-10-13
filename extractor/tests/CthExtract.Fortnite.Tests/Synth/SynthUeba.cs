using System.Buffers.Binary;

namespace CthExtract.Fortnite.Tests.Synth;

/// <summary>Deterministic xorshift64* PRNG (System.Random's sequence is not guaranteed across .NET versions).</summary>
public sealed class Prng(ulong seed)
{
    private ulong _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong Next()
    {
        _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
        return _s * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>0 .. n-1</summary>
    public int Below(int n) => (int)(Next() % (ulong)n);
    public bool Chance(int percent) => Below(100) < percent;
}

/// <summary>LSB-first bit writer (the inverse of vgmstream's bitstream_lsb reader).</summary>
public sealed class BitWriterLsb
{
    private readonly List<byte> _bytes = new();
    private int _bits;

    public int BitPosition => _bits;

    public void Write(uint value, int bits)
    {
        for (int i = 0; i < bits; i++)
        {
            if ((_bits & 7) == 0) _bytes.Add(0);
            if (((value >> i) & 1) != 0) _bytes[_bits >> 3] |= (byte)(1 << (_bits & 7));
            _bits++;
        }
    }

    public void AlignTo(int bits)
    {
        int left = _bits % bits;
        if (left != 0) Write(0, bits - left);
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>
/// Writes synthetic Unreal "UEBA" (Bink Audio 2) streams: valid bitstream syntax with chosen or random coefficients.
/// Our own encoder of the packet syntax (no psychoacoustics): used to drive the C# decoder and vgmstream-cli (oracle)
/// with the same input, and to check the inverse transform against a plain DCT-III.
/// </summary>
public static class SynthUeba
{
    private static readonly ushort[] Cutoff =
    [
        0, 100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320, 2700,
        3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500,
    ];

    private static readonly byte[] Rle = [2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13, 14, 15, 16, 32, 64];

    public static int FrameSamples(int sampleRate) => sampleRate < 22050 ? 512 : sampleRate < 44100 ? 1024 : 2048;
    public static int OutputSamples(int sampleRate) => sampleRate < 22050 ? 480 : sampleRate < 44100 ? 960 : 1920;

    public static int BandCount(int sampleRate)
    {
        int half = (sampleRate + 1) >> 1, n = 0;
        while (n < 25 && Cutoff[n] < half) n++;
        return n;
    }

    /// <summary>One channel's coefficient description: either random content or one explicit value per position.</summary>
    public sealed class ChannelSpec
    {
        /// <summary>float29 codes for coefs[0], coefs[1].</summary>
        public uint Dc0, Dc1;
        /// <summary>Scale factor index per band (7 bits).</summary>
        public int[] ScaleIndex = [];
        /// <summary>Quantised magnitudes for positions 2..frameSamples-1 (sign separately); null = all zero.</summary>
        public int[]? Values;
        public bool[]? Negative;
    }

    /// <summary>float29 code for mantissa * 2^(power-23), sign.</summary>
    public static uint Float29(int mantissa, int power, bool negative) =>
        (uint)(power & 0x1f) | ((uint)(mantissa & 0x7FFFFF) << 5) | (negative ? 1u << 28 : 0u);

    public static ChannelSpec RandomChannel(Prng r, int sampleRate, int maxQBits = 5)
    {
        int fs = FrameSamples(sampleRate);
        var spec = new ChannelSpec
        {
            Dc0 = Float29(r.Below(1 << 12), 16 + r.Below(6), r.Chance(50)),
            Dc1 = Float29(r.Below(1 << 12), 16 + r.Below(6), r.Chance(50)),
            ScaleIndex = Enumerable.Range(0, BandCount(sampleRate)).Select(_ => r.Below(36)).ToArray(),
            Values = new int[fs],
            Negative = new bool[fs],
        };
        for (int i = 2; i < fs; i++)
        {
            if (r.Chance(35)) continue;
            spec.Values[i] = r.Below(1 << r.Below(maxQBits + 1));
            spec.Negative[i] = r.Chance(50);
        }
        return spec;
    }

    /// <summary>Write one channel's bits, choosing batch sizes (RLE) and q_bits from the values.</summary>
    private static void WriteChannel(BitWriterLsb w, ChannelSpec c, int frameSamples, int bandCount, Prng r)
    {
        w.Write(c.Dc0, 29);
        w.Write(c.Dc1, 29);
        for (int b = 0; b < bandCount; b++) w.Write((uint)(b < c.ScaleIndex.Length ? c.ScaleIndex[b] : 0), 7);

        int pos = 2;
        while (pos < frameSamples)
        {
            int end;
            int rleIndex = -1;
            if (r.Chance(30))
            {
                rleIndex = r.Below(16);
                end = pos + 8 * Rle[rleIndex];
            }
            else end = pos + 8;
            if (end > frameSamples) end = frameSamples;

            int max = 0;
            for (int i = pos; i < end; i++) max = Math.Max(max, c.Values?[i] ?? 0);
            int qBits = 0;
            while ((1 << qBits) <= max) qBits++;
            if (qBits > 15) throw new ArgumentException("value too large for 15 bits");

            if (rleIndex >= 0) { w.Write(1, 1); w.Write((uint)rleIndex, 4); }
            else w.Write(0, 1);
            w.Write((uint)qBits, 4);
            if (qBits > 0)
            {
                for (int i = pos; i < end; i++) w.Write((uint)c.Values![i], qBits);
                for (int i = pos; i < end; i++)
                    if (c.Values![i] != 0) w.Write(c.Negative![i] ? 1u : 0u, 1);
            }
            pos = end;
        }
    }

    /// <summary>One packet: decoders of 2 (last: 1) channels, each 2 reserved bits + channels, aligned to 32 bits.</summary>
    public static byte[] Packet(IReadOnlyList<ChannelSpec> channels, int sampleRate, Prng r)
    {
        int fs = FrameSamples(sampleRate), bands = BandCount(sampleRate);
        var all = new List<byte>();
        for (int d = 0; d < channels.Count; d += 2)
        {
            var w = new BitWriterLsb();
            w.Write(0, 2);
            for (int c = d; c < Math.Min(d + 2, channels.Count); c++) WriteChannel(w, channels[c], fs, bands, r);
            w.AlignTo(32);
            all.AddRange(w.ToArray());
        }
        return all.ToArray();
    }

    /// <summary>A whole UEBA stream of random packets. With <paramref name="seekChunkAfter"/> a "SEEK" chunk is
    /// inserted after that many packets; the last packet uses the 0xFFFF size marker with a short sample count.</summary>
    public static byte[] RandomStream(ulong seed, int sampleRate, int channels, int packets, int seekChunkAfter = -1, int lastPacketSamples = 0)
    {
        var r = new Prng(seed);
        var pk = new List<byte[]>();
        for (int i = 0; i < packets; i++)
            pk.Add(Packet(Enumerable.Range(0, channels).Select(_ => RandomChannel(r, sampleRate)).ToList(), sampleRate, r));
        int outSamples = OutputSamples(sampleRate);
        int total = (packets - 1) * outSamples + (lastPacketSamples > 0 ? lastPacketSamples : outSamples);
        return Container(pk, sampleRate, channels, total, seekChunkAfter, lastPacketSamples);
    }

    public static byte[] Container(IReadOnlyList<byte[]> packets, int sampleRate, int channels, int numSamples, int seekChunkAfter = -1, int lastPacketSamples = 0)
    {
        var ms = new MemoryStream();
        var h = new byte[0x1c];
        "ABEU"u8.CopyTo(h);
        h[4] = 1; h[5] = (byte)channels;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(0x0c), numSamples);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x10), (ushort)packets.Max(p => p.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x18), 0); // no seek table entries
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1a), 1);
        ms.Write(h);
        for (int i = 0; i < packets.Count; i++)
        {
            if (i == seekChunkAfter)
            {
                var seek = new byte[0x0f + 2 * 2];
                "SEEK"u8.CopyTo(seek);
                seek[4] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(seek.AsSpan(0x0b), 2);
                ms.Write(seek);
            }
            var p = packets[i];
            var ph = new byte[4];
            bool last = i == packets.Count - 1 && lastPacketSamples > 0;
            BinaryPrimitives.WriteUInt16LittleEndian(ph, 0x9999);
            BinaryPrimitives.WriteUInt16LittleEndian(ph.AsSpan(2), last ? (ushort)0xFFFF : (ushort)p.Length);
            ms.Write(ph);
            if (last)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(ph, (ushort)p.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(ph.AsSpan(2), (ushort)lastPacketSamples);
                ms.Write(ph);
            }
            ms.Write(p);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(ms.GetBuffer().AsSpan(0x14), (uint)ms.Length);
        return ms.ToArray();
    }
}
