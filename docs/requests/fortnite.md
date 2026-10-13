# Requests from the Fortnite module (agent key: fortnite)

The module (`extractor/src/CthExtract.Fortnite`) is coded against everything below already; nothing here blocks it.
Each item says exactly what to change and why.

## 1. `extractor/CthExtract.slnx`: add the new projects

```xml
  <Folder Name="/vendor/">
    <Project Path="vendor/CUE4Parse/CUE4Parse/CUE4Parse.csproj" />
    <Project Path="vendor/CUE4Parse/CUE4Parse-Conversion/CUE4Parse-Conversion.csproj" />
    <Project Path="vendor/vgmstream-binka/VgmstreamBinka.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/CthExtract.Fortnite.Tests/CthExtract.Fortnite.Tests.csproj" />
  </Folder>
```

`CthExtract.Fortnite.csproj` references `src/CthExtract.Oodle` automatically when that project exists (define
`CTH_OODLE`). `-p:CthNoOodle=true` builds without it (Oodle-compressed containers then report Failed with a clear
reason). The vendored CUE4Parse has its own `Directory.Build.props` / `Directory.Packages.props` (central package
versions) that apply only below `extractor/vendor/CUE4Parse`.

## 2. `ExtractContext`: expose the cache root (Core) — same as the GTA request

Keys and mappings are cached in `cache/keys/fortnite`. Until `ExtractContext.CacheDir` exists,
`FortniteModule.DefaultKeysDir` uses the parent of `StatsDir` when that folder is named `stats` (else `WorkDir/keys`);
`FortniteOptions.KeysDir` overrides it.

## 3. `docs/CONTRACTS.md`: cache layout

Add under "Cache layout":

```
keys/fortnite/<build>_<host>.json   AES main + dynamic keys and the mapping file name for one Fortnite build, as
                                    fetched from api.fortniteporting.app / uedb.dev / fortnite-api.com (local only,
                                    never shipped; lets later runs work offline)
keys/fortnite/*.usmap               the matching type mappings (.usmap), md5-checked when the service gives one
```

## 4. `docs/CONTRACTS.md`: mesh notes for Fortnite

Add to the `.cthm` notes:

- Fortnite axes: Minecraft `(x, y, z) = (-UE.Y, UE.Z, UE.X) / 100`, UE index order kept (the map is a reflection, so UE's
  clockwise front faces become counter-clockwise). Skeletal meshes are in their bind pose.
- LOD rule: the most detailed **game** LOD whose triangle count fits `sources.maxTris`; only when none fits, the
  smallest LOD is decimated by `ExtractContext.Mesh`. (The glider row's locator says "LOD0": LOD0 is used when it fits
  4000 triangles; otherwise a lower game LOD, which looks better than decimating LOD0.)
- Battle Bus: centred in X/Z, lowest point y = 0, UE forward → +Z. If the bus' long axis comes out along X it is turned
  90° so the length runs along Z; which end is the front is then unverified (logged).
- Chest: centred in X/Z, lowest point y = 0, true size (metres); UE forward → +Z (which side opens is unverified).
- Glider: origin = the UE mesh origin after the item's `GliderMeshTransform` (the attach socket), wing span along X
  (turned 90° when the span comes out along Z).
- Material textures: `textures/fortnite/<battle_bus|glider|chest>/<n>.png`, diffuse only, alpha forced opaque; a
  material whose diffuse cannot be read gets a 4×4 flat grey texture so the mesh still renders. UVs can lie outside
  0..1 (UE tiles): sample with wrap/repeat.

## 5. `docs/CONTRACTS.md`: sound notes

- Fortnite sounds stored as Ogg Vorbis are copied unchanged (they keep their channel count; a stereo one plays
  non-positionally). Bink Audio / PCM / ADPCM sounds are decoded, down-mixed to mono and encoded (Vorbis q0.1, falling
  back to q0.0/-0.1); those files end with up to ~45 ms of silence (encoder padding).
- RAD Audio (`RADA`) and Opus (`UEOP`) sound waves cannot be decoded with redistributable code: the row is reported
  Missing with that reason and the placeholder stays.

## 6. `sheets/sources.json` (optional wording; keep `_unverified`)

The code reads names/regexes out of these fields, so only change them together with `RecipeBookTests`:

- `fn_mesh_glider` locator: `AthenaGliderItemDefinition named Glider_Default / Umbrella_Default -> SkeletalMesh (+ GliderMeshTransform), most detailed LOD within maxTris`
- `fn_snd_drink` locator: `item definition of the shield potion -> DataList FortItemComponentData_WeaponPrimaryFireSounds.PrimaryFireSoundMap (or any use/consume sound) -> SoundCue/MetaSound -> SoundWave`
- `fn_snd_heal_use` locator: same with the bandage/medkit item
- `fn_snd_chest_open` locator: `BuildingContainer blueprint (see fn_mesh_chest) class defaults SearchingSoundCueLoop, then LootRepeatSoundCue -> SoundWave (OGG kept, BINKA/PCM/ADPCM decoded, RADA -> placeholder)`

## 7. Network access at extraction time (packaging / Melty listing)

The Fortnite step downloads, for the installed build only: AES keys from `api.fortniteporting.app`, `uedb.dev` and
`fortnite-api.com`, and the `.usmap` mapping file from `supabase.fortniteporting.app` / `data.uedb.dev`. Nothing else:
every request goes through `HttpGuard` (https only, those five hosts only, never a `.dll/.so/.exe/.zip/.gz/...` path,
redirects re-checked). No Epic CDN streaming (missing on-demand content is reported missing). Offline runs reuse
`cache/keys/fortnite`; with no network and no cache every Fortnite row is Missing with "no Fortnite AES keys/mappings
available ... connect to the internet and run again". This is the FModel/FortnitePorting model; whether to ship it is
the user's/Melty's call (critique L8).

## 8. Third-party notices (packaging)

New runtime dependencies of the extractor from this module:

- CUE4Parse + CUE4Parse-Conversion, vendored at `1e3293d4` + `5ca634b5`, stripped/patched: **Apache-2.0**; ship
  `extractor/vendor/CUE4Parse/LICENSE` and `NOTICE` (Apache §4(d)).
- vgmstream Bink Audio decoder, C# port (`extractor/vendor/vgmstream-binka`): **ISC-style** (`LICENSE`).
- NuGet (resolved versions): AssetRipper.TextureDecoder 2.6.2 (MIT), Blake3 3.0.2 (BSD-2-Clause),
  BouncyCastle.Cryptography 2.6.2 (MIT), CommunityToolkit.HighPerformance 8.4.2 (MIT), FixedMathSharp 4.0.1 (MIT),
  GenericReader 2.3.0 (MIT), Infrablack.UE4Config 0.7.2.97 (MIT), K4os.Compression.LZ4(.Streams) 1.3.8 and
  K4os.Hash.xxHash 1.0.8 (MIT), LZMA-SDK 22.1.1 (MIT), MemoryPack 1.21.4 (MIT), Newtonsoft.Json 13.0.4 (MIT),
  OffiUtils 3.2.0 (MIT), Serilog 4.3.1 + Serilog.Sinks.Console 6.1.1 (Apache-2.0), SharpGLTF.Core/Runtime/Toolkit 1.0.6
  (MIT), SixLabors.ImageSharp 3.1.12 (Six Labors Split License → Apache-2.0 for open-source use; NU1902/NU1903
  advisories, not on our code path), SkiaSharp 2.88.9 managed only (MIT; native libs excluded), SubstreamSharp 1.0.3
  (MIT), System.IO.Hashing 10.0.8 / System.Numerics.Tensors 10.0.10 (MIT), VGAudio 2.2.1 (MIT), ZstdSharp.Port 0.8.8
  (MIT), OggVorbisEncoder 1.2.2 (MIT; libvorbis port → add the Xiph.Org BSD-3 notice), NVorbis 0.10.5 (MIT),
  NAudio.Core 2.3.0 (MIT, via Fmod5Sharp).
- Not shipped, not downloaded: OodleSharp, Oodle.NET, Zlib-ng.NET, oo2core/oodle-data-shared, Detex/crunch/tegra,
  ProSpiDecryptor, libSkiaSharp, radadec/binkadec, any Fortnite key or `.usmap`.

Test-only (not shipped): xunit.v3 3.2.2 (Apache-2.0), xunit.runner.visualstudio 3.1.5 (Apache-2.0),
Microsoft.NET.Test.Sdk 18.6.0 (MIT). CUE4Parse's Apache-2.0 fixtures are read from `CTH_REFS/CUE4Parse/...` when present
(never vendored); `CTH_VGMSTREAM_CLI` enables the live vgmstream comparison.

## 9. First real-PC run: what to look at in `extract.log`

Every lookup logs its candidates and choice (`[fortnite] ...`). The lines that decide whether the recipes need tuning:
`key candidate:` / `main key test` / `mapping:` (keys and usmap), `asset registry ...: N assets`,
`AthenaBattleBusItemDefinition: N definitions [...]`, `<bus BP> components: [...]`, `chest classes by ...`,
`<row>: item ... DataList [...]`, `sound references of ...`, and each sound's codec (`OGG kept`, `Bink Audio`, `RAD
Audio`). If Fortnite's sounds turn out to be RADA, the three sound rows stay on their vanilla placeholders.
