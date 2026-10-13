using System.Reflection;
using System.Resources;
using System.Security.Cryptography;

namespace CUE4Parse_Conversion.Textures;

internal static class TextureNativeLibrary
{
    public static void Prepare(string dllName)
    {
        // CthExtract: crunch.dll / tegra_swizzle_x64.dll are stripped from the vendored copy; never write a DLL to disk.
        throw new NotSupportedException($"{dllName} is not available in this build (stripped native library)");
#pragma warning disable CS0162
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CUE4Parse_Conversion.Resources.{dllName}");
        if (stream == null)
            throw new MissingManifestResourceException($"Couldn't find {dllName} in Embedded Resources");

        var embeddedData = new byte[(int) stream.Length];
        stream.ReadExactly(embeddedData);

        var fileOk = false;
        var embeddedHash = SHA1.HashData(embeddedData);
        if (File.Exists(dllName))
            fileOk = embeddedHash.AsSpan().SequenceEqual(SHA1.HashData(File.ReadAllBytes(dllName)));

        if (!fileOk)
            File.WriteAllBytes(dllName, embeddedData);
#pragma warning restore CS0162
    }
}
