namespace CthExtract.Halo;

/// <summary>
/// Raw tag-field offsets for Halo 3 MCC caches. Facts (numbers only) taken from Assembly's plugin XML
/// (XboxChaos/Assembly @ fd14ef3, Plugins/Halo3MCC/*.xml; jpt! and effe from Plugins/Halo3/*.xml because there is
/// no MCC plugin for them). Every read through these offsets is sanity-checked by the caller, because nothing here
/// has been verified against a real install from this repository.
/// </summary>
internal static class HaloLayout
{
    // Gen3 shapes
    public const int TagRefSize = 0x10;      // { i32 group, i32, i32, i32 datum } -> datum at +0xC
    public const int TagRefDatum = 0xC;
    public const int BlockSize = 0xC;        // { i32 count, i32 compressed pointer, i32 pad }

    public static class Object                // weap / eqip / proj / bipd share the object header
    {
        public const int DefaultModelVariant = 0x30; // stringid
        public const int Model = 0x34;               // tagRef hlmt
    }

    public static class Weap                  // base 0x4DC
    {
        public const int MagnificationLevels = 0x31E;  // i16
        public const int MagnificationRange = 0x320;   // rangef (min, max)
        public const int HandleNode = 0x3D8;           // stringid
        public const int FirstPerson = 0x3FC;          // block, elem 0x20: +0x0 mode
        public const int Magazines = 0x424;            // block, elem 0x80
        public const int MagazineSize = 0x80;
        public const int NewTriggers = 0x430;          // block, elem 0x90
        public const int TriggerSize = 0x90;
        public const int Barrels = 0x43C;              // block, elem 0x134
        public const int BarrelSize = 0x134;

        public static class Magazine
        {
            public const int RoundsTotalInitial = 0x6;  // i16
            public const int RoundsTotalMaximum = 0x8;  // i16
            public const int RoundsLoadedMaximum = 0xA; // i16 (clip)
            public const int ReloadTime = 0x10;         // f32 seconds
        }

        public static class Trigger
        {
            public const int Behavior = 0x6;            // enum16: 0 spew, 1 latch, 2 latch-autofire, 3 charge, ...
        }

        public static class Barrel
        {
            public const int RoundsPerSecond = 0x4;     // rangef (min, max)
            public const int ShotsPerFire = 0x1C;       // range16 (min, max)
            public const int FireRecoveryTime = 0x20;   // f32 seconds
            public const int ProjectilesPerShot = 0x76; // i16
            public const int DistributionAngle = 0x78;  // f32 radians
            public const int MinimumError = 0x7C;       // f32 radians
            public const int ErrorAngle = 0x80;         // ranged radians (min, max)
            public const int Projectile = 0x98;         // tagRef proj
            public const int DamageEffect = 0xB8;       // tagRef jpt!
            public const int FiringEffects = 0x128;     // block, elem 0xC4: +0x4 firing effect (effe)
            public const int FiringEffectSize = 0xC4;
            public const int FiringEffect = 0x4;
        }
    }

    public static class Eqip                  // base 0x26C
    {
    }

    public static class Proj                  // base 0x2A0
    {
        public const int MaximumRange = 0x118;          // f32 wu
        public const int DetonationDamage = 0x158;      // tagRef jpt!
        public const int ImpactDamage = 0x1F0;          // tagRef jpt!
        public const int InitialVelocity = 0x23C;       // f32 wu/s
        public const int FinalVelocity = 0x240;         // f32 wu/s
        public const int GuidedAngularVelocityUpper = 0x250; // f32 rad/s
    }

    public static class Jpt                   // Halo3 plugin (no Halo3MCC jpt!.xml), base 0xF0
    {
        public const int Radius = 0x0;                  // rangef wu (min, max)
        public const int DamageLowerBound = 0x1C;       // f32
        public const int DamageUpperBound = 0x20;       // rangef (min, max)
    }

    public static class Hlmt                  // base 0x188
    {
        public const int RenderModel = 0x0;             // tagRef mode (full detail)
        public const int LodRenderModel = 0x54;         // tagRef mode (reduced; not used)
        public const int Variants = 0x64;               // block, elem 0x30
        public const int VariantSize = 0x30;
        public const int NewDamageInfo = 0x88;          // block, elem 0xA4
        public const int DamageInfoSize = 0xA4;

        public static class Variant
        {
            public const int Name = 0x0;                // stringid
            public const int Regions = 0x14;            // block, elem 0x14
            public const int RegionSize = 0x14;
            public const int RegionName = 0x0;          // stringid
            public const int RegionPermutations = 0x8;  // block, elem 0x18
            public const int PermutationSize = 0x18;
            public const int PermutationName = 0x0;     // stringid
        }

        public static class DamageInfo
        {
            public const int MaximumVitality = 0xC;       // f32 body points
            public const int MaximumShieldVitality = 0x20; // f32 shield points
            public const int ShieldStunTime = 0x2C;      // f32 seconds before recharge starts
            public const int ShieldRechargeTime = 0x30;  // f32 seconds from empty to full
            public const int ShieldDamagedEffect = 0x38; // tagRef effe
            public const int ShieldDepletedEffect = 0x48; // tagRef effe
            public const int ShieldRechargingEffect = 0x58; // tagRef effe
        }
    }

    public static class Matg                  // base 0x530
    {
        public const int Grenades = 0x41C;              // block, elem 0x34
        public const int GrenadeSize = 0x34;
        public const int PlayerRepresentation = 0x440;  // block, elem 0x5C
        public const int PlayerRepresentationSize = 0x5C;
        public const int MultiplayerGlobals = 0x49C;    // tagRef mulg

        public static class Grenade
        {
            public const int ThrowingEffect = 0x4;      // tagRef effe
            public const int Equipment = 0x14;          // tagRef eqip
            public const int Projectile = 0x24;         // tagRef proj
        }

        public static class Representation
        {
            public const int ModelChoice = 0x4;         // enum8: 0 spartan, 1 elite
            public const int Class = 0x5;               // enum8: 0 campaign, 1 multiplayer, 2 editor, 3 survival
            public const int ThirdPersonUnit = 0x28;    // tagRef bipd
        }
    }

    public static class Mulg                  // base 0x18
    {
        public const int Universal = 0x0;               // block, elem 0xD0
        public const int WeaponSelections = 0x78;       // in Universal: block, elem 0x18 (+0x0 name, +0x8 weap)
        public const int WeaponSelectionSize = 0x18;
        public const int WeaponSelectionName = 0x0;
        public const int WeaponSelectionWeapon = 0x8;
        public const int GrenadeSelections = 0x90;      // in Universal: block, elem 0x14 (+0x0 name, +0x4 grenade)
        public const int GrenadeSelectionSize = 0x14;
        public const int GrenadeSelectionName = 0x0;
        public const int GrenadeSelectionGrenade = 0x4;
    }

    public static class Effe                  // Halo3 plugin (no Halo3MCC effe.xml; MCC layout unverified)
    {
        public const int Events = 0x2C;                 // block, elem 0x44
        public const int EventSize = 0x44;
        public const int EventParts = 0x20;             // in event: block, elem 0x60
        public const int PartSize = 0x60;
        public const int PartType = 0x10;               // tagRef (snd!, lsnd, ...)
    }

    public static class Snd                   // Halo3MCC snd!, base 0x24
    {
        public const int SoundClass = 0x2;              // enum8 (4 = weapon_fire)
        public const int PitchRangeCount = 0x3;         // i8
        public const int FirstPitchRangeIndex = 0x6;    // i16 into ugh! pitch ranges
    }

    public static class Ugh                   // Halo3MCC ugh! (global sound gestalt), base 0xC8
    {
        public const int PitchRanges = 0x40;            // block, elem 0xC
        public const int PitchRangeSize = 0xC;
        public const int PitchRangeEncodedFirstAndCount = 0x8; // i32: first = (v << 12) >> 12, count = (v >> 20) & 63
        public const int Permutations = 0x4C;           // block, elem 0x14
        public const int PermutationSize = 0x14;
        public const int PermutationFmodHash = 0x10;    // u32 FMod Bank Subsound ID Hash
    }
}
