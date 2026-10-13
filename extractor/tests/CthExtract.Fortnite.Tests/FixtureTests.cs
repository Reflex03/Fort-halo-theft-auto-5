using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Fortnite.Audio;
using CthExtract.Fortnite.Conversion;
using CthExtract.Fortnite.Net;
using CthExtract.Fortnite.Tests.Synth;
using CthExtract.Fortnite.Unreal;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.UObject;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>
/// Runs the module's code on CUE4Parse's UE6.0 fixture project (unversioned IoStore + a .pak with the asset registry,
/// the same layout Fortnite uses). Uncompressed containers, so these run without the Oodle decoder; see
/// OodleFixtureTests for the Oodle-compressed and AES-encrypted variants.
/// </summary>
public class FixtureTests
{
    internal static readonly ArchiveOptions FixtureArchive = new() { RequiredMappingTypes = ["StaticMesh", "SkeletalMesh", "Texture2D", "SoundWave"], MinMappingTypes = 100 };

    internal static KeySet FixtureKeys(string? key = null, string source = "https://api.fortniteporting.app/test") =>
        new(source, "6.0", null, key ?? Fixtures.AesKeyHex, [], Fixtures.Usmap(), null, null, DateTime.UtcNow);

    private sealed class Opened : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly TempCache Cache = new();
        public readonly GameArchive Archive;
        public Opened(string io = "Uncompressed", string? pak = "Uncompressed")
        {
            var root = Fixtures.MakeInstall(Dir.Path, io, pak);
            Archive = GameArchive.Open(Path.Combine(root, "FortniteGame", "Content", "Paks"), [], [FixtureKeys()], FixtureArchive, Cache.Log, default);
        }
        public T Load<T>(string path) where T : UObject => Archive.TryLoad<T>(path, out var e) ?? throw new Exception($"{path}: {e}");
        public void Dispose() { Archive.Dispose(); Cache.Dispose(); Dir.Dispose(); }
    }

    private static void Need() => Assert.SkipWhen(Fixtures.Ue60 is null, Fixtures.SkipReason);

    [Fact]
    public void Opens_the_containers_and_indexes_the_asset_registry()
    {
        Need();
        using var o = new Opened();
        Assert.True(o.Archive.Provider.Files.Count > 100);
        Assert.True(o.Archive.Index.Entries.Count >= 300, $"{o.Archive.Index.Entries.Count} registry entries");
        Assert.True(o.Archive.Index.ByClass("SoundWave").Count >= 9);
        var bp = Assert.Single(o.Archive.Index.ByName("BP_Components_C"));
        Assert.Contains("Engine.Actor", bp.Tag("NativeParentClass"));
        Assert.Equal("StaticMesh", o.Archive.Index.ClassOf("/Game/Fixtures/Meshes/SM_Fixture.SM_Fixture"));
        Assert.NotEmpty(o.Archive.Index.FilesNamed("SM_Fixture"));
        Assert.Contains("CUE4ParseFixtures-Zstandard.usmap", o.Archive.MappingNote);
    }

    [Fact]
    public void Static_mesh_converts_with_the_contract_axes_and_winding()
    {
        Need();
        using var o = new Opened();
        var sm = o.Load<UStaticMesh>("/Game/Fixtures/Meshes/SM_Fixture.SM_Fixture");
        var b = MeshConvert.Build([new MeshSource("SM_Fixture", sm)], 3000, "fortnite/test");
        Assert.Equal(2, b.Mesh.TriangleCount);
        Assert.InRange(b.Materials.Count, 1, 2); // two slots; sections sharing one material are merged
        Assert.Equal(b.Materials.Count, b.Mesh.Submeshes.Count);
        var bounds = Placement.Measure(b.Mesh);
        // UE corners (-50,-50,0) and (50,50,20) cm -> Minecraft metres
        Assert.Equal(-0.5f, bounds.Min.X, 3); Assert.Equal(0f, bounds.Min.Y, 3); Assert.Equal(-0.5f, bounds.Min.Z, 3);
        Assert.Equal(0.5f, bounds.Max.X, 3); Assert.Equal(0.2f, bounds.Max.Y, 3); Assert.Equal(0.5f, bounds.Max.Z, 3);
        var (agree, total) = MeshConvert.WindingAgreement(b.Mesh);
        Assert.Equal(total, agree);

        var nanite = o.Load<UStaticMesh>("/Game/Fixtures/Meshes/SM_Nanite.SM_Nanite");
        var nb = MeshConvert.Build([new MeshSource("SM_Nanite", nanite)], 8000, "fortnite/test");
        Assert.Equal(2042, nb.Mesh.TriangleCount);
        var (na, nt) = MeshConvert.WindingAgreement(nb.Mesh);
        Assert.True(nt > 2000 && na == nt, $"{na}/{nt} triangles face the right way");
    }

    [Fact]
    public void A_component_transform_moves_rotates_and_mirrors_correctly()
    {
        Need();
        using var o = new Opened();
        var sm = o.Load<UStaticMesh>("/Game/Fixtures/Meshes/SM_Fixture.SM_Fixture");
        // +100 cm along UE X (forward) -> +1 m along Minecraft Z
        var moved = MeshConvert.Build([new MeshSource("m", sm, [new CUE4Parse.UE4.Objects.Core.Math.FTransform(new CUE4Parse.UE4.Objects.Core.Math.FVector(100, 0, 0))])], 3000, "t");
        Assert.Equal(1.5f, Placement.Measure(moved.Mesh).Max.Z, 3);
        // mirrored scale keeps faces pointing outwards (winding flipped)
        var mirror = new CUE4Parse.UE4.Objects.Core.Math.FTransform(CUE4Parse.UE4.Objects.Core.Math.FQuat.Identity, default, new CUE4Parse.UE4.Objects.Core.Math.FVector(1, -1, 1));
        var mb = MeshConvert.Build([new MeshSource("m", sm, [mirror])], 3000, "t");
        var (a, n) = MeshConvert.WindingAgreement(mb.Mesh);
        Assert.Equal(n, a);
    }

    [Fact]
    public void Skeletal_mesh_converts_in_bind_pose_and_lods_follow_the_budget()
    {
        Need();
        using var o = new Opened();
        var sk = o.Load<USkeletalMesh>("/Game/Fixtures/Meshes/SK_Fixture.SK_Fixture");
        var b = MeshConvert.Build([new MeshSource("SK_Fixture", sk)], 4000, "fortnite/glider");
        Assert.True(b.Mesh.TriangleCount > 0);
        var bounds = Placement.Measure(b.Mesh);
        Assert.Equal(1f, bounds.Size.X, 2);
        Assert.Contains(b.Notes, n => n.Contains("LOD0"));
        var (agree, total) = MeshConvert.WindingAgreement(b.Mesh);
        Assert.Equal(total, agree);
    }

    public static readonly TheoryData<string, string, double, int> TextureCases = new()
    {
        { "T_BGRA8", "rgba.png", 0.01, 0 },
        { "T_G8", "grayscale.png", 0.01, 0 },
        { "T_BC1", "rgb.png", 1.5, 12 },
        { "T_BC3", "rgba.png", 1.5, 12 },
        { "T_BC7", "rgba.png", 1.0, 8 },
    };

    [Theory]
    [MemberData(nameof(TextureCases))]
    public void Textures_decode_to_their_source_images(string texture, string source, double maxMean, int maxAbs)
    {
        Need();
        using var o = new Opened();
        var tex = o.Load<UTexture2D>($"/Game/Fixtures/Textures/{texture}.{texture}");
        var img = TextureConvert.Decode(tex, 4096, out var detail);
        var src = PngRead.Read(File.ReadAllBytes(Path.Combine(Fixtures.Ue60!, "SourceTextures", source)));
        Assert.Equal(src.Width, img.Width);
        Assert.Equal(src.Height, img.Height);
        long sum = 0; int max = 0;
        for (int i = 0; i < img.Pixels.Length; i++)
        {
            if (i % 4 == 3 && source != "rgba.png") continue; // sources without alpha
            int d = Math.Abs(img.Pixels[i] - src.Pixels[i]);
            sum += d; max = Math.Max(max, d);
        }
        double mean = sum / (double)img.Pixels.Length;
        Assert.True(mean <= maxMean && max <= maxAbs, $"{detail}: mean |d| {mean:0.###}, max {max}");
    }

    [Fact]
    public void Blueprint_components_include_inherited_and_overridden_ones()
    {
        Need();
        using var o = new Opened();
        var parent = o.Load<UStruct>("/Game/Fixtures/Blueprints/BP_Components.BP_Components_C");
        var comps = Blueprints.Components(parent);
        Assert.Contains(comps, c => c.Name == "FixtureRoot");
        var mesh = Assert.Single(comps, c => c.Mesh != null);
        Assert.Equal("FixtureMesh", mesh.Name);
        Assert.Equal("SM_Fixture", mesh.Mesh!.Name);

        var child = o.Load<UStruct>("/Game/Fixtures/Blueprints/BP_ComponentsChild.BP_ComponentsChild_C");
        var chain = Blueprints.ClassChain(child);
        Assert.Equal("BP_ComponentsChild_C", chain[0].Name);
        Assert.Equal("BP_Components_C", chain[1].Name);
        var cm = Assert.Single(Blueprints.Components(child), c => c.Mesh != null);
        Assert.Contains("override", cm.Source);
        Assert.Equal("SM_Fixture", cm.Mesh!.Name);
    }

    [Fact]
    public void Every_fixture_sound_format_is_converted_or_reported()
    {
        Need();
        using var o = new Opened();
        SoundResult R(string n) => SoundConvert.FromWave(o.Load<USoundWave>($"/Game/Fixtures/Audio/{n}.{n}"));
        var binka = R("SW_Format_BinkAudio");
        Assert.True(binka.Ogg != null, binka.Detail);
        Assert.Contains("Bink Audio 1ch 48000 Hz", binka.Detail);
        Assert.InRange(OggEncode.Verify(binka.Ogg!, out _)!.Value.Seconds, 1.9, 2.1);
        foreach (var n in new[] { "SW_Format_PlatformSpecific", "SW_Format_ProjectDefined" })
            Assert.Contains("kept as-is", R(n).Detail);
        foreach (var n in new[] { "SW_Format_PCM", "SW_Format_ADPCM", "SW_Inline", "SW_Streaming" })
        {
            var r = R(n);
            Assert.True(r.Ogg != null, r.Detail);
        }
        Assert.True(R("SW_Format_RADAudio").Unsupported);
        Assert.True(R("SW_Format_Opus").Unsupported);
    }

    /// <summary>The fixture's Bink Audio payload decodes to exactly what vgmstream-cli (built from source) produced.</summary>
    [Fact]
    public void Fixture_bink_audio_matches_vgmstream()
    {
        Need();
        using var o = new Opened();
        var w = o.Load<USoundWave>("/Game/Fixtures/Audio/SW_Format_BinkAudio.SW_Format_BinkAudio");
        CUE4Parse_Conversion.Sounds.SoundDecoder.Decode(w, true, out var fmt, out var data);
        Assert.Equal("BINKA", fmt);
        var pcm = VgmstreamBinka.UebaReader.Decode(data!).ToPcm16();
        Assert.Equal(96000, pcm.Length);
        Assert.Equal("01FAF390C5ED71D9721219F601D79539DB345D65C4EF57D929483EAE834BDAD3", Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pcm.AsSpan()))));
        // and it is the same signal as the fixture's PCM version of the source (different codec, so only correlated)
        CUE4Parse_Conversion.Sounds.SoundDecoder.Decode(o.Load<USoundWave>("/Game/Fixtures/Audio/SW_Format_PCM.SW_Format_PCM"), true, out _, out var wav);
        var reference = WavDecode.Decode(wav!).Channels[0];
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < Math.Min(reference.Length, pcm.Length); i++) { double x = pcm[i] / 32768.0, y = reference[i]; sxy += x * y; sxx += x * x; syy += y * y; }
        Assert.True(sxy / Math.Sqrt(sxx * syy) > 0.99);
    }

    [Fact]
    public void A_sound_cue_resolves_to_its_waves()
    {
        Need();
        using var o = new Opened();
        var cue = o.Load<USoundCue>("/Game/Fixtures/Audio/SC_Fixture.SC_Fixture");
        Assert.NotEmpty(SoundGraph.Waves(new FPackageIndexLike(cue)));
    }

    private sealed class FPackageIndexLike(UObject o) : ILoadableObject
    {
        public Type? GetObjectType() => o.GetType();
        public UObject? Load() => o;
        public bool TryLoad(out UObject export) { export = o; return true; }
        public Task<UObject?> LoadAsync() => Task.FromResult<UObject?>(o);
        public Task<UObject?> TryLoadAsync() => Task.FromResult<UObject?>(o);
    }

    // ------------------------------------------------------------------ the recipes, pointed at fixture assets

    [Fact]
    public void Recipes_write_meshes_icons_and_sounds_and_report_the_rest()
    {
        Need();
        using var o = new Opened();
        var ctx = o.Cache.Ctx;
        var content = new FortniteContent(o.Archive, ctx, new RecipeOptions
        {
            ChestNativeParent = "Engine.Actor",
            ChestSize = new SizeRange(0.1f, 10f),
            BusItemClass = "NoSuchClass",
            BusMeshPattern = "(?i)^SM_Nanite$",
            BusSize = new SizeRange(0.1f, 50f),
            IconProperties = ["HardTextureReference"],
        });

        var chestRow = Rows.Get("fn_mesh_chest") with { Locator = "BuildingContainer blueprint matching (?i)^BP_Components$ -> SCS StaticMeshComponent mesh" };
        content.Chest(chestRow);
        content.BattleBus(Rows.Get("fn_mesh_battle_bus"));
        content.Icon(Rows.Get("fn_icon_bandages") with { Locator = "asset registry item definition named (DA_AllProperties) -> icon" });
        content.Icon(Rows.Get("fn_icon_med_kit")); // real names: not in this project
        content.Glider(Rows.Get("fn_mesh_glider"));
        content.ItemSound(Rows.Get("fn_snd_drink") with { Fallback = "SoundWave named (?i)SW_Format_BinkAudio" }, [Rows.Get("fn_icon_shield_potion")]);
        content.ItemSound(Rows.Get("fn_snd_heal_use") with { Fallback = "SoundWave named (?i)SW_Format_RADAudio" }, [Rows.Get("fn_icon_bandages")]);
        content.ChestSound(Rows.Get("fn_snd_chest_open") with { Fallback = "SoundWave named (?i)SW_Format_PlatformSpecific" }, chestRow);
        ctx.Finish();
        var res = ctx.Results;
        var log = o.Cache.ReadLog();

        Assert.True(res["fn_mesh_chest"].Status == AssetStatus.Ok, res["fn_mesh_chest"].Detail);
        var chest = Cthm.Read(o.Cache.Out("meshes/fortnite/chest.cthm"));
        var cb = Placement.Measure(chest);
        Assert.Equal(0f, cb.Min.Y, 3);
        Assert.Equal(0f, cb.Centre.X, 3);
        Assert.Equal(0f, cb.Centre.Z, 3);
        foreach (var s in chest.Submeshes) Assert.True(File.Exists(o.Cache.Out($"textures/{s.Material}.png")), s.Material);
        Assert.StartsWith("fortnite/chest/", chest.Submeshes[0].Material);

        Assert.True(res["fn_mesh_battle_bus"].Status == AssetStatus.Ok, res["fn_mesh_battle_bus"].Detail);
        Assert.Contains("name fallback", res["fn_mesh_battle_bus"].Detail);
        var bus = Cthm.Read(o.Cache.Out("meshes/fortnite/battle_bus.cthm"));
        Assert.Equal(2042, bus.TriangleCount);

        Assert.True(res["fn_icon_bandages"].Status == AssetStatus.Ok, res["fn_icon_bandages"].Detail);
        var icon = PngRead.Read(File.ReadAllBytes(o.Cache.Out("textures/item/fortnite/bandages.png")));
        Assert.Equal(64, icon.Width);
        Assert.Equal(AssetStatus.Missing, res["fn_icon_med_kit"].Status);
        Assert.Contains("no item definition found", res["fn_icon_med_kit"].Detail);
        Assert.Equal(AssetStatus.Missing, res["fn_mesh_glider"].Status);

        Assert.True(res["fn_snd_drink"].Status == AssetStatus.Ok, res["fn_snd_drink"].Detail);
        Assert.Contains("Bink Audio", res["fn_snd_drink"].Detail);
        Assert.Equal(AssetStatus.Missing, res["fn_snd_heal_use"].Status);
        Assert.Contains("RAD Audio", res["fn_snd_heal_use"].Detail);
        Assert.True(res["fn_snd_chest_open"].Status == AssetStatus.Ok, res["fn_snd_chest_open"].Detail);
        Assert.Contains("kept as-is", res["fn_snd_chest_open"].Detail);

        var sounds = JsonNode.Parse(File.ReadAllText(o.Cache.Out("sounds.json")))!.AsObject();
        Assert.True(sounds.ContainsKey("fortnite.drink"));
        Assert.True(sounds.ContainsKey("fortnite.chest_open"));
        Assert.False(sounds.ContainsKey("fortnite.heal_use"));
        Assert.Contains("BP_Components_C.SearchingSoundCueLoop: not set", log);
        Assert.Contains("[fortnite] fn_mesh_chest: BP_Components_C components:", log);
    }

    // ------------------------------------------------------------------ the whole module

    private static FortniteOptions ModuleOptions(TempDir t, FakeHttp http, bool offline = false, string? keysDir = null) => new()
    {
        HttpHandler = http,
        Offline = offline,
        KeysDir = keysDir,
        ManifestsDir = Path.Combine(t.Path, "Manifests"),
        LocalAppData = null,
        Archive = FixtureArchive,
    };

    private static string WriteManifest(TempDir t, string installRoot, string version)
    {
        var dir = t.Sub("Manifests");
        File.WriteAllText(Path.Combine(dir, "F.item"), new JsonObject
        {
            ["AppName"] = "Fortnite", ["DisplayName"] = "Fortnite", ["InstallLocation"] = installRoot, ["AppVersionString"] = version,
            ["LaunchExecutable"] = "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
        }.ToJsonString());
        return dir;
    }

    [Fact]
    public void Module_runs_end_to_end_and_reports_every_row_on_a_project_without_fortnite_assets()
    {
        Need();
        using var t = new TempDir();
        var root = Fixtures.MakeInstall(t.Sub("Epic", "Fortnite"));
        WriteManifest(t, root, "++Fortnite+Release-42.30-CL-58557680-Windows");
        var usmap = File.ReadAllBytes(Fixtures.Usmap());
        var md5 = Convert.ToHexString(MD5.HashData(usmap));
        var http = new FakeHttp()
            .Json(KeyService.FortnitePortingBase + "42.30", new JsonObject
            {
                ["version"] = "42.30",
                ["keys"] = new JsonObject { ["mainKey"] = new JsonObject { ["key"] = "0x" + FakeKeys.Key(1), ["guid"] = new string('0', 32) }, ["extraKeys"] = new JsonArray() },
                ["mappings"] = new JsonObject { ["url"] = "https://supabase.fortniteporting.app/storage/v1/object/public/archive-mappings/42.30/42.30.usmap", ["md5Hash"] = md5 },
            }.ToJsonString())
            .Bytes("https://supabase.fortniteporting.app/storage/v1/object/public/archive-mappings/42.30/42.30.usmap", usmap);

        using (var c = new TempCache())
        {
            new FortniteModule(ModuleOptions(t, http)).Extract(root, c.Ctx, TestContext.Current.CancellationToken);
            c.Ctx.Finish();
            var log = c.ReadLog();
            foreach (var row in c.Ctx.Rows)
            {
                var r = c.Ctx.Results[row.Id];
                Assert.True(r.Status == AssetStatus.Missing, $"{row.Id}: {r.Status} {r.Detail}");
            }
            Assert.Contains("version ++Fortnite+Release-42.30-CL-58557680-Windows", log);
            Assert.Contains("asset registry CUE4ParseFixtures/AssetRegistry.bin", log);
            Assert.Contains("mapping: 42.30.usmap", log);
            Assert.All(http.Requests, u => Assert.True(HttpGuard.IsAllowed(new Uri(u), out _), u));
            Assert.True(File.Exists(Path.Combine(c.Root, "keys", "fortnite", "42.30.usmap")));

            // a second run without network uses the cache
            var offlineHttp = new FakeHttp();
            using var c2 = new TempCache();
            new FortniteModule(ModuleOptions(t, offlineHttp, offline: true, keysDir: Path.Combine(c.Root, "keys", "fortnite"))).Extract(root, c2.Ctx, TestContext.Current.CancellationToken);
            c2.Ctx.Finish();
            Assert.Empty(offlineHttp.Requests);
            Assert.All(c2.Ctx.Results.Values, r => Assert.Equal(AssetStatus.Missing, r.Status));
            Assert.Contains("cache:", c2.ReadLog());
        }
    }

    [Fact]
    public void Module_without_keys_or_install_reports_missing_with_the_reason()
    {
        using var t = new TempDir();
        using (var c = new TempCache())
        {
            new FortniteModule(ModuleOptions(t, new FakeHttp())).Extract(Path.Combine(t.Path, "nowhere"), c.Ctx, default);
            Assert.All(c.Ctx.Results.Values, r => { Assert.Equal(AssetStatus.Missing, r.Status); Assert.Contains("Fortnite folder not found", r.Detail); });
            Assert.Equal(c.Ctx.Rows.Count, c.Ctx.Results.Count);
        }
        var root = t.Sub("Fortnite");
        Directory.CreateDirectory(Path.Combine(root, "FortniteGame", "Content", "Paks"));
        using (var c = new TempCache())
        {
            new FortniteModule(ModuleOptions(t, new FakeHttp(), offline: true)).Extract(root, c.Ctx, default);
            Assert.All(c.Ctx.Results.Values, r => { Assert.Equal(AssetStatus.Missing, r.Status); Assert.Contains("no Fortnite AES keys", r.Detail); });
        }
    }
}
