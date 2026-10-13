using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using CthExtract.Core;

namespace CthExtract.Cli.Tests;

/// <summary>A temporary folder with fake game installs (empty marker files, paths with spaces like real ones).</summary>
internal sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public string Halo => Path.Combine(Root, "Steam Library", "steamapps", "common", "Halo The Master Chief Collection");
    public string Gta => Path.Combine(Root, "Program Files", "Rockstar Games", "Grand Theft Auto V");
    public string Fortnite => Path.Combine(Root, "Program Files", "Epic Games", "Fortnite");
    public string Cache => Path.Combine(Root, "LocalAppData", "CraftTheftHalo", "cache");
    public CacheLayout Layout => new(Cache);

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "cth-cli-tests", Guid.NewGuid().ToString("N")[..12]);
        foreach (var (dir, g) in new[] { (Halo, "halo"), (Gta, "gta"), (Fortnite, "fortnite") })
            Touch(Path.Combine(dir, "fake", g + ".txt"), 100);
    }

    public string Dir(string g) => g switch { "halo" => Halo, "gta" => Gta, _ => Fortnite };

    /// <summary>Melty passes forward slashes.</summary>
    public static string Fw(string p) => p.Replace('\\', '/');

    public static void Touch(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    public string[] AllArgs(params string[] extra) =>
        new[] { "--halo", Fw(Halo), "--gta", Fw(Gta), "--fortnite", Fw(Fortnite), "--out", Fw(Cache) }.Concat(extra).ToArray();

    public JsonObject Manifest() => (JsonObject)JsonNode.Parse(File.ReadAllText(Layout.ManifestPath))!;
    public string ManifestText() => File.ReadAllText(Layout.ManifestPath);
    public string Log() => File.Exists(Layout.LogPath) ? File.ReadAllText(Layout.LogPath) : "";

    /// <summary>path → SHA-256 of every file under a folder (to prove a pack was not touched).</summary>
    public static Dictionary<string, string> Hashes(string dir)
    {
        var d = new Dictionary<string, string>();
        if (!Directory.Exists(dir)) return d;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            d[Path.GetRelativePath(dir, f)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)));
        return d;
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CTH_KEEP_TEST_DIRS") == "1") return;
        SafeFs.TryDeleteDirectory(Root);
    }
}

/// <summary>Runs the CLI in-process with fake modules (no worker processes) and captured output.</summary>
internal static class InProc
{
    public sealed record Result(int Exit, string Stdout, string Stderr, TimeSpan Elapsed);

    public static readonly System.Collections.Concurrent.ConcurrentBag<int> HardExits = new();

    /// <param name="fakes">short → FakeModule behaviour, or a custom module factory.</param>
    public static Result Run(string[] args, Dictionary<string, object>? fakes = null, Action<RunSettings>? tweak = null)
    {
        fakes ??= new() { ["halo"] = "ok", ["gta"] = "ok", ["fortnite"] = "ok" };
        var so = new StringWriter();
        var se = new StringWriter();
        var sw = Stopwatch.StartNew();
        int exit = Program.Cli(args, so, se, (cache, offline) => Specs(fakes), s =>
        {
            s.Isolated = false;
            s.HardExit = code => HardExits.Add(code); // never Environment.Exit inside the test host
            tweak?.Invoke(s);
        });
        return new Result(exit, so.ToString(), se.ToString(), sw.Elapsed);
    }

    public static IReadOnlyList<GameSpec> Specs(Dictionary<string, object> fakes) =>
        AppInfo.Games.Select(g =>
        {
            var slug = Manifest.GameSlug(g)!;
            return fakes.GetValueOrDefault(g) switch
            {
                Func<IGameModule> f => new GameSpec(g, slug, f),
                string b => new GameSpec(g, slug, () => new FakeModule(slug, g, b)),
                _ => new GameSpec(g, slug, () => new FakeModule(slug, g, "missing")),
            };
        }).ToList();
}

/// <summary>A module whose behaviour is a lambda (for cases FakeModule does not cover).</summary>
internal sealed class LambdaModule(string shortName, Action<string, ExtractContext, CancellationToken> extract,
                                   Func<string, IEnumerable<string>>? fingerprint = null) : IGameModule
{
    public string Game => Manifest.GameSlug(shortName)!;
    public string Short => shortName;
    public IEnumerable<string> FingerprintFiles(string gameDir) => fingerprint?.Invoke(gameDir) ?? new[] { $"fake/{shortName}.txt" };
    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct) => extract(gameDir, ctx, ct);
}

/// <summary>Runs the real built extractor (dotnet cthextract.dll) as a separate process.</summary>
internal static class Exe
{
    public sealed record Result(int Exit, string Stdout, string Stderr, TimeSpan Elapsed);

    public static string Dll => Path.Combine(AppContext.BaseDirectory, "cthextract.dll");

    public static string DotNet()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        // <root>/shared/Microsoft.NETCore.App/<version>/ → <root>/dotnet
        var fromRuntime = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", exe));
        if (File.Exists(fromRuntime)) return fromRuntime;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        return exe;
    }

    public static Process Start(IEnumerable<string> args, string? fakeModules)
    {
        var psi = new ProcessStartInfo(DotNet())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(Dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[ModuleFactory.TestModulesEnv] = fakeModules ?? "";
        return Process.Start(psi)!;
    }

    public static Result Run(IEnumerable<string> args, string? fakeModules = null, int timeoutMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        using var p = Start(args, fakeModules);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("cthextract did not finish");
        }
        p.WaitForExit();
        return new Result(p.ExitCode, so.Result, se.Result, sw.Elapsed);
    }
}
