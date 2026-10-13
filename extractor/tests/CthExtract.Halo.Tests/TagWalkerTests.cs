using Xunit;
using L = CthExtract.Halo.HaloLayout;

namespace CthExtract.Halo.Tests;

/// <summary>Raw-field walks over tag data written at the Assembly Halo3MCC offsets (critique D-H1 "tag-blob test").</summary>
public class TagWalkerTests
{
    [Fact]
    public void BlocksUseCompressedPointersAndTagRefsUseTheDatumIndex()
    {
        var s = new SyntheticHalo();
        var f = new TagFields(s.Map);
        var w = s.Weapons["assault_rifle"];
        var barrels = f.Block(f.Address(w) + L.Weap.Barrels);
        Assert.Equal(1, barrels.Count);
        Assert.True(barrels.Address > 0);
        var proj = f.TagRef(barrels.Address + L.Weap.Barrel.Projectile, "proj");
        Assert.NotNull(proj);
        Assert.Null(f.TagRef(barrels.Address + L.Weap.Barrel.Projectile, "jpt!")); // class is checked
        Assert.Null(f.TagRef(barrels.Address + L.Weap.Barrel.DamageEffect));       // -1 = null
    }

    [Fact]
    public void MultiplayerWeaponSelectionsAreRead()
    {
        var s = new SyntheticHalo();
        var sel = new TagWalker(s.Map).MultiplayerWeapons();
        Assert.Equal(SyntheticHalo.DefaultWeapons.Length, sel.Count);
        Assert.Contains(sel, x => x.Name == "battle_rifle" && x.Weapon == s.Weapons["battle_rifle"]);
    }

    [Fact]
    public void ExactPathResolvesFirst()
    {
        var s = new SyntheticHalo();
        var r = new TagWalker(s.Map).ResolveObject("weap", @"objects\weapons\rifle\assault_rifle\assault_rifle", "assault_rifle", "assault");
        Assert.Equal(s.Weapons["assault_rifle"], r!.Value.Tag);
        Assert.Equal("exact tag path", r.Value.Route);
    }

    [Fact]
    public void MulgSelectionResolvesARenamedWeapon()
    {
        var weapons = SyntheticHalo.DefaultWeapons.Select(x => x.Id == "magnum" ? x with { Path = @"objects\weapons\pistol\m6g\m6g" } : x);
        var s = new SyntheticHalo(weapons: weapons);
        var r = new TagWalker(s.Map).ResolveObject("weap", @"objects\weapons\pistol\magnum\magnum", "magnum", "magnum");
        Assert.Equal(@"objects\weapons\pistol\m6g\m6g", r!.Value.Tag.Name);
        Assert.StartsWith("mulg weapon selection", r.Value.Route);
    }

    [Fact]
    public void LeafNameAndHintFallbacksWithoutMulg()
    {
        var weapons = SyntheticHalo.DefaultWeapons.Select(x => x.Id switch
        {
            "shotgun" => x with { Path = @"objects\weapons\support_low\shotgun\shotgun" },
            "plasma_rifle" => x with { Path = @"objects\weapons\rifle\plasma_rifle_v2\plasma_rifle_v2" },
            _ => x,
        }).Append(SyntheticHalo.DefaultWeapons[0] with { Id = "plasma_pistol", Path = @"objects\weapons\pistol\plasma_pistol\plasma_pistol", SoundName = @"sound\weapons\plasma_pistol\fire", Hashes = new uint[] { 0xB1 } });
        var s = new SyntheticHalo(weapons: weapons, withMulg: false);
        var w = new TagWalker(s.Map);
        var shotgun = w.ResolveObject("weap", @"objects\weapons\rifle\shotgun\shotgun", "shotgun", "shotgun")!.Value;
        Assert.Equal(@"objects\weapons\support_low\shotgun\shotgun", shotgun.Tag.Name);
        Assert.Equal("same leaf name 'shotgun'", shotgun.Route);
        // hint 'plasma' matches both plasma_pistol and plasma_rifle_v2: the one sharing more words with the id wins
        var pr = w.ResolveObject("weap", @"objects\weapons\rifle\plasma_rifle\plasma_rifle", "plasma_rifle", "plasma")!.Value;
        Assert.Equal(@"objects\weapons\rifle\plasma_rifle_v2\plasma_rifle_v2", pr.Tag.Name);
        Assert.Null(w.ResolveObject("weap", @"objects\weapons\melee\energy_blade\energy_blade", "energy_sword", "sword"));
    }

    [Fact]
    public void GrenadeEquipmentFromMatgGrenades()
    {
        var s = new SyntheticHalo();
        var w = new TagWalker(s.Map);
        var g = w.Grenades();
        Assert.Equal(2, g.Count);
        Assert.Equal(s.Grenades["frag_grenade"], g[0].Equipment);
        Assert.NotNull(g[0].Throw);
        Assert.Null(g[1].Throw);
        var r = w.ResolveObject("eqip", @"objects\weapons\grenade\plasma_grenade\plasma_grenade_renamed", "plasma_grenade", "plasma")!.Value;
        Assert.Equal(s.Grenades["plasma_grenade"], r.Tag);
        Assert.StartsWith("matg grenades", r.Route);
    }

    [Fact]
    public void ModelChainReachesTheRenderModel()
    {
        var s = new SyntheticHalo();
        var w = new TagWalker(s.Map);
        var hlmt = w.ModelOf(s.Weapons["sniper_rifle"]);
        Assert.Equal("hlmt", hlmt!.Class);
        Assert.Equal(s.Modes["sniper_rifle"], w.RenderModelOf(hlmt));
    }

    [Fact]
    public void WeaponStatsAreReadAtTheAssemblyOffsets()
    {
        var s = new SyntheticHalo();
        var w = new TagWalker(s.Map);
        var br = w.ReadWeapon(s.Weapons["battle_rifle"], "hitscan");
        Assert.Equal((short)36, br.ClipSize);
        Assert.Equal(2.1f, br.ReloadSeconds);
        Assert.Equal(1, br.TriggerBehavior);
        Assert.Equal((short)3, br.ShotsPerFireMax);
        Assert.Equal(15f, br.RoundsPerSecondMax);
        Assert.Equal(0.28f, br.FireRecoverySeconds);
        Assert.Equal(6f, br.DamageUpperMax);
        Assert.Equal("proj impact", br.DamageSource);
        Assert.Equal((short)1, br.MagnificationLevels);
        Assert.Equal(2f, br.MagnificationMin);

        var rocket = w.ReadWeapon(s.Weapons["rocket_launcher"], "rocket");
        Assert.Equal("proj detonation", rocket.DamageSource);
        Assert.Equal(1.4f, rocket.DamageRadiusMax);
        Assert.Equal(15f, rocket.InitialVelocity);

        var plasma = w.ReadWeapon(s.Weapons["plasma_rifle"], "projectile");
        Assert.Null(plasma.ClipSize); // no magazine: the sheet's battery value stays
    }

    [Fact]
    public void FireSoundChainReachesTheFmodHashes()
    {
        var s = new SyntheticHalo();
        var w = new TagWalker(s.Map);
        var fx = w.FiringEffects(s.Weapons["assault_rifle"]);
        var effe = Assert.Single(fx);
        var snds = w.SoundsOfEffect(effe, out var route);
        Assert.Equal("effe events/parts", route);
        var snd = Assert.Single(snds);
        Assert.Equal(@"sound\weapons\assault_rifle\ar_fire", snd.Name);
        Assert.Equal(new uint[] { 0xA0000001, 0xA0000002 }, w.SoundHashes(snd, out _));
    }

    [Fact]
    public void EffectPartScanFindsSoundsAtAnUnexpectedOffset()
    {
        var s = new SyntheticHalo();
        var m = s.Map;
        // an effe whose sound reference sits at +0x30 of the part instead of +0x10 (MCC layout drift)
        var snd = m.Tags.First(t => t.Class == "snd!" && t.Name.Contains("magnum"));
        var effe = m.AddTag("effe", @"fx\shifted", 0x68);
        long ev = m.Block(m.At(effe) + L.Effe.Events, 1, L.Effe.EventSize);
        long part = m.Block(ev + L.Effe.EventParts, 1, L.Effe.PartSize);
        m.TagRef(part + 0x10, null);
        m.TagRef(part + 0x30, snd);
        var found = new TagWalker(m).SoundsOfEffect(effe, out var route);
        Assert.Equal(snd, Assert.Single(found));
        Assert.Equal("effe part scan", route);
    }

    [Fact]
    public void SpartanBipedAndShieldValues()
    {
        var s = new SyntheticHalo();
        var w = new TagWalker(s.Map);
        var b = w.SpartanBiped()!.Value;
        Assert.Equal(s.Biped, b.Biped); // the multiplayer spartan, not the elite listed first
        Assert.Contains("multiplayer spartan", b.Route);
        var di = w.DamageInfo(w.ModelOf(b.Biped)!);
        var raw = w.ReadShield(di, "x");
        Assert.Equal(70f, raw.ShieldVitality);
        Assert.Equal(45f, raw.BodyVitality);
        Assert.Equal(5f, raw.StunSeconds);
        Assert.Equal(2f, raw.RechargeSeconds);

        var noRep = new SyntheticHalo(withRepresentation: false);
        Assert.Equal("biped by name", new TagWalker(noRep.Map).SpartanBiped()!.Value.Route);
    }

    [Fact]
    public void ImplausibleBlockCountsReadAsEmpty()
    {
        var m = new FakeMap();
        var t = m.AddTag("weap", "w", 0x4DC);
        m.I32(m.At(t) + L.Weap.Barrels, 1 << 20);
        m.I32(m.At(t) + L.Weap.Barrels + 4, 0x400);
        Assert.True(new TagFields(m).Block(m.At(t) + L.Weap.Barrels).IsEmpty);
        var r = new TagWalker(m).ReadWeapon(t, "hitscan");
        Assert.False(r.HasBarrel);
    }

    [Fact]
    public void GarbageMulgPointerDoesNotStopTheNameFallbacks()
    {
        var weapons = SyntheticHalo.DefaultWeapons.Select(x => x.Id == "shotgun" ? x with { Path = @"objects\weapons\support_low\shotgun\shotgun" } : x);
        var s = new SyntheticHalo(weapons: weapons);
        var m = s.Map;
        var mulg = m.Tags.First(t => t.Class == "mulg");
        m.I32(m.At(mulg) + L.Mulg.Universal + 4, 0x7FFFFFF0); // universal block pointer far outside the file
        var log = new List<string>();
        var r = new TagWalker(m, log.Add).ResolveObject("weap", @"objects\weapons\rifle\shotgun\shotgun", "shotgun", "shotgun");
        Assert.Equal("same leaf name 'shotgun'", r!.Value.Route);
        Assert.Contains(log, l => l.Contains("mulg weapon selections unreadable"));
    }

    [Fact]
    public void HintMatchesNeverPickFirstPersonModels()
    {
        var m = new FakeMap();
        m.AddTag("weap", @"objects\weapons\fp\sniper_rifle_fp", 0x4DC);
        Assert.Null(new TagWalker(m).ResolveObject("weap", @"objects\weapons\rifle\sniper_rifle\sniper_rifle", "sniper_rifle", "sniper"));
    }
}
