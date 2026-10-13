using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CthExtract.Fortnite;

/// <summary>A Fortnite build as Epic writes it, e.g. "++Fortnite+Release-42.30-CL-58557680-Windows".</summary>
public sealed record FortniteVersion(string? Raw, string? MajorMinor, string? BuildString, long? Changelist)
{
    public static readonly FortniteVersion Unknown = new(null, null, null, null);

    public static FortniteVersion Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unknown;
        var s = raw.Trim();
        var mm = Regex.Match(s, @"Release-(\d+)\.(\d+)");
        string? majorMinor = mm.Success ? $"{int.Parse(mm.Groups[1].Value)}.{mm.Groups[2].Value.PadLeft(2, '0')}" : null;
        var cl = Regex.Match(s, @"-CL-(\d+)");
        long? changelist = cl.Success && long.TryParse(cl.Groups[1].Value, out var c) ? c : null;
        string? build = null;
        var b = Regex.Match(s, @"^(\+\+Fortnite\+Release-\d+\.\d+(?:\.\d+)?-CL-\d+)");
        if (b.Success) build = b.Groups[1].Value;
        return new FortniteVersion(s, majorMinor, build, changelist);
    }

    public override string ToString() => Raw ?? "unknown";
}

/// <summary>One Epic Games Launcher install manifest (%PROGRAMDATA%/Epic/EpicGamesLauncher/Data/Manifests/*.item).</summary>
public sealed record EpicManifest(string File, string? AppName, string? MainGameAppName, string? DisplayName,
    string? InstallLocation, string? AppVersionString, string? LaunchExecutable)
{
    public bool IsFortnite =>
        string.Equals(AppName, "Fortnite", StringComparison.OrdinalIgnoreCase)
        || string.Equals(MainGameAppName, "Fortnite", StringComparison.OrdinalIgnoreCase)
        || string.Equals(DisplayName, "Fortnite", StringComparison.OrdinalIgnoreCase)
        || (LaunchExecutable?.Contains("FortniteGame", StringComparison.OrdinalIgnoreCase) ?? false);

    public static IReadOnlyList<EpicManifest> ReadAll(string? dir, List<string> notes)
    {
        var list = new List<EpicManifest>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { notes.Add($"no Epic manifests folder ({dir ?? "unset"})"); return list; }
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                var j = JsonNode.Parse(System.IO.File.ReadAllText(f));
                if (j is not JsonObject) continue;
                string? S(string k) => j[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                list.Add(new EpicManifest(f, S("AppName"), S("MainGameAppName"), S("DisplayName"), S("InstallLocation"),
                    S("AppVersionString"), S("LaunchExecutable")));
            }
            catch (Exception e) { notes.Add($"unreadable manifest {Path.GetFileName(f)}: {e.Message}"); }
        }
        return list;
    }
}

/// <summary>Where Fortnite is installed and which build it is.</summary>
public sealed class FortniteInstall
{
    public required string Root { get; init; }
    public string PaksDir => Path.Combine(Root, "FortniteGame", "Content", "Paks");
    public required string How { get; init; }
    public required FortniteVersion Version { get; init; }
    public required string VersionSource { get; init; }
    public EpicManifest? Manifest { get; init; }

    public static string DefaultManifestsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static bool HasPaks(string dir) => Directory.Exists(Path.Combine(dir, "FortniteGame", "Content", "Paks"));

    /// <summary>Normalise any folder the user may have picked (store root, Fortnite root, FortniteGame, Content, Paks,
    /// the library folder above it) to the folder that contains FortniteGame/Content/Paks.</summary>
    public static string? Normalise(string? dir, List<string> tried)
    {
        if (string.IsNullOrWhiteSpace(dir)) { tried.Add("no folder given"); return null; }
        string full;
        try { full = Path.GetFullPath(dir.Trim().Trim('"')); } catch (Exception e) { tried.Add($"bad path '{dir}': {e.Message}"); return null; }
        if (full.Length > Path.GetPathRoot(full)!.Length) full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(full)) full = Path.GetDirectoryName(full)!; // e.g. the launcher exe
        if (!Directory.Exists(full)) { tried.Add($"{full} does not exist"); return null; }

        // the folder itself or one of its parents (picked Paks / Content / FortniteGame / Binaries)
        var d = new DirectoryInfo(full);
        for (int up = 0; d != null && up < 6; up++, d = d.Parent)
        {
            if (HasPaks(d.FullName)) return d.FullName;
            if (string.Equals(d.Name, "FortniteGame", StringComparison.OrdinalIgnoreCase) && d.Parent != null && HasPaks(d.Parent.FullName))
                return d.Parent.FullName;
        }
        // a library folder above it (e.g. "C:\Program Files\Epic Games" -> Fortnite)
        try
        {
            foreach (var child in Directory.EnumerateDirectories(full))
            {
                if (HasPaks(child)) return child;
                foreach (var grand in Directory.EnumerateDirectories(child))
                    if (HasPaks(grand)) return grand;
            }
        }
        catch (Exception e) { tried.Add($"cannot list {full}: {e.Message}"); }
        tried.Add($"{full}: no FortniteGame/Content/Paks in it, above it or one/two levels below it");
        return null;
    }

    public static FortniteInstall? Find(string? gameDir, string? manifestsDir, out string tried)
    {
        var notes = new List<string>();
        var root = Normalise(gameDir, notes);
        var manifests = EpicManifest.ReadAll(manifestsDir, notes).Where(m => m.IsFortnite).ToList();
        EpicManifest? manifest = null;
        string how;
        if (root != null)
        {
            how = $"folder {gameDir}";
            manifest = manifests.FirstOrDefault(m => m.InstallLocation != null && SamePath(m.InstallLocation, root))
                       ?? manifests.FirstOrDefault(m => m.InstallLocation != null && Normalise(m.InstallLocation, new List<string>()) is { } r && SamePath(r, root));
        }
        else
        {
            foreach (var m in manifests)
            {
                var r = Normalise(m.InstallLocation, notes);
                if (r != null) { root = r; manifest = m; break; }
            }
            how = manifest != null ? $"Epic manifest {Path.GetFileName(manifest.File)}" : "";
        }
        if (root == null)
        {
            if (manifests.Count == 0) notes.Add("no Fortnite entry in the Epic Games Launcher manifests");
            tried = string.Join("; ", notes);
            return null;
        }

        var version = FortniteVersion.Unknown;
        string vsrc = "unknown (no Epic manifest for this folder)";
        if (manifest?.AppVersionString is { } avs)
        {
            version = FortniteVersion.Parse(avs);
            vsrc = $"Epic manifest {Path.GetFileName(manifest.File)}";
        }
        else if (TryExeVersion(root) is { } exeVer)
        {
            version = FortniteVersion.Parse(exeVer);
            vsrc = "FortniteClient-Win64-Shipping.exe version resource";
        }
        tried = string.Join("; ", notes);
        return new FortniteInstall { Root = root, How = how, Version = version, VersionSource = vsrc, Manifest = manifest };
    }

    private static string? TryExeVersion(string root)
    {
        try
        {
            var exe = Path.Combine(root, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe");
            if (!File.Exists(exe) || !OperatingSystem.IsWindows()) return null;
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            var s = vi.ProductVersion ?? vi.FileVersion;
            return s != null && s.Contains("Release-") ? s : null;
        }
        catch { return null; }
    }

    public static bool SamePath(string a, string b)
    {
        try
        {
            static string N(string p) => Path.GetFullPath(p.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(N(a), N(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Extra content folder of on-demand/streamed bundles (FortnitePorting's ExtraDirectories).</summary>
    public static string? InstalledBundlesDir(string? localAppData)
    {
        if (string.IsNullOrEmpty(localAppData)) return null;
        var d = Path.Combine(localAppData, "FortniteGame", "Saved", "PersistentDownloadDir", "GameCustom", "InstalledBundles");
        return Directory.Exists(d) ? d : null;
    }
}
