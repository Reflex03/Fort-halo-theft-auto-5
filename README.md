# Craft Theft Halo

Drop out of Fortnite's Battle Bus over a Minecraft world, loot Halo 3 guns from Fortnite chests, steal GTA V cars
and outrun the police. A [Melty](https://melty.gg) mashup for **Minecraft: Java Edition 1.21.1** (Fabric), with
content read from **your own** installs of Halo: The Master Chief Collection, Grand Theft Auto V Legacy and
Fortnite. Nothing from those games is shipped.

## Features

- **Single player**, in an ordinary Minecraft survival world.
- **Fortnite's Battle Bus** (its real model) flies over every new world. Jump out, skydive, and open the
  **Fortnite glider** to land wherever you like.
- **Fortnite loot chests** are scattered around spawn. They hold:
  - **Halo 3 weapons** with their real models, firing sounds and stats from your MCC install: MA5C Assault Rifle,
    M6G Magnum, BR55 Battle Rifle, M90 Shotgun, SRS99 Sniper Rifle, Plasma Rifle, Needler, M41 Rocket Launcher,
    frag and plasma grenades.
  - **Fortnite heals** with their real icons and sounds: Bandages, Med Kit, Small Shield Potion, Shield Potion and
    Chug Jug.
- A **Halo energy shield** on top of your health. It recharges after a few seconds without damage, the way Halo 3's
  does; Fortnite shield items fill it the rest of the way.
- **GTA V cars** parked around the world, with their real models and textures: Pegassi Infernus, Karin Sultan and
  Dinka Blista. Get in and drive.
- **GTA wanted stars.** Crimes raise them: hitting or killing villagers, attacking an iron golem, stealing a car,
  firing near villagers, attacking the police.
- **GTA police cruisers** (real model and siren) chase you and ram you; more of them the more stars you have.
  Get away to lose the stars.
- **Vanilla mobs** still fight you.
- If a game's content cannot be read, the mod uses its own stand-in art and sounds for it, so it always plays.

## What you need

| | |
|---|---|
| **Minecraft: Java Edition** | a Microsoft account that owns it. Melty signs the bundled launcher in for you. |
| **Halo: The Master Chief Collection** | with **Halo 3** installed (MCC installs each game separately). |
| **Grand Theft Auto V Legacy** | the original PC edition. Only its story-mode files are read. The mod never touches GTA Online, and GTA V is not even started. |
| **Fortnite** | installed through the Epic Games Launcher. It is not started either. |
| **Windows 10 (1903 or later) or 11, 64-bit** | about 2 GB free for Minecraft, Java and the content read from your games. |
| **Internet on the first Play** | Minecraft downloads itself, and the content reader fetches the public keys it needs for Fortnite and GTA V. |

## How your games' content is used

Nothing from Halo, GTA V or Fortnite is in this repository, in the download, or anywhere we host. Instead:

1. On your first Play, Melty runs `cthextract.exe` (the "game content" step; it takes a few minutes).
2. It **reads** the weapons, cars, the bus, the glider, chests, icons and sounds from the game folders on your PC.
   It never writes into a game folder, never starts or modifies a game, and never goes near GTA Online.
3. What it read goes into `%LOCALAPPDATA%\CraftTheftHalo\cache` as a Minecraft resource pack, on your PC only.
   It is never uploaded.
4. To open the games' files it needs their decryption keys:
   - **Fortnite:** the public AES keys and property mappings for your Fortnite version, from community services
     (fortniteporting.app, uedb.dev, fortnite-api.com).
   - **GTA V:** your own copy's key, read from your `GTA5.exe`, plus a key table from the CodeWalker project.
     Every key is checked against known hashes before it is used.
5. Everything it tried, found and picked is logged in `cache\extract.log`, and `cache\manifest.json` says for
   every item whether it was read ("ok"), not found ("missing") or failed, and why.
6. When a game was missing or could not be read, the mod tries it again by itself once that game is installed or
   updated. Until then that game's items use the mod's stand-in art.

## Playing

Install Craft Theft Halo from Melty and press Play. The first time:

1. Melty reads your games ("Setting up game content").
2. Prism Launcher (bundled) downloads Minecraft 1.21.1, Fabric and Java, then starts the game.
3. Create a new world: you start on the Battle Bus.

Your worlds and settings live in `%LOCALAPPDATA%\CraftTheftHalo\Prism\instances\CraftTheftHalo\.minecraft`.
Uninstalling through Melty removes everything, worlds included.

## For developers

### Layout

| Folder | What | Licence |
|---|---|---|
| `sheets/` | **Source of truth**: one JSON sheet per system (weapons, consumables, vehicles, crimes, wanted, loot, systems), every piece of game content the mod uses (`sources.json`) and every Minecraft hook (`hooks.json`) | CC BY-NC-SA 4.0 |
| `tools/` | `preflight.py` (checks the sheets) and `gen.py` (turns them into Java and C# code) | CC BY-NC-SA 4.0 |
| `extractor/` | `cthextract.exe`, the game-content reader (.NET 10, C#), with vendored libraries in `extractor/vendor/` | **GPL-3.0-only** |
| `fabric/` | the Fabric mod `crafttheft` (Java 21, Minecraft 1.21.1, Fabric Loader 0.19.5, Fabric API 0.116.17+1.21.1) | CC BY-NC-SA 4.0 |
| `fabric/tools/CthSynth/` | test-only .NET tool (never shipped) that links the extractor's `CthExtract.Core` | **GPL-3.0-only** |
| `packaging/` | builds the two Melty release files: Prism Launcher + instance + extractor, and the mod jar | CC BY-NC-SA 4.0 |
| `docs/` | `CONTRACTS.md` (command line, cache layout, `.cthm` mesh format, sound events, settings) and `requests/` | CC BY-NC-SA 4.0 |
| `packaging/melty.recipe.json` | the Melty install recipe (copied to `melty.json` at the root once auto-publishing from GitHub is turned on) | CC BY-NC-SA 4.0 |

The mod and the extractor never call each other's code: they only share files (`docs/CONTRACTS.md`).

### Build

Needs Python 3.9+, the .NET 10 SDK, JDK 21 and git. Linux, macOS or Windows (Git Bash) all work; the outputs are
always the Windows files players get.

```sh
python3 tools/preflight.py && python3 tools/gen.py    # sheets -> generated code (gen.py refuses on preflight errors)
extractor/build.sh                                   # tests, then extractor/dist/cthextract.exe (self-contained)
(cd fabric && ./gradlew build)                       # fabric/build/libs/crafttheft-<version>.jar
python3 packaging/build_package.py                   # packaging/out/: the two Melty files + entries.json, SHA256SUMS
python3 packaging/build_package.py --sources --release   # for a release: also the corresponding-source zip
```

`packaging/build_package.py` downloads Prism Launcher 11.1.1 and Fabric API 0.116.17+1.21.1, refusing anything
whose size and SHA-256 differ from `packaging/pins.json`, and builds reproducible zips. If the extractor or the mod
jar has not been built yet, it says which one and how to build it, and downloads nothing. See
[`packaging/README.md`](packaging/README.md) for what goes into each file and the release steps.

Tests: `extractor/build.sh` runs the extractor's test projects; `python3 -m unittest discover -s packaging/tests`
runs the packaging tests; the mod has its own Gradle tests and a scripted test client (`fabric/src/harness`).
All tests use generated data: no game files are needed, and none may ever be committed.

To run the extractor by hand:

```
cthextract.exe --halo "<MCC folder>" --gta "<GTA V folder>" --fortnite "<Fortnite folder>" --out "<cache folder>"
               [--only halo,gta,fortnite] [--config crafttheft.properties] [--check] [--console]
```

### Rules for contributors

- Never commit, upload or attach game files or anything extracted from them (meshes, textures, sounds, keys,
  `.usmap` files, caches). Test fixtures are generated by our own code.
- Every vendored or referenced library must be GPL-3.0-compatible (MIT, BSD, Apache-2.0, ISC, Zlib, GPL-3.0...),
  goes in `extractor/vendor/<Name>/` with its `LICENSE` and a `VENDORED.md`, and is listed in
  `THIRD-PARTY-NOTICES.md` (the package build checks this).

## Licences

- **The mod and its content** (our code, our original art and sounds, the sheets, docs and packaging):
  [CC BY-NC-SA 4.0](LICENSE).
- **The extractor** (`extractor/`): [GPL-3.0-only](extractor/LICENSE), because it uses Reclaimer (GPL-3.0). The
  mod's test tool `fabric/tools/CthSynth/` links the extractor's Core library and is GPL-3.0-only too.
- **Third-party software** we ship (Prism Launcher, Qt, Fabric API, the .NET runtime and the extractor's
  libraries): see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Every release's `SOURCE.txt` says where to
  get the source of each GPL/LGPL part; we mirror it with every GitHub release.
- **Game content** is not covered by any of these licences: it stays the property of its owners, and you need your
  own copies of the games.

## Credits

- **Games:** Minecraft by Mojang Studios; Halo 3 and Halo: The Master Chief Collection by Bungie, 343 Industries
  and Halo Studios; Grand Theft Auto V by Rockstar Games; Fortnite by Epic Games.
- **Launcher and mod platform:** [Prism Launcher](https://prismlauncher.org), [FabricMC](https://fabricmc.net)
  (Fabric Loader, Fabric API, Loom).
- **Reading the games:** [Reclaimer](https://github.com/Gravemind2401/Reclaimer) by Gravemind2401,
  [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG and contributors,
  [Fmod5Sharp](https://github.com/SamboyCoding/Fmod5Sharp) by Samboy,
  [ooz](https://github.com/powzix/ooz) by powzix, [vgmstream](https://github.com/vgmstream/vgmstream),
  [gta-toolkit](https://github.com/Neodymium146/gta-toolkit) by Neodymium146,
  [CodeWalker](https://github.com/dexyfex/CodeWalker) by dexyfex (key table),
  [MeshDecimator](https://github.com/Whinarn/MeshDecimator) by Whinarn, and the libraries listed in
  THIRD-PARTY-NOTICES.md.
- **Fortnite keys and mappings:** the FortnitePorting, uedb.dev and Fortnite-API community services.
- Made for [Melty](https://melty.gg).

Craft Theft Halo is a fan project. It is not affiliated with or endorsed by Mojang Studios, Microsoft, Bungie,
343 Industries, Halo Studios, Rockstar Games, Take-Two Interactive or Epic Games.
