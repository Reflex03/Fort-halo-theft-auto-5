using System.Numerics;
using Reclaimer.Geometry;
using L = CthExtract.Halo.HaloLayout;

namespace CthExtract.Halo.Tests;

/// <summary>Weapon values written into the synthetic tags (roughly Halo 3's, so the self-check has something to compare).</summary>
public sealed record SynthWeapon(
    string Id, string Path, short Clip, float Reload, short Behavior, float RpsMin, float RpsMax, short Shots, float Recovery,
    short Pellets, float DistributionRad, float MinErrorRad, float ErrorMinRad, float ErrorMaxRad, short MagLevels, float MagMin,
    float Velocity, float Guided, float DmgLower, float DmgUpper, float RadiusMax, string SoundName, uint[] Hashes);

/// <summary>
/// Populates a <see cref="FakeMap"/> with the tag graph the module walks on a real riverworld.map:
/// matg → mulg (weapon selections) / grenades / player representation → bipd → hlmt (damage info, shield effects);
/// weap → hlmt → mode (+ scene), barrels → proj → jpt!, magazines, triggers, firing effect → effe → snd!;
/// global ugh! pitch ranges/permutations carrying FMOD subsound hashes.
/// </summary>
public sealed class SyntheticHalo
{
    public FakeMap Map { get; }
    public Dictionary<string, HaloTag> Weapons { get; } = new();
    public Dictionary<string, HaloTag> Grenades { get; } = new();
    public Dictionary<string, HaloTag> Modes { get; } = new();
    public HaloTag Matg { get; }
    public HaloTag Ugh { get; }
    public HaloTag Biped { get; private set; } = null!;
    public uint ShieldRechargeHash = 0x5AA10001, ShieldDepletedHash = 0x5AA10002, ThrowHash = 0x7A110001;

    public static readonly SynthWeapon[] DefaultWeapons =
    {
        new("assault_rifle", @"objects\weapons\rifle\assault_rifle\assault_rifle", 32, 2.2f, 0, 9, 10, 1, 0, 1, 0, 0.002f, 0.035f, 0.1f, 0, 1, 400, 0, 6.5f, 7.5f, 0, @"sound\weapons\assault_rifle\ar_fire", new uint[] { 0xA0000001, 0xA0000002 }),
        new("magnum", @"objects\weapons\pistol\magnum\magnum", 8, 1.5f, 1, 4, 4, 1, 0.25f, 1, 0, 0.01f, 0.01f, 0.03f, 0, 1, 500, 0, 11, 12, 0, @"sound\weapons\magnum\magnum_fire", new uint[] { 0xA0000011 }),
        new("battle_rifle", @"objects\weapons\rifle\battle_rifle\battle_rifle", 36, 2.1f, 1, 15, 15, 3, 0.28f, 1, 0, 0.005f, 0.007f, 0.02f, 1, 2, 500, 0, 5.5f, 6, 0, @"sound\weapons\battle_rifle\br_fire", new uint[] { 0xA0000021 }),
        new("shotgun", @"objects\weapons\rifle\shotgun\shotgun", 6, 3.0f, 1, 1.2f, 1.2f, 1, 0.8f, 15, 0.24f, 0, 0, 0, 0, 1, 300, 0, 9, 10, 0, @"sound\weapons\shotgun\shotgun_fire", new uint[] { 0xA0000031 }),
        new("sniper_rifle", @"objects\weapons\rifle\sniper_rifle\sniper_rifle", 4, 2.8f, 4, 1.4f, 1.4f, 1, 0.7f, 1, 0, 0, 0, 0, 2, 5, 900, 0, 78, 80, 0, @"sound\weapons\sniper_rifle\sniper_fire", new uint[] { 0xA0000041 }),
        new("plasma_rifle", @"objects\weapons\rifle\plasma_rifle\plasma_rifle", 0, 0, 0, 6, 9, 1, 0, 1, 0, 0.01f, 0.02f, 0.05f, 0, 1, 20, 0, 7, 8, 0, @"sound\weapons\plasma_rifle\plasma_fire", new uint[] { 0xA0000051 }),
        new("needler", @"objects\weapons\pistol\needler\needler", 19, 2.5f, 0, 8, 8, 1, 0, 1, 0, 0.02f, 0.04f, 0.08f, 0, 1, 12, 2.5f, 6, 7, 0, @"sound\weapons\needler\needler_fire", new uint[] { 0xA0000061 }),
        new("rocket_launcher", @"objects\weapons\support_high\rocket_launcher\rocket_launcher", 2, 3.5f, 5, 1, 1, 1, 0.9f, 1, 0, 0, 0, 0, 1, 2, 15, 0, 190, 200, 1.4f, @"sound\weapons\rocket_launcher\rocket_fire", new uint[] { 0xA0000071 }),
    };

    public SyntheticHalo(FakeMap? map = null, IEnumerable<SynthWeapon>? weapons = null, bool withMulg = true, bool withRepresentation = true)
    {
        Map = map ?? new FakeMap();
        var m = Map;
        Matg = m.AddTag("matg", @"globals\globals", 0x530, global: true);
        Ugh = m.AddTag("ugh!", @"i've got a lovely bunch of coconuts", 0xC8, global: true);
        var list = (weapons ?? DefaultWeapons).ToList();

        // ---- ugh!: one pitch range per snd!, permutations carry hashes ----
        var allSounds = list.Select(w => (w.SoundName, w.Hashes)).ToList();
        allSounds.Add((@"sound\characters\spartan\shield_recharge", new[] { ShieldRechargeHash }));
        allSounds.Add((@"sound\characters\spartan\shield_depleted", new[] { ShieldDepletedHash }));
        allSounds.Add((@"sound\weapons\grenade\frag_grenade\throw", new[] { ThrowHash }));
        int totalPerms = allSounds.Sum(s => s.Hashes.Length);
        long prs = m.Block(m.At(Ugh) + L.Ugh.PitchRanges, allSounds.Count + 1, L.Ugh.PitchRangeSize);
        long perms = m.Block(m.At(Ugh) + L.Ugh.Permutations, totalPerms + 3, L.Ugh.PermutationSize);
        var sndTags = new Dictionary<string, HaloTag>();
        int permCursor = 3; // a few unrelated permutations first, so indices are not trivially 0
        for (int i = 0; i < allSounds.Count; i++)
        {
            var (name, hashes) = allSounds[i];
            var snd = m.AddTag("snd!", name, 0x24);
            m.U8(m.At(snd) + L.Snd.SoundClass, 4);
            m.U8(m.At(snd) + L.Snd.PitchRangeCount, 1);
            m.I16(m.At(snd) + L.Snd.FirstPitchRangeIndex, (short)(i + 1));
            m.I32(prs + (i + 1) * L.Ugh.PitchRangeSize + L.Ugh.PitchRangeEncodedFirstAndCount, TagWalker.EncodePitchRange(permCursor, hashes.Length));
            foreach (var h in hashes) m.U32(perms + permCursor++ * L.Ugh.PermutationSize + L.Ugh.PermutationFmodHash, h);
            sndTags[name] = snd;
        }

        HaloTag Effect(string name, HaloTag snd)
        {
            var effe = m.AddTag("effe", name, 0x68);
            long ev = m.Block(m.At(effe) + L.Effe.Events, 1, L.Effe.EventSize);
            long parts = m.Block(ev + L.Effe.EventParts, 2, L.Effe.PartSize);
            m.TagRef(parts + L.Effe.PartType, null);                    // a non-sound part (e.g. a light)
            m.TagRef(parts + L.Effe.PartSize + L.Effe.PartType, snd);
            return effe;
        }

        HaloTag ModelFor(string id, string path, float x0, float x1, float hw, float hh, bool markers)
        {
            var mode = m.AddTag("mode", path, 0x100);
            var hlmt = m.AddTag("hlmt", path, 0x188);
            m.TagRef(m.At(hlmt) + L.Hlmt.RenderModel, mode);
            var (p, n, uv, idx) = SceneFactory.Box(x0, x1, hw, hh);
            var mat = SceneFactory.MakeMaterial(10 + Modes.Count, id + "_mat", AlphaMode.Opaque, SceneFactory.Bgra.Solid(4, 4, 30, 60, 90, 0));
            var model = SceneFactory.Model(id, SceneFactory.MakeMesh(p, n, uv, idx, mat));
            if (markers)
            {
                SceneFactory.AddMarker(model, "right_hand", new Vector3(x0 + (x1 - x0) * 0.25f, 0, -hh));
                SceneFactory.AddMarker(model, "primary_trigger", new Vector3(x1, 0, 0));
            }
            m.SetScene(mode, SceneFactory.Wrap(model));
            Modes[id] = mode;
            return hlmt;
        }

        // ---- weapons ----
        foreach (var w in list)
        {
            var weap = m.AddTag("weap", w.Path, 0x4DC);
            Weapons[w.Id] = weap;
            long a = m.At(weap);
            m.TagRef(a + L.Object.Model, ModelFor(w.Id, w.Path, -0.05f, 0.25f, 0.02f, 0.04f, markers: true));
            m.I16(a + L.Weap.MagnificationLevels, w.MagLevels);
            m.Range(a + L.Weap.MagnificationRange, w.MagMin, w.MagMin * 2);
            if (w.Clip > 0)
            {
                long mag = m.Block(a + L.Weap.Magazines, 1, L.Weap.MagazineSize);
                m.I16(mag + L.Weap.Magazine.RoundsLoadedMaximum, w.Clip);
                m.F32(mag + L.Weap.Magazine.ReloadTime, w.Reload);
            }
            long trig = m.Block(a + L.Weap.NewTriggers, 1, L.Weap.TriggerSize);
            m.I16(trig + L.Weap.Trigger.Behavior, w.Behavior);
            long b = m.Block(a + L.Weap.Barrels, 1, L.Weap.BarrelSize);
            m.Range(b + L.Weap.Barrel.RoundsPerSecond, w.RpsMin, w.RpsMax);
            m.I16(b + L.Weap.Barrel.ShotsPerFire, w.Shots); m.I16(b + L.Weap.Barrel.ShotsPerFire + 2, w.Shots);
            m.F32(b + L.Weap.Barrel.FireRecoveryTime, w.Recovery);
            m.I16(b + L.Weap.Barrel.ProjectilesPerShot, w.Pellets);
            m.F32(b + L.Weap.Barrel.DistributionAngle, w.DistributionRad);
            m.F32(b + L.Weap.Barrel.MinimumError, w.MinErrorRad);
            m.Range(b + L.Weap.Barrel.ErrorAngle, w.ErrorMinRad, w.ErrorMaxRad);
            var proj = m.AddTag("proj", w.Path + "_projectile", 0x2A0);
            m.TagRef(b + L.Weap.Barrel.Projectile, proj);
            m.F32(m.At(proj) + L.Proj.InitialVelocity, w.Velocity);
            m.F32(m.At(proj) + L.Proj.GuidedAngularVelocityUpper, w.Guided);
            var jpt = m.AddTag("jpt!", w.Path + "_damage", 0xF0);
            m.TagRef(m.At(proj) + (w.RadiusMax > 0 ? L.Proj.DetonationDamage : L.Proj.ImpactDamage), jpt);
            m.F32(m.At(jpt) + L.Jpt.DamageLowerBound, w.DmgLower);
            m.Range(m.At(jpt) + L.Jpt.DamageUpperBound, w.DmgUpper, w.DmgUpper);
            m.Range(m.At(jpt) + L.Jpt.Radius, 0, w.RadiusMax);
            long fx = m.Block(b + L.Weap.Barrel.FiringEffects, 1, L.Weap.Barrel.FiringEffectSize);
            m.TagRef(fx + L.Weap.Barrel.FiringEffect, Effect(w.Path + @"\fx\firing", sndTags[w.SoundName]));
        }

        // ---- grenades (matg Grenades block: frag, plasma) ----
        long gren = m.Block(m.At(Matg) + L.Matg.Grenades, 2, L.Matg.GrenadeSize);
        string[] gnames = { "frag_grenade", "plasma_grenade" };
        for (int i = 0; i < 2; i++)
        {
            var path = $@"objects\weapons\grenade\{gnames[i]}\{gnames[i]}";
            var eqip = m.AddTag("eqip", path, 0x26C);
            m.TagRef(m.At(eqip) + L.Object.Model, ModelFor(gnames[i], path, -0.02f, 0.02f, 0.02f, 0.02f, markers: false));
            var proj = m.AddTag("proj", path, 0x2A0);
            m.F32(m.At(proj) + L.Proj.InitialVelocity, 8);
            var jpt = m.AddTag("jpt!", path + "_explosion", 0xF0);
            m.TagRef(m.At(proj) + L.Proj.DetonationDamage, jpt);
            m.Range(m.At(jpt) + L.Jpt.DamageUpperBound, 120, 125);
            m.Range(m.At(jpt) + L.Jpt.Radius, 0, i == 0 ? 1.2f : 1.0f);
            long e = gren + i * L.Matg.GrenadeSize;
            m.TagRef(e + L.Matg.Grenade.Equipment, eqip);
            m.TagRef(e + L.Matg.Grenade.Projectile, proj);
            if (i == 0) m.TagRef(e + L.Matg.Grenade.ThrowingEffect, Effect(@"fx\grenade_throw", sndTags[@"sound\weapons\grenade\frag_grenade\throw"]));
            Grenades[gnames[i]] = eqip;
        }

        // ---- multiplayer globals ----
        if (withMulg)
        {
            var mulg = m.AddTag("mulg", @"multiplayer\multiplayer_globals", 0x18);
            m.TagRef(m.At(Matg) + L.Matg.MultiplayerGlobals, mulg);
            long uni = m.Block(m.At(mulg) + L.Mulg.Universal, 1, 0xD0);
            long sel = m.Block(uni + L.Mulg.WeaponSelections, list.Count, L.Mulg.WeaponSelectionSize);
            for (int i = 0; i < list.Count; i++)
            {
                m.StringId(sel + i * L.Mulg.WeaponSelectionSize + L.Mulg.WeaponSelectionName, list[i].Id);
                m.TagRef(sel + i * L.Mulg.WeaponSelectionSize + L.Mulg.WeaponSelectionWeapon, Weapons[list[i].Id]);
            }
        }

        // ---- spartan: matg player representation -> bipd -> hlmt damage info ----
        Biped = m.AddTag("bipd", @"objects\characters\masterchief\mp_masterchief\mp_masterchief", 0x490);
        var bhlmt = m.AddTag("hlmt", @"objects\characters\masterchief\mp_masterchief\mp_masterchief", 0x188);
        m.TagRef(m.At(Biped) + L.Object.Model, bhlmt);
        long di = m.Block(m.At(bhlmt) + L.Hlmt.NewDamageInfo, 1, L.Hlmt.DamageInfoSize);
        m.F32(di + L.Hlmt.DamageInfo.MaximumVitality, 45);
        m.F32(di + L.Hlmt.DamageInfo.MaximumShieldVitality, 70);
        m.F32(di + L.Hlmt.DamageInfo.ShieldStunTime, 5);
        m.F32(di + L.Hlmt.DamageInfo.ShieldRechargeTime, 2);
        m.TagRef(di + L.Hlmt.DamageInfo.ShieldRechargingEffect, Effect(@"fx\shield_recharge", sndTags[@"sound\characters\spartan\shield_recharge"]));
        m.TagRef(di + L.Hlmt.DamageInfo.ShieldDepletedEffect, Effect(@"fx\shield_depleted", sndTags[@"sound\characters\spartan\shield_depleted"]));
        if (withRepresentation)
        {
            long rep = m.Block(m.At(Matg) + L.Matg.PlayerRepresentation, 2, L.Matg.PlayerRepresentationSize);
            var elite = m.AddTag("bipd", @"objects\characters\elite\mp_elite\mp_elite", 0x490);
            m.U8(rep + L.Matg.Representation.ModelChoice, 1); m.U8(rep + L.Matg.Representation.Class, 1);
            m.TagRef(rep + L.Matg.Representation.ThirdPersonUnit, elite);
            long r1 = rep + L.Matg.PlayerRepresentationSize;
            m.U8(r1 + L.Matg.Representation.ModelChoice, 0); m.U8(r1 + L.Matg.Representation.Class, 1);
            m.TagRef(r1 + L.Matg.Representation.ThirdPersonUnit, Biped);
        }
    }

    /// <summary>The .info records + FSB items for every hash in this map, plus filename-only extras.</summary>
    public (List<SynthAudio.FsbItem> Items, List<(uint, uint, int, uint, string)> Info) Bank(SynthAudio.Encoded audio, IEnumerable<SynthWeapon>? weapons = null)
    {
        var items = new List<SynthAudio.FsbItem>();
        var info = new List<(uint, uint, int, uint, string)>();
        void Add(uint hash, string file)
        {
            items.Add(new SynthAudio.FsbItem($"s{items.Count:D5}", audio));
            info.Add((hash, (uint)audio.Frames, audio.Channels, 0, file));
        }
        Add(0x11111111, @"data\sound\ambience\wind\wind_loop\01.aif");
        foreach (var w in weapons ?? DefaultWeapons)
        {
            var folder = w.SoundName.Substring(0, w.SoundName.LastIndexOf('\\'));
            for (int i = 0; i < w.Hashes.Length; i++) Add(w.Hashes[i], $@"data\{w.SoundName}\{19100 + i}_{(char)('a' + i)}.aif");
            Add(0x22000000u + (uint)items.Count, $@"data\{folder}\{w.Id}_fire_lod\far.aif"); // must lose to the real fire sound
        }
        Add(ThrowHash, @"data\sound\weapons\grenade\frag_grenade\throw\throw1.aif");
        Add(ShieldRechargeHash, @"data\sound\characters\spartan\shield_recharge\charge1.aif");
        Add(ShieldDepletedHash, @"data\sound\characters\spartan\shield_depleted\depleted1.aif");
        return (items, info);
    }
}
