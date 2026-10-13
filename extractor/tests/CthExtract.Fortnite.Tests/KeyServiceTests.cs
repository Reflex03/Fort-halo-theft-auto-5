using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Fortnite.Net;
using Xunit;

namespace CthExtract.Fortnite.Tests;

public class KeyServiceTests
{
    private static string Fp(string version, string main, string? usmapUrl, string? md5, params (string Guid, string Key)[] extra) =>
        new JsonObject
        {
            ["version"] = version,
            ["keys"] = new JsonObject
            {
                ["mainKey"] = new JsonObject { ["key"] = "0x" + main, ["guid"] = new string('0', 32) },
                ["extraKeys"] = new JsonArray(extra.Select(e => (JsonNode)new JsonObject { ["key"] = "0x" + e.Key, ["guid"] = e.Guid }).ToArray()),
            },
            ["mappings"] = usmapUrl == null ? null : new JsonObject { ["url"] = usmapUrl, ["md5Hash"] = md5 },
        }.ToJsonString();

    private static string Uedb(string build, string main, params (string Guid, string Key)[] dyn) =>
        new JsonObject
        {
            ["version"] = build, ["mainKey"] = "0x" + main, ["updated"] = "2026-10-12T00:00:16Z",
            ["dynamicKeys"] = new JsonArray(dyn.Select(d => (JsonNode)new JsonObject { ["guid"] = d.Guid, ["key"] = "0x" + d.Key }).ToArray()),
        }.ToJsonString();

    private static string FortniteApi(string build, string main, params (string Guid, string Key, string Pak)[] dyn) =>
        new JsonObject
        {
            ["status"] = 200,
            ["data"] = new JsonObject
            {
                ["build"] = build, ["mainKey"] = main.ToLowerInvariant(),
                ["dynamicKeys"] = new JsonArray(dyn.Select(d => (JsonNode)new JsonObject { ["pakFilename"] = d.Pak, ["pakGuid"] = d.Guid, ["key"] = d.Key.ToLowerInvariant() }).ToArray()),
            },
        }.ToJsonString();

    /// <summary>A tiny file with the .usmap magic (enough for the download checks; parsing is tested on real mappings).</summary>
    private static byte[] FakeUsmap(int n) => [0xC4, 0x30, 0x04, 0, 0, 0, 0, 0, (byte)n, 1, 2, 3, 4, 5, 6, 7, 8, 9];

    private static (KeyService Svc, HttpGuard Guard, HttpClient Http) Make(FakeHttp fake, string dir, Log log)
    {
        var (http, guard) = HttpGuard.CreateClient(fake, TimeSpan.FromSeconds(5), log.Info);
        return (new KeyService(http, dir, log), guard, http);
    }

    [Fact]
    public void Parses_the_three_services()
    {
        var fp = KeyService.ParseFortnitePorting(JsonNode.Parse(Fp("42.30", FakeKeys.Key(1), "https://supabase.fortniteporting.app/x/42.30.usmap", "abc",
            (FakeKeys.Guid(1), FakeKeys.Key(2)), ("short", FakeKeys.Key(3)))), "fp")!;
        Assert.Equal("42.30", fp.Version);
        Assert.Single(fp.DynamicKeys); // the malformed GUID is dropped
        Assert.Equal("https://supabase.fortniteporting.app/x/42.30.usmap", fp.UsmapUrl);

        var u = KeyService.ParseUedbAes(JsonNode.Parse(Uedb("++Fortnite+Release-42.30-CL-58557680", FakeKeys.Key(1), (FakeKeys.Guid(1), FakeKeys.Key(2)))), "u")!;
        Assert.Equal("42.30", u.Version);
        Assert.Equal("++Fortnite+Release-42.30-CL-58557680", u.Build);
        Assert.Null(KeyService.ParseUedbAes(JsonNode.Parse("""{"error":"Version '42.30' not found"}"""), "u"));
        Assert.Equal("https://data.uedb.dev/m/x_zs.usmap",
            KeyService.ParseUedbMappingsUrl(JsonNode.Parse("""{"mappings":{"ZStandard":"https://data.uedb.dev/m/x_zs.usmap","Brotli":"https://data.uedb.dev/m/x_br.usmap"}}""")));

        var f = KeyService.ParseFortniteApi(JsonNode.Parse(FortniteApi("++Fortnite+Release-42.30-CL-58557680", FakeKeys.Key(1),
            (FakeKeys.Guid(1), FakeKeys.Key(2), "pakchunk1001-WindowsClient.pak"), (FakeKeys.Guid(1), FakeKeys.Key(2), "pakchunk1001-WindowsClient.ucas"))), "f")!;
        Assert.Single(f.DynamicKeys); // .pak and .ucas share a GUID
        Assert.Equal("pakchunk1001-WindowsClient.pak", f.DynamicKeys[0].Container);

        Assert.Null(KeyService.ParseFortnitePorting(JsonNode.Parse("""{"keys":{"mainKey":{"key":"0x1234"}}}"""), "fp"));
        Assert.False(KeyService.IsValidKey("0xZZ"));
        Assert.True(KeyService.IsValidKey("0x" + FakeKeys.Key(9)));
        Assert.Equal("09AAAA…", KeySet.Mask("0x" + FakeKeys.Key(9)));
    }

    [Fact]
    public void Fetches_version_specific_keys_first_downloads_and_caches_the_mapping_and_works_offline_later()
    {
        using var t = new TempDir();
        using var log = new Log(null);
        var usmap = FakeUsmap(1);
        var md5 = Convert.ToHexString(MD5.HashData(usmap)).ToLowerInvariant();
        var build = "++Fortnite+Release-42.30-CL-58557680";
        var fake = new FakeHttp()
            .Json(KeyService.FortnitePortingBase + "42.30", Fp("42.30", FakeKeys.Key(1), "https://supabase.fortniteporting.app/m/42.30/42.30.usmap", md5, (FakeKeys.Guid(1), FakeKeys.Key(2))))
            .Bytes("https://supabase.fortniteporting.app/m/42.30/42.30.usmap", usmap)
            .Json(KeyService.UedbBase + "aes/" + Uri.EscapeDataString(build), Uedb(build, FakeKeys.Key(1), (FakeKeys.Guid(3), FakeKeys.Key(4))))
            .Json(KeyService.FortnitePortingBase + "latest", Fp("42.40", FakeKeys.Key(7), null, null))
            .Json(KeyService.FortniteApiAes, FortniteApi("++Fortnite+Release-42.40-CL-60000000", FakeKeys.Key(7)));
        var (svc, guard, http) = Make(fake, Path.Combine(t.Path, "keys"), log);
        using (http)
        {
            var list = svc.GetCandidatesAsync(FortniteVersion.Parse(build + "-Windows"), true, default).GetAwaiter().GetResult();
            Assert.Equal(KeyService.NormalizeKey(FakeKeys.Key(1)), KeyService.NormalizeKey(list[0].MainKey));
            Assert.NotNull(list[0].UsmapPath);
            Assert.Equal(2, list[0].DynamicKeys.Count); // merged from fortniteporting + uedb
            Assert.Contains(list, k => KeyService.NormalizeKey(k.MainKey) == KeyService.NormalizeKey(FakeKeys.Key(7)));
            Assert.All(guard.Requested, u => Assert.True(HttpGuard.IsAllowed(new Uri(u), out _), u));
        }

        // offline: everything comes from the cache, the matching version first
        var (svc2, guard2, http2) = Make(new FakeHttp(), Path.Combine(t.Path, "keys"), log);
        using (http2)
        {
            var cached = svc2.GetCandidatesAsync(FortniteVersion.Parse(build), false, default).GetAwaiter().GetResult();
            Assert.Empty(guard2.Requested);
            Assert.Equal(KeyService.NormalizeKey(FakeKeys.Key(1)), KeyService.NormalizeKey(cached[0].MainKey));
            Assert.NotNull(cached[0].UsmapPath);
            Assert.StartsWith("cache:", cached[0].Source);
        }
    }

    [Fact]
    public void Bad_mappings_are_rejected_and_services_that_fail_are_skipped()
    {
        using var t = new TempDir();
        using var log = new Log(null);
        var fake = new FakeHttp()
            .Json(KeyService.FortnitePortingBase + "42.30", Fp("42.30", FakeKeys.Key(1), "https://supabase.fortniteporting.app/m/a.usmap", "00", []))
            .Bytes("https://supabase.fortniteporting.app/m/a.usmap", FakeUsmap(2)) // md5 mismatch
            .Status(KeyService.FortnitePortingBase + "latest", HttpStatusCode.InternalServerError)
            .Json(KeyService.UedbBase + "aes", "this is not json")
            .Json(KeyService.FortniteApiAes, """{"status":200,"data":{"mainKey":"tooshort"}}""");
        var (svc, _, http) = Make(fake, Path.Combine(t.Path, "keys"), log);
        using (http)
        {
            var list = svc.GetCandidatesAsync(FortniteVersion.Parse("++Fortnite+Release-42.30-CL-1"), true, default).GetAwaiter().GetResult();
            Assert.Single(list);
            Assert.Null(list[0].UsmapPath);
        }
        Assert.False(KeyService.UsmapLooksValid(Path.Combine(t.Path, "nope.usmap"), null, out _));
        var notUsmap = Path.Combine(t.Path, "x.usmap");
        File.WriteAllBytes(notUsmap, new byte[64]);
        Assert.False(KeyService.UsmapLooksValid(notUsmap, null, out var why));
        Assert.Contains("magic", why);
    }

    [Theory]
    [InlineData("https://api.fortniteporting.app/v2/fortnite/versions/latest", true)]
    [InlineData("https://data.uedb.dev/mappings/x_zs.usmap", true)]
    [InlineData("https://fortnite-api.com/v2/aes", true)]
    [InlineData("http://fortnite-api.com/v2/aes", false)]
    [InlineData("https://github.com/WorkingRobot/OodleUE/releases/download/2026-06-04-1357/clang-cl-x64-release.zip", false)]
    [InlineData("https://github.com/NotOfficer/Zlib-ng.NET/releases/download/1.0.0/zlib-ng2.dll.gz", false)]
    [InlineData("https://uedb.dev/oo2core_9_win64.dll", false)]
    [InlineData("https://data.uedb.dev/payload.zip", false)]
    [InlineData("https://egdownload.fastly-edge.com/chunks/ab/abc.iochunk", false)]
    public void The_http_guard_only_allows_the_key_services_and_never_a_library(string url, bool allowed)
    {
        Assert.Equal(allowed, HttpGuard.IsAllowed(new Uri(url), out _));
    }

    [Fact]
    public void Redirects_between_the_key_services_are_followed()
    {
        using var log = new Log(null);
        var fake = new FakeHttp()
            .Redirect("https://uedb.dev/svc/api/v1/fortnite/mappings/file", "https://data.uedb.dev/m/x_zs.usmap")
            .Bytes("https://data.uedb.dev/m/x_zs.usmap", FakeUsmap(3));
        var (http, guard) = HttpGuard.CreateClient(fake, TimeSpan.FromSeconds(5), log.Info);
        using (http)
        {
            var bytes = http.GetByteArrayAsync("https://uedb.dev/svc/api/v1/fortnite/mappings/file").GetAwaiter().GetResult();
            Assert.Equal(FakeUsmap(3), bytes);
        }
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public void Redirects_to_other_hosts_are_refused()
    {
        using var log = new Log(null);
        var fake = new FakeHttp().Redirect(KeyService.FortniteApiAes, "https://evil.example/oo2core_9_win64.dll");
        var (http, guard) = HttpGuard.CreateClient(fake, TimeSpan.FromSeconds(5), log.Info);
        using (http)
        {
            var e = Assert.ThrowsAny<HttpRequestException>(() => http.GetAsync(KeyService.FortniteApiAes).GetAwaiter().GetResult());
            Assert.Contains("evil.example", e.Message);
        }
        Assert.Equal(2, guard.Requested.Count);
        Assert.Single(fake.Requests); // the refused hop never reached the network
    }
}
