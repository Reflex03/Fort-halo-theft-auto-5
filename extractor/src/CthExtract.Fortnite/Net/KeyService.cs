using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CthExtract.Core;

namespace CthExtract.Fortnite.Net;

public sealed record DynamicKey(string Guid, string Key, string? Container);

/// <summary>AES keys (+ the mapping file) for one Fortnite build, from one source.</summary>
public sealed record KeySet(
    string Source,
    string? Version,
    string? Build,
    string MainKey,
    IReadOnlyList<DynamicKey> DynamicKeys,
    string? UsmapPath,
    string? UsmapUrl,
    string? UsmapMd5,
    DateTime FetchedUtc)
{
    public string Describe() =>
        $"{Source} version={Version ?? "?"} build={Build ?? "?"} mainKey={Mask(MainKey)} dynamicKeys={DynamicKeys.Count} usmap={(UsmapPath is null ? "none" : Path.GetFileName(UsmapPath))}";

    /// <summary>Keys are never written to the log in full.</summary>
    public static string Mask(string key)
    {
        var k = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? key[2..] : key;
        return k.Length <= 8 ? "****" : k[..6] + "…";
    }
}

/// <summary>
/// Finds AES keys and the .usmap mapping file for the player's Fortnite version.
/// Order: api.fortniteporting.app versions/{ver}, uedb.dev for the exact build, then the "latest" of
/// fortniteporting / uedb / fortnite-api.com, then whatever an earlier run cached in cache/keys/fortnite.
/// Nothing is ever shipped; everything fetched is cached locally so later runs work offline.
/// </summary>
public sealed class KeyService
{
    public const string FortnitePortingBase = "https://api.fortniteporting.app/v2/fortnite/versions/";
    public const string UedbBase = "https://uedb.dev/svc/api/v1/fortnite/";
    public const string FortniteApiAes = "https://fortnite-api.com/v2/aes";
    public const long MaxUsmapBytes = 64L << 20;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly Log _log;

    public KeyService(HttpClient http, string cacheDir, Log log)
    {
        _http = http; _cacheDir = cacheDir; _log = log;
        Directory.CreateDirectory(_cacheDir);
    }

    public string CacheDir => _cacheDir;

    /// <summary>All key sets worth trying, best first, de-duplicated by main key.</summary>
    public async Task<List<KeySet>> GetCandidatesAsync(FortniteVersion version, bool allowNetwork, CancellationToken ct)
    {
        var found = new List<KeySet>();
        if (allowNetwork)
        {
            if (version.MajorMinor is { } mm)
                Add(found, await TryFortnitePortingAsync(mm, ct).ConfigureAwait(false));
            if (version.BuildString is { } build)
                Add(found, await TryUedbAsync(build, ct).ConfigureAwait(false));
            Add(found, await TryFortnitePortingAsync("latest", ct).ConfigureAwait(false));
            Add(found, await TryUedbAsync(null, ct).ConfigureAwait(false));
            var fapi = await TryFortniteApiAsync(ct).ConfigureAwait(false);
            if (fapi != null)
            {
                // keys only: borrow the mapping of a set for the same build/version
                var donor = found.FirstOrDefault(k => k.UsmapPath != null && SameBuild(k, fapi));
                Add(found, donor == null ? fapi : fapi with { UsmapPath = donor.UsmapPath, UsmapUrl = donor.UsmapUrl, UsmapMd5 = donor.UsmapMd5 });
            }
        }
        else _log.Info("[fortnite] network disabled: using cached keys only");

        foreach (var cached in LoadCached())
            Add(found, cached);

        // matching version first (keeping source order otherwise)
        var ordered = found
            .Select((k, i) => (k, i))
            .OrderBy(t => Matches(t.k, version) ? 0 : 1)
            .ThenBy(t => t.i)
            .Select(t => t.k)
            .ToList();
        // fill a missing mapping from any set of the same build
        for (int i = 0; i < ordered.Count; i++)
            if (ordered[i].UsmapPath == null)
            {
                var donor = ordered.FirstOrDefault(k => k.UsmapPath != null && SameBuild(k, ordered[i]));
                if (donor != null) ordered[i] = ordered[i] with { UsmapPath = donor.UsmapPath, UsmapMd5 = donor.UsmapMd5 };
            }
        foreach (var k in ordered) _log.Info($"[fortnite] key candidate: {k.Describe()}");
        return ordered;
    }

    private static void Add(List<KeySet> list, KeySet? k)
    {
        if (k == null) return;
        var norm = NormalizeKey(k.MainKey);
        var existing = list.FindIndex(x => NormalizeKey(x.MainKey) == norm);
        if (existing < 0) { list.Add(k); return; }
        var e = list[existing];
        // merge: keep the first source, fill what it lacks
        list[existing] = e with
        {
            UsmapPath = e.UsmapPath ?? k.UsmapPath,
            UsmapUrl = e.UsmapUrl ?? k.UsmapUrl,
            UsmapMd5 = e.UsmapMd5 ?? k.UsmapMd5,
            DynamicKeys = e.DynamicKeys.Concat(k.DynamicKeys).GroupBy(d => d.Guid.ToUpperInvariant()).Select(g => g.First()).ToList(),
            Build = e.Build ?? k.Build,
            Version = e.Version ?? k.Version,
        };
    }

    public static string NormalizeKey(string key)
    {
        var k = key.Trim();
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) k = k[2..];
        return k.ToUpperInvariant();
    }

    public static bool IsValidKey(string? key) => key != null && Regex.IsMatch(NormalizeKey(key), "^[0-9A-F]{64}$");

    private static bool SameBuild(KeySet a, KeySet b) =>
        (a.Build != null && b.Build != null && FortniteVersion.Parse(a.Build).BuildString == FortniteVersion.Parse(b.Build).BuildString)
        || (a.Version != null && b.Version != null && a.Version == b.Version);

    private static bool Matches(KeySet k, FortniteVersion v) =>
        (v.MajorMinor != null && (k.Version == v.MajorMinor || (k.Build != null && FortniteVersion.Parse(k.Build).MajorMinor == v.MajorMinor)))
        || (v.BuildString != null && k.Build != null && FortniteVersion.Parse(k.Build).BuildString == v.BuildString);

    // ------------------------------------------------------------------ sources

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.Info($"[fortnite] GET {url}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length > 4 << 20) { _log.Warn($"[fortnite] GET {url}: response too large ({text.Length} chars)"); return null; }
            return JsonNode.Parse(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _log.Info($"[fortnite] GET {url}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>api.fortniteporting.app/v2/fortnite/versions/{version|latest}: keys + mapping URL (+md5), with history.</summary>
    internal async Task<KeySet?> TryFortnitePortingAsync(string version, CancellationToken ct)
    {
        var url = FortnitePortingBase + Uri.EscapeDataString(version);
        var j = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var ks = ParseFortnitePorting(j, url);
        if (ks == null) return null;
        ks = await WithUsmapAsync(ks, ct).ConfigureAwait(false);
        Save(ks);
        return ks;
    }

    internal static KeySet? ParseFortnitePorting(JsonNode? j, string source)
    {
        var main = j?["keys"]?["mainKey"]?["key"]?.GetValue<string>();
        if (!IsValidKey(main)) return null;
        var dyn = new List<DynamicKey>();
        if (j!["keys"]?["extraKeys"] is JsonArray extra)
            foreach (var e in extra)
            {
                var g = e?["guid"]?.GetValue<string>(); var k = e?["key"]?.GetValue<string>();
                if (g != null && IsValidKey(k) && Regex.IsMatch(g, "^[0-9A-Fa-f]{32}$")) dyn.Add(new DynamicKey(g, k!, e?["name"]?.GetValue<string>()));
            }
        var ver = j["version"]?.GetValue<string>();
        return new KeySet(source, ver, null, main!, dyn, null, j["mappings"]?["url"]?.GetValue<string>(),
            j["mappings"]?["md5Hash"]?.GetValue<string>(), DateTime.UtcNow);
    }

    /// <summary>uedb.dev: aes[/build] + mappings[/build] (FModel's default service).</summary>
    internal async Task<KeySet?> TryUedbAsync(string? build, CancellationToken ct)
    {
        var suffix = build == null ? "" : "/" + Uri.EscapeDataString(build);
        var aesUrl = UedbBase + "aes" + suffix;
        var aes = await GetJsonAsync(aesUrl, ct).ConfigureAwait(false);
        var ks = ParseUedbAes(aes, aesUrl);
        if (ks == null) return null;
        var map = await GetJsonAsync(UedbBase + "mappings" + suffix, ct).ConfigureAwait(false);
        ks = ks with { UsmapUrl = ParseUedbMappingsUrl(map) };
        ks = await WithUsmapAsync(ks, ct).ConfigureAwait(false);
        Save(ks);
        return ks;
    }

    internal static KeySet? ParseUedbAes(JsonNode? j, string source)
    {
        if (j?["error"] != null) return null;
        var main = j?["mainKey"]?.GetValue<string>();
        if (!IsValidKey(main)) return null;
        var dyn = new List<DynamicKey>();
        if (j!["dynamicKeys"] is JsonArray arr)
            foreach (var e in arr)
            {
                var g = e?["guid"]?.GetValue<string>(); var k = e?["key"]?.GetValue<string>();
                if (g != null && IsValidKey(k) && Regex.IsMatch(g, "^[0-9A-Fa-f]{32}$")) dyn.Add(new DynamicKey(g, k!, null));
            }
        var build = j["version"]?.GetValue<string>();
        return new KeySet(source, FortniteVersion.Parse(build).MajorMinor, build, main!, dyn, null, null, null, DateTime.UtcNow);
    }

    internal static string? ParseUedbMappingsUrl(JsonNode? j)
    {
        var m = j?["mappings"];
        return m?["ZStandard"]?.GetValue<string>() ?? m?["Brotli"]?.GetValue<string>() ?? m?["Uncompressed"]?.GetValue<string>();
    }

    /// <summary>fortnite-api.com/v2/aes: latest keys only (no mappings).</summary>
    internal async Task<KeySet?> TryFortniteApiAsync(CancellationToken ct)
    {
        var j = await GetJsonAsync(FortniteApiAes, ct).ConfigureAwait(false);
        var ks = ParseFortniteApi(j, FortniteApiAes);
        if (ks != null) Save(ks);
        return ks;
    }

    internal static KeySet? ParseFortniteApi(JsonNode? j, string source)
    {
        var d = j?["data"];
        var main = d?["mainKey"]?.GetValue<string>();
        if (!IsValidKey(main)) return null;
        var dyn = new List<DynamicKey>();
        if (d!["dynamicKeys"] is JsonArray arr)
            foreach (var e in arr)
            {
                var g = e?["pakGuid"]?.GetValue<string>(); var k = e?["key"]?.GetValue<string>();
                if (g != null && IsValidKey(k) && Regex.IsMatch(g, "^[0-9A-Fa-f]{32}$")) dyn.Add(new DynamicKey(g, k!, e?["pakFilename"]?.GetValue<string>()));
            }
        var build = d["build"]?.GetValue<string>();
        dyn = dyn.GroupBy(x => x.Guid.ToUpperInvariant()).Select(g => g.First()).ToList(); // .pak and .ucas share a GUID
        return new KeySet(source, FortniteVersion.Parse(build).MajorMinor, build, main!, dyn, null, null, null, DateTime.UtcNow);
    }

    // ------------------------------------------------------------------ mapping file

    private async Task<KeySet> WithUsmapAsync(KeySet ks, CancellationToken ct)
    {
        if (ks.UsmapUrl == null) return ks;
        var name = SafeName(Path.GetFileName(new Uri(ks.UsmapUrl).AbsolutePath));
        if (!name.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase)) name += ".usmap";
        var path = Path.Combine(_cacheDir, name);
        if (File.Exists(path) && UsmapLooksValid(path, ks.UsmapMd5, out _))
        {
            _log.Info($"[fortnite] mapping {name}: cached");
            return ks with { UsmapPath = path };
        }
        try
        {
            using var resp = await _http.GetAsync(ks.UsmapUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { _log.Info($"[fortnite] mapping {ks.UsmapUrl}: HTTP {(int)resp.StatusCode}"); return ks; }
            if (resp.Content.Headers.ContentLength is > MaxUsmapBytes) { _log.Warn("[fortnite] mapping too large"); return ks; }
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxUsmapBytes) { _log.Warn("[fortnite] mapping too large"); return ks; }
            }
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, ms.ToArray(), ct).ConfigureAwait(false);
            if (!UsmapLooksValid(tmp, ks.UsmapMd5, out var why))
            {
                File.Delete(tmp);
                _log.Warn($"[fortnite] mapping {ks.UsmapUrl} rejected: {why}");
                return ks;
            }
            File.Move(tmp, path, overwrite: true);
            _log.Info($"[fortnite] mapping {name}: downloaded {ms.Length} bytes");
            return ks with { UsmapPath = path };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            _log.Info($"[fortnite] mapping {ks.UsmapUrl}: {e.GetType().Name}: {e.Message}");
            return ks;
        }
    }

    /// <summary>A .usmap starts with magic 0x30C4 (little-endian); the md5 is checked when the source gave one.</summary>
    public static bool UsmapLooksValid(string path, string? md5, out string why)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length < 16) { why = "too small"; return false; }
        if (fi.Length > MaxUsmapBytes) { why = "too large"; return false; }
        using (var f = File.OpenRead(path))
        {
            int a = f.ReadByte(), b = f.ReadByte();
            if (a != 0xC4 || b != 0x30) { why = "not a .usmap (bad magic)"; return false; }
        }
        if (!string.IsNullOrWhiteSpace(md5))
        {
            using var f = File.OpenRead(path);
            var h = Convert.ToHexString(MD5.HashData(f));
            if (!h.Equals(md5.Trim(), StringComparison.OrdinalIgnoreCase)) { why = $"md5 {h} != {md5}"; return false; }
        }
        why = "";
        return true;
    }

    // ------------------------------------------------------------------ cache

    private static string SafeName(string s) => Regex.Replace(s, "[^A-Za-z0-9._+-]", "_");

    private void Save(KeySet ks)
    {
        try
        {
            var label = SafeName($"{(ks.Build ?? ks.Version ?? "unknown")}_{new Uri(ks.Source).Host}");
            var o = new JsonObject
            {
                ["source"] = ks.Source,
                ["version"] = ks.Version,
                ["build"] = ks.Build,
                ["mainKey"] = ks.MainKey,
                ["dynamicKeys"] = new JsonArray(ks.DynamicKeys.Select(d => (JsonNode)new JsonObject { ["guid"] = d.Guid, ["key"] = d.Key, ["container"] = d.Container }).ToArray()),
                ["usmap"] = ks.UsmapPath == null ? null : Path.GetFileName(ks.UsmapPath),
                ["usmapUrl"] = ks.UsmapUrl,
                ["usmapMd5"] = ks.UsmapMd5,
                ["fetchedUtc"] = ks.FetchedUtc.ToString("O"),
            };
            var path = Path.Combine(_cacheDir, label + ".json");
            File.WriteAllText(path + ".tmp", o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) { _log.Warn($"[fortnite] could not cache keys: {e.Message}"); }
    }

    internal IEnumerable<KeySet> LoadCached()
    {
        var list = new List<KeySet>();
        foreach (var f in Directory.EnumerateFiles(_cacheDir, "*.json"))
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(f));
                var main = j?["mainKey"]?.GetValue<string>();
                if (!IsValidKey(main)) continue;
                var dyn = new List<DynamicKey>();
                if (j!["dynamicKeys"] is JsonArray arr)
                    foreach (var e in arr)
                        if (e?["guid"]?.GetValue<string>() is { } g && e["key"]?.GetValue<string>() is { } k && IsValidKey(k))
                            dyn.Add(new DynamicKey(g, k, e["container"]?.GetValue<string>()));
                var usmapName = j["usmap"]?.GetValue<string>();
                string? usmap = usmapName == null ? null : Path.Combine(_cacheDir, Path.GetFileName(usmapName));
                if (usmap != null && !UsmapLooksValid(usmap, j["usmapMd5"]?.GetValue<string>(), out _)) usmap = null;
                DateTime.TryParse(j["fetchedUtc"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when);
                list.Add(new KeySet("cache:" + Path.GetFileName(f), j["version"]?.GetValue<string>(), j["build"]?.GetValue<string>(), main!, dyn,
                    usmap, j["usmapUrl"]?.GetValue<string>(), j["usmapMd5"]?.GetValue<string>(), when));
            }
            catch (Exception e) { _log.Warn($"[fortnite] ignoring cached key file {Path.GetFileName(f)}: {e.Message}"); }
        }
        return list.OrderByDescending(k => k.FetchedUtc);
    }
}
