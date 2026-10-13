namespace CthExtract;

/// <summary>
/// Never write inside a game folder (docs/CONTRACTS.md; nothing is ever placed in GTA V's folder). The cache folder
/// must not be, or be inside, a game folder given on the command line or in --config. Each game module also walks UP
/// from the folder it is given (GTA: a subfolder such as update\x64; MCC: halo3\maps; Fortnite: FortniteGame\Content\Paks),
/// so an ancestor of a given folder that carries a game's install markers counts as a game folder too.
/// </summary>
internal static class GameDirGuard
{
    /// <summary>How far up from a given folder the modules look for the install (the largest of the three).</summary>
    public const int MaxLevelsUp = 6;

    private static readonly string[] MarkerFiles =
    [
        "GTA5.exe", "GTA5_Enhanced.exe", "PlayGTAV.exe", "GTAVLauncher.exe",   // GTA V Legacy / Enhanced
        "mcclauncher.exe", "MCC-Win64-Shipping.exe",                           // Halo: MCC
        "FortniteLauncher.exe",                                                // Fortnite (FortniteGame\Binaries\Win64)
    ];

    private static readonly string[][] MarkerDirs =
    [
        ["halo3", "maps"],                      // MCC root
        ["FortniteGame", "Content", "Paks"],    // Fortnite root
        ["x64", "audio", "sfx"],                // GTA V root
        ["update", "x64", "dlcpacks"],          // GTA V root
    ];

    /// <summary>Null when <paramref name="cacheRoot"/> is safe to write to, else why not.</summary>
    public static string? Problem(string cacheRoot, IEnumerable<KeyValuePair<string, string?>> gameDirs)
    {
        string cache;
        try { cache = Trim(Path.GetFullPath(cacheRoot)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        foreach (var (game, d) in gameDirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            string full;
            try { full = Trim(Path.GetFullPath(d.Trim().Trim('"'))); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Inside(cache, full))
                return $"the cache folder {cache} is inside the {game} folder {full}; refusing to write there";

            DirectoryInfo? up;
            try { up = new DirectoryInfo(full).Parent; } catch (Exception) { continue; }
            for (int i = 0; i < MaxLevelsUp && up is not null; i++, up = up.Parent)
            {
                var root = Trim(up.FullName);
                if (Inside(cache, root) && LooksLikeGameInstall(root))
                    return $"the cache folder {cache} is inside {root}, the game install above the {game} folder {full}; refusing to write there";
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="dir"/> holds a file or folder that only a game install has.</summary>
    public static bool LooksLikeGameInstall(string dir)
    {
        try
        {
            foreach (var f in MarkerFiles)
                if (File.Exists(Path.Combine(dir, f))) return true;
            foreach (var parts in MarkerDirs)
                if (Directory.Exists(Path.Combine([dir, .. parts]))) return true;
        }
        catch (Exception) { /* unreadable: not provably a game folder */ }
        return false;
    }

    /// <summary>a == b, or a is below b (case-insensitive: Windows paths).</summary>
    public static bool Inside(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = b.EndsWith(Path.DirectorySeparatorChar) || b.EndsWith(Path.AltDirectorySeparatorChar) ? b : b + Path.DirectorySeparatorChar;
        return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string p)
    {
        var root = Path.GetPathRoot(p) ?? "";
        return p.Length > root.Length ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;
    }
}
