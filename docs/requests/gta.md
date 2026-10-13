# Requests from the GTA V module (extractor/src/CthExtract.Gta)

The module is coded against everything below already; nothing here blocks it. Each item says what to change and why.

## 1. `ExtractContext`: expose the cache root (Core)

The brief says to cache the player's AES key in `cache/keys`, but `ExtractContext` only exposes `PackDir`,
`StatsDir` and `WorkDir`. Requested addition to `extractor/src/CthExtract.Core/ExtractContext.cs`:

```csharp
/// <summary>The cache root (cache/), for module-private files such as cache/keys.</summary>
public string CacheDir { get; }
```

Until then `GtaModule.DefaultKeysDir` uses the parent of `StatsDir` when that folder is named `stats`, and falls back
to `WorkDir/keys` otherwise. `GtaOptions.KeysDir` overrides both.

## 2. docs/CONTRACTS.md: cache layout

Add one line under "Cache layout":

```
keys/gta5_aes_key.json    the player's own GTA V PC AES key, found in their GTA5.exe (base64 + exe size/mtime);
                          local only, re-verified by SHA1 on load. NG keys/tables are never written anywhere.
```

## 3. sheets/sources.json `gta_snd_siren`: the three ids are now identified

The ids were matched by hash (`joaat(name) & 0x1FFFFFFF`, unit test `Sheet_siren_ids_are_these_names`):
`0x0D329446 = siren_pa20a_wail`, `0x159C9182 = siren_firetruck_wail_01`, `0x161768E7 = siren_firetruck_quick_01`.
So two of the three are fire-truck tones. Suggested text (the code already does exactly this; keep `_unverified`):

- locator: `x64/audio/sfx/RESIDENT.rpf/vehicles.awc stream siren_pa20a_wail (0x0D329446), then siren_firetruck_wail_01 (0x159C9182), siren_firetruck_quick_01 (0x161768E7) (IMA-ADPCM -> OGG)`
- fallback: `longest looping stream whose id is the hash of a siren_* name (siren_wail_01, siren_quick_01, siren_police_*, ...)`

The first real-PC run logs every stream of vehicles.awc (id, name when known, codec, rate, length, loop flag), so
the police tone can be picked from the log if `siren_pa20a_wail` turns out to be the wrong sound.

## 4. docs/CONTRACTS.md: mesh notes for vehicles

Add to the `.cthm` notes:

- GTA wheels map to nodes `wheel_lf`, `wheel_rf`, `wheel_lb` (GTA `wheel_lr`), `wheel_rb` (GTA `wheel_rr`);
  six-wheelers also get `wheel_lm1..3` / `wheel_rm1..3`. Each wheel's submeshes use its node; the pivot is the wheel
  centre. Right-side wheels are the left wheel rotated 180 degrees about the car's forward axis (as the game does),
  so triangle winding and normals stay valid.
- Minecraft left = +X when facing +Z (GTA x is negated: mc = (-x, z, y)).
- Vehicle material UVs can lie outside 0..1 (GTA tiles some textures): sample with wrap/repeat.
- Glass is written opaque (dark tint) because cut-out rendering would drop it; decals/badges keep their alpha.
- Paint-shader materials are a baked default colour (police white primary / black secondary), optionally with the
  shader's diffuse decals composited on top.

## 5. docs/CONTRACTS.md: sound notes

- GTA sounds are mono Ogg Vorbis (positional). Because of the encoder, an .ogg may end with up to ~20 ms of silence;
  a looped siren therefore has a tiny gap per loop.

## 6. Network access at extraction time (packaging / Melty listing)

When any archive the module reads is NG-encrypted (expected for a retail install), the extractor downloads
`https://raw.githubusercontent.com/dexyfex/CodeWalker/485d56bec00262ed7fa472261cce7bbc6202b96e/CodeWalker.Core/Resources/magic.dat`
(154,069 bytes, SHA-256 `dc35981f822e892ced3aa81d31e7a96927d573ee28f67417592b5afeaf330832`, checked here on
2026-10-12). It is held in memory only and every decoded key/table is checked against gta-toolkit's SHA1 list. Without
network access, GTA rows report Missing with "NG key blob unavailable ...". Packaging should mention this.

## 7. extractor/CthExtract.slnx: add the test project

```xml
<Folder Name="/tests/">
  <Project Path="tests/CthExtract.Gta.Tests/CthExtract.Gta.Tests.csproj" />
</Folder>
```

## 8. Third-party notices (packaging)

New runtime dependencies of the extractor from this module:

- BCnEncoder.Net 2.3.0: MIT OR Unlicense
- CommunityToolkit.HighPerformance 8.4.2: MIT
- OggVorbisEncoder 1.2.2: MIT. It is a C# port of libvorbis, so include the Xiph.Org BSD-3 notice as well.
- NVorbis 0.10.5: MIT. It decodes every encoded .ogg so the result can be checked against the source.
- gta-toolkit subset: MIT header from `extractor/vendor/gta-toolkit/LICENSE`

Test-only dependencies (not shipped): xunit.v3 3.2.2 (Apache-2.0), xunit.runner.visualstudio 3.1.5 (Apache-2.0),
Microsoft.NET.Test.Sdk 18.6.0 (MIT).

## 9. GTA V Enhanced

The Enhanced edition uses gen9 resources (YFT 171, YTD 5), which this module does not parse yet. It finds and
opens the Enhanced install (`GTA5_Enhanced.exe`, same keys). Vehicle rows are then reported Failed ("Enhanced gen9
fragment; not supported") and the star Missing, each with that reason. Legacy (`GTA5.exe`) is preferred when both
are installed. A sheet note would make this visible.
