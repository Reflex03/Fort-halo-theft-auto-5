namespace CthExtract.Oodle.Tests;

/// <summary>
/// Optional external test data. Nothing here is in the repository; every test that needs it skips when it is absent.
/// <list type="table">
/// <item><term>CTH_OOZ_TESTDATA</term><description>ooz's testdata folder (dickens.kraken ... xml.leviathan; made with
/// real Oodle, GPL repo https://github.com/powzix/ooz). Falls back to $CTH_REFS/ooz/testdata.</description></item>
/// <item><term>CTH_SILESIA</term><description>the uncompressed Silesia corpus files (dickens, mozilla, ...), the
/// originals of those vectors (https://sun.aei.polsl.pl/~sdeor/corpus/silesia.zip).</description></item>
/// <item><term>CTH_OOZ_VECTORS</term><description>a folder of *.ooz vectors plus manifest.json
/// ([{vector, codec, level, size, sha256, native_roundtrip}]), produced locally with an Oodle-compatible compressor
/// and checked against the native ooz decoder.</description></item>
/// <item><term>CTH_OOZ_UE_BLOCKS</term><description>*.ooz files holding real Unreal Engine Oodle blocks (e.g. dumped
/// from CUE4Parse's Apache-2.0 test fixtures), optionally with native-ooz.sha256 ("name sha256" per line) giving
/// the native ooz decoder's output hash for each.</description></item>
/// </list>
/// An .ooz file is ooz's container: an 8-byte little-endian decompressed size (or 4 bytes, auto-detected like ooz),
/// then the raw Oodle stream.
/// </summary>
internal static class TestData
{
    public static string? Dir(string env, string? refsSubdir = null)
    {
        var d = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
            return d;
        if (refsSubdir != null)
        {
            var refs = Environment.GetEnvironmentVariable("CTH_REFS");
            if (!string.IsNullOrEmpty(refs) && Directory.Exists(Path.Combine(refs, refsSubdir)))
                return Path.Combine(refs, refsSubdir);
        }
        return null;
    }

    /// <summary>Reads an ooz container: returns the stream and its decompressed size.</summary>
    public static (byte[] Stream, int RawSize) ReadOoz(string path)
    {
        var input = File.ReadAllBytes(path);
        ulong v = BitConverter.ToUInt64(input, 0);
        int hdr = v >= 0x10000000000UL ? 4 : 8;
        long raw = hdr == 8 ? (long)v : BitConverter.ToUInt32(input, 0);
        return (input[hdr..], checked((int)raw));
    }

    public static string Sha256(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    public static int FuzzSeconds(int dflt)
    {
        var s = Environment.GetEnvironmentVariable("CTH_OOZ_FUZZ_SECONDS");
        return int.TryParse(s, out int v) && v > 0 ? v : dflt;
    }
}
