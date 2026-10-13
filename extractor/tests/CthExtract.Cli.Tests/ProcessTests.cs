using System.Diagnostics;
using System.Text.Json.Nodes;
using CthExtract.Core.Generated;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>
/// The real built extractor as separate processes ("dotnet cthextract.dll"), with its default one-worker-process-per-game
/// mode. Fake modules come in through CTHEXTRACT_TEST_MODULES; one test runs the real Halo/GTA/Fortnite modules on
/// empty folders. These cover what in-process tests cannot: fatal crashes, kills, stdin cancel, the OS lock.
/// </summary>
[Collection("processes")]
public class ProcessTests
{
    [Fact]
    public void Version_prints_from_the_built_executable()
    {
        var r = Exe.Run(new[] { "--version" });
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("cthextract 0.1.0", r.Stdout);
    }

    [Fact]
    public void All_ok_through_worker_processes()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs(), "halo=ok,gta=ok,fortnite=ok");
        Assert.True(r.Exit == 0, r.Stderr + sb.Log());
        var m = sb.Manifest();
        Assert.StartsWith("{\"cacheSchema\":1,", sb.ManifestText());
        foreach (var g in new[] { "halo", "gta", "fortnite" })
        {
            Assert.Equal("ok", (string?)m["games"]![g]!["status"]);
            Assert.Equal("process", (string?)m["games"]![g]!["mode"]);
        }
        var log = sb.Log();
        Assert.Contains("[halo] worker started:", log);
        Assert.Contains("TEST MODE", log);
        Assert.Contains("FAKE module 'ok'", log); // the worker's log lines are relayed into extract.log
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("fortnite"), "meshes/fortnite/battle_bus.cthm")));
    }

    [Fact]
    public void A_fatal_crash_in_a_worker_costs_only_that_game()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs(), "halo=failfast,gta=ok,fortnite=ok");
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        var halo = m["games"]!["halo"]!;
        Assert.Equal("partial", (string?)halo["status"]);
        Assert.Contains("worker process ended abnormally", (string?)halo["error"]);
        Assert.True((bool)halo["interrupted"]!);
        Assert.Equal(2, (int)halo["counts"]!["ok"]!); // streamed before the crash
        var rows = Sources.ForGame("halo-mcc").ToList();
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), rows[0].Out)));
        Assert.Equal("ok", (string?)m["games"]!["gta"]!["status"]);
        Assert.Equal("ok", (string?)m["games"]!["fortnite"]!["status"]);
    }

    [Fact]
    public void A_worker_that_exits_early_is_a_crash_too()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs("--only", "gta"), "gta=exit");
        Assert.Equal(1, r.Exit);
        Assert.Contains("exit 42", (string?)sb.Manifest()["games"]!["gta"]!["error"]);
    }

    [Fact]
    public void A_hung_worker_is_killed_by_the_watchdog_and_the_manifest_is_still_written()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs("--timeout-sec", "4", "--grace-sec", "1"), "halo=ok,gta=hang,fortnite=ok");
        Assert.Equal(5, r.Exit);
        Assert.InRange(r.Elapsed.TotalSeconds, 4, 30);
        var m = sb.Manifest();
        Assert.True((bool)m["run"]!["watchdog"]!);
        var gta = m["games"]!["gta"]!;
        Assert.Contains("timed out", (string?)gta["error"]);
        Assert.Equal(1, (int)gta["counts"]!["ok"]!); // what it reported before hanging is kept
        Assert.Contains("killing it", sb.Log());
        Assert.Contains("not started", (string?)m["games"]!["fortnite"]!["error"]);
    }

    [Fact]
    public void A_cooperative_worker_stops_on_cancel_within_the_grace_period()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs("--game-timeout-sec", "2", "--grace-sec", "20"), "halo=coop,gta=ok,fortnite=ok");
        Assert.Equal(1, r.Exit);
        Assert.InRange(r.Elapsed.TotalSeconds, 2, 20); // did not need the 20 s grace
        var log = sb.Log();
        Assert.Contains("supervisor asked to stop", log);
        Assert.DoesNotContain("killing it", log);
        var halo = sb.Manifest()["games"]!["halo"]!;
        Assert.Equal("partial", (string?)halo["status"]);
        Assert.Contains("timed out", (string?)halo["error"]);
        Assert.Equal("ok", (string?)sb.Manifest()["games"]!["gta"]!["status"]);
    }

    [Fact]
    public void Real_modules_on_empty_folders_report_every_row_missing()
    {
        using var sb = new Sandbox();
        var r = Exe.Run(sb.AllArgs("--offline"));
        Assert.True(r.Exit == 2, $"exit {r.Exit}\n{r.Stderr}\n{sb.Log()}");
        var m = sb.Manifest();
        Assert.StartsWith("{\"cacheSchema\":1,", sb.ManifestText());
        foreach (var row in Sources.All)
            Assert.Equal("missing", (string?)m["assets"]![row.Id]!["status"]);
        Assert.Contains("no Halo 3 maps found", (string?)m["games"]!["halo"]!["error"]);
        Assert.Contains("GTA V folder not found", (string?)m["games"]!["gta"]!["error"]);
        Assert.Contains("Fortnite folder not found", (string?)m["games"]!["fortnite"]!["error"]);
        Assert.DoesNotContain("TEST MODE", sb.Log());
    }

    [Fact]
    public void A_second_extractor_process_gets_exit_4_while_the_first_runs()
    {
        using var sb = new Sandbox();
        using var first = Exe.Start(sb.AllArgs("--timeout-sec", "6", "--grace-sec", "1"), "halo=hang");
        var sw = Stopwatch.StartNew();
        while (!File.Exists(sb.Layout.LockInfoPath) && sw.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(50);
        Thread.Sleep(300);
        var second = Exe.Run(sb.AllArgs("--only", "gta"), "gta=ok");
        Assert.Equal(4, second.Exit);
        Assert.Contains("another extractor", second.Stderr);
        Assert.True(first.WaitForExit(60_000));
        Assert.Equal(5, first.ExitCode);
        // the locked-out run wrote nothing: gta's entry is the first run's ("not started" after the watchdog)
        Assert.Contains("not started", (string?)sb.Manifest()["games"]!["gta"]!["error"]);
        Assert.Equal(5, (int)sb.Manifest()["run"]!["exitCode"]!);
    }

    [Fact]
    public void Bad_arguments_and_check_through_the_executable()
    {
        using var sb = new Sandbox();
        var bad = Exe.Run(new[] { "--out", sb.Cache, "--nope" });
        Assert.Equal(3, bad.Exit);
        Assert.Contains("unknown option --nope", bad.Stderr);
        File.Delete(sb.Layout.ManifestPath);

        var check = Exe.Run(sb.AllArgs("--check"));
        Assert.Equal(10, check.Exit);
        Assert.Equal("full", (string?)JsonNode.Parse(check.Stdout)!["advice"]);

        Assert.Equal(0, Exe.Run(sb.AllArgs(), "halo=ok,gta=ok,fortnite=ok").Exit);
        var fresh = Exe.Run(sb.AllArgs("--check"), "halo=ok,gta=ok,fortnite=ok");
        Assert.Equal(0, fresh.Exit);
    }
}

[CollectionDefinition("processes", DisableParallelization = true)]
public class ProcessCollection { }
