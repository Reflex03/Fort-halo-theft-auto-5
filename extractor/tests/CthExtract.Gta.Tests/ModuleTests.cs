using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Tests.Synth;
using Xunit;

namespace CthExtract.Gta.Tests;

public class GameFolderTests
{
    [Fact]
    public void Finds_the_install_from_root_subfolder_exe_or_itself_and_prefers_legacy()
    {
        using var t = new TempDir();
        var legacy = Path.Combine(t.Path, "SteamLibrary", "steamapps", "common", "Grand Theft Auto V");
        var enhanced = Path.Combine(t.Path, "SteamLibrary", "steamapps", "common", "Grand Theft Auto V Enhanced");
        Directory.CreateDirectory(Path.Combine(legacy, "update", "x64"));
        Directory.CreateDirectory(enhanced);
        File.WriteAllBytes(Path.Combine(legacy, "GTA5.exe"), new byte[4]);
        File.WriteAllBytes(Path.Combine(enhanced, "GTA5_Enhanced.exe"), new byte[4]);

        Assert.Equal(legacy, GameFolder.Find(legacy, out _)!.Dir);
        Assert.Equal(legacy, GameFolder.Find(Path.Combine(legacy, "update", "x64"), out _)!.Dir);
        Assert.Equal(legacy, GameFolder.Find(Path.Combine(legacy, "GTA5.exe"), out _)!.Dir);
        var fromRoot = GameFolder.Find(Path.Combine(t.Path, "SteamLibrary"), out _)!;
        Assert.Equal(legacy, fromRoot.Dir);
        Assert.False(fromRoot.Enhanced);
        var e = GameFolder.Find(enhanced, out _)!;
        Assert.True(e.Enhanced);
        Assert.Null(GameFolder.Find(Path.Combine(t.Path, "nope"), out var why));
        Assert.Contains("does not exist", why);
        Assert.Null(GameFolder.Find("", out _));
    }

    [Fact]
    public void Exe_name_match_is_case_insensitive()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "gta5.EXE"), new byte[4]);
        Assert.NotNull(GameFolder.Find(t.Path, out _));
    }
}

public class ModuleTests
{
    private static (Dictionary<string, AssetResult> results, string log) Run(SynthGame g, TempCache c, GtaOptions? opt = null, string? dir = null)
    {
        var m = new GtaModule(opt ?? g.ModuleOptions(c.KeysDir));
        m.Extract(dir ?? g.Root, c.Ctx, TestContext.Current.CancellationToken);
        c.Ctx.Finish();
        return (c.Ctx.Results.ToDictionary(kv => kv.Key, kv => kv.Value), File.ReadAllText(c.LogPath));
    }

    [Fact]
    public void Full_synthetic_install_extracts_every_available_row()
    {
        using var g = new SynthGame();
        using var c = new TempCache();
        var (res, log) = Run(g, c);

        Assert.Equal(7, res.Count);
        foreach (var id in new[] { "gta_mesh_infernus", "gta_mesh_police", "gta_mesh_sultan", "gta_snd_siren", "gta_snd_horn", "gta_tex_wanted_star" })
            Assert.True(res[id].Status == AssetStatus.Ok, $"{id}: {res[id].Status} {res[id].Detail}");
        Assert.Equal(AssetStatus.Missing, res["gta_mesh_blista"].Status);
        Assert.Contains("blista.yft not found", res["gta_mesh_blista"].Detail);

        // sultan came from the patch DLC fallback
        Assert.Contains("update/x64/dlcpacks/patchday9ng/dlc.rpf", res["gta_mesh_sultan"].Detail);
        Assert.Contains("x64e.rpf/levels/gta5/vehicles.rpf/infernus.yft", res["gta_mesh_infernus"].Detail);

        // meshes and their textures
        var mesh = Cthm.Read(c.Asset("meshes/gta/infernus.cthm"));
        Assert.Equal(new[] { "wheel_lf", "wheel_rf", "wheel_lb", "wheel_rb" }, mesh.Nodes.Select(n => n.Name));
        foreach (var s in mesh.Submeshes) Assert.True(File.Exists(c.Asset("textures/" + s.Material + ".png")), s.Material);
        Assert.Contains(mesh.Submeshes, s => s.Material == "gta/infernus/synth_interior"); // found through vehicles.meta txdRelationships
        Assert.Contains(mesh.Submeshes, s => s.Material == "gta/infernus/vehicle_genericglass_glass"); // vehshare.ytd
        Assert.DoesNotContain("not found in any dictionary", log);

        // sounds
        var sounds = JsonNode.Parse(File.ReadAllText(c.Asset("sounds.json")))!.AsObject();
        Assert.True(sounds.ContainsKey("gta.siren"));
        Assert.True(sounds.ContainsKey("gta.horn"));
        Assert.Equal("crafttheft:gta/siren", (string)sounds["gta.siren"]!["sounds"]![0]!["name"]!);
        using (var v = new NVorbis.VorbisReader(c.Asset("sounds/gta/siren.ogg")))
        {
            Assert.Equal(1, v.Channels);
            Assert.InRange(v.TotalSamples / (double)v.SampleRate, 1.3, 1.7);
        }
        Assert.Contains("sheet id 0x0D329446 (siren_pa20a_wail)", res["gta_snd_siren"].Detail);
        Assert.Contains("horn_std_1", res["gta_snd_horn"].Detail);

        // star
        Assert.True(File.Exists(c.Asset("textures/gui/gta/wanted_star.png")));
        Assert.Contains("update/update.rpf/x64/textures/script_txds.rpf/commonmenu.ytd/shop_new_star", res["gta_tex_wanted_star"].Detail);

        // logging: census, keys, AWC census
        Assert.Contains("census summary", log);
        Assert.Contains("AES key found in GTA5.exe", log);
        Assert.Contains("NG keys verified", log);
        Assert.Contains("AWC census", log);
        Assert.Contains("siren_pa20a_wail", log);

        // the AES key is cached, NG material is not written anywhere
        Assert.True(File.Exists(Path.Combine(c.KeysDir, AesKeyFinder.CacheFileName)));
        Assert.Single(Directory.GetFiles(c.KeysDir));
        var allCacheFiles = Directory.GetFiles(c.Dir, "*", SearchOption.AllDirectories);
        var tableSample = SynthKeys.KeyBytes(g.Keys.Ng.Tables[3][7]).AsSpan(0, 64).ToArray();
        foreach (var f in allCacheFiles)
            Assert.True(File.ReadAllBytes(f).AsSpan().IndexOf(tableSample) < 0, $"NG table bytes found in {f}");
    }

    [Fact]
    public void Second_run_uses_the_cached_aes_key()
    {
        using var g = new SynthGame(new SynthGame.Options { IncludeAwc = false, IncludeStar = false });
        using var c1 = new TempCache();
        var keys = Path.Combine(c1.Dir, "shared-keys");
        Run(g, c1, g.ModuleOptions(keys));
        using var c2 = new TempCache();
        var (res, log) = Run(g, c2, g.ModuleOptions(keys));
        Assert.Contains("AES key from cache", log);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_infernus"].Status);
    }

    [Fact]
    public void No_game_folder_reports_every_row_missing()
    {
        using var c = new TempCache();
        var m = new GtaModule(new GtaOptions { Magic = new BytesMagicSource(null), KeysDir = c.KeysDir });
        m.Extract(Path.Combine(c.Dir, "does-not-exist"), c.Ctx, TestContext.Current.CancellationToken);
        c.Ctx.Finish();
        Assert.Equal(7, c.Ctx.Results.Count);
        Assert.All(c.Ctx.Results.Values, r => Assert.Equal(AssetStatus.Missing, r.Status));
        Assert.All(c.Ctx.Results.Values, r => Assert.Contains("GTA V folder not found", r.Detail));
        Assert.Empty(new GtaModule().FingerprintFiles(Path.Combine(c.Dir, "does-not-exist")));
    }

    [Fact]
    public void Folder_without_archives_reports_missing_with_reasons()
    {
        using var t = new TempDir();
        File.WriteAllBytes(Path.Combine(t.Path, "GTA5.exe"), FakeExe.Build(3, 1 << 14, 1 << 10).Bytes);
        using var c = new TempCache();
        var m = new GtaModule(new GtaOptions { Hashes = new SynthKeys(1).Hashes, Magic = new BytesMagicSource(null), KeysDir = c.KeysDir });
        m.Extract(t.Path, c.Ctx, TestContext.Current.CancellationToken);
        c.Ctx.Finish();
        Assert.All(c.Ctx.Results.Values, r => Assert.Equal(AssetStatus.Missing, r.Status));
        Assert.Contains("RESIDENT.rpf: not found", c.Ctx.Results["gta_snd_siren"].Detail);
        Assert.Contains("infernus.yft not found", c.Ctx.Results["gta_mesh_infernus"].Detail);
    }

    [Fact]
    public void Exe_without_the_aes_key_reports_encrypted_content_missing()
    {
        using var g = new SynthGame(new SynthGame.Options { PlantAesKey = false, IncludeAwc = false });
        using var c = new TempCache();
        var (res, log) = Run(g, c);
        Assert.Contains("AES key not found in GTA5.exe", log);
        Assert.Equal(AssetStatus.Missing, res["gta_mesh_infernus"].Status);
        Assert.Contains("AES key not found", res["gta_mesh_infernus"].Detail);
        Assert.Equal(AssetStatus.Missing, res["gta_tex_wanted_star"].Status);
        Assert.False(File.Exists(Path.Combine(c.KeysDir, AesKeyFinder.CacheFileName)));
    }

    [Fact]
    public void Failed_ng_fetch_marks_ng_content_missing_but_keeps_aes_content()
    {
        using var g = new SynthGame(new SynthGame.Options { BaseEnc = RpfEnc.Ng });
        using var c = new TempCache();
        var opt = new GtaOptions { Hashes = g.Keys.Hashes, Magic = new BytesMagicSource(null, "offline"), KeysDir = c.KeysDir };
        var (res, _) = Run(g, c, opt);
        Assert.Equal(AssetStatus.Missing, res["gta_mesh_infernus"].Status);
        Assert.Contains("NG", res["gta_mesh_infernus"].Detail);
        Assert.Equal(AssetStatus.Missing, res["gta_snd_siren"].Status);
        Assert.Contains("NG key blob unavailable from offline", res["gta_snd_siren"].Detail);
        // the star sits in an NG nested archive inside the AES update.rpf: missing, with the reason
        Assert.Equal(AssetStatus.Missing, res["gta_tex_wanted_star"].Status);
    }

    [Fact]
    public void Tampered_or_wrong_magic_is_refused()
    {
        using var g = new SynthGame(new SynthGame.Options { IncludeAwc = false });
        using var c = new TempCache();
        var bad = (byte[])g.Magic.Clone();
        bad[100] ^= 1;
        // pinned hash still the good one -> refused before decoding
        var (res, _) = Run(g, c, g.ModuleOptions(c.KeysDir, bad, sha: Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(g.Magic))));
        Assert.Equal(AssetStatus.Missing, res["gta_mesh_infernus"].Status);
        Assert.Contains("refusing it", res["gta_mesh_infernus"].Detail);
        using var c2 = new TempCache();
        // hash check disabled -> the per-item SHA1 verification still refuses it
        var (res2, _) = Run(g, c2, g.ModuleOptions(c2.KeysDir, bad, sha: null));
        Assert.Equal(AssetStatus.Missing, res2["gta_mesh_infernus"].Status);
        Assert.Contains("did not verify", res2["gta_mesh_infernus"].Detail);
    }

    [Fact]
    public void Aes_content_still_extracts_when_the_ng_fetch_fails()
    {
        using var g = new SynthGame(new SynthGame.Options { BaseEnc = RpfEnc.Aes, AudioEnc = RpfEnc.Aes, TxdEnc = RpfEnc.Aes });
        using var c = new TempCache();
        var opt = new GtaOptions { Hashes = g.Keys.Hashes, Magic = new ThrowingMagic(), KeysDir = c.KeysDir };
        var (res, _) = Run(g, c, opt);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_infernus"].Status);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_police"].Status);
        Assert.Equal(AssetStatus.Ok, res["gta_tex_wanted_star"].Status);
        // the AES archive's AWC: chunks not encrypted, so no AWC key needed
        Assert.Equal(AssetStatus.Ok, res["gta_snd_siren"].Status);
        // sultan only exists in the NG patch DLC
        Assert.Equal(AssetStatus.Missing, res["gta_mesh_sultan"].Status);
        Assert.Contains("network disabled in test", res["gta_mesh_sultan"].Detail);
    }

    [Fact]
    public void No_ng_archive_means_no_fetch_at_all()
    {
        using var g = new SynthGame(new SynthGame.Options { BaseEnc = RpfEnc.Aes, AudioEnc = RpfEnc.Open, TxdEnc = RpfEnc.Open, DlcEnc = RpfEnc.Aes });
        using var c = new TempCache();
        var magic = new ThrowingMagic();
        var (res, log) = Run(g, c, new GtaOptions { Hashes = g.Keys.Hashes, Magic = magic, KeysDir = c.KeysDir });
        Assert.Equal(0, magic.Calls);
        Assert.Contains("not fetching NG keys", log);
        Assert.All(res.Where(kv => kv.Key != "gta_mesh_blista"), kv => Assert.Equal(AssetStatus.Ok, kv.Value.Status));
    }

    [Fact]
    public void Gen9_fragment_is_reported_failed_with_the_reason()
    {
        using var g = new SynthGame(new SynthGame.Options { PoliceYftVersion = 171, IncludeAwc = false, IncludeStar = false });
        using var c = new TempCache();
        var (res, _) = Run(g, c);
        Assert.Equal(AssetStatus.Failed, res["gta_mesh_police"].Status);
        Assert.Contains("gen9", res["gta_mesh_police"].Detail);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_infernus"].Status);
    }

    [Fact]
    public void Missing_vehicles_meta_still_finds_model_and_vehshare_textures()
    {
        using var g = new SynthGame(new SynthGame.Options { IncludeMeta = false, IncludeAwc = false, IncludeStar = false });
        using var c = new TempCache();
        var (res, log) = Run(g, c);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_infernus"].Status);
        Assert.Contains("vehicles.meta sources: none found", log);
        Assert.Contains("synth_interior' not found", log); // the parent dictionary is only reachable through the meta
    }

    [Fact]
    public void Encrypted_awc_chunks_use_the_awc_key_from_the_blob()
    {
        using var g = new SynthGame(new SynthGame.Options { EncryptAwcChunks = true, IncludeStar = false });
        using var c = new TempCache();
        var (res, _) = Run(g, c);
        Assert.Equal(AssetStatus.Ok, res["gta_snd_siren"].Status);
        Assert.Equal(AssetStatus.Ok, res["gta_snd_horn"].Status);
    }

    [Fact]
    public void Damaged_awc_fails_both_sound_rows_and_nothing_else()
    {
        using var g = new SynthGame(new SynthGame.Options { CorruptAwc = true, IncludeStar = false });
        using var c = new TempCache();
        var (res, _) = Run(g, c);
        Assert.Equal(AssetStatus.Failed, res["gta_snd_siren"].Status);
        Assert.Equal(AssetStatus.Failed, res["gta_snd_horn"].Status);
        Assert.Contains("implausible AWC stream count", res["gta_snd_horn"].Detail);
        Assert.Equal(AssetStatus.Ok, res["gta_mesh_infernus"].Status);
    }

    [Fact]
    public void Fingerprint_lists_existing_files_relative_to_the_given_folder()
    {
        using var g = new SynthGame();
        var fp = new GtaModule().FingerprintFiles(g.Root).ToList();
        Assert.Contains("Library/Grand Theft Auto V/GTA5.exe", fp);
        Assert.Contains("Library/Grand Theft Auto V/x64e.rpf", fp);
        Assert.Contains("Library/Grand Theft Auto V/x64/audio/sfx/RESIDENT.rpf", fp);
    }

    private sealed class ThrowingMagic : IMagicSource
    {
        public int Calls;
        public string Describe => "throwing";
        public byte[]? Fetch(Log log, CancellationToken ct, out string? error) { Calls++; error = "network disabled in test"; return null; }
    }
}
