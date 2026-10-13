using System.Text;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthSynth;

/// <summary>
/// Synthetic cache writer and stand-in extractor for the mod's tests (never shipped, never reads a game).
/// <code>
///   CthSynth --out &lt;cache dir&gt; [--only halo|gta|fortnite]...
///   CthSynth --config &lt;crafttheft.properties&gt; [--only ...]     (reads cache_dir like cthextract.exe)
/// env CTH_SYNTH_FAIL=gta        a full run (no --only) reports that game failed (no pack swapped in)
/// env CTH_SYNTH_SLOW_MS=200     sleep per row so progress.json can be watched
/// env CTH_SYNTH_REAL_CACHE=dir  take every row the REAL extractor produced in that cache (extractor/tests/e2e.sh)
///                               instead of the synthetic one (RealCache.cs)
/// </code>
/// Exit codes follow the extractor: 0 all ok, 1 partial, 3 bad arguments.
/// </summary>
public static class Program
{
    static readonly string[] Games = { "halo", "gta", "fortnite" };

    public static int Main(string[] args)
    {
        string? outDir = null, config = null;
        var only = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--only" when i + 1 < args.Length: only.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                default: Console.Error.WriteLine($"CthSynth: unknown argument {args[i]}"); return 3;
            }
        }
        if (outDir is null && config is not null) outDir = ReadProperty(config, "cache_dir");
        if (string.IsNullOrWhiteSpace(outDir)) { Console.Error.WriteLine("CthSynth: need --out or --config with cache_dir"); return 3; }
        foreach (var g in only) if (!Games.Contains(g)) { Console.Error.WriteLine($"CthSynth: unknown game {g}"); return 3; }
        var selected = only.Count > 0 ? only.Distinct().ToList() : Games.ToList();
        string failGame = only.Count == 0 ? Environment.GetEnvironmentVariable("CTH_SYNTH_FAIL") ?? "" : "";
        int slow = int.TryParse(Environment.GetEnvironmentVariable("CTH_SYNTH_SLOW_MS"), out var ms) ? ms : 0;

        string cache = Path.GetFullPath(outDir);
        Directory.CreateDirectory(cache);
        var layout = new CacheLayout(cache);
        using var log = new Log(layout.LogPath);
        log.Info($"CthSynth (synthetic test data, no game files read) -> {cache}; games {string.Join(",", selected)}; fail '{failGame}'; slow {slow} ms");
        var prev = Manifest.TryRead(cache, out _);
        var manifest = new Manifest { Extractor = "0.1.0-synthetic", Status = "failed" };
        if (prev is not null)
        {
            foreach (var (k, v) in prev.Games) manifest.Games[k] = v;
            foreach (var (k, v) in prev.Assets) manifest.Assets[k] = v;
        }
        var real = RealCache.Load(Environment.GetEnvironmentVariable("CTH_SYNTH_REAL_CACHE"), log);
        var progress = new ProgressFile(cache);
        int total = selected.Sum(g => Sources.ForGame(Manifest.GameSlug(g)!).Count()), done = 0;
        foreach (var game in selected)
        {
            string slug = Manifest.GameSlug(game)!;
            var rows = Sources.ForGame(slug).ToList();
            string stage = Path.Combine(layout.StagingRoot, $"{game}-{Environment.ProcessId}");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            var ctx = new ExtractContext(slug, game, Path.Combine(stage, "pack"), Path.Combine(stage, "stats"), Path.Combine(stage, "work"), log) { CacheDir = cache };
            var started = DateTime.UtcNow;
            bool fail = game == failGame;
            var extras = new Dictionary<string, AssetResult>();
            foreach (var r in rows)
            {
                progress.Write(game, $"{r.Id}", done, total, force: true);
                if (slow > 0) Thread.Sleep(slow);
                if (fail) ctx.Failed(r.Id, "synthetic failure (CTH_SYNTH_FAIL)");
                else if (real is not null && real.TryWrite(ctx, r, out var extra)) { if (extra is not null) extras[r.Id] = extra; }
                else
                {
                    try { Content.Write(ctx, r.Id); }
                    catch (Exception e) { ctx.Failed(r.Id, $"synthetic writer error: {e.Message}"); }
                }
                done++;
            }
            ctx.Finish();
            var results = ctx.Results.ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (k, v) in extras) results[k] = v;
            int ok = results.Values.Count(x => x.Status == AssetStatus.Ok);
            var mg = new ManifestGame
            {
                Status = ok == rows.Count ? "ok" : ok > 0 ? "partial" : "failed",
                Dir = "(synthetic)",
                Error = ok == rows.Count ? null : fail ? "synthetic failure (CTH_SYNTH_FAIL)" : $"{rows.Count - ok} rows not ok",
                StartedUtc = started.ToString("O"),
                ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
                Mode = "in-process",
            };
            foreach (var st in new[] { AssetStatus.Ok, AssetStatus.Missing, AssetStatus.Failed, AssetStatus.Unimplemented })
                mg.Counts[StatusNames.Of(st)] = results.Values.Count(x => x.Status == st);
            manifest.Games[game] = mg;
            foreach (var res in results.Values) manifest.Assets[res.Id] = new ManifestAsset(res.Id, game, res.Status, res.Detail, res.Status == AssetStatus.Ok ? res.Out : null);
            if (ok > 0)
            {
                // swap the pack in only when the game produced something (like the real extractor)
                string dest = layout.PackDir(game);
                Directory.CreateDirectory(layout.PacksDir);
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(Path.Combine(stage, "pack"), dest);
                string stats = Path.Combine(stage, "stats");
                if (Directory.Exists(stats))
                    foreach (var f in Directory.GetFiles(stats))
                    {
                        Directory.CreateDirectory(layout.StatsDir);
                        File.Copy(f, Path.Combine(layout.StatsDir, Path.GetFileName(f)), true);
                    }
            }
            Directory.Delete(stage, true);
            log.Info($"[{game}] {mg.Status}: {ok}/{rows.Count} rows ok");
        }
        if (Directory.Exists(layout.StagingRoot) && !Directory.EnumerateFileSystemEntries(layout.StagingRoot).Any()) Directory.Delete(layout.StagingRoot);
        int okGames = Games.Count(g => manifest.Games.TryGetValue(g, out var mg) && mg.Status == "ok");
        bool anyOk = manifest.Assets.Values.Any(a => a.Status == AssetStatus.Ok);
        manifest.Status = okGames == Games.Length ? "ok" : anyOk ? "partial" : "failed";
        manifest.FinishedUtc = DateTime.UtcNow.ToString("O");
        int exit = selected.All(g => manifest.Games[g].Status == "ok") ? 0 : 1;
        manifest.Run = new System.Text.Json.Nodes.JsonObject { ["exitCode"] = exit, ["games"] = string.Join(",", selected), ["synthetic"] = true };
        progress.Write(null, "finished", total, total, finished: true, force: true);
        manifest.Write(cache);
        log.Info($"manifest {manifest.Status}, exit {exit}");
        Console.WriteLine($"CthSynth: wrote {cache} ({manifest.Status}), exit {exit}");
        return exit;
    }

    /// <summary>Enough of java.util.Properties for crafttheft.properties (key=value / key:value, # comments, \ escapes).</summary>
    static string? ReadProperty(string file, string key)
    {
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] is '#' or '!') continue;
            int sep = line.IndexOfAny(new[] { '=', ':' });
            if (sep < 0) continue;
            if (line[..sep].Trim() != key) continue;
            var v = line[(sep + 1)..].Trim();
            var sb = new StringBuilder();
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] == '\\' && i + 1 < v.Length) { i++; sb.Append(v[i] switch { 't' => '\t', 'n' => '\n', _ => v[i] }); }
                else sb.Append(v[i]);
            }
            return sb.ToString();
        }
        return null;
    }
}
