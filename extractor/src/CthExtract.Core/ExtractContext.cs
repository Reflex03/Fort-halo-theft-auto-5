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

    /// <summary>The cache root (the folder holding manifest.json, packs/, stats/), for module-private files that must
    /// outlive one run, such as cache/keys. The extractor sets it; when constructed without it, it is the parent of
    /// StatsDir if that folder is named "stats", else WorkDir.</summary>
    public string CacheDir { get; init; }

    /// <summary>Called with a short message as rows complete or a module reports a step; the extractor turns it into
    /// progress.json (done/total = rows reported so far / rows of this game).</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>Called after every row result is recorded (Ok / Missing / Failed), on the reporting thread. The extractor
    /// streams these to its supervisor so results survive a crash later in the module.</summary>
    public Action<AssetResult>? ResultRecorded { get; set; }

    public ExtractContext(string game, string shortName, string packDir, string statsDir, string workDir, Log log)
    {
        Game = game; Short = shortName; PackDir = packDir; StatsDir = statsDir; WorkDir = workDir; Log = log;
        var stats = Path.GetFullPath(statsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        CacheDir = string.Equals(Path.GetFileName(stats), "stats", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(stats)! : workDir;
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
        ResultRecorded?.Invoke(r);
        Progress?.Invoke($"{r.Id}: {r.Status}");
    }

    /// <summary>Write a mesh for a sources row. Decimates to the row's maxTris. Textures referenced by its
    /// materials must be written with <see cref="Texture"/>.</summary>
    public void Mesh(string id, MeshData mesh, string? detail = null)
    {
        var row = Row(id);
        if (mesh.TriangleCount == 0) { Failed(id, "mesh has no triangles"); return; }
        int before = mesh.TriangleCount;
        string how = "";
        if (row.MaxTris > 0 && before > row.MaxTris)
        {
            mesh = Decimate.ToBudget(mesh, row.MaxTris, out var method);
            how = ", " + method;
            if (mesh.TriangleCount == 0) { Failed(id, $"decimation to {row.MaxTris} left no triangles (from {before})"); return; }
        }
        Validate.Mesh(mesh);
        var path = Path.Combine(AssetsDir, row.Out);
        Cthm.Write(path, mesh);
        Record(new AssetResult(id, AssetStatus.Ok, $"{mesh.TriangleCount} tris (from {before}{how}){(detail is null ? "" : "; " + detail)}", row.Out));
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
        lock (_lock) _sounds[ev] = SoundJson(row);
        Record(new AssetResult(id, AssetStatus.Ok, $"{oggVorbis.Length} bytes{(detail is null ? "" : "; " + detail)}", row.Out));
    }

    /// <summary>The sounds.json entry for a sound row: replaces the jar's placeholder event with the extracted file.</summary>
    public static JsonObject SoundJson(SourceRow row)
    {
        var soundName = $"{Namespace}:{Path.ChangeExtension(row.Out, null)!.Replace('\\', '/').Substring("sounds/".Length)}";
        return new JsonObject
        {
            ["replace"] = true,
            ["sounds"] = new JsonArray(new JsonObject { ["name"] = soundName, ["stream"] = false })
        };
    }

    /// <summary>Write a pack's pack.mcmeta (pack_format 34 = Minecraft 1.21.1).</summary>
    public static void WritePackMeta(string packDir, string shortName)
    {
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":34,\"description\":\"Craft Theft Halo: content read from your own " + shortName + " install\"}}",
            new UTF8Encoding(false));
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

        WritePackMeta(PackDir, Short);
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
