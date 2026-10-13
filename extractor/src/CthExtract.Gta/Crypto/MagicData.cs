using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using CthExtract.Core;

namespace CthExtract.Gta.Crypto;

/// <summary>Where the NG key blob comes from. The default fetches it over HTTPS; tests supply bytes.</summary>
public interface IMagicSource
{
    /// <summary>Human-readable origin for the log.</summary>
    string Describe { get; }
    /// <summary>The raw blob, or null with a reason.</summary>
    byte[]? Fetch(Log log, CancellationToken ct, out string? error);
}

/// <summary>
/// CodeWalker's <c>magic.dat</c>, fetched at run time from a pinned commit on raw.githubusercontent.com.
/// We never ship it and never write it (or anything decoded from it) to disk.
/// </summary>
public sealed class PinnedHttpMagicSource : IMagicSource
{
    public const string Url =
        "https://raw.githubusercontent.com/dexyfex/CodeWalker/485d56bec00262ed7fa472261cce7bbc6202b96e/CodeWalker.Core/Resources/magic.dat";
    public const string Sha256 = "dc35981f822e892ced3aa81d31e7a96927d573ee28f67417592b5afeaf330832";
    public const int ExpectedLength = 154069;

    public string Describe => Url;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);

    public byte[]? Fetch(Log log, CancellationToken ct, out string? error)
    {
        try
        {
            // the pinned blob is 154 KB; never buffer more than 1 MB whatever the server sends (SHA-256 checked later)
            using var http = new HttpClient { Timeout = Timeout, MaxResponseContentBufferSize = 1 << 20 };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("cthextract/0.1 (+Craft Theft Halo extractor)");
            log.Info($"[gta] fetching NG key blob from {Url}");
            var bytes = http.GetByteArrayAsync(Url, ct).GetAwaiter().GetResult();
            error = null;
            return bytes;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            error = $"download failed: {e.GetType().Name}: {e.Message}";
            return null;
        }
    }
}

/// <summary>A blob already in memory (tests, or a future offline route).</summary>
public sealed class BytesMagicSource(byte[]? bytes, string describe = "in-memory") : IMagicSource
{
    public string Describe => describe;
    public byte[]? Fetch(Log log, CancellationToken ct, out string? error)
    {
        error = bytes is null ? "no blob" : null;
        return bytes;
    }
}

/// <summary>
/// Decodes CodeWalker's magic.dat layout (format facts; no CodeWalker code):
/// blob[i] -= sum of four <c>new Random((int)joaat(aesKey)).NextBytes</c> streams; AES-256-ECB decrypt (tail
/// untouched); raw deflate; then 101 NG keys (0x110 each), 272 NG tables (0x400 each), the 256-byte LUT and the
/// 16-byte AWC key. Every key, table and the LUT must match the expected SHA1 list, else nothing is returned.
/// Because the layout details come from source reading, a few byte-combination / inflate variants are tried and
/// the hash check picks the right one.
/// </summary>
public static class MagicData
{
    public static int PayloadLength =>
        KeyHashes.NgKeyCount * KeyHashes.NgKeyBytes + KeyHashes.NgTableCount * KeyHashes.NgTableBytes + KeyHashes.LutBytes;

    public static NgKeySet? Decode(byte[] blob, byte[] aesKey, KeyHashes hashes, Log log, out string detail)
    {
        string? lastProblem = null;
        var seeds = new List<int> { unchecked((int)Hashes.Joaat(aesKey.AsSpan())) };
        int folded = unchecked((int)Hashes.Joaat(new string(aesKey.Select(b => (char)b).ToArray())));
        if (!seeds.Contains(folded)) seeds.Add(folded);
        foreach (var seed in seeds)
        foreach (var subtract in new[] { true, false })
        {
            byte[] work = Deobfuscate(blob, seed, subtract);
            AesCrypto.DecryptInPlace(work, aesKey);
            foreach (var zlib in new[] { false, true })
            {
                byte[]? plain = TryInflate(work, zlib);
                if (plain is null) { lastProblem = $"inflate ({(zlib ? "zlib" : "raw deflate")}) failed"; continue; }
                if (plain.Length < PayloadLength) { lastProblem = $"decoded payload is {plain.Length} bytes, need {PayloadLength}"; continue; }
                var set = Parse(plain, hashes, out var problem);
                if (set is null) { lastProblem = problem; continue; }
                detail = $"NG keys verified: {KeyHashes.NgKeyCount} keys, {KeyHashes.NgTableCount} tables and the LUT match the SHA1 list" +
                         $"{(set.AwcKey is null ? "; no AWC key in blob" : "; AWC key present")} (variant {(subtract ? "sub" : "add")}/{(zlib ? "zlib" : "deflate")})";
                log.Info("[gta] " + detail);
                return set;
            }
        }
        detail = $"NG key blob did not verify against the SHA1 list ({lastProblem ?? "unknown"})";
        log.Warn("[gta] " + detail);
        return null;
    }

    /// <summary>Undo the additive keystream: four NextBytes(len) streams from Random(seed = joaat(aesKey)).</summary>
    public static byte[] Deobfuscate(byte[] blob, int seed, bool subtract)
    {
        var m = (byte[])blob.Clone();
        var rnd = new Random(seed);
        var streams = new byte[4][];
        for (int s = 0; s < 4; s++) { streams[s] = new byte[m.Length]; rnd.NextBytes(streams[s]); }
        for (int i = 0; i < m.Length; i++)
        {
            int sum = streams[0][i] + streams[1][i] + streams[2][i] + streams[3][i];
            m[i] = subtract ? (byte)(m[i] - sum) : (byte)(m[i] + sum);
        }
        return m;
    }

    private static byte[]? TryInflate(byte[] data, bool zlib)
    {
        try
        {
            using var src = new MemoryStream(data);
            using Stream z = zlib ? new ZLibStream(src, CompressionMode.Decompress) : new DeflateStream(src, CompressionMode.Decompress);
            using var dst = new MemoryStream();
            z.CopyTo(dst);
            return dst.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    private static NgKeySet? Parse(byte[] b, KeyHashes hashes, out string? problem)
    {
        int o = 0;
        var keys = new uint[KeyHashes.NgKeyCount][];
        for (int i = 0; i < KeyHashes.NgKeyCount; i++, o += KeyHashes.NgKeyBytes)
        {
            var span = b.AsSpan(o, KeyHashes.NgKeyBytes);
            if (!KeyHashes.Matches(span, hashes.NgKeys[i])) { problem = $"NG key {i} hash mismatch"; return null; }
            keys[i] = ToUInts(span);
        }
        var tables = new uint[17][][];
        for (int r = 0; r < 17; r++)
        {
            tables[r] = new uint[16][];
            for (int t = 0; t < 16; t++, o += KeyHashes.NgTableBytes)
            {
                var span = b.AsSpan(o, KeyHashes.NgTableBytes);
                if (!KeyHashes.Matches(span, hashes.NgTables[r * 16 + t])) { problem = $"NG table {r * 16 + t} hash mismatch"; return null; }
                tables[r][t] = ToUInts(span);
            }
        }
        var lut = b.AsSpan(o, KeyHashes.LutBytes).ToArray();
        if (!KeyHashes.Matches(lut, hashes.Lut)) { problem = "LUT hash mismatch"; return null; }
        o += KeyHashes.LutBytes;
        // The AWC key has no published hash; it is trusted because the blob's SHA-256 is pinned and every other
        // item decoded from the same AES stream verified.
        uint[]? awc = b.Length >= o + 16 ? ToUInts(b.AsSpan(o, 16)) : null;
        problem = null;
        return new NgKeySet { Keys = keys, Tables = tables, Lut = lut, AwcKey = awc };
    }

    private static uint[] ToUInts(ReadOnlySpan<byte> s)
    {
        var u = new uint[s.Length / 4];
        for (int i = 0; i < u.Length; i++) u[i] = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i * 4));
        return u;
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
