# packaging/

Builds the files a Craft Theft Halo release is made of, as `melty.json` (repository root) expects them.

```sh
python3 packaging/build_package.py                       # packaging/out/
python3 packaging/build_package.py --sources --release   # a release: + the corresponding-source zip, clean tree only
python3 -m unittest discover -s packaging/tests -v       # tests (generated fixtures, no network)
```

## What it builds

| File | Melty component | Installed to | Contents |
|---|---|---|---|
| `crafttheft-<ver>.jar` | `main` (kind `main`) | `{localappdata}/CraftTheftHalo/Prism/instances/CraftTheftHalo/.minecraft/mods` | copied from `fabric/build/libs/crafttheft-<ver>.jar` (never the `-sources`, `-dev` or `-harness` jars); `fabric.mod.json` must say id `crafttheft` and that version |
| `CraftTheftHalo-Minecraft-<ver>.zip` | `minecraft` (kind `extra`, `required: true`) | `{localappdata}/CraftTheftHalo` | see below |
| `CraftTheftHalo-<ver>-sources.zip` (`--sources`) | none: a GitHub release asset | - | the corresponding source of every GPL/LGPL binary in the zip, and `git archive` of this repository |
| `entries.json` | - | - | `{"main": [{path, size}], "minecraft": [...]}` for Melty's `validate_recipe` / `one_click_check` |
| `release.json`, `SHA256SUMS` | - | - | sizes and SHA-256 of every file and every zip entry |

The `minecraft` zip:

```
Prism/                                  Prism Launcher 11.1.1 portable MSVC build, unmodified (portable.txt included)
Prism/LICENSE-PrismLauncher.txt         Prism's LICENSE at tag 11.1.1 (the release zip has no licence file)
Prism/COPYING-PrismLauncher.md          Prism's own notices at tag 11.1.1
Prism/prismlauncher_update.cfg          [General] auto_check=false: opening Prism by hand never replaces the pinned build
Prism/instances/CraftTheftHalo/instance.cfg        from packaging/instance/ (CRLF)
Prism/instances/CraftTheftHalo/mmc-pack.json       from packaging/instance/ (UTF-8, no BOM)
Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar   Fabric API 0.116.17+1.21.1, unmodified
bin/cthextract.exe                      extractor/dist/cthextract.exe (checked: x64, GUI subsystem, .sha256 sidecar)
licenses/                               every licence text and notice; licenses/README.txt is the index
THIRD-PARTY-NOTICES.md, SOURCE.txt, README.txt, bundle-version.txt
```

Never in it (checked on every build): `accounts.json` and `prismlauncher.cfg` (Melty signs Prism in and fills that
file), `options.txt`, `crafttheft.properties` (Melty writes it before every Play), `cache/` (the extractor's output),
Prism's `libraries/ assets/ meta/ java/ logs/`, and any game archive (`.rpf .map .pak .utoc .ucas .fsb ...`).
Minecraft, Fabric Loader, LWJGL and Java are downloaded by Prism on the player's PC.

### The instance

- `instance.cfg` has **no `ConfigVersion`** on purpose: Prism then reads it with its old `key=value` parser, where a
  comma in a value (the `notes`) stays text; with `ConfigVersion=1.3` QSettings would turn it into a list. No `#`
  either (it starts a comment there). Prism rewrites the file in its own format the first time it saves it.
- `OverrideMemory=true`, `MaxMemAlloc=4096` (4 GB), `MinMemAlloc=1024`, and `LowMemWarning=false` so Prism never
  stops the launch with its low-free-RAM question (the companions can be running).
- `mmc-pack.json`: LWJGL 3.3.3, Minecraft 1.21.1 (important), Intermediary 1.21.1, Fabric Loader 0.19.5. The build
  checks these against `pins.json` `versions`.
- The game folder is `.minecraft` (Prism uses it when it exists and `minecraft/` does not), so the settings file
  Melty writes is `.minecraft/config/crafttheft.properties`.

## Pins (`pins.json`)

Every downloaded file has its URL(s), size and SHA-256 (plus SHA-512 / MD5 where upstream publishes one). A file
that does not match is never used: a cached copy is deleted and fetched again, a download that does not match moves
on to the next URL, and the build stops with exit code 3 if none matches. Git sources are pinned by commit **and**
by the tree hash of every path taken. To bump Prism, Qt, Fabric API or the vcpkg baseline: change `pins.json`, then
run `python3 packaging/tools/fetch_licenses.py --update` and review the licence diffs.

Downloads are cached in `packaging/downloads/` (git-ignored); `--fetch-only` fills the cache, `--offline` uses only
the cache. Outputs go to `packaging/out/` (git-ignored).

## Licence texts (`licenses/`)

The licence texts the bundle needs are committed in `packaging/licenses/`, each with its origin and SHA-256 in
`licenses/SOURCES.json`; `tools/fetch_licenses.py` re-creates them (`--check` verifies without network). The Qt
notices (`licenses/qt/qt-6.10.2-third-party.txt`, 44 entries) are generated from the `qt_attribution.json` files of
the pinned Qt source tarballs. At build time the builder adds:

- `licenses/CC-BY-NC-SA-4.0.txt` (repository `LICENSE`) and `licenses/GPL-3.0.txt` (`extractor/LICENSE`);
- `licenses/extractor/vendor/<Name>/` (each vendored library's `LICENSE`, `NOTICE`, `VENDORED.md`);
- `licenses/extractor/nuget-packages.txt`: generated from the `deps.json` **inside** `cthextract.exe`, so it lists
  exactly the NuGet packages compiled into the exe, each with the licence text from its package in the NuGet cache;
- `licenses/dotnet/runtime-<ver>-{LICENSE,THIRD-PARTY-NOTICES}.TXT` for the .NET runtime version compiled into the
  exe (from the NuGet cache, else the committed copy in `licenses/dotnet/<ver>/`).

The build stops (exit 2) when the exe contains a project or package that `THIRD-PARTY-NOTICES.md` does not name, a
file no `deps.json` library accounts for, a package whose licence cannot be found, or a project `build_package.py`
does not map to a vendor folder (`PROJECT_VENDOR`). `--allow-incomplete-notices` turns the notice problems into
warnings for local test builds; `--release` refuses it.

## GPL/LGPL source obligations

Prism (GPL-3.0), Qt 6.10.2 (LGPL-3.0; Qt Network Authorization GPL-3.0), LZO 2.10 (`lzo2.dll`, GPL-2.0+) and
libqrencode 4.1.1 (`qrencode.dll`, LGPL-2.1+) are object code we convey, and our extractor is GPL-3.0. Prism's
source tarball does not contain Qt, LZO or libqrencode. So each release:

1. `--sources` builds `CraftTheftHalo-<ver>-sources.zip`: the pinned upstream tarballs (Prism, qtbase, qtsvg,
   qtimageformats, qtnetworkauth, LZO), `git archive`s of libqrencode at the commit vcpkg builds and of the vcpkg
   ports/scripts that built `lzo2.dll`/`qrencode.dll`, and `git archive` of this repository at the release commit.
2. Upload it to the GitHub release `v<ver>` next to the two Melty files and keep it there at least three years.
   `SOURCE.txt` in the bundle points to it (and to each upstream URL with its hash).
3. Recommended: also attach it to the Melty release as a non-installed `extra` component (see
   `docs/requests/packaging.md`), so the source sits next to the binaries.

## Release steps

1. Commit everything (`--release` refuses uncommitted changes and untracked, not-ignored files, because `SOURCE.txt`
   names the commit).
2. `python3 tools/preflight.py && python3 tools/gen.py`, `extractor/build.sh`, `(cd fabric && ./gradlew build)`.
   Build the extractor **after** the commit: `build.sh` writes `extractor/dist/cthextract.source.json` (the commit,
   the `extractor/` tree hash, whether it was dirty, the exe's SHA-256), and `--release` refuses an exe whose stamp is
   missing, dirty, for another exe, or for another `extractor/` tree than the release commit's, so the GPL-3.0
   corresponding source of `bin/cthextract.exe` is exactly the tagged `extractor/` folder.
3. `python3 packaging/build_package.py --sources --release`.
4. Tag `v<ver>`, create the GitHub release, upload `crafttheft-<ver>.jar`, `CraftTheftHalo-Minecraft-<ver>.zip`,
   `CraftTheftHalo-<ver>-sources.zip` and `SHA256SUMS`.
5. Upload the two Melty files to Melty (component ids `main` and `minecraft`), using `entries.json` for
   `validate_recipe` / `one_click_check`. `melty.json` is read from the repository root.

## Exit codes

0 ok; 1 a check of the built files failed; 2 an input is missing or invalid (nothing downloaded or written);
3 a download failed or did not match its pin; 4 bad arguments.

## Reproducibility

Entries are sorted, use `/`, a fixed timestamp (2026-01-01 00:00) and fixed attributes; text files are generated
deterministically. Two builds from the same inputs give the same SHA-256 (checked by the tests and on the real
inputs). Deflate output can differ between zlib versions, so compare builds made with the same Python/zlib.
