using System.Security.Cryptography;
using RageLib.GTA5.Cryptography;

namespace CthExtract.Gta.Crypto;

/// <summary>
/// SHA1 hashes every piece of key material must match before it is used. The defaults are gta-toolkit's
/// published hash list (MIT, vendor/gta-toolkit/GTA5Constants.cs): it holds only hashes, never a key.
/// Tests pass their own hashes for synthetic keys.
/// </summary>
public sealed class KeyHashes
{
    public const int NgKeyCount = 101;
    public const int NgKeyBytes = 0x110;      // 17 round keys x 4 uint
    public const int NgTableCount = 17 * 16;  // 17 rounds x 16 tables
    public const int NgTableBytes = 0x400;    // 256 uint
    public const int LutBytes = 0x100;
    public const int AesKeyBytes = 0x20;

    public required byte[] AesKey { get; init; }
    public required byte[][] NgKeys { get; init; }
    public required byte[][] NgTables { get; init; }
    public required byte[] Lut { get; init; }

    public static KeyHashes Gta5Pc { get; } = new()
    {
        AesKey = GTA5HashConstants.PC_AES_KEY_HASH,
        NgKeys = GTA5HashConstants.PC_NG_KEY_HASHES,
        NgTables = GTA5HashConstants.PC_NG_DECRYPT_TABLE_HASHES,
        Lut = GTA5HashConstants.PC_LUT_HASH,
    };

    public static bool Matches(ReadOnlySpan<byte> data, byte[] expectedSha1)
    {
        Span<byte> h = stackalloc byte[20];
        SHA1.HashData(data, h);
        return h.SequenceEqual(expectedSha1);
    }
}
