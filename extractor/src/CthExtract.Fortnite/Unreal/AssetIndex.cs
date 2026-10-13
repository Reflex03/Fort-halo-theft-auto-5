using CthExtract.Core;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.AssetRegistry;

namespace CthExtract.Fortnite.Unreal;

/// <summary>One asset from a cooked asset registry (or synthesised from a package path when no registry lists it).</summary>
public sealed record AssetEntry(string PackageName, string AssetName, string AssetClass, IReadOnlyDictionary<string, string> Tags, string From)
{
    public string ObjectPath => $"{PackageName}.{AssetName}";
    public string? Tag(string name) => Tags.TryGetValue(name, out var v) ? v : null;
    public override string ToString() => $"{AssetClass} {ObjectPath}";
}

/// <summary>
/// Everything the recipes can search: the cooked asset registries (class + tags, no package loading) and the package
/// file names of every mounted container (fallback when a registry does not list an asset).
/// </summary>
public sealed class AssetIndex
{
    private readonly List<AssetEntry> _entries = new();
    private readonly Dictionary<string, List<AssetEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AssetEntry>> _byClass = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<AssetEntry>> _byPackage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _filesByName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AssetEntry> Entries => _entries;
    public IReadOnlyList<string> Registries { get; private set; } = [];
    public int PackageFiles { get; private set; }

    public static AssetIndex Build(IFileProvider provider, Log log, CancellationToken ct)
    {
        var idx = new AssetIndex();
        var regs = new List<string>();
        foreach (var path in provider.Files.Keys
                     .Where(p => p.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase)
                                 && p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                                 && !p.Contains("Editor", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var ar = provider.CreateReader(path);
                var state = new FAssetRegistryState(ar);
                int n = 0;
                foreach (var a in state.PreallocatedAssetDataBuffers)
                {
                    // keep only the tags recipes read (Fortnite's registry has hundreds of thousands of assets)
                    Dictionary<string, string>? kept = null;
                    if (a.TagsAndValues is { Count: > 0 })
                        foreach (var kv in a.TagsAndValues)
                            if (KeptTags.Contains(kv.Key.Text))
                                (kept ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[kv.Key.Text] = kv.Value;
                    IReadOnlyDictionary<string, string> tags = kept ?? EmptyTags;
                    idx.Add(new AssetEntry(a.PackageName.Text, a.AssetName.Text, a.AssetClass.Text, tags, path));
                    n++;
                }
                regs.Add(path);
                log.Info($"[fortnite] asset registry {path}: {n} assets in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                log.Warn($"[fortnite] asset registry {path} unreadable: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            }
        }
        idx.Registries = regs;

        int files = 0;
        foreach (var path in provider.Files.Keys)
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) continue;
            files++;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!idx._filesByName.TryGetValue(name, out var l)) idx._filesByName[name] = l = new List<string>(1);
            l.Add(path);
        }
        idx.PackageFiles = files;
        log.Info($"[fortnite] index: {idx._entries.Count} registry assets from {regs.Count} registries, {files} package files");
        return idx;
    }

    private static readonly Dictionary<string, string> EmptyTags = new();

    /// <summary>Registry tags the recipes use.</summary>
    public static readonly HashSet<string> KeptTags = new(StringComparer.OrdinalIgnoreCase)
        { "NativeParentClass", "ParentClass", "GeneratedClass", "DisplayName", "ItemName", "Duration", "NumChannels", "SampleRate" };

    internal void Add(AssetEntry e)
    {
        _entries.Add(e);
        Put(_byName, e.AssetName, e);
        Put(_byClass, e.AssetClass, e);
        Put(_byPackage, e.PackageName, e);
    }

    private static void Put(Dictionary<string, List<AssetEntry>> d, string k, AssetEntry e)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new List<AssetEntry>(1);
        l.Add(e);
    }

    public IReadOnlyList<AssetEntry> ByName(string assetName) => _byName.TryGetValue(assetName, out var l) ? l : [];
    public IReadOnlyList<AssetEntry> ByClass(string assetClass) => _byClass.TryGetValue(assetClass, out var l) ? l : [];
    public IReadOnlyList<AssetEntry> ByPackage(string packageName) => _byPackage.TryGetValue(packageName, out var l) ? l : [];
    public IReadOnlyList<string> FilesNamed(string fileName) => _filesByName.TryGetValue(fileName, out var l) ? l : [];
    public IEnumerable<string> FileNames => _filesByName.Keys;

    /// <summary>Class of the asset an object path ("/Game/A/B.B") points to, when the registry knows it.</summary>
    public string? ClassOf(string objectPath)
    {
        var (pkg, name) = SplitObjectPath(objectPath);
        foreach (var e in ByPackage(pkg))
            if (name == null || string.Equals(e.AssetName, name, StringComparison.OrdinalIgnoreCase))
                return e.AssetClass;
        return null;
    }

    public static (string Package, string? Name) SplitObjectPath(string objectPath)
    {
        var p = objectPath;
        int colon = p.IndexOf(':');
        if (colon > 0) p = p[..colon];
        int dot = p.LastIndexOf('.');
        int slash = p.LastIndexOf('/');
        return dot > slash ? (p[..dot], p[(dot + 1)..]) : (p, null);
    }

    /// <summary>"/Game/X/Y/Z" style package name for a provider file path such as "FortniteGame/Content/X/Y/Z.uasset"
    /// (used to load packages found only by file name).</summary>
    public static string ObjectPathForFile(string filePath)
    {
        var noExt = filePath[..^Path.GetExtension(filePath).Length];
        return noExt + "." + Path.GetFileName(noExt);
    }
}
