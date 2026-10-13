using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>Whole runs through Program.Cli with fake modules in-process (synthetic content only).</summary>
public class RunTests
{
    private static Dictionary<string, object> Fakes(string halo = "ok", string gta = "ok", string fortnite = "ok") =>
        new() { ["halo"] = halo, ["gta"] = gta, ["fortnite"] = fortnite };

    [Fact]
    public void All_ok_exits_0_and_writes_packs_stats_sounds_progress_log_and_manifest()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs());
        Assert.Equal(0, r.Exit);

        var text = sb.ManifestText();
        Assert.StartsWith("{\"cacheSchema\":1,", text);
        var m = sb.Manifest();
        Assert.Equal("ok", (string?)m["status"]);
        Assert.Equal("0.1.0", (string?)m["extractor"]);
        foreach (var g in AppInfo.Games)
        {
            var e = m["games"]![g]!;
            Assert.Equal("ok", (string?)e["status"]);
            Assert.Null((string?)e["error"]);
            Assert.Equal(Sandbox.Fw(sb.Dir(g)), (string?)e["dir"]);
            var fp = (JsonArray)e["fingerprint"]!;
            Assert.Equal($"fake/{g}.txt", (string?)fp[0]!["path"]);
            Assert.Equal(100, (long)fp[0]!["size"]!);
            Assert.True((long)fp[0]!["mtimeMs"]! > 0);
            Assert.True(File.Exists(Path.Combine(sb.Layout.PackDir(g), "pack.mcmeta")));
        }
        foreach (var row in Sources.All)
        {
            var a = m["assets"]![row.Id]!;
            Assert.Equal("ok", (string?)a["status"]);
            Assert.Equal(row.Out, (string?)a["out"]);
        }

        // files where CONTRACTS.md says
        var mesh = Cthm.Read(Path.Combine(sb.Layout.AssetsDir("halo"), "meshes/halo/assault_rifle.cthm"));
        Assert.Equal(12, mesh.TriangleCount);
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "textures", mesh.Submeshes[0].Material + ".png")));
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("fortnite"), "textures/item/fortnite/chug_jug.png")));
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("gta"), "sounds/gta/siren.ogg")));
        var sounds = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(sb.Layout.AssetsDir("halo"), "sounds.json")))!;
        Assert.True((bool)sounds["halo.assault_rifle_fire"]!["replace"]!);
        Assert.Equal("crafttheft:halo/assault_rifle_fire", (string?)sounds["halo.assault_rifle_fire"]!["sounds"]![0]!["name"]);
        Assert.True(File.Exists(Path.Combine(sb.Cache, "stats", "halo_weapons.json")));
        Assert.True(File.Exists(Path.Combine(sb.Cache, "stats", "halo_shield.json")));

        // nothing left behind
        Assert.False(Directory.Exists(sb.Layout.StagingRoot));
        Assert.Empty(Directory.EnumerateFiles(sb.Cache, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(sb.Layout.PacksDir, ".old-*"));
        Assert.False(Directory.Exists(Path.Combine(sb.Layout.PackDir("halo"), "work")));

        var progress = JsonNode.Parse(File.ReadAllText(sb.Layout.ProgressPath))!;
        Assert.True((bool)progress["finished"]!);
        Assert.Equal((int)progress["total"]!, (int)progress["done"]!);
        Assert.Equal(Sources.All.Count, (int)progress["total"]!);

        var log = sb.Log();
        Assert.Contains("exit 0 (all ok)", log);
        Assert.Contains("FAKE module 'ok'", log);
        Assert.Contains("[halo] pack swapped into", log);
    }

    [Fact]
    public void Manifest_is_the_last_file_written()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var manifestTime = File.GetLastWriteTimeUtc(sb.Layout.ManifestPath);
        var others = Directory.EnumerateFiles(sb.Cache, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("extract.log") && !f.EndsWith("manifest.json") && !Path.GetFileName(f).StartsWith(".lock"));
        foreach (var f in others) Assert.True(File.GetLastWriteTimeUtc(f) <= manifestTime, $"{f} written after manifest.json");
    }

    [Fact]
    public void A_missing_game_gives_partial_exit_1_with_its_reason()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs(), Fakes(halo: "missing"));
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        Assert.Equal("partial", (string?)m["status"]);
        Assert.Equal("missing", (string?)m["games"]!["halo"]!["status"]);
        Assert.Contains("fake: halo is not installed", (string?)m["games"]!["halo"]!["error"]);
        Assert.Equal("missing", (string?)m["assets"]!["halo_mesh_magnum"]!["status"]);
        Assert.Null((string?)m["assets"]!["halo_mesh_magnum"]!["out"]);
        Assert.Equal("ok", (string?)m["games"]!["gta"]!["status"]);
        Assert.False(File.Exists(Path.Combine(sb.Cache, "stats", "halo_weapons.json")));
    }

    [Fact]
    public void Nothing_extracted_exits_2_and_still_writes_the_manifest()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs(), Fakes("missing", "missing", "missing"));
        Assert.Equal(2, r.Exit);
        Assert.StartsWith("{\"cacheSchema\":1,", sb.ManifestText());
        Assert.Equal("failed", (string?)sb.Manifest()["status"]);
    }

    [Fact]
    public void Folders_that_do_not_exist_or_are_not_given_are_missing_not_errors()
    {
        using var sb = new Sandbox();
        var args = new[] { "--halo", Path.Combine(sb.Root, "nope"), "--gta", "{game:gta-v}", "--out", sb.Cache };
        var r = InProc.Run(args);
        Assert.Equal(2, r.Exit);
        var m = sb.Manifest();
        Assert.Equal("missing", (string?)m["games"]!["halo"]!["status"]);
        Assert.Contains("folder not found", (string?)m["games"]!["halo"]!["error"]);
        Assert.Contains("no gta folder given", (string?)m["games"]!["gta"]!["error"]);
        Assert.Contains("no fortnite folder given", (string?)m["games"]!["fortnite"]!["error"]);
        Assert.Contains("unexpanded placeholder '{game:gta-v}' ignored", sb.Log());
    }

    [Fact]
    public void An_exception_in_one_game_is_isolated_and_keeps_what_it_reported()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs(), Fakes(halo: "throw"));
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        var halo = m["games"]!["halo"]!;
        Assert.Equal("partial", (string?)halo["status"]);
        Assert.Contains("fake parser failure", (string?)halo["error"]);
        Assert.True((bool)halo["interrupted"]!);
        Assert.Equal(2, (int)halo["counts"]!["ok"]!);
        var rows = Sources.ForGame("halo-mcc").ToList();
        Assert.Equal("ok", (string?)m["assets"]![rows[0].Id]!["status"]);
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), rows[0].Out)));
        Assert.Equal("failed", (string?)m["assets"]![rows[5].Id]!["status"]);
        Assert.Contains("not reached", (string?)m["assets"]![rows[5].Id]!["detail"]);
        Assert.Equal("ok", (string?)m["games"]!["gta"]!["status"]);
        Assert.Equal("ok", (string?)m["games"]!["fortnite"]!["status"]);
    }

    [Fact]
    public void A_module_constructor_that_throws_only_fails_that_game()
    {
        using var sb = new Sandbox();
        var fakes = Fakes();
        int created = 0;
        fakes["gta"] = (Func<IGameModule>)(() => ++created == 1 ? new FakeModule("gta-v", "gta", "ok") : throw new TypeLoadException("broken module"));
        var r = InProc.Run(sb.AllArgs(), fakes);
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        Assert.Equal("failed", (string?)m["games"]!["gta"]!["status"]);
        Assert.Contains("broken module", (string?)m["games"]!["gta"]!["error"]);
        Assert.Equal("ok", (string?)m["games"]!["fortnite"]!["status"]);
    }

    [Fact]
    public void Only_leaves_other_games_packs_stats_and_manifest_entries_untouched()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var before = sb.Manifest();
        var gtaFiles = Sandbox.Hashes(sb.Layout.PackDir("gta"));
        var fnFiles = Sandbox.Hashes(sb.Layout.PackDir("fortnite"));

        var r = InProc.Run(sb.AllArgs("--only", "halo", "--no-keep"), Fakes(halo: "partial", gta: "throw", fortnite: "throw"));
        Assert.Equal(1, r.Exit);
        var after = sb.Manifest();
        Assert.Equal(gtaFiles, Sandbox.Hashes(sb.Layout.PackDir("gta")));
        Assert.Equal(fnFiles, Sandbox.Hashes(sb.Layout.PackDir("fortnite")));
        Assert.True(JsonNode.DeepEquals(before["games"]!["gta"], after["games"]!["gta"]));
        Assert.True(JsonNode.DeepEquals(before["games"]!["fortnite"], after["games"]!["fortnite"]));
        foreach (var row in Sources.All.Where(x => x.Game != "halo-mcc"))
            Assert.True(JsonNode.DeepEquals(before["assets"]![row.Id], after["assets"]![row.Id]), row.Id);
        Assert.Equal("partial", (string?)after["games"]!["halo"]!["status"]);
        Assert.Equal("partial", (string?)after["status"]);
        Assert.Contains("\"halo\"", after["run"]!["games"]!.ToJsonString());
        Assert.DoesNotContain("gta", after["run"]!["games"]!.ToJsonString());
    }

    [Fact]
    public void Rows_that_fail_this_time_keep_the_previous_good_file()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var haloFiles = Sandbox.Hashes(sb.Layout.PackDir("halo"));
        var stats = File.ReadAllText(Path.Combine(sb.Cache, "stats", "halo_weapons.json"));

        var r = InProc.Run(sb.AllArgs("--only", "halo"), Fakes(halo: "missing"));
        Assert.Equal(2, r.Exit); // nothing new extracted
        var m = sb.Manifest();
        var halo = m["games"]!["halo"]!;
        Assert.Equal("ok", (string?)halo["status"]);
        Assert.Equal(23, (int)halo["counts"]!["kept"]!);
        var a = m["assets"]!["halo_snd_magnum_fire"]!;
        Assert.Equal("ok", (string?)a["status"]);
        Assert.True((bool)a["kept"]!);
        Assert.Contains("kept from the previous run", (string?)a["detail"]);
        Assert.Contains("this run: missing: fake: halo is not installed", (string?)a["detail"]);
        Assert.Equal(haloFiles, Sandbox.Hashes(sb.Layout.PackDir("halo")));
        Assert.Equal(stats, File.ReadAllText(Path.Combine(sb.Cache, "stats", "halo_weapons.json")));
    }

    [Fact]
    public void Partial_rerun_mixes_new_files_and_kept_ones_and_sounds_json_lists_both()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var r = InProc.Run(sb.AllArgs("--only", "halo"), Fakes(halo: "partial"));
        Assert.Equal(0, r.Exit); // every halo row is ok again (new or kept)
        var m = sb.Manifest();
        var rows = Sources.ForGame("halo-mcc").ToList();
        Assert.Null(m["assets"]![rows[0].Id]!["kept"]);
        Assert.True((bool)m["assets"]![rows[1].Id]!["kept"]!);
        var sounds = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(sb.Layout.AssetsDir("halo"), "sounds.json")))!;
        foreach (var row in rows.Where(x => x.Kind == "sound"))
            Assert.NotNull(sounds[ExtractContext.SoundEvent(row)]);
    }

    [Fact]
    public void No_keep_drops_previous_files_and_stale_stats()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var r = InProc.Run(sb.AllArgs("--only", "halo", "--no-keep"), Fakes(halo: "missing"));
        Assert.Equal(2, r.Exit);
        Assert.Equal("missing", (string?)sb.Manifest()["games"]!["halo"]!["status"]);
        Assert.False(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "meshes/halo/magnum.cthm")));
        Assert.False(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "sounds.json")));
        Assert.False(File.Exists(Path.Combine(sb.Cache, "stats", "halo_weapons.json")));
        Assert.True(File.Exists(Path.Combine(sb.Layout.PackDir("halo"), "pack.mcmeta")));
    }

    [Fact]
    public void Missing_folder_on_a_later_run_keeps_the_previous_pack()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var files = Sandbox.Hashes(sb.Layout.PackDir("gta"));
        var r = InProc.Run(new[] { "--out", sb.Cache, "--only", "gta" });
        Assert.Equal(2, r.Exit);
        var gta = sb.Manifest()["games"]!["gta"]!;
        Assert.Equal("ok", (string?)gta["status"]);
        Assert.True((bool)gta["keptPrevious"]!);
        Assert.Contains("no gta folder given", (string?)gta["error"]);
        Assert.Contains("is kept", (string?)gta["error"]);
        Assert.Equal(files, Sandbox.Hashes(sb.Layout.PackDir("gta")));
    }

    [Fact]
    public void Outputs_that_fail_the_check_are_downgraded_and_removed()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs("--no-keep"), Fakes(halo: "badfile"));
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        var a = m["assets"]!["halo_mesh_assault_rifle"]!;
        Assert.Equal("failed", (string?)a["status"]);
        Assert.Contains("output check failed", (string?)a["detail"]);
        Assert.False(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "meshes/halo/assault_rifle.cthm")));
        Assert.Equal("partial", (string?)m["games"]!["halo"]!["status"]);
    }

    [Fact]
    public void Stats_reported_done_but_never_written_are_failed()
    {
        using var sb = new Sandbox();
        var fakes = Fakes();
        fakes["halo"] = (Func<IGameModule>)(() => new LambdaModule("halo", (dir, ctx, ct) =>
        {
            foreach (var row in ctx.Rows.Where(r => r.Kind != "stats")) SyntheticContent.Write(ctx, row.Id);
            ctx.StatsDone("halo_stats_weapons", "claims done");   // no Stats() call: no file
            throw new InvalidOperationException("boom after stats");
        }));
        var r = InProc.Run(sb.AllArgs(), fakes);
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        Assert.Equal("failed", (string?)m["assets"]!["halo_stats_weapons"]!["status"]);
        Assert.Contains("was not written", (string?)m["assets"]!["halo_stats_weapons"]!["detail"]);
        Assert.Equal("failed", (string?)m["assets"]!["halo_stats_shield"]!["status"]);
    }

    [Fact]
    public void Over_budget_meshes_are_decimated_by_the_context()
    {
        using var sb = new Sandbox();
        var fakes = Fakes();
        fakes["fortnite"] = (Func<IGameModule>)(() => new LambdaModule("fortnite", (dir, ctx, ct) =>
        {
            var row = ctx.Row("fn_mesh_glider");
            ctx.MaterialTexture("fortnite/glider/0", SyntheticContent.Icon(8, 8, 1));
            ctx.MaterialTexture("fortnite/glider/1", SyntheticContent.Icon(8, 8, 2));
            ctx.Mesh(row.Id, Meshes.Sphere(rings: 75, segments: 200, materials: 2, material: "fortnite/glider"), "synthetic sphere");
            foreach (var r in ctx.Rows.Where(r => r.Id != row.Id)) ctx.Missing(r.Id, "not in this test");
        }));
        var r = InProc.Run(sb.AllArgs(), fakes);
        Assert.Equal(1, r.Exit);
        var a = sb.Manifest()["assets"]!["fn_mesh_glider"]!;
        Assert.Equal("ok", (string?)a["status"]);
        Assert.Contains("(from 30000, quadric)", (string?)a["detail"]);
        var mesh = Cthm.Read(Path.Combine(sb.Layout.AssetsDir("fortnite"), "meshes/fortnite/glider.cthm"));
        Assert.InRange(mesh.TriangleCount, 3000, 4000);
        Assert.Equal(2, mesh.Submeshes.Count);
    }

    [Fact]
    public void Unknown_row_ids_from_a_module_are_contained()
    {
        using var sb = new Sandbox();
        var fakes = Fakes();
        fakes["gta"] = (Func<IGameModule>)(() => new LambdaModule("gta", (dir, ctx, ct) =>
        {
            SyntheticContent.Write(ctx, "gta_snd_horn");
            ctx.Missing("not_a_row", "oops"); // throws ArgumentException? no: Missing records any id
            ctx.Row("not_a_row_either");      // throws ArgumentException
        }));
        var r = InProc.Run(sb.AllArgs(), fakes);
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        Assert.Equal("ok", (string?)m["assets"]!["gta_snd_horn"]!["status"]);
        Assert.Null(m["assets"]!["not_a_row"]);
        Assert.Contains("unknown source id", (string?)m["games"]!["gta"]!["error"]);
    }

    [Fact]
    public void Second_extractor_on_the_same_cache_exits_4_without_touching_it()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        var before = sb.ManifestText();
        using (var held = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _))
        {
            Assert.NotNull(held);
            var r = InProc.Run(sb.AllArgs(), Fakes("missing", "missing", "missing"));
            Assert.Equal(4, r.Exit);
            Assert.Contains("another extractor", r.Stderr);
            Assert.Contains("pid=" + Environment.ProcessId, r.Stderr);
        }
        Assert.Equal(before, sb.ManifestText());
    }

    [Fact]
    public async Task Lock_wait_lets_a_second_run_proceed_once_the_first_is_done()
    {
        using var sb = new Sandbox();
        var held = CacheLock.TryAcquire(sb.Cache, TimeSpan.Zero, out _)!;
        var release = Task.Run(async () => { await Task.Delay(700); held.Dispose(); });
        var r = await Task.Run(() => InProc.Run(sb.AllArgs("--lock-wait-sec", "10")));
        await release;
        Assert.Equal(0, r.Exit);
        Assert.InRange(r.Elapsed.TotalMilliseconds, 500, 15_000);
    }

    [Fact]
    public void Global_watchdog_cuts_a_hung_game_writes_the_manifest_and_exits_5()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs("--timeout-sec", "2", "--grace-sec", "1"), Fakes(gta: "hang"));
        Assert.Equal(5, r.Exit);
        Assert.InRange(r.Elapsed.TotalSeconds, 2, 20);
        var m = sb.Manifest();
        Assert.True((bool)m["run"]!["watchdog"]!);
        Assert.Equal(5, (int)m["run"]!["exitCode"]!);
        Assert.Equal("ok", (string?)m["games"]!["halo"]!["status"]);
        Assert.Contains("timed out", (string?)m["games"]!["gta"]!["error"]);
        Assert.True((bool)m["games"]!["gta"]!["interrupted"]!);
        Assert.Contains("not started", (string?)m["games"]!["fortnite"]!["error"]);
        Assert.Equal("partial", (string?)m["status"]);
        Assert.Contains("abandoning its thread", sb.Log());
    }

    [Fact]
    public void Per_game_time_limit_cancels_one_game_and_the_run_continues()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(sb.AllArgs("--game-timeout-sec", "1", "--grace-sec", "5"), Fakes(gta: "coop"));
        Assert.Equal(1, r.Exit);
        var m = sb.Manifest();
        var gta = m["games"]!["gta"]!;
        Assert.Equal("partial", (string?)gta["status"]);
        Assert.True((bool)gta["interrupted"]!);
        Assert.Equal(1, (int)gta["interruptedRuns"]!);
        Assert.Contains("timed out", (string?)gta["error"]);
        Assert.Equal(1, (int)gta["counts"]!["ok"]!);
        Assert.Contains("not reached", (string?)m["assets"]!["gta_tex_wanted_star"]!["detail"]);
        Assert.Equal("ok", (string?)m["games"]!["fortnite"]!["status"]);
        Assert.False((bool)m["run"]!["watchdog"]!);
    }

    [Fact]
    public async Task Hard_watchdog_writes_the_manifest_when_the_supervisor_itself_is_stuck()
    {
        using var sb = new Sandbox();
        using var stuck = new ManualResetEventSlim(false);
        var fired = new ManualResetEventSlim(false);
        int exitCode = -1;
        var fakes = Fakes();
        fakes["gta"] = (Func<IGameModule>)(() => new LambdaModule("gta", (d, c, t) => { },
            fingerprint: _ => { stuck.Wait(); return new[] { "fake/gta.txt" }; })); // the supervisor stats files itself
        var run = Task.Run(() => InProc.Run(sb.AllArgs("--timeout-sec", "1", "--grace-sec", "0"), fakes, s =>
        {
            s.HardWatchdogSlack = TimeSpan.FromSeconds(1);
            s.HardExit = code => { exitCode = code; fired.Set(); };
        }));
        Assert.True(fired.Wait(TimeSpan.FromSeconds(20)), "hard watchdog did not fire");
        Assert.Equal(5, exitCode);
        var m = sb.Manifest();
        Assert.StartsWith("{\"cacheSchema\":1,", sb.ManifestText());
        Assert.True((bool)m["run"]!["watchdog"]!);
        Assert.Equal("ok", (string?)m["games"]!["halo"]!["status"]);
        Assert.Contains("watchdog stopped it", (string?)m["games"]!["gta"]!["error"]);
        stuck.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Leftovers_of_an_interrupted_run_are_cleaned_and_a_half_swapped_pack_restored()
    {
        using var sb = new Sandbox();
        Assert.Equal(0, InProc.Run(sb.AllArgs()).Exit);
        // simulate a crash between "rename old pack aside" and "move new pack in"
        Directory.Move(sb.Layout.PackDir("halo"), Path.Combine(sb.Layout.PacksDir, ".old-halo-99999"));
        Sandbox.Touch(Path.Combine(sb.Layout.StagingRoot, "gta-1-2", "pack", "x.bin"), 10);
        File.WriteAllText(Path.Combine(sb.Cache, "manifest.json.123.abc.tmp"), "{");

        var r = InProc.Run(sb.AllArgs("--only", "gta"));
        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "meshes/halo/magnum.cthm")));
        Assert.Empty(Directory.EnumerateDirectories(sb.Layout.PacksDir, ".old-*"));
        Assert.False(Directory.Exists(sb.Layout.StagingRoot));
        Assert.Empty(Directory.EnumerateFiles(sb.Cache, "*.tmp"));
        Assert.Contains("restoring .old-halo-99999 as packs/halo", sb.Log());
    }

    [Fact]
    public void Cache_inside_a_game_folder_is_refused()
    {
        using var sb = new Sandbox();
        var r = InProc.Run(new[] { "--gta", sb.Gta, "--out", Path.Combine(sb.Gta, "cache") });
        Assert.Equal(3, r.Exit);
        Assert.Contains("inside the gta folder", r.Stderr);
        Assert.False(Directory.Exists(Path.Combine(sb.Gta, "cache")));
    }

    /// <summary>The modules walk up from a subfolder (GTA: update\x64) to the install, so a cache in the install root
    /// is refused although it is not below the folder that was given.</summary>
    [Theory]
    [InlineData("gta", "GTA5.exe", "update/x64")]
    [InlineData("halo", "halo3/maps/riverworld.map", "halo3/maps")]
    [InlineData("fortnite", "FortniteGame/Content/Paks/global.utoc", "FortniteGame/Content/Paks")]
    public void Cache_inside_the_install_above_a_given_subfolder_is_refused(string game, string marker, string sub)
    {
        using var sb = new Sandbox();
        var install = sb.Dir(game);
        Sandbox.Touch(Path.Combine(install, marker), 10);
        Directory.CreateDirectory(Path.Combine(install, sub));
        var cache = Path.Combine(install, "cth-cache");
        var before = Directory.GetFileSystemEntries(install, "*", SearchOption.AllDirectories).Length;

        foreach (var extra in new[] { Array.Empty<string>(), new[] { "--check" }, new[] { "--only", "bogus" } })
        {
            var r = InProc.Run(new[] { "--" + game, Sandbox.Fw(Path.Combine(install, sub)), "--out", Sandbox.Fw(cache) }.Concat(extra).ToArray());
            Assert.Equal(3, r.Exit);
            Assert.False(Directory.Exists(cache), $"{string.Join(' ', extra)} created {cache}");
            Assert.Equal(before, Directory.GetFileSystemEntries(install, "*", SearchOption.AllDirectories).Length);
        }
        Assert.Contains("refusing to write there", InProc.Run(new[] { "--" + game, Path.Combine(install, sub), "--out", cache }).Stderr);
    }

    [Fact]
    public void Cache_next_to_a_game_install_is_allowed()
    {
        using var sb = new Sandbox();
        Sandbox.Touch(Path.Combine(sb.Gta, "GTA5.exe"), 10);
        Directory.CreateDirectory(Path.Combine(sb.Gta, "update", "x64"));
        // a library folder that holds the install is not itself a game folder
        var cache = Path.Combine(Path.GetDirectoryName(sb.Gta)!, "cth-cache");
        var r = InProc.Run(new[] { "--gta", Path.Combine(sb.Gta, "update", "x64"), "--out", cache, "--only", "gta" });
        Assert.NotEqual(3, r.Exit);
        Assert.True(File.Exists(Path.Combine(cache, "manifest.json")));
        Assert.Null(GameDirGuard.Problem(cache, new Dictionary<string, string?> { ["gta"] = sb.Gta }));
        Assert.NotNull(GameDirGuard.Problem(Path.Combine(sb.Gta, "x"), new Dictionary<string, string?> { ["gta"] = Path.Combine(sb.Gta, "update", "x64") }));
    }

    [Fact]
    public void Log_is_rotated_per_run()
    {
        using var sb = new Sandbox();
        InProc.Run(sb.AllArgs());
        InProc.Run(sb.AllArgs("--only", "gta"));
        Assert.Contains("--only gta", sb.Log());
        Assert.DoesNotContain("--only gta", File.ReadAllText(sb.Layout.PreviousLogPath));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sb.Log(), "==== cthextract"));
    }

    [Fact]
    public void Progress_reports_rows_across_games()
    {
        using var sb = new Sandbox();
        var seen = new List<JsonNode>();
        var fakes = Fakes();
        fakes["gta"] = (Func<IGameModule>)(() => new LambdaModule("gta", (dir, ctx, ct) =>
        {
            foreach (var row in ctx.Rows)
            {
                SyntheticContent.Write(ctx, row.Id);
                Thread.Sleep(210); // past the progress throttle
                seen.Add(JsonNode.Parse(File.ReadAllText(Path.Combine(ctx.CacheDir, "progress.json")))!);
            }
        }));
        Assert.Equal(0, InProc.Run(sb.AllArgs(), fakes).Exit);
        Assert.All(seen, p => Assert.Equal("gta", (string?)p["game"]));
        int haloRows = Sources.ForGame("halo-mcc").Count();
        Assert.Contains(seen, p => (int)p["done"]! > haloRows);
        Assert.All(seen, p => Assert.Equal(Sources.All.Count, (int)p["total"]!));
    }
}
