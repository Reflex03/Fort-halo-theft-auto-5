using System.Globalization;
using System.Text.Json.Nodes;
using CthExtract.Core;
using CthExtract.Core.Generated;

namespace CthExtract.Halo;

/// <summary>
/// Reads halo-mcc content (sources sheet rows with game "halo-mcc") from the player's own Halo: The Master Chief
/// Collection install: Halo 3 weapon models + diffuse textures, fire/throw/shield sounds, weapon and shield stats.
/// Never writes inside the game folder; every game file is opened read-only with FileShare.ReadWrite | Delete.
/// Every row ends as Ok, Missing (the content is not there) or Failed (it is there but could not be converted).
/// </summary>
public sealed class HaloModule : IGameModule
{
    public string Game => "halo-mcc";
    public string Short => "halo";

    private readonly Func<string, IHaloMap> openMap;

    public HaloModule() : this(null) { }

    /// <param name="openMap">Opens a .map file; defaults to Reclaimer. Tests pass an in-memory implementation.</param>
    public HaloModule(Func<string, IHaloMap>? openMap) => this.openMap = openMap ?? ReclaimerMap.Open;

    public IEnumerable<string> FingerprintFiles(string gameDir)
    {
        var install = HaloInstall.Locate(gameDir);
        if (install is null)
            return new[] { @"halo3/maps/riverworld.map", @"halo3/maps/shared.map", @"halo3/fmod/pc/sfx.fsb", @"halo3/fmod/pc/sfx.fsb.info" };
        var baseDir = Directory.Exists(gameDir) ? Path.GetFullPath(gameDir) : Path.GetDirectoryName(Path.GetFullPath(gameDir))!;
        return install.FingerprintFiles().Select(f => Path.GetRelativePath(baseDir, f)).ToList();
    }

    public void Extract(string gameDir, ExtractContext ctx, CancellationToken ct)
    {
        var log = ctx.Log;
        var rows = ctx.Rows;
        log.Info($"[halo] Halo module: {rows.Count} sources rows, MCC folder '{gameDir}'");

        var install = HaloInstall.Locate(gameDir, m => log.Info("[halo] " + m));
        if (install is null)
        {
            foreach (var r in rows) ctx.Missing(r.Id, $"no Halo 3 maps found from '{gameDir}' (looked for halo3\\maps\\*.map in it, its parents and MCC subfolders)");
            return;
        }

        var weaponPlans = HaloPlan.Weapons(rows, WeaponIds.All, m => log.Warn(m));
        var soundPlans = HaloPlan.OtherSounds(rows);
        using var maps = new MapSet(install, openMap, log);
        using var banks = SoundBanks.Load(install, log);
        string? noBanks = banks.Banks.Count == 0
            ? $"no FMOD banks (*.fsb + *.fsb.info) in {install.FmodDir ?? Path.Combine(install.Halo3Dir, "fmod", "pc") + " (folder missing)"}"
            : null;
        if (noBanks != null) log.Warn("[halo] " + noBanks);

        var weaponStats = new Dictionary<string, JsonObject>();
        var statsNotes = new List<string>();

        foreach (var plan in weaponPlans)
        {
            ct.ThrowIfCancellationRequested();
            // lazily opens maps (search order) until the tag is found; logs each route
            var first = maps.Resolve(plan).FirstOrDefault();

            if (plan.MeshRow is { } meshRow)
                Guard(ctx, meshRow.Id, () => ExportMesh(ctx, plan, meshRow, maps.Resolve(plan), maps));

            if (first.Map != null)
            {
                try
                {
                    var stats = ReadStats(first.Map, first.Tag, plan, log, out var notes);
                    if (stats != null) weaponStats[plan.WeaponId] = stats;
                    foreach (var n in notes) statsNotes.Add($"{plan.WeaponId}: {n}");
                }
                catch (Exception e)
                {
                    statsNotes.Add($"{plan.WeaponId}: {e.GetType().Name}: {e.Message}");
                    log.Warn($"[halo] stats for {plan.WeaponId} failed: {e}");
                }
            }
            else statsNotes.Add($"{plan.WeaponId}: tag not found");

            if (plan.FireSoundRow is { } fireRow)
            {
                if (noBanks != null) ctx.Missing(fireRow.Id, noBanks);
                else Guard(ctx, fireRow.Id, () => ExportFireSound(ctx, plan, fireRow, first, banks));
            }
        }

        foreach (var sp in soundPlans)
        {
            ct.ThrowIfCancellationRequested();
            if (noBanks != null) { ctx.Missing(sp.Row.Id, noBanks); continue; }
            Guard(ctx, sp.Row.Id, () => ExportOtherSound(ctx, sp, maps, banks));
        }

        // ---- stats rows ----
        var weaponsRow = rows.FirstOrDefault(r => r.Id == "halo_stats_weapons");
        if (weaponsRow != null)
        {
            if (weaponStats.Count == 0 && maps.NoneReadable)
                ctx.Failed(weaponsRow.Id, $"no Halo 3 map could be read: {maps.TriedSummary}");
            else if (weaponStats.Count == 0)
                ctx.Missing(weaponsRow.Id, "no weapon tags readable: " + string.Join("; ", statsNotes.Take(6)));
            else
            {
                foreach (var (id, o) in weaponStats) ctx.Stats(weaponsRow.Id, id, o);
                try { SelfCheck(weaponStats, log); }
                catch (Exception e) { log.Warn($"[halo] self-check failed: {e.Message}"); }
                ctx.StatsDone(weaponsRow.Id, $"{weaponStats.Count}/{weaponPlans.Count} weapons read" +
                    (statsNotes.Count > 0 ? "; " + string.Join("; ", statsNotes.Take(12)) : ""));
            }
        }

        var shieldRow = rows.FirstOrDefault(r => r.Id == "halo_stats_shield");
        if (shieldRow != null)
            Guard(ctx, shieldRow.Id, () => ExportShieldStats(ctx, shieldRow, maps));

        log.Info($"[halo] done; maps opened: {string.Join(", ", maps.OpenedNames)}");
    }

    /// <summary>Run one row; anything thrown becomes a Failed row with the exception message.</summary>
    private static void Guard(ExtractContext ctx, string rowId, Func<RowOutcome> body)
    {
        try
        {
            var o = body();
            if (o.Missing != null) ctx.Missing(rowId, o.Missing);
            else if (o.Failed != null) ctx.Failed(rowId, o.Failed);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ctx.Log.Warn($"[halo] {rowId}: {e}");
            ctx.Failed(rowId, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Ok (already reported through ctx), Missing or Failed.</summary>
    private readonly record struct RowOutcome(string? Missing, string? Failed)
    {
        public static RowOutcome Ok => default;
        public static RowOutcome Miss(string why) => new(why, null);
        public static RowOutcome Fail(string why) => new(null, why);
    }

    // ------------------------------------------------------------------ meshes

    private static RowOutcome ExportMesh(ExtractContext ctx, WeaponPlan plan, SourceRow row, IEnumerable<(IHaloMap Map, HaloTag Tag, string Route)> found, MapSet maps)
    {
        var problems = new List<string>();
        foreach (var (map, tag, route) in found)
        {
            try
            {
                if (TryExportMeshFrom(ctx, plan, row, map, tag, route, problems)) return RowOutcome.Ok;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                problems.Add($"{map.Name()}: {e.GetType().Name}: {e.Message}");
            }
        }
        if (problems.Count == 0)
            return maps.NoneReadable
                ? RowOutcome.Fail($"no Halo 3 map could be read: {maps.TriedSummary}")
                : RowOutcome.Miss($"{plan.TagClass} '{plan.TagPath}' not found in {maps.TriedCount} map(s): {maps.TriedSummary}");
        return RowOutcome.Fail(string.Join("; ", problems));
    }

    /// <summary>One map's attempt at a weapon mesh: true when the row was written, else a reason in <paramref name="problems"/>.</summary>
    private static bool TryExportMeshFrom(ExtractContext ctx, WeaponPlan plan, SourceRow row, IHaloMap map, HaloTag tag, string route, List<string> problems)
    {
        var w = new TagWalker(map, m => ctx.Log.Info("[halo] " + m));
        var hlmt = w.ModelOf(tag);
        if (hlmt is null) { problems.Add($"{map.Name()}: {tag.Name} has no hlmt model"); return false; }
        var mode = w.RenderModelOf(hlmt);
        if (mode is null) { problems.Add($"{map.Name()}: {hlmt.Name} has no render model"); return false; }
        string? variantName = null, handle = null;
        Dictionary<string, string> variant = new();
        try { variantName = w.DefaultVariantName(tag); variant = w.VariantPermutations(hlmt, variantName); }
        catch (Exception e) { ctx.Log.Info($"[halo] {row.Id}: model variants unreadable ({e.Message}); using default permutations"); }
        try { handle = w.HandleNode(tag); }
        catch (Exception e) { ctx.Log.Info($"[halo] {row.Id}: handle node unreadable ({e.Message})"); }
        ctx.Log.Info($"[halo] {row.Id}: {map.Name()} {tag} via {route} -> {hlmt} -> {mode}; variant '{variantName ?? "-"}' ({variant.Count} region choices)");

        Reclaimer.Geometry.Scene? scene;
        try { scene = map.Geometry(mode); }
        catch (Exception e) { problems.Add($"{map.Name()}: geometry of {mode.Name} failed ({e.GetType().Name}: {e.Message}; is shared.map present?)"); return false; }
        if (scene is null) { problems.Add($"{map.Name()}: no geometry for {mode.Name}"); return false; }

        var built = MeshBuilder.Build(scene, new MeshBuildOptions
        {
            MaterialPrefix = $"halo/{plan.WeaponId}",
            Held = plan.Held,
            HandleNode = handle,
            VariantPermutations = variant,
        });
        if (built.Mesh.TriangleCount == 0) { problems.Add($"{map.Name()}: {mode.Name} produced no triangles ({string.Join("; ", built.Notes)})"); return false; }
        var (min, max) = Extent(built.Mesh);
        var size = max - min;
        if (!float.IsFinite(size.X + size.Y + size.Z) || size.Z <= 1e-4f || size.Length() > 10f)
        {
            problems.Add($"{map.Name()}: implausible mesh size {size} m");
            return false;
        }

        var texNotes = new List<string>();
        var textures = built.Materials.Select(m => TextureExport.Diffuse(m, texNotes)).ToList();
        var detail = $"{tag.Name} in {map.Name()} ({route}); {built.Summary()}; {built.Materials.Count} material(s)" +
                     (built.Notes.Count + texNotes.Count > 0 ? "; " + string.Join("; ", built.Notes.Concat(texNotes)) : "");
        ctx.Mesh(row.Id, built.Mesh, detail);
        for (int i = 0; i < textures.Count; i++)
            ctx.MaterialTexture($"halo/{plan.WeaponId}/{i}", textures[i]);
        return true;
    }

    private static (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) Extent(MeshData m)
    {
        var min = new System.Numerics.Vector3(float.MaxValue); var max = new System.Numerics.Vector3(float.MinValue);
        foreach (var s in m.Submeshes)
            for (int i = 0; i < s.Vertices.Count; i += 8)
            {
                var p = new System.Numerics.Vector3(s.Vertices[i], s.Vertices[i + 1], s.Vertices[i + 2]);
                min = System.Numerics.Vector3.Min(min, p); max = System.Numerics.Vector3.Max(max, p);
            }
        return (min, max);
    }

    // ------------------------------------------------------------------ stats

    private static JsonObject? ReadStats(IHaloMap map, HaloTag tag, WeaponPlan plan, Log log, out List<string> notes)
    {
        notes = new List<string>();
        var w = new TagWalker(map);
        RawWeaponStats raw;
        if (tag.Class == "weap") raw = w.ReadWeapon(tag, plan.Kind);
        else
        {
            // grenade: the projectile matg pairs with this equipment, else a proj with the same path
            raw = new RawWeaponStats();
            var proj = w.Grenades().FirstOrDefault(g => g.Equipment?.Index == tag.Index).Projectile
                       ?? w.Find("proj", tag.Name)
                       ?? w.OfClass("proj").FirstOrDefault(p => string.Equals(p.Leaf, tag.Leaf, StringComparison.OrdinalIgnoreCase));
            if (proj is null) { notes.Add("no projectile for the grenade"); return null; }
            w.ReadProjectile(proj, plan.Kind, raw);
        }
        var o = StatConversions.Weapon(raw, plan.Kind, notes);
        // where each value came from goes to the log, not into the stats file (keys there are weapons-sheet columns only)
        log.Info($"[halo] stats {plan.WeaponId}: {map.Name()}: {tag.Name}" + (raw.ProjectileTag != null ? $" / {raw.ProjectileTag}" : "") +
                  (raw.DamageTag != null ? $" / {raw.DamageTag} ({raw.DamageSource})" : "") + $" -> {o.ToJsonString()}");
        return o;
    }

    /// <summary>Compare with values Halo 3 players know (Halopedia / field note), so a first real run shows misreads.</summary>
    private static void SelfCheck(Dictionary<string, JsonObject> stats, Log log)
    {
        void Check(string id, string key, double expected)
        {
            if (!stats.TryGetValue(id, out var o) || Number(o[key]) is not { } v) { log.Info($"[halo] self-check {id}.{key}: not read (reference {expected})"); return; }
            bool ok = Math.Abs(v - expected) <= Math.Max(0.5, expected * 0.15);
            log.Info($"[halo] self-check {id}.{key} = {v.ToString(CultureInfo.InvariantCulture)} (reference {expected}) {(ok ? "OK" : "DIFFERS: check offsets")}");
        }
        Check("assault_rifle", "damage", 7.5); Check("assault_rifle", "clip", 32);
        Check("battle_rifle", "damage", 6); Check("battle_rifle", "clip", 36); Check("battle_rifle", "burst", 3);
        Check("sniper_rifle", "damage", 80); Check("shotgun", "pellets", 15);
    }

    /// <summary>A JSON number as double whether it was stored as int or double.</summary>
    internal static double? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out float f)) return f;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out System.Text.Json.JsonElement e) && e.ValueKind == System.Text.Json.JsonValueKind.Number) return e.GetDouble();
        return null;
    }

    private static RowOutcome ExportShieldStats(ExtractContext ctx, SourceRow row, MapSet maps)
    {
        var tried = new List<string>();
        foreach (var map in maps.Maps())
        {
            try
            {
                if (TryShieldFrom(ctx, row, map, tried)) return RowOutcome.Ok;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                tried.Add($"{map.Name()}: {e.GetType().Name}: {e.Message}");
            }
        }
        if (tried.Count > 0) return RowOutcome.Miss(string.Join("; ", tried));
        return maps.NoneReadable ? RowOutcome.Fail($"no Halo 3 map could be read: {maps.TriedSummary}")
                                 : RowOutcome.Miss("no scenario map (only shared resource caches) in the maps folder");
    }

    private static bool TryShieldFrom(ExtractContext ctx, SourceRow row, IHaloMap map, List<string> tried)
    {
        var w = new TagWalker(map, m => ctx.Log.Info("[halo] " + m));
        var biped = w.SpartanBiped();
        if (biped is null) { tried.Add($"{map.Name()}: no spartan biped"); return false; }
        var hlmt = w.ModelOf(biped.Value.Biped);
        long di = hlmt is null ? -1 : w.DamageInfo(hlmt);
        if (di < 0) { tried.Add($"{map.Name()}: {biped.Value.Biped.Name} has no damage info"); return false; }
        var raw = w.ReadShield(di, $"{map.Name()}: {biped.Value.Biped.Name} ({biped.Value.Route})");
        var notes = new List<string>();
        var o = StatConversions.Shield(raw, notes);
        if (o["rechargeDelayTicks"] is null && o["rechargePerTick"] is null) { tried.Add($"{map.Name()}: {string.Join("; ", notes)}"); return false; }
        ctx.Log.Info($"[halo] stats shield: {raw.Source} -> {o.ToJsonString()}");
        ctx.Stats(row.Id, "shield", o);
        ctx.StatsDone(row.Id, $"{raw.Source}: shield {raw.ShieldVitality}, body {raw.BodyVitality}, stun {raw.StunSeconds}s, recharge {raw.RechargeSeconds}s" +
                              (notes.Count > 0 ? "; " + string.Join("; ", notes) : ""));
        return true;
    }

    // ------------------------------------------------------------------ sounds

    private sealed record Candidate(SoundBank Bank, FsbInfoRecord Record, string Route);

    private static RowOutcome ExportFireSound(ExtractContext ctx, WeaponPlan plan, SourceRow row, (IHaloMap Map, HaloTag Tag, string Route) found, SoundBanks banks)
    {
        var cands = new List<Candidate>();
        var trail = new List<string>();
        if (found.Map != null && found.Tag.Class == "weap")
        try
        {
            var w = new TagWalker(found.Map, m => trail.Add(m));
            var effects = w.FiringEffects(found.Tag);
            if (effects.Count == 0) trail.Add("weapon has no firing effect");
            foreach (var effe in effects)
            {
                var snds = w.SoundsOfEffect(effe, out var route);
                if (snds.Count == 0) trail.Add($"{effe.Name}: no snd! ({route})");
                // weapon_fire sound class first, then names with "fire"
                foreach (var snd in snds.OrderBy(s => SoundClassRank(w, s)).ThenBy(s => s.Name.Contains("fire", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                    AddHashes(w, snd, $"{found.Tag.Leaf} -> effe '{effe.Name}' ({route}) -> snd! '{snd.Name}'", cands, trail, banks);
            }
            // snd! tags named like the sheet pattern (sound\weapons\<weapon>\...fire...)
            if (plan.FireSoundPattern != null)
            {
                var rx = FsbInfo.PatternRegex(plan.FireSoundPattern, anchored: false);
                foreach (var snd in w.OfClass("snd!").Where(s => rx.IsMatch(s.Name)).OrderBy(s => s.Name.Length).Take(8))
                    AddHashes(w, snd, $"snd! by name '{snd.Name}'", cands, trail, banks);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trail.Add($"tag chain stopped: {e.GetType().Name}: {e.Message}");
        }
        AddPattern(plan.FireSoundPattern, SoundPurpose.Fire, cands, trail, banks);
        return DecodeFirst(ctx, row, cands, trail, plan.FireSoundPattern);
    }

    private static RowOutcome ExportOtherSound(ExtractContext ctx, SoundPlan sp, MapSet maps, SoundBanks banks)
    {
        var cands = new List<Candidate>();
        var trail = new List<string>();
        var map = maps.Maps().FirstOrDefault();
        if (map is null) trail.Add("no map could be opened");
        else try
        {
            var w = new TagWalker(map, m => trail.Add(m));
            var effects = new List<(HaloTag Effe, string Route)>();
            if (sp.Purpose == SoundPurpose.Throw)
            {
                var g = w.Grenades();
                foreach (var (thr, eq, _) in g.Where(x => x.Throw != null))
                    effects.Add((thr!, $"matg grenade ({eq?.Leaf ?? "?"}) throwing effect"));
                if (g.Count == 0) trail.Add("matg has no grenades block");
            }
            else
            {
                var biped = w.SpartanBiped();
                var hlmt = biped is null ? null : w.ModelOf(biped.Value.Biped);
                long di = hlmt is null ? -1 : w.DamageInfo(hlmt);
                if (di >= 0)
                {
                    int off = sp.Purpose == SoundPurpose.ShieldRecharge ? HaloLayout.Hlmt.DamageInfo.ShieldRechargingEffect : HaloLayout.Hlmt.DamageInfo.ShieldDepletedEffect;
                    var effe = w.F.TagRef(di + off, "effe");
                    if (effe != null) effects.Add((effe, $"{biped!.Value.Biped.Leaf} hlmt shield {(sp.Purpose == SoundPurpose.ShieldRecharge ? "recharging" : "depleted")} effect"));
                    else trail.Add("biped hlmt has no shield effect");
                }
                else trail.Add("no spartan biped damage info");
            }
            foreach (var (effe, route) in effects)
            {
                var snds = w.SoundsOfEffect(effe, out var r2);
                if (snds.Count == 0) trail.Add($"{effe.Name}: no snd! ({r2})");
                foreach (var snd in snds) AddHashes(w, snd, $"{route} '{effe.Name}' -> snd! '{snd.Name}'", cands, trail, banks);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trail.Add($"tag chain stopped: {e.GetType().Name}: {e.Message}");
        }
        AddPattern(sp.Pattern, sp.Purpose, cands, trail, banks);
        return DecodeFirst(ctx, sp.Row, cands, trail, sp.Pattern);
    }

    private static int SoundClassRank(TagWalker w, HaloTag snd)
    {
        try { return w.F.U8(w.F.Address(snd) + HaloLayout.Snd.SoundClass) == 4 ? 0 : 1; }
        catch { return 1; }
    }

    private static void AddHashes(TagWalker w, HaloTag snd, string route, List<Candidate> cands, List<string> trail, SoundBanks banks)
    {
        if (trail.Contains("visited " + snd.Name)) return;
        trail.Add("visited " + snd.Name);
        var hashes = w.SoundHashes(snd, out var detail);
        if (hashes.Count == 0) { trail.Add($"{snd.Name}: {detail}"); return; }
        foreach (var h in hashes)
        {
            var hit = banks.FindHash(h);
            if (hit is null) { trail.Add($"{snd.Name}: hash 0x{h:x8} not in any .fsb.info"); continue; }
            if (!cands.Any(c => c.Bank == hit.Value.Bank && c.Record.Index == hit.Value.Record.Index))
                cands.Add(new Candidate(hit.Value.Bank, hit.Value.Record, $"{route} -> hash 0x{h:x8} ({detail})"));
        }
    }

    private static void AddPattern(string? pattern, SoundPurpose purpose, List<Candidate> cands, List<string> trail, SoundBanks banks)
    {
        if (pattern is null) { trail.Add("no filename pattern in the sheet"); return; }
        var matches = banks.Match(pattern);
        if (matches.Count == 0) { trail.Add($"no .fsb.info filename matches '{pattern}'"); return; }
        foreach (var (bank, rec) in SoundPicker.Rank(matches, purpose).Take(5))
            if (!cands.Any(c => c.Bank == bank && c.Record.Index == rec.Index))
                cands.Add(new Candidate(bank, rec, $"filename match '{pattern}' ({matches.Count} candidates)"));
    }

    private static RowOutcome DecodeFirst(ExtractContext ctx, SourceRow row, List<Candidate> cands, List<string> trail, string? pattern)
    {
        trail.RemoveAll(t => t.StartsWith("visited ", StringComparison.Ordinal));
        foreach (var t in trail) ctx.Log.Info($"[halo] {row.Id}: {t}");
        if (cands.Count == 0) return RowOutcome.Miss(string.Join("; ", trail.Take(6)));
        var failures = new List<string>();
        var rx = pattern is null ? null : FsbInfo.PatternRegex(pattern, anchored: false);
        foreach (var c in cands.Take(8))
        {
            var notes = new List<string>();
            try
            {
                var ogg = SoundDecode.ToOgg(c.Bank, c.Record, notes);
                if (!Ogg.LooksLikeVorbis(ogg)) { failures.Add($"{c.Bank.Name}#{c.Record.Index}: output is not Ogg Vorbis"); continue; }
                string agree = rx is null ? "" : rx.IsMatch(c.Record.FileName) ? "; filename agrees with the sheet pattern" : "; filename does NOT match the sheet pattern";
                ctx.Sound(row.Id, ogg, $"{c.Route}; {string.Join("; ", notes)}{agree}");
                return RowOutcome.Ok;
            }
            catch (Exception e)
            {
                failures.Add($"{c.Bank.Name}#{c.Record.Index} ({c.Record.FileName}): {e.GetType().Name}: {e.Message}");
            }
        }
        return RowOutcome.Fail(string.Join("; ", failures.Take(4)));
    }
}

/// <summary>Maps of one install, opened lazily in search order and kept open for the run.</summary>
internal sealed class MapSet : IDisposable
{
    private readonly HaloInstall install;
    private readonly Func<string, IHaloMap> open;
    private readonly Log log;
    private readonly IReadOnlyList<string> order;
    private readonly List<(string Path, IHaloMap? Map, string? Error)> opened = new();

    public MapSet(HaloInstall install, Func<string, IHaloMap> open, Log log)
    {
        this.install = install; this.open = open; this.log = log;
        order = install.MapSearchOrder();
        log.Info($"[halo] map search order: {string.Join(", ", order.Select(Path.GetFileNameWithoutExtension))}");
        if (install.MapFile("shared") is null) log.Warn("[halo] shared.map is missing: geometry and bitmaps stored there will fail");
    }

    public int TriedCount => opened.Count;
    /// <summary>True when maps were tried and none could be opened (present but unreadable: Failed, not Missing).</summary>
    public bool NoneReadable => opened.Count > 0 && opened.All(o => o.Map is null);
    public IEnumerable<string> OpenedNames => opened.Where(o => o.Map != null).Select(o => Path.GetFileName(o.Path));
    public string TriedSummary => opened.Count == 0 ? "none"
        : string.Join(", ", opened.Select(o => Path.GetFileNameWithoutExtension(o.Path) + (o.Error != null ? $" (unreadable: {o.Error})" : "")));

    /// <summary>Every readable map, opening the next one only when the caller asks for it.</summary>
    public IEnumerable<IHaloMap> Maps()
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (i >= opened.Count)
            {
                var path = order[i];
                try
                {
                    var m = open(path);
                    opened.Add((path, m, null));
                    log.Info($"[halo] opened {m.Description}");
                }
                catch (Exception e)
                {
                    opened.Add((path, null, $"{e.GetType().Name}: {e.Message}"));
                    log.Warn($"[halo] cannot open {path}: {e.GetType().Name}: {e.Message}");
                }
            }
            if (opened[i].Map is { } map) yield return map;
        }
    }

    private readonly Dictionary<string, (List<(IHaloMap Map, HaloTag Tag, string Route)> Hits, HashSet<IHaloMap> Searched)> resolved = new();

    /// <summary>
    /// Where a weapon's tag is, map by map. Lazy (stops opening maps once the caller stops iterating) and memoised, so
    /// asking again replays what was found before and only searches maps not searched yet. At most 3 hits.
    /// </summary>
    public IEnumerable<(IHaloMap Map, HaloTag Tag, string Route)> Resolve(WeaponPlan plan)
    {
        if (!resolved.TryGetValue(plan.WeaponId, out var memo))
            resolved[plan.WeaponId] = memo = (new(), new());
        for (int i = 0; i < memo.Hits.Count; i++) yield return memo.Hits[i];
        if (memo.Hits.Count >= 3) yield break; // enough fallbacks for geometry failures
        foreach (var map in Maps())
        {
            if (!memo.Searched.Add(map)) continue;
            (HaloTag Tag, string Route)? r;
            try { r = new TagWalker(map).ResolveObject(plan.TagClass, plan.TagPath, plan.WeaponId, plan.Hint); }
            catch (Exception e) { log.Warn($"[halo] {plan.WeaponId}: lookup in {map.Name()} failed: {e.Message}"); continue; }
            if (r is null) { log.Info($"[halo] {plan.WeaponId}: {plan.TagClass} '{plan.TagPath}' not in {map.Name()}"); continue; }
            log.Info($"[halo] {plan.WeaponId}: {map.Name()} {r.Value.Tag} (#{r.Value.Tag.Index}) via {r.Value.Route}");
            var hit = (map, r.Value.Tag, r.Value.Route);
            memo.Hits.Add(hit);
            yield return hit;
            if (memo.Hits.Count >= 3) yield break;
        }
    }

    public void Dispose()
    {
        foreach (var (_, m, _) in opened) try { m?.Dispose(); } catch { }
    }
}

internal static class HaloMapExtensions
{
    public static string Name(this IHaloMap map) => Path.GetFileName(map.Path);
}
