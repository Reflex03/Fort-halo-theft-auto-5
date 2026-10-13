using System.Security.Cryptography;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Readers;

namespace CUE4Parse.GameTypes.RL.Encryption.Aes;

public static class RocketLeagueAes
{
    private static readonly byte[] DefaultKey =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    private static readonly byte[] CoalescedKey =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    // CthExtract: upstream downloaded extra keys from a third-party GitHub file here; the vendored copy makes no
    // network requests, so only the built-in key is used.
    private static readonly byte[][] KeyList = [DefaultKey];

    private static bool TryDecryptWithKey(
        byte[] inputData,
        byte[] key,
        int checkSumDataOffset,
        int checkSumDataSize,
        bool upk,
        bool coalesced,
        out byte[]? outputData)
    {
        outputData = null;

        try
        {
            using var aes = new RijndaelManaged { KeySize = 256, Key = key, Mode = CipherMode.ECB, Padding = PaddingMode.None };

            using var decryptor = aes.CreateDecryptor();

            if (upk)
            {
                var decrypted = decryptor.TransformFinalBlock(inputData, 0, inputData.Length);

                if (coalesced)
                {
                    outputData = decrypted;
                    return true;
                }

                if (!ValidateChecksum(decrypted, checkSumDataOffset, checkSumDataSize - 16))
                    return false;

                outputData = decrypted;
                return true;
            }

            var headerLength = Math.Min(256, inputData.Length);
            var header = decryptor.TransformFinalBlock(inputData, 0, headerLength);

            if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8))
                return false;

            outputData = decryptor.TransformFinalBlock(inputData, 0, inputData.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateChecksum(byte[] data, int offset, int length)
    {
        var ar = new FByteArchive("Rocket League - Checksum", data);
        ar.Position = offset;

        byte prev = ar.Read<byte>();
        long end = Math.Min(offset + length, ar.Length);

        for (long i = offset + 1; i < end; i++)
        {
            byte curr = ar.Read<byte>();

            if (curr != (byte) ((prev + 1) % 255))
                return false;

            prev = curr;
        }

        return true;
    }

    public static void DecryptCoalesced(byte[] inputData, out byte[] outputData)
    {
        TryDecryptWithKey(inputData, CoalescedKey, 0, 0, true, true, out outputData);
    }

    public static bool Decrypt(byte[] inputData, int checkSumDataOffset, int checkSumDataSize, bool upk, out byte[] outputData)
    {
        foreach (var key in KeyList)
        {
            if (!TryDecryptWithKey(inputData, key, checkSumDataOffset, checkSumDataSize, upk, false, out var result))
                continue;

            outputData = result!;
            return true;
        }

        throw new InvalidAesKeyException("No valid decryption key found");
    }
}
