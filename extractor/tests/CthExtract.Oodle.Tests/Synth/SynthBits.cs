namespace CthExtract.Oodle.Tests.Synth;

/// <summary>MSB-first bit writer: the format read by ooz's <c>BitReader</c> (forward) and, with the bytes reversed,
/// by its backward variant.</summary>
internal sealed class MsbBitWriter
{
    readonly List<byte> _bytes = new();
    int _bitCount;

    public int BitCount => _bitCount;

    public void Write(uint value, int nbits)
    {
        for (int i = nbits - 1; i >= 0; i--)
            WriteBit((int)((value >> i) & 1));
    }

    public void WriteBit(int bit)
    {
        if ((_bitCount & 7) == 0)
            _bytes.Add(0);
        if (bit != 0)
            _bytes[^1] |= (byte)(0x80 >> (_bitCount & 7));
        _bitCount++;
    }

    /// <summary>The bytes, zero padded to a whole byte (ceil(bits / 8) bytes).</summary>
    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>LSB-first bit writer: the format of ooz's Huffman and tANS symbol streams.</summary>
internal sealed class LsbBitWriter
{
    readonly List<byte> _bytes = new();
    int _bitCount;

    public int BitCount => _bitCount;

    public void WriteBit(int bit)
    {
        if ((_bitCount & 7) == 0)
            _bytes.Add(0);
        if (bit != 0)
            _bytes[^1] |= (byte)(1 << (_bitCount & 7));
        _bitCount++;
    }

    /// <summary>Writes a code MSB first (the first bit written is the code's top bit).</summary>
    public void WriteCodeMsbFirst(uint code, int len)
    {
        for (int i = len - 1; i >= 0; i--)
            WriteBit((int)((code >> i) & 1));
    }

    public void WriteLsbFirst(uint value, int nbits)
    {
        for (int i = 0; i < nbits; i++)
            WriteBit((int)((value >> i) & 1));
    }

    public byte[] ToArray() => _bytes.ToArray();
}
