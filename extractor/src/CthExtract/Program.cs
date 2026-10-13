// cthextract: Craft Theft Halo's content extractor. See docs/CONTRACTS.md for the contract with Melty and the mod,
// docs/requests/extractor.md for the details this executable adds (manifest fields, --check codes, time limits).
using System.Runtime.InteropServices;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static int Main(string[] argv)
    {
        // GUI-subsystem exe: no console unless asked for (debugging from cmd).
        if (OperatingSystem.IsWindows() && argv.Contains("--console"))
            try { AttachConsole(-1); } catch { }
        if (argv.Length > 0 && argv[0] == Worker.Flag) return Worker.Run(argv);
        return Cli(argv, Console.Out, Console.Error);
    }

    /// <param name="specs">Game modules for a cache folder (tests pass fakes); default: the real modules.</param>
    /// <param name="tweak">Adjust run settings before the run (tests: in-process, short limits).</param>
    internal static int Cli(string[] argv, TextWriter stdout, TextWriter stderr,
                            Func<string, bool, IReadOnlyList<GameSpec>>? specs = null, Action<RunSettings>? tweak = null)
    {
        specs ??= ModuleFactory.Specs;
        var o = CliOptions.Parse(argv);
        if (o.Version) { stdout.WriteLine(AppInfo.Banner); return ExitCodes.Ok; }
        if (o.Help) { stdout.WriteLine(AppInfo.Banner); stdout.WriteLine(CliOptions.Usage); return ExitCodes.Ok; }

        string? cacheDir = null;
        string? problem = null;
        if (o.Error is null)
        {
            cacheDir = o.ResolveCacheDir(out problem);
            if (cacheDir is null) o.Error = problem;
            // never write inside a game folder (also guards --check and the bad-arguments manifest)
            else if (GameDirGuard.Problem(cacheDir, o.Dirs) is { } inGame) o.Error = inGame;
        }
        if (o.Error is not null)
        {
            stderr.WriteLine($"cthextract: {o.Error}");
            stderr.WriteLine(CliOptions.Usage);
            TryWriteBadArgsManifest(o, o.Error);
            return ExitCodes.BadArguments;
        }

        if (o.Check) return CheckCommand.Run(o, cacheDir!, specs(cacheDir!, o.Offline), stdout);

        var settings = RunSettings.From(o, cacheDir!, argv);
        settings.Stderr = stderr;
        tweak?.Invoke(settings);
        return Runner.Run(settings, specs(cacheDir!, o.Offline));
    }

    /// <summary>
    /// Melty's setup step waits for manifest.json. When the arguments are unusable but name a cache folder that has no
    /// manifest yet, write a "failed" one so Play is never blocked; the mod then shows the reason. An existing manifest
    /// is never touched (a bad refresh must not hide good content).
    /// </summary>
    private static void TryWriteBadArgsManifest(CliOptions o, string error)
    {
        if (o.Out is null || o.Check) return;
        try
        {
            var root = Path.GetFullPath(o.Out);
            if (GameDirGuard.Problem(root, o.Dirs) is not null) return; // never write inside a game folder
            var layout = new CacheLayout(root);
            if (File.Exists(layout.ManifestPath)) return;
            using var l = CacheLock.TryAcquire(root, TimeSpan.Zero, out _);
            if (l is null || File.Exists(layout.ManifestPath)) return;
            var m = new Manifest { Extractor = AppInfo.Version, Status = "failed", FinishedUtc = DateTime.UtcNow.ToString("O") };
            foreach (var g in AppInfo.Games)
                m.Games[g] = new ManifestGame { Status = "failed", Error = "bad arguments: " + error, Mode = "none" };
            foreach (var row in Sources.All)
                m.Assets[row.Id] = new ManifestAsset(row.Id, Manifest.GameShort(row.Game) ?? "", AssetStatus.Failed, "bad arguments: " + error, null);
            m.Run["exitCode"] = ExitCodes.BadArguments;
            m.Run["error"] = error;
            m.Write(root);
        }
        catch { /* best effort */ }
    }
}
