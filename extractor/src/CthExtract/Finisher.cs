using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract;

/// <summary>
/// Turns one game's staging folder into the final pack: checks every file a row reported as written, keeps the
/// previous run's good file for rows that did not come out this time, fixes sounds.json and pack.mcmeta, then swaps
/// the pack into cache/packs/&lt;short&gt; and the stats files into cache/stats.
/// </summary>
internal sealed class Finisher(CacheLayout layout, Log log, Manifest? previous, bool keep)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string StageAssets(string stageDir) => Path.Combine(stageDir, "pack", "assets", ExtractContext.Namespace);

    /// <summary>Final per-row entries for this game, after output checks and carry-over. Never throws for a single row.</summary>
    public List<ManifestAsset> Finalize(GameSpec spec, string stageDir, IReadOnlyDictionary<string, AssetResult> results)
    {
        var rows = Sources.ForGame(spec.Slug).ToList();
        var stageAssets = StageAssets(stageDir);
        var oldAssets = layout.AssetsDir(spec.Short);
        Directory.CreateDirectory(stageAssets);
        var entries = new List<ManifestAsset>();

        foreach (var row in rows)
        {
            ManifestAsset entry;
            try
            {
                var r = results.TryGetValue(row.Id, out var rr) ? rr
                    : new AssetResult(row.Id, AssetStatus.Unimplemented, "not reported by the module", null);
                var status = r.Status;
                var detail = r.Detail;
                if (status == AssetStatus.Ok)
                {
                    var problem = Verify(row, stageAssets, stageDir, out var warning);
                    if (problem is not null)
                    {
                        log.Error($"[{spec.Short}] {row.Id}: reported ok but its output is bad: {problem}");
                        DeleteOutput(row, stageAssets, stageDir);
                        status = AssetStatus.Failed;
                        detail = $"output check failed: {problem} (module said: {r.Detail})";
                    }
                    else if (warning is not null)
                    {
                        log.Warn($"[{spec.Short}] {row.Id}: {warning}");
                        detail = detail is null ? warning : $"{detail}; {warning}";
                    }
                }
                entry = new ManifestAsset(row.Id, spec.Short, status, detail, status == AssetStatus.Ok ? row.Out : null);
                if (status != AssetStatus.Ok && keep && TryKeep(spec, row, stageAssets, oldAssets, stageDir, entry) is { } kept)
                    entry = kept;
            }
            catch (Exception e)
            {
                log.Error($"[{spec.Short}] {row.Id}: finishing failed: {e}");
                entry = new ManifestAsset(row.Id, spec.Short, AssetStatus.Failed, $"finishing failed: {e.GetType().Name}: {e.Message}", null);
            }
            entries.Add(entry);
        }

        FixSounds(spec, rows, entries, stageAssets, oldAssets);
        if (!File.Exists(Path.Combine(stageDir, "pack", "pack.mcmeta"))) ExtractContext.WritePackMeta(Path.Combine(stageDir, "pack"), spec.Short);
        SafeFs.TryDeleteDirectory(Path.Combine(stageDir, "work"));
        return entries;
    }

    // ------------------------------------------------------------------ output checks

    /// <summary>Null when the row's file is there and well-formed; otherwise what is wrong.</summary>
    internal static string? Verify(SourceRow row, string assetsDir, string statsBase, out string? warning)
    {
        warning = null;
        bool isStats = row.Out.StartsWith("stats/", StringComparison.Ordinal);
        var path = isStats ? Path.Combine(statsBase, row.Out) : Path.Combine(assetsDir, row.Out);
        var fi = new FileInfo(path);
        if (!fi.Exists) return $"{row.Out} was not written";
        if (fi.Length == 0) return $"{row.Out} is empty";
        switch (Path.GetExtension(row.Out).ToLowerInvariant())
        {
            case ".cthm":
                {
                    MeshData m;
                    try { m = Cthm.Read(path); }
                    catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { return $"{row.Out} is not a valid .cthm ({e.Message})"; }
                    try { Validate.Mesh(m); } catch (InvalidDataException e) { return $"{row.Out}: {e.Message}"; }
                    if (m.TriangleCount == 0) return $"{row.Out} has no triangles";
                    if (row.MaxTris > 0 && m.TriangleCount > row.MaxTris) return $"{row.Out} has {m.TriangleCount} triangles, budget {row.MaxTris}";
                    var missing = m.Submeshes.Select(s => s.Material).Distinct()
                        .Where(mat => !File.Exists(MaterialPath(assetsDir, mat))).ToList();
                    if (missing.Count > 0) warning = $"{missing.Count} material texture(s) missing: {string.Join(", ", missing.Take(4))}";
                    return null;
                }
            case ".png":
                {
                    var head = ReadHead(path, 24);
                    if (head.Length < 24 || !head.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
                        return $"{row.Out} is not a PNG";
                    int w = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(16)), h = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(20));
                    if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return $"{row.Out} has odd size {w}x{h}";
                    return null;
                }
            case ".ogg":
                return Ogg.LooksLikeVorbis(ReadHead(path, 4096)) ? null : $"{row.Out} is not an Ogg Vorbis stream";
            case ".json":
                try
                {
                    using var s = SafeFs.OpenShared(path);
                    return JsonNode.Parse(s) is JsonObject { Count: > 0 } ? null : $"{row.Out} is not a non-empty JSON object";
                }
                catch (JsonException e) { return $"{row.Out} is not valid JSON ({e.Message})"; }
            default:
                return null;
        }
    }

    /// <summary>textures/&lt;material&gt;.png under a pack's assets folder. Material names are read back from .cthm files
    /// (the previous run's, for the keep policy), so a damaged or edited file must not steer a copy outside the pack:
    /// rooted names, backslashes, drive letters and "." / ".." folder segments are refused (an empty path is returned,
    /// which never exists).</summary>
    internal static string MaterialPath(string assetsDir, string material)
    {
        if (!SafeMaterial(material)) return "";
        return Path.Combine(assetsDir, "textures", material.Replace('/', Path.DirectorySeparatorChar) + ".png");
    }

    internal static bool SafeMaterial(string material)
    {
        if (string.IsNullOrEmpty(material) || material.Length > 512 || material[0] == '/' || material.Contains('\\') || material.Contains(':')
            || material.Any(char.IsControl))
            return false;
        var parts = material.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i] is "" or "." or "..") return false;
        return parts[^1].Length > 0;
    }

    private static byte[] ReadHead(string path, int n)
    {
        using var s = SafeFs.OpenShared(path);
        var b = new byte[Math.Min(n, s.Length)];
        s.ReadExactly(b);
        return b;
    }

    private static void DeleteOutput(SourceRow row, string assetsDir, string statsBase)
    {
        bool isStats = row.Out.StartsWith("stats/", StringComparison.Ordinal);
        SafeFs.TryDeleteFile(isStats ? Path.Combine(statsBase, row.Out) : Path.Combine(assetsDir, row.Out));
    }

    // ------------------------------------------------------------------ carry-over

    /// <summary>When this run did not produce a row but the previous run did (and its file still checks out), copy
    /// the previous file (and a mesh's material textures) into the staging pack. Returns the kept entry or null.</summary>
    private ManifestAsset? TryKeep(GameSpec spec, SourceRow row, string stageAssets, string oldAssets, string stageDir, ManifestAsset now)
    {
        if (previous is null || !previous.Assets.TryGetValue(row.Id, out var old) || old.Status != AssetStatus.Ok) return null;
        bool isStats = row.Out.StartsWith("stats/", StringComparison.Ordinal);
        if (Verify(row, oldAssets, layout.Root, out _) is { } problem)
        {
            log.Info($"[{spec.Short}] {row.Id}: previous file not reusable ({problem})");
            return null;
        }
        var from = isStats ? Path.Combine(layout.Root, row.Out) : Path.Combine(oldAssets, row.Out);
        var to = isStats ? Path.Combine(stageDir, row.Out) : Path.Combine(stageAssets, row.Out);
        SafeFs.CopyFile(from, to);
        int textures = 0;
        if (row.Out.EndsWith(".cthm", StringComparison.OrdinalIgnoreCase))
            foreach (var mat in Cthm.Read(from).Submeshes.Select(s => s.Material).Distinct())
            {
                var src = MaterialPath(oldAssets, mat);
                if (src.Length == 0) { log.Warn($"[{spec.Short}] {row.Id}: previous mesh names an unsafe material '{mat}'; not copied"); continue; }
                if (!File.Exists(src)) continue;
                SafeFs.CopyFile(src, MaterialPath(stageAssets, mat)); // the old mesh's UVs belong to the old textures
                textures++;
            }
        var detail = $"kept from the previous run ({old.Detail}); this run: {StatusNames.Of(now.Status)}: {now.Detail}";
        log.Info($"[{spec.Short}] {row.Id}: kept the previous file{(textures > 0 ? $" and {textures} texture(s)" : "")}; this run: {StatusNames.Of(now.Status)}: {now.Detail}");
        return new ManifestAsset(row.Id, spec.Short, AssetStatus.Ok, detail, row.Out, Kept: true);
    }

    /// <summary>sounds.json lists exactly the sound rows that are ok (kept rows reuse their previous entry).</summary>
    private void FixSounds(GameSpec spec, List<SourceRow> rows, List<ManifestAsset> entries, string stageAssets, string oldAssets)
    {
        var path = Path.Combine(stageAssets, "sounds.json");
        var sounds = ReadJsonObject(path) ?? new JsonObject();
        var oldSounds = ReadJsonObject(Path.Combine(oldAssets, "sounds.json"));
        bool changed = false;
        foreach (var row in rows.Where(r => r.Out.StartsWith("sounds/", StringComparison.Ordinal)))
        {
            var e = entries.First(x => x.Id == row.Id);
            var ev = ExtractContext.SoundEvent(row);
            if (e.Status != AssetStatus.Ok)
            {
                changed |= sounds.Remove(ev);
                continue;
            }
            if (e.Kept || sounds[ev] is null)
            {
                sounds[ev] = (e.Kept ? oldSounds?[ev]?.DeepClone() : null) ?? ExtractContext.SoundJson(row);
                changed = true;
            }
        }
        if (sounds.Count == 0) { SafeFs.TryDeleteFile(path); return; }
        if (changed || !File.Exists(path)) SafeFs.AtomicWriteText(path, sounds.ToJsonString(Indented));
        log.Info($"[{spec.Short}] sounds.json: {sounds.Count} event(s)");
    }

    private static JsonObject? ReadJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var s = SafeFs.OpenShared(path);
            return JsonNode.Parse(s) as JsonObject;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ swap

    /// <summary>Move stage/pack to packs/&lt;short&gt; (rename; old pack renamed aside first and removed after) and the
    /// game's stats files into stats/. Returns null on success, else why the old pack was left in place.</summary>
    public string? Swap(GameSpec spec, string stageDir, List<ManifestAsset> entries)
    {
        var stagePack = Path.Combine(stageDir, "pack");
        var final = layout.PackDir(spec.Short);
        Directory.CreateDirectory(layout.PacksDir);
        var aside = Path.Combine(layout.PacksDir, $".old-{spec.Short}-{Environment.ProcessId}");
        SafeFs.TryDeleteDirectory(aside);
        string? packError = null;
        try
        {
            if (Directory.Exists(final)) SafeFs.MoveDirectory(final, aside);
            try { SafeFs.MoveDirectory(stagePack, final); }
            catch
            {
                if (Directory.Exists(aside) && !Directory.Exists(final)) SafeFs.MoveDirectory(aside, final);
                throw;
            }
            if (!SafeFs.TryDeleteDirectory(aside)) log.Warn($"[{spec.Short}] could not remove {aside}; it is removed on the next run");
            log.Info($"[{spec.Short}] pack swapped into {final}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[{spec.Short}] folder swap failed ({e.Message}); copying files over the old pack instead");
            try { CopyOver(stagePack, final); log.Info($"[{spec.Short}] pack copied into {final}"); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
            {
                packError = $"could not replace {final}: {e2.Message}";
                log.Error($"[{spec.Short}] {packError}");
                return packError;
            }
        }

        // stats files: ok rows replace cache/stats/x.json, other rows remove a stale one
        foreach (var row in Sources.ForGame(spec.Slug).Where(r => r.Out.StartsWith("stats/", StringComparison.Ordinal)))
        {
            var e = entries.First(x => x.Id == row.Id);
            var src = Path.Combine(stageDir, row.Out);
            var dst = Path.Combine(layout.Root, row.Out);
            try
            {
                if (e.Status == AssetStatus.Ok && File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    for (int i = 0; ; i++)
                    {
                        try { File.Move(src, dst, overwrite: true); break; }
                        catch (IOException) when (i < 10) { Thread.Sleep(100 * (i + 1)); }
                    }
                }
                else if (e.Status != AssetStatus.Ok) SafeFs.TryDeleteFile(dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Error($"[{spec.Short}] could not replace {dst}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>Fallback when the pack folder cannot be renamed (a file in it is open): overwrite file by file and
    /// remove files the new pack does not have.</summary>
    private static void CopyOver(string from, string to)
    {
        Directory.CreateDirectory(to);
        var keepSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, f);
            keepSet.Add(rel);
            SafeFs.CopyFile(f, Path.Combine(to, rel));
        }
        foreach (var f in Directory.EnumerateFiles(to, "*", SearchOption.AllDirectories).ToList())
            if (!keepSet.Contains(Path.GetRelativePath(to, f))) SafeFs.TryDeleteFile(f);
    }
}
