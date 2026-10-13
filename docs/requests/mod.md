# Requests from the Fabric mod (agent key: mod)

The mod (`fabric/`) is coded against everything below already. Each item says what to change and why.

## 1. Melty recipe: `test.checks`

The mod implements Melty's test mode exactly as specified (`fabric/src/main/java/dev/crafttheft/melty/MeltyTest.java`,
verbatim apart from the package line; slug `craft-theft-halo`). `MeltyChecks` reports each check **once per game
session, at the moment it actually happens in normal play** (no scripted scenario, the game is never quit), and also
logs it as `[melty] check <name>: pass|fail (<why>)`. Put exactly these names in the recipe's `test.checks`:

| name | reported when | pass means | fail means |
|---|---|---|---|
| `mod-loads` | `ModInitializer.onInitialize` (MeltyTest.start) | the mod initialised | (never fails; absent = mod did not load) |
| `cache-ready` | the first time the title screen shows with no blocking extraction left | `manifest.json` is usable (cacheSchema >= 1) **and** at least one `crafttheft_cache_*` pack is mounted | no/unusable manifest, or no pack mounted (the why names the cache folder and the CacheGuard state) |
| `halo-content` | the first time a Halo mesh is drawn (weapon in hand/hotbar/thrown grenade) | that mesh came from `crafttheft_cache_halo` | the jar placeholder was drawn; the why quotes the manifest (game status/error, row status/detail) |
| `gta-content` | the first time a GTA mesh is drawn (parked car, police car) | from `crafttheft_cache_gta` | placeholder, with the manifest's reason |
| `fortnite-content` | the first time a Fortnite mesh is drawn (normally the Battle Bus right after joining) | from `crafttheft_cache_fortnite` | placeholder, with the manifest's reason |
| `bus-ride` | the player has ridden the Battle Bus for 40 server ticks, or drops from it, whichever comes first | rode the bus | - |
| `landed` | the skydive/glide ends on the ground | landed without losing health during the drop | health was lost during the drop |
| `chest-opened` | a supply chest is opened | opened | - |
| `weapon-fired` | the first Halo weapon round is fired (server) | fired | - |
| `car-driven` | a player has driven one car 20 blocks | driven | - |
| `wanted-stars` | a crime first gives the player at least one star | stars set | - |
| `police-chase` | a dispatched police car first comes within 12 blocks of its wanted player | chased | - |

A tester has to play for these to appear: join a new world (bus, landing, fortnite-content), open a chest and fire a
gun (halo-content, weapon-fired), get in a parked car and drive (gta-content, car-driven, wanted-stars), keep the
stars until the police arrive (police-chase). A cold first start also runs the extractor before the title screen.

## 2. `sheets/hooks.json` (please apply; the code already does this)

- `test_mode`: event -> `Melty test file %LOCALAPPDATA%/Melty/test/craft-theft-halo.json (read once in ModInitializer)`,
  handler -> `MeltyChecks.start -> MeltyTest.start; MeltyChecks.report -> MeltyTest.report`,
  purpose -> `Melty test runs: report each check (docs/requests/mod.md) once, when it happens in normal play`.
  (`MeltyTest.tick` does not exist: Melty's spec says to report as checks happen and never to script or quit.)
- `chunk_generate`: event -> `ServerChunkEvents.CHUNK_GENERATE` (present in Fabric API 0.116.17; fires once per newly
  generated chunk, which is what "new chunks" meant; CHUNK_LOAD in 0.116 has no "new" flag).
- `server_started`: purpose -> `plan loot chests + parked cars once per world (SavedData); each is placed when its
  chunk loads (no forced generation at world start); reload weapon/shield stats`.
- `client_started`: handler -> `CacheGuard.onClientStarted (+ ScreenEvents.AFTER_INIT on TitleScreen: progress screen while blocking, cache-ready check)`.
- New rows (all registered and covered by the harness; they are listed as "extra hook" until added):

```json
{"id": "player_respawn", "side": "server", "event": "ServerPlayerEvents.AFTER_RESPAWN", "handler": "ShieldSystem (respawn)", "purpose": "shield back to systems.shield.start, wanted level cleared after death"},
{"id": "bus_jump", "side": "both", "event": "C2S payload crafttheft:bus_jump (ServerPlayNetworking) + mixin accessor LivingEntity#jumping (LivingEntityAccessor)", "handler": "ClientInput.tick -> BattleBusEntity.requestDrop", "purpose": "the jump key of a bus rider drops them, also while the server is busy (the vanilla input packet's jumping flag alone can be overwritten before the bus ticks)"},
{"id": "glider_layer", "side": "client", "event": "LivingEntityFeatureRendererRegistrationCallback.EVENT", "handler": "GliderLayer.render", "purpose": "draw the Fortnite glider mesh on gliding players"},
{"id": "hide_shield_hearts", "side": "client", "event": "mixin Gui#renderPlayerHealth (redirect Player#getAbsorptionAmount)", "handler": "GuiShieldMixin", "purpose": "the Halo shield is vanilla absorption shown as the shield bar; hide the golden hearts"}
```

## 3. `docs/CONTRACTS.md` additions (mod side of the contract)

Under "Cache layout" / the mod:
- The mod mounts **every** folder `packs/<name>/` that has a `pack.mcmeta` as a required, top-priority client pack
  `crafttheft_cache_<name>` (folders starting with `.` are skipped, so `.old-*` swap folders never mount). Packs are
  re-scanned on every resource reload.
- Mesh materials resolve to `assets/crafttheft/textures/<material>.png` in any pack; a missing material texture is drawn
  with `textures/placeholder/generic.png` and logged (`[mesh] ... material textures missing`). Textures are sampled
  with repeat (no `.mcmeta`), so UVs outside 0..1 tile as the GTA/Fortnite notes require.
- `stats/halo_weapons.json`: keys are weapons sheet columns (`damage`, `pellets`, `burst`, `fireIntervalTicks`, `auto`,
  `clip`, `reserve`, `reloadTicks`, `spreadDeg`, `range`, `projectileSpeed`, `homing`, `blastRadius`, `zoom`); each
  value is range-checked (e.g. `zoom` 0.05..1, `clip` 1..1000) and an out-of-range value keeps the sheet value
  (logged `[stats]`). `stats/halo_shield.json` `{"shield":{"max","rechargeDelayTicks","rechargePerTick"}}` likewise.
- The jar's `assets/crafttheft/sounds.json` maps each sound event to the sources row's placeholder **vanilla sound
  event** (`{"name":"minecraft:...","type":"event"}`); the cache packs' entries (`"replace": true`) win.
- The jar ships original placeholder meshes at every mesh row's `out` (except `fn_mesh_chest`, whose placeholder is
  the vanilla chest model drawn in code) and placeholder icons at every icon row's `out`. All are generated at build
  time from the sheets (`fabric/src/tools`), so a new sources row gets its placeholder automatically.
- How the mod uses the mesh conventions: weapons are drawn at true size with the grip at the hand (clamped to
  0.1-1.4 blocks long if an extraction comes out absurd); vehicles/bus at true size; the chest at true size but scaled
  down to fit 1.5 blocks; the glider from the player's back.

Under "Extractor command line" (how the mod calls it, `CacheGuard`):
- No usable manifest: `"<extractor>" --config "<instance>/config/crafttheft.properties"`, working directory = the
  extractor's folder, output appended to `<instance>/logs/crafttheft-extractor.log`; the title screen is replaced by a
  progress screen reading `progress.json` until the process exits ("Play with placeholders" lets the player go on;
  the run continues and its content appears when it finishes).
- A game whose manifest status is `failed`: `--config <props> --only <game>` in the background, one game per process,
  one after another; at most 3 consecutive retries per game (`config/crafttheft-guard.properties`, reset when the
  game is no longer failed).
- After every run (exit 0, 1, 2, 5) the mod calls `reloadResourcePacks()`. Exit 4 (lock held) = another extractor is
  running: the mod waits for its `progress.json` `"finished":true` and reloads then.
- `extractor` may be relative to the instance folder (`.minecraft`).

## 4. Packaging

- Release jar: `fabric/build/libs/crafttheft-0.1.0.jar` (remapped to intermediary, about 270 KB, 199 files). Needs Fabric Loader
  0.19.5 and Fabric API 0.116.17+1.21.1 in the instance. The harness jar (`build/harness-libs/crafttheft-0.1.0-harness.jar`,
  built only by the harness tasks) is a test-only mod and must **not** go into the instance.
- `FirstRun` writes `options.txt` defaults on a fresh instance (onboardAccessibility:false, tutorialStep:none,
  skipMultiplayerWarning:true, renderDistance 10, simulationDistance 8, maxFps 120, music 0.3) before Minecraft reads
  it; an existing options.txt only gets `onboardAccessibility:false` appended when that key is missing. The bundle may
  still ship its own options.txt; FirstRun then leaves it alone.
- Licence of the mod: CC BY-NC-SA 4.0 (`fabric/LICENSE.md`, included in the jar as `LICENSE_crafttheft.md`). The jar
  contains no third-party code.

## 5. Not verifiable here (needs the real PC)

- Real extracted meshes/textures/sounds (all tests used the synthetic cache from `fabric/tools/CthSynth`, written with
  the extractor's own Core writers). In particular: weapon hand placement with real Halo grip origins, real vehicle and
  bus sizes against the hitboxes in the vehicles sheet, the Fortnite glider's attach point.
- Windows specifics of `CacheGuard`: starting the GUI-subsystem `cthextract.exe` from Java's ProcessBuilder (no
  console window expected), paths with spaces/non-ASCII in `crafttheft.properties`.
- Performance with 8k-triangle cars on real GPUs (immediate-mode drawing; measured only on Mesa llvmpipe).
- Quitting a world: once in about 12 automated runs the integrated server never finished "Saving worlds" (server
  thread looping in vanilla `ChunkMap.processUnloads`, 9 chunk generation tasks left, after the scripted player had
  just teleported to far-away, still-generating chunks). Not reproduced since; the mod requests no chunk loads (every
  placement is guarded by `hasChunk`). Worth watching for on the real PC.
