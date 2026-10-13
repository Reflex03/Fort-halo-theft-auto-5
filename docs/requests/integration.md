# Integration: what was applied, and the CONTRACTS.md text still to apply (agent key: integration)

## 1. Applied to the sheets (then `python3 tools/preflight.py`: 0 errors, `python3 tools/gen.py`)

Each request was checked against the code before applying it (the code already behaved this way).

| Request | Sheet change | Checked |
|---|---|---|
| gta.md #3 | `sources.gta_snd_siren` locator/fallback name the identified streams (`siren_pa20a_wail` 0x0D329446, `siren_firetruck_wail_01` 0x159C9182, `siren_firetruck_quick_01` 0x161768E7) and the `siren_*` fallback; `_unverified` kept | GTA code does not parse the locator (ids are in code; `Sheet_siren_ids_are_these_names` test) |
| fortnite.md #6 | `sources.fn_mesh_glider` locator "... -> SkeletalMesh (+ GliderMeshTransform), most detailed LOD within maxTris"; `fn_snd_drink`, `fn_snd_heal_use`, `fn_snd_chest_open` locators as requested; `_unverified` kept | `RecipeBook` reads names only from `named A / B ->` (glider still parses to `[Glider_Default, Umbrella_Default]`, `RecipeBookTests` pass) and regexes only from sound **fallbacks** (unchanged) |
| mod.md #2 | `hooks.test_mode` (event/handler/purpose), `hooks.chunk_generate` event `ServerChunkEvents.CHUNK_GENERATE`, `hooks.server_started` purpose, `hooks.client_started` handler; new rows `player_respawn`, `bus_jump`, `glider_layer`, `hide_shield_hearts` | every row matches a `Hooks.mark(...)` in `fabric/src`; `CHUNK_GENERATE` and `ServerPlayerEvents.AFTER_RESPAWN` exist in Fabric API tag `0.116.17+1.21.1` (source-inspection); the harness now reports every hooks row registered and no "extra hook" |

Generated files: `extractor/src/CthExtract.Core/Generated/Sources.g.cs` changed (locator text); `Sheets.java` is
unchanged (it skips locator/fallback and has no hooks).

Not applied, for the coordinator:
- gta.md #9 asks for "a sheet note" that GTA V Enhanced (gen9) is not supported: `sources` has no notes column. Proposed
  instead in CONTRACTS (section 2 below, "Game folders").
- packaging.md 1.2 (third, non-installed `sources` component in `melty.json`): a release decision, left as is
  (`melty.json` keeps the two components the brief asks for).
- mod.md #1: `melty.json` `test.checks` already lists exactly the 12 names the mod reports. Nothing to change.

## 2. `docs/CONTRACTS.md`: replace the file with this text

This merges every CONTRACTS request (halo.md #3, gta.md #2/#4/#5, fortnite.md #3/#4/#5, extractor.md #1-#5,
mod.md #3, packaging.md 1.1) into the existing text. Nothing in it changes a file format; the extractor, the mod and
the packaging already implement all of it, and `extractor/tests/CthExtract.E2E/CacheContract.cs` checks a real
extractor cache against it (section 3).

````markdown
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
````

## 3. How the layout above was checked

`extractor/tests/e2e.sh` (and `CthExtract.Cli.Tests.RealCliEndToEndTests`) runs the real extractor (Linux build,
`dotnet run`, real modules, default options) on fake game folders built from the modules' synthetic data, then
`CthExtract.E2E verify` checks the cache against the text above: top-level entries, the manifest prefix and fields,
every sources row once with consistent game/top statuses, `out` only for ok rows, a `detail` on every non-ok row,
progress finished, `pack.mcmeta` pack_format 34, `.cthm` v1 / PNG / Ogg Vorbis per ok row at its `sources.out`, vehicle
placement (centred, floor 0, wheel nodes), `sounds.json` = exactly the ok sound rows with `replace:true`, stats keys
against the weapons sheet, and no game-format or binary file anywhere in the cache. Result: PASS.

## 4. Changes made during integration (outside the sheets)

- `extractor/src/CthExtract.Core/OggVorbis.cs` (new): one shared PCM -> Ogg Vorbis encoder (moved from
  `CthExtract.Gta/Audio/OggEncode.cs`, generalised). Measured here: OggVorbisEncoder 1.2.2 drops the first
  BlockSizes[1]/2 input samples (1024 at 32-48 kHz q0.1, 2048 at q-0.1), and throws for stereo at 32-48 kHz. The Halo
  (PCM-bank fallback) and Fortnite encoders did not pad the start, so they cut the attack of every sound they encoded
  and shifted it ~23 ms early; Halo's stereo PCM path would have failed. All three modules now use the shared encoder
  (head padding, attempt ladder, NVorbis decode + lag-0 correlation check). Tests: `CthExtract.Cli.Tests/OggVorbisTests.cs`.
- `extractor/src/CthExtract.Fortnite/FortniteModule.cs`: when no `.usmap` mapping is usable, each non-ok row's detail
  now says so (the recipes' own "not found" was misleading: nothing unversioned can be read without a mapping).
- `extractor/tests/CthExtract.E2E/` (new, test-only), `extractor/tests/e2e.sh` (new),
  `extractor/tests/CthExtract.Cli.Tests/RealCliEndToEndTests.cs` (new), `SynthGame.Options.CommonEnc/UpdateEnc` (new
  options, defaults unchanged).
- `fabric/tools/CthSynth/RealCache.cs` (new) + `CTH_SYNTH_REAL_CACHE`: the mod harness's cache takes every row the real
  extractor produced in the e2e run; `fabric/scripts/run_harness.sh` uses `extractor/bin/e2e/cache` when present.
- `extractor/vendor/Reclaimer`: `vendor.py` now also strips the three binary Xbox P8 bump palettes
  (`Halo1/Halo2/StubbsBumpPalette.bin`, 1 KiB lookup tables of unclear, probably game-derived provenance; Halo 1/2 only),
  their resx entries/accessors, and makes the two P8-bump decode sites throw `NotSupportedException`. Re-running
  `vendor.py` against upstream `36224d7a` reproduces the folder exactly (`diff -r`). `VendorTests` now fails on any
  vendored `.bin`.
- Root `.gitignore`: `cache/` also matched the mod's source package `fabric/src/client/java/dev/crafttheft/client/cache/`
  (CacheGuard, CachePacks, ExtractProgressScreen would never have been committed); re-included with `!**/src/**/cache/`.
  Added game-format patterns (`*.awc *.yft *.ytd *.ydr *.uasset *.uexp *.ubulk *.bk2 *.usmap magic.dat`).

## 5. Open, for the coordinator

- `extractor/vendor/CUE4Parse/CUE4Parse/GameTypes/*/Encryption` (75 files) is upstream Apache-2.0 decryption code for
  other games, some with constant keys/tables in the source (e.g. `ProSpi/Encryption/Aes/DescriptorTable.cs`, 206 KB).
  Fortnite never uses it. The Fortnite agent left it and recorded that in VENDORED.md; stripping it needs patching about
  ten core CUE4Parse files that dispatch to it (`PakFileReader`, `FPakInfo`, `FPakEntry`, `IoStoreReader`, `Package`,
  `FText`, `FTextLocalizationResource`, `FStringTable`, `CriWareAwbDecryption`, `FTheiaArchive`). Decide whether the
  "no keys in the repo" rule covers a library's own source; if yes, that is the change to make in `cthextract.patch`.
  **Resolved (licence/safety review, 2026-10-13):** the code stays, the keys go. `vendor.py` step 4 zeroes every
  hard-coded key, key table, key string and XOR key of the other games (52 files, incl. the FSB5/HCA key lists in doc
  comments and the TOC/pak-footer keys in `FIoStoreTocResource`/`FPakInfo`) *before* `cthextract.patch` is applied,
  so the patch shows no key either; the Reclaimer Reach/H4 AES strings are zeroed by its `vendor.py`. Guarded by
  `VendorTests` (sources, patch and the compiled CUE4Parse assembly). See both `VENDORED.md` files.
- Reclaimer keeps P/Invoke wrappers for `oo2core_8_win64.dll` (Halo Infinite modules) and the stripped `xcompress`
  (Xbox 360 LZX); CUE4Parse keeps wrappers for its natives. None is shipped, downloaded or reached on our paths
  (the publish bundle has 0 native files); a DLL of that name would only load if it already sat next to the exe.
- `CraftTheftHalo-<ver>-sources.zip` was not built: `--sources` archives the committed tree and HEAD (`c2f7084`) does not
  contain this work yet. After committing: `python3 packaging/build_package.py --sources --release`.
