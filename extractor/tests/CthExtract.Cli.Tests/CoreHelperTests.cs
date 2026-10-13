using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;
using Xunit;

namespace CthExtract.Cli.Tests;

public class CoreHelperTests
{
    [Fact]
    public void Manifest_round_trips_and_starts_with_the_done_marker()
    {
        using var sb = new Sandbox();
        var m = new Manifest { Extractor = "0.1.0", Status = "partial", FinishedUtc = "2026-01-01T00:00:00Z" };
        m.Games["halo"] = new ManifestGame
        {
            Status = "partial", Dir = "C:/MCC", Error = "x", StartedUtc = "s", Mode = "process", Interrupted = true, InterruptedRuns = 2,
            FingerprintFiles = new() { new FingerprintEntry("halo3/maps/shared.map", 123, 456) },
            Counts = new() { ["ok"] = 1, ["failed"] = 2 },
        };
        m.Assets["halo_mesh_magnum"] = new ManifestAsset("halo_mesh_magnum", "halo", AssetStatus.Ok, "d", "meshes/halo/magnum.cthm", Kept: true);
        m.Assets["halo_snd_magnum_fire"] = new ManifestAsset("halo_snd_magnum_fire", "halo", AssetStatus.Unimplemented, null, null);
        m.Write(sb.Cache);
        var text = File.ReadAllText(sb.Layout.ManifestPath);
        Assert.StartsWith("{\"cacheSchema\":1,\"extractor\":\"0.1.0\",\"status\":\"partial\",", text);
        Assert.DoesNotContain("\n", text);
        Assert.Empty(Directory.EnumerateFiles(sb.Cache, "*.tmp"));

        var back = Manifest.TryRead(sb.Cache, out var problem)!;
        Assert.Null(problem);
        var g = back.Games["halo"];
        Assert.Equal(("partial", "C:/MCC", "x", true, 2), (g.Status, g.Dir, g.Error, g.Interrupted, g.InterruptedRuns));
        Assert.True(Fingerprint.Same(m.Games["halo"].FingerprintFiles, g.FingerprintFiles));
        Assert.Equal(2, g.Counts["failed"]);
        Assert.True(back.Assets["halo_mesh_magnum"].Kept);
        Assert.Equal(AssetStatus.Unimplemented, back.Assets["halo_snd_magnum_fire"].Status);
        Assert.Equal("unimplemented", (string?)JsonNode.Parse(text)!["assets"]!["halo_snd_magnum_fire"]!["status"]);
    }

    [Fact]
    public void Manifest_read_problems_are_reported_not_thrown()
    {
        using var sb = new Sandbox();
        Assert.Null(Manifest.TryRead(sb.Cache, out var p1));
        Assert.Equal("no manifest.json", p1);
        Directory.CreateDirectory(sb.Cache);
        File.WriteAllText(sb.Layout.ManifestPath, "[1,2]");
        Assert.Null(Manifest.TryRead(sb.Cache, out var p2));
        Assert.Contains("not a JSON object", p2);
        File.WriteAllText(sb.Layout.ManifestPath, "{\"cacheSchema\":1,\"games\":{\"halo\":{\"status\":5,\"fingerprint\":\"x\"}},\"assets\":{\"halo_mesh_magnum\":{\"status\":\"weird\"}}}");
        var m = Manifest.TryRead(sb.Cache, out var p3)!;
        Assert.Null(p3);
        Assert.Equal("missing", m.Games["halo"].Status);
        Assert.Null(m.Games["halo"].FingerprintFiles);
        Assert.Equal(AssetStatus.Unimplemented, m.Assets["halo_mesh_magnum"].Status);
        Assert.Equal("halo", m.Assets["halo_mesh_magnum"].Game);
    }

    [Fact]
    public void Fingerprint_records_absent_files_and_diffs_changes()
    {
        using var sb = new Sandbox();
        var module = new LambdaModule("gta", (_, _, _) => { }, _ => new[] { "fake/gta.txt", "fake\\absent.bin", "fake/gta.txt" });
        var fp = Fingerprint.Compute(module, sb.Gta, out var err);
        Assert.Null(err);
        Assert.Equal(2, fp.Count);
        Assert.Contains(fp, e => e.Path == "fake/absent.bin" && e.Size == -1 && e.MtimeMs == 0);
        Assert.Contains(fp, e => e.Path == "fake/gta.txt" && e.Size == 100);
        Assert.True(Fingerprint.Same(fp, Fingerprint.Compute(module, sb.Gta, out _)));
        Sandbox.Touch(Path.Combine(sb.Gta, "fake", "absent.bin"), 3);
        var now = Fingerprint.Compute(module, sb.Gta, out _);
        Assert.False(Fingerprint.Same(fp, now));
        Assert.Equal("fake/absent.bin appeared", Fingerprint.Diff(fp, now));

        var throwing = new LambdaModule("gta", (_, _, _) => { }, _ => throw new UnauthorizedAccessException("denied"));
        Assert.Empty(Fingerprint.Compute(throwing, sb.Gta, out var err2));
        Assert.Contains("denied", err2);
    }

    [Fact]
    public void Cache_lock_is_exclusive_and_released_on_dispose()
    {
        using var sb = new Sandbox();
        using (var a = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _))
        {
            Assert.NotNull(a);
            var b = CacheLock.TryAcquire(sb.Cache, TimeSpan.FromMilliseconds(300), out var holder);
            Assert.Null(b);
            Assert.Contains($"pid={Environment.ProcessId}", holder);
        }
        using var c = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _);
        Assert.NotNull(c);
    }

    [Fact]
    public void Progress_file_is_throttled_but_game_changes_and_finish_always_land()
    {
        using var sb = new Sandbox();
        Directory.CreateDirectory(sb.Cache);
        var p = new ProgressFile(sb.Cache);
        p.Write("halo", "a", 1, 10);
        p.Write("halo", "b", 2, 10); // throttled
        Assert.Equal("a", (string?)JsonNode.Parse(File.ReadAllText(sb.Layout.ProgressPath))!["step"]);
        p.Write("gta", "c", 3, 10);
        Assert.Equal("c", (string?)JsonNode.Parse(File.ReadAllText(sb.Layout.ProgressPath))!["step"]);
        p.Write(null, "finished", 12, 10, finished: true);
        var j = JsonNode.Parse(File.ReadAllText(sb.Layout.ProgressPath))!;
        Assert.True((bool)j["finished"]!);
        Assert.Equal(10, (int)j["done"]!); // clamped to total
    }

    [Fact]
    public void Finisher_verify_catches_each_kind_of_bad_output()
    {
        using var sb = new Sandbox();
        var assets = Path.Combine(sb.Root, "a");
        var stats = Path.Combine(sb.Root, "s");
        SourceRow Row(string id) => Sources.All.First(r => r.Id == id);
        void Put(string rel, byte[] b) { var f = Path.Combine(assets, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllBytes(f, b); }

        Assert.Contains("was not written", Finisher.Verify(Row("fn_icon_chug_jug"), assets, stats, out _));
        Put("textures/item/fortnite/chug_jug.png", new byte[30]);
        Assert.Contains("not a PNG", Finisher.Verify(Row("fn_icon_chug_jug"), assets, stats, out _));
        Put("textures/item/fortnite/chug_jug.png", Png.Encode(SyntheticContent.Icon(4, 4, 1)));
        Assert.Null(Finisher.Verify(Row("fn_icon_chug_jug"), assets, stats, out _));

        Put("sounds/gta/siren.ogg", new byte[200]);
        Assert.Contains("not an Ogg Vorbis", Finisher.Verify(Row("gta_snd_siren"), assets, stats, out _));
        Put("sounds/gta/siren.ogg", SyntheticContent.OggLike("x"));
        Assert.Null(Finisher.Verify(Row("gta_snd_siren"), assets, stats, out _));

        var big = Meshes.Sphere(60, 120); // 14400 > maxTris 3000
        Cthm.Write(Path.Combine(assets, "meshes/halo/magnum.cthm"), big);
        Assert.Contains("budget 3000", Finisher.Verify(Row("halo_mesh_magnum"), assets, stats, out _));
        Cthm.Write(Path.Combine(assets, "meshes/halo/magnum.cthm"), SyntheticContent.Box("halo/magnum/0"));
        Assert.Null(Finisher.Verify(Row("halo_mesh_magnum"), assets, stats, out var warning));
        Assert.Contains("material texture(s) missing", warning);

        Directory.CreateDirectory(Path.Combine(stats, "stats"));
        File.WriteAllText(Path.Combine(stats, "stats", "halo_shield.json"), "{}");
        Assert.Contains("non-empty JSON object", Finisher.Verify(Row("halo_stats_shield"), assets, stats, out _));
        File.WriteAllText(Path.Combine(stats, "stats", "halo_shield.json"), "{\"shield\":{\"max\":100}}");
        Assert.Null(Finisher.Verify(Row("halo_stats_shield"), assets, stats, out _));
    }
}
