using System.Globalization;

namespace CthExtract;

/// <summary>Where a game folder value came from (for the log).</summary>
internal enum DirSource { None, Argument, Config }

/// <summary>
/// The command line (docs/CONTRACTS.md "Extractor command line"):
/// <code>
/// cthextract --halo &lt;MCC&gt; --gta &lt;GTA V&gt; --fortnite &lt;Fortnite&gt; --out &lt;cache&gt;
///            [--only halo,gta,fortnite] [--config crafttheft.properties] [--check] [--version]
///            [--timeout-sec 1500] [--game-timeout-sec 900] [--grace-sec 20] [--lock-wait-sec 0]
///            [--in-process] [--no-keep] [--offline] [--console]
/// </code>
/// Options also accept "--name=value". Command-line values win over --config values.
/// </summary>
internal sealed class CliOptions
{
    public const int DefaultTimeoutSec = 1500, DefaultGameTimeoutSec = 900, DefaultGraceSec = 20;

    public readonly Dictionary<string, string?> Dirs = new() { ["halo"] = null, ["gta"] = null, ["fortnite"] = null };
    public readonly Dictionary<string, DirSource> DirSources = new() { ["halo"] = DirSource.None, ["gta"] = DirSource.None, ["fortnite"] = DirSource.None };
    public string? Out;
    public DirSource OutSource;
    public string? Config;
    public List<string>? Only;
    public bool Check, Version, Help, Console, InProcess, NoKeep, Offline;
    public int TimeoutSec = DefaultTimeoutSec, GameTimeoutSec = DefaultGameTimeoutSec, GraceSec = DefaultGraceSec, LockWaitSec;
    /// <summary>Things worth logging about how the arguments were read (ignored placeholders, config keys).</summary>
    public readonly List<string> Notes = new();
    /// <summary>Set when the arguments are unusable: exit 3.</summary>
    public string? Error;

    public static readonly string Usage =
        "usage: cthextract --halo <MCC folder> --gta <GTA V folder> --fortnite <Fortnite folder> --out <cache folder>\n" +
        "                  [--only halo,gta,fortnite] [--config <crafttheft.properties>] [--check] [--version]\n" +
        "                  [--timeout-sec N] [--game-timeout-sec N] [--grace-sec N] [--lock-wait-sec N]\n" +
        "                  [--in-process] [--no-keep] [--offline] [--console]\n" +
        "exit codes: 0 all ok, 1 partial, 2 nothing extracted, 3 bad arguments, 4 cache locked, 5 watchdog timeout;\n" +
        "--check: 0 fresh, 10 full extraction needed, 11 some games should be re-read, 12 games were updated";

    public static CliOptions Parse(IReadOnlyList<string> argv)
    {
        var o = new CliOptions();
        try { o.ParseArgs(argv); }
        catch (ArgumentException e) { o.Error ??= e.Message; }
        if (o.Error is null && o.Config is not null && !o.Help && !o.Version) o.ApplyConfig();
        if (o.Error is null) o.Validate();
        return o;
    }

    private void ParseArgs(IReadOnlyList<string> v)
    {
        for (int i = 0; i < v.Count; i++)
        {
            var raw = v[i];
            string name = raw;
            string? inline = null;
            if (raw.StartsWith("--", StringComparison.Ordinal) && raw.IndexOf('=') is var eq and > 2)
            {
                name = raw[..eq];
                inline = raw[(eq + 1)..];
            }
            string Value()
            {
                if (inline is not null) return inline;
                if (i + 1 >= v.Count) throw new ArgumentException($"{name} needs a value");
                return v[++i];
            }
            void Flag() { if (inline is not null) throw new ArgumentException($"{name} takes no value"); }

            switch (name)
            {
                case "--halo": SetDir("halo", Value(), DirSource.Argument); break;
                case "--gta": SetDir("gta", Value(), DirSource.Argument); break;
                case "--fortnite": SetDir("fortnite", Value(), DirSource.Argument); break;
                case "--out":
                    {
                        var val = Clean(Value(), "--out");
                        if (val is not null) { Out = val; OutSource = DirSource.Argument; }
                        break;
                    }
                case "--config": Config = Value(); break;
                case "--only":
                    Only ??= new List<string>();
                    foreach (var part in Value().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var g = part.ToLowerInvariant() switch
                        {
                            "halo" or "halo-mcc" or "halo3" => "halo",
                            "gta" or "gta-v" or "gta5" => "gta",
                            "fortnite" or "fn" => "fortnite",
                            _ => throw new ArgumentException($"--only takes halo, gta, fortnite (comma-separated), not '{part}'"),
                        };
                        if (!Only.Contains(g)) Only.Add(g);
                    }
                    break;
                case "--check": Flag(); Check = true; break;
                case "--version": Flag(); Version = true; break;
                case "--help" or "-h" or "/?": Flag(); Help = true; break;
                case "--console": Flag(); Console = true; break;
                case "--in-process": Flag(); InProcess = true; break;
                case "--no-keep": Flag(); NoKeep = true; break;
                case "--offline": Flag(); Offline = true; break;
                case "--timeout-sec": TimeoutSec = Seconds(name, Value()); break;
                case "--game-timeout-sec": GameTimeoutSec = Seconds(name, Value()); break;
                case "--grace-sec": GraceSec = Seconds(name, Value(), allowZero: true); break;
                case "--lock-wait-sec": LockWaitSec = Seconds(name, Value(), allowZero: true); break;
                default:
                    throw new ArgumentException(raw.StartsWith('-') ? $"unknown option {raw}" : $"unexpected argument '{raw}' (options start with --)");
            }
        }
        if (Only is { Count: 0 }) throw new ArgumentException("--only needs at least one game");
    }

    private static int Seconds(string name, string value, bool allowZero = false)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < (allowZero ? 0 : 1) || n > 7 * 24 * 3600)
            throw new ArgumentException($"{name} takes a whole number of seconds{(allowZero ? "" : " above 0")}, not '{value}'");
        return n;
    }

    private void SetDir(string game, string value, DirSource source)
    {
        var val = Clean(value, $"--{game}");
        if (val is null) return;
        Dirs[game] = val;
        DirSources[game] = source;
    }

    /// <summary>Trim, drop surrounding quotes, and treat empty values and unexpanded Melty placeholders ("{game:x}") as absent.</summary>
    private string? Clean(string value, string what)
    {
        var v = value.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1].Trim();
        if (v.Length == 0) { Notes.Add($"{what}: empty value ignored"); return null; }
        if (v.Contains("{game:", StringComparison.OrdinalIgnoreCase) || v.Contains("{localappdata}", StringComparison.OrdinalIgnoreCase))
        {
            Notes.Add($"{what}: unexpanded placeholder '{v}' ignored (game not installed or not known to Melty?)");
            return null;
        }
        return v;
    }

    private void ApplyConfig()
    {
        Dictionary<string, string> props;
        try { props = JavaProperties.Load(Config!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Error = $"--config {Config}: cannot read it ({e.GetType().Name}: {e.Message})";
            return;
        }
        Notes.Add($"--config {Config}: keys {string.Join(", ", props.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        foreach (var (key, game) in new[] { ("halo_dir", "halo"), ("gta_dir", "gta"), ("fortnite_dir", "fortnite") })
            if (DirSources[game] == DirSource.None && props.TryGetValue(key, out var val))
                SetDir(game, val, DirSource.Config);
        if (OutSource == DirSource.None && props.TryGetValue("cache_dir", out var cache))
        {
            var c = Clean(cache, "cache_dir");
            if (c is not null) { Out = c; OutSource = DirSource.Config; }
        }
    }

    private void Validate()
    {
        if (Help || Version) return;
        if (Check && (InProcess || NoKeep)) Notes.Add("--in-process / --no-keep have no effect with --check");
    }

    /// <summary>The cache folder: --out, else cache_dir from --config, else %LOCALAPPDATA%/CraftTheftHalo/cache.</summary>
    public string? ResolveCacheDir(out string? problem)
    {
        problem = null;
        string? dir = Out;
        if (dir is null)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) { problem = "no --out given and %LOCALAPPDATA% is not set"; return null; }
            dir = Path.Combine(local, "CraftTheftHalo", "cache");
        }
        try { return Path.GetFullPath(dir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"cache folder '{dir}' is not a valid path: {e.Message}";
            return null;
        }
    }

    /// <summary>Selected games in canonical order.</summary>
    public string[] SelectedGames() => AppInfo.Games.Where(g => Only is null || Only.Contains(g)).ToArray();
}
