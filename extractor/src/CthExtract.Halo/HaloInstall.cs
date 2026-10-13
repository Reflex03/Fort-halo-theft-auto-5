namespace CthExtract.Halo;

/// <summary>
/// Where the Halo 3 files of an MCC install are. Accepts whatever folder the user (or Melty) passes:
/// the MCC root (Steam: ...\steamapps\common\Halo The Master Chief Collection; Xbox app / Game Pass:
/// ...\XboxGames\Halo- The Master Chief Collection or its Content folder), the halo3 folder, halo3\maps,
/// halo3\fmod\pc, a Steam library root, or a file inside any of those. Names are matched case-insensitively so the
/// same code works for Windows installs and for case-sensitive test trees.
/// </summary>
public sealed class HaloInstall
{
    public required string Root { get; init; }       // folder that contains halo3/
    public required string Halo3Dir { get; init; }
    public required string MapsDir { get; init; }
    public string? FmodDir { get; init; }             // halo3/fmod/pc, if present

    public static readonly string[] SteamFolderNames = { "Halo The Master Chief Collection" };
    public static readonly string[] XboxFolderNames = { "Halo- The Master Chief Collection", "Halo - The Master Chief Collection", "Halo The Master Chief Collection" };

    /// <summary>Find the install, logging every candidate folder tried. Returns null when nothing matches.</summary>
    public static HaloInstall? Locate(string? given, Action<string>? log = null)
    {
        log ??= _ => { };
        if (string.IsNullOrWhiteSpace(given)) { log("no MCC folder given"); return null; }
        string start;
        try { start = Path.GetFullPath(given.Trim().Trim('"')); }
        catch (Exception e) { log($"bad MCC folder '{given}': {e.Message}"); return null; }
        if (File.Exists(start)) start = Path.GetDirectoryName(start)!;
        if (!Directory.Exists(start)) { log($"MCC folder does not exist: {start}"); return null; }

        var candidates = new List<string>();
        void Add(string? p) { if (p != null && !candidates.Contains(p, StringComparer.OrdinalIgnoreCase)) candidates.Add(p); }

        // the folder itself and its ancestors (covers halo3, halo3\maps, halo3\fmod\pc, Content)
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
        {
            Add(d.FullName);
            if (candidates.Count > 6) break;
        }
        // children for roots above the install
        Add(Child(start, "Content"));
        foreach (var n in SteamFolderNames.Concat(XboxFolderNames))
        {
            var c = Child(start, n);
            Add(c);
            Add(c is null ? null : Child(c, "Content"));
        }
        var common = ChildPath(start, "steamapps", "common");
        if (common != null) foreach (var n in SteamFolderNames) Add(Child(common, n));

        foreach (var c in candidates)
        {
            var halo3 = Child(c, "halo3");
            var maps = halo3 is null ? null : Child(halo3, "maps");
            if (maps is null) { log($"  tried {c}: no halo3{Path.DirectorySeparatorChar}maps"); continue; }
            bool anyMap = SafeFiles(maps).Any(f => f.EndsWith(".map", StringComparison.OrdinalIgnoreCase));
            if (!anyMap) { log($"  tried {c}: {maps} has no .map files"); continue; }
            var fmod = ChildPath(halo3!, "fmod", "pc");
            log($"  MCC root: {c} (halo3 maps: {maps}; fmod: {fmod ?? "missing"})");
            return new HaloInstall { Root = c, Halo3Dir = halo3!, MapsDir = maps, FmodDir = fmod };
        }
        return null;
    }

    /// <summary>A map file by base name ("riverworld"), case-insensitive, or null.</summary>
    public string? MapFile(string name)
    {
        var want = name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? name : name + ".map";
        return SafeFiles(MapsDir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>All scenario maps in the maps folder (not the shared resource caches).</summary>
    public IEnumerable<string> ScenarioMaps() => SafeFiles(MapsDir)
        .Where(f => f.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        .Where(f => !ResourceCaches.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase));

    public static readonly string[] ResourceCaches = { "shared", "campaign", "single_player_shared", "mainmenu" };

    /// <summary>Map search order: Valhalla first (holds every MP weapon), then other MP maps, then campaign.</summary>
    public static readonly string[] PreferredMaps =
    {
        "riverworld", "guardian", "construct", "isolation", "chill", "zanzibar", "snowbound", "salvation", "shrine",
        "deadlock", "cyberdyne", "bunkerworld", "warehouse", "sidewinder", "armory", "chillout", "descent", "docks",
        "fortress", "ghosttown", "lockout", "midship", "sandbox", "spacecamp",
        "010_jungle", "020_base", "030_outskirts", "040_voi", "050_floodvoi", "070_waste", "100_citadel", "110_hc",
        "120_halo", "005_intro", "130_epilogue",
    };

    public IReadOnlyList<string> MapSearchOrder()
    {
        var all = ScenarioMaps().ToList();
        var ordered = new List<string>();
        foreach (var n in PreferredMaps)
        {
            var f = all.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), n, StringComparison.OrdinalIgnoreCase));
            if (f != null) ordered.Add(f);
        }
        ordered.AddRange(all.Where(p => !ordered.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    /// <summary>FMOD banks with their .info index: (bank path, info path) for every *.fsb that has a .fsb.info.</summary>
    public IReadOnlyList<(string Fsb, string Info)> SoundBanks()
    {
        if (FmodDir is null) return Array.Empty<(string, string)>();
        var files = SafeFiles(FmodDir).ToList();
        var banks = new List<(string, string)>();
        foreach (var fsb in files.Where(f => f.EndsWith(".fsb", StringComparison.OrdinalIgnoreCase)))
        {
            var info = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(fsb) + ".info", StringComparison.OrdinalIgnoreCase));
            if (info != null) banks.Add((fsb, info));
        }
        // sfx first: it holds the weapon and shield effects
        return banks.OrderBy(b => Path.GetFileName(b.Item1).StartsWith("sfx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(b => b.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Files whose size+mtime identify the installed version.</summary>
    public IEnumerable<string> FingerprintFiles()
    {
        foreach (var m in new[] { "riverworld", "shared" })
        {
            var f = MapFile(m);
            if (f != null) yield return f;
        }
        foreach (var (fsb, info) in SoundBanks().Where(b => Path.GetFileName(b.Fsb).StartsWith("sfx", StringComparison.OrdinalIgnoreCase)))
        {
            yield return fsb;
            yield return info;
        }
    }

    internal static string? Child(string dir, string name)
    {
        try
        {
            var exact = Path.Combine(dir, name);
            if (Directory.Exists(exact)) return exact;
            return Directory.EnumerateDirectories(dir).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    internal static string? ChildPath(string dir, params string[] names)
    {
        string? d = dir;
        foreach (var n in names)
        {
            d = Child(d, n);
            if (d is null) return null;
        }
        return d;
    }

    internal static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }
}
