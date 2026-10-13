// Cache bookkeeping shared by the extractor executable and anything else that reads the cache: layout, manifest.json,
// progress.json, the single-extractor lock and game fingerprints. See docs/CONTRACTS.md ("Cache layout").
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CthExtract.Core.Generated;

namespace CthExtract.Core;

/// <summary>Where things live in the cache folder.</summary>
public sealed class CacheLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string ManifestPath => Path.Combine(Root, "manifest.json");
    public string ProgressPath => Path.Combine(Root, "progress.json");
    public string LogPath => Path.Combine(Root, "extract.log");
    public string PreviousLogPath => Path.Combine(Root, "extract.previous.log");
    public string CheckLogPath => Path.Combine(Root, "check.log");
    public string CheckPath => Path.Combine(Root, "check.json");
    public string LockPath => Path.Combine(Root, ".lock");
    public string LockInfoPath => Path.Combine(Root, ".lock.info");
    public string PacksDir => Path.Combine(Root, "packs");
    public string StatsDir => Path.Combine(Root, "stats");
    public string KeysDir => Path.Combine(Root, "keys");
    /// <summary>Per-run staging folders; everything below it is disposable once the lock is held.</summary>
    public string StagingRoot => Path.Combine(Root, ".staging");
    public string PackDir(string shortName) => Path.Combine(PacksDir, shortName);
    public string AssetsDir(string shortName) => Path.Combine(PackDir(shortName), "assets", ExtractContext.Namespace);
}

/// <summary>Small file-system helpers that retry around transient Windows sharing violations (antivirus, a reader
/// that has the file open) and never leave half-written files behind.</summary>
public static class SafeFs
{
    /// <summary>Write text to a unique temp file next to <paramref name="path"/> and move it over the target.
    /// Retries the move; as a last resort writes the target directly so the file always appears.</summary>
    public static void AtomicWriteText(string path, string text, int attempts = 40)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (i >= attempts)
                {
                    try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
                    finally { TryDeleteFile(tmp); }
                    return;
                }
                Thread.Sleep(Math.Min(250, 25 * (i + 1)));
            }
        }
    }

    public static bool TryDeleteFile(string path)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(50 * (i + 1)); }
        }
        return false;
    }

    /// <summary>Delete a folder tree, clearing read-only flags; retries; returns false instead of throwing.</summary>
    public static bool TryDeleteDirectory(string path)
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(100 * (i + 1)); }
        }
        return !Directory.Exists(path);
    }

    /// <summary>Rename a folder, retrying around transient sharing violations.</summary>
    public static void MoveDirectory(string from, string to, int attempts = 10)
    {
        for (int i = 0; ; i++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < attempts && Directory.Exists(from) && !Directory.Exists(to))
            {
                Thread.Sleep(100 * (i + 1));
            }
        }
    }

    /// <summary>Copy a file, creating its folder.</summary>
    public static void CopyFile(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to, overwrite: true);
    }

    /// <summary>Open a file for reading without ever blocking a writer, renamer or deleter (game launchers, updates).</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

/// <summary>size + mtime of one file the game module depends on (path relative to the game folder, '/' separators).
/// A file that does not exist is recorded with size -1 and mtimeMs 0, so it appearing later counts as a change.</summary>
public sealed record FingerprintEntry(string Path, long Size, long MtimeMs);

public static class Fingerprint
{
    /// <summary>Stat the module's fingerprint files. Never throws: problems come back in <paramref name="error"/>.</summary>
    public static List<FingerprintEntry> Compute(IGameModule module, string gameDir, out string? error)
    {
        error = null;
        var list = new List<FingerprintEntry>();
        List<string> rels;
        try { rels = module.FingerprintFiles(gameDir).ToList(); }
        catch (Exception e) { error = $"{module.Short} fingerprint files: {e.GetType().Name}: {e.Message}"; return list; }
        foreach (var rel in rels.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var full = Path.IsPathRooted(rel) ? rel : Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar));
                var fi = new FileInfo(full);
                list.Add(fi.Exists
                    ? new FingerprintEntry(rel, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds())
                    : new FingerprintEntry(rel, -1, 0));
            }
            catch (Exception e)
            {
                error ??= $"stat {rel}: {e.GetType().Name}: {e.Message}";
                list.Add(new FingerprintEntry(rel, -1, 0));
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return list;
    }

    public static bool Same(IReadOnlyList<FingerprintEntry>? a, IReadOnlyList<FingerprintEntry>? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Count != b.Count) return false;
        var sa = a.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        var sb = b.OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
        for (int i = 0; i < sa.Count; i++)
            if (!string.Equals(sa[i].Path, sb[i].Path, StringComparison.OrdinalIgnoreCase) || sa[i].Size != sb[i].Size || sa[i].MtimeMs != sb[i].MtimeMs)
                return false;
        return true;
    }

    /// <summary>Human-readable list of what differs, for logs.</summary>
    public static string Diff(IReadOnlyList<FingerprintEntry>? before, IReadOnlyList<FingerprintEntry>? now)
    {
        var b = (before ?? []).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var n = (now ?? []).ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (var (p, e) in n)
        {
            if (!b.TryGetValue(p, out var o)) parts.Add($"{p} new");
            else if (o.Size != e.Size || o.MtimeMs != e.MtimeMs)
                parts.Add(e.Size < 0 ? $"{p} gone" : o.Size < 0 ? $"{p} appeared" : $"{p} changed ({o.Size}->{e.Size} bytes)");
        }
        foreach (var p in b.Keys) if (!n.ContainsKey(p)) parts.Add($"{p} no longer listed");
        return parts.Count == 0 ? "same" : string.Join(", ", parts);
    }

    public static JsonArray ToJson(IEnumerable<FingerprintEntry> list)
    {
        var arr = new JsonArray();
        foreach (var e in list) arr.Add(new JsonObject { ["path"] = e.Path, ["size"] = e.Size, ["mtimeMs"] = e.MtimeMs });
        return arr;
    }

    public static List<FingerprintEntry>? FromJson(JsonNode? node)
    {
        if (node is not JsonArray arr) return null;
        var list = new List<FingerprintEntry>();
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var p = JsonRead.Str(o["path"]);
            if (p is null) continue;
            list.Add(new FingerprintEntry(p, JsonRead.Long(o["size"]) ?? -1, JsonRead.Long(o["mtimeMs"]) ?? 0));
        }
        return list;
    }
}

/// <summary>Tolerant JSON accessors (a manifest written by an older or newer extractor must never crash a reader).</summary>
public static class JsonRead
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)d;
        return null;
    }
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

public static class StatusNames
{
    public static string Of(AssetStatus s) => s switch
    {
        AssetStatus.Ok => "ok",
        AssetStatus.Missing => "missing",
        AssetStatus.Failed => "failed",
        _ => "unimplemented",
    };

    public static AssetStatus Parse(string? s) => s switch
    {
        "ok" => AssetStatus.Ok,
        "missing" => AssetStatus.Missing,
        "failed" => AssetStatus.Failed,
        _ => AssetStatus.Unimplemented,
    };
}

/// <summary>One sources row as recorded in manifest.json "assets".</summary>
public sealed record ManifestAsset(string Id, string Game, AssetStatus Status, string? Detail, string? Out, bool Kept = false)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["status"] = StatusNames.Of(Status), ["detail"] = Detail, ["out"] = Out, ["game"] = Game };
        if (Kept) o["kept"] = true;
        return o;
    }
}

/// <summary>One game as recorded in manifest.json "games".</summary>
public sealed class ManifestGame
{
    /// <summary>ok | partial | missing | failed (see docs/requests/extractor.md for the exact rules).</summary>
    public string Status = "missing";
    public string? Dir;
    public string? Error;
    public List<FingerprintEntry>? FingerprintFiles;
    public string? StartedUtc;
    public long ElapsedMs;
    /// <summary>"process" (isolated worker), "in-process", or "none" (not run this time).</summary>
    public string? Mode;
    /// <summary>The last attempt was cut short (crash, timeout, watchdog); --check advises a retry.</summary>
    public bool Interrupted;
    /// <summary>Consecutive interrupted attempts (retries stop being advised after 3).</summary>
    public int InterruptedRuns;
    /// <summary>The pack on disk is from an earlier run (this run did not replace it).</summary>
    public bool KeptPrevious;
    public Dictionary<string, int> Counts = new();

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["status"] = Status,
            ["dir"] = Dir,
            ["error"] = Error,
            ["fingerprint"] = FingerprintFiles is null ? null : Fingerprint.ToJson(FingerprintFiles),
            ["startedUtc"] = StartedUtc,
            ["elapsedMs"] = ElapsedMs,
            ["mode"] = Mode,
            ["interrupted"] = Interrupted,
            ["interruptedRuns"] = InterruptedRuns,
            ["keptPrevious"] = KeptPrevious,
        };
        var c = new JsonObject();
        foreach (var k in new[] { "ok", "missing", "failed", "unimplemented", "kept" })
            c[k] = Counts.TryGetValue(k, out var v) ? v : 0;
        o["counts"] = c;
        return o;
    }

    public static ManifestGame FromJson(JsonObject o)
    {
        var g = new ManifestGame
        {
            Status = JsonRead.Str(o["status"]) ?? "missing",
            Dir = JsonRead.Str(o["dir"]),
            Error = JsonRead.Str(o["error"]),
            FingerprintFiles = Fingerprint.FromJson(o["fingerprint"]),
            StartedUtc = JsonRead.Str(o["startedUtc"]),
            ElapsedMs = JsonRead.Long(o["elapsedMs"]) ?? 0,
            Mode = JsonRead.Str(o["mode"]),
            Interrupted = JsonRead.Bool(o["interrupted"]),
            InterruptedRuns = (int)(JsonRead.Long(o["interruptedRuns"]) ?? 0),
            KeptPrevious = JsonRead.Bool(o["keptPrevious"]),
        };
        if (o["counts"] is JsonObject c)
            foreach (var (k, v) in c) g.Counts[k] = (int)(JsonRead.Long(v) ?? 0);
        return g;
    }
}

/// <summary>
/// manifest.json: the extractor's "done file". Compact JSON that always starts with <c>{"cacheSchema":1,</c> (Melty's
/// setup step waits for that text), written last, atomically (temp file + move).
/// </summary>
public sealed class Manifest
{
    public const int CacheSchema = 1;

    public int Schema = CacheSchema;
    public string Extractor = "";
    /// <summary>ok (every game ok) | partial (something usable) | failed (nothing usable).</summary>
    public string Status = "failed";
    public string? FinishedUtc;
    /// <summary>Extra run facts (exit code, watchdog, args) kept at the end of the file.</summary>
    public JsonObject Run = new();
    public Dictionary<string, ManifestGame> Games = new();
    public Dictionary<string, ManifestAsset> Assets = new();

    public string ToJson()
    {
        var games = new JsonObject();
        foreach (var (k, g) in Games) games[k] = g.ToJson();
        var assets = new JsonObject();
        foreach (var (k, a) in Assets) assets[k] = a.ToJson();
        var doc = new JsonObject
        {
            ["cacheSchema"] = Schema,
            ["extractor"] = Extractor,
            ["status"] = Status,
            ["finishedUtc"] = FinishedUtc,
            ["games"] = games,
            ["assets"] = assets,
            ["run"] = Run.DeepClone(),
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public void Write(string cacheDir) => SafeFs.AtomicWriteText(new CacheLayout(cacheDir).ManifestPath, ToJson());

    /// <summary>Read manifest.json; null with a reason when it is absent, unreadable or of another cacheSchema.</summary>
    public static Manifest? TryRead(string cacheDir, out string? problem)
    {
        problem = null;
        var path = new CacheLayout(cacheDir).ManifestPath;
        if (!File.Exists(path)) { problem = "no manifest.json"; return null; }
        JsonObject? doc;
        try
        {
            using var s = SafeFs.OpenShared(path);
            doc = JsonNode.Parse(s) as JsonObject;
        }
        catch (Exception e) { problem = $"manifest.json unreadable: {e.GetType().Name}: {e.Message}"; return null; }
        if (doc is null) { problem = "manifest.json is not a JSON object"; return null; }
        var schema = JsonRead.Long(doc["cacheSchema"]);
        if (schema != CacheSchema) { problem = $"manifest.json has cacheSchema {schema?.ToString() ?? "none"}, this extractor writes {CacheSchema}"; return null; }
        var m = new Manifest
        {
            Schema = CacheSchema,
            Extractor = JsonRead.Str(doc["extractor"]) ?? "",
            Status = JsonRead.Str(doc["status"]) ?? "failed",
            FinishedUtc = JsonRead.Str(doc["finishedUtc"]),
            Run = doc["run"] is JsonObject r ? (JsonObject)r.DeepClone() : new JsonObject(),
        };
        if (doc["games"] is JsonObject games)
            foreach (var (k, v) in games)
                if (v is JsonObject go) m.Games[k] = ManifestGame.FromJson(go);
        if (doc["assets"] is JsonObject assets)
            foreach (var (k, v) in assets)
                if (v is JsonObject ao)
                {
                    var row = Sources.All.FirstOrDefault(x => x.Id == k);
                    m.Assets[k] = new ManifestAsset(k, JsonRead.Str(ao["game"]) ?? GameShort(row?.Game) ?? "", StatusNames.Parse(JsonRead.Str(ao["status"])),
                        JsonRead.Str(ao["detail"]), JsonRead.Str(ao["out"]), JsonRead.Bool(ao["kept"]));
                }
        return m;
    }

    /// <summary>"halo-mcc" → "halo", "gta-v" → "gta", "fortnite" → "fortnite".</summary>
    public static string? GameShort(string? slug) => slug switch
    {
        "halo-mcc" => "halo",
        "gta-v" => "gta",
        "fortnite" => "fortnite",
        _ => null,
    };

    public static string? GameSlug(string? shortName) => shortName switch
    {
        "halo" => "halo-mcc",
        "gta" => "gta-v",
        "fortnite" => "fortnite",
        _ => null,
    };
}

/// <summary>progress.json while the extractor runs: {"game":..,"step":..,"done":n,"total":n,...}.</summary>
public sealed class ProgressFile(string cacheDir)
{
    private readonly string _path = new CacheLayout(cacheDir).ProgressPath;
    private readonly object _lock = new();
    private long _lastTicks;
    private string? _lastGame;

    /// <summary>Write progress (throttled to ~5 per second unless the game changes or it is final). Never throws.</summary>
    public void Write(string? game, string step, int done, int total, bool finished = false, bool force = false)
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && !finished && game == _lastGame && Stopwatch.GetElapsedTime(_lastTicks, now) < TimeSpan.FromMilliseconds(200)) return;
            _lastTicks = now; _lastGame = game;
            var o = new JsonObject
            {
                ["game"] = game,
                ["step"] = step.Length > 300 ? step[..300] : step,
                ["done"] = Math.Min(done, total),
                ["total"] = total,
                ["finished"] = finished,
                ["pid"] = Environment.ProcessId,
                ["updatedUtc"] = DateTime.UtcNow.ToString("O"),
            };
            try { SafeFs.AtomicWriteText(_path, o.ToJsonString(), attempts: 3); } catch { }
        }
    }
}

/// <summary>
/// One extractor per cache: cache/.lock held open with FileShare.None (an OS lock, released when the process dies,
/// so a crashed extractor never leaves a stale lock). Holder details go to .lock.info for the next one's log.
/// </summary>
public sealed class CacheLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _infoPath;

    private CacheLock(FileStream s, string infoPath) { _stream = s; _infoPath = infoPath; }

    /// <summary>Try to take the lock, waiting up to <paramref name="wait"/>. Null when another process holds it;
    /// <paramref name="holder"/> then says who (from .lock.info) when known.</summary>
    public static CacheLock? TryAcquire(string cacheDir, TimeSpan wait, out string? holder)
    {
        var layout = new CacheLayout(cacheDir);
        Directory.CreateDirectory(layout.Root);
        holder = null;
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var s = new FileStream(layout.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var l = new CacheLock(s, layout.LockInfoPath);
                try
                {
                    File.WriteAllText(layout.LockInfoPath,
                        $"pid={Environment.ProcessId} started={DateTime.UtcNow:O} exe={Environment.ProcessPath}\n", new UTF8Encoding(false));
                }
                catch { }
                return l;
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    try { holder = File.ReadAllText(layout.LockInfoPath).Trim(); } catch { }
                    return null;
                }
                Thread.Sleep(250);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
