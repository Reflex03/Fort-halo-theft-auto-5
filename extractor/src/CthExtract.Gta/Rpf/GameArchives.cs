using CthExtract.Core;
using CthExtract.Gta.Crypto;

namespace CthExtract.Gta.Rpf;

/// <summary>
/// The archives of one GTA V install that this run opened, plus an index of the files found in them.
/// Opening is targeted: callers name the top-level archives and a predicate for which nested archives to descend into.
/// Every failure is remembered per archive (for the Missing/Failed reasons) instead of thrown.
/// </summary>
public sealed class GameArchives : IDisposable
{
    private readonly Dictionary<string, RpfFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RpfArchive?> _archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _walked = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RpfEntry> _index = new();
    private readonly Dictionary<string, List<RpfEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string GameDir { get; }
    public GtaKeys Keys { get; }
    public Log Log { get; }

    public GameArchives(string gameDir, GtaKeys keys, Log log) { GameDir = gameDir; Keys = keys; Log = log; }

    public IReadOnlyList<RpfEntry> Index => _index;

    /// <summary>Relative path ('/'-separated) of every *.rpf on disk under the game folder (OpenIV's mods/ folder excluded).</summary>
    public List<string> DiskArchives()
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(GameDir, "*.rpf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }))
            {
                var rel = Path.GetRelativePath(GameDir, f).Replace('\\', '/');
                if (rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(rel);
            }
        }
        catch (Exception e) { Log.Warn($"[gta] listing archives failed: {e.Message}"); }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Log the plain header (entries, encryption) of every archive on disk. Needs no keys.</summary>
    public void Census(IEnumerable<string> rels)
    {
        var counts = new Dictionary<string, int>();
        foreach (var rel in rels)
        {
            try
            {
                var f = FileFor(rel);
                if (f is null) continue;
                var h = RpfArchive.PeekHeader(f);
                string enc = h is null ? "not RPF7" : h.Value.encryption switch
                {
                    0 or RpfArchive.EncOpen => "OPEN", RpfArchive.EncAes => "AES", RpfArchive.EncNg => "NG", var v => $"0x{v:X8}",
                };
                counts[enc] = counts.GetValueOrDefault(enc) + 1;
                Log.Info($"[gta] census {rel}: {enc}{(h is null ? "" : $", {h.Value.entries} entries")}, {f.Length / 1048576.0:F1} MiB");
            }
            catch (Exception e) { Log.Warn($"[gta] census {rel}: {e.Message}"); }
        }
        Log.Info($"[gta] census summary: {string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}"))}");
    }

    private RpfFile? FileFor(string rel)
    {
        if (_files.TryGetValue(rel, out var f)) return f;
        var full = ResolveCaseInsensitive(rel);
        if (full is null) return null;
        f = new RpfFile(full);
        _files[rel] = f;
        return f;
    }

    /// <summary>Resolve a '/'-separated relative path case-insensitively (Windows installs are case-insensitive;
    /// this keeps synthetic tests on Linux honest too).</summary>
    public string? ResolveCaseInsensitive(string rel)
    {
        var cur = GameDir;
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var direct = Path.Combine(cur, part);
            if (File.Exists(direct) || Directory.Exists(direct)) { cur = direct; continue; }
            if (!Directory.Exists(cur)) return null;
            var match = Directory.EnumerateFileSystemEntries(cur).FirstOrDefault(p => string.Equals(Path.GetFileName(p), part, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            cur = match;
        }
        return File.Exists(cur) ? cur : null;
    }

    /// <summary>Open a top-level archive (relative path such as "x64e.rpf" or "update/update.rpf"). Null if absent or unreadable
    /// (the reason is in <see cref="Errors"/>).</summary>
    public RpfArchive? OpenTop(string rel)
    {
        if (_archives.TryGetValue(rel, out var cached)) return cached;
        RpfArchive? a = null;
        try
        {
            var f = FileFor(rel);
            if (f is null) Errors[rel] = "not found";
            else
            {
                a = RpfArchive.Open(f, rel, Keys);
                Log.Info($"[gta] opened {rel} ({a.EncryptionName}, {a.Entries.Count} entries)");
            }
        }
        catch (Exception e) when (e is KeyUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Errors[rel] = e.Message;
            Log.Warn($"[gta] cannot open {rel}: {e.Message}");
        }
        _archives[rel] = a;
        return a;
    }

    /// <summary>Add an archive's files to the index (once) and descend into nested archives the predicate accepts
    /// (it gets the nested archive's full virtual path, e.g. "x64e.rpf/levels/gta5/vehicles.rpf"). Walking the same
    /// archive again with another predicate opens the nested archives the earlier walks skipped.</summary>
    public void Walk(RpfArchive a, Func<string, bool> descend, int maxDepth = 4)
    {
        if (_walked.Add(a.VirtualPath))
            foreach (var e in a.Files)
            {
                _index.Add(e);
                if (!_byName.TryGetValue(e.Name, out var list)) _byName[e.Name] = list = new List<RpfEntry>();
                list.Add(e);
            }
        if (maxDepth <= 0) return;
        foreach (var e in a.Files)
        {
            if (!e.IsRpf) continue;
            var vp = a.VirtualPath + "/" + e.Path;
            if (!descend(vp) || Errors.ContainsKey(vp)) continue;
            if (!_archives.TryGetValue(vp, out var n))
            {
                try
                {
                    n = a.OpenNested(e);
                    Log.Info($"[gta] opened {vp} ({n.EncryptionName}, {n.Entries.Count} entries)");
                }
                catch (Exception ex) when (ex is KeyUnavailableException or InvalidDataException or IOException or EndOfStreamException)
                {
                    Errors[vp] = ex.Message;
                    Log.Warn($"[gta] cannot open {vp}: {ex.Message}");
                    n = null;
                }
                _archives[vp] = n;
            }
            if (n is not null) Walk(n, descend, maxDepth - 1);
        }
    }

    /// <summary>Files with this name (case-insensitive) anywhere in what was walked, in walk order.</summary>
    public List<RpfEntry> Find(string fileName) =>
        _byName.TryGetValue(fileName, out var list) ? new List<RpfEntry>(list) : new List<RpfEntry>();

    public byte[] ReadBinary(RpfEntry e) => e.Archive.ReadBinary(e);
    public byte[] ReadResourceRaw(RpfEntry e) => e.Archive.ReadResourceRaw(e);

    public void Dispose()
    {
        foreach (var f in _files.Values) f.Dispose();
        _files.Clear();
    }
}
