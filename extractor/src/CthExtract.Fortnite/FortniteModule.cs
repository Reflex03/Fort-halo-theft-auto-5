using System.Diagnostics;
using CthExtract.Core;
using CthExtract.Core.Generated;
using CthExtract.Fortnite.Net;
using CthExtract.Fortnite.Unreal;

namespace CthExtract.Fortnite;

/// <summary>Knobs for tests and diagnostics. Defaults are the real thing.</summary>
public sealed class FortniteOptions
{
    /// <summary>Inner HTTP handler (tests supply a fake); every request still goes through <see cref="HttpGuard"/>.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }
    /// <summary>Use cached keys/mappings only.</summary>
    public bool Offline { get; init; }
    /// <summary>Where keys and mappings are cached (default cache/keys/fortnite, next to cache/stats).</summary>
    public string? KeysDir { get; init; }
    /// <summary>Epic Games Launcher manifests folder (default %PROGRAMDATA%/Epic/EpicGamesLauncher/Data/Manifests).</summary>
    public string? ManifestsDir { get; init; }
    /// <summary>%LOCALAPPDATA% (for FortniteGame/Saved/PersistentDownloadDir/GameCustom/InstalledBundles); null skips it.</summary>
    public string? LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>Key sets tried before the fetched ones (tests).</summary>
    public IReadOnlyList<KeySet> ExtraKeys { get; init; } = [];
    public ArchiveOptions Archive { get; init; } = new();
    public RecipeOptions Recipes { get; init; } = new();
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Reads fortnite content from the player's own install: the five heal icons, the Battle Bus, the default glider and
/// the loot chest meshes (+ textures), and the chest/drink/heal sounds. Keys and the mapping file for the installed
/// build come from community services and are cached in cache/keys/fortnite; nothing from Fortnite is shipped.
/// </summary>
public sealed class FortniteModule(FortniteOptions? options = null) : IGameModule
{
    private readonly FortniteOptions _opt = options ?? new FortniteOptions();

    public string Game => "fortnite";
    public string Short => "fortnite";

    public IEnumerable<string> FingerprintFiles(string gameDir)
    {
        var root = FortniteInstall.Normalise(gameDir, new List<string>());
        if (root == null) yield break;
        var paks = Path.Combine(root, "FortniteGame", "Content", "Paks");
        var files = new List<string>
        {
            Path.Combine(paks, "global.utoc"),
            Path.Combine(paks, "pakchunk0-WindowsClient.utoc"),
            Path.Combine(paks, "pakchunk0-WindowsClient.pak"),
            Path.Combine(root, "FortniteGame", "Binaries", "Win64", "FortniteClient-Win64-Shipping.exe"),
        };
        foreach (var f in files)
            if (File.Exists(f)) yield return Path.GetRelativePath(gameDir, f).Replace('\\', '/');
    }

    public static string DefaultKeysDir(ExtractContext ctx)
    {
        var stats = Path.GetFullPath(ctx.StatsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cache = string.Equals(Path.GetFileName(stats), "stats", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(stats)!
            : Path.Combine(ctx.WorkDir);
        return Path.Combine(cache, "keys", "fortnite");
    }

    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct)
    {
        var log = ctx.Log;
        var rows = ctx.Rows.ToList();
        var sw = Stopwatch.StartNew();

        var install = FortniteInstall.Find(gameDir, _opt.ManifestsDir ?? FortniteInstall.DefaultManifestsDir(), out var tried);
        if (install == null)
        {
            foreach (var r in rows) ctx.Missing(r.Id, $"Fortnite folder not found ({tried})");
            return;
        }
        log.Info($"[fortnite] install {install.Root} ({install.How}); version {install.Version} ({install.VersionSource})" +
                 (tried.Length > 0 ? $"; notes: {tried}" : ""));

        ctx.Progress?.Invoke("fortnite: AES keys and mappings");
        var keysDir = _opt.KeysDir ?? DefaultKeysDir(ctx);
        List<KeySet> candidates;
        var (http, guard) = HttpGuard.CreateClient(_opt.HttpHandler, _opt.HttpTimeout, log.Info);
        using (http)
        {
            var service = new KeyService(http, keysDir, log);
            candidates = _opt.ExtraKeys.ToList();
            try { candidates.AddRange(service.GetCandidatesAsync(install.Version, !_opt.Offline, ct).GetAwaiter().GetResult()); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { log.Warn($"[fortnite] key lookup failed: {e.GetType().Name}: {e.Message}"); }
            log.Info($"[fortnite] {guard.Requested.Count} HTTP requests: {string.Join(", ", guard.Requested.Select(u => new Uri(u).Host).Distinct())}");
        }
        if (candidates.Count == 0)
        {
            foreach (var r in rows)
                ctx.Missing(r.Id, "no Fortnite AES keys/mappings available (offline and nothing cached in " + keysDir + "); connect to the internet and run again");
            return;
        }

        var bridge = LogBridge.Install(log);
        try { ExtractWithKeys(install, candidates, ctx, bridge, sw, ct); }
        finally { LogBridge.Silence(); }
    }

    private void ExtractWithKeys(FortniteInstall install, List<KeySet> candidates, ExtractContext ctx, LogBridge bridge, Stopwatch sw, CancellationToken ct)
    {
        var log = ctx.Log;
        var rows = ctx.Rows.ToList();
        log.Info($"[fortnite] parser: CUE4Parse {Version(typeof(CUE4Parse.Compression.Compression))}, Bink Audio: vgmstream port {Version(typeof(VgmstreamBinka.UebaReader))}");
        GameArchive archive;
        ctx.Progress?.Invoke("fortnite: mounting containers");
        var extras = FortniteInstall.InstalledBundlesDir(_opt.LocalAppData) is { } extra ? new[] { extra } : Array.Empty<string>();
        try
        {
            archive = GameArchive.Open(install.PaksDir, extras, candidates, _opt.Archive, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (KeyMismatchException e)
        {
            foreach (var r in rows) ctx.Missing(r.Id, $"cannot open the containers of Fortnite {install.Version}: {e.Message}");
            return;
        }
        catch (Exception e)
        {
            log.Warn($"[fortnite] opening the containers failed: {e}");
            foreach (var r in rows) ctx.Failed(r.Id, $"opening the containers failed: {e.GetType().Name}: {e.Message.Split('\n')[0]}");
            return;
        }

        using (archive)
        {
            log.Info($"[fortnite] keys: {archive.Keys.Describe()}; mapping: {archive.MappingNote}");
            var content = new FortniteContent(archive, ctx, _opt.Recipes);
            var byId = rows.ToDictionary(r => r.Id);
            SourceRow? R(string id) => byId.GetValueOrDefault(id) ?? Sources.All.FirstOrDefault(r => r.Id == id);

            // icons first (the sound rows reuse their item definitions), chest before its sound
            var order = rows.OrderBy(r => r.Kind switch { "icon" => 0, "mesh" => 1, _ => 2 }).ToList();
            int done = 0;
            foreach (var row in order)
            {
                ct.ThrowIfCancellationRequested();
                ctx.Progress?.Invoke($"fortnite: {row.Id} ({++done}/{order.Count})");
                var rsw = Stopwatch.StartNew();
                try
                {
                    switch (row.Id)
                    {
                        case "fn_mesh_battle_bus": content.BattleBus(row); break;
                        case "fn_mesh_glider": content.Glider(row); break;
                        case "fn_mesh_chest": content.Chest(row); break;
                        case "fn_snd_chest_open":
                            content.ChestSound(row, R("fn_mesh_chest") ?? row);
                            break;
                        case "fn_snd_drink":
                            content.ItemSound(row, new[] { R("fn_icon_shield_potion"), R("fn_icon_small_shield_potion") }.OfType<SourceRow>());
                            break;
                        case "fn_snd_heal_use":
                            content.ItemSound(row, new[] { R("fn_icon_bandages"), R("fn_icon_med_kit") }.OfType<SourceRow>());
                            break;
                        default:
                            if (row.Kind == "icon") content.Icon(row);
                            else log.Warn($"[fortnite] {row.Id}: no recipe for this row (kind {row.Kind}); it stays unimplemented");
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    log.Warn($"[fortnite] {row.Id}: {e}");
                    ctx.Failed(row.Id, $"{e.GetType().Name}: {e.Message.Split('\n')[0]}");
                }
                log.Info($"[fortnite] {row.Id}: {rsw.ElapsedMilliseconds} ms");
            }

            // Without a type mapping Fortnite's unversioned packages cannot be read, so every recipe above misses for
            // that reason and not the one it names: say so in the manifest row (re-recording a row replaces it).
            if (archive.MappingNote == "none")
            {
                const string why = " [no usable .usmap type mapping for this build (every candidate was rejected or unreadable, see " +
                                   "'mapping' in extract.log): unversioned packages cannot be read]";
                foreach (var (id, r) in ctx.Results)
                {
                    if (!byId.ContainsKey(id) || r.Status == AssetStatus.Ok || (r.Detail ?? "").Contains("type mapping")) continue;
                    if (r.Status == AssetStatus.Failed) ctx.Failed(id, (r.Detail ?? "failed") + why);
                    else ctx.Missing(id, (r.Detail ?? "missing") + why);
                }
            }
        }
        if (bridge.Suppressed > 0) log.Info($"[fortnite] {bridge.Suppressed} CUE4Parse messages were not logged");
        log.Info($"[fortnite] done in {sw.Elapsed.TotalSeconds:0.0} s");
    }

    private static string Version(Type t) =>
        t.Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
        ?? t.Assembly.GetName().Version?.ToString() ?? "?";
}
