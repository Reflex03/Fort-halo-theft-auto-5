using System.Text.RegularExpressions;
using CthExtract.Core.Generated;

namespace CthExtract.Halo;

/// <summary>What to extract for one weapon of the weapons sheet, read from the sources sheet rows.</summary>
public sealed record WeaponPlan(
    string WeaponId,
    string Kind,                // weapons sheet "kind": hitscan | projectile | rocket | grenade
    HeldClass Held,
    string TagClass,            // weap | eqip
    string TagPath,             // objects\weapons\rifle\assault_rifle\assault_rifle
    string Hint,                // fallback name fragment ('assault')
    SourceRow? MeshRow,
    SourceRow? FireSoundRow,
    string? FireSoundPattern);  // sfx.fsb.info filename pattern

/// <summary>A sound row that is found by an effect chain (or not) plus a filename pattern.</summary>
public sealed record SoundPlan(SourceRow Row, SoundPurpose Purpose, string? Pattern);

/// <summary>
/// Builds the work list from the sources sheet (Sources.ForGame("halo-mcc")). The sheet is the source of truth: tag
/// paths, name hints and filename patterns are parsed out of the locator / fallback text. Built-in values are only
/// used when a row's text cannot be parsed (logged).
/// </summary>
public static class HaloPlan
{
    /// <summary>Mirror of sheets/weapons.json "kind" (not generated into C#; a unit test checks it against the sheet).</summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["assault_rifle"] = "hitscan", ["magnum"] = "hitscan", ["battle_rifle"] = "hitscan", ["shotgun"] = "hitscan",
        ["sniper_rifle"] = "hitscan", ["plasma_rifle"] = "projectile", ["needler"] = "projectile",
        ["rocket_launcher"] = "rocket", ["frag_grenade"] = "grenade", ["plasma_grenade"] = "grenade",
    };

    private static readonly Regex TagRx = new(@"\b(weap|eqip)\s+'([^']+)'", RegexOptions.CultureInvariant);
    private static readonly Regex HintRx = new(@"containing\s+'([^']+)'", RegexOptions.CultureInvariant);
    private static readonly Regex PatternRx = new(@"filename matching\s+(\S+)", RegexOptions.CultureInvariant);

    public static HeldClass HeldFor(string weaponId, string kind) => kind == "grenade" ? HeldClass.Grenade
        : weaponId is "magnum" or "needler" or "plasma_pistol" ? HeldClass.Pistol : HeldClass.Rifle;

    public static string? Pattern(string text)
    {
        var m = PatternRx.Match(text);
        return m.Success ? m.Groups[1].Value.TrimEnd('.', ',', ';') : null;
    }

    public static List<WeaponPlan> Weapons(IReadOnlyList<SourceRow> rows, IEnumerable<string> weaponIds, Action<string> log)
    {
        var plans = new List<WeaponPlan>();
        foreach (var id in weaponIds)
        {
            var mesh = rows.FirstOrDefault(r => r.Id == $"halo_mesh_{id}");
            var fire = rows.FirstOrDefault(r => r.Id == $"halo_snd_{id}_fire");
            string kind = Kinds.TryGetValue(id, out var k) ? k : "hitscan";
            string tagClass = kind == "grenade" ? "eqip" : "weap";
            string path = DefaultPath(id), hint = id.Split('_')[0];
            var src = mesh?.Locator ?? fire?.Locator;
            if (src != null)
            {
                var m = TagRx.Match(src);
                if (m.Success) { tagClass = m.Groups[1].Value; path = m.Groups[2].Value; }
                else log($"[halo] cannot parse a tag path from '{src}', using {tagClass} '{path}'");
            }
            if (mesh != null)
            {
                var h = HintRx.Match(mesh.Fallback);
                if (h.Success) hint = h.Groups[1].Value;
            }
            string? pattern = fire is null ? null : Pattern(fire.Fallback) ?? Pattern(fire.Locator);
            if (fire != null && pattern is null) log($"[halo] no filename pattern in {fire.Id} fallback '{fire.Fallback}'");
            plans.Add(new WeaponPlan(id, kind, HeldFor(id, kind), tagClass, path, hint, mesh, fire, pattern));
        }
        return plans;
    }

    public static List<SoundPlan> OtherSounds(IReadOnlyList<SourceRow> rows)
    {
        var list = new List<SoundPlan>();
        void Add(string id, SoundPurpose purpose)
        {
            var row = rows.FirstOrDefault(r => r.Id == id);
            if (row != null) list.Add(new SoundPlan(row, purpose, Pattern(row.Fallback) ?? Pattern(row.Locator)));
        }
        Add("halo_snd_grenade_throw", SoundPurpose.Throw);
        Add("halo_snd_shield_recharge", SoundPurpose.ShieldRecharge);
        Add("halo_snd_shield_depleted", SoundPurpose.ShieldDepleted);
        return list;
    }

    public static string DefaultPath(string id) => id switch
    {
        "magnum" => @"objects\weapons\pistol\magnum\magnum",
        "needler" => @"objects\weapons\pistol\needler\needler",
        "rocket_launcher" => @"objects\weapons\support_high\rocket_launcher\rocket_launcher",
        "frag_grenade" => @"objects\weapons\grenade\frag_grenade\frag_grenade",
        "plasma_grenade" => @"objects\weapons\grenade\plasma_grenade\plasma_grenade",
        _ => $@"objects\weapons\rifle\{id}\{id}",
    };
}
