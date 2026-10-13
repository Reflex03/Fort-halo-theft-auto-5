using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;
using CthExtract.Gta;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Tests.Synth;
using CthExtract.Halo;
using CthExtract.Halo.Tests;
using Xunit;

namespace CthExtract.Cli.Tests;

/// <summary>
/// The real Halo and GTA modules on the synthetic installs their own tests build (fake map served through the Halo
/// module's map opener + a synthetic FSB bank; a synthetic GTA folder with random test keys), run through the whole
/// extractor pipeline: staging, output checks, carry-over, swap, manifest. Proves real module output passes the
/// extractor's checks (nothing is downgraded) and lands where the contract says.
/// </summary>
public class ModuleIntegrationTests
{
    /// <summary>MCC folder like CthExtract.Halo.Tests' FakeInstall: placeholder .map files + a real synthetic FSB bank.</summary>
    private static Func<IGameModule> HaloInstall(string mccRoot)
    {
        var halo = new SyntheticHalo();
        var audio = SynthAudio.Encode(1, 44100, 0.0f, 0.25);
        var (items, info) = halo.Bank(audio);
        foreach (var m in new[] { "riverworld", "shared", "guardian" })
            Sandbox.Touch(Path.Combine(mccRoot, "halo3", "maps", m + ".map"), 16);
        File.WriteAllBytes(Path.Combine(mccRoot, "halo3", "fmod", "pc", "sfx.fsb").Also(Directory.CreateDirectory), SynthAudio.BuildFsb5(items));
        File.WriteAllBytes(Path.Combine(mccRoot, "halo3", "fmod", "pc", "sfx.fsb.info"), FsbInfo.Write(info));
        return () => new HaloModule(path => Path.GetFileName(path) == "riverworld.map"
            ? halo.Map : throw new InvalidDataException("not a valid map file (synthetic)"));
    }

    [Fact]
    public void Real_halo_and_gta_modules_through_the_extractor()
    {
        using var sb = new Sandbox();
        using var gta = new SynthGame();
        var mcc = Path.Combine(sb.Root, "MCC");
        var fakes = new Dictionary<string, object>
        {
            ["halo"] = HaloInstall(mcc),
            ["gta"] = (Func<IGameModule>)(() => new GtaModule(gta.ModuleOptions(sb.Layout.KeysDir))),
            ["fortnite"] = "missing",
        };
        var args = new[] { "--halo", mcc, "--gta", gta.Root, "--fortnite", sb.Fortnite, "--out", sb.Cache };
        var r = InProc.Run(args, fakes);
        var log = sb.Log();
        Assert.True(r.Exit == 1, $"exit {r.Exit}\n{log}");
        Assert.DoesNotContain("output check failed", log);
        Assert.DoesNotContain("material texture(s) missing", log);

        var m = sb.Manifest();
        foreach (var row in Sources.ForGame("halo-mcc"))
            Assert.True((string?)m["assets"]![row.Id]!["status"] == "ok", $"{row.Id}: {m["assets"]![row.Id]}");
        Assert.Equal("ok", (string?)m["games"]!["halo"]!["status"]);
        foreach (var id in new[] { "gta_mesh_infernus", "gta_mesh_police", "gta_mesh_sultan", "gta_snd_siren", "gta_snd_horn", "gta_tex_wanted_star" })
            Assert.True((string?)m["assets"]![id]!["status"] == "ok", $"{id}: {m["assets"]![id]}");
        Assert.Equal("missing", (string?)m["assets"]!["gta_mesh_blista"]!["status"]);
        Assert.Equal("partial", (string?)m["games"]!["gta"]!["status"]);

        // contract locations in the swapped packs and cache/stats
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("halo"), "meshes/halo/assault_rifle.cthm")));
        Assert.True(File.Exists(Path.Combine(sb.Layout.AssetsDir("gta"), "meshes/gta/infernus.cthm")));
        Assert.Equal(4, Cthm.Read(Path.Combine(sb.Layout.AssetsDir("gta"), "meshes/gta/infernus.cthm")).Nodes.Count);
        var weapons = JsonNode.Parse(File.ReadAllText(Path.Combine(sb.Cache, "stats", "halo_weapons.json")))!.AsObject();
        Assert.Equal(10, weapons.Count);
        Assert.True(File.Exists(Path.Combine(sb.Cache, "stats", "halo_shield.json")));
        var gtaSounds = JsonNode.Parse(File.ReadAllText(Path.Combine(sb.Layout.AssetsDir("gta"), "sounds.json")))!.AsObject();
        Assert.Equal(new[] { "gta.horn", "gta.siren" }, gtaSounds.Select(kv => kv.Key).Order());
        // the GTA module's AES key cache lives in cache/keys, outside the swapped pack
        Assert.True(Directory.Exists(sb.Layout.KeysDir) && Directory.EnumerateFiles(sb.Layout.KeysDir).Any());
        Assert.Empty(Directory.EnumerateFiles(sb.Layout.PacksDir, "*key*", SearchOption.AllDirectories));

        // fingerprints come from the modules' own FingerprintFiles
        var fp = (JsonArray)m["games"]!["gta"]!["fingerprint"]!;
        Assert.Contains(fp, e => (string?)e!["path"] is { } p && p.EndsWith("GTA5.exe"));

        // a later run where GTA cannot be read keeps every good GTA file
        var before = Sandbox.Hashes(sb.Layout.PackDir("gta"));
        fakes["gta"] = (Func<IGameModule>)(() => new GtaModule(new GtaOptions { Hashes = new SynthKeys(99).Hashes, Magic = new BytesMagicSource(null), KeysDir = Path.Combine(sb.Root, "otherkeys") }));
        var r2 = InProc.Run(args.Concat(new[] { "--only", "gta" }).ToArray(), fakes);
        Assert.Equal(2, r2.Exit);
        var m2 = sb.Manifest();
        Assert.True((bool)m2["assets"]!["gta_mesh_infernus"]!["kept"]!);
        Assert.Equal("partial", (string?)m2["games"]!["gta"]!["status"]);
        Assert.Equal(before, Sandbox.Hashes(sb.Layout.PackDir("gta")));
    }
}

internal static class PathExt
{
    /// <summary>Create the parent folder of a file path and return the path.</summary>
    public static string Also(this string path, Func<string, DirectoryInfo> mkdir) { mkdir(Path.GetDirectoryName(path)!); return path; }
}
