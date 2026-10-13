using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract;

/// <summary>Everything a run needs, resolved from the command line (or built directly by tests).</summary>
internal sealed class RunSettings
{
    public required string CacheDir;
    /// <summary>Selected games in canonical order.</summary>
    public required string[] Games;
    public Dictionary<string, string?> Dirs = new();
    public Dictionary<string, DirSource> DirSources = new();
    public TimeSpan Timeout = TimeSpan.FromSeconds(CliOptions.DefaultTimeoutSec);
    public TimeSpan GameTimeout = TimeSpan.FromSeconds(CliOptions.DefaultGameTimeoutSec);
    public TimeSpan Grace = TimeSpan.FromSeconds(CliOptions.DefaultGraceSec);
    public TimeSpan LockWait = TimeSpan.Zero;
    /// <summary>Run each game in a worker process (default) instead of a thread of this process.</summary>
    public bool Isolated = true;
    /// <summary>Keep the previous run's good file for rows that fail this time.</summary>
    public bool Keep = true;
    public bool Offline;
    public List<string> Notes = new();
    public string CommandLine = "";
    public WorkerCommand? Worker;
    /// <summary>What the last-resort watchdog does after writing the manifest (tests replace it).</summary>
    public Action<int> HardExit = Environment.Exit;
    /// <summary>How long after the global deadline (plus grace) the last-resort watchdog fires.</summary>
    public TimeSpan HardWatchdogSlack = TimeSpan.FromSeconds(90);
    public TextWriter Stderr = Console.Error;

    public static RunSettings From(CliOptions o, string cacheDir, IEnumerable<string> argv) => new()
    {
        CacheDir = cacheDir,
        Games = o.SelectedGames(),
        Dirs = new Dictionary<string, string?>(o.Dirs),
        DirSources = new Dictionary<string, DirSource>(o.DirSources),
        Timeout = TimeSpan.FromSeconds(o.TimeoutSec),
        GameTimeout = TimeSpan.FromSeconds(o.GameTimeoutSec),
        Grace = TimeSpan.FromSeconds(o.GraceSec),
        LockWait = TimeSpan.FromSeconds(o.LockWaitSec),
        Isolated = !o.InProcess,
        Keep = !o.NoKeep,
        Offline = o.Offline,
        Notes = new List<string>(o.Notes),
        CommandLine = string.Join(' ', argv.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
    };
}

/// <summary>
/// One extraction run: lock, staging, one game at a time (each isolated), atomic swaps, progress.json, extract.log,
/// and manifest.json written last. See docs/CONTRACTS.md and docs/requests/extractor.md.
/// </summary>
internal sealed class Runner
{
    private readonly RunSettings _s;
    private readonly IReadOnlyList<GameSpec> _specs;
    private readonly CacheLayout _layout;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private Log _log = null!;
    private Manifest? _previous;
    private readonly Dictionary<string, ManifestGame> _games = new();
    private readonly Dictionary<string, ManifestAsset> _assets = new();
    private readonly HashSet<string> _done = new();
    private int _newlyOk;
    private bool _watchdogFired;
    private volatile string? _emergencyJson;
    private readonly object _manifestGate = new();
    private bool _manifestWritten;

    private Runner(RunSettings s, IReadOnlyList<GameSpec> specs)
    {
        _s = s; _specs = specs; _layout = new CacheLayout(s.CacheDir);
    }

    public static int Run(RunSettings s, IReadOnlyList<GameSpec> specs) => new Runner(s, specs).Run();

    private int Run()
    {
        // never write inside a game folder (Program.Cli checks too; this covers callers that build RunSettings directly)
        if (GameDirGuard.Problem(_layout.Root, _s.Dirs) is { } inGame)
        {
            _s.Stderr.WriteLine($"cthextract: {inGame}");
            return ExitCodes.BadArguments;
        }
        try { Directory.CreateDirectory(_layout.Root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _s.Stderr.WriteLine($"cthextract: cannot create the cache folder {_layout.Root}: {e.Message}");
            return ExitCodes.BadArguments;
        }

        CacheLock? cacheLock;
        try { cacheLock = CacheLock.TryAcquire(_layout.Root, _s.LockWait, out var holder); if (cacheLock is null) { _s.Stderr.WriteLine($"cthextract: another extractor is using {_layout.Root} ({holder ?? "holder unknown"}); exiting {ExitCodes.Locked}"); return ExitCodes.Locked; } }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            _s.Stderr.WriteLine($"cthextract: cannot lock the cache folder {_layout.Root}: {e.Message}");
            return ExitCodes.BadArguments;
        }

        using (cacheLock)
        {
            RotateLog();
            using (_log = new Log(_layout.LogPath))
            {
                Timer? hard = null;
                int exit;
                try
                {
                    LogHeader();
                    var deadline = _startedUtc + _s.Timeout;
                    var hardIn = deadline + _s.Grace + _s.HardWatchdogSlack - DateTime.UtcNow;
                    hard = new Timer(_ => HardWatchdog(), null, hardIn > TimeSpan.Zero ? hardIn : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                    exit = Body(deadline);
                }
                catch (Exception e)
                {
                    // a bug in the supervisor itself: still write a manifest with what is known
                    _log.Error($"extractor failed: {e}");
                    exit = _newlyOk > 0 ? ExitCodes.Partial : ExitCodes.NothingExtracted;
                    try
                    {
                        if (_games.Count == 0) InitState();
                        foreach (var g in _s.Games.Where(g => !_done.Contains(g)))
                            Commit(g, NotRun(g, $"extractor error before this game finished: {e.GetType().Name}: {e.Message}", interrupted: true, dir: null, fingerprint: null));
                        WriteManifest(exit);
                    }
                    catch (Exception e2)
                    {
                        _log.Error($"writing the manifest after the error failed too: {e2}");
                        if (_emergencyJson is { } json) try { SafeFs.AtomicWriteText(_layout.ManifestPath, json); } catch { }
                    }
                }
                finally { hard?.Dispose(); }
                _log.Info($"exit {exit} ({ExitCodes.Describe(exit)}) after {_clock.Elapsed.TotalSeconds:F1} s");
                return exit;
            }
        }
    }

    private int Body(DateTime deadline)
    {
        Cleanup();
        _previous = Manifest.TryRead(_layout.Root, out var problem);
        if (_previous is null) _log.Info($"previous manifest: {problem}");
        else _log.Info($"previous manifest: extractor {_previous.Extractor}, status {_previous.Status}, finished {_previous.FinishedUtc}");
        InitState();
        _emergencyJson = Emergency();

        var progress = new ProgressFile(_layout.Root);
        int total = _s.Games.Sum(g => Rows(g).Count), doneBefore = 0;
        bool isolated = _s.Isolated;
        IGameExecutor inProc = new InProcessExecutor();
        IGameExecutor? proc = isolated ? new ProcessExecutor(_s.Worker ?? WorkerCommand.Self()) : null;

        foreach (var g in _s.Games)
        {
            var spec = _specs.First(x => x.Short == g);
            int rowsOf = Rows(g).Count;
            progress.Write(g, $"{g}: starting", doneBefore, total, force: true);
            if (DateTime.UtcNow >= deadline)
            {
                _watchdogFired = true;
                _log.Error($"[{g}] not started: the extractor's time limit ({_s.Timeout.TotalSeconds:F0} s) ran out");
                Commit(g, NotRun(g, $"not started: the extractor's time limit ({_s.Timeout.TotalSeconds:F0} s) ran out", interrupted: true, dir: null, fingerprint: null));
            }
            else
            {
                try { RunGame(spec, deadline, ref isolated, ref proc, inProc, progress, doneBefore, total); }
                catch (Exception e)
                {
                    _log.Error($"[{g}] {e}");
                    Commit(g, NotRun(g, $"extractor error: {e.GetType().Name}: {e.Message}", interrupted: true, dir: null, fingerprint: null));
                }
            }
            _done.Add(g);
            doneBefore += rowsOf;
            _emergencyJson = Emergency();
        }

        try { if (Directory.Exists(_layout.StagingRoot) && !Directory.EnumerateFileSystemEntries(_layout.StagingRoot).Any()) Directory.Delete(_layout.StagingRoot); }
        catch (IOException) { }
        progress.Write(null, "finished", total, total, finished: true);
        int exit = ExitCode();
        WriteManifest(exit);
        LogSummary();
        return exit;
    }

    private void RunGame(GameSpec spec, DateTime deadline, ref bool isolated, ref IGameExecutor? proc, IGameExecutor inProc,
                         ProgressFile progress, int doneBefore, int total)
    {
        var g = spec.Short;
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var dir = _s.Dirs.GetValueOrDefault(g);
        if (dir is null)
        {
            var reason = $"no {g} folder given (--{g} or {g}_dir in --config)";
            _log.Info($"[{g}] {reason}");
            Commit(g, NotRun(g, reason, interrupted: false, dir: null, fingerprint: null));
            return;
        }
        string full;
        try { full = Path.GetFullPath(dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Commit(g, NotRun(g, $"{g} folder '{dir}' is not a valid path: {e.Message}", interrupted: false, dir: dir, fingerprint: null));
            return;
        }
        if (!Directory.Exists(full) && !File.Exists(full))
        {
            var reason = $"{g} folder not found: {full}";
            _log.Warn($"[{g}] {reason}");
            Commit(g, NotRun(g, reason, interrupted: false, dir: full, fingerprint: null));
            return;
        }

        List<FingerprintEntry>? fp = null;
        try
        {
            fp = Fingerprint.Compute(spec.Create(), full, out var fpError);
            if (fpError is not null) _log.Warn($"[{g}] fingerprint: {fpError}");
            _log.Info($"[{g}] fingerprint: {(fp.Count == 0 ? "(no files)" : string.Join(", ", fp.Select(f => f.Size < 0 ? $"{f.Path} absent" : $"{f.Path} {f.Size} B")))}");
        }
        catch (Exception e) { _log.Warn($"[{g}] fingerprint failed: {e.GetType().Name}: {e.Message}"); }

        var stage = Path.Combine(_layout.StagingRoot, $"{g}-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}");
        Directory.CreateDirectory(stage);
        var gameDeadline = started + _s.GameTimeout;
        bool globalBinds = deadline <= gameDeadline;
        var job = new GameJob
        {
            Spec = spec,
            GameDir = full,
            StageDir = stage,
            CacheDir = _layout.Root,
            DeadlineUtc = globalBinds ? deadline : gameDeadline,
            Grace = _s.Grace,
            Log = _log,
            Offline = _s.Offline,
            Progress = (msg, n) =>
            {
                progress.Write(g, msg, doneBefore + n, total);
                if (!IsResultMessage(msg)) _log.Info($"[{g}] step: {msg}");
            },
        };
        _log.Info($"[{g}] extracting from {full} ({(isolated ? "worker process" : "in-process")}, time limit {(job.DeadlineUtc - DateTime.UtcNow).TotalSeconds:F0} s)");

        ExecOutcome outcome;
        if (isolated && proc is not null)
        {
            outcome = proc.Run(job);
            if (outcome.SpawnFailed)
            {
                isolated = false; // same problem would hit every game: stay in-process for the rest of this run
                SafeFs.TryDeleteDirectory(stage);
                Directory.CreateDirectory(stage);
                outcome = inProc.Run(job);
            }
        }
        else outcome = inProc.Run(job);

        if (outcome.TimedOut && globalBinds) _watchdogFired = true;
        _log.Info($"[{g}] module finished in {outcome.Elapsed.TotalSeconds:F1} s ({outcome.Mode}){(outcome.Error is null ? "" : ": " + outcome.Error)}");

        if (!outcome.StagingUsable)
        {
            Commit(g, NotRun(g, outcome.Error ?? "the module did not stop", interrupted: true, dir: full, fingerprint: fp));
            if (!SafeFs.TryDeleteDirectory(stage)) _log.Warn($"[{g}] staging folder {stage} still in use; removed on the next run");
            return;
        }

        var finisher = new Finisher(_layout, _log, _previous, _s.Keep);
        var entries = finisher.Finalize(spec, stage, outcome.Results);
        var swapError = finisher.Swap(spec, stage, entries);
        SafeFs.TryDeleteDirectory(stage);
        if (swapError is not null)
        {
            Commit(g, NotRun(g, swapError, interrupted: true, dir: full, fingerprint: fp));
            return;
        }

        var entry = new ManifestGame
        {
            Dir = Slashes(full),
            FingerprintFiles = fp,
            StartedUtc = started.ToString("O"),
            ElapsedMs = sw.ElapsedMilliseconds,
            Mode = outcome.Mode,
            Interrupted = outcome.Interrupted,
            InterruptedRuns = outcome.Interrupted ? (_previous?.Games.GetValueOrDefault(g)?.InterruptedRuns ?? 0) + 1 : 0,
        };
        Fill(entry, entries, outcome.Error);
        _newlyOk += entries.Count(e => e.Status == AssetStatus.Ok && !e.Kept);
        Commit(g, (entry, entries));
    }

    private static bool IsResultMessage(string msg)
    {
        int c = msg.IndexOf(": ", StringComparison.Ordinal);
        return c > 0 && Sources.All.Any(r => r.Id.Length == c && msg.StartsWith(r.Id, StringComparison.Ordinal))
               && Enum.TryParse<AssetStatus>(msg[(c + 2)..], out _);
    }

    // ------------------------------------------------------------------ state

    private static List<SourceRow> Rows(string g) => Sources.ForGame(Manifest.GameSlug(g)!).ToList();

    private void InitState()
    {
        foreach (var g in AppInfo.Games)
        {
            var prev = _previous?.Games.GetValueOrDefault(g);
            _games[g] = prev ?? new ManifestGame { Status = "missing", Error = "not extracted yet", Mode = "none" };
            foreach (var row in Rows(g))
                _assets[row.Id] = _previous?.Assets.GetValueOrDefault(row.Id) is { } a
                    ? a with { Game = g }
                    : new ManifestAsset(row.Id, g, AssetStatus.Missing, "not extracted yet", null);
            if (prev is not null) Fill(prev, Rows(g).Select(r => _assets[r.Id]).ToList(), prev.Error, keepError: true);
        }
    }

    private void Commit(string g, (ManifestGame Entry, List<ManifestAsset> Assets) r)
    {
        _games[g] = r.Entry;
        foreach (var a in r.Assets) _assets[a.Id] = a;
    }

    /// <summary>This run did not replace the game's pack. The previous content (if any) stays on disk and in the
    /// manifest; otherwise every row is missing (nothing to read) or failed (the attempt broke).</summary>
    private (ManifestGame, List<ManifestAsset>) NotRun(string g, string reason, bool interrupted, string? dir, List<FingerprintEntry>? fingerprint)
    {
        var prev = _previous?.Games.GetValueOrDefault(g);
        var prevAssets = Rows(g).Select(r => _previous?.Assets.GetValueOrDefault(r.Id)).ToList();
        bool havePrevious = prev is not null && prevAssets.Any(a => a?.Status == AssetStatus.Ok) && Directory.Exists(_layout.PackDir(g));
        var entry = new ManifestGame
        {
            Mode = "none",
            StartedUtc = DateTime.UtcNow.ToString("O"),
            Interrupted = interrupted,
            InterruptedRuns = interrupted ? (prev?.InterruptedRuns ?? 0) + 1 : 0,
        };
        List<ManifestAsset> assets;
        if (havePrevious)
        {
            assets = Rows(g).Select((r, i) => prevAssets[i] is { } a ? a with { Game = g }
                : new ManifestAsset(r.Id, g, AssetStatus.Missing, "not extracted yet", null)).ToList();
            entry.Dir = prev!.Dir;
            entry.FingerprintFiles = prev.FingerprintFiles;
            entry.KeptPrevious = true;
            Fill(entry, assets, $"{reason}; content from the previous run ({prev.StartedUtc ?? "earlier"}) is kept");
        }
        else
        {
            var status = interrupted ? AssetStatus.Failed : AssetStatus.Missing;
            assets = Rows(g).Select(r => new ManifestAsset(r.Id, g, status, reason, null)).ToList();
            entry.Dir = dir is null ? null : Slashes(dir);
            entry.FingerprintFiles = fingerprint;
            Fill(entry, assets, reason);
        }
        return (entry, assets);
    }

    /// <summary>Status, counts and error of a game from its rows.</summary>
    private static void Fill(ManifestGame e, List<ManifestAsset> assets, string? error, bool keepError = false)
    {
        e.Counts = new Dictionary<string, int>
        {
            ["ok"] = assets.Count(a => a.Status == AssetStatus.Ok),
            ["missing"] = assets.Count(a => a.Status == AssetStatus.Missing),
            ["failed"] = assets.Count(a => a.Status == AssetStatus.Failed),
            ["unimplemented"] = assets.Count(a => a.Status == AssetStatus.Unimplemented),
            ["kept"] = assets.Count(a => a.Kept),
        };
        e.Status = GameStatus(assets);
        if (keepError) return;
        e.Error = error ?? (e.Status == "ok" ? null : Summary(assets));
    }

    internal static string GameStatus(IReadOnlyCollection<ManifestAsset> assets) =>
        assets.Count > 0 && assets.All(a => a.Status == AssetStatus.Ok) ? "ok"
        : assets.Any(a => a.Status == AssetStatus.Ok) ? "partial"
        : assets.All(a => a.Status == AssetStatus.Missing) ? "missing"
        : "failed";

    private static string Summary(List<ManifestAsset> assets)
    {
        var bad = assets.Where(a => a.Status != AssetStatus.Ok).ToList();
        var groups = bad.GroupBy(a => (a.Status, a.Detail)).ToList();
        string text = groups.Count == 1
            ? $"{bad.Count} of {assets.Count} rows {StatusNames.Of(groups[0].Key.Status)}: {groups[0].Key.Detail}"
            : $"{bad.Count} of {assets.Count} rows not ok: " + string.Join("; ", bad.Take(4).Select(a => $"{a.Id} {StatusNames.Of(a.Status)} ({a.Detail})")) + (bad.Count > 4 ? "; ..." : "");
        return text.Length > 700 ? text[..697] + "..." : text;
    }

    private static string Slashes(string p) => p.Replace('\\', '/');

    private int ExitCode()
    {
        if (_watchdogFired) return ExitCodes.Watchdog;
        if (_newlyOk == 0) return ExitCodes.NothingExtracted;
        return _s.Games.All(g => _games[g].Status == "ok") ? ExitCodes.Ok : ExitCodes.Partial;
    }

    // ------------------------------------------------------------------ manifest

    private Manifest Build(int exit, bool watchdog)
    {
        var m = new Manifest
        {
            Extractor = AppInfo.Version,
            FinishedUtc = DateTime.UtcNow.ToString("O"),
        };
        foreach (var g in AppInfo.Games) m.Games[g] = _games[g];
        foreach (var row in Sources.All)
            if (_assets.TryGetValue(row.Id, out var a)) m.Assets[row.Id] = a;
        bool allOk = AppInfo.Games.All(g => _games[g].Status == "ok");
        bool anyOk = m.Assets.Values.Any(a => a.Status == AssetStatus.Ok);
        m.Status = allOk ? "ok" : anyOk ? "partial" : "failed";
        m.Run = new JsonObject
        {
            ["exitCode"] = exit,
            ["watchdog"] = watchdog,
            ["games"] = new JsonArray(_s.Games.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()),
            ["mode"] = _s.Isolated ? "process" : "in-process",
            ["keep"] = _s.Keep,
            ["startedUtc"] = _startedUtc.ToString("O"),
            ["elapsedMs"] = _clock.ElapsedMilliseconds,
            ["pid"] = Environment.ProcessId,
            ["build"] = AppInfo.Build,
        };
        return m;
    }

    private void WriteManifest(int exit)
    {
        lock (_manifestGate)
        {
            var json = Build(exit, exit == ExitCodes.Watchdog).ToJson();
            SafeFs.AtomicWriteText(_layout.ManifestPath, json);
            _manifestWritten = true;
            _log.Info($"manifest.json written ({json.Length} bytes, status {Build(exit, false).Status})");
        }
    }

    /// <summary>The manifest to write if the last-resort watchdog has to end the process now.</summary>
    private string Emergency()
    {
        var savedGames = new Dictionary<string, ManifestGame>(_games);
        var savedAssets = new Dictionary<string, ManifestAsset>(_assets);
        try
        {
            foreach (var g in _s.Games.Where(g => !_done.Contains(g)))
                Commit(g, NotRun(g, "the extractor's watchdog stopped it before this game finished", interrupted: true, dir: null, fingerprint: null));
            return Build(ExitCodes.Watchdog, true).ToJson();
        }
        finally
        {
            _games.Clear(); foreach (var (k, v) in savedGames) _games[k] = v;
            _assets.Clear(); foreach (var (k, v) in savedAssets) _assets[k] = v;
        }
    }

    private void HardWatchdog()
    {
        try { _log.Error("last-resort watchdog: the run is far past its time limit; writing the manifest and exiting 5"); } catch { }
        try { ProcessExecutor.Current?.Kill(entireProcessTree: true); } catch { }
        bool locked = Monitor.TryEnter(_manifestGate, TimeSpan.FromSeconds(5));
        try
        {
            if (!_manifestWritten && _emergencyJson is { } json)
            {
                try { SafeFs.AtomicWriteText(_layout.ManifestPath, json); _manifestWritten = true; } catch { }
            }
        }
        finally { if (locked) Monitor.Exit(_manifestGate); }
        _s.HardExit(ExitCodes.Watchdog);
    }

    // ------------------------------------------------------------------ housekeeping

    private void RotateLog()
    {
        try
        {
            if (File.Exists(_layout.LogPath)) File.Move(_layout.LogPath, _layout.PreviousLogPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep appending */ }
    }

    private void LogHeader()
    {
        _log.Info($"==== {AppInfo.Banner}, pid {Environment.ProcessId}");
        _log.Info($"os {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}, exe {Environment.ProcessPath}");
        _log.Info($"args: {_s.CommandLine}");
        foreach (var n in _s.Notes) _log.Info($"note: {n}");
        _log.Info($"cache: {_layout.Root}");
        foreach (var g in AppInfo.Games)
            _log.Info($"{g}: {(_s.Games.Contains(g) ? "selected" : "not selected (--only)")}, folder {_s.Dirs.GetValueOrDefault(g) ?? "(none)"} [{_s.DirSources.GetValueOrDefault(g)}]");
        _log.Info($"mode: {(_s.Isolated ? "one worker process per game" : "in-process")}, time limits: run {_s.Timeout.TotalSeconds:F0} s, game {_s.GameTimeout.TotalSeconds:F0} s, grace {_s.Grace.TotalSeconds:F0} s; keep previous good files: {_s.Keep}");
        if (ModuleFactory.FakeBehaviours() is { Count: > 0 } fakes)
            _log.Warn($"TEST MODE: {ModuleFactory.TestModulesEnv} replaces modules: {string.Join(", ", fakes.Select(kv => kv.Key + "=" + kv.Value))}");
    }

    /// <summary>Remove what an interrupted earlier run left (we hold the lock, so nothing else is using it).</summary>
    private void Cleanup()
    {
        if (Directory.Exists(_layout.StagingRoot))
        {
            _log.Info($"removing leftover staging folders: {string.Join(", ", Directory.EnumerateDirectories(_layout.StagingRoot).Select(Path.GetFileName))}");
            if (!SafeFs.TryDeleteDirectory(_layout.StagingRoot)) _log.Warn($"could not remove {_layout.StagingRoot}");
        }
        if (Directory.Exists(_layout.PacksDir))
            foreach (var old in Directory.EnumerateDirectories(_layout.PacksDir, ".old-*").ToList())
            {
                var name = Path.GetFileName(old)!;
                var g = name.Split('-').ElementAtOrDefault(1);
                if (g is not null && AppInfo.Games.Contains(g) && !Directory.Exists(_layout.PackDir(g)))
                {
                    _log.Warn($"restoring {name} as packs/{g} (an earlier swap was interrupted)");
                    try { SafeFs.MoveDirectory(old, _layout.PackDir(g)); continue; } catch (Exception e) { _log.Warn($"restore failed: {e.Message}"); }
                }
                SafeFs.TryDeleteDirectory(old);
            }
        foreach (var dir in new[] { _layout.Root, _layout.StatsDir })
            if (Directory.Exists(dir))
                foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
                    SafeFs.TryDeleteFile(tmp);
    }

    private void LogSummary()
    {
        foreach (var g in AppInfo.Games)
        {
            var e = _games[g];
            var c = e.Counts;
            _log.Info($"summary {g}: {e.Status} (ok {c.GetValueOrDefault("ok")}, missing {c.GetValueOrDefault("missing")}, failed {c.GetValueOrDefault("failed")}, " +
                      $"unimplemented {c.GetValueOrDefault("unimplemented")}, kept {c.GetValueOrDefault("kept")}){(_s.Games.Contains(g) ? "" : " [not run]")}" +
                      (e.Error is null ? "" : $" - {e.Error}"));
            if (!_s.Games.Contains(g)) continue;
            var notable = Rows(g).Select(r => _assets.GetValueOrDefault(r.Id)).OfType<ManifestAsset>()
                .Where(a => a.Status != AssetStatus.Ok || a.Kept);
            foreach (var grp in notable.GroupBy(a => (a.Status, a.Kept, a.Detail)))
            {
                var ids = grp.Select(a => a.Id).ToList();
                _log.Info($"  {(ids.Count == 1 ? ids[0] : $"{ids.Count} rows ({string.Join(", ", ids)})")}: " +
                          $"{StatusNames.Of(grp.Key.Status)}{(grp.Key.Kept ? " (kept)" : "")} - {grp.Key.Detail}");
            }
        }
    }
}
