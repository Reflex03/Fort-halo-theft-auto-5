# Requests from the Halo module (agent key: halo)

The Halo module is coded against the current contract; nothing below blocks it. Each item says exactly what to change.

## 1. `extractor/CthExtract.slnx`: add the new projects

The Halo project already builds them through ProjectReferences, but the solution should list them so
`dotnet test extractor/CthExtract.slnx` runs the Halo tests:

```xml
  <Folder Name="/vendor/">
    <Project Path="vendor/Reclaimer/Reclaimer.Core/Reclaimer.Core.csproj" />
    <Project Path="vendor/Reclaimer/Reclaimer.Blam/Reclaimer.Blam.csproj" />
    <Project Path="vendor/Fmod5Sharp/Fmod5Sharp/Fmod5Sharp.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/CthExtract.Halo.Tests/CthExtract.Halo.Tests.csproj" />
  </Folder>
```

## 2. One Fmod5Sharp in the exe (critique C11 / G10 / L4)

`extractor/vendor/Fmod5Sharp` keeps the assembly name `Fmod5Sharp` and the 3.1.0 public API (source-compatible).
Wherever the Fortnite module's vendored CUE4Parse has `<PackageReference Include="Fmod5Sharp" ... />`, replace it with
`<ProjectReference Include="<relative path>/vendor/Fmod5Sharp/Fmod5Sharp/Fmod5Sharp.csproj" />`. Otherwise the exe
carries two `Fmod5Sharp` assemblies, and the NuGet one ships the FMOD-derived Vorbis table that the vendored copy
replaced with a libvorbis-generated one (see `extractor/vendor/Fmod5Sharp/VENDORED.md`).

## 3. `docs/CONTRACTS.md`: wording to add (no format change)

Under "Cache layout", after the two stats lines:

```
stats/halo_weapons.json keys per weapon are a subset of the weapons sheet columns, in the sheet's units:
  damage (Halo points per bullet/pellet/impact; the mod applies systems.combat.haloPointToMc), fireIntervalTicks,
  burst, pellets, clip, reloadTicks, auto, spreadDeg, projectileSpeed (blocks/tick, non-hitscan only), homing
  (projectile only), blastRadius (blocks, rocket/grenade only), zoom (FOV multiplier). Missing keys = keep the sheet
  value. Conversions: extractor/src/CthExtract.Halo/Stats.cs (StatConversions).
stats/halo_shield.json "max" is the Fortnite-scale bar (= systems.shield.max, 100); Halo supplies the timing:
  rechargeDelayTicks = shield stun time × 20, rechargePerTick = max / (full recharge time × 20).
```

Under "Mesh file", after the Weapons line:

```
- Weapons are true scale (1 Halo world unit = 3.048 m) unless that falls outside the held size, then scaled
  uniformly into it: rifles/launchers 0.6–1.2 m, pistols (magnum, needler) 0.3–0.6 m, grenades 0.12–0.3 m along +Z.
  The factor is in the manifest detail ("held scale ×…"). Material textures: textures/halo/<weapon id>/<n>.png.
```

Under "Sound events":

```
Halo sounds are a lossless remux of the game's Vorbis and keep its channel count; stereo files play
non-positionally in Minecraft (research/halo.md §3.6: downmix in the mod if a positional Halo sound is needed).
```

## 4. Notices for the release (packaging)

Add to THIRD-PARTY-NOTICES of the extractor:
- Reclaimer (GPL-3.0), vendored + patched: `extractor/vendor/Reclaimer` (commit 36224d7a, VENDORED.md).
- Fmod5Sharp (MIT) incl. its NOTICE (vgmstream ISC text): `extractor/vendor/Fmod5Sharp` (commit 28951c6, VENDORED.md).
  Its Vorbis setup table is generated from libvorbis 1.3.7 / libogg 1.3.5 (BSD-3-Clause, Xiph.Org): add the Xiph
  BSD-3 notice.
- NuGet: NVorbis 0.10.5 (MIT), OggVorbisEncoder 1.2.2 (MIT; a libvorbis port: Xiph BSD-3 notice), NAudio.Core 2.3.0
  (MIT), System.Drawing.Common 10.0.9 (MIT, Reclaimer dependency).

## 5. Test note (no change needed)

`dotnet test extractor/tests/CthExtract.Halo.Tests` runs everything on synthetic data. Two cross-checks also need
clones of the upstream repos (reference only, never vendored) and skip otherwise: set `CTH_REFS` to a folder holding
`Reclaimer/` (RMF oracle: MeshBuilder vs Reclaimer's own Python RMF reader; needs `python3`) and `Fmod5Sharp/`
(seek-bit finder vs all 161 upstream setup headers). `CTH_TEST_LOG_DIR` keeps each synthetic run's extract.log.
