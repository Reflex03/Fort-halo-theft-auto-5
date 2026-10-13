using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract.Gta.Crypto;

/// <summary>
/// Finds the 32-byte PC AES key inside the player's own GTA5.exe / GTA5_Enhanced.exe by SHA1 (the hash is public,
/// the key is not shipped). Searches PE data sections first, then the whole file; 8-byte alignment first, then 4,
/// then 1. The found key is cached in cache/keys (it is the player's own key; it is re-verified by hash on load).
/// </summary>
public static class AesKeyFinder
{
    public sealed record Hit(byte[] Key, long Offset, int Alignment, string Where);

    public const string CacheFileName = "gta5_aes_key.json";

    /// <summary>Load the cached key if its SHA1 still matches, else search the exe and cache the result.</summary>
    public static byte[]? Get(string exePath, string? keysDir, byte[] expectedSha1, Log log, CancellationToken ct, out string detail)
    {
        var cached = keysDir is null ? null : LoadCache(Path.Combine(keysDir, CacheFileName), expectedSha1, log);
        if (cached is not null) { detail = "AES key from cache (SHA1 verified)"; return cached; }

        byte[] exe;
        try { exe = ReadShared(exePath); }
        catch (Exception e) { detail = $"cannot read {Path.GetFileName(exePath)}: {e.Message}"; return null; }

        var sw = Stopwatch.StartNew();
        var hit = Search(exe, expectedSha1, log, ct);
        if (hit is null)
        {
            detail = $"AES key not found in {Path.GetFileName(exePath)} ({exe.Length / 1048576.0:F1} MiB, searched alignments 8/4/1 in {sw.Elapsed.TotalSeconds:F1} s)";
            return null;
        }
        detail = $"AES key found in {Path.GetFileName(exePath)} at 0x{hit.Offset:X} ({hit.Where}, {hit.Alignment}-byte aligned) in {sw.Elapsed.TotalSeconds:F1} s";
        log.Info("[gta] " + detail);
        if (keysDir is not null)
        {
            try { SaveCache(Path.Combine(keysDir, CacheFileName), exePath, exe.LongLength, hit); }
            catch (Exception e) { log.Warn($"[gta] could not cache the AES key: {e.Message}"); }
        }
        return hit.Key;
    }

    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[fs.Length];
        fs.ReadExactly(buf);
        return buf;
    }

    /// <summary>Search order: PE initialised-data sections (.rdata, .data, others) at 8-byte alignment, then the whole
    /// file at 8, 4 and 1.</summary>
    public static Hit? Search(byte[] data, byte[] expectedSha1, Log log, CancellationToken ct, int keyLength = KeyHashes.AesKeyBytes)
    {
        var sections = PeSections(data);
        if (sections.Count > 0)
            log.Info($"[gta] exe sections: {string.Join(", ", sections.Select(s => $"{s.Name}@0x{s.Offset:X}+0x{s.Size:X}"))}");
        foreach (var s in sections.OrderBy(s => s.Name == ".rdata" ? 0 : s.Name == ".data" ? 1 : 2))
        {
            ct.ThrowIfCancellationRequested();
            var o = Scan(data, s.Offset, s.Size, 8, expectedSha1, keyLength, ct);
            if (o >= 0) return new Hit(data.AsSpan((int)o, keyLength).ToArray(), o, 8, $"section {s.Name}");
        }
        int previous = 0;
        foreach (int align in new[] { 8, 4, 1 })
        {
            ct.ThrowIfCancellationRequested();
            log.Info($"[gta] AES key search: whole file, {align}-byte alignment");
            var o = Scan(data, 0, data.LongLength, align, expectedSha1, keyLength, ct, skipMultipleOf: previous);
            if (o >= 0) return new Hit(data.AsSpan((int)o, keyLength).ToArray(), o, align, "whole file");
            previous = align;
        }
        return null;
    }

    /// <summary>Parallel SHA1 over every aligned window in [start, start+size). Returns the file offset or -1.</summary>
    public static long Scan(byte[] data, long start, long size, int align, byte[] expectedSha1, int keyLength, CancellationToken ct, int skipMultipleOf = 0)
    {
        long end = Math.Min(data.LongLength - keyLength, start + size - keyLength);
        if (end < start) return -1;
        long first = (start + align - 1) / align * align;
        const long Chunk = 1 << 20;
        long chunks = (end - first) / Chunk + 1;
        long found = long.MaxValue;
        ulong want0 = BinaryPrimitives.ReadUInt64LittleEndian(expectedSha1);
        Parallel.For(0L, chunks, new ParallelOptions { CancellationToken = ct }, (ci, state) =>
        {
            Span<byte> h = stackalloc byte[20];
            long a = first + ci * Chunk, b = Math.Min(end, a + Chunk - 1);
            for (long o = a; o <= b; o += align)
            {
                if (skipMultipleOf > 0 && o % skipMultipleOf == 0) continue; // already covered by the coarser pass
                if (Interlocked.Read(ref found) < o) return;
                SHA1.HashData(data.AsSpan((int)o, keyLength), h);
                if (BinaryPrimitives.ReadUInt64LittleEndian(h) == want0 && h.SequenceEqual(expectedSha1))
                {
                    long cur;
                    while ((cur = Interlocked.Read(ref found)) > o && Interlocked.CompareExchange(ref found, o, cur) != cur) { }
                    state.Stop();
                    return;
                }
            }
        });
        return found == long.MaxValue ? -1 : found;
    }

    public sealed record Section(string Name, long Offset, long Size);

    /// <summary>Initialised-data sections of a PE file (raw file ranges); empty for anything that is not a PE.</summary>
    public static List<Section> PeSections(byte[] d)
    {
        var list = new List<Section>();
        try
        {
            if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return list;
            int pe = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(0x3C));
            if (pe <= 0 || pe + 24 > d.Length || BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pe)) != 0x00004550) return list;
            int nsec = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 6));
            int optSize = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pe + 20));
            int sec = pe + 24 + optSize;
            for (int i = 0; i < nsec && sec + 40 * (i + 1) <= d.Length; i++)
            {
                int s = sec + 40 * i;
                string name = Encoding.ASCII.GetString(d, s, 8).TrimEnd('\0');
                uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(s + 16));
                uint rawPtr = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(s + 20));
                uint ch = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(s + 36));
                bool initData = (ch & 0x40) != 0, code = (ch & 0x20) != 0;
                if (rawSize == 0 || rawPtr >= d.Length || code || !initData) continue;
                list.Add(new Section(name, rawPtr, Math.Min(rawSize, d.Length - rawPtr)));
            }
        }
        catch (ArgumentOutOfRangeException) { }
        return list;
    }

    public static byte[]? LoadCache(string path, byte[] expectedSha1, Log log)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var key = Convert.FromBase64String((string)o["key"]!);
            if (key.Length == KeyHashes.AesKeyBytes && KeyHashes.Matches(key, expectedSha1)) return key;
            log.Warn($"[gta] cached AES key in {path} does not match the expected hash; searching again");
        }
        catch (Exception e) { log.Warn($"[gta] ignoring unreadable key cache {path}: {e.Message}"); }
        return null;
    }

    private static void SaveCache(string path, string exePath, long exeSize, Hit hit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fi = new FileInfo(exePath);
        var o = new JsonObject
        {
            ["note"] = "The player's own GTA V PC AES key, read from their exe. Local cache only; never shared or shipped.",
            ["exe"] = Path.GetFileName(exePath),
            ["exeSize"] = exeSize,
            ["exeMtimeMs"] = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            ["offset"] = hit.Offset,
            ["alignment"] = hit.Alignment,
            ["key"] = Convert.ToBase64String(hit.Key),
        };
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
