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
               [--only halo,gta,fortnite] [--config <crafttheft.properties>] [--check] [--version] [--help]
               [--timeout-sec 1500] [--game-timeout-sec 900] [--grace-sec 20] [--lock-wait-sec 0]
               [--in-process] [--no-keep] [--offline] [--console]
```
- Every option also accepts `--name=value`. `--only` takes a comma list and may repeat (`halo-mcc`/`gta-v` accepted).
- `--config` reads `halo_dir`, `gta_dir`, `fortnite_dir`, `cache_dir` from the properties file (UTF-8, read like
  `java.util.Properties.load(Reader)`: escapes, `\uXXXX`, continuations, BOM); `extractor` is ignored. Command-line
  values win over `--config` values.
- Game folders may be the store root or a subfolder; each module normalises (MCC: folder containing `halo3/maps`;
  GTA: folder containing `GTA5.exe` or `GTA5_Enhanced.exe`, Legacy preferred when both exist; Fortnite: folder
  containing `FortniteGame/Content/Paks`). GTA V Enhanced (gen9 resources) is found but not parsed: its car rows are
  reported failed and the star missing, with that reason.
- An empty value or an unexpanded Melty placeholder (`{game:...}`, `{localappdata}`) counts as not given: that game is
  recorded missing with "no <game> folder given" (not a bad argument).
- No `--out` and no `cache_dir`: `%LOCALAPPDATA%/CraftTheftHalo/cache`. A cache folder inside a game folder is refused
  (exit 3): the extractor never writes into game folders.
- `--timeout-sec`: whole-run watchdog (exit 5). `--game-timeout-sec`: per game; a game over it is cancelled, killed
  after `--grace-sec`, and the run continues with the next game (that game is failed/partial). `--in-process`: modules
  on threads instead of one worker process per game (debugging). `--no-keep`: do not carry the previous run's good
  files over. `--offline`: Fortnite uses cached keys/mappings only. `--console`: attach to the calling console (the exe
  is GUI-subsystem and silent otherwise).
- Exit codes:

| code | meaning |
|---|---|
| 0 | every selected game is ok |
| 1 | partial: something new was extracted, but not every selected game is fully ok |
| 2 | nothing new extracted by this run (no game found, all failed, or every ok row was kept from the previous run) |
| 3 | bad arguments (a "failed" manifest is written only if the named cache has none yet, so Melty is never blocked) |
| 4 | another extractor holds `cache/.lock` (nothing in the cache is touched) |
| 5 | the whole-run watchdog fired (manifest written with `"run":{"watchdog":true}`) |

- `manifest.json` is **always** written last (atomic temp+move), starting with `{"cacheSchema":1,"extractor":"0.1.0","status":`.
- Network at extraction time: Fortnite fetches the installed build's AES keys and `.usmap` from
  `api.fortniteporting.app`, `uedb.dev`, `fortnite-api.com` (mappings from `supabase.fortniteporting.app` /
  `data.uedb.dev`) through an allow-list (https only, those hosts only, never a binary path); GTA fetches
  `https://raw.githubusercontent.com/dexyfex/CodeWalker/485d56bec00262ed7fa472261cce7bbc6202b96e/CodeWalker.Core/Resources/magic.dat`
  (154,069 bytes, SHA-256 `dc35981f822e892ced3aa81d31e7a96927d573ee28f67417592b5afeaf330832`) only when an archive it
  reads is NG-encrypted, held in memory only. Without network those rows are missing with that reason.

How the mod runs it (`CacheGuard`):
- No usable manifest: `"<extractor>" --config "<instance>/config/crafttheft.properties"`, working directory = the
  extractor's folder, output appended to `<instance>/logs/crafttheft-extractor.log`; the title screen is replaced by a
  progress screen reading `progress.json` until the process exits ("Play with placeholders" lets the player go on).
- A game whose manifest status is `failed`: `--config <props> --only <game>` in the background, one game per process,
  at most 3 consecutive retries per game (`config/crafttheft-guard.properties`).
- After exit 0, 1, 2 or 5 the mod reloads resource packs. Exit 4: it waits for `progress.json` `"finished":true`.
- `extractor` may be relative to the instance folder (`.minecraft`).

## `--check`

Reads `manifest.json` and stats each game's fingerprint files; never extracts. Prints one JSON line (also `check.json`):
`{"cacheSchema":1,"exe":"0.1.0","running":bool,"manifest":{"state":"ok|missing",..},"games":{"halo":{"state":"fresh|updated|unchanged|changed|never|moved|extractor-updated|interrupted|no-folder","detail":..,"status":..}},"rerun":[..],"updated":[..],"advice":"fresh|full|rerun|updated","exitCode":n}`.

| exit | advice | mod action |
|---|---|---|
| 0 | fresh | nothing |
| 10 | full | no usable manifest: blocking run with `--config` |
| 11 | rerun | background `--config <props> --only <rerun list>` (never extracted, folder moved, extractor version changed, a non-ok game whose fingerprint changed, an interrupted attempt; at most 3 in a row) |
| 12 | updated | only ok games whose fingerprint changed: optional refresh |

`running:true` means an extractor holds the lock right now: do not start another.

## Cache layout (`%LOCALAPPDATA%/CraftTheftHalo/cache`)

```
manifest.json      see below
progress.json      {"game":"halo|gta|fortnite|null","step":..,"done":n,"total":n,"finished":bool,"pid":n,"updatedUtc":..}
                   while running (rows over the whole run, ~5 writes/s); the last write has "finished":true
extract.log        plain text log of this run; the previous run's is extract.previous.log
check.json, check.log   output of the last --check
.lock, .lock.info  the single-extractor lock (an OS lock held open) + holder pid
.staging/<game>-<pid>-<ticks>/{pack,stats,work}/   per-game staging, removed after each game and at every start
packs/.old-<game>-<pid>/   transient during a swap (rolled back at the next start if interrupted)
keys/gta5_aes_key.json      the player's own GTA V AES key found in their GTA5.exe (base64 + exe size/mtime), re-verified
                            by SHA1 on load; NG keys/tables are never written anywhere
keys/fortnite/<build>_<host>.json   AES main + dynamic keys and the mapping file name for one Fortnite build
keys/fortnite/*.usmap               the matching type mappings (md5-checked when the service gives one)
stats/halo_weapons.json   {"<weapon id>": {fields read from the game, same names as the weapons sheet columns}}
stats/halo_shield.json    {"shield": {"max":.., "rechargeDelayTicks":.., "rechargePerTick":..}}
packs/<halo|gta|fortnite>/pack.mcmeta            pack_format 34
packs/<..>/assets/crafttheft/meshes/...cthm      sources.out paths
packs/<..>/assets/crafttheft/textures/...png     icons (sources.out) and mesh material textures
packs/<..>/assets/crafttheft/sounds/...ogg       Ogg Vorbis
packs/<..>/assets/crafttheft/sounds.json         events "<game short>.<name>" with "replace": true
```
`keys/` is local only, never shipped, and is not swapped with the packs.

Each game's pack is built in a staging folder and swapped in only when that game finishes, so a failed or `--only`
run never touches the other games (their packs, stats and manifest entries stay byte-for-byte).

`manifest.json` (compact JSON, always lists all three games and every sources row):
```
{"cacheSchema":1,"extractor":"0.1.0","status":"ok|partial|failed","finishedUtc":"...",
 "games":{"halo":{"status":"ok|partial|missing|failed","dir":"...","error":null|"...",
                  "fingerprint":[{"path":"halo3/maps/shared.map","size":123,"mtimeMs":456}],
                  "startedUtc":"...","elapsedMs":n,"mode":"process|in-process|none","interrupted":bool,
                  "interruptedRuns":n,"keptPrevious":bool,"counts":{"ok":n,"missing":n,"failed":n,"unimplemented":n,"kept":n}},
          "gta":{..},"fortnite":{..}},
 "assets":{"<sources id>":{"status":"ok|missing|failed|unimplemented","detail":"...","out":"<sources.out>|null","game":"halo","kept":true?}},
 "run":{"exitCode":n,"watchdog":bool,"games":[..],"mode":"process","keep":true,"startedUtc":..,"elapsedMs":n,"pid":n,"build":"0.1.0+<git sha>"}}
```
- Top `status`: ok = all three games ok; partial = at least one row ok anywhere; failed = no row ok.
- Game `status`: ok = every row ok; partial = some ok; missing = none ok and every row missing; failed = none ok and
  something broke. `error` is null when ok, else the attempt's error or a summary of the rows that are not ok.
- `fingerprint`: the module's files, stat'ed before extraction (`/` separators); a file that does not exist is
  `{"size":-1,"mtimeMs":0}`.
- `out` is set only for ok rows; every row that is not ok has a `detail` saying what was tried.
- Keep policy (never downgrade): a row that is not ok this time but was ok before, and whose previous file still passes
  the output check, is copied into the new pack and recorded ok with `"kept":true`. A game that does not run at all keeps
  its previous pack and rows (`keptPrevious:true`). `--no-keep` turns the per-row copy off.
- Output check before a pack is swapped in: `.cthm` parses, validates, has triangles and fits `maxTris`; `.png` has a
  PNG signature and a sane size; `.ogg` is Ogg Vorbis; stats `.json` is a non-empty object. A row that fails is
  recorded failed ("output check failed: ...") and its file removed. `sounds.json` lists exactly the ok sound rows;
  `pack.mcmeta` is always present.

Stats files:
- `stats/halo_weapons.json`: keys per weapon are a subset of the weapons sheet columns, in the sheet's units: damage
  (Halo points per bullet/pellet/impact; the mod applies `systems.combat.haloPointToMc`), fireIntervalTicks, burst,
  pellets, clip, reloadTicks, auto, spreadDeg, projectileSpeed (blocks/tick, non-hitscan only), homing (projectile
  only), blastRadius (blocks, rocket/grenade only), zoom (FOV multiplier). Missing keys = keep the sheet value.
  Conversions: `extractor/src/CthExtract.Halo/Stats.cs`.
- `stats/halo_shield.json` `"max"` is the Fortnite-scale bar (= `systems.shield.max`, 100); Halo supplies the timing:
  rechargeDelayTicks = shield stun time × 20, rechargePerTick = max / (full recharge time × 20).
- The mod range-checks every value (e.g. `zoom` 0.05..1, `clip` 1..1000); an out-of-range value keeps the sheet value
  (logged `[stats]`).

How the mod uses the cache:
- It mounts every folder `packs/<name>/` with a `pack.mcmeta` as a required, top-priority client pack
  `crafttheft_cache_<name>` (folders starting with `.` are skipped); packs are re-scanned on every resource reload.
- Mesh materials resolve to `assets/crafttheft/textures/<material>.png` in any pack; a missing material texture is
  drawn with `textures/placeholder/generic.png` and logged. Textures are sampled with repeat (UVs may lie outside 0..1).
- The jar ships original placeholder meshes at every mesh row's `out` (except `fn_mesh_chest`, drawn as the vanilla
  chest) and placeholder icons at every icon row's `out`, generated from the sheets at build time; its `sounds.json`
  maps each sound event to the row's placeholder vanilla sound event. The cache packs' entries (`"replace": true`) win,
  so missing content falls back silently.
- The stats files are found at the stats rows' `out` paths under the cache root (`sources.halo_stats_weapons.out`,
  `sources.halo_stats_shield.out`), not at hard-coded names.
- Halo rounds that are entities (plasma bolts, needles, rockets) fly at the weapons sheet's `projectileSpeed`
  (blocks/tick) without vanilla's 0.99 per-tick drag, for `range / projectileSpeed` ticks, so they reach `range`
  (rockets reached about 77% of it before). Water still slows them. Grenades keep vanilla gravity and drag; their
  `range` column is not used (the throw distance follows from `projectileSpeed` and gravity, about 24 blocks at 45°).
- Hitscan rays stop at the edge of the loaded chunks (a 192-block sniper ray never makes the server load or generate
  chunks on its thread).
- Rocket and grenade blasts damage GTA cars as well as living things (`vehicles.health`), so explosives can destroy a
  police car and count as `assault_police`.
- A dispatched police car remembers whom it chases across save/load (`DispatchedFor`), consistent with the wanted
  level, which is a persistent player attachment. Only dispatched police cars (not a parked or stolen one, not the car
  the player drives) count as "police sight" for `wanted.loseAfterTicks`. Damaging a police car the player stole and
  owns is not `assault_police`.
- Leaving the game while riding the Battle Bus is not a drop: vanilla saves the player with the bus as `RootVehicle`
  and re-seats them on rejoin; if the bus cannot be restored, the player boards a new bus (they are not marked as
  dropped, so `player_join` boards them again instead of leaving them at the bus altitude without a glider).

## Mesh file `.cthm` v1 (little-endian)

```
"CTHM" u32 version=1
u32 nodeCount  { str name; f32 x,y,z }            pivots (wheels: wheel_lf, wheel_rf, wheel_lb, wheel_rb)
u32 subCount   { str material; i32 node; u32 vertexCount; u32 indexCount;
                 f32[vertexCount*8] (px py pz nx ny nz u v); u32[indexCount] }
str = u16 byteLength + UTF-8 bytes
```
- Minecraft space: metres, +Y up, forward +Z, right-handed; left = +X when facing +Z. UV origin top-left (no V flip).
  UVs may lie outside 0..1 (GTA and Unreal tile textures): sample with repeat.
- `material` = texture path under `assets/crafttheft/textures` without `.png`.
- Triangle budgets come from `sources.maxTris` (the most detailed game LOD that fits; only when none fits is a mesh
  decimated by the extractor).
- Vehicles and the bus: centred in X/Z, lowest point at y = 0, front towards +Z.
- GTA: mc = (-x, z, y). Wheels map to nodes `wheel_lf`, `wheel_rf`, `wheel_lb` (GTA `wheel_lr`), `wheel_rb` (GTA
  `wheel_rr`); six-wheelers also get `wheel_lm1..3` / `wheel_rm1..3`. Each wheel's submeshes use its node; the pivot is
  the wheel centre; right-side wheels are the left wheel rotated 180° about the forward axis (winding and normals stay
  valid). Glass is opaque (dark tint); decals/badges keep their alpha; paint shaders are a baked default colour (police:
  white primary, black secondary). Material textures: `textures/gta/<car>/<name>.png`.
- Fortnite: (x, y, z) = (-UE.Y, UE.Z, UE.X) / 100, UE index order kept; skeletal meshes in bind pose. Bus: if its long
  axis comes out along X it is turned 90° (which end is the front is unverified). Chest: true size, UE forward → +Z.
  Glider: origin at the attach point (player's back) after the item's `GliderMeshTransform`, wing span along X.
  Material textures `textures/fortnite/<battle_bus|glider|chest>/<n>.png`, diffuse only, alpha opaque; an unreadable
  diffuse gets a 4×4 flat grey texture.
- Weapons: origin at the grip (Halo `right_hand` marker when present, else the handle node, else the bounding-box
  centre), barrel towards +Z. True scale (1 Halo world unit = 3.048 m) unless that falls outside the held size, then
  scaled uniformly into it: rifles/launchers 0.6–1.2 m, pistols (magnum, needler) 0.3–0.6 m, grenades 0.12–0.3 m along
  +Z; the factor is in the manifest detail ("held scale ×…"). Material textures: `textures/halo/<weapon id>/<n>.png`.
- How the mod draws them: weapons at true size with the grip at the hand (clamped to 0.1–1.4 blocks long if an
  extraction comes out absurd); vehicles/bus at true size; the chest at true size scaled down to fit 1.5 blocks; the
  glider from the player's back.

## Sound events

`crafttheft:<game short>.<file>`, e.g. `crafttheft:halo.assault_rifle_fire`, `crafttheft:gta.siren`,
`crafttheft:fortnite.drink`. The jar's `sounds.json` maps each to the sheet's placeholder vanilla sound.
- Halo sounds are a lossless remux of the game's Vorbis and keep its channel count (stereo plays non-positionally).
  PCM banks, GTA and Fortnite non-Ogg sounds are encoded by the extractor's shared encoder
  (`CthExtract.Core.OggVorbis`: mono where possible, sample-aligned start, decode-verified); such a file may end with
  up to ~20-45 ms of silence (encoder padding), so a looped siren has a tiny gap per loop.
- Fortnite sounds stored as Ogg Vorbis are copied unchanged. RAD Audio (`RADA`) and Opus (`UEOP`) waves cannot be
  decoded with redistributable code: the row is reported missing with that reason and the placeholder stays.

## Settings Melty writes before every Play

`%LOCALAPPDATA%/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/config/crafttheft.properties`:
`halo_dir`, `gta_dir`, `fortnite_dir`, `cache_dir`, `extractor`. The mod reads it as UTF-8 (`Properties.load(Reader)`).

## Release files (packaging/build_package.py)

```
crafttheft-<ver>.jar                 Melty component "main" -> {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods
                                     (copied from fabric/build/libs; -sources/-dev/-harness/... jars are never shipped)
CraftTheftHalo-Minecraft-<ver>.zip   Melty component "minecraft" (extra, required) -> {localappdata}/CraftTheftHalo
  Prism/                             Prism Launcher 11.1.1 portable MSVC, unmodified; + LICENSE/COPYING, prismlauncher_update.cfg
  Prism/instances/CraftTheftHalo/    instance.cfg (no ConfigVersion), mmc-pack.json, .minecraft/mods/fabric-api.jar
  bin/cthextract.exe                 extractor/dist/cthextract.exe
  licenses/, THIRD-PARTY-NOTICES.md, SOURCE.txt, README.txt, bundle-version.txt
CraftTheftHalo-<ver>-sources.zip     GitHub release asset only: corresponding source of every GPL/LGPL binary
```
The zip never contains `cache/`, `accounts.json`, `prismlauncher.cfg`, `options.txt` or `crafttheft.properties`, so an
update never touches the player's cache, sign-in, settings or worlds.
