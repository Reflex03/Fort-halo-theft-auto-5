using System.Security.Cryptography;
using CUE4Parse.UE4.VirtualFileSystem;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using AesProvider = System.Security.Cryptography.Aes;

namespace CUE4Parse.GameTypes.Rennsport.Encryption.Aes;

/// <summary>
/// Reversed by Spiritovod
/// </summary>
public static class RennsportAes
{
    private static AesProvider Provider = new AesCryptoServiceProvider
    {
        Mode = CipherMode.CBC,
        Padding = PaddingMode.None,
        BlockSize = 16 * 8
    };

    private static byte[] iv0 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static byte[] iv1 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static byte[] iv2 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    //private static byte[] iv3 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static byte[] tempkey =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    public static byte[] RennsportDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null) =>
        RennsportDecrypt(bytes, beginOffset, count, isIndex, reader, false);

    public static byte[] RennsportDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, bool directoryIndex)
    {
        if (bytes.Length < beginOffset + count)
            throw new IndexOutOfRangeException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16");
        if (reader.AesKey == null)
            throw new NullReferenceException("reader.AesKey");

        // 1C AC 76 88 32 7D 56 4C A8 7E 23 11 4A 24 7D B1 4F 52 43 34 AC DF D0 EB B0 F3 91 5D D0 84 61 85
        var key = CUE4Parse.Encryption.Aes.Aes.Decrypt(tempkey, reader.AesKey);

        if (!isIndex) return Provider.CreateDecryptor(key, iv0).TransformFinalBlock(bytes, beginOffset, count);

        var output = new byte[count];
        byte[] iv = directoryIndex ? iv2 : iv1;
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
        cipher.ProcessBytes(bytes, 0, count, output, 0);
        return output;
    }
}
