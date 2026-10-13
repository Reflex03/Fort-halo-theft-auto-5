using System.Numerics;
using CUE4Parse.GameTypes.ABI.UE4.Lua;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;

namespace CUE4Parse.GameTypes.ABI.Encryption.SM4;

public static class ABIDecryption
{
    public static readonly string[] encryptedFiles = ["ini", "lua", "uasset", "umap"];
    private static readonly byte[] _iniDecryptKey = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptKey37 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptKey38 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptKey39 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptKey3A = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    #region Mobile
    private static readonly byte[] _pakInfoMobileKey87 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _pakInfoMobileKey88 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly byte[] _pakIndexMobileKey = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly byte[] _iniDecryptMobileKey45 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _iniDecryptMobileKey46 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static readonly byte[] _uassetDecryptMobileKey38 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptMobileKey39 = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] _uassetDecryptMobileKey3A = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    #endregion

    private static readonly byte[] _uassetMagic = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
    private const int UassetMagicLength = 8;

    public static byte[] ABIDecrypt(byte[] bytes, int beginOffset, int count, bool isIndex, IAesVfsReader reader, object? customData = null)
    {
        if (bytes.Length < beginOffset + count)
            throw new IndexOutOfRangeException("beginOffset + count is larger than the length of bytes");
        if (count % 16 != 0)
            throw new ArgumentException("count must be a multiple of 16");

        var output = new byte[count];
        Buffer.BlockCopy(bytes, beginOffset, output, 0, count);

        if (isIndex)
        {
            if (reader.Game is GAME_ArenaBreakoutMobile)
            {
                Sm4Helper.Decrypt(ref output, _pakIndexMobileKey, SM4Mode.None, SboxMode.None);
            }
            else
            {
                if (reader.AesKey == null)
                    throw new NullReferenceException("reader.AesKey");

                Sm4Helper.Decrypt(ref output, reader.AesKey.Key, SM4Mode.A, SboxMode.None);
            }

            return output;
        }

        for (var i = 0; i < count; i++)
        {
            if (output[i] != 0 && output[i] != 0x93)
                output[i] ^= 0x93;
        }

        return output;
    }

    public static byte[] AbiDecryptPackageSummary(byte[] bytes)
    {
        var magic = BitConverter.ToUInt32(bytes);

        // For PC base is 0x00000001, for mobile 0x00000002
        (byte[] currentKey, SM4Mode mode, SboxMode sboxMode) = magic switch
        {
            0x00000003 or 0x00000004 => (_uassetDecryptKey37, SM4Mode.C, SboxMode.Mode37),
            0x00000005 => (_uassetDecryptKey38, SM4Mode.C, SboxMode.Mode38),
            0x00000006 => (_uassetDecryptKey39, SM4Mode.D, SboxMode.Mode39),
            0x00000007 => (_uassetDecryptKey3A, SM4Mode.E, SboxMode.Mode3A),
            0x00000008 => (_uassetDecryptMobileKey38, SM4Mode.C, SboxMode.Mode38Mobile),
            0x00000009 => (_uassetDecryptMobileKey39, SM4Mode.C, SboxMode.Mode39Mobile),
            0x0000000A => (_uassetDecryptMobileKey3A, SM4Mode.MobileD, SboxMode.Mode3AMobile),
            _ => throw new ParserException($"FilePackageSummary magic is different 0x{magic:X} (encryption is not supported)")
        };

        var encryptedLength = BitConverter.ToUInt16(bytes, 6);
        var unencryptedLength = encryptedLength + UassetMagicLength;
        var output = new byte[bytes.Length];
        Buffer.BlockCopy(_uassetMagic, 0, output, 0, UassetMagicLength);
        Buffer.BlockCopy(bytes, unencryptedLength, output, unencryptedLength, bytes.Length - unencryptedLength);

        var encryptedBlock = new byte[encryptedLength];
        Buffer.BlockCopy(bytes, UassetMagicLength, encryptedBlock, 0, encryptedLength);

        Sm4Helper.Decrypt(ref encryptedBlock, currentKey, mode, sboxMode);

        Buffer.BlockCopy(encryptedBlock, 0, output, UassetMagicLength, encryptedLength);
        return output;
    }

    public static byte[] AbiDecryptIni(byte[] bytes, EGame game)
    {
        if (bytes.Length < 8)
            throw new ArgumentException("ini file must be at least 8 bytes", nameof(bytes));
        if (bytes is not [0x1b, _, 0x55, ..])
            return bytes;

        var encryptionVersion = bytes[3];
        (byte[] key, SM4Mode mode) = encryptionVersion switch
        {
            0x45 when game is GAME_ArenaBreakoutMobile => (_iniDecryptMobileKey45, SM4Mode.None),
            0x46 when game is GAME_ArenaBreakoutMobile => (_iniDecryptMobileKey46, SM4Mode.F),
            0x41 when game is GAME_ArenaBreakoutInfinite => (_iniDecryptKey, SM4Mode.A),
            _ => throw new ParserException($"Unknown ABI SM4 mode: 0x{encryptionVersion:X2}")
        };

        var iniLength = BitConverter.ToInt32(bytes, 4);
        var length = iniLength.Align(16);

        var output = new byte[length];
        Buffer.BlockCopy(bytes, 8, output, 0, length);
        Sm4Helper.Decrypt(ref output, key, mode, SboxMode.None);

        return iniLength != length ? output.SubByteArray(iniLength) : output;
    }

    public static byte[] AbiDecryptLua(byte[] bytes, EGame game)
    {
        if (bytes.Length < 12)
            throw new ArgumentException("Lua file must be at least 12 bytes", nameof(bytes));

        var magic = BitConverter.ToUInt32(bytes);
        if (magic != 0x0000000B)
            return bytes;

        var decrypted = ABILuaReader.DecryptLuaBytecode(bytes, game is GAME_ArenaBreakoutMobile);

        return decrypted;
    }

    public static void DecryptAbiMobilePakInfo(byte[] data, uint tag)
    {
        var key = tag switch
        {
            0x87 => _pakInfoMobileKey87,
            0x88 => _pakInfoMobileKey88,
            _ => throw new ParserException($"Unknown Arena Breakout Mobile pak tag: 0x{tag:X}. Encryption is not supported.")
        };

        Sm4Helper.Decrypt(ref data, key, SM4Mode.B, SboxMode.None);
    }

    public static long DecodeIndexInfo(ulong encoded, ulong finalXor)
        => (long) (BitOperations.RotateRight(encoded ^ 0x000000000000000CUL, 23) ^ finalXor);

    public static FSHAHash DecodeIndexHash(Span<byte> hash)
    {
        var key = 0x000000000000000DUL;
        for (var index = 0; index < hash.Length; index++)
        {
            hash[index] ^= (byte) (key >> ((index & 7) * 8));
            key = BitOperations.RotateLeft(key, 7);
        }

        return new FSHAHash(hash);
    }
}
