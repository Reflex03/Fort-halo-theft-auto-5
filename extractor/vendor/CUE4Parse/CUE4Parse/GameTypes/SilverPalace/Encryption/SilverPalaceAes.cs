using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Blake3;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;

namespace CUE4Parse.GameTypes.SilverPalace.Encryption;

public static class SilverPalaceAes
{
    // Only the first 16 bytes of this are actually used.
    private static ReadOnlySpan<byte> XorKey =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    // Used starting from CBT2
    public static byte[] SilverPalaceDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null)
    {
        if (bytes.Length < beginOffset + count)
            throw new IndexOutOfRangeException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16");
        if (reader.AesKey == null)
            throw new NullReferenceException("reader.AesKey");

        var key = reader.AesKey;
        var pakName = Path.GetFileNameWithoutExtension(reader.Name).ToLower();

        var pakKey = (stackalloc byte[KeySize]);
        {
            using var hash = Hasher.New();
            hash.Update(key.Key);
            hash.Update(Encoding.UTF8.GetBytes(pakName));
            hash.Finalize(pakKey);
        }

        var data = bytes.AsSpan(beginOffset, count).ToArray();
        DecryptCustomAes(data, pakKey);
        return data;
    }

    private static void DecryptCustomAes(Span<byte> data, Span<byte> key)
    {
        DecryptData(data, key);
        TensorUtils.Xor(data, XorKey[..16]);
    }

    private const int BlockSize = 16;
    private const int KeySize = 32;
    private const int RoundCount = 14;

    private static void DecryptData(Span<byte> contents, ReadOnlySpan<byte> key)
    {
        Span<uint> dKey = stackalloc uint[4 * (RoundCount + 1)];
        AesDecryptExpand(dKey, key);

        for (int offset = 0; offset < contents.Length; offset += BlockSize)
        {
            Span<byte> block = contents.Slice(offset, BlockSize);
            DecryptBlock(block, dKey);
        }
    }

    private static void DecryptBlock(Span<byte> block, Span<uint> dKey)
    {
        uint s0 = ReadU32Le(block.Slice(0, 4)) ^ dKey[0];
        uint s1 = ReadU32Le(block.Slice(4, 4)) ^ dKey[1];
        uint s2 = ReadU32Le(block.Slice(8, 4)) ^ dKey[2];
        uint s3 = ReadU32Le(block.Slice(12, 4)) ^ dKey[3];

        for (int round = 1; round < RoundCount; round++)
        {
            uint t0 = AesDec(s0, s3, s2, s1);
            uint t1 = AesDec(s1, s0, s3, s2);
            uint t2 = AesDec(s2, s1, s0, s3);
            uint t3 = AesDec(s3, s2, s1, s0);

            s0 = t0 ^ dKey[4 * round + 0];
            s1 = t1 ^ dKey[4 * round + 1];
            s2 = t2 ^ dKey[4 * round + 2];
            s3 = t3 ^ dKey[4 * round + 3];
        }

        uint u0 = AesDecLast(s0, s3, s2, s1) ^ dKey[4 * RoundCount + 0];
        uint u1 = AesDecLast(s1, s0, s3, s2) ^ dKey[4 * RoundCount + 1];
        uint u2 = AesDecLast(s2, s1, s0, s3) ^ dKey[4 * RoundCount + 2];
        uint u3 = AesDecLast(s3, s2, s1, s0) ^ dKey[4 * RoundCount + 3];

        WriteU32Le(block.Slice(0, 4), u0);
        WriteU32Le(block.Slice(4, 4), u1);
        WriteU32Le(block.Slice(8, 4), u2);
        WriteU32Le(block.Slice(12, 4), u3);
    }

    private static void AesEncryptExpand(Span<uint> eKey, ReadOnlySpan<byte> key)
    {
        eKey[0] = ReadU32Le(key.Slice(0, 4));
        eKey[1] = ReadU32Le(key.Slice(4, 4));
        eKey[2] = ReadU32Le(key.Slice(8, 4));
        eKey[3] = ReadU32Le(key.Slice(12, 4));
        eKey[4] = ReadU32Le(key.Slice(16, 4));
        eKey[5] = ReadU32Le(key.Slice(20, 4));
        eKey[6] = ReadU32Le(key.Slice(24, 4));
        eKey[7] = ReadU32Le(key.Slice(28, 4));

        int baseIdx = 0;
        for (int index = 0; index < RoundCount / 2; index++, baseIdx += 8)
        {
            eKey[baseIdx + 8] = eKey[baseIdx + 0] ^ RotateRight(AesEncryptMix(eKey[baseIdx + 7]), 8) ^ _rcon[index];
            eKey[baseIdx + 9] = eKey[baseIdx + 1] ^ eKey[baseIdx + 8];
            eKey[baseIdx + 10] = eKey[baseIdx + 2] ^ eKey[baseIdx + 9];
            eKey[baseIdx + 11] = eKey[baseIdx + 3] ^ eKey[baseIdx + 10];

            if (index < RoundCount / 2 - 1)
            {
                eKey[baseIdx + 12] = eKey[baseIdx + 4] ^ AesEncryptMix(eKey[baseIdx + 11]);
                eKey[baseIdx + 13] = eKey[baseIdx + 5] ^ eKey[baseIdx + 12];
                eKey[baseIdx + 14] = eKey[baseIdx + 6] ^ eKey[baseIdx + 13];
                eKey[baseIdx + 15] = eKey[baseIdx + 7] ^ eKey[baseIdx + 14];
            }
        }
    }

    private static void AesDecryptExpand(Span<uint> dKey, ReadOnlySpan<byte> key)
    {
        Span<uint> eKey = stackalloc uint[4 * (RoundCount + 1)];
        AesEncryptExpand(eKey, key);

        int e = 4 * RoundCount;
        int d = 0;

        dKey[d++] = eKey[e++];
        dKey[d++] = eKey[e++];
        dKey[d++] = eKey[e++];
        dKey[d++] = eKey[e];
        e -= 7;

        for (int round = 1; round < RoundCount; round++)
        {
            dKey[d++] = AesDecryptMix(eKey[e + 0]);
            dKey[d++] = AesDecryptMix(eKey[e + 1]);
            dKey[d++] = AesDecryptMix(eKey[e + 2]);
            dKey[d++] = AesDecryptMix(eKey[e + 3]);
            e -= 4;
        }

        dKey[d++] = eKey[e + 0];
        dKey[d++] = eKey[e + 1];
        dKey[d++] = eKey[e + 2];
        dKey[d] = eKey[e + 3];
    }

    private static uint AesEncryptMix(uint x)
    {
        return _sBox[(byte) (x >> 0)] |
               ((uint) _sBox[(byte) (x >> 8)] << 8) |
               ((uint) _sBox[(byte) (x >> 16)] << 16) |
               ((uint) _sBox[(byte) (x >> 24)] << 24);
    }

    private static uint AesDecryptMix(uint x)
    {
        return _itBox[_sBox[(byte) (x >> 0)]] ^
               RotateLeft(_itBox[_sBox[(byte) (x >> 8)]], 8) ^
               RotateLeft(_itBox[_sBox[(byte) (x >> 16)]], 16) ^
               RotateLeft(_itBox[_sBox[(byte) (x >> 24)]], 24);
    }

    private static uint AesDec(uint a, uint b, uint c, uint d)
    {
        return _itBox[(byte) (a >> 0)] ^
               RotateLeft(_itBox[(byte) (b >> 8)], 8) ^
               RotateLeft(_itBox[(byte) (c >> 16)], 16) ^
               RotateLeft(_itBox[(byte)(d >> 24)], 24);
    }

    private static uint AesDecLast(uint a, uint b, uint c, uint d)
    {
        return _isBox[(byte) (a >> 0)] |
               ((uint) _isBox[(byte) (b >> 8)] << 8) |
               ((uint) _isBox[(byte) (c >> 16)] << 16) |
               ((uint) _isBox[(byte) (d >> 24)] << 24);
    }

    private static uint RotateRight(uint x, int n)
        => BitOperations.RotateRight(x, n);
    private static uint RotateLeft(uint x, int n)
        => BitOperations.RotateLeft(x, n);

    private static uint ReadU32Le(ReadOnlySpan<byte> src)
        => BinaryPrimitives.ReadUInt32LittleEndian(src);
    private static void WriteU32Le(Span<byte> dst, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(dst, value);

    private static readonly byte[] _rcon =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] _sBox =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] _isBox =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly uint[] _itBox =
    [
        000000000u, 0000000000u, 000000000u, 0000000000u, 000000000u, 000000000u, 000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 00000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 00000000u,
        0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 000000000u, 0u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u,
        0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 00000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 000000000u, 0000000000u,
        000000000u, 0000000000u, 0000000000u, 0000000000u, 00000000u, 0000000000u, 0000000000u, 000000000u,
        0000000000u, 000000000u, 000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u,
        0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u,
        0000000000u, 000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        000000000u, 000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 000000000u, 0000000000u, 000000000u, 000000000u, 000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 00000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u,
        0000000000u, 0000000000u, 0000000000u, 000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u,
        0000000000u, 0000000000u, 0000000000u, 0000000000u, 000000000u, 000000000u, 0000000000u, 0000000000u,
        000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u, 0000000000u
    ];
}
