using System.Globalization;
using System.Text.Json.Nodes;

namespace CthExtract.Halo;

/// <summary>Weapon fields as stored in the tags (Halo units: seconds, world units, radians, Halo damage points).</summary>
public sealed class RawWeaponStats
{
    public string? WeaponTag;            // weap (null for grenades)
    public short? ClipSize;              // Magazines[0] Rounds Loaded Maximum
    public float? ReloadSeconds;         // Magazines[0] Reload Time
    public int? TriggerBehavior;         // New Triggers[0] Behavior
    public float RoundsPerSecondMin, RoundsPerSecondMax; // Barrels[0] Rounds Per Second
    public short ShotsPerFireMin, ShotsPerFireMax;       // Barrels[0] Shots Per Fire
    public float FireRecoverySeconds;    // Barrels[0] Fire Recovery Time
    public short ProjectilesPerShot;     // Barrels[0] Projectiles Per Shot
    public float DistributionAngleRad;   // Barrels[0] Distribution Angle
    public float MinimumErrorRad;        // Barrels[0] Minimum Error (single wield)
    public float ErrorAngleMinRad, ErrorAngleMaxRad; // Barrels[0] Error Angle (single wield)
    public bool HasBarrel;
    public short MagnificationLevels;
    public float MagnificationMin, MagnificationMax;

    public string? ProjectileTag;
    public float? InitialVelocity;       // wu/s
    public float? GuidedAngularVelocity; // rad/s (upper)

    public string? DamageTag;            // jpt!
    public string? DamageSource;         // "proj impact" | "proj detonation" | "barrel damage effect"
    public float? DamageLowerBound, DamageUpperMin, DamageUpperMax;
    public float? DamageRadiusMax;       // wu
}

/// <summary>Shield fields of the multiplayer Spartan (hlmt New Damage Info[0]).</summary>
public sealed class RawShieldStats
{
    public string? Source;
    public float? BodyVitality, ShieldVitality, StunSeconds, RechargeSeconds;
}

/// <summary>
/// Converts tag values into the weapons-sheet columns. All conversions in one place:
/// <list type="bullet">
/// <item>damage (Halo points per bullet/pellet/impact) = the damage effect's "Damage Upper Bound" max (falls back to its
///   min, then "Damage Lower Bound"). Hitscan/projectile weapons use the projectile's Impact Damage, rockets and grenades
///   its Detonation Damage, and weapons without a projectile the barrel's Damage Effect. The mod applies
///   systems.combat.haloPointToMc; the extractor never converts to Minecraft health.</item>
/// <item>fireIntervalTicks = 20 ticks/s × (burst &gt; 1 ? burst / rps + fire recovery : max(1 / rps, fire recovery)),
///   rps = the barrel's Rounds Per Second max (the rate an automatic weapon spins up to), rounded, clamped 1..200.</item>
/// <item>burst = Shots Per Fire max; pellets = Projectiles Per Shot; clip = Rounds Loaded Maximum of magazine 0
///   (energy weapons have no magazine and keep the sheet value).</item>
/// <item>reloadTicks = Reload Time × 20. auto = trigger behaviour Spew (0) or Spew-Charge (6).</item>
/// <item>spreadDeg = degrees(Distribution Angle) / 2 for multi-pellet weapons (half-angle of the pellet cone), else
///   degrees(max(Minimum Error, Error Angle min)).</item>
/// <item>projectileSpeed (blocks/tick) = Initial Velocity (wu/s) × 3.048 m/wu / 20 ticks/s, only for non-hitscan kinds.</item>
/// <item>homing = Guided Angular Velocity Upper &gt; 0 (projectile kinds only).</item>
/// <item>blastRadius (blocks) = damage effect Radius max × 3.048, rockets and grenades only.</item>
/// <item>zoom (FOV multiplier) = 1 / Magnification Range min when the weapon has magnification levels.</item>
/// </list>
/// Every value is range-checked; out-of-range values are dropped (the sheet default stays) and noted.
/// reserve and range are design values of this mashup and are not read from the tags.
/// </summary>
public static class StatConversions
{
    public const double TicksPerSecond = 20.0;
    public const double MetresPerWorldUnit = 3.048;

    /// <summary>systems.shield.max (sheets/systems.json): the shield bar is on the Fortnite 0..100 scale so Fortnite
    /// shield items keep their meaning. Halo supplies the timing (delay and full-recharge time).</summary>
    public const double ShieldBarMax = 100.0;

    public static JsonObject Weapon(RawWeaponStats r, string kind, List<string> notes)
    {
        var o = new JsonObject();
        bool hitscan = kind == "hitscan";
        bool explosive = kind is "rocket" or "grenade";

        void Put(string key, double value, double min, double max, int decimals = 3)
        {
            if (!double.IsFinite(value) || value < min || value > max)
            {
                notes.Add($"{key}={value.ToString("0.###", CultureInfo.InvariantCulture)} out of range [{min},{max}], sheet default kept");
                return;
            }
            o[key] = Math.Round(value, decimals);
        }
        void PutInt(string key, double value, int min, int max)
        {
            if (!double.IsFinite(value) || value < min || value > max)
            {
                notes.Add($"{key}={value.ToString("0.###", CultureInfo.InvariantCulture)} out of range [{min},{max}], sheet default kept");
                return;
            }
            o[key] = (int)Math.Round(value);
        }

        // damage
        float? dmg = r.DamageUpperMax is > 0 ? r.DamageUpperMax : r.DamageUpperMin is > 0 ? r.DamageUpperMin : r.DamageLowerBound is > 0 ? r.DamageLowerBound : null;
        if (dmg is { } d) Put("damage", d, 0.1, 2000, 2);
        else notes.Add("no damage effect read");

        if (r.HasBarrel)
        {
            int burst = Math.Max((int)r.ShotsPerFireMin, (int)r.ShotsPerFireMax);
            if (burst < 1) burst = 1;
            PutInt("burst", burst, 1, 20);
            if (kind != "grenade") PutInt("pellets", Math.Max(1, (int)r.ProjectilesPerShot), 1, 64);

            double rps = Math.Max(r.RoundsPerSecondMin, r.RoundsPerSecondMax);
            if (rps > 0 && double.IsFinite(rps))
            {
                double rec = Math.Max(0, (double)r.FireRecoverySeconds);
                double interval = burst > 1 ? burst / rps + rec : Math.Max(1.0 / rps, rec);
                PutInt("fireIntervalTicks", Math.Max(1, Math.Round(interval * TicksPerSecond)), 1, 200);
            }
            else notes.Add($"rounds per second {rps} unusable");

            double spread = r.ProjectilesPerShot > 1
                ? RadToDeg(r.DistributionAngleRad) / 2
                : RadToDeg(Math.Max(r.MinimumErrorRad, r.ErrorAngleMinRad));
            Put("spreadDeg", spread, 0, 45, 2);
        }

        if (r.TriggerBehavior is { } tb && kind != "grenade") o["auto"] = tb is 0 or 6;
        if (r.ClipSize is { } clip && kind != "grenade") PutInt("clip", clip, 1, 1000);
        if (r.ReloadSeconds is { } rs && kind != "grenade" && rs > 0) PutInt("reloadTicks", rs * TicksPerSecond, 1, 400);

        if (!hitscan && r.InitialVelocity is { } v)
            Put("projectileSpeed", v * MetresPerWorldUnit / TicksPerSecond, 0.05, 20, 3);
        if (!hitscan && r.GuidedAngularVelocity is { } g && kind == "projectile")
            o["homing"] = g > 0.001f;
        if (explosive && r.DamageRadiusMax is { } rad)
            Put("blastRadius", rad * MetresPerWorldUnit, 0.2, 30, 2);

        if (r.MagnificationLevels > 0 && r.MagnificationMin > 1.01f)
            Put("zoom", 1.0 / r.MagnificationMin, 0.02, 1, 3);
        else if (r.WeaponTag != null && kind != "grenade")
            o["zoom"] = 1.0;

        return o;
    }

    public static JsonObject Shield(RawShieldStats s, List<string> notes)
    {
        var o = new JsonObject { ["max"] = ShieldBarMax };
        if (s.StunSeconds is { } stun && double.IsFinite(stun) && stun >= 0.2 && stun <= 60)
            o["rechargeDelayTicks"] = (int)Math.Round(stun * TicksPerSecond);
        else notes.Add($"shield stun time {s.StunSeconds} unusable, sheet default kept");
        if (s.RechargeSeconds is { } rec && double.IsFinite(rec) && rec >= 0.1 && rec <= 60)
            o["rechargePerTick"] = Math.Round(ShieldBarMax / (rec * TicksPerSecond), 4);
        else notes.Add($"shield recharge time {s.RechargeSeconds} unusable, sheet default kept");
        // the raw game values (shield/body vitality, stun and recharge seconds) go to the row detail and the log only:
        // the file keeps exactly the keys of the contract
        return o;
    }

    public static double RadToDeg(double rad) => rad * 180.0 / Math.PI;
}
