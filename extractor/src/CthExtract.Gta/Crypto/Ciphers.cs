using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CthExtract.Gta.Crypto;

/// <summary>
/// NG key material (101 expanded keys, 17x16 decryption tables, the name-hash lookup table and the AWC key).
/// Lives in memory only; it is never written to disk.
/// </summary>
public sealed class NgKeySet
{
    /// <summary>[101][68]: 17 round keys of 4 uint each.</summary>
    public required uint[][] Keys { get; init; }
    /// <summary>[17][16][256].</summary>
    public required uint[][][] Tables { get; init; }
    public required byte[] Lut { get; init; }
    /// <summary>4 uint XXTEA key for encrypted AWC data, or null when unknown.</summary>
    public uint[]? AwcKey { get; init; }
}

/// <summary>
/// The NG block cipher used for RPF7 tables of contents and encrypted binary entries. Algorithm as in gta-toolkit's
/// GTA5Crypto (MIT, vendored as the reference implementation); this version decrypts in place without allocating.
/// Trailing (length % 16) bytes stay as they are.
/// </summary>
public static class NgCrypto
{
    /// <summary>Key index for an archive or entry: (GTA5Hash(name) + length + 61) % 101.</summary>
    public static int KeyIndex(string name, uint length, byte[] lut)
    {
        uint hash = RageLib.GTA5.Cryptography.GTA5Hash.CalculateHash(name, lut);
        return (int)((hash + length + (101 - 40)) % 0x65);
    }

    public static void DecryptInPlace(Span<byte> data, ReadOnlySpan<uint> key, uint[][][] tables)
    {
        if (key.Length < 68) throw new ArgumentException("NG key must have 68 words");
        int blocks = data.Length / 16;
        Span<byte> a = stackalloc byte[16];
        for (int b = 0; b < blocks; b++)
        {
            var block = data.Slice(b * 16, 16);
            block.CopyTo(a);
            RoundA(a, key.Slice(0, 4), tables[0]);
            RoundA(a, key.Slice(4, 4), tables[1]);
            for (int r = 2; r <= 15; r++) RoundB(a, key.Slice(r * 4, 4), tables[r]);
            RoundA(a, key.Slice(64, 4), tables[16]);
            a.CopyTo(block);
        }
    }

    // rounds 1, 2 and 17: byte i goes through table i
    private static void RoundA(Span<byte> d, ReadOnlySpan<uint> k, uint[][] t)
    {
        uint x0 = t[0][d[0]] ^ t[1][d[1]] ^ t[2][d[2]] ^ t[3][d[3]] ^ k[0];
        uint x1 = t[4][d[4]] ^ t[5][d[5]] ^ t[6][d[6]] ^ t[7][d[7]] ^ k[1];
        uint x2 = t[8][d[8]] ^ t[9][d[9]] ^ t[10][d[10]] ^ t[11][d[11]] ^ k[2];
        uint x3 = t[12][d[12]] ^ t[13][d[13]] ^ t[14][d[14]] ^ t[15][d[15]] ^ k[3];
        Store(d, x0, x1, x2, x3);
    }

    // rounds 3..16: bytes are taken diagonally (0,7,10,13 / 1,4,11,14 / 2,5,8,15 / 3,6,9,12)
    private static void RoundB(Span<byte> d, ReadOnlySpan<uint> k, uint[][] t)
    {
        uint x0 = t[0][d[0]] ^ t[7][d[7]] ^ t[10][d[10]] ^ t[13][d[13]] ^ k[0];
        uint x1 = t[1][d[1]] ^ t[4][d[4]] ^ t[11][d[11]] ^ t[14][d[14]] ^ k[1];
        uint x2 = t[2][d[2]] ^ t[5][d[5]] ^ t[8][d[8]] ^ t[15][d[15]] ^ k[2];
        uint x3 = t[3][d[3]] ^ t[6][d[6]] ^ t[9][d[9]] ^ t[12][d[12]] ^ k[3];
        Store(d, x0, x1, x2, x3);
    }

    private static void Store(Span<byte> d, uint x0, uint x1, uint x2, uint x3)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, x0);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(4), x1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(8), x2);
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(12), x3);
    }
}

/// <summary>GTA V PC AES: AES-256-ECB, no padding, one pass; trailing (length % 16) bytes stay as they are.</summary>
public static class AesCrypto
{
    public static void DecryptInPlace(Span<byte> data, byte[] key)
    {
        int len = data.Length - data.Length % 16;
        if (len == 0) return;
        using var aes = Aes.Create();
        aes.Key = key;
        aes.DecryptEcb(data.Slice(0, len), data.Slice(0, len), PaddingMode.None);
    }

    public static void EncryptInPlace(Span<byte> data, byte[] key)
    {
        int len = data.Length - data.Length % 16;
        if (len == 0) return;
        using var aes = Aes.Create();
        aes.Key = key;
        aes.EncryptEcb(data.Slice(0, len), data.Slice(0, len), PaddingMode.None);
    }
}

/// <summary>Standard XXTEA (Wheeler and Needham, "Correction to xtea"), little-endian words, decrypt only.
/// Used for encrypted AWC data (format facts from vgmstream, ISC).</summary>
public static class Xxtea
{
    private const uint Delta = 0x9E3779B9;

    public static void DecryptInPlace(Span<byte> data, ReadOnlySpan<uint> key)
    {
        int n = data.Length / 4;
        if (n < 2) return;
        var v = new uint[n];
        for (int i = 0; i < n; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4));
        int rounds = 6 + 52 / n;
        uint sum = (uint)(rounds * Delta);
        uint y = v[0], z;
        do
        {
            uint e = (sum >> 2) & 3;
            int p;
            for (p = n - 1; p > 0; p--)
            {
                z = v[p - 1];
                y = v[p] -= Mx(y, z, sum, p, e, key);
            }
            z = v[n - 1];
            y = v[0] -= Mx(y, z, sum, 0, e, key);
            sum -= Delta;
        } while (--rounds > 0);
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i * 4), v[i]);
    }

    public static void EncryptInPlace(Span<byte> data, ReadOnlySpan<uint> key)
    {
        int n = data.Length / 4;
        if (n < 2) return;
        var v = new uint[n];
        for (int i = 0; i < n; i++) v[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i * 4));
        int rounds = 6 + 52 / n;
        uint sum = 0, z = v[n - 1], y;
        do
        {
            sum += Delta;
            uint e = (sum >> 2) & 3;
            int p;
            for (p = 0; p < n - 1; p++)
            {
                y = v[p + 1];
                z = v[p] += Mx(y, z, sum, p, e, key);
            }
            y = v[0];
            z = v[n - 1] += Mx(y, z, sum, p, e, key);
        } while (--rounds > 0);
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(i * 4), v[i]);
    }

    private static uint Mx(uint y, uint z, uint sum, int p, uint e, ReadOnlySpan<uint> key) =>
        (((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4))) ^ ((sum ^ y) + (key[(int)((p & 3) ^ e)] ^ z));
}
