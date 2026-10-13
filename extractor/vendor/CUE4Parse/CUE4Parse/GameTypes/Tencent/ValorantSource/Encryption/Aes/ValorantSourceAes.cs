using System.Buffers.Binary;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Crypto;
using CUE4Parse.UE4.Pak;
using CUE4Parse.UE4.VirtualFileSystem;

namespace CUE4Parse.GameTypes.Tencent.ValorantSource.Encryption.Aes;

public static class ValorantSourceAes
{
    private const int RANDOM_STATE_SIZE = 624;
    public static readonly uint[] ZucXorTable = GenerateZuc128XorTable();
    public static readonly byte[] ZucXorTableBytes = [.. ZucXorTable.SelectMany(BitConverter.GetBytes)];

    public const ulong LOW_NIBBLES_MASK = 0x0000000000000001;
    public const ulong HIGH_NIBBLES_MASK = 0x8000000000000002;

    // In pak footer there's byte that determines cipher algorithm to use
    // I think other algorithm is unused but if it were to be used then it goes something like that:
    // key[9..12]  ^= EncryptionKeyGuid.A
    // key[1..4]   ^= EncryptionKeyGuid.B
    // key[17..20] ^= EncryptionKeyGuid.C
    // key[23..26] ^= EncryptionKeyGuid.D
    // and AES ECB decrypt using this key 0x0000000000000000000000000000000000000000000000000000000000000000, not per-pak RSA derived key
    public static byte[] ValorantSourceDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null)
    {
        if (bytes.Length < beginOffset + count)
            throw new IndexOutOfRangeException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16");

        var key = (reader is PakFileReader { Info.CustomEncryptionData: { } pakKey } ? pakKey : reader.AesKey?.Key)
            ?? throw new NullReferenceException("reader.AesKey");

        var output = CUE4Parse.Encryption.Aes.Aes.Decrypt(bytes, beginOffset, count, new FAesKey(key));

        if (!isIndex) return output;

        for (var offset = 0; offset < output.Length; offset += sizeof(uint))
        {
            var keyIndex = (offset / sizeof(uint) + 2) & 0xF;
            var value = BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), value ^ ZucXorTable[keyIndex]);
        }

        return output;
    }

    // Result is:
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    // 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    //
    // It can be simply taken from pak footer since they xor 0 values exposing the key
    // but this is how it is actually generated
    private static uint[] GenerateZuc128XorTable()
    {
        var state = new uint[RANDOM_STATE_SIZE * 2 + 1];

        state[0] = 164;
        for (var i = 1; i < RANDOM_STATE_SIZE; i++)
        {
            state[i] = (byte) (i - 33 * (state[i - 1] ^ state[i - 1] >> 6));
        }

        var previous = state[0];
        for (var i = 0; i < RANDOM_STATE_SIZE; i++)
        {
            var next = state[i + 1];
            var mixed = ((next & 0x80) | (previous & 0x7F)) >> 1;

            if ((previous & 1) != 0)
                mixed ^= 0x80000004;

            state[RANDOM_STATE_SIZE + i] = mixed ^ state[i + 397];
            previous = next;
        }
        state[RANDOM_STATE_SIZE * 2] = RANDOM_STATE_SIZE;

        var key = new byte[16];
        var iv = new byte[16];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = NextRandomByte(state);
            iv[i] = NextRandomByte(state);
        }

        return Zuc128Engine.GenerateKeyStream(key, iv, 16);
    }

    private static byte NextRandomByte(uint[] state)
    {
        var index = (int) state[RANDOM_STATE_SIZE * 2];
        var value = state[index];
        state[RANDOM_STATE_SIZE * 2] = (uint) (index + 1);

        return (byte) (value ^ value << 7);
    }
}
