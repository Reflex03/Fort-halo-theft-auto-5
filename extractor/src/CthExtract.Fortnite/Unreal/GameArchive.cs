using System.Diagnostics;
using CthExtract.Core;
using CthExtract.Fortnite.Net;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;

namespace CthExtract.Fortnite.Unreal;

/// <summary>Thrown when no key set opens the player's containers; the module reports every row Missing.</summary>
public sealed class KeyMismatchException(string message) : Exception(message);

/// <summary>Options for opening the containers (tests point them at CUE4Parse's fixtures).</summary>
public sealed class ArchiveOptions
{
    public EGame Game { get; init; } = EGame.GAME_UE6_0;
    /// <summary>Classes that must be in the mapping file for it to count as a Fortnite mapping.</summary>
    public IReadOnlyList<string> RequiredMappingTypes { get; init; } =
        ["StaticMesh", "SkeletalMesh", "Texture2D", "SoundWave", "AthenaBattleBusItemDefinition", "BuildingContainer"];
    /// <summary>Minimum number of types for a mapping file to be plausible.</summary>
    public int MinMappingTypes { get; init; } = 1000;
}

/// <summary>The player's mounted Fortnite containers (CUE4Parse DefaultFileProvider) plus the asset index.</summary>
public sealed class GameArchive : IDisposable
{
    public required DefaultFileProvider Provider { get; init; }
    public required AssetIndex Index { get; init; }
    public required KeySet Keys { get; init; }
    public required string MappingNote { get; init; }

    public void Dispose() => Provider.Dispose();

    public static GameArchive Open(string paksDir, IReadOnlyList<string> extraDirs, IReadOnlyList<KeySet> candidates,
        ArchiveOptions opt, Log log, CancellationToken ct)
    {
        OodleBridge.Ensure();
        TextureDecoder.UseAssetRipperTextureDecoder = true; // managed BCn decoding (no Detex.dll)
        log.Info($"[fortnite] {OodleBridge.Describe()}");

        var sw = Stopwatch.StartNew();
        var extras = extraDirs.Where(Directory.Exists).Select(d => new DirectoryInfo(d)).ToArray();
        var provider = new DefaultFileProvider(new DirectoryInfo(paksDir), extras, SearchOption.AllDirectories,
            new VersionContainer(opt.Game), StringComparer.OrdinalIgnoreCase);
        try
        {
            provider.Initialize();
            var unloaded = provider.UnloadedVfs.ToList();
            log.Info($"[fortnite] containers: {unloaded.Count} found in {paksDir}{(extras.Length > 0 ? $" + {string.Join(", ", extras.Select(e => e.FullName))}" : "")}; " +
                     $"{unloaded.Count(r => r.IsEncrypted)} encrypted; key GUIDs needed: {provider.RequiredKeys.Count}");
            if (unloaded.Count == 0) throw new KeyMismatchException($"no .utoc/.pak containers in {paksDir}");

            // ---- main key: test it on encrypted zero-GUID containers before mounting anything
            var probes = unloaded.Where(r => r.IsEncrypted && r.EncryptionKeyGuid == default).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            KeySet? chosen = null;
            var tried = new List<string>();
            foreach (var ks in candidates)
            {
                ct.ThrowIfCancellationRequested();
                FAesKey key;
                try { key = new FAesKey(ks.MainKey); }
                catch (Exception e) { tried.Add($"{ks.Source}: bad key ({e.Message})"); continue; }
                bool ok = probes.Count == 0 || probes.All(p => SafeTest(p, key));
                tried.Add($"{ks.Source} ({ks.Version ?? ks.Build ?? "?"}): {(ok ? "opens" : "wrong key")}");
                if (ok) { chosen = ks; break; }
            }
            log.Info($"[fortnite] main key test on {(probes.Count == 0 ? "(no encrypted containers)" : string.Join(", ", probes.Select(p => p.Name)))}: {string.Join("; ", tried)}");
            if (chosen == null)
                throw new KeyMismatchException(candidates.Count == 0
                    ? "no AES keys available (no network and nothing cached)"
                    : $"none of {candidates.Count} key sets opens this install ({string.Join("; ", tried)})");

            // ---- mapping file: the chosen set's, else one of the same build, else the newest we have
            var (mappings, note) = ChooseMappings(chosen, candidates, opt, log);
            provider.MappingsContainer = mappings;

            // ---- mount: main key, then every dynamic key
            int mounted = provider.SubmitKey(new FGuid(), new FAesKey(chosen.MainKey));
            log.Info($"[fortnite] SubmitKey(main) mounted {mounted} containers");
            if (mounted <= 0) throw new KeyMismatchException($"the main key of {chosen.Source} mounted 0 containers");
            var dyn = new Dictionary<FGuid, FAesKey>();
            foreach (var d in chosen.DynamicKeys)
            {
                try { dyn[new FGuid(d.Guid)] = new FAesKey(d.Key); }
                catch (Exception e) { log.Warn($"[fortnite] skipping dynamic key {d.Guid}: {e.Message}"); }
            }
            if (dyn.Count > 0)
            {
                int dm = provider.SubmitKeys(dyn);
                log.Info($"[fortnite] dynamic keys: {dyn.Count} submitted, {dm} containers mounted");
            }
            int plain = provider.Mount();
            if (plain > 0) log.Info($"[fortnite] {plain} more containers mounted without a key");
            try { provider.LoadVirtualPaths(); } catch (Exception e) { log.Warn($"[fortnite] LoadVirtualPaths: {e.Message}"); }
            try { provider.PostMount(); } catch (Exception e) { log.Warn($"[fortnite] PostMount: {e.Message}"); }
            var stillLocked = provider.UnloadedVfs.Where(r => r.IsEncrypted).Select(r => r.Name).ToList();
            log.Info($"[fortnite] mounted {provider.MountedVfs.Count} containers, {provider.Files.Count} files in {sw.ElapsedMilliseconds} ms" +
                     (stillLocked.Count > 0 ? $"; still locked (no key): {stillLocked.Count} ({string.Join(", ", stillLocked.Take(8))}{(stillLocked.Count > 8 ? ", …" : "")})" : ""));

            var index = AssetIndex.Build(provider, log, ct);
            return new GameArchive { Provider = provider, Index = index, Keys = chosen, MappingNote = note };
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    private static bool SafeTest(CUE4Parse.UE4.VirtualFileSystem.IAesVfsReader r, FAesKey key)
    {
        try { return r.TestAesKey(key); }
        catch { return false; }
    }

    private static (ITypeMappingsProvider? Mappings, string Note) ChooseMappings(KeySet chosen, IReadOnlyList<KeySet> all, ArchiveOptions opt, Log log)
    {
        var order = new List<(string Path, string Why)>();
        if (chosen.UsmapPath != null) order.Add((chosen.UsmapPath, $"from {chosen.Source}"));
        foreach (var k in all)
            if (k.UsmapPath != null && k != chosen && (k.Version == chosen.Version || k.Build == chosen.Build))
                order.Add((k.UsmapPath, $"same version, from {k.Source}"));
        foreach (var k in all.OrderByDescending(k => k.FetchedUtc))
            if (k.UsmapPath != null)
                order.Add((k.UsmapPath, $"OTHER version {k.Version ?? k.Build}, from {k.Source} (may misparse; outputs are sanity-checked)"));

        foreach (var (path, why) in order.DistinctBy(o => o.Path))
        {
            try
            {
                var m = new FileUsmapTypeMappingsProvider(path);
                var types = m.MappingsForGame?.Types;
                int count = types?.Count ?? 0;
                var missing = opt.RequiredMappingTypes.Where(t => types == null || !types.ContainsKey(t)).ToList();
                if (count < opt.MinMappingTypes || missing.Count > 0)
                {
                    log.Warn($"[fortnite] mapping {Path.GetFileName(path)} rejected: {count} types, missing [{string.Join(", ", missing)}]");
                    continue;
                }
                var note = $"{Path.GetFileName(path)} ({count} types; {why})";
                log.Info($"[fortnite] mapping: {note}");
                return (m, note);
            }
            catch (Exception e)
            {
                log.Warn($"[fortnite] mapping {Path.GetFileName(path)} unreadable: {e.GetType().Name}: {e.Message}");
            }
        }
        log.Warn("[fortnite] no usable mapping file: unversioned packages cannot be read");
        return (null, "none");
    }

    // ------------------------------------------------------------------ loading helpers

    /// <summary>Load an object by object path ("/Game/A/B.B" or "/Plugin/A/B.B_C"); null (logged by caller) on failure.</summary>
    public UObject? TryLoad(string objectPath, out string? error)
    {
        error = null;
        try
        {
            if (Provider.TryLoadPackageObject(objectPath, out var obj)) return obj;
            error = "not found";
            return null;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
            return null;
        }
    }

    public T? TryLoad<T>(string objectPath, out string? error) where T : UObject
    {
        var o = TryLoad(objectPath, out error);
        if (o is T t) return t;
        if (o != null) error = $"is a {o.ExportType}, not {typeof(T).Name}";
        return null;
    }

    /// <summary>All exports of a package (by package or object path); empty on failure.</summary>
    public IReadOnlyList<UObject> Exports(string packagePath, out string? error)
    {
        error = null;
        var (pkg, _) = AssetIndex.SplitObjectPath(packagePath);
        try
        {
            if (Provider.TryLoadPackage(pkg, out var p)) return p.GetExports().ToList();
            error = "package not found";
        }
        catch (Exception e) { error = $"{e.GetType().Name}: {e.Message.Split('\n')[0]}"; }
        return [];
    }
}
