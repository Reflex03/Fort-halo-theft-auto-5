using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using AesProvider = CUE4Parse.Encryption.Aes.Aes;

namespace CUE4Parse.GameTypes.Tencent.GangstarMirageCity.Encryption;

public static class GangstarMirageCityAes
{
    internal static readonly byte[] _xorKey =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    public static byte[] GangstarMirageCityDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null)
    {
        if (beginOffset > bytes.Length - count)
            throw new ArgumentException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16", nameof(count));
        if (reader.AesKey == null)
            throw new NullReferenceException("reader.AesKey");

        var output = AesProvider.Decrypt(bytes, beginOffset, count, reader.AesKey);
        if (isIndex)
        {
            TensorUtils.Xor(output, _xorKey);
        }

        return output;
    }
}
