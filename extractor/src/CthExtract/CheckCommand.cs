using System.Text;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract;

/// <summary>
/// --check: is the cache still right for the installs it came from? Never extracts and never takes the lock; only
/// stats each game's fingerprint files. Prints one JSON line (also written to check.json) and exits with:
/// 0 fresh, 10 a full extraction is needed (no usable manifest), 11 some games should be re-read ("rerun" lists
/// them: never extracted, folder moved, extractor updated, a failed/partial game whose inputs changed, or an
/// interrupted attempt), 12 only games that were ok have been updated ("updated" lists them; refreshing is optional).
/// </summary>
internal static class CheckCommand
{
    public const int MaxInterruptedRetries = 3;

    public static int Run(CliOptions o, string cacheDir, IReadOnlyList<GameSpec> specs, TextWriter stdout)
    {
        var layout = new CacheLayout(cacheDir);
        var log = new StringBuilder();
        void Log(string m) => log.Append($"{DateTime.UtcNow:O} {m}\n");
        Log($"{AppInfo.Banner} --check, cache {layout.Root}");
        foreach (var n in o.Notes) Log("note: " + n);

        var result = new JsonObject
        {
            ["cacheSchema"] = Manifest.CacheSchema,
            ["exe"] = AppInfo.Version,
        };
        int exit;
        var games = new JsonObject();
        var rerun = new JsonArray();
        var updated = new JsonArray();
        string? problem = null;
        var manifest = Directory.Exists(layout.Root) ? Manifest.TryRead(layout.Root, out problem) : null;
        if (!Directory.Exists(layout.Root)) problem = "no cache folder";
        result["running"] = IsRunning(layout);

        if (manifest is null)
        {
            Log($"manifest: {problem}");
            result["manifest"] = new JsonObject { ["state"] = "missing", ["problem"] = problem };
            foreach (var g in o.SelectedGames()) rerun.Add(g);
            exit = ExitCodes.CheckFullRunNeeded;
        }
        else
        {
            bool extractorChanged = manifest.Extractor != AppInfo.Version;
            result["manifest"] = new JsonObject
            {
                ["state"] = "ok",
                ["status"] = manifest.Status,
                ["extractor"] = manifest.Extractor,
                ["finishedUtc"] = manifest.FinishedUtc,
            };
            foreach (var g in o.SelectedGames())
            {
                var (state, detail) = CheckGame(g, o, manifest, specs, extractorChanged);
                Log($"{g}: {state} - {detail}");
                games[g] = new JsonObject { ["state"] = state, ["detail"] = detail, ["status"] = manifest.Games.GetValueOrDefault(g)?.Status };
                if (state is "never" or "moved" or "extractor-updated" or "changed" or "interrupted") rerun.Add(g);
                else if (state == "updated") updated.Add(g);
            }
            exit = rerun.Count > 0 ? ExitCodes.CheckRerunGames : updated.Count > 0 ? ExitCodes.CheckGamesUpdated : ExitCodes.CheckFresh;
        }
        result["games"] = games;
        result["rerun"] = rerun;
        result["updated"] = updated;
        result["advice"] = exit switch
        {
            ExitCodes.CheckFullRunNeeded => "full",
            ExitCodes.CheckRerunGames => "rerun",
            ExitCodes.CheckGamesUpdated => "updated",
            _ => "fresh",
        };
        result["exitCode"] = exit;
        var json = result.ToJsonString();
        Log($"result: {json}");
        try
        {
            Directory.CreateDirectory(layout.Root);
            SafeFs.AtomicWriteText(layout.CheckPath, json, attempts: 5);
            SafeFs.AtomicWriteText(layout.CheckLogPath, log.ToString(), attempts: 5);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* stdout still has it */ }
        stdout.WriteLine(json);
        return exit;
    }

    private static (string State, string Detail) CheckGame(string g, CliOptions o, Manifest m, IReadOnlyList<GameSpec> specs, bool extractorChanged)
    {
        var e = m.Games.GetValueOrDefault(g);
        if (e is null || e.StartedUtc is null) return ("never", "not extracted yet");
        if (extractorChanged) return ("extractor-updated", $"cache written by extractor {m.Extractor}, this is {AppInfo.Version}");

        string? given = o.Dirs.GetValueOrDefault(g);
        string? dir = e.Dir;
        if (given is not null)
        {
            string full;
            try { full = Path.GetFullPath(given).Replace('\\', '/').TrimEnd('/'); }
            catch (Exception ex) { return ("moved", $"folder '{given}' is not a valid path: {ex.Message}"); }
            if (!string.Equals(full, e.Dir?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                return ("moved", $"folder is now {full}, cache came from {e.Dir ?? "(none)"}");
            dir = full;
        }
        if (dir is null) return ("no-folder", e.Error ?? "no folder known for this game");

        if (e.Interrupted && e.InterruptedRuns < MaxInterruptedRetries)
            return ("interrupted", $"last attempt was cut short ({e.Error}); retry {e.InterruptedRuns + 1} of {MaxInterruptedRetries}");

        List<FingerprintEntry> now;
        try
        {
            var spec = specs.First(s => s.Short == g);
            now = Fingerprint.Compute(spec.Create(), dir, out var err);
            if (err is not null) return (e.Status == "ok" ? "updated" : "changed", "fingerprint error: " + err);
        }
        catch (Exception ex) { return (e.Status == "ok" ? "updated" : "changed", $"fingerprint error: {ex.GetType().Name}: {ex.Message}"); }

        bool same = Fingerprint.Same(e.FingerprintFiles, now);
        if (e.Status == "ok") return same ? ("fresh", "fingerprint matches") : ("updated", Fingerprint.Diff(e.FingerprintFiles, now));
        return same
            ? ("unchanged", $"{e.Status} last time and nothing changed since ({e.Error})")
            : ("changed", $"{e.Status} last time; inputs changed: {Fingerprint.Diff(e.FingerprintFiles, now)}");
    }

    /// <summary>Whether an extractor holds the cache lock right now (taken and released immediately when free).</summary>
    private static bool IsRunning(CacheLayout layout)
    {
        if (!File.Exists(layout.LockPath)) return false;
        try
        {
            using var l = CacheLock.TryAcquire(layout.Root, TimeSpan.Zero, out _);
            return l is null;
        }
        catch { return false; }
    }
}
