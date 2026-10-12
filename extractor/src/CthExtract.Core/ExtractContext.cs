using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CthExtract.Core.Generated;

namespace CthExtract.Core;

/// <summary>
/// Where a game module puts what it read. Writes into a staging pack folder for one game; the caller swaps it into
/// cache/packs/&lt;short&gt; when the game is done. Thread-safe for simple parallel use within one game.
/// </summary>
public sealed class ExtractContext
{
    public const string Namespace = "crafttheft";

    private readonly object _lock = new();
    private readonly Dictionary<string, AssetResult> _results = new();
    private readonly JsonObject _sounds = new();
    private readonly Dictionary<string, JsonObject> _stats = new();

    public string Game { get; }
    public string Short { get; }
    public string PackDir { get; }
    public string StatsDir { get; }
    public string WorkDir { get; }
    public Log Log { get; }

    /// <summary>Called with (done, total, message) as rows complete; written to progress.json by the caller.</summary>
    public Action<string>? Progress { get; set; }

    public ExtractContext(string game, string shortName, string packDir, string statsDir, string workDir, Log log)
    {
        Game = game; Short = shortName; PackDir = packDir; StatsDir = statsDir; WorkDir = workDir; Log = log;
        Directory.CreateDirectory(AssetsDir);
        Directory.CreateDirectory(StatsDir);
        Directory.CreateDirectory(WorkDir);
    }

    public string AssetsDir => Path.Combine(PackDir, "assets", Namespace);

    public IReadOnlyList<SourceRow> Rows => Sources.ForGame(Game).ToList();

    public SourceRow Row(string id) =>
        Sources.All.FirstOrDefault(r => r.Id == id) ?? throw new ArgumentException($"unknown source id {id}");

    public IReadOnlyDictionary<string, AssetResult> Results { get { lock (_lock) return new Dictionary<string, AssetResult>(_results); } }

    private void Record(AssetResult r)
    {
        lock (_lock) _results[r.Id] = r;
        Log.Info($"[{Short}] {r.Id}: {r.Status}{(r.Detail is null ? "" : " - " + r.Detail)}");
        Progress?.Invoke($"{r.Id}: {r.Status}");
    }

    /// <summary>Write a mesh for a sources row. Decimates to the row's maxTris. Textures referenced by its
    /// materials must be written with <see cref="Texture"/>.</summary>
    public void Mesh(string id, MeshData mesh, string? detail = null)
    {
        var row = Row(id);
        if (mesh.TriangleCount == 0) { Failed(id, "mesh has no triangles"); return; }
        int before = mesh.TriangleCount;
        if (row.MaxTris > 0 && before > row.MaxTris) mesh = Decimate.ToBudget(mesh, row.MaxTris);
        Validate.Mesh(mesh);
        var path = Path.Combine(AssetsDir, row.Out);
        Cthm.Write(path, mesh);
        Record(new AssetResult(id, AssetStatus.Ok, $"{mesh.TriangleCount} tris (from {before}){(detail is null ? "" : "; " + detail)}", row.Out));
    }

    /// <summary>Write a texture used by a mesh material ("halo/assault_rifle/0" → textures/halo/assault_rifle/0.png).
    /// Not a sources row by itself; no result recorded.</summary>
    public void MaterialTexture(string material, Rgba image, int maxSize = 512)
    {
        var path = Path.Combine(AssetsDir, "textures", material.Replace('/', Path.DirectorySeparatorChar) + ".png");
        Png.Write(path, Images.FitPowerOfTwo(image, maxSize));
    }

    /// <summary>Write an icon/texture sources row (out is a path under assets/crafttheft/).</summary>
    public void Texture(string id, Rgba image, int maxSize = 256, string? detail = null)
    {
        var row = Row(id);
        var path = Path.Combine(AssetsDir, row.Out);
        Png.Write(path, Images.FitPowerOfTwo(image, maxSize));
        Record(new AssetResult(id, AssetStatus.Ok, $"{image.Width}x{image.Height}{(detail is null ? "" : "; " + detail)}", row.Out));
    }

    /// <summary>Write a sound sources row from Ogg Vorbis bytes and register it in this pack's sounds.json.
    /// The sound event is "crafttheft:&lt;game short&gt;.&lt;file name&gt;", e.g. crafttheft:halo.assault_rifle_fire.</summary>
    public void Sound(string id, byte[] oggVorbis, string? detail = null)
    {
        var row = Row(id);
        if (!Ogg.LooksLikeVorbis(oggVorbis)) { Failed(id, "not an Ogg Vorbis stream"); return; }
        var path = Path.Combine(AssetsDir, row.Out);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, oggVorbis);
        var ev = SoundEvent(row);
        var soundName = $"{Namespace}:{Path.ChangeExtension(row.Out, null)!.Replace('\\', '/').Substring("sounds/".Length)}";
        lock (_lock)
        {
            _sounds[ev] = new JsonObject
            {
                ["replace"] = true,
                ["sounds"] = new JsonArray(new JsonObject { ["name"] = soundName, ["stream"] = false })
            };
        }
        Record(new AssetResult(id, AssetStatus.Ok, $"{oggVorbis.Length} bytes{(detail is null ? "" : "; " + detail)}", row.Out));
    }

    /// <summary>Sound event name (without namespace) for a sound row: "halo.assault_rifle_fire".</summary>
    public static string SoundEvent(SourceRow row)
    {
        var rel = Path.ChangeExtension(row.Out, null)!.Replace('\\', '/');
        if (!rel.StartsWith("sounds/")) throw new ArgumentException($"sound row {row.Id} out must start with sounds/");
        return rel.Substring("sounds/".Length).Replace('/', '.');
    }

    /// <summary>Merge stats for a stats row (out "stats/x.json"); key = e.g. weapon id, value = fields read from the game.</summary>
    public void Stats(string id, string key, JsonObject fields)
    {
        var row = Row(id);
        lock (_lock)
        {
            if (!_stats.TryGetValue(row.Out, out var doc)) _stats[row.Out] = doc = new JsonObject();
            doc[key] = fields;
        }
    }

    /// <summary>Mark a stats row finished (after all Stats() calls for it).</summary>
    public void StatsDone(string id, string? detail = null) =>
        Record(new AssetResult(id, AssetStatus.Ok, detail, Row(id).Out));

    public void Missing(string id, string why) => Record(new AssetResult(id, AssetStatus.Missing, why, null));
    public void Failed(string id, string why) => Record(new AssetResult(id, AssetStatus.Failed, why, null));

    /// <summary>Write pack.mcmeta, sounds.json and stats files; mark unreported rows unimplemented.</summary>
    public void Finish()
    {
        foreach (var r in Rows)
            lock (_lock)
                if (!_results.ContainsKey(r.Id))
                    _results[r.Id] = new AssetResult(r.Id, AssetStatus.Unimplemented, "not reported by the module", null);

        Directory.CreateDirectory(PackDir);
        File.WriteAllText(Path.Combine(PackDir, "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":34,\"description\":\"Craft Theft Halo: content read from your own " + Short + " install\"}}",
            new UTF8Encoding(false));
        lock (_lock)
        {
            if (_sounds.Count > 0)
                File.WriteAllText(Path.Combine(AssetsDir, "sounds.json"), _sounds.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            foreach (var (rel, doc) in _stats)
            {
                var p = Path.Combine(Path.GetDirectoryName(StatsDir)!, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
        }
    }
}
