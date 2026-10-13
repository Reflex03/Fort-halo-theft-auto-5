using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Exceptions;
using Org.BouncyCastle.Utilities;

namespace CUE4Parse.GameTypes.Tencent.PUBGMobile.Encryption.SM4;

public enum EPUBGMobileEncryptionMethod
{
    ChainedXor = 16,
    SM4 = 17,
    LiteSaltSM4 = 18, // PUBG Lite
    // SM4_19 = 19,
    SaltSM4Min = 31,
    SaltSM4Max = 222
}

// sub_8887A48, sub_8887E2C
public static class PUBGMobileSM4
{
    private const int BLOCK_SIZE = 16;
    private static readonly byte[] _sm4KeyGlobal = SHA1.HashData(Encoding.ASCII.GetBytes("0000000000000000")); // sub_A149F18, there's also "0000000000000000" for 0x13, haven't seen it used though
    private const string SM4KeyLite = "00000000000000000000";

    // Constructed at sub_A18BE0C to qword_F1B9528
    private static readonly string[] _staticPathKeySalts =
    [
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000", "00000000000000000000", "00000000000000000000",
        "00000000000000000000"
    ];

    // Dynamic encryption keys come from the TCP
    private static readonly Dictionary<uint, string> _dynamicKeySalts = new()
    {
        [0] = "00000000000000000000",
        [1] = "00000000000000000000",
        [2] = "00000000000000000000",
        [3] = "00000000000000000000",
        [4] = "00000000000000000000",
        [5] = "00000000000000000000",
        [6] = "00000000000000000000",
        [7] = "00000000000000000000",
        [8] = "00000000000000000000",
        [9] = "00000000000000000000",
        [10] = "00000000000000000000",
        [11] = "00000000000000000000",
        [12] = "00000000000000000000",
        [13] = "00000000000000000000",
        [14] = "00000000000000000000",
        [15] = "00000000000000000000",
        [16] = "00000000000000000000",
        [17] = "00000000000000000000",
        [18] = "00000000000000000000",
        [19] = "00000000000000000000",
        [20] = "00000000000000000000",
        [21] = "00000000000000000000",
        [22] = "00000000000000000000",
        [23] = "00000000000000000000",
        [24] = "00000000000000000000",
        [25] = "00000000000000000000",
        [26] = "00000000000000000000",
        [27] = "00000000000000000000",
        [28] = "00000000000000000000",
        [29] = "00000000000000000000",
        [30] = "00000000000000000000",
        [31] = "00000000000000000000",
        [32] = "00000000000000000000",
        [33] = "00000000000000000000",
        [34] = "00000000000000000000",
        [35] = "00000000000000000000",
        [36] = "00000000000000000000",
        [37] = "00000000000000000000",
        [42] = "00000000000000000000",
        [43] = "00000000000000000000",
        [44] = "00000000000000000000"
    };

    public static byte[] Decrypt(byte[] bytes, int beginOffset, int count, string path, EPUBGMobileEncryptionMethod encryptionMethod, uint encryptionKeyId)
    {
        if (beginOffset > bytes.Length - count)
            throw new ArgumentException("beginOffset + count is larger than the length of bytes");
        if (encryptionMethod is EPUBGMobileEncryptionMethod.SM4 or EPUBGMobileEncryptionMethod.LiteSaltSM4
                or (>= EPUBGMobileEncryptionMethod.SaltSM4Min and <= EPUBGMobileEncryptionMethod.SaltSM4Max) && count % BLOCK_SIZE != 0)
            throw new ArgumentException($"{nameof(count)} must be a multiple of {BLOCK_SIZE}", nameof(count));

        var output = new byte[count];
        Buffer.BlockCopy(bytes, beginOffset, output, 0, count);
        switch (encryptionMethod)
        {
            case >= EPUBGMobileEncryptionMethod.SaltSM4Min and <= EPUBGMobileEncryptionMethod.SaltSM4Max:
            {
                var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
                var name = path.AsSpan(separator + 1);
                var extension = name.LastIndexOf('.');
                var cleanName = (extension >= 0 ? name[..extension] : name).ToString().ToLowerInvariant();

                var saltIndex = encryptionMethod - EPUBGMobileEncryptionMethod.SaltSM4Min;
                if (saltIndex >= _staticPathKeySalts.Length)
                    throw new ParserException($"Static encryption key with index {saltIndex} is not available");

                var salt = _staticPathKeySalts[saltIndex];
                var keySource = string.Concat(cleanName, salt, encryptionMethod.ToString().ToLowerInvariant());

                Span<byte> key = stackalloc byte[SHA1.HashSizeInBytes];
                SHA1.HashData(Encoding.ASCII.GetBytes(keySource), key);

                DecryptSm4(output, key[..BLOCK_SIZE]);
                break;
            }
            case EPUBGMobileEncryptionMethod.LiteSaltSM4:
            {
                var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
                var name = path.AsSpan(separator + 1);
                var extension = name.LastIndexOf('.');
                var cleanName = (extension >= 0 ? name[..extension] : name).ToString().ToLowerInvariant();
                var keySource = string.Concat(cleanName, SM4KeyLite);

                Span<byte> key = stackalloc byte[SHA1.HashSizeInBytes];
                SHA1.HashData(Encoding.ASCII.GetBytes(keySource), key);
                DecryptSm4(output, key[..BLOCK_SIZE]);
                break;
            }
            case EPUBGMobileEncryptionMethod.SM4:
            {
                var isDynamicallyEncrypted = (encryptionKeyId & 0x80000001) == 0x00000002;

                var key = _sm4KeyGlobal;
                if (isDynamicallyEncrypted)
                {
                    var encryptionKeyIndex = encryptionKeyId & 0xFFFFFF;
                    if (!_dynamicKeySalts.TryGetValue(encryptionKeyIndex, out var keySalt))
                        throw new ParserException($"Dynamic encryption key with index {encryptionKeyIndex} is not available");

                    Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
                    SHA1.HashData(Encoding.ASCII.GetBytes(keySalt), hash);

                    key = hash[..16].ToArray();
                }

                DecryptSm4(output, key);
                break;
            }
            case EPUBGMobileEncryptionMethod.ChainedXor:
                Decrypt16(output);
                break;
            default:
                throw new ParserException($"Unknown PUBG Mobile encryption method {encryptionMethod}");
        }

        return output;
    }

    private static void Decrypt16(Span<byte> output)
    {
        if (output.Length < 4)
            return;

        ReadOnlySpan<byte> key = [0x00, 0x00, 0x00, 0x00];
        for (var i = 0; i < 4; i++)
            output[i] ^= key[(output.Length + i) & 3];
        for (var i = 4; i < output.Length; i++)
            output[i] ^= output[i - 4];
    }

    private static void DecryptSm4(Span<byte> output, ReadOnlySpan<byte> key)
    {
        var engine = new PUBGMobileSM4Engine(key);
        for (var offset = 0; offset < output.Length; offset += BLOCK_SIZE)
            engine.ProcessBlock(output.Slice(offset, BLOCK_SIZE));
    }

    // Standard SM4 but with different constants
    private sealed class PUBGMobileSM4Engine
    {
        private readonly uint[] _rk;

        private static readonly uint[] _fk =
        [
            0x00000000, 0x00000000, 0x00000000, 0x00000000
        ];

        private static readonly uint[] _ck =
        [
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000,
            0x00000000, 0x00000000, 0x00000000, 0x00000000
        ];

        private static readonly byte[] _sbox =
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

        public PUBGMobileSM4Engine(ReadOnlySpan<byte> key)
        {
            if (key.Length < BLOCK_SIZE)
                throw new ArgumentException($"SM4 key must be at least {BLOCK_SIZE} bytes", nameof(key));

            _rk = new uint[32];
            ExpandKey(key);
        }

        // non-linear substitution tau.
        private static uint tau(uint A)
        {
            uint b0 = _sbox[A >> 24];
            uint b1 = _sbox[(A >> 16) & 0xFF];
            uint b2 = _sbox[(A >> 8) & 0xFF];
            uint b3 = _sbox[A & 0xFF];

            return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        }

        private static uint L_ap(uint B)
        {
            return B ^ Integers.RotateLeft(B, 13) ^ Integers.RotateLeft(B, 23);
        }

        private static uint T_ap(uint Z)
        {
            return L_ap(tau(Z));
        }

        // Key expansion
        private void ExpandKey(ReadOnlySpan<byte> key)
        {
            uint K0 = BinaryPrimitives.ReadUInt32BigEndian(key) ^ _fk[0];
            uint K1 = BinaryPrimitives.ReadUInt32BigEndian(key[4..]) ^ _fk[1];
            uint K2 = BinaryPrimitives.ReadUInt32BigEndian(key[8..]) ^ _fk[2];
            uint K3 = BinaryPrimitives.ReadUInt32BigEndian(key[12..]) ^ _fk[3];

            _rk[31] = K0 ^ T_ap(K1     ^ K2     ^ K3     ^ _ck[0]);
            _rk[30] = K1 ^ T_ap(K2     ^ K3     ^ _rk[31] ^ _ck[1]);
            _rk[29] = K2 ^ T_ap(K3     ^ _rk[31] ^ _rk[30] ^ _ck[2]);
            _rk[28] = K3 ^ T_ap(_rk[31] ^ _rk[30] ^ _rk[29] ^ _ck[3]);
            for (int i = 27; i >= 0; --i)
            {
                _rk[i] = _rk[i + 4] ^ T_ap(_rk[i + 3] ^ _rk[i + 2] ^ _rk[i + 1] ^ _ck[31 - i]);
            }
        }

        // Linear substitution L
        private static uint L(uint B)
        {
            return B ^ Integers.RotateLeft(B, 2) ^ Integers.RotateLeft(B, 10) ^ Integers.RotateLeft(B, 18) ^ Integers.RotateLeft(B, 24);
        }

        // Mixer-substitution T
        private static uint T(uint Z)
        {
            return L(tau(Z));
        }

        public int ProcessBlock(Span<byte> data)
        {
            uint X0 = BinaryPrimitives.ReadUInt32BigEndian(data);
            uint X1 = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            uint X2 = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            uint X3 = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);

            for (int i = 0; i < 32; i += 4)
            {
                X0 ^= T(X1 ^ X2 ^ X3 ^ _rk[i    ]);  // F0
                X1 ^= T(X2 ^ X3 ^ X0 ^ _rk[i + 1]);  // F1
                X2 ^= T(X3 ^ X0 ^ X1 ^ _rk[i + 2]);  // F2
                X3 ^= T(X0 ^ X1 ^ X2 ^ _rk[i + 3]);  // F3
            }

            BinaryPrimitives.WriteUInt32BigEndian(data[0..], X3);
            BinaryPrimitives.WriteUInt32BigEndian(data[4..], X2);
            BinaryPrimitives.WriteUInt32BigEndian(data[8..], X1);
            BinaryPrimitives.WriteUInt32BigEndian(data[12..], X0);

            return BLOCK_SIZE;
        }
    }
}
