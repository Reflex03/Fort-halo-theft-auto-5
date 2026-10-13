using System.Text;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract.E2E;

/// <summary>
/// Checks a finished cache folder against docs/CONTRACTS.md (cache layout, manifest.json, packs, .cthm, sounds.json,
/// stats) plus the extractor's documented additions (docs/requests/extractor.md section 3). Read-only.
/// </summary>
public sealed class CacheContract
{
    public List<string> Problems { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Facts { get; } = new();

    private static readonly string[] Games = { "halo", "gta", "fortnite" };
    private static readonly HashSet<string> RowStatuses = new() { "ok", "missing", "failed", "unimplemented" };
    private static readonly HashSet<string> GameStatuses = new() { "ok", "partial", "missing", "failed" };

    /// <summary>Top-level entries a cache may hold after a run (CONTRACTS + extractor additions).</summary>
    private static readonly HashSet<string> TopLevel = new()
    {
        "manifest.json", "progress.json", "extract.log", "extract.previous.log", "check.json", "check.log", ".lock", ".lock.info",
        "packs", "stats", "keys",
    };

    private void Bad(string m) => Problems.Add(m);

    public static CacheContract Verify(string cache, string sheetsDir)
    {
        var c = new CacheContract();
        c.Run(Path.GetFullPath(cache), sheetsDir);
        return c;
    }

    private void Run(string cache, string sheetsDir)
    {
        var layout = new CacheLayout(cache);
        if (!Directory.Exists(cache)) { Bad($"cache folder {cache} does not exist"); return; }

        // ---- top level
        foreach (var e in Directory.EnumerateFileSystemEntries(cache))
        {
            var name = Path.GetFileName(e);
            if (!TopLevel.Contains(name)) Bad($"unexpected top-level entry '{name}' (CONTRACTS cache layout)");
        }
        if (Directory.Exists(layout.StagingRoot)) Bad(".staging left behind after the run");

        // ---- manifest.json
        if (!File.Exists(layout.ManifestPath)) { Bad("manifest.json missing"); return; }
        var text = File.ReadAllText(layout.ManifestPath, Encoding.UTF8);
        if (!text.StartsWith("{\"cacheSchema\":1,")) Bad("manifest.json does not start with {\"cacheSchema\":1, (Melty's done marker)");
        if (!text.StartsWith("{\"cacheSchema\":1,\"extractor\":\"0.1.0\",\"status\":")) Bad("manifest.json does not start with {\"cacheSchema\":1,\"extractor\":\"0.1.0\",\"status\":");
        if (!text.Contains("\"cacheSchema\":1")) Bad("manifest.json lacks the literal \"cacheSchema\":1 that melty.json setup.done waits for");
        JsonObject m;
        try { m = (JsonObject)JsonNode.Parse(text)!; }
        catch (Exception e) { Bad($"manifest.json does not parse: {e.Message}"); return; }
        Facts.Add($"manifest: {text.Length} bytes, status {(string?)m["status"]}");
        string? top = (string?)m["status"];
        if (top is not ("ok" or "partial" or "failed")) Bad($"manifest status '{top}' not ok|partial|failed");
        if (m["finishedUtc"]?.GetValue<string>() is not { Length: > 0 }) Bad("manifest finishedUtc missing");
        var games = m["games"] as JsonObject;
        var assets = m["assets"] as JsonObject;
        if (games is null || assets is null) { Bad("manifest games/assets missing"); return; }
        foreach (var g in Games) if (!games.ContainsKey(g)) Bad($"manifest games lacks '{g}' (must always list all three)");
        foreach (var (k, _) in games) if (!Games.Contains(k)) Bad($"manifest games has unknown key '{k}'");

        // every sources row exactly once, consistent with its game
        var rows = Sources.All.ToDictionary(r => r.Id);
        foreach (var id in rows.Keys) if (!assets.ContainsKey(id)) Bad($"manifest assets lacks sources row '{id}'");
        foreach (var (id, _) in assets) if (!rows.ContainsKey(id)) Bad($"manifest assets has unknown id '{id}'");

        var okRows = new Dictionary<string, List<SourceRow>>();
        foreach (var g in Games) okRows[g] = new();
        foreach (var (id, node) in assets)
        {
            if (!rows.TryGetValue(id, out var row) || node is not JsonObject a) continue;
            var st = (string?)a["status"];
            var gshort = Manifest.GameShort(row.Game)!;
            if (st is null || !RowStatuses.Contains(st)) Bad($"{id}: status '{st}'");
            if ((string?)a["game"] is { } ag && ag != gshort) Bad($"{id}: game '{ag}', expected '{gshort}'");
            var outv = (string?)a["out"];
            if (st == "ok")
            {
                if (outv != row.Out) Bad($"{id}: ok but out '{outv}' != sources.out '{row.Out}'");
                okRows[gshort].Add(row);
            }
            else if (outv is not null) Bad($"{id}: {st} but out is set");
            if (st != "ok" && a["detail"]?.GetValue<string>() is not { Length: > 0 }) Bad($"{id}: {st} without a detail (every lookup must say what it tried)");
        }

        // game statuses agree with their rows
        foreach (var g in Games)
        {
            if (games[g] is not JsonObject gm) continue;
            var gs = (string?)gm["status"];
            if (gs is null || !GameStatuses.Contains(gs)) Bad($"games.{g}.status '{gs}'");
            var mine = rows.Values.Where(r => Manifest.GameShort(r.Game) == g).Select(r => (string?)assets[r.Id]?["status"]).ToList();
            int ok = mine.Count(s => s == "ok");
            string expect = ok == mine.Count ? "ok" : ok > 0 ? "partial" : mine.All(s => s == "missing") ? "missing" : "failed";
            if (gs != expect) Bad($"games.{g}.status '{gs}' but its rows say '{expect}'");
            if (gs == "ok" && gm["error"] is not null) Bad($"games.{g}: ok with an error");
            if (gs != "ok" && gm["error"]?.GetValue<string>() is not { Length: > 0 }) Bad($"games.{g}: {gs} without an error text");
            if (gm["fingerprint"] is not JsonArray fp) Bad($"games.{g}.fingerprint missing");
            else
                foreach (var e in fp)
                {
                    if (e is not JsonObject fe || fe["path"] is null || fe["size"] is null || fe["mtimeMs"] is null) { Bad($"games.{g}.fingerprint entry {e?.ToJsonString()} lacks path/size/mtimeMs"); continue; }
                    if (((string)fe["path"]!).Contains('\\')) Bad($"games.{g}.fingerprint path uses backslashes: {fe["path"]}");
                }
            Facts.Add($"{g}: {gs}, {ok}/{mine.Count} rows ok{(gm["error"] is { } err ? $" (error: {Trim(err.ToString(), 160)})" : "")}");
        }
        bool anyOk = okRows.Values.Any(l => l.Count > 0);
        string expectTop = Games.All(g => (string?)games[g]?["status"] == "ok") ? "ok" : anyOk ? "partial" : "failed";
        if (top != expectTop) Bad($"manifest status '{top}' but games/rows say '{expectTop}'");

        // ---- progress.json (finished)
        if (File.Exists(layout.ProgressPath))
        {
            try
            {
                var p = (JsonObject)JsonNode.Parse(File.ReadAllText(layout.ProgressPath))!;
                foreach (var k in new[] { "game", "step", "done", "total" }) if (!p.ContainsKey(k)) Bad($"progress.json lacks '{k}'");
                if (p["finished"]?.GetValue<bool>() != true) Bad("progress.json not finished:true after the run");
                if ((int?)p["done"] != (int?)p["total"]) Bad($"progress.json done {p["done"]} != total {p["total"]}");
            }
            catch (Exception e) { Bad($"progress.json does not parse: {e.Message}"); }
        }
        else Bad("progress.json missing");
        if (!File.Exists(layout.LogPath)) Bad("extract.log missing");

        // ---- packs
        if (Directory.Exists(layout.PacksDir))
            foreach (var d in Directory.EnumerateDirectories(layout.PacksDir))
                if (!Games.Contains(Path.GetFileName(d))) Bad($"packs/{Path.GetFileName(d)}: not a game pack (swap leftovers?)");
        foreach (var g in Games)
        {
            var pack = layout.PackDir(g);
            if (!Directory.Exists(pack))
            {
                if (okRows[g].Any(r => r.Kind != "stats")) Bad($"packs/{g} missing although rows are ok");
                continue;
            }
            CheckPack(g, pack, layout.AssetsDir(g), okRows[g], rows.Values.Where(r => Manifest.GameShort(r.Game) == g && r.Kind != "stats").ToList(), assets);
        }

        // ---- stats
        foreach (var row in rows.Values.Where(r => r.Kind == "stats"))
        {
            var path = Path.Combine(cache, row.Out);
            bool ok = okRows[Manifest.GameShort(row.Game)!].Contains(row);
            if (ok) CheckStats(row, path, sheetsDir);
            else if (File.Exists(path)) Warnings.Add($"{row.Out} exists although {row.Id} is not ok (left from an earlier run)");
        }

        // ---- nothing that looks like game data or a key anywhere outside keys/
        var gameExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".rpf", ".map", ".pak", ".utoc", ".ucas", ".fsb", ".awc", ".yft", ".ytd", ".ydr", ".uasset", ".uexp", ".ubulk", ".bk2", ".bin", ".dat", ".exe", ".dll" };
        foreach (var f in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(cache, f).Replace('\\', '/');
            if (gameExt.Contains(Path.GetExtension(f))) Bad($"{rel}: a game-format/binary file in the cache");
            if (rel.StartsWith("keys/") && Path.GetExtension(f) is not (".json" or ".usmap")) Bad($"{rel}: unexpected file in keys/");
            if (rel.StartsWith("packs/") && Path.GetExtension(f) is not (".cthm" or ".png" or ".ogg" or ".json" or ".mcmeta")) Bad($"{rel}: unexpected file type in a pack");
        }
    }

    private void CheckPack(string g, string pack, string assetsDir, List<SourceRow> ok, List<SourceRow> all, JsonObject assets)
    {
        var meta = Path.Combine(pack, "pack.mcmeta");
        if (!File.Exists(meta)) Bad($"packs/{g}/pack.mcmeta missing");
        else
        {
            try
            {
                var j = JsonNode.Parse(File.ReadAllText(meta))!;
                if ((int?)j["pack"]?["pack_format"] != 34) Bad($"packs/{g}/pack.mcmeta pack_format {(j["pack"]?["pack_format"])} != 34");
            }
            catch (Exception e) { Bad($"packs/{g}/pack.mcmeta: {e.Message}"); }
        }
        foreach (var row in all)
        {
            var path = Path.Combine(assetsDir, row.Out);
            bool isOk = ok.Contains(row);
            if (!isOk)
            {
                if (File.Exists(path)) Bad($"{row.Id}: {(string?)assets[row.Id]?["status"]} but {row.Out} is in packs/{g}");
                continue;
            }
            if (!File.Exists(path)) { Bad($"{row.Id}: ok but packs/{g}/assets/crafttheft/{row.Out} missing"); continue; }
            try
            {
                switch (row.Kind)
                {
                    case "mesh": CheckMesh(row, path, assetsDir); break;
                    case "icon": CheckPng(row.Id, path); break;
                    case "sound": CheckOgg(row.Id, path); break;
                    default: Bad($"{row.Id}: unknown kind {row.Kind}"); break;
                }
            }
            catch (Exception e) { Bad($"{row.Id}: {e.GetType().Name}: {e.Message}"); }
        }
        // sounds.json: exactly the ok sound rows, "<short>.<name>" with replace:true
        var sj = Path.Combine(assetsDir, "sounds.json");
        var okSounds = ok.Where(r => r.Kind == "sound").ToList();
        if (!File.Exists(sj)) { if (okSounds.Count > 0) Bad($"packs/{g}: sounds.json missing for {okSounds.Count} ok sound rows"); return; }
        var sounds = (JsonObject)JsonNode.Parse(File.ReadAllText(sj))!;
        var expected = okSounds.ToDictionary(ExtractContext.SoundEvent);
        foreach (var (ev, node) in sounds)
        {
            if (!ev.StartsWith(g + ".")) Bad($"packs/{g} sounds.json event '{ev}' is not '{g}.<name>'");
            if (!expected.TryGetValue(ev, out var row)) { Bad($"packs/{g} sounds.json lists '{ev}' which is not an ok sound row"); continue; }
            if (node?["replace"]?.GetValue<bool>() != true) Bad($"sounds.json {ev}: replace is not true");
            var name = (string?)node?["sounds"]?[0]?["name"];
            var want = $"crafttheft:{Path.ChangeExtension(row.Out, null)!.Substring("sounds/".Length)}";
            if (name != want) Bad($"sounds.json {ev}: sound '{name}' != '{want}'");
        }
        foreach (var ev in expected.Keys) if (!sounds.ContainsKey(ev)) Bad($"packs/{g} sounds.json lacks ok sound event '{ev}'");
        Facts.Add($"packs/{g}: {ok.Count(r => r.Kind == "mesh")} meshes, {ok.Count(r => r.Kind == "icon")} icons, {okSounds.Count} sounds");
    }

    private void CheckMesh(SourceRow row, string path, string assetsDir)
    {
        var raw = File.ReadAllBytes(path);
        if (!raw.AsSpan(0, 4).SequenceEqual("CTHM"u8) || BitConverter.ToUInt32(raw, 4) != 1) { Bad($"{row.Id}: not CTHM v1"); return; }
        var m = Cthm.Read(path);
        Validate.Mesh(m);
        int tris = m.TriangleCount;
        if (tris == 0) Bad($"{row.Id}: no triangles");
        if (row.MaxTris > 0 && tris > row.MaxTris) Bad($"{row.Id}: {tris} tris > maxTris {row.MaxTris}");
        foreach (var s in m.Submeshes)
            if (!File.Exists(Path.Combine(assetsDir, "textures", s.Material.Replace('/', Path.DirectorySeparatorChar) + ".png")))
                Warnings.Add($"{row.Id}: material texture textures/{s.Material}.png not in the pack");
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var s in m.Submeshes)
            for (int i = 0; i + 7 < s.Vertices.Count; i += 8)
            {
                minX = Math.Min(minX, s.Vertices[i]); maxX = Math.Max(maxX, s.Vertices[i]);
                minY = Math.Min(minY, s.Vertices[i + 1]); maxY = Math.Max(maxY, s.Vertices[i + 1]);
                minZ = Math.Min(minZ, s.Vertices[i + 2]); maxZ = Math.Max(maxZ, s.Vertices[i + 2]);
            }
        string size = $"{maxX - minX:F2} x {maxY - minY:F2} x {maxZ - minZ:F2} m";
        bool vehicle = row.Id.StartsWith("gta_mesh_") || row.Id == "fn_mesh_battle_bus" || row.Id == "fn_mesh_chest";
        if (vehicle)
        {
            float cx = (minX + maxX) / 2, cz = (minZ + maxZ) / 2;
            if (Math.Abs(minY) > 0.01f) Bad($"{row.Id}: lowest point y = {minY:F3}, contract says 0");
            if (Math.Abs(cx) > 0.02f * (maxX - minX) + 0.01f || Math.Abs(cz) > 0.02f * (maxZ - minZ) + 0.01f) Bad($"{row.Id}: not centred in X/Z (centre {cx:F3}, {cz:F3})");
        }
        if (row.Id.StartsWith("gta_mesh_"))
            foreach (var w in new[] { "wheel_lf", "wheel_rf", "wheel_lb", "wheel_rb" })
                if (!m.Nodes.Any(n => n.Name == w)) Bad($"{row.Id}: node {w} missing");
        Facts.Add($"{row.Id}: {tris} tris, {m.Submeshes.Count} submeshes, nodes [{string.Join(",", m.Nodes.Select(n => n.Name))}], {size}");
    }

    private void CheckPng(string id, string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 33 || !b.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) { Bad($"{id}: not a PNG"); return; }
        int w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
        if (w is < 1 or > 4096 || h is < 1 or > 4096) Bad($"{id}: PNG {w}x{h}");
        Facts.Add($"{id}: PNG {w}x{h}");
    }

    private void CheckOgg(string id, string path)
    {
        var b = File.ReadAllBytes(path);
        if (!Ogg.LooksLikeVorbis(b)) { Bad($"{id}: not Ogg Vorbis"); return; }
        using var r = new NVorbis.VorbisReader(new MemoryStream(b), true);
        var buf = new float[4096 * r.Channels];
        long frames = 0; int n; float peak = 0;
        while ((n = r.ReadSamples(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) { if (!float.IsFinite(buf[i])) { Bad($"{id}: non-finite samples"); return; } peak = Math.Max(peak, Math.Abs(buf[i])); }
            frames += n / r.Channels;
        }
        if (frames == 0) Bad($"{id}: decodes to no samples");
        if (peak < 1e-4) Warnings.Add($"{id}: silent");
        Facts.Add($"{id}: Ogg Vorbis {r.Channels}ch {r.SampleRate} Hz {frames / (double)r.SampleRate:F2} s peak {peak:F2}");
    }

    private void CheckStats(SourceRow row, string path, string sheetsDir)
    {
        if (!File.Exists(path)) { Bad($"{row.Id}: ok but {row.Out} missing"); return; }
        var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (o is null || o.Count == 0) { Bad($"{row.Id}: {row.Out} is not a non-empty object"); return; }
        if (row.Id == "halo_stats_shield")
        {
            var s = o["shield"] as JsonObject;
            if (o.Count != 1 || s is null) Bad($"{row.Out}: must be {{\"shield\":{{...}}}}");
            else foreach (var k in new[] { "max", "rechargeDelayTicks", "rechargePerTick" }) if (s[k] is null) Bad($"{row.Out}: shield.{k} missing");
        }
        else if (row.Id == "halo_stats_weapons")
        {
            var sheet = JsonNode.Parse(File.ReadAllText(Path.Combine(sheetsDir, "weapons.json")))!;
            var cols = ((JsonObject)sheet["columns"]!).Select(kv => kv.Key).ToHashSet();
            var ids = ((JsonArray)sheet["rows"]!).Select(r => (string)r!["id"]!).ToHashSet();
            foreach (var (w, f) in o)
            {
                if (!ids.Contains(w)) Bad($"{row.Out}: unknown weapon '{w}'");
                if (f is JsonObject fo) foreach (var (k, _) in fo) if (!cols.Contains(k)) Bad($"{row.Out}: {w}.{k} is not a weapons sheet column");
            }
        }
        Facts.Add($"{row.Id}: {row.Out} with {o.Count} entries");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
