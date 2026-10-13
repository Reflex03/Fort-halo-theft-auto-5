# Craft Theft Halo: the Fabric mod (`crafttheft`)

Minecraft 1.21.1, Fabric Loader 0.19.5, Fabric API 0.116.17+1.21.1, Loom 1.17.21, Mojang mappings, Java 21.
Everything gameplay-related is driven by the generated `src/main/java/dev/crafttheft/gen/Sheets.java`
(`python3 tools/gen.py` from the repo root; never edit it by hand).

## Build

```
./gradlew build            # -> build/libs/crafttheft-<version>.jar (remapped release jar) + unit tests
```
If Maven Central answers HTTP 429, add `--init-script scripts/mirror.init.gradle`.
The build generates, from the sheets, the jar's `sounds.json`, item models, `lang/en_us.json`, and original
placeholder meshes/art at every sources row's `out` path (`src/tools`, task `genSheetResources`).

## Layout

| Folder | What |
|---|---|
| `src/main` | common code: items/entities/blocks per sheet row, weapons, Halo shield, crimes + wanted + police, Battle Bus + skydive, loot chests + world setup, Melty test mode |
| `src/client` | cache packs (`CachePacks` + `PackRepositoryMixin`), `CacheGuard` + progress screen, `FirstRun`, mesh loading/drawing, HUD, input, camera/zoom mixins |
| `src/tools` | build-time generator (no Minecraft classes) |
| `src/harness` | scripted end-to-end test mod `crafttheft_harness` (never shipped) |
| `src/test` | unit tests (mesh format, manifest/config parsing, sound-event naming) |
| `tools/CthSynth` | .NET test tool: writes a **synthetic** cache with the extractor's own Core writers and acts as a stand-in extractor |
| `scripts/` | `run_harness.sh`, `fake-extractor.sh`, `mirror.init.gradle` |

## Test

```
scripts/run_harness.sh all dev        # dev client: first-start extraction (synthetic), whole game, ~6 min
scripts/run_harness.sh all prod       # same with the remapped release jar in a production-style launch
scripts/run_harness.sh nocache dev    # no cache, no extractor: placeholders only (also: nocache prod)
scripts/run_server_smoke.sh           # production-style dedicated server with the release jar: starts, ticks, no errors
```
Screenshots, the client log, a summary and Melty's checks file land in `run-shots/<mode>-<launch>/`.
Hooks: every row of `sheets/hooks.json` calls `Hooks.mark(id, handler)` when it registers; the harness compares
them with the sheet.
