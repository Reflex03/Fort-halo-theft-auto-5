using System.Text;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract;

/// <summary>
/// The child side of <see cref="ProcessExecutor"/> (hidden command line: --worker &lt;game&gt; --worker-game-dir D
/// --worker-stage S --worker-cache C [--offline]). Runs one game module into the staging folder and reports over
/// stdout, one line per message: "@cth hello", "@cth result {json}", "@cth progress text", "@cth finished {json}";
/// every other line is log text. Reads "cancel" from stdin; stdin closing means the supervisor is gone.
/// </summary>
internal static class Worker
{
    public const string Flag = "--worker";
    public const string Prefix = "@cth ";
    private static readonly object WriteLock = new();
    private static TextWriter _out = Console.Out;

    public static int Run(string[] argv)
    {
        // our own UTF-8 writer: Console's default encoding is the OEM code page on Windows
        try { Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }); }
        catch { }
        _out = Console.Out; // synchronized wrapper: log lines and protocol lines never interleave

        string? game = null, gameDir = null, stage = null, cache = null;
        bool offline = false;
        for (int i = 0; i < argv.Length; i++)
        {
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
            switch (argv[i])
            {
                case Flag: game = Next(); break;
                case "--worker-game-dir": gameDir = Next(); break;
                case "--worker-stage": stage = Next(); break;
                case "--worker-cache": cache = Next(); break;
                case "--offline": offline = true; break;
                case "--console": break;
                default: Console.Error.WriteLine($"worker: unknown argument {argv[i]}"); return ExitCodes.BadArguments;
            }
        }
        if (game is null || gameDir is null || stage is null || cache is null)
        {
            Console.Error.WriteLine("worker: needs --worker <game> --worker-game-dir --worker-stage --worker-cache");
            return ExitCodes.BadArguments;
        }
        // defence in depth: the supervisor already refused a cache inside a game folder; never write there either
        var dirs = new Dictionary<string, string?> { [game] = gameDir };
        if ((GameDirGuard.Problem(stage, dirs) ?? GameDirGuard.Problem(cache, dirs)) is { } inGame)
        {
            Console.Error.WriteLine($"worker: {inGame}");
            return ExitCodes.BadArguments;
        }

        Send("hello", $"{AppInfo.Build} pid {Environment.ProcessId} game {game}");
        using var log = new Log(null);
        using var cts = new CancellationTokenSource();
        var stdin = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = Console.In.ReadLine()) is not null)
                    if (line.Trim().Trim('\uFEFF') == "cancel") { log.Warn($"[{game}] supervisor asked to stop"); cts.Cancel(); }
            }
            catch { }
            // stdin closed: the supervisor is gone (or done with us); stop soon either way
            cts.Cancel();
            Thread.Sleep(3000);
            Environment.Exit(70);
        }) { IsBackground = true, Name = "cth-worker-stdin" };
        stdin.Start();

        string? error = null;
        ExtractContext? ctx = null;
        var run = new Thread(() =>
        {
            try
            {
                var spec = ModuleFactory.Spec(game, cache, offline);
                ctx = new ExtractContext(spec.Slug, spec.Short, Path.Combine(stage, "pack"), Path.Combine(stage, "stats"), Path.Combine(stage, "work"), log)
                {
                    CacheDir = cache,
                };
                ctx.ResultRecorded = r => Send("result", ResultsWire.ToJson(r));
                ctx.Progress = m => Send("progress", m.ReplaceLineEndings(" "));
                var module = spec.Create();
                module.Extract(gameDir, ctx, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                error = "cancelled (time limit)";
                log.Warn($"[{game}] module stopped on request");
            }
            catch (Exception e)
            {
                error = ResultsWire.Describe(e);
                log.Error($"[{game}] module failed: {e}");
            }
            try
            {
                if (ctx is not null)
                {
                    if (error is not null) ResultsWire.FailUnreported(ctx, $"not reached: the {game} module stopped ({error})");
                    ctx.Finish();
                }
            }
            catch (Exception e)
            {
                error ??= ResultsWire.Describe(e);
                log.Error($"[{game}] finishing the pack failed: {e}");
            }
        }, InProcessExecutor.StackBytes) { Name = "cth-worker-" + game };
        run.Start();
        run.Join();

        var arr = new JsonArray();
        if (ctx is not null)
            foreach (var r in ctx.Results.Values) arr.Add(JsonNode.Parse(ResultsWire.ToJson(r)));
        Send("finished", new JsonObject { ["error"] = error, ["results"] = arr }.ToJsonString());
        return 0;
    }

    private static void Send(string verb, string payload)
    {
        lock (WriteLock)
        {
            try { _out.WriteLine(Prefix + verb + " " + payload); _out.Flush(); } catch { }
        }
    }
}
