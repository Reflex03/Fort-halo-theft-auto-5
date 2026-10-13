using System.Text.Json.Nodes;
using CthExtract.Core;
using Xunit;

namespace CthExtract.Cli.Tests;

public class CheckTests
{
    private static (int Exit, JsonObject Json) Check(Sandbox sb, params string[] extra)
    {
        var r = InProc.Run(sb.AllArgs(new[] { "--check" }.Concat(extra).ToArray()));
        var json = (JsonObject)JsonNode.Parse(r.Stdout.Trim())!;
        Assert.Equal(r.Exit, (int)json["exitCode"]!);
        Assert.True(JsonNode.DeepEquals(json, JsonNode.Parse(File.ReadAllText(sb.Layout.CheckPath))));
        return (r.Exit, json);
    }

    private static string State(JsonObject j, string g) => (string)j["games"]![g]!["state"]!;

    [Fact]
    public void No_cache_means_a_full_run()
    {
        using var sb = new Sandbox();
        var (exit, j) = Check(sb);
        Assert.Equal(10, exit);
        Assert.Equal("full", (string?)j["advice"]);
        Assert.Equal(3, ((JsonArray)j["rerun"]!).Count);
        Assert.False((bool)j["running"]!);
    }

    [Fact]
    public void Fresh_then_updated_after_a_game_update_then_fresh_after_refresh()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var (exit, j) = Check(sb);
        Assert.Equal(0, exit);
        Assert.Equal("fresh", State(j, "gta"));

        Sandbox.Touch(Path.Combine(sb.Gta, "fake", "gta.txt"), 101); // a game update changes size + mtime
        (exit, j) = Check(sb);
        Assert.Equal(12, exit);
        Assert.Equal("updated", State(j, "gta"));
        Assert.Contains("fake/gta.txt changed (100->101 bytes)", (string?)j["games"]!["gta"]!["detail"]);
        Assert.Equal(new[] { "gta" }, ((JsonArray)j["updated"]!).Select(x => (string)x!));

        Assert.Equal(0, InProc.Run(sb.AllArgs("--only", "gta")).Exit);
        Assert.Equal(0, Check(sb).Exit);
    }

    [Fact]
    public void Failed_game_is_rerun_only_when_its_inputs_change()
    {
        using var sb = new Sandbox();
        File.Delete(Path.Combine(sb.Halo, "fake", "halo.txt"));
        Assert.Equal(1, InProc.Run(sb.AllArgs(), new() { ["halo"] = "missing", ["gta"] = "ok", ["fortnite"] = "ok" }).Exit);
        var (exit, j) = Check(sb);
        Assert.Equal(0, exit);
        Assert.Equal("unchanged", State(j, "halo"));

        Sandbox.Touch(Path.Combine(sb.Halo, "fake", "halo.txt"), 5); // "installed Halo 3"
        (exit, j) = Check(sb);
        Assert.Equal(11, exit);
        Assert.Equal("changed", State(j, "halo"));
        Assert.Contains("fake/halo.txt appeared", (string?)j["games"]!["halo"]!["detail"]);
        Assert.Equal(new[] { "halo" }, ((JsonArray)j["rerun"]!).Select(x => (string)x!));
    }

    [Fact]
    public void Moved_folder_never_extracted_and_extractor_update_ask_for_a_rerun()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs("--only", "halo,gta")).Exit); // fortnite never run
        var r = InProc.Run(new[] { "--check", "--out", sb.Cache, "--halo", Path.Combine(sb.Root, "Other MCC") });
        var j = (JsonObject)JsonNode.Parse(r.Stdout)!;
        Assert.Equal(11, r.Exit);
        Assert.Equal("moved", State(j, "halo"));
        Assert.Equal("fresh", State(j, "gta")); // no folder given now: the manifest's folder is checked
        Assert.Equal("never", State(j, "fortnite"));

        var text = File.ReadAllText(sb.Layout.ManifestPath).Replace("\"extractor\":\"0.1.0\"", "\"extractor\":\"0.0.9\"");
        File.WriteAllText(sb.Layout.ManifestPath, text);
        var (exit, j2) = Check(sb, "--only", "gta");
        Assert.Equal(11, exit);
        Assert.Equal("extractor-updated", State(j2, "gta"));
        Assert.Null(j2["games"]!["halo"]); // --only limits what is checked
    }

    [Fact]
    public void Interrupted_games_are_retried_three_times_then_left_alone()
    {
        using var sb = new Sandbox();
        var fakes = new Dictionary<string, object> { ["halo"] = "ok", ["gta"] = "coop", ["fortnite"] = "ok" };
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            InProc.Run(sb.AllArgs("--only", "gta", "--game-timeout-sec", "1"), fakes);
            var (exit, j) = Check(sb, "--only", "gta");
            if (attempt < 3)
            {
                Assert.Equal(11, exit);
                Assert.Equal("interrupted", State(j, "gta"));
            }
            else
            {
                Assert.NotEqual("interrupted", State(j, "gta"));
            }
        }
    }

    [Fact]
    public void Unreadable_or_other_schema_manifest_means_a_full_run()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(sb.Cache);
        File.WriteAllText(sb.Layout.ManifestPath, "{\"cacheSchema\":2,\"games\":{}}");
        var (exit, j) = Check(sb);
        Assert.Equal(10, exit);
        Assert.Contains("cacheSchema 2", (string?)j["manifest"]!["problem"]);
        File.WriteAllText(sb.Layout.ManifestPath, "{ not json");
        Assert.Equal(10, Check(sb).Exit);
    }

    [Fact]
    public void Running_reports_a_held_lock_and_check_never_takes_it_for_long()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        using (var l = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _))
        {
            var (_, j) = Check(sb);
            Assert.True((bool)j["running"]!);
        }
        var (_, j2) = Check(sb);
        Assert.False((bool)j2["running"]!);
        using var again = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _);
        Assert.NotNull(again);
    }
}
