using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CUE4Parse.GameTypes.Tencent.RocoKingdomWorld.Encryption.Algorithms;
using CUE4Parse.UE4.Lua.Archives;
using CUE4Parse.UE4.Lua.Readers;
using CUE4Parse.UE4.Lua.Writers;

namespace CUE4Parse.GameTypes.Tencent.RocoKingdomWorld.Lua;

public static class NRCLua
{
    // Password found in NRC/Plugins/NRCCrypto/Config/Crypto.ini
    private static ReadOnlySpan<byte> Password => "00000000000000000000000000000000000000000000"u8;
    private static ReadOnlySpan<byte> MLEKey =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static ReadOnlySpan<byte> MLEMagic => "\x1BMLE"u8;
    private static ReadOnlySpan<byte> AESMagic => [0xFA, 0xE5, 0xC0];

    private static readonly byte[] _key = DeriveKey();
    private static readonly byte[] _iv = DeriveIV();

    private static byte[] DeriveKey()
        => Rfc2898DeriveBytes.Pbkdf2(Password, SHA256.HashData(Password), 100000, HashAlgorithmName.SHA256, 32);

    private static byte[] DeriveIV()
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Password);
        hash.AppendData("iv"u8);
        return hash.GetHashAndReset()[..16];
    }

    public static byte[] Decrypt(this byte[] encrypted, int offset = 0)
    {
        using var aes = Aes.Create();

        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = _key;
        aes.IV = _iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encrypted, offset, encrypted.Length - offset);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct LuaMLEHeader
    {
        public uint Magic;
        public byte Version;
        public ushort BlockSize;
        public int BlockCount;
        public ushort LastBlockSize;
        public ulong KeySeed;
    }

    private static byte[] DecryptLuaData(byte[] encryptedData, out bool usesNewHeader)
    {
        Span<byte> encryptedSpan = encryptedData.AsSpan();
        if (encryptedSpan.Length > Unsafe.SizeOf<LuaMLEHeader>() && encryptedSpan[..4].SequenceEqual(MLEMagic))
        {
            LuaMLEHeader header = MemoryMarshal.Read<LuaMLEHeader>(encryptedSpan);

            var plaintext = new byte[Math.Max(0, header.BlockCount - 1) * header.BlockSize + header.LastBlockSize];
            FConfigurableCryptoAlgorithmMLE.DecryptSlice(encryptedSpan[Unsafe.SizeOf<LuaMLEHeader>()..], MLEKey, plaintext, header.BlockSize, header.KeySeed);

            usesNewHeader = true;
            return plaintext;
        }

        usesNewHeader = false;

        if (encryptedSpan.Length > 8 && encryptedSpan[..3].SequenceEqual(AESMagic))
            return encryptedData.Decrypt(7);

        return encryptedData.Decrypt();
    }

    public static byte[] DecryptLuaBytecode(string name, byte[] encryptedData)
    {
        var decryptedData = DecryptLuaData(encryptedData, out var usesNewHeader);

        if (!usesNewHeader && !FLuaReader.IsValidLuaMagic(decryptedData))
            throw new InvalidDataException("Failed to decrypt. Expected Lua magic");

        using var Ar = new FNRCLuaArchive(name, decryptedData);
        var lua = NRCLuaReader.ReadBytecode(Ar, usesNewHeader);

        return new FLuaWriter54(lua).GetBuffer();
    }
}
