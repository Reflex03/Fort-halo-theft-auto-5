using System.Net;
using System.Text;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract.Fortnite.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cthfn-" + Guid.NewGuid().ToString("N")[..12]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

/// <summary>A cache folder layout like the real one (cache/packs/fortnite staging, cache/stats, cache/work).</summary>
public sealed class TempCache : IDisposable
{
    private readonly TempDir _dir = new();
    public string Root => _dir.Path;
    public string LogPath => System.IO.Path.Combine(Root, "extract.log");
    public Log Log { get; }
    public ExtractContext Ctx { get; }
    public string KeysDir => System.IO.Path.Combine(Root, "keys", "fortnite");

    public TempCache()
    {
        Log = new Log(LogPath);
        Ctx = new ExtractContext("fortnite", "fortnite", System.IO.Path.Combine(Root, "packs", "fortnite.staging"),
            System.IO.Path.Combine(Root, "stats"), System.IO.Path.Combine(Root, "work"), Log);
    }

    public string Out(string relative) => System.IO.Path.Combine(Ctx.AssetsDir, relative);
    public string ReadLog() { Log.Dispose(); return File.ReadAllText(LogPath); }
    public void Dispose() { Log.Dispose(); _dir.Dispose(); }
}

/// <summary>CUE4Parse's own Apache-2.0 test fixtures (a UE6.0 project cooked by the CUE4Parse team, not game data).
/// Never vendored: found through CTH_CUE4PARSE_FIXTURES (the Fixtures folder) or CTH_REFS (folder holding a CUE4Parse
/// clone); tests that need them are skipped otherwise.</summary>
public static class Fixtures
{
    public static string? Root
    {
        get
        {
            var direct = Environment.GetEnvironmentVariable("CTH_CUE4PARSE_FIXTURES");
            if (!string.IsNullOrEmpty(direct) && Directory.Exists(Path.Combine(direct, "UE6_0"))) return direct;
            var refs = Environment.GetEnvironmentVariable("CTH_REFS");
            if (!string.IsNullOrEmpty(refs))
            {
                var p = Path.Combine(refs, "CUE4Parse", "CUE4Parse.Tests", "Fixtures");
                if (Directory.Exists(Path.Combine(p, "UE6_0"))) return p;
            }
            return null;
        }
    }

    public static string? Ue60 => Root is { } r ? Path.Combine(r, "UE6_0") : null;
    public const string SkipReason = "CUE4Parse fixtures not available (set CTH_REFS to a folder with a CUE4Parse clone)";

    /// <summary>The AES key the fixture project was cooked with (ASCII), as 64 hex digits.</summary>
    public static string AesKeyHex => Convert.ToHexString(Encoding.ASCII.GetBytes("CUE4ParseFixtureAESKey0123456789"));

    public static string Usmap(string flavour = "Zstandard") => Path.Combine(Ue60!, "Mappings", $"CUE4ParseFixtures-{flavour}.usmap");

    /// <summary>A fake Fortnite install (FortniteGame/Content/Paks) holding copies of one IoStore variant (+ the global
    /// container) and optionally one .pak variant (it carries the AssetRegistry.bin, like Fortnite's pakchunk0).</summary>
    public static string MakeInstall(string root, string ioVariant = "Uncompressed", string? pakVariant = "Uncompressed", string serialization = "Unversioned")
    {
        var paks = Path.Combine(root, "FortniteGame", "Content", "Paks");
        Directory.CreateDirectory(paks);
        var io = Path.Combine(Ue60!, "IoStore", serialization);
        foreach (var f in Directory.GetFiles(Path.Combine(io, ioVariant))) File.Copy(f, Path.Combine(paks, Path.GetFileName(f)), true);
        foreach (var f in new[] { "global.utoc", "global.ucas" }) File.Copy(Path.Combine(io, f), Path.Combine(paks, f), true);
        if (pakVariant != null)
            foreach (var f in Directory.GetFiles(Path.Combine(Ue60!, "Pak", pakVariant))) File.Copy(f, Path.Combine(paks, Path.GetFileName(f)), true);
        return root;
    }
}

/// <summary>Fake HTTP endpoint: URL -> response; records every request.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = new();

    public FakeHttp Json(string url, string json) { _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; return this; }
    public FakeHttp Bytes(string url, byte[] bytes) { _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; return this; }
    public FakeHttp Status(string url, HttpStatusCode code) { _routes[url] = () => new HttpResponseMessage(code); return this; }
    public FakeHttp Redirect(string url, string to)
    {
        _routes[url] = () => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new Uri(to); return r; };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests) Requests.Add(url);
        return Task.FromResult(_routes.TryGetValue(url, out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public static class Rows
{
    public static SourceRow Get(string id) => Sources.All.First(r => r.Id == id);
}

/// <summary>Fake 64-hex-digit keys (never real Fortnite keys).</summary>
public static class FakeKeys
{
    public static string Key(int n) => n.ToString("X2") + new string('A', 62);
    public static string Guid(int n) => n.ToString("X2") + new string('B', 30);
}
