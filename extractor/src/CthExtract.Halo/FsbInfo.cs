using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace CthExtract.Halo;

/// <summary>
/// One record of an MCC "&lt;bank&gt;.fsb.info" file: a flat array of 0x118-byte records, one per FSB subsound in the
/// same order (layout from TagTool Audio/FMOD/FMODSoundIndex.cs FMODSoundInfo):
/// u32 hash, u32 sampleCount, i32 channelCount, u32 sampleSize, u32 loopStart, u32 loopEnd, char filename[256].
/// </summary>
public sealed record FsbInfoRecord(int Index, uint Hash, uint SampleCount, int ChannelCount, uint SampleSize,
                                   uint LoopStart, uint LoopEnd, string FileName);

public sealed class FsbInfo
{
    public const int RecordSize = 0x118;
    public const int FileNameLength = 256;

    public string Path { get; }
    public IReadOnlyList<FsbInfoRecord> Records { get; }
    /// <summary>Number of trailing bytes that did not form a whole record (should be 0).</summary>
    public int TrailingBytes { get; }
    private readonly Dictionary<uint, int> byHash = new();
    public int DuplicateHashes { get; }

    public FsbInfo(string path, byte[] data)
    {
        Path = path;
        var list = new List<FsbInfoRecord>(data.Length / RecordSize);
        int dup = 0;
        for (int i = 0; (i + 1) * RecordSize <= data.Length; i++)
        {
            var r = data.AsSpan(i * RecordSize, RecordSize);
            var nameBytes = r.Slice(0x18, FileNameLength);
            int end = nameBytes.IndexOf((byte)0);
            if (end < 0) end = nameBytes.Length;
            var rec = new FsbInfoRecord(i,
                BinaryPrimitives.ReadUInt32LittleEndian(r),
                BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[20..]),
                Encoding.Latin1.GetString(nameBytes[..end]));
            list.Add(rec);
            // TagTool keeps the first index per hash
            if (!byHash.TryAdd(rec.Hash, i)) dup++;
        }
        Records = list;
        TrailingBytes = data.Length % RecordSize;
        DuplicateHashes = dup;
    }

    public static FsbInfo Load(string path) => new(path, GameFile.ReadAllBytes(path));

    /// <summary>Record index (= FSB subsound index) of the first record with this hash, or -1.</summary>
    public int IndexOfHash(uint hash) => byHash.TryGetValue(hash, out var i) ? i : -1;

    /// <summary>
    /// Records whose filename matches a sheet pattern such as <c>data\sound\weapons\assault_rifle\*fire*</c> or
    /// <c>*shield*(deplet|down|empty)*</c>: '*' is any run of characters, "(a|b)" alternation, case-insensitive.
    /// When the anchored pattern finds nothing, the leading "data\" is dropped and the rest may start at any folder.
    /// </summary>
    public IReadOnlyList<FsbInfoRecord> Match(string pattern)
    {
        var rx = PatternRegex(pattern, anchored: true);
        var hits = Records.Where(r => rx.IsMatch(r.FileName)).ToList();
        if (hits.Count > 0) return hits;
        var loose = PatternRegex(pattern, anchored: false);
        return Records.Where(r => loose.IsMatch(r.FileName)).ToList();
    }

    public static Regex PatternRegex(string pattern, bool anchored)
    {
        var p = pattern.Trim().Replace('/', '\\');
        if (!anchored && p.StartsWith("data\\", StringComparison.OrdinalIgnoreCase)) p = p.Substring(5);
        var sb = new StringBuilder();
        foreach (var ch in p)
        {
            switch (ch)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                case '(': case ')': case '|': sb.Append(ch); break;
                default: sb.Append(Regex.Escape(ch.ToString())); break;
            }
        }
        var body = sb.ToString();
        var full = anchored ? "^" + body + "$" : @"(^|\\)" + body + "$";
        return new Regex(full, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Write records in the .info layout (used by tests and diagnostics; never writes into a game folder).</summary>
    public static byte[] Write(IEnumerable<(uint Hash, uint SampleCount, int Channels, uint SampleSize, string FileName)> records)
    {
        using var ms = new MemoryStream();
        foreach (var (hash, count, ch, size, name) in records)
        {
            var rec = new byte[RecordSize];
            BinaryPrimitives.WriteUInt32LittleEndian(rec, hash);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), count);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(8), ch);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), size);
            var nb = Encoding.Latin1.GetBytes(name);
            nb.AsSpan(0, Math.Min(nb.Length, FileNameLength - 1)).CopyTo(rec.AsSpan(0x18));
            ms.Write(rec);
        }
        return ms.ToArray();
    }
}

/// <summary>Read-only access to files inside a game install: never take a write lock, never block the game.</summary>
public static class GameFile
{
    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    public static byte[] ReadAllBytes(string path)
    {
        using var fs = OpenRead(path);
        var data = new byte[fs.Length];
        fs.ReadExactly(data);
        return data;
    }
}
