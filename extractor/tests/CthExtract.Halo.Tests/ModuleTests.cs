using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;
using Xunit;

namespace CthExtract.Halo.Tests;

/// <summary>The whole module against synthetic installs: nothing there, unreadable maps, and a complete fake install.</summary>
public class ModuleTests
{
    private sealed class Run : IDisposable
    {
        public TempDir Dir = new();
        // set CTH_TEST_LOG_DIR to keep each run's extract log for inspection
        public Log Log = new(Environment.GetEnvironmentVariable("CTH_TEST_LOG_DIR") is { Length: > 0 } d
            ? System.IO.Path.Combine(d, $"halo-{DateTime.UtcNow:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log") : null);
        public ExtractContext Ctx;
        public string Cache => System.IO.Path.Combine(Dir.Path, "cache");
        public string Pack => System.IO.Path.Combine(Cache, "packs", "halo");
        public Run()
        {
            Ctx = new ExtractContext("halo-mcc", "halo", System.IO.Path.Combine(Cache, "staging", "halo"),
                System.IO.Path.Combine(Cache, "stats"), System.IO.Path.Combine(Cache, "work"), Log);
        }
        public string Staged(string rel) => System.IO.Path.Combine(Ctx.AssetsDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Dispose() { Log.Dispose(); Dir.Dispose(); }
    }

    private static IReadOnlyList<SourceRow> HaloRows => Sources.ForGame("halo-mcc").ToList();

    [Fact]
    public void NoInstallMarksEveryRowMissing()
    {
        using var r = new Run();
        new HaloModule().Extract(r.Dir.Sub("not-mcc"), r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;
        Assert.Equal(HaloRows.Count, res.Count);
        Assert.All(HaloRows, row => Assert.Equal(AssetStatus.Missing, res[row.Id].Status));
        Assert.Contains("no Halo 3 maps", res["halo_mesh_assault_rifle"].Detail);
    }

    [Fact]
    public void UnreadableMapsFailAndNoFmodIsMissingWithReasons()
    {
        using var r = new Run();
        r.Dir.File(@"mcc\halo3\maps\riverworld.map", new byte[20000]); // not a cache file: real Reclaimer rejects it
        r.Dir.File(@"mcc\halo3\maps\shared.map", new byte[100]);
        new HaloModule().Extract(System.IO.Path.Combine(r.Dir.Path, "mcc"), r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;
        Assert.All(HaloRows, row => Assert.NotEqual(AssetStatus.Ok, res[row.Id].Status));
        Assert.All(HaloRows, row => Assert.NotEqual(AssetStatus.Unimplemented, res[row.Id].Status));
        // the maps are there but unreadable (e.g. an MCC update changed the format): Failed, so it is not mistaken for
        // "Halo not installed"
        Assert.Equal(AssetStatus.Failed, res["halo_mesh_battle_rifle"].Status);
        Assert.Contains("riverworld (unreadable", res["halo_mesh_battle_rifle"].Detail);
        Assert.Contains("not a valid map file", res["halo_mesh_battle_rifle"].Detail);
        Assert.Equal(AssetStatus.Missing, res["halo_snd_magnum_fire"].Status);
        Assert.Contains("no FMOD banks", res["halo_snd_magnum_fire"].Detail);
        Assert.Equal(AssetStatus.Failed, res["halo_stats_weapons"].Status);
        Assert.Equal(AssetStatus.Failed, res["halo_stats_shield"].Status);
        Assert.False(File.Exists(System.IO.Path.Combine(r.Cache, "stats", "halo_weapons.json")));
    }

    /// <summary>MCC folder with fake maps (served by FakeMap through the module's map opener) and a real synthetic FSB bank.</summary>
    private static (string Root, SyntheticHalo Halo, SynthAudio.Encoded Audio) FakeInstall(TempDir dir, Action<SyntheticHalo>? tweak = null, bool dropArHashes = false)
    {
        var halo = new SyntheticHalo();
        tweak?.Invoke(halo);
        var audio = SynthAudio.Encode(1, 44100, 0.0f, 0.25);
        var (items, info) = halo.Bank(audio);
        if (dropArHashes)
            info = info.Select(x => x.Item1 is 0xA0000001 or 0xA0000002 ? (0xFFFF0000u + x.Item1 % 16, x.Item2, x.Item3, x.Item4, x.Item5) : x).ToList();
        dir.File(@"mcc\halo3\maps\riverworld.map", new byte[16]);
        dir.File(@"mcc\halo3\maps\shared.map", new byte[16]);
        dir.File(@"mcc\halo3\maps\guardian.map", new byte[16]);
        dir.File(@"mcc\halo3\fmod\pc\sfx.fsb", SynthAudio.BuildFsb5(items));
        dir.File(@"mcc\halo3\fmod\pc\sfx.fsb.info", FsbInfo.Write(info));
        return (System.IO.Path.Combine(dir.Path, "mcc"), halo, audio);
    }

    private static Func<string, IHaloMap> Opener(SyntheticHalo halo) => path =>
        System.IO.Path.GetFileName(path) == "riverworld.map" ? halo.Map : throw new InvalidDataException("not a valid map file (synthetic)");

    [Fact]
    public void CompleteSyntheticInstallFillsEveryRow()
    {
        using var r = new Run();
        var (root, halo, audio) = FakeInstall(r.Dir);
        new HaloModule(Opener(halo)).Extract(root, r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;
        foreach (var row in HaloRows)
            Assert.True(res[row.Id].Status == AssetStatus.Ok, $"{row.Id}: {res[row.Id].Status} {res[row.Id].Detail}");

        // meshes: .cthm written at the sources out path, textures for each material, barrel +Z, grip at the origin
        var ar = Cthm.Read(r.Staged("meshes/halo/assault_rifle.cthm"));
        Assert.Equal("halo/assault_rifle/0", Assert.Single(ar.Submeshes).Material);
        Assert.True(File.Exists(r.Staged("textures/halo/assault_rifle/0.png")));
        var zs = Enumerable.Range(0, ar.Submeshes[0].VertexCount).Select(i => ar.Submeshes[0].Vertices[i * 8 + 2]).ToList();
        Assert.True(zs.Max() > 0.5f && zs.Min() < 0, "grip (origin) should sit between butt and muzzle");
        Assert.Contains("marker 'right_hand'", res["halo_mesh_assault_rifle"].Detail);
        Assert.Contains("bounding-box centre", res["halo_mesh_frag_grenade"].Detail);

        // texture alpha forced opaque (synthetic diffuse alpha is 0 = a specular mask)
        var png = File.ReadAllBytes(r.Staged("textures/halo/assault_rifle/0.png"));
        Assert.Equal(0x89, png[0]);

        // sounds: remuxed Vorbis that decodes, chosen through the tag chain
        var ogg = File.ReadAllBytes(r.Staged("sounds/halo/assault_rifle_fire.ogg"));
        var pcm = SynthAudio.Decode(ogg, out var ch, out var rate);
        Assert.Equal(1, ch); Assert.Equal(44100, rate);
        Assert.InRange(pcm.Length, audio.Frames - 2048, audio.Frames + 2048);
        Assert.Contains("ar_fire", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.Contains("hash 0xa0000001", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.Contains("filename agrees", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.Contains("throwing effect", res["halo_snd_grenade_throw"].Detail);
        Assert.Contains("shield recharging effect", res["halo_snd_shield_recharge"].Detail);
        Assert.Contains("shield depleted effect", res["halo_snd_shield_depleted"].Detail);
        var sounds = JsonNode.Parse(File.ReadAllText(r.Staged("sounds.json")))!.AsObject();
        Assert.True(sounds.ContainsKey("halo.assault_rifle_fire"));
        Assert.True(sounds.ContainsKey("halo.shield_recharge"));

        // stats files in the cache
        var weapons = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(r.Cache, "stats", "halo_weapons.json")))!.AsObject();
        Assert.Equal(10, weapons.Count);
        Assert.Equal(7.5, weapons["assault_rifle"]!["damage"]!.GetValue<double>());
        Assert.Equal(32, weapons["assault_rifle"]!["clip"]!.GetValue<int>());
        Assert.Equal(3, weapons["battle_rifle"]!["burst"]!.GetValue<int>());
        Assert.Equal(15, weapons["shotgun"]!["pellets"]!.GetValue<int>());
        Assert.True(weapons["needler"]!["homing"]!.GetValue<bool>());
        Assert.Equal(125, weapons["frag_grenade"]!["damage"]!.GetValue<double>());
        var shield = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(r.Cache, "stats", "halo_shield.json")))!["shield"]!;
        Assert.Equal(100, shield["rechargeDelayTicks"]!.GetValue<int>());
        Assert.Equal(100, shield["max"]!.GetValue<double>());
    }

    [Fact]
    public void FallbacksAndPerRowFailuresStayIsolated()
    {
        using var r = new Run();
        var (root, halo, _) = FakeInstall(r.Dir, h =>
        {
            h.Map.SetScene(h.Modes["magnum"], new Reclaimer.Geometry.Scene()); // geometry without triangles
        }, dropArHashes: true);
        // the needler is not in any map
        var renamed = halo.Map.Tags.Where(t => t.Name.Contains("needler")).ToList();
        var opener = Opener(halo);
        var filtered = new FilteredMap(halo.Map, t => !t.Name.Contains("needler"));
        new HaloModule(p => System.IO.Path.GetFileName(p) == "riverworld.map" ? filtered : opener(p)).Extract(root, r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;

        // AR hashes are not in the .info any more: the filename pattern from the sheet finds the sample instead
        Assert.Equal(AssetStatus.Ok, res["halo_snd_assault_rifle_fire"].Status);
        Assert.Contains("filename match", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.Contains("ar_fire", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.DoesNotContain("fire_lod", res["halo_snd_assault_rifle_fire"].Detail);

        Assert.Equal(AssetStatus.Failed, res["halo_mesh_magnum"].Status);
        Assert.Contains("no triangles", res["halo_mesh_magnum"].Detail);

        Assert.Equal(AssetStatus.Missing, res["halo_mesh_needler"].Status);
        Assert.Contains("not found", res["halo_mesh_needler"].Detail);
        Assert.Contains("guardian (unreadable", res["halo_mesh_needler"].Detail);
        // the needler fire sound still comes from the bank by filename
        Assert.Equal(AssetStatus.Ok, res["halo_snd_needler_fire"].Status);

        // everything else is unaffected
        Assert.Equal(AssetStatus.Ok, res["halo_mesh_assault_rifle"].Status);
        Assert.Equal(AssetStatus.Ok, res["halo_stats_weapons"].Status);
        Assert.Contains("needler: tag not found", res["halo_stats_weapons"].Detail);
        Assert.All(renamed, t => Assert.DoesNotContain(t, filtered.Tags));
    }

    [Fact]
    public void BrokenTextureFallsBackToASwatchAndBrokenBankFailsOnlySounds()
    {
        using var r = new Run();
        var (root, halo, _) = FakeInstall(r.Dir, h =>
        {
            var (p, n, uv, idx) = SceneFactory.Box(-0.05f, 0.25f, 0.02f, 0.04f);
            var mat = SceneFactory.MakeMaterial(77, "broken", Reclaimer.Geometry.AlphaMode.Opaque, new SceneFactory.Broken());
            h.Map.SetScene(h.Modes["shotgun"], SceneFactory.Wrap(SceneFactory.Model("sg", SceneFactory.MakeMesh(p, n, uv, idx, mat))));
        });
        // corrupt the FSB after its header so samples cannot be decoded
        var fsb = System.IO.Path.Combine(root, "halo3", "fmod", "pc", "sfx.fsb");
        var bytes = File.ReadAllBytes(fsb);
        for (int i = bytes.Length / 2; i < bytes.Length; i++) bytes[i] = 0x5A;
        File.WriteAllBytes(fsb, bytes);
        new HaloModule(Opener(halo)).Extract(root, r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;
        Assert.Equal(AssetStatus.Ok, res["halo_mesh_shotgun"].Status);
        Assert.Contains("failed to decode", res["halo_mesh_shotgun"].Detail);
        Assert.True(File.Exists(r.Staged("textures/halo/shotgun/0.png")));
        Assert.Equal(AssetStatus.Ok, res["halo_stats_weapons"].Status);
        // samples late in the file are garbage now: the remux still "works" but the decode check rejects it, so those
        // rows fail (placeholder sound in game) instead of shipping noise; early samples are still fine
        Assert.All(HaloRows.Where(x => x.Kind == "sound"), row => Assert.NotEqual(AssetStatus.Unimplemented, res[row.Id].Status));
        Assert.Equal(AssetStatus.Ok, res["halo_snd_assault_rifle_fire"].Status);
        Assert.Equal(AssetStatus.Failed, res["halo_snd_shield_depleted"].Status);
        Assert.Contains("decodes to", res["halo_snd_shield_depleted"].Detail);
        Assert.False(File.Exists(r.Staged("sounds/halo/shield_depleted.ogg")));
    }

    [Fact]
    public void BrokenTagDataFallsBackInsteadOfFailingTheRow()
    {
        using var r = new Run();
        var (root, halo, _) = FakeInstall(r.Dir, h =>
        {
            var m = h.Map;
            // the AR's firing effect has an events block pointing outside the file
            var effe = m.Tags.First(t => t.Class == "effe" && t.Name.Contains("assault_rifle"));
            m.I32(m.At(effe) + HaloLayout.Effe.Events + 4, 0x7FFFFFF0);
            // the sniper's hlmt points its render model at garbage
            var hlmt = m.Tags.First(t => t.Class == "hlmt" && t.Name.Contains("sniper_rifle"));
            m.I32(m.At(hlmt) + HaloLayout.Hlmt.RenderModel + HaloLayout.TagRefDatum, 0x0000FFF0);
        });
        new HaloModule(Opener(halo)).Extract(root, r.Ctx, CancellationToken.None);
        r.Ctx.Finish();
        var res = r.Ctx.Results;
        Assert.Equal(AssetStatus.Ok, res["halo_snd_assault_rifle_fire"].Status);
        Assert.Contains("filename match", res["halo_snd_assault_rifle_fire"].Detail);
        Assert.Equal(AssetStatus.Failed, res["halo_mesh_sniper_rifle"].Status);
        Assert.Contains("no render model", res["halo_mesh_sniper_rifle"].Detail);
        Assert.Equal(AssetStatus.Ok, res["halo_mesh_battle_rifle"].Status);
    }

    [Fact]
    public void CancellationStopsTheRun()
    {
        using var r = new Run();
        var (root, halo, _) = FakeInstall(r.Dir);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new HaloModule(Opener(halo)).Extract(root, r.Ctx, cts.Token));
    }

    [Fact]
    public void FingerprintFilesAreRelativeToTheGivenFolder()
    {
        using var t = new TempDir();
        var (root, _, _) = FakeInstall(t);
        var files = new HaloModule().FingerprintFiles(root).ToList();
        Assert.Contains(System.IO.Path.Combine("halo3", "maps", "riverworld.map"), files);
        Assert.Contains(System.IO.Path.Combine("halo3", "fmod", "pc", "sfx.fsb.info"), files);
        Assert.All(files, f => Assert.True(File.Exists(System.IO.Path.Combine(root, f))));
        Assert.NotEmpty(new HaloModule().FingerprintFiles(System.IO.Path.Combine(t.Path, "nothing")));
    }

    /// <summary>A view of a map that hides some tags (a weapon missing from this map).</summary>
    private sealed class FilteredMap : IHaloMap
    {
        private readonly IHaloMap inner; private readonly Func<HaloTag, bool> keep;
        public FilteredMap(IHaloMap inner, Func<HaloTag, bool> keep) { this.inner = inner; this.keep = keep; }
        public string Path => inner.Path;
        public string Description => inner.Description;
        public IReadOnlyList<HaloTag> Tags => inner.Tags.Where(keep).ToList();
        public HaloTag? Global(string c) => inner.Global(c);
        public HaloTag? Tag(int i) => inner.Tag(i) is { } t && keep(t) ? t : null;
        public long TagAddress(HaloTag t) => inner.TagAddress(t);
        public byte[] Read(long a, int n) => inner.Read(a, n);
        public long Pointer(int c) => inner.Pointer(c);
        public string? StringId(int id) => inner.StringId(id);
        public Reclaimer.Geometry.Scene? Geometry(HaloTag m) => inner.Geometry(m);
        public void Dispose() { }
    }
}
