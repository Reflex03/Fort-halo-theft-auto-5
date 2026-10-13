namespace CthExtract.Gta;

/// <summary>Name hashes used by RAGE data. Both are public, well-known algorithms.</summary>
public static class Hashes
{
    /// <summary>Jenkins one-at-a-time over the lower-cased ASCII name ("joaat"). Used for shader parameter names,
    /// shader names, texture/dictionary names and (masked to 29 bits) AWC stream ids.</summary>
    public static uint Joaat(string text)
    {
        uint h = 0;
        foreach (char ch in text)
        {
            char c = ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch;
            h += (byte)c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    /// <summary>Joaat over raw bytes (no case folding).</summary>
    public static uint Joaat(ReadOnlySpan<byte> data)
    {
        uint h = 0;
        foreach (byte b in data)
        {
            h += b;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    /// <summary>AWC stream id of a wave name: joaat(name) &amp; 0x1FFFFFFF (top 3 bits of the stored word are the tag count).</summary>
    public static uint AwcStreamId(string name) => Joaat(name) & 0x1FFFFFFF;

    /// <summary>Skeleton bone tag of a bone name: ELF hash of the upper-cased name, mod 0xFE8F, + 0x170.
    /// e.g. wheel_lf = 27922, wheel_rf = 26418, wheel_lr = 27902, wheel_rr = 26398.</summary>
    public static ushort BoneTag(string name)
    {
        uint h = 0;
        foreach (char ch in name)
        {
            char c = ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch;
            h = (h << 4) + (byte)c;
            uint x = h & 0xF0000000;
            if (x != 0) h ^= x >> 24;
            h &= ~x;
        }
        return (ushort)(h % 0xFE8F + 0x170);
    }
}
