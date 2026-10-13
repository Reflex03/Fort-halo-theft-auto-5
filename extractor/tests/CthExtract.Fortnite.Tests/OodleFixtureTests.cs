using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Fortnite.Conversion;
using CthExtract.Fortnite.Net;
using CthExtract.Fortnite.Tests.Synth;
using CthExtract.Fortnite.Unreal;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Xunit;

namespace CthExtract.Fortnite.Tests;

/// <summary>
/// Oodle-compressed and AES-encrypted fixture containers (the Fortnite case) through the managed Oodle decoder
/// (CthExtract.Oodle). Skipped when the build has no Oodle decoder or the fixtures are absent.
/// </summary>
public class OodleFixtureTests
{
    private static void Need()
    {
        Assert.SkipWhen(Fixtures.Ue60 is null, Fixtures.SkipReason);
        Assert.SkipUnless(OodleBridge.Available, "built without CthExtract.Oodle");
    }

    [Fact]
    public void Encrypted_oodle_containers_open_with_the_right_key_and_read_correctly()
    {
        Need();
        using var t = new TempDir();
        using var c = new TempCache();
        var root = Fixtures.MakeInstall(t.Path, "OodleEncrypted", "Oodle");
        var wrong = FixtureTests.FixtureKeys(FakeKeys.Key(5), "https://api.fortniteporting.app/wrong");
        var right = FixtureTests.FixtureKeys(null, "https://api.fortniteporting.app/right");
        using var a = GameArchive.Open(Path.Combine(root, "FortniteGame", "Content", "Paks"), [], [wrong, right], FixtureTests.FixtureArchive, c.Log, default);
        Assert.Equal("https://api.fortniteporting.app/right", a.Keys.Source);
        var log = c.ReadLog();
        Assert.Contains("wrong (6.0): wrong key", log);
        Assert.Contains("right (6.0): opens", log);
        Assert.True(a.Index.Entries.Count >= 300);

        var sm = a.TryLoad<UStaticMesh>("/Game/Fixtures/Meshes/SM_Nanite.SM_Nanite", out var e) ?? throw new Exception(e);
        Assert.Equal(2042, MeshConvert.Build([new MeshSource("n", sm)], 8000, "t").Mesh.TriangleCount);

        var tex = a.TryLoad<UTexture2D>("/Game/Fixtures/Textures/T_BC7.T_BC7", out e) ?? throw new Exception(e);
        var img = TextureConvert.Decode(tex, 4096, out _);
        var src = PngRead.Read(File.ReadAllBytes(Path.Combine(Fixtures.Ue60!, "SourceTextures", "rgba.png")));
        int max = 0;
        for (int i = 0; i < img.Pixels.Length; i++) max = Math.Max(max, Math.Abs(img.Pixels[i] - src.Pixels[i]));
        Assert.True(max <= 8, $"max |d| {max}");

        var w = a.TryLoad<USoundWave>("/Game/Fixtures/Audio/SW_Format_BinkAudio.SW_Format_BinkAudio", out e) ?? throw new Exception(e);
        CUE4Parse_Conversion.Sounds.SoundDecoder.Decode(w, true, out _, out var data);
        Assert.Equal("01FAF390C5ED71D9721219F601D79539DB345D65C4EF57D929483EAE834BDAD3",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(VgmstreamBinka.UebaReader.Decode(data!).ToPcm16().AsSpan()))));
    }

    [Fact]
    public void Only_wrong_keys_is_a_key_mismatch()
    {
        Need();
        using var t = new TempDir();
        using var c = new TempCache();
        var root = Fixtures.MakeInstall(t.Path, "OodleEncrypted", null);
        var e = Assert.Throws<KeyMismatchException>(() =>
            GameArchive.Open(Path.Combine(root, "FortniteGame", "Content", "Paks"), [], [FixtureTests.FixtureKeys(FakeKeys.Key(6))], FixtureTests.FixtureArchive, c.Log, default));
        Assert.Contains("wrong key", e.Message);
    }

    /// <summary>The installed version's key does not open the containers, the "latest" one does: the module keeps
    /// going with the key that works and reports every (non-Fortnite) row Missing.</summary>
    [Fact]
    public void Module_falls_back_to_a_key_that_opens_the_install()
    {
        Need();
        using var t = new TempDir();
        var root = Fixtures.MakeInstall(t.Sub("Epic", "Fortnite"), "OodleEncrypted", "Oodle");
        var manifests = t.Sub("Manifests");
        File.WriteAllText(Path.Combine(manifests, "F.item"), new JsonObject { ["AppName"] = "Fortnite", ["InstallLocation"] = root, ["AppVersionString"] = "++Fortnite+Release-42.30-CL-1-Windows" }.ToJsonString());
        var usmap = File.ReadAllBytes(Fixtures.Usmap());
        string Fp(string v, string key, string map) => new JsonObject
        {
            ["version"] = v,
            ["keys"] = new JsonObject { ["mainKey"] = new JsonObject { ["key"] = "0x" + key, ["guid"] = new string('0', 32) } },
            ["mappings"] = new JsonObject { ["url"] = map },
        }.ToJsonString();
        var http = new FakeHttp()
            .Json(KeyService.FortnitePortingBase + "42.30", Fp("42.30", FakeKeys.Key(1), "https://supabase.fortniteporting.app/m/42.30.usmap"))
            .Bytes("https://supabase.fortniteporting.app/m/42.30.usmap", usmap)
            .Json(KeyService.FortnitePortingBase + "latest", Fp("42.40", Fixtures.AesKeyHex, "https://supabase.fortniteporting.app/m/42.40.usmap"))
            .Bytes("https://supabase.fortniteporting.app/m/42.40.usmap", usmap);
        using var c = new TempCache();
        new FortniteModule(new FortniteOptions { HttpHandler = http, ManifestsDir = manifests, LocalAppData = null, Archive = FixtureTests.FixtureArchive })
            .Extract(root, c.Ctx, TestContext.Current.CancellationToken);
        c.Ctx.Finish();
        var log = c.ReadLog();
        Assert.Contains("(42.30): wrong key", log);
        Assert.Contains("(42.40): opens", log);
        Assert.All(c.Ctx.Results.Values, r => Assert.True(r.Status == AssetStatus.Missing, $"{r.Id}: {r.Status} {r.Detail}"));
    }
}
