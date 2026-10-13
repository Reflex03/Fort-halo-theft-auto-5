using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.VirtualFileSystem;

namespace CUE4Parse.GameTypes.ApexMobile.Encryption.Aes;

public static class ApexLegendsMobileAes
{
    public static byte[] DecryptApexMobile(this byte[] encrypted, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null)
    {
        if (encrypted.Length < beginOffset + count)
            throw new IndexOutOfRangeException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16");
        if (reader.AesKey == null)
            throw new NullReferenceException("reader.AesKey");

        return ApexDecryptData(encrypted, beginOffset, count, reader.AesKey);
    }

    private static readonly byte[] _table1 =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly uint[] _table2 =
    [
        0x00000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x0000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000,
        0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000
    ];

    private static byte[] ApexDecryptData(byte[] encrypted, int beginOffset, int count, FAesKey key)
    {
        unsafe
        {
            Span<uint> rk = stackalloc uint[32];
            ApexSetupDecrypt(key, rk);

            var decrypt = new byte[count];
            Buffer.BlockCopy(encrypted, beginOffset, decrypt, 0, count);
            fixed (byte* pDecrypt = decrypt)
            {
                for (var i = 0; i < count; i += 16)
                {
                    ApexDecrypt(rk, pDecrypt + i);
                }
            }

            return decrypt;
        }
    }

    private static unsafe void ApexSetupDecrypt(FAesKey key, Span<uint> rk)
    {
        Span<uint> internalKey = stackalloc uint[4];
        internalKey[0] = BitConverter.ToUInt32(key.Key).ByteSwap() ^ 0x1E3DFA;
        internalKey[1] = BitConverter.ToUInt32(key.Key, 4).ByteSwap() ^ 0x00000001;
        internalKey[2] = BitConverter.ToUInt32(key.Key, 8).ByteSwap() ^ 0xD99D2CF;
        internalKey[3] = BitConverter.ToUInt32(key.Key, 12).ByteSwap() ^ 0x00000002;
        for (var i = 0; i < 32; i++)
        {
            var tempInt = _table2[i] ^ internalKey[(i + 1) % 4] ^ internalKey[(i + 2) % 4] ^ internalKey[(i + 3) % 4];
            var temp = (byte*) &tempInt;
            var tablePart01 = _table1[temp[0]] | (uint) (_table1[temp[1]] << 8);
            var tablePart012 = tablePart01 | (uint) (_table1[temp[2]] << 16);
            var tableEntry = tablePart012 | (uint) (_table1[temp[3]] << 24);
            var rkValue = tableEntry ^ internalKey[i % 4] ^ (tableEntry >> 19 | tablePart012 << 13) ^ (tableEntry >> 9 | tablePart01 << 23);
            internalKey[i % 4] = rkValue;
            rk[i] = rkValue;
        }
    }

    private static unsafe void ApexDecrypt(Span<uint> rk, byte* data)
    {
        var src = (uint*) data;
        Span<uint> internalData = stackalloc uint[4];

        internalData[0] = src[0].ByteSwap();
        internalData[1] = src[1].ByteSwap();
        internalData[2] = src[2].ByteSwap();
        internalData[3] = src[3].ByteSwap();
        for (var i = 0; i < 32; i++)
        {
            var tempInt = rk[^(i + 1)] ^ internalData[(i + 1) % 4] ^ internalData[(i + 2) % 4] ^ internalData[(i + 3) % 4];
            var temp = (byte*) &tempInt;
            var tablePart3 = _table1[temp[3]];
            var tablePart0 = _table1[temp[0]];
            var tablePart01 = tablePart0 | (uint) (_table1[temp[1]] << 8);
            var tablePart012 = tablePart01 | (uint) (_table1[temp[2]] << 16);
            var tableEntry = tablePart012 | (uint) (tablePart3 << 24);
            internalData[i % 4] ^= tableEntry ^ (4 * tableEntry | (uint) (tablePart3 >> 6)) ^ (tableEntry >> 22 | tablePart012 << 10) ^ (tableEntry >> 14 | tablePart01 << 18) ^ (tableEntry >> 8 | (uint) tablePart0 << 24);
        }

        var intData = (uint*) data;
        intData[0] = internalData[3].ByteSwap();
        intData[1] = internalData[2].ByteSwap();
        intData[2] = internalData[1].ByteSwap();
        intData[3] = internalData[0].ByteSwap();
    }

    private static uint ByteSwap(this uint value) => value >> 24 | value >> 8 & 0xFF00 | value << 8 & 0xFF0000 | value << 24;
}
