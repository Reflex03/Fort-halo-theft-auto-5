using System.Diagnostics;
using CthExtract.Core;
using CthExtract.Core.Generated;
using CthExtract.Gta.Audio;
using CthExtract.Gta.Crypto;
using CthExtract.Gta.Resources;
using CthExtract.Gta.Rpf;
using CthExtract.Gta.Vehicles;

namespace CthExtract.Gta;

/// <summary>Knobs for tests and diagnostics. Defaults are the real thing.</summary>
public sealed class GtaOptions
{
    /// <summary>SHA1 list every key must match (default: gta-toolkit's published GTA V PC hashes).</summary>
    public KeyHashes Hashes { get; init; } = KeyHashes.Gta5Pc;
    /// <summary>Where the NG key blob comes from (default: pinned HTTPS URL).</summary>
    public IMagicSource Magic { get; init; } = new PinnedHttpMagicSource();
    /// <summary>SHA-256 the blob must have (default: the pinned commit's magic.dat); null skips the check (tests only).</summary>
    public string? MagicSha256 { get; init; } = PinnedHttpMagicSource.Sha256;
    /// <summary>Where the player's AES key is cached; default cache/keys (next to cache/stats).</summary>
    public string? KeysDir { get; init; }
}

/// <summary>
/// Reads gta-v content from the player's own GTA V install (Legacy; Enhanced gen9 resources are reported as
/// unsupported): car meshes + textures from vehicles.rpf, the police siren and a horn from RESIDENT.rpf/vehicles.awc,
/// and the wanted star from commonmenu.ytd. Every row is reported Ok / Missing / Failed with what was tried.
/// </summary>
public sealed class GtaModule(GtaOptions? options = null) : IGameModule
{
    private readonly GtaOptions _opt = options ?? new GtaOptions();

    /// <summary>The options this module runs with (diagnostics and tests).</summary>
    public GtaOptions Options => _opt;

    public string Game => "gta-v";
    public string Short => "gta";

    private static readonly string[] VehicleRows = { "gta_mesh_infernus", "gta_mesh_sultan", "gta_mesh_blista", "gta_mesh_police" };
    public const string SirenRow = "gta_snd_siren", HornRow = "gta_snd_horn", StarRow = "gta_tex_wanted_star";

    public IEnumerable<string> FingerprintFiles(string gameDir)
    {
        var f = GameFolder.Find(gameDir, out _);
        if (f is null) yield break;
        foreach (var rel in new[] { Path.GetFileName(f.ExePath), "x64e.rpf", "update/update.rpf", "x64/audio/sfx/RESIDENT.rpf" })
        {
            var full = Path.Combine(f.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) yield return Path.GetRelativePath(gameDir, full).Replace('\\', '/');
        }
    }

    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct)
    {
        var log = ctx.Log;
        var rows = ctx.Rows.Select(r => r.Id).ToList();
        var sw = Stopwatch.StartNew();

        var found = GameFolder.Find(gameDir, out var tried);
        if (found is null)
        {
            foreach (var id in rows) ctx.Missing(id, $"GTA V folder not found ({tried})");
            return;
        }
        log.Info($"[gta] game folder {found.Dir} ({found.How}); edition {(found.Enhanced ? "Enhanced (gen9)" : "Legacy")}; exe {Path.GetFileName(found.ExePath)}");

        using var census = new GameArchives(found.Dir, GtaKeys.None, log);
        var disk = census.DiskArchives();
        log.Info($"[gta] {disk.Count} archives on disk");
        census.Census(disk);
        ct.ThrowIfCancellationRequested();

        GtaKeys keys;
        try { keys = LoadKeys(found, ctx, census, disk, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            log.Warn($"[gta] key setup failed: {e}");
            keys = new GtaKeys { AesProblem = $"key setup failed: {e.Message}", NgProblem = $"key setup failed: {e.Message}" };
        }
        using var arch = new GameArchives(found.Dir, keys, log);
        var reported = new HashSet<string>();
        void Run(string id, Action body)
        {
            if (!rows.Contains(id)) return;
            ct.ThrowIfCancellationRequested();
            ctx.Progress?.Invoke($"gta: {id}");
            try { body(); }
            catch (KeyUnavailableException e) { ctx.Missing(id, e.Message); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                // one bad asset (or a bug) must not cost the other rows
                log.Warn($"[gta] {id}: {e}");
                ctx.Failed(id, $"{e.GetType().Name}: {e.Message}");
            }
            reported.Add(id);
        }

        // vehicles
        var vehicleIndex = new VehicleIndex(arch, log, found.Enhanced);
        foreach (var id in VehicleRows) Run(id, () => ExtractVehicle(id, vehicleIndex, ctx));

        // sounds
        AwcFile? awc = null;
        string? awcProblem = null, awcFailure = null;
        string awcWhere = "";
        void LoadAwc()
        {
            if (awc is not null || awcProblem is not null || awcFailure is not null) return;
            var res = arch.OpenTop("x64/audio/sfx/RESIDENT.rpf");
            if (res is null) { awcProblem = $"x64/audio/sfx/RESIDENT.rpf: {arch.Errors.GetValueOrDefault("x64/audio/sfx/RESIDENT.rpf", "not found")}"; return; }
            arch.Walk(res, _ => false);
            log.Info($"[gta] AWCs in RESIDENT.rpf: {string.Join(", ", res.Files.Where(e => e.Name.EndsWith(".awc", StringComparison.OrdinalIgnoreCase)).Select(e => $"{e.Path} ({e.UncompressedSize / 1024} KiB{(e.Encrypted ? ", encrypted entry" : "")})"))}");
            var entry = res.Files.FirstOrDefault(e => string.Equals(e.Name, "vehicles.awc", StringComparison.OrdinalIgnoreCase));
            if (entry is null) { awcProblem = "vehicles.awc not in x64/audio/sfx/RESIDENT.rpf"; return; }
            awcWhere = entry.FullPath;
            try
            {
                awc = AwcFile.Parse(arch.ReadBinary(entry), keys.Ng?.AwcKey);
                SoundPicker.Census(awc, awcWhere, log);
            }
            catch (KeyUnavailableException e) { awcProblem = $"{awcWhere}: {e.Message}"; }
            catch (Exception e) when (e is InvalidDataException or IOException or EndOfStreamException or ArgumentException)
            {
                log.Warn($"[gta] {awcWhere}: {e}");
                awcFailure = $"{awcWhere}: {e.GetType().Name}: {e.Message}";
            }
        }
        bool AwcReady(string id)
        {
            LoadAwc();
            if (awcFailure is not null) { ctx.Failed(id, awcFailure); return false; }
            if (awc is null) { ctx.Missing(id, awcProblem ?? "vehicles.awc unavailable"); return false; }
            return true;
        }
        Run(SirenRow, () =>
        {
            if (!AwcReady(SirenRow)) return;
            var pick = SoundPicker.PickSiren(awc!);
            if (pick is null) { ctx.Missing(SirenRow, $"no siren stream in {awcWhere}: none of the sheet ids 0x0D329446/0x159C9182/0x161768E7 and no siren-named stream (see census in the log)"); return; }
            WriteSound(ctx, SirenRow, pick.Value.stream, $"{awcWhere} {pick.Value.why}");
        });
        Run(HornRow, () =>
        {
            if (!AwcReady(HornRow)) return;
            var pick = SoundPicker.PickHorn(awc!);
            if (pick is null) { ctx.Missing(HornRow, $"no stream in {awcWhere} matches any of {SoundPicker.HornNames.Length} horn_* names (see census in the log)"); return; }
            WriteSound(ctx, HornRow, pick.Value.stream, $"{awcWhere} {pick.Value.why}");
        });

        // wanted star
        Run(StarRow, () => ExtractStar(arch, ctx, disk, found.Enhanced));

        foreach (var id in rows.Where(r => !reported.Contains(r)))
            ctx.Failed(id, "not handled by the GTA module");
        log.Info($"[gta] done in {sw.Elapsed.TotalSeconds:F1} s");
    }

    // ---------------------------------------------------------------- keys

    private GtaKeys LoadKeys(GameFolder.Found found, ExtractContext ctx, GameArchives census, List<string> disk, CancellationToken ct)
    {
        var log = ctx.Log;
        var keysDir = _opt.KeysDir ?? DefaultKeysDir(ctx);
        ctx.Progress?.Invoke("gta: looking for the AES key in " + Path.GetFileName(found.ExePath));
        var aes = AesKeyFinder.Get(found.ExePath, keysDir, _opt.Hashes.AesKey, log, ct, out var aesDetail);
        if (aes is null || aesDetail.Contains("cache")) log.Info($"[gta] {aesDetail}");
        if (aes is null) return new GtaKeys { AesProblem = aesDetail, NgProblem = "needs the AES key first: " + aesDetail };

        // NG material is only fetched when an archive we read is NG-encrypted
        bool needNg = false;
        foreach (var rel in disk.Where(IsInteresting))
        {
            try
            {
                var p = census.ResolveCaseInsensitive(rel);
                if (p is null) continue;
                using var f = new RpfFile(p);
                var h = RpfArchive.PeekHeader(f);
                if (h is { } hh && hh.encryption != 0 && hh.encryption != RpfArchive.EncOpen && hh.encryption != RpfArchive.EncAes) { needNg = true; break; }
            }
            catch (IOException) { }
        }
        if (!needNg)
        {
            log.Info("[gta] none of the archives we read is NG-encrypted; not fetching NG keys");
            return new GtaKeys { Aes = aes, NgProblem = "not needed (no NG archives among the ones read)" };
        }

        ct.ThrowIfCancellationRequested();
        ctx.Progress?.Invoke("gta: fetching NG key blob");
        var blob = _opt.Magic.Fetch(log, ct, out var fetchError);
        if (blob is null)
            return new GtaKeys { Aes = aes, NgProblem = $"NG key blob unavailable from {_opt.Magic.Describe}: {fetchError}" };
        if (_opt.MagicSha256 is not null)
        {
            var sha = MagicData.Sha256Hex(blob);
            if (!string.Equals(sha, _opt.MagicSha256, StringComparison.OrdinalIgnoreCase))
                return new GtaKeys { Aes = aes, NgProblem = $"NG key blob from {_opt.Magic.Describe} has SHA-256 {sha}, expected {_opt.MagicSha256}; refusing it" };
            log.Info($"[gta] NG key blob SHA-256 verified ({blob.Length} bytes)");
        }
        var ng = MagicData.Decode(blob, aes, _opt.Hashes, log, out var ngDetail);
        return ng is null ? new GtaKeys { Aes = aes, NgProblem = ngDetail } : new GtaKeys { Aes = aes, Ng = ng };
    }

    private static bool IsInteresting(string rel)
    {
        var r = rel.ToLowerInvariant();
        return r == "x64e.rpf" || r == "common.rpf" || r.StartsWith("update/update") || r == "x64/audio/sfx/resident.rpf"
               || (r.StartsWith("x64") && r.EndsWith(".rpf") && !r.Contains('/'))
               || (r.StartsWith("update/x64/dlcpacks/") && r.EndsWith("/dlc.rpf"));
    }

    public static string DefaultKeysDir(ExtractContext ctx)
    {
        var stats = Path.GetFullPath(ctx.StatsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cache = string.Equals(Path.GetFileName(stats), "stats", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(stats)! : ctx.WorkDir;
        return Path.Combine(cache, "keys");
    }

    // ---------------------------------------------------------------- vehicles

    /// <summary>Finds vehicle files: x64e.rpf/levels/gta5/vehicles.rpf first (sheet locator), then
    /// update/x64/dlcpacks/*/dlc.rpf (any nested vehicles archive), then update/update.rpf.</summary>
    private sealed class VehicleIndex(GameArchives arch, Log log, bool enhanced)
    {
        public readonly GameArchives Arch = arch;
        public readonly bool Enhanced = enhanced;
        private bool _base, _dlc, _update, _meta;
        public VehiclesMeta Meta { get; } = new();
        private readonly Dictionary<RpfEntry, List<TextureEntry>?> _ytds = new();

        private static bool VehicleArchive(string vp) => vp.Contains("vehicle", StringComparison.OrdinalIgnoreCase);

        public void EnsureBase()
        {
            if (_base) return;
            _base = true;
            var a = Arch.OpenTop("x64e.rpf");
            if (a is not null) Arch.Walk(a, vp => vp.EndsWith("/levels/gta5/vehicles.rpf", StringComparison.OrdinalIgnoreCase));
        }

        public void EnsureDlc()
        {
            if (_dlc) return;
            _dlc = true;
            foreach (var rel in Arch.DiskArchives().Where(r => r.StartsWith("update/x64/dlcpacks/", StringComparison.OrdinalIgnoreCase) && r.EndsWith("/dlc.rpf", StringComparison.OrdinalIgnoreCase)))
            {
                var a = Arch.OpenTop(rel);
                if (a is not null) Arch.Walk(a, VehicleArchive);
            }
        }

        public void EnsureUpdate()
        {
            if (_update) return;
            _update = true;
            foreach (var rel in new[] { "update/update.rpf", "update/update2.rpf" })
            {
                var a = Arch.OpenTop(rel);
                if (a is not null) Arch.Walk(a, VehicleArchive);
            }
        }

        public void EnsureMeta()
        {
            if (_meta) return;
            _meta = true;
            EnsureUpdate();
            var common = Arch.OpenTop("common.rpf");
            if (common is not null) Arch.Walk(common, _ => false);
            var metas = Arch.Find("vehicles.meta")
                .OrderBy(e => e.FullPath.StartsWith("common.rpf", StringComparison.OrdinalIgnoreCase) ? 0 : e.FullPath.Contains("dlcpacks") ? 2 : 1)
                .ToList();
            foreach (var e in metas)
            {
                try { Meta.Merge(Arch.ReadBinary(e), e.FullPath); }
                catch (Exception ex) when (ex is KeyUnavailableException or InvalidDataException or IOException)
                { log.Warn($"[gta] cannot read {e.FullPath}: {ex.Message}"); }
            }
            log.Info($"[gta] vehicles.meta sources: {(Meta.Sources.Count == 0 ? "none found (textures fall back to <model>.ytd and vehshare.ytd)" : string.Join(", ", Meta.Sources))}; " +
                     $"{Meta.TxdOfModel.Count} models, {Meta.ParentOfTxd.Count} txd relationships");
        }

        private static int Rank(RpfEntry e)
        {
            var p = e.FullPath.ToLowerInvariant();
            if (p.StartsWith("x64e.rpf/levels/gta5/vehicles.rpf/")) return 0;
            if (p.StartsWith("update/x64/dlcpacks/")) return 1;
            if (p.StartsWith("update/update")) return 2;
            return 3;
        }

        /// <summary>Candidates for a file name in locator / fallback order (each tier opened only when needed).</summary>
        public List<RpfEntry> Find(string fileName, bool all = false)
        {
            EnsureBase();
            var list = Arch.Find(fileName);
            if (list.Count == 0 || all) { EnsureDlc(); list = Arch.Find(fileName); }
            if (list.Count == 0 || all) { EnsureUpdate(); list = Arch.Find(fileName); }
            return list.OrderBy(Rank).ToList();
        }

        public List<TextureEntry>? Ytd(RpfEntry e)
        {
            if (_ytds.TryGetValue(e, out var cached)) return cached;
            List<TextureEntry>? list = null;
            try
            {
                var r = Resource.FromRaw(Arch.ReadResourceRaw(e), e.FullPath, e.SysFlags, e.GfxFlags);
                if (r.Version != TextureDictionaryReader.Gen8YtdVersion)
                    log.Warn($"[gta] {e.FullPath}: resource version {r.Version} (gen8 ytd is {TextureDictionaryReader.Gen8YtdVersion}){(r.Version == 5 ? "; Enhanced gen9 textures are not supported" : "")}");
                if (r.Version != 5)
                {
                    var problems = new List<string>();
                    list = TextureDictionaryReader.Read(r, 0, problems);
                    log.Info($"[gta] {e.FullPath}: {list.Count} textures{(problems.Count > 0 ? $"; skipped: {string.Join("; ", problems.Take(8))}" : "")}");
                }
            }
            catch (InvalidDataException ex) { log.Warn($"[gta] {e.FullPath}: {ex.Message}"); }
            _ytds[e] = list;
            return list;
        }
    }

    private static void ExtractVehicle(string id, VehicleIndex vi, ExtractContext ctx)
    {
        var log = ctx.Log;
        var row = ctx.Row(id);
        string model = id.Substring("gta_mesh_".Length);
        var candidates = vi.Find(model + ".yft");
        log.Info($"[gta] {id}: {model}.yft candidates: {(candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(c => c.FullPath)))}");
        if (candidates.Count == 0)
        {
            var errs = vi.Arch.Errors.Where(kv => kv.Value != "not found").Select(kv => $"{kv.Key}: {kv.Value}").Take(4).ToList();
            ctx.Missing(id, $"{model}.yft not found in x64e.rpf/levels/gta5/vehicles.rpf, update/x64/dlcpacks/*/dlc.rpf or update/update.rpf" +
                            (errs.Count > 0 ? $" (archive errors: {string.Join("; ", errs)})" : ""));
            return;
        }

        var problems = new List<string>();
        foreach (var yft in candidates)
        {
            if (yft.Kind != RpfEntryKind.Resource) { problems.Add($"{yft.FullPath}: not a resource entry"); continue; }
            Resource r;
            try { r = Resource.FromRaw(vi.Arch.ReadResourceRaw(yft), yft.FullPath, yft.SysFlags, yft.GfxFlags); }
            catch (InvalidDataException e) { problems.Add(e.Message); continue; }
            if (r.Version != FragmentReader.Gen8YftVersion)
            {
                problems.Add($"{yft.FullPath}: resource version {r.Version}, expected {FragmentReader.Gen8YftVersion}" +
                             (r.Version == 171 || vi.Enhanced ? " (Enhanced gen9 fragment; not supported)" : ""));
                if (r.Version == 171) continue;
            }
            FragmentData frag;
            try { frag = FragmentReader.Read(r); }
            catch (InvalidDataException e) { problems.Add($"{yft.FullPath}: {e.Message}"); continue; }
            foreach (var p in frag.Drawable.Problems.Concat(frag.Problems).Take(20)) log.Warn($"[gta] {yft.FullPath}: {p}");
            if (!frag.Drawable.HasGeometry) { problems.Add($"{yft.FullPath}: no geometry in the main drawable"); continue; }
            log.Info($"[gta] {yft.FullPath}: fragment '{frag.Name}', {frag.Drawable.Shaders.Count} shaders, {frag.Drawable.Bones.Count} bones, " +
                     $"{frag.Children.Count} physics children (tags {string.Join(",", frag.Children.Select(c => c.BoneTag))}), " +
                     $"tris per LOD {string.Join("/", Enumerable.Range(0, 4).Select(frag.Drawable.TriangleCount))}, embedded textures {frag.Drawable.EmbeddedTextures.Count}");
            foreach (var (s, i) in frag.Drawable.Shaders.Select((s, i) => (s, i)))
                log.Info($"[gta]   shader {i}: {s} textures [{string.Join(", ", s.Textures.Select(kv => $"{ParamName(kv.Key)}={kv.Value}"))}]");

            vi.EnsureMeta();
            var chain = vi.Meta.TxdChain(model);
            log.Info($"[gta] {model}: texture dictionary chain {string.Join(" -> ", chain)}");
            TextureEntry? FindTexture(string name)
            {
                foreach (var txd in chain)
                    foreach (var e in vi.Find(txd + ".ytd").OrderBy(e => e.Archive == yft.Archive ? 0 : 1))
                    {
                        var t = vi.Ytd(e)?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (t is not null) return t;
                    }
                return null;
            }

            var builder = new VehicleBuilder(model, frag, FindTexture, row.MaxTris, log);
            var result = builder.Build();
            if (result.Mesh.TriangleCount == 0) { problems.Add($"{yft.FullPath}: assembled mesh is empty"); continue; }
            var sanity = Sanity(result.Mesh);
            foreach (var (material, img) in result.Textures) ctx.MaterialTexture(material, img);
            ctx.Mesh(id, result.Mesh, $"{yft.FullPath}; {string.Join("; ", result.Notes)}; {result.Textures.Count} textures; {sanity}");
            return;
        }
        ctx.Failed(id, string.Join(" | ", problems));
    }

    private static string ParamName(uint h) =>
        h == ShaderParams.DiffuseSampler ? "DiffuseSampler" : h == ShaderParams.BumpSampler ? "BumpSampler" : h == ShaderParams.SpecSampler ? "SpecSampler"
        : h == ShaderParams.DiffuseSampler2 ? "DiffuseSampler2" : h == ShaderParams.DirtSampler ? "DirtSampler" : $"0x{h:X8}";

    /// <summary>Size of the assembled car in blocks, as a plausibility note (a car is roughly 4-6 x 1.2-2 x 1.8-2.5).</summary>
    private static string Sanity(MeshData m)
    {
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var s in m.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                minX = Math.Min(minX, s.Vertices[i]); maxX = Math.Max(maxX, s.Vertices[i]); maxY = Math.Max(maxY, s.Vertices[i + 1]);
                minZ = Math.Min(minZ, s.Vertices[i + 2]); maxZ = Math.Max(maxZ, s.Vertices[i + 2]);
            }
        float w = maxX - minX, h = maxY, l = maxZ - minZ;
        bool plausible = l is > 2.5f and < 9f && w is > 1.2f and < 3.5f && h is > 0.8f and < 4f && l > w;
        return $"size {w:F2} x {h:F2} x {l:F2} m (w x h x l){(plausible ? "" : " - UNUSUAL for a car, check orientation/scale")}";
    }

    // ---------------------------------------------------------------- sounds

    private static void WriteSound(ExtractContext ctx, string id, AwcStream s, string where)
    {
        var pcm = s.DecodePcm();
        if (pcm.Length == 0) { ctx.Failed(id, $"{where}: decoded no samples"); return; }
        int peak = pcm.Max(v => Math.Abs((int)v));
        var ogg = OggEncode.Encode(pcm, s.SampleRate, ctx.Log, id, out var detail);
        if (ogg is null) { ctx.Failed(id, $"{where}: {detail}"); return; }
        ctx.Sound(id, ogg, $"{where}; {s.CodecName} {s.SampleRate} Hz {s.Seconds:F2} s{(s.Loops ? $", loop@{s.LoopPoint}" : "")}{(s.Note is null ? "" : ", " + s.Note)}, peak {peak}; {detail}");
    }

    // ---------------------------------------------------------------- wanted star

    private static void ExtractStar(GameArchives arch, ExtractContext ctx, List<string> disk, bool enhanced)
    {
        var log = ctx.Log;
        static bool TextureArchive(string vp) =>
            vp.Contains("/textures", StringComparison.OrdinalIgnoreCase) || vp.Contains("txd", StringComparison.OrdinalIgnoreCase);
        var tops = new List<string> { "update/update.rpf", "update/update2.rpf" };
        tops.AddRange(disk.Where(r => !r.Contains('/') && r.StartsWith("x64", StringComparison.OrdinalIgnoreCase)));
        tops.AddRange(disk.Where(r => r.StartsWith("x64/textures/", StringComparison.OrdinalIgnoreCase)));
        tops.Add("common.rpf");
        foreach (var rel in tops.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var a = arch.OpenTop(rel);
            if (a is not null) arch.Walk(a, TextureArchive);
        }
        int Rank(RpfEntry e) => e.FullPath.StartsWith("update/update.rpf", StringComparison.OrdinalIgnoreCase) ? 0
            : e.FullPath.StartsWith("update/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        var dicts = arch.Find("commonmenu.ytd").OrderBy(Rank).ToList();
        log.Info($"[gta] commonmenu.ytd candidates: {(dicts.Count == 0 ? "none" : string.Join(", ", dicts.Select(d => d.FullPath)))}");
        var tried = new List<string>();
        foreach (var e in dicts.Concat(arch.Index.Where(x => x.Name.EndsWith(".ytd", StringComparison.OrdinalIgnoreCase) && !dicts.Contains(x)
                                                             && x.FullPath.Contains("script_txd", StringComparison.OrdinalIgnoreCase)).OrderBy(Rank)))
        {
            if (e.Kind != RpfEntryKind.Resource) continue;
            Resource r;
            try { r = Resource.FromRaw(arch.ReadResourceRaw(e), e.FullPath, e.SysFlags, e.GfxFlags); }
            catch (InvalidDataException ex) { tried.Add(ex.Message); continue; }
            if (r.Version == 5) { tried.Add($"{e.FullPath}: Enhanced gen9 texture dictionary, not supported"); continue; }
            var texs = TextureDictionaryReader.Read(r, 0);
            var star = texs.FirstOrDefault(t => string.Equals(t.Name, "shop_new_star", StringComparison.OrdinalIgnoreCase));
            if (star is null)
            {
                tried.Add($"{e.FullPath}: no shop_new_star ({texs.Count} textures{(texs.Any(t => t.Name.Contains("star", StringComparison.OrdinalIgnoreCase)) ? ", star-like: " + string.Join(",", texs.Where(t => t.Name.Contains("star", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).Take(6)) : "")})");
                continue;
            }
            var img = TextureDecoder.Decode(star);
            ctx.Texture(StarRow, img, 256, $"{e.FullPath}/{star.Name} {TextureFormats.Name(star.Format)}");
            return;
        }
        var errs = arch.Errors.Where(kv => kv.Value != "not found").Select(kv => $"{kv.Key}: {kv.Value}").Take(4).ToList();
        ctx.Missing(StarRow, (dicts.Count == 0 ? "commonmenu.ytd not found in update.rpf, x64*.rpf or common.rpf texture archives" : string.Join(" | ", tried.Take(6)))
                             + (errs.Count > 0 ? $" (archive errors: {string.Join("; ", errs)})" : "") + (enhanced ? " (Enhanced edition)" : ""));
    }
}
