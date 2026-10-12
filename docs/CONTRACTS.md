# Craft Theft Halo: contracts between the pieces

The sheets in `sheets/` are the source of truth. `tools/preflight.py` must be clean before any build, and
`tools/gen.py` turns sheet rows into code (`fabric/.../gen/Sheets.java`, `extractor/src/CthExtract.Core/Generated/Sources.g.cs`).

## Pieces

| Piece | Where | License | Runs |
|---|---|---|---|
| Fabric mod `crafttheft` | `fabric/` | CC BY-NC-SA 4.0 | inside Minecraft 1.21.1 (Fabric Loader 0.19.5, Fabric API 0.116.17+1.21.1) |
| Extractor `cthextract.exe` | `extractor/` | GPL-3.0-only (uses Reclaimer, GPL-3.0) | on the player's PC, before the first Play and when the mod asks |
| Bundle (Prism + instance) | `packaging/` | Prism: GPL-3.0; ours: CC BY-NC-SA 4.0 | Melty unpacks it to `%LOCALAPPDATA%/CraftTheftHalo` |

The mod and the extractor are separate programs that only exchange files (the cache below).

## Extractor command line

```
cthextract.exe --halo <MCC folder> --gta <GTA V folder> --fortnite <Fortnite folder> --out <cache dir>
               [--only halo|gta|fortnite] [--config <crafttheft.properties>] [--check]
```
- `--config` reads `halo_dir`, `gta_dir`, `fortnite_dir`, `cache_dir` from the properties file (UTF-8).
- Game folders may be the store root or a subfolder; each module normalises (MCC: folder containing `halo3/maps`;
  GTA: folder containing `GTA5.exe` or `GTA5_Enhanced.exe`; Fortnite: folder containing `FortniteGame/Content/Paks`).
- Exit codes: 0 all ok, 1 partial, 2 nothing extracted, 3 bad arguments, 4 another extractor holds the lock, 5 watchdog timeout.
- `manifest.json` is **always** written last (atomic temp+move), starting with `{"cacheSchema":1,`.

## Cache layout (`%LOCALAPPDATA%/CraftTheftHalo/cache`)

```
manifest.json      {"cacheSchema":1,"extractor":"0.1.0","status":"ok|partial|failed","finishedUtc":..,
                    "games":{"halo":{"status":..,"dir":..,"error":..,"fingerprint":[{path,size,mtimeMs}]},..},
                    "assets":{"<sources id>":{"status":"ok|missing|failed|unimplemented","detail":..,"out":..}}}
progress.json      {"game":..,"step":..,"done":n,"total":n} while running
extract.log        plain text log
stats/halo_weapons.json   {"<weapon id>": {fields read from the game, same names as the weapons sheet columns}}
stats/halo_shield.json    {"shield": {"max":.., "rechargeDelayTicks":.., "rechargePerTick":..}}
packs/<halo|gta|fortnite>/pack.mcmeta            pack_format 34
packs/<..>/assets/crafttheft/meshes/...cthm      sources.out paths
packs/<..>/assets/crafttheft/textures/...png     icons (sources.out) and mesh material textures
packs/<..>/assets/crafttheft/sounds/...ogg       Ogg Vorbis
packs/<..>/assets/crafttheft/sounds.json         events "<game short>.<name>" with "replace": true
```
Each game's pack is built in a staging folder and swapped in only when that game finishes, so a failed or
`--only` run never touches the other games. The mod registers every `packs/*` folder as a required,
top-priority client resource pack; the jar ships original placeholder art and placeholder sound events at the same
paths, so missing content falls back silently.

## Mesh file `.cthm` v1 (little-endian)

```
"CTHM" u32 version=1
u32 nodeCount  { str name; f32 x,y,z }            pivots (wheels: wheel_lf, wheel_rf, wheel_lb, wheel_rb)
u32 subCount   { str material; i32 node; u32 vertexCount; u32 indexCount;
                 f32[vertexCount*8] (px py pz nx ny nz u v); u32[indexCount] }
str = u16 byteLength + UTF-8 bytes
```
- Minecraft space: metres, +Y up, forward +Z, right-handed. UV origin top-left (no V flip).
- `material` = texture path under `assets/crafttheft/textures` without `.png`.
- Vehicles and the bus: centred in X/Z, lowest point at y = 0, front towards +Z.
- Weapons: origin at the grip (Halo `right_hand` marker when present, else bounding-box centre), barrel towards +Z.
- Glider: origin at the attach point (player's back), wing span along X.
- Triangle budgets come from `sources.maxTris`.

## Sound events

`crafttheft:<game short>.<file>`, e.g. `crafttheft:halo.assault_rifle_fire`, `crafttheft:gta.siren`,
`crafttheft:fortnite.drink`. The jar's `sounds.json` maps each to the sheet's placeholder vanilla sound.

## Settings Melty writes before every Play

`%LOCALAPPDATA%/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/config/crafttheft.properties`:
`halo_dir`, `gta_dir`, `fortnite_dir`, `cache_dir`, `extractor`.
