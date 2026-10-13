using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.GameTypes.Tencent.RocoKingdomWorld.Encryption.Algorithms;
using CUE4Parse.Utils;

namespace CUE4Parse.GameTypes.Tencent.RocoKingdomWorld.Encryption;

public static class FConfigurableCrypto
{
    private readonly record struct FCryptoStrategy(byte KeyId, byte AlgorithmId)
    {
        private const byte FileBasedKeyId = 0x80;

        public bool IsFileBasedKey => KeyId == FileBasedKeyId;
        public bool IsDefaultAesKey => KeyId == 0;

        public static FCryptoStrategy[] CreateTable(int algorithmCount, int keyCount)
        {
            // Default AES + static keys + file-based key per algorithm
            var strategies = new FCryptoStrategy[1 + algorithmCount * keyCount];

            var index = 0;
            strategies[index++] = new(0, 0); // Default AES
            for (byte algorithmId = 0; algorithmId < algorithmCount; algorithmId++)
            {
                for (byte keyId = 1; keyId < keyCount; keyId++) // the last key is not used??
                {
                    strategies[index++] = new(keyId, algorithmId);
                }

                strategies[index++] = new(FileBasedKeyId, algorithmId);
            }

            return strategies;
        }
    }

    private static readonly IConfigurableCryptoAlgorithm[] _algorithms =
    [
        new FConfigurableCryptoAlgorithmAES(),
        new FConfigurableCryptoAlgorithmNotImplemented("SM4"),
        new FConfigurableCryptoAlgorithmMLE(),
        new FConfigurableCryptoAlgorithmNotImplemented("RC5"),
        new FConfigurableCryptoAlgorithmNotImplemented("XTEA"),
        new FConfigurableCryptoAlgorithmNotImplemented("Speck"),
        new FConfigurableCryptoAlgorithmNotImplemented("Salsa20"),
        new FConfigurableCryptoAlgorithmNotImplemented("Simon"),
        new FConfigurableCryptoAlgorithmNotImplemented("00000000")
    ];

    private static readonly byte[][] _keyMaterial =
    [
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ],
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ],
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ],
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ],
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        ]
    ];
    private static readonly FCryptoStrategy[] _strategies = FCryptoStrategy.CreateTable(_algorithms.Length, _keyMaterial.Length);
    private static ReadOnlySpan<byte> DerivationSeed => "00000000000000000000000000000000"u8;

    private sealed class FConfigurableCryptoAlgorithmNotImplemented(string name) : IConfigurableCryptoAlgorithm
    {
        public byte[] Decrypt(byte[] ciphertext, ReadOnlyMemory<byte> key)
        {
            throw new NotImplementedException($"Algorithm {name} not yet implemented");
        }
    }

    private static byte[] DeriveFileNameKey(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var encoded = Encoding.Unicode.GetBytes(name);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(encoded);
        sha1.AppendData(DerivationSeed[..16]);

        var shaDigest = sha1.GetCurrentHash();

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(shaDigest);
        md5.AppendData(DerivationSeed[16..]);

        var xorKey = md5.GetCurrentHash();

        var outputKey = new byte[32];
        TensorUtils.Xor(xorKey.AsSpan()[..16], DerivationSeed[..16], outputKey.AsSpan(0, 16));

        for (int i = 0; i < 16; i++)
        {
            outputKey[16 + i] = (byte) (xorKey[i] ^ DerivationSeed[DerivationSeed.Length - i - 1]);
        }

        return outputKey;
    }

    private static byte[] DeriveKey(byte[] keyMaterial)
    {
        var outputKey = new byte[keyMaterial.Length];
        var outputKeySpan = outputKey.AsSpan();

        var processed = 0;
        byte round = 0;

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        while (processed != outputKey.Length)
        {
            sha1.AppendData(keyMaterial);
            sha1.AppendData(DerivationSeed);
            sha1.AppendData(new ReadOnlySpan<byte>(ref round));

            var currentHash = sha1.GetHashAndReset();
            var currentSize = Math.Min(outputKey.Length - processed, currentHash.Length);
            currentHash.AsSpan(0, currentSize).CopyTo(outputKeySpan.Slice(processed, currentSize));

            round++;
            processed += currentSize;
        }

        return outputKey;
    }

    private static byte[] GetKeyForStrategy(in FCryptoStrategy strategy, FAesKey? masterKey, string? fileName)
    {
        return strategy switch
        {
            { IsDefaultAesKey: true } => masterKey?.Key ?? throw new ArgumentNullException(nameof(masterKey)),
            { IsFileBasedKey: true } => fileName is not null ? DeriveFileNameKey(fileName) : throw new ArgumentNullException(nameof(fileName)),
            _ => DeriveKey(_keyMaterial[strategy.KeyId - 1])
        };
    }

    private static (IConfigurableCryptoAlgorithm Algorithm, byte[] Key) ResolveCryptoConfig(byte strategyIndex, FAesKey? masterKey, string? fileName)
    {
        if (strategyIndex >= _strategies.Length)
            throw new ArgumentOutOfRangeException(nameof(strategyIndex), strategyIndex, "Invalid crypto strategy index");

        var strategy = _strategies[strategyIndex];
        return (_algorithms[strategy.AlgorithmId], GetKeyForStrategy(strategy, masterKey, fileName));
    }

    public static byte[] Decrypt(byte[] ciphertext, byte strategyIndex, FAesKey? masterKey, string? fileName)
    {
        var (algorithm, key) = ResolveCryptoConfig(strategyIndex, masterKey, fileName);
        return algorithm.Decrypt(ciphertext, key);
    }

    public static byte[] DecryptChunked(byte[] ciphertext, byte strategyIndex, FAesKey? masterKey, string? fileName)
    {
        var (algorithm, key) = ResolveCryptoConfig(strategyIndex, masterKey, fileName);
        return algorithm.DecryptChunked(ciphertext, key);
    }
}
