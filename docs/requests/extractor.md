# Requests from the extractor executable (agent key: extractor)

`extractor/src/CthExtract` (cthextract.exe), `extractor/tests/CthExtract.Cli.Tests`, `extractor/build.sh` and the Core
helpers are coded against everything below already. Nothing here blocks the modules; it is what the mod (CacheGuard,
progress screen) and packaging should rely on. Please fold it into `docs/CONTRACTS.md`.

## 1. Command line (add to "Extractor command line")

```
cthextract.exe --halo <MCC> --gta <GTA V> --fortnite <Fortnite> --out <cache dir>
               [--only halo,gta,fortnite] [--config <crafttheft.properties>] [--check] [--version] [--help]
               [--timeout-sec 1500] [--game-timeout-sec 900] [--grace-sec 20] [--lock-wait-sec 0]
               [--in-process] [--no-keep] [--offline] [--console]
```

- Every option also accepts `--name=value`. `--only` takes a comma list and may repeat (`halo-mcc`/`gta-v` accepted).
- Command-line values win over `--config` values. `--config` also reads `cache_dir`; `extractor` is ignored.
  Properties are read like `java.util.Properties.load(Reader)` in UTF-8 (escapes, `\uXXXX`, continuations, BOM).
- An empty value or an unexpanded Melty placeholder (`{game:...}`, `{localappdata}`) counts as "not given"; the game
  is then recorded as missing with "no <game> folder given", not as a bad argument.
- No `--out` and no `cache_dir`: `%LOCALAPPDATA%/CraftTheftHalo/cache`.
- A cache folder inside a game folder is refused (exit 3): the extractor never writes into game folders.
- `--timeout-sec`: whole-run watchdog (exit 5). `--game-timeout-sec`: per game; a game over it is cancelled, then
  killed after `--grace-sec`, and the run continues with the next game (that game is failed/partial, exit 1 or 2,
  not 5). Each game's limit is also capped by the whole-run deadline.
- `--in-process`: run modules on threads instead of worker processes (debugging). `--no-keep`: do not carry the
  previous run's good files over (section 4). `--offline`: Fortnite uses cached keys/mappings only.
  `--console`: attach to the calling console (the exe is GUI-subsystem, so it is silent otherwise).
- Hidden: `--worker <game> --worker-game-dir D --worker-stage S --worker-cache C` is the child-process mode (section 6).

## 2. Exit codes (replace the line in CONTRACTS.md)

| code | meaning |
|---|---|
| 0 | every selected game is ok |
| 1 | partial: something new was extracted, but not every selected game is fully ok |
| 2 | nothing new extracted by this run (no game found, all failed, or every ok row was kept from the previous run) |
| 3 | bad arguments (a "failed" manifest is written only if the named cache has none yet, so Melty is never blocked) |
| 4 | another extractor holds `cache/.lock` (nothing in the cache is touched) |
| 5 | the whole-run watchdog fired (manifest written with `"run":{"watchdog":true}`) |

`--check` uses its own codes (section 5). The mod should call `reloadResources()` after exit 0, 1 or 5.

## 3. Cache layout additions

```
manifest.json          see section 4
progress.json          {"game":"halo|gta|fortnite|null","step":"<text>","done":n,"total":n,"finished":bool,"pid":n,"updatedUtc":..}
                       done/total count sources rows over the whole run; throttled to ~5 writes/s; last write has
                       "finished":true and is followed only by manifest.json
extract.log            this run; the previous run's log is moved to extract.previous.log at start
check.json, check.log  output of the last --check
.lock, .lock.info      the single-extractor lock (an OS lock held open; released if the process dies) + holder pid
.staging/<game>-<pid>-<ticks>/{pack,stats,work}/   per-game staging; removed after each game and at every start
packs/.old-<game>-<pid>/   transient during a swap; an interrupted swap is rolled back at the next start
keys/                  module key caches (GTA: keys/gta5_aes_key.json; Fortnite: keys/fortnite/...), never swapped
```

## 4. manifest.json (replace the manifest line)

Compact JSON, always written last (temp file + rename, retried; direct write as a last resort), always starts with
`{"cacheSchema":1,"extractor":"0.1.0","status":`. Always lists **all three games and every sources row**.

```
{"cacheSchema":1,"extractor":"0.1.0","status":"ok|partial|failed","finishedUtc":"...",
 "games":{"halo":{"status":"ok|partial|missing|failed","dir":"C:/.../Halo The Master Chief Collection","error":null|"...",
                  "fingerprint":[{"path":"halo3/maps/shared.map","size":123,"mtimeMs":456}],
                  "startedUtc":"...","elapsedMs":n,"mode":"process|in-process|none","interrupted":bool,
                  "interruptedRuns":n,"keptPrevious":bool,"counts":{"ok":n,"missing":n,"failed":n,"unimplemented":n,"kept":n}},
          "gta":{..},"fortnite":{..}},
 "assets":{"<sources id>":{"status":"ok|missing|failed|unimplemented","detail":"...","out":"<sources.out>|null","game":"halo","kept":true?}},
 "run":{"exitCode":n,"watchdog":bool,"games":["halo",..],"mode":"process","keep":true,"startedUtc":..,"elapsedMs":n,"pid":n,"build":"0.1.0+<git sha>"}}
```

- Top `status`: ok = all three games ok; partial = at least one row ok anywhere; failed = no row ok.
- Game `status`: ok = every row ok; partial = some ok; missing = none ok and every row missing (game not
  installed / no folder); failed = none ok and something broke. `error` is null when ok, else the attempt's error
  (crash, timeout, "no folder given", "folder not found") or a summary of the rows that are not ok.
- `fingerprint`: the module's `FingerprintFiles`, stat'ed **before** extraction; a listed file that does not exist
  is `{"size":-1,"mtimeMs":0}` (so it appearing later counts as a change). Paths use `/`.
- `out` is set only for ok rows. `"kept":true` marks a row whose file comes from an earlier run.
- `interrupted` = the attempt was cut short (worker crash, timeout, abandoned thread, watchdog);
  `interruptedRuns` counts consecutive interrupted attempts.
- Games not selected by `--only` keep their previous entries and rows byte-for-byte, and their packs/stats are not
  touched.

**Keep policy (never downgrade).** After a game runs, every row that is not ok this time but was ok in the previous
manifest, and whose previous file still passes the output check, is copied into the new pack (meshes with their
material textures, sounds with their sounds.json entry, stats files) and recorded ok with `kept:true` and a detail
naming both runs. When a game does not run at all (no folder given, folder not found, abandoned, swap failed) its
previous pack and manifest rows stay as they were (`keptPrevious:true`). `--no-keep` turns the per-row copy off.

**Output check.** Before a pack is swapped in, each row reported ok is verified: `.cthm` parses, passes
`Validate.Mesh`, has triangles and is within `maxTris` (missing material textures are a warning in `detail`);
`.png` has a PNG signature and a sane size; `.ogg` passes `Ogg.LooksLikeVorbis`; stats `.json` is a non-empty
object. A row that fails is recorded failed ("output check failed: ...") and its file removed. sounds.json lists
exactly the ok sound rows; pack.mcmeta is always present.

## 5. `--check` (new section)

Reads manifest.json and stats each game's fingerprint files; never extracts, never holds the lock for more than a
moment. Prints one JSON line (also `cache/check.json`):

```
{"cacheSchema":1,"exe":"0.1.0","running":bool,"manifest":{"state":"ok|missing","status":..,"extractor":..,"problem":..},
 "games":{"halo":{"state":"fresh|updated|unchanged|changed|never|moved|extractor-updated|interrupted|no-folder","detail":"...","status":".."}},
 "rerun":["gta"],"updated":["fortnite"],"advice":"fresh|full|rerun|updated","exitCode":n}
```

| exit | advice | mod action (CacheGuard) |
|---|---|---|
| 0 | fresh | nothing |
| 10 | full | no usable manifest (missing, unreadable, other cacheSchema): blocking "Reading your games..." run with `--config` |
| 11 | rerun | background `--config <props> --only <rerun list>`: never extracted, folder moved (`--halo`/config now differs), extractor version changed, a non-ok game whose fingerprint changed, or an interrupted attempt (retried at most 3 times in a row) |
| 12 | updated | only ok games whose fingerprint changed: optional refresh (`refresh_on_game_update`) |

`running:true` means an extractor holds the lock right now: do not start another (it would exit 4).

## 6. Process model (informational)

The supervisor runs each game in a child process (the same exe with the hidden `--worker` option), one at a time.
Results stream back over stdout as they are recorded, so a fatal crash (access violation in unsafe decoders, stack
overflow, FailFast) or a kill keeps every row reported before it, and the manifest is always written. On a time limit
the supervisor writes `cancel` to the worker's stdin, waits `--grace-sec`, then kills the worker's process tree. A
worker exits on its own if the supervisor disappears (stdin closes). If no worker can start (no "hello" within 60 s)
the run falls back to in-process threads for the rest of that run. A last-resort timer writes the manifest and exits 5
if the supervisor itself is stuck 90 s past the deadline plus grace.

## 7. Core API additions (already in `extractor/src/CthExtract.Core`; additive, existing signatures unchanged)

- `ExtractContext.CacheDir` (answers the GTA and Fortnite requests #1/#2). The extractor also passes explicit
  `GtaOptions.KeysDir = cache/keys` and `FortniteOptions.KeysDir = cache/keys/fortnite`, so keys survive the staging swap.
- `ExtractContext.ResultRecorded` (callback per recorded row), `ExtractContext.SoundJson(row)`,
  `ExtractContext.WritePackMeta(packDir, short)`.
- `Decimate.ToBudget(mesh, maxTris, out method)`: quadric edge collapse (vendored MeshDecimator) per pivot node with a
  vertex-clustering fallback; `ExtractContext.Mesh` details now read `"N tris (from M, quadric)"`.
- `Cache.cs`: `CacheLayout`, `Manifest`/`ManifestGame`/`ManifestAsset`, `ProgressFile`, `CacheLock`, `Fingerprint`,
  `SafeFs`, `JsonRead`, `StatusNames`.
- `Log.Raw(line)`.

## 8. Third-party notices (packaging)

- MeshDecimator (Whinarn), commit `a1e85d43`, **MIT**, vendored unmodified in `extractor/vendor/MeshDecimator`
  (ship its `LICENSE.md`; includes Sven Forstmann's MIT Fast-Quadric-Mesh-Simplification notice in the source).
- Test-only (not shipped): xunit.v3 3.2.2, xunit.runner.visualstudio 3.1.5 (Apache-2.0), Microsoft.NET.Test.Sdk 18.6.0 (MIT).

## 9. Not verifiable here

- Windows behaviour of the GUI-subsystem exe: stdout/stdin pipes between supervisor and worker (falls back to
  in-process if the worker never says hello), `AttachConsole` for `--console`, directory rename with files open
  (falls back to copying file by file), long paths. All logic was exercised on Linux with the same code paths.
- Real game data: the real modules were run only on empty folders (every row missing, as expected).
