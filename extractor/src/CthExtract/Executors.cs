using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract;

/// <summary>Everything one game's extraction needs. The staging folder holds pack/, stats/ and work/.</summary>
internal sealed class GameJob
{
    public required GameSpec Spec;
    public required string GameDir;
    public required string StageDir;
    public required string CacheDir;
    /// <summary>When the game must stop (per-game budget or the global watchdog, whichever is first).</summary>
    public required DateTime DeadlineUtc;
    public required TimeSpan Grace;
    public required Log Log;
    public bool Offline;
    /// <summary>(message, rows reported so far) as the module works.</summary>
    public Action<string, int>? Progress;

    public string PackDir => Path.Combine(StageDir, "pack");
    public string StatsDir => Path.Combine(StageDir, "stats");
    public string WorkDir => Path.Combine(StageDir, "work");
}

/// <summary>How one game's run ended.</summary>
internal sealed class ExecOutcome
{
    /// <summary>"process" or "in-process".</summary>
    public required string Mode;
    /// <summary>Nothing is writing into the staging folder any more, so it can be finalised and swapped in.</summary>
    public bool StagingUsable = true;
    /// <summary>The worker process never started properly (no hello): run this game in-process instead.</summary>
    public bool SpawnFailed;
    /// <summary>Crash, timeout or abandoned thread: the run did not end normally.</summary>
    public bool Interrupted;
    public bool TimedOut;
    public string? Error;
    public Dictionary<string, AssetResult> Results = new();
    public TimeSpan Elapsed;
}

internal interface IGameExecutor
{
    ExecOutcome Run(GameJob job);
}

internal static class ResultsWire
{
    public static string ToJson(AssetResult r) => new JsonObject
    {
        ["id"] = r.Id,
        ["status"] = StatusNames.Of(r.Status),
        ["detail"] = r.Detail,
        ["out"] = r.Out,
    }.ToJsonString();

    public static AssetResult? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o || JsonRead.Str(o["id"]) is not { } id) return null;
        return new AssetResult(id, StatusNames.Parse(JsonRead.Str(o["status"])), JsonRead.Str(o["detail"]), JsonRead.Str(o["out"]));
    }

    /// <summary>Rows the module never reached get a Failed result naming why (instead of "unimplemented").</summary>
    public static void FailUnreported(ExtractContext ctx, string why)
    {
        var have = ctx.Results;
        foreach (var r in ctx.Rows)
            if (!have.ContainsKey(r.Id)) ctx.Failed(r.Id, why);
    }

    public static string Describe(Exception e) =>
        e is OperationCanceledException ? "cancelled (time limit)" : $"{e.GetType().Name}: {e.Message}";
}

/// <summary>
/// Runs the module on a dedicated thread of this process. Used by tests, by --in-process, and as the fallback when a
/// worker process cannot be started. A module that ignores cancellation past the grace period is abandoned (its thread
/// keeps running in the background) and its staging folder is not used.
/// </summary>
internal sealed class InProcessExecutor : IGameExecutor
{
    /// <summary>Big stack for deeply recursive parsers on odd data.</summary>
    public const int StackBytes = 64 * 1024 * 1024;

    public ExecOutcome Run(GameJob job)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new ExecOutcome { Mode = "in-process" };
        ExtractContext? ctx = null;
        Exception? error = null;
        using var cts = new CancellationTokenSource();
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                ctx = new ExtractContext(job.Spec.Slug, job.Spec.Short, job.PackDir, job.StatsDir, job.WorkDir, job.Log) { CacheDir = job.CacheDir };
                var c = ctx;
                c.Progress = m => job.Progress?.Invoke(m, c.Results.Count);
                var module = job.Spec.Create();
                module.Extract(job.GameDir, c, cts.Token);
            }
            catch (OperationCanceledException e) when (cts.IsCancellationRequested)
            {
                error = e;
                job.Log.Warn($"[{job.Spec.Short}] module stopped on request");
            }
            catch (Exception e) { error = e; }
            try
            {
                if (ctx is not null)
                {
                    if (error is not null) ResultsWire.FailUnreported(ctx, $"not reached: the {job.Spec.Short} module stopped ({ResultsWire.Describe(error)})");
                    ctx.Finish();
                }
            }
            catch (Exception e) { error ??= e; }
            done.Set();
        }, StackBytes) { IsBackground = true, Name = "cth-" + job.Spec.Short };
        thread.Start();

        var wait = job.DeadlineUtc - DateTime.UtcNow;
        if (!done.Wait(wait > TimeSpan.Zero ? wait : TimeSpan.Zero))
        {
            outcome.TimedOut = outcome.Interrupted = true;
            job.Log.Warn($"[{job.Spec.Short}] time limit reached after {sw.Elapsed.TotalSeconds:F0} s; asking the module to stop");
            cts.Cancel();
            if (!done.Wait(job.Grace))
            {
                job.Log.Error($"[{job.Spec.Short}] module did not stop within {job.Grace.TotalSeconds:F0} s; abandoning its thread (staging folder not used)");
                outcome.StagingUsable = false;
            }
        }
        if (error is not null)
        {
            outcome.Interrupted = true;
            if (error is not OperationCanceledException || !outcome.TimedOut)
                job.Log.Error($"[{job.Spec.Short}] module failed: {error}");
        }
        outcome.Error = outcome.TimedOut
            ? $"timed out after {sw.Elapsed.TotalSeconds:F0} s"
            : error is null ? null : ResultsWire.Describe(error);
        outcome.Results = ctx is null ? new() : new Dictionary<string, AssetResult>(ctx.Results);
        outcome.Elapsed = sw.Elapsed;
        return outcome;
    }
}

/// <summary>
/// Runs the module in a child process (this same executable with --worker), so a fatal crash (access violation,
/// stack overflow, FailFast) or a hang costs only that game: the supervisor always gets to write the manifest.
/// Results stream back over stdout as they are recorded; "cancel" on stdin asks the worker to stop; after the grace
/// period the worker's process tree is killed.
/// </summary>
internal sealed class ProcessExecutor(WorkerCommand command) : IGameExecutor
{
    public static TimeSpan HelloTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The live worker, so the hard watchdog can kill it.</summary>
    public static volatile Process? Current;

    public ExecOutcome Run(GameJob job)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new ExecOutcome { Mode = "process" };
        var results = new Dictionary<string, AssetResult>();
        var gate = new object();
        var hello = new ManualResetEventSlim(false);
        string? finishedError = null;
        bool finished = false;
        Dictionary<string, AssetResult>? finalResults = null;

        var psi = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = job.CacheDir,
        };
        foreach (var a in command.Prefix) psi.ArgumentList.Add(a);
        foreach (var a in new[] { Worker.Flag, job.Spec.Short, "--worker-game-dir", job.GameDir, "--worker-stage", job.StageDir, "--worker-cache", job.CacheDir })
            psi.ArgumentList.Add(a);
        if (job.Offline) psi.ArgumentList.Add("--offline");

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = e.Data;
            if (!line.StartsWith(Worker.Prefix, StringComparison.Ordinal)) { job.Log.Raw(line); return; }
            var rest = line[Worker.Prefix.Length..];
            int sp = rest.IndexOf(' ');
            var verb = sp < 0 ? rest : rest[..sp];
            var payload = sp < 0 ? "" : rest[(sp + 1)..];
            try
            {
                switch (verb)
                {
                    case "hello":
                        job.Log.Info($"[{job.Spec.Short}] worker started: {payload}");
                        hello.Set();
                        break;
                    case "result":
                        if (ResultsWire.FromJson(JsonNode.Parse(payload)) is { } r)
                        {
                            int n;
                            lock (gate) { results[r.Id] = r; n = results.Count; }
                            job.Progress?.Invoke($"{r.Id}: {r.Status}", n);
                        }
                        break;
                    case "progress":
                        {
                            int n;
                            lock (gate) n = results.Count;
                            job.Progress?.Invoke(payload, n);
                            break;
                        }
                    case "finished":
                        {
                            var o = JsonNode.Parse(payload) as JsonObject;
                            var all = new Dictionary<string, AssetResult>();
                            if (o?["results"] is JsonArray arr)
                                foreach (var n in arr)
                                    if (ResultsWire.FromJson(n) is { } fr) all[fr.Id] = fr;
                            lock (gate) { finalResults = all; finishedError = JsonRead.Str(o?["error"]); finished = true; }
                            break;
                        }
                    default:
                        job.Log.Raw(line);
                        break;
                }
            }
            catch (Exception ex) { job.Log.Warn($"[{job.Spec.Short}] unreadable worker message ({ex.GetType().Name}): {line}"); }
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) job.Log.Raw($"[{job.Spec.Short} stderr] {e.Data}"); };

        try
        {
            if (!p.Start()) throw new InvalidOperationException("Process.Start returned false");
        }
        catch (Exception e)
        {
            job.Log.Warn($"[{job.Spec.Short}] cannot start a worker process ({command}): {e.GetType().Name}: {e.Message}");
            outcome.SpawnFailed = true;
            outcome.StagingUsable = true;
            return outcome;
        }
        Current = p;
        try
        {
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // 1. the worker must say hello (proves stdout works in this environment)
            var helloBy = DateTime.UtcNow + HelloTimeout;
            while (!hello.IsSet && !p.HasExited && DateTime.UtcNow < helloBy) hello.Wait(100);
            if (!hello.IsSet)
            {
                if (!p.HasExited) Kill(p, job);
                p.WaitForExit();
                hello.Wait(200);
                if (!hello.IsSet)
                {
                    job.Log.Warn($"[{job.Spec.Short}] worker process gave no hello (exit {SafeExitCode(p)}); falling back to in-process");
                    outcome.SpawnFailed = true;
                    return outcome;
                }
            }

            // 2. wait for it to finish, within the deadline
            var wait = job.DeadlineUtc - DateTime.UtcNow;
            if (!p.WaitForExit(wait > TimeSpan.Zero ? wait : TimeSpan.Zero))
            {
                outcome.TimedOut = true;
                job.Log.Warn($"[{job.Spec.Short}] time limit reached after {sw.Elapsed.TotalSeconds:F0} s; asking the worker to stop");
                try { p.StandardInput.WriteLine("cancel"); p.StandardInput.Flush(); } catch { }
                if (!p.WaitForExit(job.Grace))
                {
                    job.Log.Error($"[{job.Spec.Short}] worker did not stop within {job.Grace.TotalSeconds:F0} s; killing it");
                    Kill(p, job);
                }
            }
            p.WaitForExit(); // drains the redirected streams
            try { p.StandardInput.Close(); } catch { }
        }
        finally { Current = null; }

        int code = SafeExitCode(p);
        lock (gate)
        {
            outcome.Results = finalResults ?? new Dictionary<string, AssetResult>(results);
            if (finished && code == 0 && !outcome.TimedOut)
            {
                outcome.Error = finishedError;
                outcome.Interrupted = finishedError is not null;
            }
            else
            {
                outcome.Interrupted = true;
                outcome.Error = outcome.TimedOut
                    ? $"timed out after {sw.Elapsed.TotalSeconds:F0} s"
                    : $"worker process ended abnormally: {DescribeExit(code)}" + (finishedError is null ? "" : $"; module error: {finishedError}");
                if (!outcome.TimedOut) job.Log.Error($"[{job.Spec.Short}] {outcome.Error}");
            }
        }
        // after a crash or kill, rows the worker never reported are failed with the reason
        foreach (var row in Core.Generated.Sources.ForGame(job.Spec.Slug))
            if (!outcome.Results.ContainsKey(row.Id))
                outcome.Results[row.Id] = new AssetResult(row.Id, AssetStatus.Failed, $"not reached: {outcome.Error ?? "worker ended without reporting it"}", null);
        outcome.StagingUsable = true; // the process is gone: nothing writes into the staging folder any more
        outcome.Elapsed = sw.Elapsed;
        return outcome;
    }

    private static void Kill(Process p, GameJob job)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception e) { job.Log.Warn($"[{job.Spec.Short}] kill failed: {e.Message}"); }
        try { p.WaitForExit(10_000); } catch { }
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.HasExited ? p.ExitCode : int.MinValue; } catch { return int.MinValue; }
    }

    public static string DescribeExit(int code) => code switch
    {
        int.MinValue => "exit code unknown",
        unchecked((int)0xC0000005) => "exit 0xC0000005 (access violation)",
        unchecked((int)0xC00000FD) => "exit 0xC00000FD (stack overflow)",
        unchecked((int)0xC0000409) => "exit 0xC0000409 (fail-fast)",
        unchecked((int)0xE0434352) => "exit 0xE0434352 (unhandled .NET exception)",
        unchecked((int)0xC0000017) or unchecked((int)0xE0000001) => $"exit 0x{code:X8} (out of memory)",
        134 => "exit 134 (abort: fail-fast or unhandled exception)",
        137 => "exit 137 (killed)",
        139 => "exit 139 (segmentation fault)",
        _ => code < 0 ? $"exit 0x{code:X8}" : $"exit {code}",
    };
}

/// <summary>How to start this executable again as a worker.</summary>
internal sealed record WorkerCommand(string FileName, IReadOnlyList<string> Prefix)
{
    /// <summary>The running executable; under "dotnet cthextract.dll" it is dotnet plus the dll.</summary>
    public static WorkerCommand Self()
    {
        var path = Environment.ProcessPath ?? "cthextract";
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return new WorkerCommand(path, new[] { Path.Combine(AppContext.BaseDirectory, "cthextract.dll") });
        return new WorkerCommand(path, Array.Empty<string>());
    }

    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(Prefix));
}
