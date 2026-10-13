using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CthExtract.Halo.Tests;

public class StatsTests
{
    private static RawWeaponStats Ar() => new()
    {
        WeaponTag = "ar", HasBarrel = true, ClipSize = 32, ReloadSeconds = 2.2f, TriggerBehavior = 0,
        RoundsPerSecondMin = 9, RoundsPerSecondMax = 10, ShotsPerFireMin = 1, ShotsPerFireMax = 1, FireRecoverySeconds = 0,
        ProjectilesPerShot = 1, MinimumErrorRad = 0.002f, ErrorAngleMinRad = 0.035f, ErrorAngleMaxRad = 0.1f,
        InitialVelocity = 400, DamageLowerBound = 6.5f, DamageUpperMin = 7.5f, DamageUpperMax = 7.5f,
    };

    [Fact]
    public void AssaultRifleConvertsToSheetColumns()
    {
        var notes = new List<string>();
        var o = StatConversions.Weapon(Ar(), "hitscan", notes);
        Assert.Equal(7.5, o["damage"]!.GetValue<double>());
        Assert.Equal(32, o["clip"]!.GetValue<int>());
        Assert.Equal(2, o["fireIntervalTicks"]!.GetValue<int>());   // 10 rps -> 0.1 s -> 2 ticks
        Assert.Equal(44, o["reloadTicks"]!.GetValue<int>());        // 2.2 s
        Assert.True(o["auto"]!.GetValue<bool>());
        Assert.Equal(1, o["burst"]!.GetValue<int>());
        Assert.Equal(1, o["pellets"]!.GetValue<int>());
        Assert.Equal(2.01, o["spreadDeg"]!.GetValue<double>(), 2);  // 0.035 rad
        Assert.Null(o["projectileSpeed"]);                          // hitscan
        Assert.Null(o["blastRadius"]);
        Assert.Equal(1.0, o["zoom"]!.GetValue<double>());
        Assert.Empty(notes);
    }

    [Fact]
    public void BurstWeaponIntervalIsBurstPlusRecovery()
    {
        var r = Ar();
        r.TriggerBehavior = 1; r.ShotsPerFireMin = 3; r.ShotsPerFireMax = 3; r.RoundsPerSecondMin = r.RoundsPerSecondMax = 15;
        r.FireRecoverySeconds = 0.28f; r.MagnificationLevels = 1; r.MagnificationMin = 2; r.MagnificationMax = 4;
        var o = StatConversions.Weapon(r, "hitscan", new());
        Assert.Equal(3, o["burst"]!.GetValue<int>());
        Assert.Equal(10, o["fireIntervalTicks"]!.GetValue<int>()); // (3/15 + 0.28) * 20 = 9.6
        Assert.False(o["auto"]!.GetValue<bool>());
        Assert.Equal(0.5, o["zoom"]!.GetValue<double>());
    }

    [Fact]
    public void SemiAutoUsesTheSlowerOfRateAndRecovery()
    {
        var r = Ar();
        r.TriggerBehavior = 1; r.RoundsPerSecondMin = r.RoundsPerSecondMax = 4; r.FireRecoverySeconds = 0.7f;
        Assert.Equal(14, StatConversions.Weapon(r, "hitscan", new())["fireIntervalTicks"]!.GetValue<int>());
        r.FireRecoverySeconds = 0.1f;
        Assert.Equal(5, StatConversions.Weapon(r, "hitscan", new())["fireIntervalTicks"]!.GetValue<int>());
    }

    [Fact]
    public void ShotgunSpreadIsHalfTheDistributionCone()
    {
        var r = Ar();
        r.ProjectilesPerShot = 15; r.DistributionAngleRad = 0.24f;
        var o = StatConversions.Weapon(r, "hitscan", new());
        Assert.Equal(15, o["pellets"]!.GetValue<int>());
        Assert.Equal(6.88, o["spreadDeg"]!.GetValue<double>(), 2);
    }

    [Fact]
    public void ProjectilesGetSpeedAndHomingRocketsGetBlastRadius()
    {
        var r = Ar();
        r.InitialVelocity = 12; r.GuidedAngularVelocity = 2.5f;
        var o = StatConversions.Weapon(r, "projectile", new());
        Assert.Equal(1.829, o["projectileSpeed"]!.GetValue<double>(), 3); // 12 wu/s * 3.048 / 20
        Assert.True(o["homing"]!.GetValue<bool>());
        Assert.Null(o["blastRadius"]);

        var rk = Ar();
        rk.InitialVelocity = 15; rk.DamageUpperMax = 200; rk.DamageRadiusMax = 1.4f;
        var k = StatConversions.Weapon(rk, "rocket", new());
        Assert.Equal(4.27, k["blastRadius"]!.GetValue<double>(), 2);
        Assert.Equal(200, k["damage"]!.GetValue<double>());
        Assert.Null(k["homing"]); // only for projectile kind
    }

    [Fact]
    public void GrenadeFromProjectileOnly()
    {
        var g = new RawWeaponStats { InitialVelocity = 8, DamageUpperMin = 120, DamageUpperMax = 125, DamageRadiusMax = 1.2f };
        var o = StatConversions.Weapon(g, "grenade", new());
        Assert.Equal(125, o["damage"]!.GetValue<double>());
        Assert.Equal(1.219, o["projectileSpeed"]!.GetValue<double>(), 3);
        Assert.Equal(3.66, o["blastRadius"]!.GetValue<double>(), 2);
        Assert.Null(o["clip"]); Assert.Null(o["auto"]); Assert.Null(o["zoom"]); Assert.Null(o["fireIntervalTicks"]);
    }

    [Fact]
    public void GarbageValuesAreDroppedWithNotes()
    {
        var r = Ar();
        r.DamageUpperMax = float.NaN; r.DamageUpperMin = 1e9f; r.DamageLowerBound = null;
        r.ClipSize = -3; r.RoundsPerSecondMin = r.RoundsPerSecondMax = 0; r.ReloadSeconds = 1e6f;
        var notes = new List<string>();
        var o = StatConversions.Weapon(r, "hitscan", notes);
        Assert.Null(o["clip"]); Assert.Null(o["fireIntervalTicks"]); Assert.Null(o["reloadTicks"]);
        Assert.True(notes.Count >= 3);
        Assert.Null(o["damage"]); // NaN max falls back to the huge min, which is out of range
    }

    [Fact]
    public void ShieldKeepsTheFortniteBarAndHalosTiming()
    {
        var notes = new List<string>();
        var o = StatConversions.Shield(new RawShieldStats { BodyVitality = 45, ShieldVitality = 70, StunSeconds = 5, RechargeSeconds = 2 }, notes);
        Assert.Equal(100, o["max"]!.GetValue<double>());
        Assert.Equal(100, o["rechargeDelayTicks"]!.GetValue<int>());
        Assert.Equal(2.5, o["rechargePerTick"]!.GetValue<double>(), 4); // 100 points over 40 ticks
        Assert.Equal(new[] { "max", "rechargeDelayTicks", "rechargePerTick" }, o.Select(kv => kv.Key)); // contract keys only
        Assert.Empty(notes);

        var bad = StatConversions.Shield(new RawShieldStats { StunSeconds = -1, RechargeSeconds = float.PositiveInfinity }, notes);
        Assert.Null(bad["rechargeDelayTicks"]);
        Assert.Null(bad["rechargePerTick"]);
        Assert.Equal(2, notes.Count);
    }

    // ---- the C# mirrors of sheet values must match the sheets (skipped when the repo sheets are not next to the tests) ----

    private static string? Sheet(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "sheets", name);
            if (File.Exists(f)) return f;
        }
        return null;
    }

    [Fact]
    public void ShieldBarMaxMatchesSystemsSheet()
    {
        var f = Sheet("systems.json");
        Assert.SkipWhen(f is null, "sheets/systems.json not found");
        var rows = JsonNode.Parse(File.ReadAllText(f!))!["rows"]!.AsArray();
        var max = rows.First(r => r!["system"]!.GetValue<string>() == "shield" && r["key"]!.GetValue<string>() == "max")!["value"]!.GetValue<double>();
        Assert.Equal(StatConversions.ShieldBarMax, max);
    }

    [Fact]
    public void WeaponKindsMatchWeaponsSheet()
    {
        var f = Sheet("weapons.json");
        Assert.SkipWhen(f is null, "sheets/weapons.json not found");
        var rows = JsonNode.Parse(File.ReadAllText(f!))!["rows"]!.AsArray();
        foreach (var r in rows)
            Assert.Equal(r!["kind"]!.GetValue<string>(), HaloPlan.Kinds[r["id"]!.GetValue<string>()]);
        Assert.Equal(rows.Count, HaloPlan.Kinds.Count);
    }

    [Fact]
    public void StatKeysAreWeaponsSheetColumns()
    {
        var f = Sheet("weapons.json");
        Assert.SkipWhen(f is null, "sheets/weapons.json not found");
        var cols = JsonNode.Parse(File.ReadAllText(f!))!["columns"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        var r = Ar(); r.MagnificationLevels = 1; r.MagnificationMin = 2; r.GuidedAngularVelocity = 1; r.DamageRadiusMax = 1;
        foreach (var kind in new[] { "hitscan", "projectile", "rocket", "grenade" })
            foreach (var key in StatConversions.Weapon(r, kind, new()).Select(kv => kv.Key))
                Assert.Contains(key, cols);
    }
}
