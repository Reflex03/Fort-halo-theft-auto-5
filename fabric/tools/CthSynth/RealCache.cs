using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthSynth;

/// <summary>
/// Takes rows from a cache written by the REAL extractor (cthextract on the synthetic game folders of
/// extractor/tests/e2e.sh: real Halo sounds from a synthetic FSB bank, real GTA cars/siren/horn/star from synthetic
/// archives) instead of drawing them here, so the mod is tested on the real extractor's files wherever one exists.
/// Enabled with env CTH_SYNTH_REAL_CACHE=&lt;cache dir&gt;. Rows the real run did not produce stay synthetic.
/// Everything is re-emitted through the extractor's own ExtractContext writers (meshes re-validated, sounds re-registered).
/// </summary>
public sealed class RealCache
{
    private readonly string _root;
    private readonly Manifest _manifest;
    private readonly Log _log;

    private RealCache(string root, Manifest m, Log log) { _root = root; _manifest = m; _log = log; }

    public static RealCache? Load(string? dir, Log log)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var m = Manifest.TryRead(dir, out var problem);
        if (m is null) { log.Warn($"CTH_SYNTH_REAL_CACHE={dir}: no usable manifest ({problem}); all rows synthetic"); return null; }
        var ok = m.Assets.Values.Where(a => a.Status == AssetStatus.Ok).Select(a => a.Id).ToList();
        log.Info($"real extractor cache {dir} (extractor {m.Extractor}, {m.FinishedUtc}): {ok.Count} ok rows [{string.Join(", ", ok)}]");
        return new RealCache(Path.GetFullPath(dir), m, log);
    }

    /// <summary>Re-emit the real row into <paramref name="ctx"/>; true when it did. Icons are copied byte for byte
    /// (Core has no PNG reader), so their result is returned in <paramref name="extra"/> for the manifest.</summary>
    public bool TryWrite(ExtractContext ctx, SourceRow row, out AssetResult? extra)
    {
        extra = null;
        if (!_manifest.Assets.TryGetValue(row.Id, out var a) || a.Status != AssetStatus.Ok) return false;
        string assets = Path.Combine(_root, "packs", ctx.Short, "assets", ExtractContext.Namespace);
        string detail = "REAL extractor output (synthetic game folder, extractor/tests/e2e.sh): " + (a.Detail ?? "");
        try
        {
            switch (row.Kind)
            {
                case "mesh":
                {
                    var mesh = Cthm.Read(Path.Combine(assets, row.Out));
                    foreach (var mat in mesh.Submeshes.Select(s => s.Material).Distinct())
                    {
                        var rel = Path.Combine("textures", mat.Replace('/', Path.DirectorySeparatorChar) + ".png");
                        var src = Path.Combine(assets, rel);
                        if (!File.Exists(src)) { _log.Warn($"{row.Id}: real material texture {rel} missing"); continue; }
                        var dst = Path.Combine(ctx.AssetsDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst, true);
                    }
                    ctx.Mesh(row.Id, mesh, detail);
                    return true;
                }
                case "sound":
                    ctx.Sound(row.Id, File.ReadAllBytes(Path.Combine(assets, row.Out)), detail);
                    return true;
                case "icon":
                {
                    var dst = Path.Combine(ctx.AssetsDir, row.Out);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(Path.Combine(assets, row.Out), dst, true);
                    extra = new AssetResult(row.Id, AssetStatus.Ok, detail, row.Out);
                    _log.Info($"[{ctx.Short}] {row.Id}: Ok - {detail}");
                    return true;
                }
                case "stats":
                {
                    var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_root, row.Out)))!;
                    foreach (var (k, v) in doc.ToList()) ctx.Stats(row.Id, k, (JsonObject)v!.DeepClone());
                    ctx.StatsDone(row.Id, detail);
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            _log.Warn($"{row.Id}: real cache row unusable ({e.GetType().Name}: {e.Message}); writing the synthetic one");
        }
        return false;
    }
}
