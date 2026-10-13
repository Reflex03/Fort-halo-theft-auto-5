# Packaging: requests and records (agent key: packaging)

Everything here is coded already: `packaging/build_package.py`, `packaging/pins.json`, `packaging/instance/`,
`packaging/templates/`, `packaging/licenses/`, `packaging/tools/fetch_licenses.py`, `packaging/tests/`,
`melty.json`, `LICENSE`, `extractor/LICENSE`, `THIRD-PARTY-NOTICES.md`, `README.md`. Nothing below blocks a build.

## 1. Requests

### 1.1 `docs/CONTRACTS.md`: add a "Release files" section (coordinator)

Append after "Settings Melty writes before every Play":

```
## Release files (packaging/build_package.py)

crafttheft-<ver>.jar                 Melty component "main" -> {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods
                                     (copied from fabric/build/libs; -sources/-dev/-harness/... jars are never shipped)
CraftTheftHalo-Minecraft-<ver>.zip   Melty component "minecraft" (extra, required) -> {localappdata}/CraftTheftHalo
  Prism/                             Prism Launcher 11.1.1 portable MSVC, unmodified; + LICENSE/COPYING, prismlauncher_update.cfg
  Prism/instances/CraftTheftHalo/    instance.cfg (no ConfigVersion), mmc-pack.json, .minecraft/mods/fabric-api.jar
  bin/cthextract.exe                 extractor/dist/cthextract.exe
  licenses/, THIRD-PARTY-NOTICES.md, SOURCE.txt, README.txt, bundle-version.txt
CraftTheftHalo-<ver>-sources.zip     GitHub release asset only: corresponding source of every GPL/LGPL binary
The zip never contains cache/, accounts.json, prismlauncher.cfg, options.txt or crafttheft.properties, so an update
never touches the player's cache, sign-in, settings or worlds.
```

### 1.2 `melty.json`: optionally add the sources zip as a non-installed component (coordinator decision)

The brief asks for exactly two components, so `melty.json` has two. To put the GPL/LGPL source next to the
binaries on Melty itself (the strictest reading of GPL-2.0 section 3 for `Prism/lzo2.dll`), add this third component
and upload `CraftTheftHalo-<ver>-sources.zip` with each release. Melty's validator accepts it: valid, one click yes,
publishable, its files listed as unmapped (not installed). Exact output in section 2.4.

```json
{"id": "sources", "kind": "extra", "label": "Source code of the GPL/LGPL parts (not installed)", "fileName": "CraftTheftHalo-*-sources.zip", "required": false}
```

Either way, upload the sources zip to the GitHub release `v<ver>`: `SOURCE.txt` in every bundle points there.

### 1.3 Mod agent (`fabric/`)

- The builder takes `fabric/build/libs/crafttheft-<ver>.jar`. It ignores jars with the classifiers `sources`, `dev`,
  `javadoc`, `all`, `shadow`, `slim`, `api`, `harness`, `test(s)` (the `remapHarnessJar` task writes
  `crafttheft-<ver>-harness.jar` into the same folder: it is never shipped). It refuses a jar whose
  `fabric.mod.json` id is not `crafttheft` or whose version differs from the file name (e.g. an unexpanded
  `${version}`), and any jar containing `.rpf/.map/.pak/.utoc/.ucas/.fsb/.bk2/.ytd/.yft` files.
- `melty.json` `test.checks` is the fallback list from the brief, because `docs/requests/mod.md` did not exist when
  this was written: `mod-loads, cache-ready, halo-content, gta-content, fortnite-content, bus-ride, landed,
  chest-opened, weapon-fired, car-driven, wanted-stars, police-chase` (12 = Melty's maximum; each must match
  `^[a-z0-9][a-z0-9-]{0,39}$`), `minutes: 10`. If the mod reports other names, `melty.json` must list exactly those.
  `packaging/tests` checks the fallback list only while `mod.md` is absent.
- Melty's validator prints this advisory for our recipe shape (not a problem, warning or finding; publishing is not
  blocked): "This release has no code Melty knows runs in the game (no loader that runs mods' code, and not a
  program of its own), so nothing can write check lines: leave test.checks out and have the runs played instead."
  Melty does not see that Prism runs our Fabric mod. The checks stay as the brief asks; if `submit_release` or
  `test_release` rejects them, drop `test.checks` and keep the in-mod harness for our own runs.
- The mod should read `config/crafttheft.properties` with `Files.newBufferedReader(p, UTF_8)` +
  `Properties.load(Reader)`: Melty writes the player's folder paths (Windows user names can be non-ASCII).

### 1.4 Extractor agents (`extractor/`)

- The builder reads the `deps.json` embedded in `extractor/dist/cthextract.exe` and stops the release build when a
  NuGet package or project in it is not named in `THIRD-PARTY-NOTICES.md`, when a project is missing from
  `PROJECT_VENDOR` in `packaging/build_package.py`, or when a bundled file is not accounted for. So: **any new
  dependency needs a line in `THIRD-PARTY-NOTICES.md`** (and a `PROJECT_VENDOR` entry for a new project).
  Today the exe holds 32 NuGet packages and 13 projects; all are listed.
- `extractor/dist/` should keep holding only `cthextract.exe` + `cthextract.exe.sha256` (the builder checks the hash).
- Suggested (from research/packaging.md section 2.7, not required): the bundled Prism is the MSVC build and needs the
  VC++ 2015-2022 runtime, which we do not ship. The extractor runs first on every PC, so it could log
  `vcredist-missing` (no `%SystemRoot%\System32\{msvcp140,vcruntime140,vcruntime140_1,msvcp140_1}.dll`) as a manifest
  warning. The fallback is pinning Prism's MinGW build instead (`PrismLauncher-Windows-MinGW-w64-Portable-11.1.1.zip`,
  43,687,692 B, SHA-256 `05841d0b3bfc0a8212658457cc6035e1c29cd7ffae919464e874e384cbe8d062`), which needs about 45 more
  DLL notices.
- Observation, no action: `Blake3.dll` is compiled in but its native library is not (no native files in the bundle);
  in the vendored CUE4Parse only the SilverPalace, Theia and Aion2 game types use Blake3, never the Fortnite path.

### 1.5 Release steps (coordinator)

1. Commit everything; `python3 packaging/build_package.py --sources --release` refuses uncommitted changes because
   `SOURCE.txt` names the commit.
2. Upload `crafttheft-<ver>.jar` and `CraftTheftHalo-Minecraft-<ver>.zip` to Melty (component ids `main`,
   `minecraft`), with `packaging/out/entries.json` for `validate_recipe` / `one_click_check`.
3. Create the GitHub release `v<ver>` with those two files, `CraftTheftHalo-<ver>-sources.zip` and `SHA256SUMS`, and
   keep the sources zip there for at least three years.

## 2. Melty validator runs (exact output)

Inputs, the same for every run:

- `recipe`: `melty.json` as committed (2.1, 2.2), or as noted.
- `entries`: `packaging/out/entries.json` of a real build made here on 2026-10-13: Prism 11.1.1, its LICENSE and
  COPYING.md and Fabric API downloaded and verified against `pins.json`, the real `extractor/dist/cthextract.exe`
  (96,373,933 B), and a **stand-in mod jar** (433 B, generated by me with `fabric.mod.json` id `crafttheft`, version
  0.1.0) because `fabric/build/libs` did not exist yet. 94 zip entries + the jar = 95 files.
- `says`: the "Features" section of `README.md` (bold markers removed).
- `slug`: `craft-theft-halo` (validate_recipe only; from `create_mod`, `scratchpad/melty-ids.txt`).

Calls: `python3 -I scratchpad/call.py validate_recipe '<args>'` and `... one_click_check '<args>'`.

### 2.1 `validate_recipe`, final `melty.json`: valid, one click yes, no problems, warnings or findings

Complete output, verbatim (its text part lists only the first 15 placements; the JSON on the last line has all 95):

```
Valid.
Mode installed; games: minecraft-java (primary), halo-mcc (companion), gta-v (companion), fortnite (companion).
Files: main = crafttheft-*.jar (main, required); minecraft = CraftTheftHalo-Minecraft-*.zip (extra, required).
Installs: minecraft:(archive root) → {localappdata}/CraftTheftHalo; main:(archive root) → {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods.
Launch: {localappdata}/CraftTheftHalo/Prism/prismlauncher.exe --launch CraftTheftHalo.
First start: sets up game content ({localappdata}/CraftTheftHalo/bin/cthextract.exe --halo {game:halo-mcc} --gta {game:gta-v} --fortnite {game:fortnite} --out {localappdata}/CraftTheftHalo/cache) and waits until {localappdata}/CraftTheftHalo/cache/manifest.json has ""cacheSchema":1" in it, then starts the game.
Before every start: sets halo_dir, gta_dir, fortnite_dir, cache_dir, extractor in {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/config/crafttheft.properties.
Runtime data: {localappdata}/CraftTheftHalo.
One click: yes. A player with the games presses Play and Melty does the rest.
Saved as melty.json at the root of the mashup's GitHub repository (a component's fileName may use * where the version goes), this recipe is read from the GitHub link on upload and for every new release.
This release has no code Melty knows runs in the game (no loader that runs mods' code, and not a program of its own), so nothing can write check lines: leave test.checks out and have the runs played instead.
95 files placed, 0 left out.
  crafttheft-0.1.0.jar → {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods/crafttheft-0.1.0.jar
  Prism/COPYING-PrismLauncher.md → {localappdata}/CraftTheftHalo/Prism/COPYING-PrismLauncher.md
  Prism/LICENSE-PrismLauncher.txt → {localappdata}/CraftTheftHalo/Prism/LICENSE-PrismLauncher.txt
  Prism/Qt6Core.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Core.dll
  Prism/Qt6Gui.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Gui.dll
  Prism/Qt6Network.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Network.dll
  Prism/Qt6NetworkAuth.dll → {localappdata}/CraftTheftHalo/Prism/Qt6NetworkAuth.dll
  Prism/Qt6OpenGL.dll → {localappdata}/CraftTheftHalo/Prism/Qt6OpenGL.dll
  Prism/Qt6Svg.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Svg.dll
  Prism/Qt6Widgets.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Widgets.dll
  Prism/Qt6Xml.dll → {localappdata}/CraftTheftHalo/Prism/Qt6Xml.dll
  Prism/archive.dll → {localappdata}/CraftTheftHalo/Prism/archive.dll
  Prism/bz2.dll → {localappdata}/CraftTheftHalo/Prism/bz2.dll
  Prism/cmark.dll → {localappdata}/CraftTheftHalo/Prism/cmark.dll
  Prism/iconengines/qsvgicon.dll → {localappdata}/CraftTheftHalo/Prism/iconengines/qsvgicon.dll
{"valid":true,"problems":[],"warnings":[],"findings":[],"summary":["Mode installed; games: minecraft-java (primary), halo-mcc (companion), gta-v (companion), fortnite (companion).","Files: main = crafttheft-*.jar (main, required); minecraft = CraftTheftHalo-Minecraft-*.zip (extra, required).","Installs: minecraft:(archive root) → {localappdata}/CraftTheftHalo; main:(archive root) → {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods.","Launch: {localappdata}/CraftTheftHalo/Prism/prismlauncher.exe --launch CraftTheftHalo.","First start: sets up game content ({localappdata}/CraftTheftHalo/bin/cthextract.exe --halo {game:halo-mcc} --gta {game:gta-v} --fortnite {game:fortnite} --out {localappdata}/CraftTheftHalo/cache) and waits until {localappdata}/CraftTheftHalo/cache/manifest.json has \"\"cacheSchema\":1\" in it, then starts the game.","Before every start: sets halo_dir, gta_dir, fortnite_dir, cache_dir, extractor in {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/config/crafttheft.properties.","Runtime data: {localappdata}/CraftTheftHalo."],"oneClick":{"oneClick":"yes","needs":["Minecraft: Java Edition","Halo: The Master Chief Collection","Grand Theft Auto V Legacy","Fortnite"],"automatic":["Before the first start Melty sets up game content and waits for it, then starts the game."],"byHand":[],"someByHand":[],"notes":[],"blockers":[],"toFinish":[]},"plan":{"placed":[{"component":"main","from":"crafttheft-0.1.0.jar","to":"{localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods/crafttheft-0.1.0.jar"},{"component":"minecraft","from":"Prism/COPYING-PrismLauncher.md","to":"{localappdata}/CraftTheftHalo/Prism/COPYING-PrismLauncher.md"},{"component":"minecraft","from":"Prism/LICENSE-PrismLauncher.txt","to":"{localappdata}/CraftTheftHalo/Prism/LICENSE-PrismLauncher.txt"},{"component":"minecraft","from":"Prism/Qt6Core.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Core.dll"},{"component":"minecraft","from":"Prism/Qt6Gui.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Gui.dll"},{"component":"minecraft","from":"Prism/Qt6Network.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Network.dll"},{"component":"minecraft","from":"Prism/Qt6NetworkAuth.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6NetworkAuth.dll"},{"component":"minecraft","from":"Prism/Qt6OpenGL.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6OpenGL.dll"},{"component":"minecraft","from":"Prism/Qt6Svg.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Svg.dll"},{"component":"minecraft","from":"Prism/Qt6Widgets.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Widgets.dll"},{"component":"minecraft","from":"Prism/Qt6Xml.dll","to":"{localappdata}/CraftTheftHalo/Prism/Qt6Xml.dll"},{"component":"minecraft","from":"Prism/archive.dll","to":"{localappdata}/CraftTheftHalo/Prism/archive.dll"},{"component":"minecraft","from":"Prism/bz2.dll","to":"{localappdata}/CraftTheftHalo/Prism/bz2.dll"},{"component":"minecraft","from":"Prism/cmark.dll","to":"{localappdata}/CraftTheftHalo/Prism/cmark.dll"},{"component":"minecraft","from":"Prism/iconengines/qsvgicon.dll","to":"{localappdata}/CraftTheftHalo/Prism/iconengines/qsvgicon.dll"},{"component":"minecraft","from":"Prism/icuuc.dll","to":"{localappdata}/CraftTheftHalo/Prism/icuuc.dll"},{"component":"minecraft","from":"Prism/imageformats/qgif.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qgif.dll"},{"component":"minecraft","from":"Prism/imageformats/qicns.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qicns.dll"},{"component":"minecraft","from":"Prism/imageformats/qico.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qico.dll"},{"component":"minecraft","from":"Prism/imageformats/qjpeg.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qjpeg.dll"},{"component":"minecraft","from":"Prism/imageformats/qsvg.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qsvg.dll"},{"component":"minecraft","from":"Prism/imageformats/qtga.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qtga.dll"},{"component":"minecraft","from":"Prism/imageformats/qtiff.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qtiff.dll"},{"component":"minecraft","from":"Prism/imageformats/qwbmp.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qwbmp.dll"},{"component":"minecraft","from":"Prism/imageformats/qwebp.dll","to":"{localappdata}/CraftTheftHalo/Prism/imageformats/qwebp.dll"},{"component":"minecraft","from":"Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar","to":"{localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar"},{"component":"minecraft","from":"Prism/instances/CraftTheftHalo/instance.cfg","to":"{localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/instance.cfg"},{"component":"minecraft","from":"Prism/instances/CraftTheftHalo/mmc-pack.json","to":"{localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/mmc-pack.json"},{"component":"minecraft","from":"Prism/jars/JavaCheck.jar","to":"{localappdata}/CraftTheftHalo/Prism/jars/JavaCheck.jar"},{"component":"minecraft","from":"Prism/jars/NewLaunch.jar","to":"{localappdata}/CraftTheftHalo/Prism/jars/NewLaunch.jar"},{"component":"minecraft","from":"Prism/jars/NewLaunchLegacy.jar","to":"{localappdata}/CraftTheftHalo/Prism/jars/NewLaunchLegacy.jar"},{"component":"minecraft","from":"Prism/liblzma.dll","to":"{localappdata}/CraftTheftHalo/Prism/liblzma.dll"},{"component":"minecraft","from":"Prism/lz4.dll","to":"{localappdata}/CraftTheftHalo/Prism/lz4.dll"},{"component":"minecraft","from":"Prism/lzo2.dll","to":"{localappdata}/CraftTheftHalo/Prism/lzo2.dll"},{"component":"minecraft","from":"Prism/manifest.txt","to":"{localappdata}/CraftTheftHalo/Prism/manifest.txt"},{"component":"minecraft","from":"Prism/platforms/qwindows.dll","to":"{localappdata}/CraftTheftHalo/Prism/platforms/qwindows.dll"},{"component":"minecraft","from":"Prism/portable.txt","to":"{localappdata}/CraftTheftHalo/Prism/portable.txt"},{"component":"minecraft","from":"Prism/prismlauncher.exe","to":"{localappdata}/CraftTheftHalo/Prism/prismlauncher.exe"},{"component":"minecraft","from":"Prism/prismlauncher_filelink.exe","to":"{localappdata}/CraftTheftHalo/Prism/prismlauncher_filelink.exe"},{"component":"minecraft","from":"Prism/prismlauncher_update.cfg","to":"{localappdata}/CraftTheftHalo/Prism/prismlauncher_update.cfg"},{"component":"minecraft","from":"Prism/prismlauncher_updater.exe","to":"{localappdata}/CraftTheftHalo/Prism/prismlauncher_updater.exe"},{"component":"minecraft","from":"Prism/qrencode.dll","to":"{localappdata}/CraftTheftHalo/Prism/qrencode.dll"},{"component":"minecraft","from":"Prism/qt.conf","to":"{localappdata}/CraftTheftHalo/Prism/qt.conf"},{"component":"minecraft","from":"Prism/qtlogging.ini","to":"{localappdata}/CraftTheftHalo/Prism/qtlogging.ini"},{"component":"minecraft","from":"Prism/styles/qmodernwindowsstyle.dll","to":"{localappdata}/CraftTheftHalo/Prism/styles/qmodernwindowsstyle.dll"},{"component":"minecraft","from":"Prism/tls/qcertonlybackend.dll","to":"{localappdata}/CraftTheftHalo/Prism/tls/qcertonlybackend.dll"},{"component":"minecraft","from":"Prism/tls/qschannelbackend.dll","to":"{localappdata}/CraftTheftHalo/Prism/tls/qschannelbackend.dll"},{"component":"minecraft","from":"Prism/tomlplusplus-3.dll","to":"{localappdata}/CraftTheftHalo/Prism/tomlplusplus-3.dll"},{"component":"minecraft","from":"Prism/zlib1.dll","to":"{localappdata}/CraftTheftHalo/Prism/zlib1.dll"},{"component":"minecraft","from":"Prism/zstd.dll","to":"{localappdata}/CraftTheftHalo/Prism/zstd.dll"},{"component":"minecraft","from":"README.txt","to":"{localappdata}/CraftTheftHalo/README.txt"},{"component":"minecraft","from":"SOURCE.txt","to":"{localappdata}/CraftTheftHalo/SOURCE.txt"},{"component":"minecraft","from":"THIRD-PARTY-NOTICES.md","to":"{localappdata}/CraftTheftHalo/THIRD-PARTY-NOTICES.md"},{"component":"minecraft","from":"bin/cthextract.exe","to":"{localappdata}/CraftTheftHalo/bin/cthextract.exe"},{"component":"minecraft","from":"bundle-version.txt","to":"{localappdata}/CraftTheftHalo/bundle-version.txt"},{"component":"minecraft","from":"licenses/Apache-2.0.txt","to":"{localappdata}/CraftTheftHalo/licenses/Apache-2.0.txt"},{"component":"minecraft","from":"licenses/CC-BY-NC-SA-4.0.txt","to":"{localappdata}/CraftTheftHalo/licenses/CC-BY-NC-SA-4.0.txt"},{"component":"minecraft","from":"licenses/GPL-2.0.txt","to":"{localappdata}/CraftTheftHalo/licenses/GPL-2.0.txt"},{"component":"minecraft","from":"licenses/GPL-3.0.txt","to":"{localappdata}/CraftTheftHalo/licenses/GPL-3.0.txt"},{"component":"minecraft","from":"licenses/LGPL-2.1.txt","to":"{localappdata}/CraftTheftHalo/licenses/LGPL-2.1.txt"},{"component":"minecraft","from":"licenses/LGPL-3.0.txt","to":"{localappdata}/CraftTheftHalo/licenses/LGPL-3.0.txt"},{"component":"minecraft","from":"licenses/README.txt","to":"{localappdata}/CraftTheftHalo/licenses/README.txt"},{"component":"minecraft","from":"licenses/dotnet/runtime-10.0.12-LICENSE.TXT","to":"{localappdata}/CraftTheftHalo/licenses/dotnet/runtime-10.0.12-LICENSE.TXT"},{"component":"minecraft","from":"licenses/dotnet/runtime-10.0.12-THIRD-PARTY-NOTICES.TXT","to":"{localappdata}/CraftTheftHalo/licenses/dotnet/runtime-10.0.12-THIRD-PARTY-NOTICES.TXT"},{"component":"minecraft","from":"licenses/extractor/nuget-packages.txt","to":"{localappdata}/CraftTheftHalo/licenses/extractor/nuget-packages.txt"},{"component":"minecraft","from":"licenses/extractor/vendor/CUE4Parse/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/CUE4Parse/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/CUE4Parse/NOTICE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/CUE4Parse/NOTICE"},{"component":"minecraft","from":"licenses/extractor/vendor/CUE4Parse/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/CUE4Parse/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/Fmod5Sharp/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/Fmod5Sharp/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/Fmod5Sharp/NOTICE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/Fmod5Sharp/NOTICE"},{"component":"minecraft","from":"licenses/extractor/vendor/Fmod5Sharp/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/Fmod5Sharp/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/MeshDecimator/LICENSE.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/MeshDecimator/LICENSE.md"},{"component":"minecraft","from":"licenses/extractor/vendor/MeshDecimator/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/MeshDecimator/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/Reclaimer/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/Reclaimer/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/Reclaimer/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/Reclaimer/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/gta-toolkit/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/gta-toolkit/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/gta-toolkit/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/gta-toolkit/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/ooz/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/ooz/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/ooz/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/ooz/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/vendor/vgmstream-binka/LICENSE","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/vgmstream-binka/LICENSE"},{"component":"minecraft","from":"licenses/extractor/vendor/vgmstream-binka/VENDORED.md","to":"{localappdata}/CraftTheftHalo/licenses/extractor/vendor/vgmstream-binka/VENDORED.md"},{"component":"minecraft","from":"licenses/extractor/xiph-libogg-1.3.5-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/extractor/xiph-libogg-1.3.5-COPYING.txt"},{"component":"minecraft","from":"licenses/extractor/xiph-libvorbis-1.3.7-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/extractor/xiph-libvorbis-1.3.7-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/bzip2-1.0.8-LICENSE.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/bzip2-1.0.8-LICENSE.txt"},{"component":"minecraft","from":"licenses/prism-libs/cmark-0.31.1-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/cmark-0.31.1-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/libarchive-3.8.2-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/libarchive-3.8.2-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/libqrencode-4.1.1-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/libqrencode-4.1.1-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/lz4-1.10.0-LICENSE.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/lz4-1.10.0-LICENSE.txt"},{"component":"minecraft","from":"licenses/prism-libs/lzo-2.10-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/lzo-2.10-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/tomlplusplus-3.4.0-LICENSE.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/tomlplusplus-3.4.0-LICENSE.txt"},{"component":"minecraft","from":"licenses/prism-libs/xz-5.8.1-COPYING.0BSD.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/xz-5.8.1-COPYING.0BSD.txt"},{"component":"minecraft","from":"licenses/prism-libs/xz-5.8.1-COPYING.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/xz-5.8.1-COPYING.txt"},{"component":"minecraft","from":"licenses/prism-libs/zlib-1.3.1-LICENSE.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/zlib-1.3.1-LICENSE.txt"},{"component":"minecraft","from":"licenses/prism-libs/zstd-1.5.7-LICENSE.txt","to":"{localappdata}/CraftTheftHalo/licenses/prism-libs/zstd-1.5.7-LICENSE.txt"},{"component":"minecraft","from":"licenses/qt/qt-6.10.2-third-party.txt","to":"{localappdata}/CraftTheftHalo/licenses/qt/qt-6.10.2-third-party.txt"}],"unmapped":[]}}
```

### 2.2 `one_click_check`, final `melty.json`: one click yes, publishable, nothing by hand, no notes

```
One click: yes. A player with the games presses Play and Melty does the rest.
Players need: Minecraft: Java Edition, Halo: The Master Chief Collection, Grand Theft Auto V Legacy, Fortnite.
Melty does it: Before the first start Melty sets up game content and waits for it, then starts the game.
Checked: the recipe and the files you listed.
{"oneClick":"yes","needs":["Minecraft: Java Edition","Halo: The Master Chief Collection","Grand Theft Auto V Legacy","Fortnite"],"automatic":["Before the first start Melty sets up game content and waits for it, then starts the game."],"byHand":[],"someByHand":[],"notes":[],"blockers":[],"toFinish":[],"publishable":true,"standard":["Melty publishes a release only when a player who has the games can press Play once and be playing:","1. Melty installs every file itself (mode \"installed\", or \"standalone\" for a program of its own that plays from the player's copy of a game).","2. Every loader it needs is one Melty installs for every player of that game (game_info lists them), and the recipe asks for it.","3. Everything else it needs to run is in the release: you upload it, with credit, when its license lets it be shared, or the mashup works without it. Players are never sent anywhere to get something. A second game it needs goes under games; one players bring their own copy of (a console game) goes there with ownCopy: true, and recipe.ownCopies says what it looks like and what to take, so Melty finds it on the player's PC in whatever state it is in and readies it on Play.","4. Play starts the game (launch is not none).","5. A program of its own is told where the player's game is when it starts (\"{game}\" in launch.args or launch.env), so it never asks.","6. Every program, plugin and data file in the release is installed."]}
```

### 2.3 What was fixed: the first runs (with `notes.firstLaunch`)

The first runs used the recipe with a `notes.firstLaunch` text. Both were already valid, one click yes and
publishable, with no problems, warnings, findings or blockers, but both reported one note. `one_click_check` said:

```
Worth knowing: The first launch asks the player for something: "The first Play reads your games once (a few minutes), then Minecraft downloads itself. Every new world starts on the Battle Bus."
```

and `validate_recipe` had the same text in `oneClick.notes`: `["The first launch asks the player for something: \"The first Play reads your games once (a few minutes), then Minecraft downloads itself. Every new world starts on the Battle Bus.\""]`.

Our text asked nothing of the player, but Melty reads any `firstLaunch` note as a step the player has to do. Fix:
`firstLaunch` removed; its one useful fact (the first Play takes a few minutes and must be online) moved into
`beforeInstall` (187 characters; Melty cut a longer note at about 200 characters in earlier runs). Result: sections
2.1 and 2.2, with `notes: []` in both. No other line of either output changed.

### 2.4 Variant: the sources zip as a third, non-installed component (request 1.2)

Same inputs, plus the component of request 1.2 and the 11 entries of `CraftTheftHalo-0.1.0-sources.zip` built here.
`validate_recipe` text lines, verbatim (placement lines omitted):

```
Valid.
Mode installed; games: minecraft-java (primary), halo-mcc (companion), gta-v (companion), fortnite (companion).
Files: main = crafttheft-*.jar (main, required); minecraft = CraftTheftHalo-Minecraft-*.zip (extra, required); sources = CraftTheftHalo-*-sources.zip (extra).
Installs: minecraft:(archive root) → {localappdata}/CraftTheftHalo; main:(archive root) → {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods.
Launch: {localappdata}/CraftTheftHalo/Prism/prismlauncher.exe --launch CraftTheftHalo.
First start: sets up game content ({localappdata}/CraftTheftHalo/bin/cthextract.exe --halo {game:halo-mcc} --gta {game:gta-v} --fortnite {game:fortnite} --out {localappdata}/CraftTheftHalo/cache) and waits until {localappdata}/CraftTheftHalo/cache/manifest.json has ""cacheSchema":1" in it, then starts the game.
Before every start: sets halo_dir, gta_dir, fortnite_dir, cache_dir, extractor in {localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/config/crafttheft.properties.
Runtime data: {localappdata}/CraftTheftHalo.
One click: yes. A player with the games presses Play and Melty does the rest.
Saved as melty.json at the root of the mashup's GitHub repository (a component's fileName may use * where the version goes), this recipe is read from the GitHub link on upload and for every new release.
This release has no code Melty knows runs in the game (no loader that runs mods' code, and not a program of its own), so nothing can write check lines: leave test.checks out and have the runs played instead.
95 files placed, 11 left out.
Left out: SHA256SUMS, SOURCE.txt, crafttheft/fort-halo-theft-auto-5-c2f7084d7cdd.tar.gz, upstream/PrismLauncher-11.1.1.tar.gz, upstream/libqrencode-715e29fd4cd71b6e452ae0f4e36d917b43122ce8.tar, upstream/lzo-2.10.tar.gz, upstream/qtbase-everywhere-src-6.10.2.tar.xz, upstream/qtimageformats-everywhere-src-6.10.2.tar.xz, upstream/qtnetworkauth-everywhere-src-6.10.2.tar.xz, upstream/qtsvg-everywhere-src-6.10.2.tar.xz…
```

The verdict fields of its JSON (values verbatim):

```json
{
 "valid": true,
 "problems": [],
 "warnings": [],
 "findings": [],
 "oneClick": {
  "oneClick": "yes",
  "byHand": [],
  "notes": [],
  "blockers": []
 },
 "unmapped": [
  {
   "component": "sources",
   "path": "SHA256SUMS"
  },
  {
   "component": "sources",
   "path": "SOURCE.txt"
  },
  {
   "component": "sources",
   "path": "crafttheft/fort-halo-theft-auto-5-c2f7084d7cdd.tar.gz"
  },
  {
   "component": "sources",
   "path": "upstream/PrismLauncher-11.1.1.tar.gz"
  },
  {
   "component": "sources",
   "path": "upstream/libqrencode-715e29fd4cd71b6e452ae0f4e36d917b43122ce8.tar"
  },
  {
   "component": "sources",
   "path": "upstream/lzo-2.10.tar.gz"
  },
  {
   "component": "sources",
   "path": "upstream/qtbase-everywhere-src-6.10.2.tar.xz"
  },
  {
   "component": "sources",
   "path": "upstream/qtimageformats-everywhere-src-6.10.2.tar.xz"
  },
  {
   "component": "sources",
   "path": "upstream/qtnetworkauth-everywhere-src-6.10.2.tar.xz"
  },
  {
   "component": "sources",
   "path": "upstream/qtsvg-everywhere-src-6.10.2.tar.xz"
  },
  {
   "component": "sources",
   "path": "upstream/vcpkg-2d6a6cf3ac9a7cc93942c3d289a2f9c661a6f4a7-subset.tar"
  }
 ]
}
```

`one_click_check` for the same variant (values verbatim):

```json
{
 "oneClick": "yes",
 "byHand": [],
 "notes": [],
 "blockers": [],
 "toFinish": [],
 "publishable": true
}
```

## 3. What was verified here, and how

| Claim | How | Label |
|---|---|---|
| Builder behaviour: picks the right jar (ignores -sources/-harness); reports missing/invalid inputs together before downloading; refuses a pin mismatch; replaces a corrupt cached file; offline mode; refuses a non-pristine Prism zip, a console exe, a stale exe hash, an unexpanded jar version, out-of-date notices, an unknown project, a stray bundled file, a missing NuGet package; reproducible zips; sources zip with git tree pins; `--release` refuses a dirty tree | `python3 -m unittest discover -s packaging/tests`: 32 tests, all pass, on generated fixtures (fake Prism zip, fake PE + .NET single-file bundle, fake NuGet folder, local git repositories) | synthetic-test |
| Real inputs: the Prism 11.1.1 zip, its LICENSE/COPYING.md and Fabric API download and match `pins.json`; the real `cthextract.exe` passes the PE/sha256/bundle checks, and its 32 NuGet packages and 13 projects are all covered by the notices | full build with real downloads, the real exe and a generated stand-in mod jar: 94-entry zip, 63,802,577 B (147,716,293 B unpacked) | synthetic-test (real third-party inputs, stand-in jar) |
| Reproducible with real inputs | a second build (`--offline`, from the cache) gave the same SHA-256 `effaad548f91dd67676224d2d9d7df203a43dd630d51bf254fc940a5dee1def9` | synthetic-test |
| Corresponding-source zip | `--sources` with the real pins: every tarball hash and every git tree hash matched; 11 entries, 62,198,950 B | synthetic-test |
| Melty recipe | sections 2.1 to 2.4 | Melty validator |
| Qt build: official Qt 6.10.2, MSVC 2022 (`Qt6Core.dll` version string); PCRE2 10.47, zlib 1.3.1, libpng 1.6.54, libjpeg-turbo 3.1.3 and libtiff inside the Qt DLLs | `strings` on the DLLs of the pinned Prism zip | source-inspection |
| vcpkg port versions and refs (lzo 2.10 port-version 9, libqrencode `715e29f` port-version 3, libarchive 3.8.2, ...), and vcpkg's SHA-512 pins equal the tarballs we pin | partial clone of microsoft/vcpkg at Prism's baseline | source-inspection |
| Prism reads an `instance.cfg` without `ConfigVersion` with its old key=value parser; `.minecraft` is the game folder when it exists; `prismlauncher_update.cfg` key `auto_check` | Prism 11.1.1 source: `launcher/settings/INIFile.cpp`, `launcher/minecraft/MinecraftInstance.cpp:352`, `launcher/updater/PrismExternalUpdater.cpp:60` | source-inspection |
| The bundle on a real Windows PC (Prism launch, Melty sign-in, extractor run, mod load) | not possible here | **untested** |
| The real mod jar | `fabric/build/libs` did not exist while this was written | **untested** (stand-in used) |
