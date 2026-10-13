namespace CthExtract.Gta;

/// <summary>
/// Normalises what the player (or Melty) gave us to the GTA V folder: the one containing GTA5.exe (Legacy) or
/// GTA5_Enhanced.exe (Enhanced). Accepts the folder itself, a file or subfolder inside it (walks up), or a store /
/// library root above it (searches down a few levels). Legacy wins when both editions are found.
/// </summary>
public static class GameFolder
{
    public const string LegacyExe = "GTA5.exe";
    public const string EnhancedExe = "GTA5_Enhanced.exe";

    public sealed record Found(string Dir, string ExePath, bool Enhanced, string How);

    public static Found? Find(string? given, out string tried)
    {
        tried = "";
        if (string.IsNullOrWhiteSpace(given)) { tried = "no folder given"; return null; }
        string start;
        try { start = Path.GetFullPath(given.Trim().Trim('"')); }
        catch (Exception e) { tried = $"bad path: {e.Message}"; return null; }
        if (File.Exists(start)) start = Path.GetDirectoryName(start)!;
        if (!Directory.Exists(start)) { tried = $"'{start}' does not exist"; return null; }

        var hit = Check(start, "given folder");
        if (hit is not null) return hit;

        // a subfolder of the install (e.g. ...\update\x64): walk up
        var up = Directory.GetParent(start);
        for (int i = 0; i < 4 && up is not null; i++, up = up.Parent)
        {
            hit = Check(up.FullName, $"parent of the given folder ({i + 1} up)");
            if (hit is not null) return hit;
        }

        // a library / store root (e.g. steamapps\common, Epic Games, Rockstar Games): search down, breadth first
        var found = new List<Found>();
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((start, 0));
        int visited = 0;
        while (queue.Count > 0 && visited < 4000)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;
            if (depth > 0)
            {
                var f = Check(dir, $"found {depth} level(s) below the given folder");
                if (f is not null) { found.Add(f); continue; }
            }
            if (depth >= 3) continue;
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { continue; }
            foreach (var s in subs) queue.Enqueue((s, depth + 1));
        }
        var pick = found.OrderBy(f => f.Enhanced ? 1 : 0).FirstOrDefault();
        if (pick is not null) return pick;
        tried = $"no {LegacyExe} or {EnhancedExe} in '{start}', its parents, or {visited} folders below it";
        return null;
    }

    private static Found? Check(string dir, string how)
    {
        string? legacy = FileIn(dir, LegacyExe), enhanced = FileIn(dir, EnhancedExe);
        if (legacy is not null) return new Found(dir, legacy, false, how);
        if (enhanced is not null) return new Found(dir, enhanced, true, how);
        return null;
    }

    public static string? FileIn(string dir, string name)
    {
        try
        {
            var direct = Path.Combine(dir, name);
            if (File.Exists(direct)) return direct;
            return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return null; }
    }
}
