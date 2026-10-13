using static CthExtract.Halo.HaloLayout;

namespace CthExtract.Halo;

/// <summary>
/// Follows tag references through raw fields at the Assembly Halo3MCC offsets (see <see cref="HaloLayout"/>).
/// Every step validates the class of what it reached, so a wrong offset yields "not found" rather than garbage.
/// </summary>
public sealed class TagWalker
{
    public IHaloMap Map { get; }
    public TagFields F { get; }
    private readonly Action<string> log;

    public TagWalker(IHaloMap map, Action<string>? log = null)
    {
        Map = map; F = new TagFields(map); this.log = log ?? (_ => { });
    }

    // ---------------------------------------------------------------- lookups

    public HaloTag? Find(string tagClass, string path) =>
        Map.Tags.FirstOrDefault(t => t.Class == tagClass && string.Equals(t.Name, path, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<HaloTag> OfClass(string tagClass) => Map.Tags.Where(t => t.Class == tagClass && t.Name != null);

    /// <summary>mulg Universal[0] Weapon Selections: (selection name, weap).</summary>
    public List<(string Name, HaloTag Weapon)> MultiplayerWeapons()
    {
        var list = new List<(string, HaloTag)>();
        var mulg = Mulg();
        if (mulg is null) return list;
        var universal = F.Block(F.Address(mulg) + HaloLayout.Mulg.Universal);
        if (universal.IsEmpty) return list;
        var sel = F.Block(universal.Address + HaloLayout.Mulg.WeaponSelections);
        for (int i = 0; i < sel.Count && i < 256; i++)
        {
            long e = sel.Element(i, HaloLayout.Mulg.WeaponSelectionSize);
            var w = F.TagRef(e + HaloLayout.Mulg.WeaponSelectionWeapon, "weap");
            if (w != null) list.Add((F.StringId(e + HaloLayout.Mulg.WeaponSelectionName) ?? "", w));
        }
        return list;
    }

    public HaloTag? Mulg()
    {
        var matg = Map.Global("matg");
        return matg is null ? null : F.TagRef(F.Address(matg) + HaloLayout.Matg.MultiplayerGlobals, "mulg");
    }

    /// <summary>matg Grenades: (throwing effect, equipment, projectile) per grenade type (Halo 3: frag, plasma, spike, firebomb).</summary>
    public List<(HaloTag? Throw, HaloTag? Equipment, HaloTag? Projectile)> Grenades()
    {
        var list = new List<(HaloTag?, HaloTag?, HaloTag?)>();
        var matg = Map.Global("matg");
        if (matg is null) return list;
        var b = F.Block(F.Address(matg) + HaloLayout.Matg.Grenades);
        for (int i = 0; i < b.Count && i < 16; i++)
        {
            long e = b.Element(i, HaloLayout.Matg.GrenadeSize);
            list.Add((F.TagRef(e + HaloLayout.Matg.Grenade.ThrowingEffect, "effe"),
                      F.TagRef(e + HaloLayout.Matg.Grenade.Equipment, "eqip"),
                      F.TagRef(e + HaloLayout.Matg.Grenade.Projectile, "proj")));
        }
        return list;
    }

    /// <summary>
    /// The weapon/equipment tag for a sheet row: exact path; then mulg weapon selections (name = weapon id, or name
    /// containing the hint) / matg grenades (equipment name containing the hint); then any tag of the class with the same
    /// leaf name; then any tag of the class whose name contains the hint. Returns the route taken.
    /// </summary>
    public (HaloTag Tag, string Route)? ResolveObject(string tagClass, string path, string weaponId, string hint)
    {
        var exact = Find(tagClass, path);
        if (exact != null) return (exact, "exact tag path");

        var tokens = weaponId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tagClass == "weap")
        {
            var sel = Safe(MultiplayerWeapons, "mulg weapon selections");
            var byId = sel.FirstOrDefault(s => string.Equals(s.Name, weaponId, StringComparison.OrdinalIgnoreCase));
            if (byId.Weapon != null) return (byId.Weapon, $"mulg weapon selection '{byId.Name}'");
            var byHint = sel.Where(s => s.Name.Contains(hint, StringComparison.OrdinalIgnoreCase) || s.Weapon.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(s => TokenScore(s.Name + " " + s.Weapon.Leaf, tokens)).ThenBy(s => s.Weapon.Name.Length).FirstOrDefault();
            if (byHint.Weapon != null) return (byHint.Weapon, $"mulg weapon selection '{byHint.Name}' (contains '{hint}')");
        }
        else if (tagClass == "eqip")
        {
            var g = Safe(Grenades, "matg grenades").Where(x => x.Equipment != null && x.Equipment.Name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                              .OrderByDescending(x => TokenScore(x.Equipment!.Leaf, tokens)).FirstOrDefault();
            if (g.Equipment != null) return (g.Equipment, $"matg grenades (equipment contains '{hint}')");
        }

        var leaf = path.Substring(path.LastIndexOf('\\') + 1);
        var byLeaf = OfClass(tagClass).Where(t => string.Equals(t.Leaf, leaf, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Name.Length).FirstOrDefault();
        if (byLeaf != null) return (byLeaf, $"same leaf name '{leaf}'");

        var contains = OfClass(tagClass).Where(t => t.Leaf.Contains(hint, StringComparison.OrdinalIgnoreCase)
                                                 && !t.Name.Contains("\\fp\\", StringComparison.OrdinalIgnoreCase))
                                        .OrderByDescending(t => TokenScore(t.Leaf, tokens)).ThenBy(t => t.Name.Length).FirstOrDefault();
        if (contains != null) return (contains, $"name containing '{hint}'");
        return null;
    }

    /// <summary>Run a raw walk; a bad offset or pointer (InvalidDataException from the reader) gives an empty list, logged.</summary>
    private List<T> Safe<T>(Func<List<T>> walk, string what)
    {
        try { return walk(); }
        catch (Exception e) { log($"{what} unreadable in {Map.Path}: {e.GetType().Name}: {e.Message}"); return new List<T>(); }
    }

    /// <summary>How many words of the weapon id a name contains ("plasma_rifle" prefers plasma_rifle over plasma_pistol).</summary>
    private static int TokenScore(string name, string[] tokens) => tokens.Count(tok => name.Contains(tok, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- models

    public HaloTag? ModelOf(HaloTag obj) => F.TagRef(F.Address(obj) + HaloLayout.Object.Model, "hlmt");

    public HaloTag? RenderModelOf(HaloTag hlmt) => F.TagRef(F.Address(hlmt) + HaloLayout.Hlmt.RenderModel, "mode");

    public string? DefaultVariantName(HaloTag obj) => F.StringId(F.Address(obj) + HaloLayout.Object.DefaultModelVariant);

    public string? HandleNode(HaloTag weap) =>
        weap.Class == "weap" ? F.StringId(F.Address(weap) + HaloLayout.Weap.HandleNode) : null;

    /// <summary>Region → permutation of the hlmt variant with this name (or the first variant). Empty if none.</summary>
    public Dictionary<string, string> VariantPermutations(HaloTag hlmt, string? variantName)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variants = F.Block(F.Address(hlmt) + HaloLayout.Hlmt.Variants);
        if (variants.IsEmpty || variants.Count > 256) return result;
        long chosen = -1;
        for (int i = 0; i < variants.Count; i++)
        {
            long v = variants.Element(i, HaloLayout.Hlmt.VariantSize);
            if (variantName != null && string.Equals(F.StringId(v + HaloLayout.Hlmt.Variant.Name), variantName, StringComparison.OrdinalIgnoreCase)) { chosen = v; break; }
        }
        if (chosen < 0) chosen = variants.Element(0, HaloLayout.Hlmt.VariantSize);
        var regions = F.Block(chosen + HaloLayout.Hlmt.Variant.Regions);
        for (int r = 0; r < regions.Count && r < 64; r++)
        {
            long re = regions.Element(r, HaloLayout.Hlmt.Variant.RegionSize);
            var regionName = F.StringId(re + HaloLayout.Hlmt.Variant.RegionName);
            var perms = F.Block(re + HaloLayout.Hlmt.Variant.RegionPermutations);
            if (regionName is null || perms.IsEmpty) continue;
            var permName = F.StringId(perms.Element(0, HaloLayout.Hlmt.Variant.PermutationSize) + HaloLayout.Hlmt.Variant.PermutationName);
            if (permName != null) result[regionName] = permName;
        }
        return result;
    }

    // ---------------------------------------------------------------- stats

    public RawWeaponStats ReadWeapon(HaloTag weap, string kind)
    {
        long w = F.Address(weap);
        var r = new RawWeaponStats { WeaponTag = weap.Name };
        var mags = F.Block(w + HaloLayout.Weap.Magazines);
        if (!mags.IsEmpty)
        {
            long m = mags.Element(0, HaloLayout.Weap.MagazineSize);
            r.ClipSize = F.I16(m + HaloLayout.Weap.Magazine.RoundsLoadedMaximum);
            r.ReloadSeconds = F.F32(m + HaloLayout.Weap.Magazine.ReloadTime);
        }
        var trig = F.Block(w + HaloLayout.Weap.NewTriggers);
        if (!trig.IsEmpty) r.TriggerBehavior = F.I16(trig.Element(0, HaloLayout.Weap.TriggerSize) + HaloLayout.Weap.Trigger.Behavior);
        r.MagnificationLevels = F.I16(w + HaloLayout.Weap.MagnificationLevels);
        (r.MagnificationMin, r.MagnificationMax) = F.Range(w + HaloLayout.Weap.MagnificationRange);

        var barrels = F.Block(w + HaloLayout.Weap.Barrels);
        HaloTag? proj = null, barrelDamage = null;
        if (!barrels.IsEmpty)
        {
            long b = barrels.Element(0, HaloLayout.Weap.BarrelSize);
            r.HasBarrel = true;
            (r.RoundsPerSecondMin, r.RoundsPerSecondMax) = F.Range(b + HaloLayout.Weap.Barrel.RoundsPerSecond);
            (r.ShotsPerFireMin, r.ShotsPerFireMax) = F.Range16(b + HaloLayout.Weap.Barrel.ShotsPerFire);
            r.FireRecoverySeconds = F.F32(b + HaloLayout.Weap.Barrel.FireRecoveryTime);
            r.ProjectilesPerShot = F.I16(b + HaloLayout.Weap.Barrel.ProjectilesPerShot);
            r.DistributionAngleRad = F.F32(b + HaloLayout.Weap.Barrel.DistributionAngle);
            r.MinimumErrorRad = F.F32(b + HaloLayout.Weap.Barrel.MinimumError);
            (r.ErrorAngleMinRad, r.ErrorAngleMaxRad) = F.Range(b + HaloLayout.Weap.Barrel.ErrorAngle);
            proj = F.TagRef(b + HaloLayout.Weap.Barrel.Projectile, "proj");
            barrelDamage = F.TagRef(b + HaloLayout.Weap.Barrel.DamageEffect, "jpt!");
        }
        ReadProjectile(proj, kind, r, barrelDamage);
        return r;
    }

    /// <summary>Projectile + damage effect fields into <paramref name="r"/> (grenades: proj only).</summary>
    public void ReadProjectile(HaloTag? proj, string kind, RawWeaponStats r, HaloTag? barrelDamage = null)
    {
        bool explosive = kind is "rocket" or "grenade";
        HaloTag? jpt = null;
        if (proj != null)
        {
            long p = F.Address(proj);
            r.ProjectileTag = proj.Name;
            r.InitialVelocity = F.F32(p + HaloLayout.Proj.InitialVelocity);
            r.GuidedAngularVelocity = F.F32(p + HaloLayout.Proj.GuidedAngularVelocityUpper);
            var impact = F.TagRef(p + HaloLayout.Proj.ImpactDamage, "jpt!");
            var detonation = F.TagRef(p + HaloLayout.Proj.DetonationDamage, "jpt!");
            (jpt, r.DamageSource) = explosive
                ? (detonation ?? impact, detonation != null ? "proj detonation" : "proj impact")
                : (impact ?? barrelDamage ?? detonation, impact != null ? "proj impact" : barrelDamage != null ? "barrel damage effect" : "proj detonation");
        }
        else if (barrelDamage != null)
        {
            (jpt, r.DamageSource) = (barrelDamage, "barrel damage effect");
        }
        if (jpt != null)
        {
            long j = F.Address(jpt);
            r.DamageTag = jpt.Name;
            r.DamageLowerBound = F.F32(j + HaloLayout.Jpt.DamageLowerBound);
            (var lo, var hi) = F.Range(j + HaloLayout.Jpt.DamageUpperBound);
            r.DamageUpperMin = lo; r.DamageUpperMax = hi;
            r.DamageRadiusMax = F.Range(j + HaloLayout.Jpt.Radius).Max;
        }
    }

    /// <summary>The multiplayer Spartan biped: matg Player Representation (class multiplayer, Spartan), else by name.</summary>
    public (HaloTag Biped, string Route)? SpartanBiped()
    {
        var matg = Map.Global("matg");
        if (matg != null)
        {
            try
            {
            var reps = F.Block(F.Address(matg) + HaloLayout.Matg.PlayerRepresentation);
            HaloTag? any = null;
            for (int i = 0; i < reps.Count && i < 32; i++)
            {
                long e = reps.Element(i, HaloLayout.Matg.PlayerRepresentationSize);
                var unit = F.TagRef(e + HaloLayout.Matg.Representation.ThirdPersonUnit, "bipd");
                if (unit is null) continue;
                any ??= unit;
                if (F.U8(e + HaloLayout.Matg.Representation.Class) == 1 && F.U8(e + HaloLayout.Matg.Representation.ModelChoice) == 0)
                    return (unit, $"matg player representation #{i} (multiplayer spartan)");
            }
            if (any != null) return (any, "matg player representation (first with a biped)");
            }
            catch (Exception e) { log($"matg player representation unreadable: {e.GetType().Name}: {e.Message}"); }
        }
        var named = Find("bipd", @"objects\characters\masterchief\mp_masterchief\mp_masterchief")
                    ?? OfClass("bipd").Where(t => t.Name.Contains("masterchief", StringComparison.OrdinalIgnoreCase))
                                      .OrderBy(t => t.Name.Contains("mp_", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Name.Length).FirstOrDefault();
        return named is null ? null : (named, "biped by name");
    }

    /// <summary>hlmt New Damage Info[0] address, or -1.</summary>
    public long DamageInfo(HaloTag hlmt)
    {
        var b = F.Block(F.Address(hlmt) + HaloLayout.Hlmt.NewDamageInfo);
        return b.IsEmpty ? -1 : b.Element(0, HaloLayout.Hlmt.DamageInfoSize);
    }

    public RawShieldStats ReadShield(long damageInfo, string source) => new()
    {
        Source = source,
        BodyVitality = F.F32(damageInfo + HaloLayout.Hlmt.DamageInfo.MaximumVitality),
        ShieldVitality = F.F32(damageInfo + HaloLayout.Hlmt.DamageInfo.MaximumShieldVitality),
        StunSeconds = F.F32(damageInfo + HaloLayout.Hlmt.DamageInfo.ShieldStunTime),
        RechargeSeconds = F.F32(damageInfo + HaloLayout.Hlmt.DamageInfo.ShieldRechargeTime),
    };

    // ---------------------------------------------------------------- sounds

    /// <summary>Firing effects of every barrel (barrel 0 first).</summary>
    public List<HaloTag> FiringEffects(HaloTag weap)
    {
        var list = new List<HaloTag>();
        var barrels = F.Block(F.Address(weap) + HaloLayout.Weap.Barrels);
        for (int i = 0; i < barrels.Count && i < 8; i++)
        {
            long b = barrels.Element(i, HaloLayout.Weap.BarrelSize);
            var fx = F.Block(b + HaloLayout.Weap.Barrel.FiringEffects);
            for (int k = 0; k < fx.Count && k < 16; k++)
            {
                var effe = F.TagRef(fx.Element(k, HaloLayout.Weap.Barrel.FiringEffectSize) + HaloLayout.Weap.Barrel.FiringEffect, "effe");
                if (effe != null && !list.Contains(effe)) list.Add(effe);
            }
        }
        return list;
    }

    /// <summary>
    /// snd! tags an effect plays: Events → Parts → Type (Halo3 effe layout). If that finds none (the MCC layout of
    /// effe is unverified) every 4-byte-aligned tag reference inside each part is checked for a snd! target.
    /// </summary>
    public List<HaloTag> SoundsOfEffect(HaloTag effe, out string route)
    {
        var found = new List<HaloTag>();
        var events = F.Block(F.Address(effe) + HaloLayout.Effe.Events);
        route = "effe events/parts";
        if (events.IsEmpty || events.Count > 64) { route = "effe has no readable events"; return found; }
        for (int e = 0; e < events.Count; e++)
        {
            var parts = F.Block(events.Element(e, HaloLayout.Effe.EventSize) + HaloLayout.Effe.EventParts);
            if (parts.Count > 256) continue;
            for (int p = 0; p < parts.Count; p++)
            {
                var snd = F.TagRef(parts.Element(p, HaloLayout.Effe.PartSize) + HaloLayout.Effe.PartType, "snd!");
                if (snd != null && !found.Contains(snd)) found.Add(snd);
            }
        }
        if (found.Count > 0) return found;

        route = "effe part scan";
        for (int e = 0; e < events.Count; e++)
        {
            var parts = F.Block(events.Element(e, HaloLayout.Effe.EventSize) + HaloLayout.Effe.EventParts);
            if (parts.Count > 256) continue;
            for (int p = 0; p < parts.Count; p++)
            {
                long pe = parts.Element(p, HaloLayout.Effe.PartSize);
                for (int o = 0; o + HaloLayout.TagRefSize <= HaloLayout.Effe.PartSize; o += 4)
                {
                    var snd = F.TagRef(pe + o, "snd!");
                    if (snd != null && !found.Contains(snd)) found.Add(snd);
                }
            }
        }
        return found;
    }

    /// <summary>
    /// FMOD subsound hashes of a snd! tag, in order: snd! pitch ranges → ugh! Pitch Ranges (encoded first permutation
    /// and count, MCC packing: first = (v &lt;&lt; 12) &gt;&gt; 12, count = (v &gt;&gt; 20) &amp; 63, per TagTool
    /// SoundCacheFileGestalt) → ugh! Permutations "FMod Bank Subsound ID Hash".
    /// </summary>
    public List<uint> SoundHashes(HaloTag snd, out string detail)
    {
        var hashes = new List<uint>();
        var ugh = Map.Global("ugh!");
        if (ugh is null) { detail = "no ugh! sound gestalt in this map"; return hashes; }
        long s = F.Address(snd);
        int prCount = F.I8(s + HaloLayout.Snd.PitchRangeCount);
        int firstPr = F.I16(s + HaloLayout.Snd.FirstPitchRangeIndex);
        long u = F.Address(ugh);
        var prs = F.Block(u + HaloLayout.Ugh.PitchRanges);
        var perms = F.Block(u + HaloLayout.Ugh.Permutations);
        if (prCount <= 0 || firstPr < 0 || firstPr + prCount > prs.Count)
        {
            detail = $"pitch ranges {firstPr}+{prCount} outside ugh! ({prs.Count})";
            return hashes;
        }
        for (int i = 0; i < prCount; i++)
        {
            int v = F.I32(prs.Element(firstPr + i, HaloLayout.Ugh.PitchRangeSize) + HaloLayout.Ugh.PitchRangeEncodedFirstAndCount);
            var (first, count) = DecodePitchRange(v);
            for (int k = 0; k < count; k++)
            {
                int pi = first + k;
                if (pi < 0 || pi >= perms.Count) continue;
                var h = F.U32(perms.Element(pi, HaloLayout.Ugh.PermutationSize) + HaloLayout.Ugh.PermutationFmodHash);
                if (h != 0 && !hashes.Contains(h)) hashes.Add(h);
            }
        }
        detail = $"{prCount} pitch range(s) from #{firstPr}, {hashes.Count} permutation hash(es)";
        return hashes;
    }

    /// <summary>MCC ugh! pitch range "Encoded First Permutation And Counts".</summary>
    public static (int First, int Count) DecodePitchRange(int v) => ((v << 12) >> 12, (v >> 20) & 63);

    public static int EncodePitchRange(int first, int count) => (first & 0xFFFFF) | ((count & 63) << 20);
}
