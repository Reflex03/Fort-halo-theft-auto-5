using System.Text.Json.Nodes;
using CthExtract.Core.Generated;
using CthExtract.E2E;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>
/// The built extractor as a separate process with its REAL modules and default options (no CTHEXTRACT_TEST_MODULES,
/// no injected keys, openers or HTTP), on fake game folders made from the modules' own synthetic data
/// (tests/CthExtract.E2E/FakeInstalls.cs), then the cache is checked against docs/CONTRACTS.md (CacheContract).
/// tests/e2e.sh runs the same through `dotnet run` and keeps the result.
/// </summary>
[Collection("processes")]
public class RealCliEndToEndTests
{
    [Fact]
    public void Real_modules_on_synthetic_installs_fill_a_cache_that_follows_the_contract()
    {
        using var sb = new Sandbox();
        var made = FakeInstalls.Make(Path.Combine(sb.Root, "e2e"));
        if (made.FortniteKeysSeed is { } seed)
            FakeInstalls.CopyTree(seed, Path.Combine(sb.Cache, "keys", "fortnite"));
        var args = new[] { "--halo", Sandbox.Fw(made.Halo), "--gta", Sandbox.Fw(made.Gta), "--fortnite", Sandbox.Fw(made.Fortnite), "--out", Sandbox.Fw(sb.Cache), "--offline" };
        var r = Exe.Run(args, timeoutMs: 600_000);
        var log = sb.Log();
        Assert.True(r.Exit == 1, $"exit {r.Exit}\n{r.Stderr}\n{log}");
        Assert.DoesNotContain("TEST MODE", log);

        var c = CacheContract.Verify(sb.Cache, CthExtract.E2E.Program.DefaultSheets());
        Assert.True(c.Problems.Count == 0, string.Join("\n", c.Problems));

        var m = sb.Manifest();
        string St(string id) => (string?)m["assets"]![id]!["status"] ?? "";
        // Halo: the junk maps are refused by Reclaimer (Failed, not Missing); every sound comes from the synthetic bank
        foreach (var row in Sources.ForGame("halo-mcc"))
            Assert.Equal(row.Kind == "sound" ? "ok" : "failed", St(row.Id));
        Assert.Contains("not a valid map file", (string?)m["assets"]!["halo_mesh_magnum"]!["detail"]);
        // GTA: OPEN synthetic archives without any key; blista is not in the synthetic game
        foreach (var id in new[] { "gta_mesh_infernus", "gta_mesh_sultan", "gta_mesh_police", "gta_snd_siren", "gta_snd_horn", "gta_tex_wanted_star" })
            Assert.Equal("ok", St(id));
        Assert.Equal("missing", St("gta_mesh_blista"));
        Assert.Contains("AES key not found in GTA5.exe", log);
        // Fortnite: fixture containers (with CTH_REFS) or an empty Paks folder: never a Fortnite asset, never a crash
        foreach (var row in Sources.ForGame("fortnite")) Assert.Equal("missing", St(row.Id));
        Assert.Equal(0, (int)m["games"]!["fortnite"]!["counts"]!["failed"]!);
        if (made.FortniteKeysSeed is not null)
            Assert.Contains("type mapping", (string?)m["assets"]!["fn_mesh_battle_bus"]!["detail"]);
        // every game gets a pack (pack.mcmeta always present); only halo and gta have content
        foreach (var g in new[] { "halo", "gta", "fortnite" }) Assert.True(File.Exists(Path.Combine(sb.Layout.PackDir(g), "pack.mcmeta")), g);
        Assert.Equal(new[] { "gta", "halo" }, new[] { "halo", "gta", "fortnite" }
            .Where(g => Directory.Exists(sb.Layout.AssetsDir(g)) && Directory.EnumerateFiles(sb.Layout.AssetsDir(g), "*", SearchOption.AllDirectories).Any()).Order().ToArray());

        // --check right after: nothing changed, nothing to re-run
        var chk = Exe.Run(args.Where(a => a != "--offline").Append("--check"));
        Assert.True(chk.Exit == 0, chk.Stdout + chk.Stderr);
        Assert.Equal("fresh", (string?)JsonNode.Parse(File.ReadAllText(sb.Layout.CheckPath))!["advice"]);
    }
}
