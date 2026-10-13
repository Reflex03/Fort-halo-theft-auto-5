using System.Text;
using CUE4Parse.UE4.Readers;
using CUE4Parse.Utils;

namespace CUE4Parse.GameTypes.EOTU.Encryption;

public class EOTUStringEncryption
{
    private const string Suffix = "000000000";
    private const string Key = "00000000000000000000000000000000000000000000";

    public static string DecryptString(FArchive Ar) => DecryptString(Ar.ReadFString());
    public static string DecryptString(string encryptedString)
    {
        if (string.IsNullOrEmpty(encryptedString) || !encryptedString.EndsWith(Suffix, StringComparison.Ordinal))
            return encryptedString;

        var payload = encryptedString[..^Suffix.Length];
        var data = Convert.FromBase64String(payload);
        var keyBytes = Encoding.ASCII.GetBytes(Key);

        TensorUtils.Xor(data, keyBytes);

        return Encoding.Unicode.GetString(data).TrimEnd('\0');
    }
}
