# Third-party notices: Craft Theft Halo

This file lists everything in a Craft Theft Halo release that we did not write, under which licence it is used,
and where its source code is. It is shipped in the `minecraft` component as `THIRD-PARTY-NOTICES.md`, next to the
`licenses/` folder that holds every licence text named here (`licenses/README.txt` is the index).

**No game content is shipped.** Nothing from Minecraft, Halo: The Master Chief Collection, Grand Theft Auto V or
Fortnite is in the release, the repository or our CI. `bin/cthextract.exe` reads those games' files from the
player's own installs on the player's own PC, into `%LOCALAPPDATA%/CraftTheftHalo/cache`, which is never uploaded.

How this file is kept complete: `packaging/build_package.py` reads the dependency list compiled into
`cthextract.exe` and refuses to build a release while any NuGet package or vendored library in it is missing here,
or while any licence text in `packaging/licenses/` differs from its recorded SHA-256.

## 1. What a release contains

| Release file | What | Licence |
|---|---|---|
| `crafttheft-<ver>.jar` (component `main`) | The Fabric mod: our code and our original placeholder art and sounds | CC BY-NC-SA 4.0 (`LICENSE`, `licenses/CC-BY-NC-SA-4.0.txt`) |
| `CraftTheftHalo-Minecraft-<ver>.zip` (component `minecraft`) → `Prism/` | Prism Launcher 11.1.1, official portable Windows MSVC build, **unmodified**, with the libraries it ships (section 3) | GPL-3.0-only; libraries as listed |
| ... → `Prism/instances/CraftTheftHalo/instance.cfg`, `mmc-pack.json`; `Prism/prismlauncher_update.cfg`; `README.txt`, `SOURCE.txt`, `bundle-version.txt` | Our configuration and text files | CC BY-NC-SA 4.0 |
| ... → `Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar` | Fabric API 0.116.17+1.21.1, unmodified (section 4) | Apache-2.0 |
| ... → `bin/cthextract.exe` | Our extractor, with the .NET runtime and the libraries of section 5 compiled in | GPL-3.0-only (`extractor/LICENSE`, `licenses/GPL-3.0.txt`) |
| `CraftTheftHalo-<ver>-sources.zip` (GitHub release asset, not installed) | Corresponding source of every GPL/LGPL program above (section 6) | as for each part |

Not included, because Prism Launcher downloads them itself from their publishers on the player's PC: Minecraft
1.21.1 (Mojang), Fabric Loader 0.19.5 and Intermediary (FabricMC), LWJGL 3.3.3, and the Java 21 runtime.

## 2. Our own code

| Part | Where | Licence |
|---|---|---|
| Fabric mod `crafttheft` and its original placeholder assets | `fabric/` | CC BY-NC-SA 4.0 |
| Extractor `cthextract.exe` (CthExtract, CthExtract.Core, .Halo, .Gta, .Fortnite, .Oodle) | `extractor/` | GPL-3.0-only (it links Reclaimer, GPL-3.0) |
| Design sheets, code generator, packaging, docs | `sheets/`, `tools/`, `packaging/`, `docs/` | CC BY-NC-SA 4.0 |
| Synthetic-cache test tool `CthSynth` (never shipped; links `CthExtract.Core`) | `fabric/tools/CthSynth/` | GPL-3.0-only |

Source: https://github.com/reflex03/fort-halo-theft-auto-5 (each release is tagged `v<ver>`; `SOURCE.txt` names the
exact commit).

## 3. Prism Launcher 11.1.1 and the libraries it ships (`Prism/`)

Prism Launcher is shipped exactly as published
(`PrismLauncher-Windows-MSVC-Portable-11.1.1.zip`, SHA-256 `ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7`).
That zip carries no licence files, so we add Prism's own `LICENSE` and `COPYING.md` (tag 11.1.1) as
`Prism/LICENSE-PrismLauncher.txt` and `Prism/COPYING-PrismLauncher.md`; `COPYING.md` holds Prism's notices for the
code inside `prismlauncher.exe` (MultiMC, PolyMC, libnbt++, rainbow, cmark, launcher, lionshead, tomlplusplus,
gamemode, libqrencode, vcpkg ports, icon sets). Library versions below were read from the DLLs' version strings and
from Prism's `vcpkg.json` at its vcpkg baseline `2d6a6cf3ac9a7cc93942c3d289a2f9c661a6f4a7` (source inspection).

| Files in `Prism/` | Library | Version | Licence | Licence text in the bundle | Source we must offer |
|---|---|---|---|---|---|
| `prismlauncher.exe`, `prismlauncher_updater.exe`, `prismlauncher_filelink.exe`, `jars/*.jar` | Prism Launcher | 11.1.1 | GPL-3.0-only (parts Apache-2.0, from MultiMC) | `Prism/LICENSE-PrismLauncher.txt`, `Prism/COPYING-PrismLauncher.md` | **yes** |
| `Qt6Core.dll`, `Qt6Gui.dll`, `Qt6Widgets.dll`, `Qt6Network.dll`, `Qt6OpenGL.dll`, `Qt6Xml.dll`, `platforms/qwindows.dll`, `styles/qmodernwindowsstyle.dll`, `tls/*.dll`, `imageformats/{qgif,qico,qjpeg}.dll` | Qt (qtbase) | 6.10.2 | LGPL-3.0-only | `licenses/LGPL-3.0.txt` + `licenses/GPL-3.0.txt` | **yes** |
| `Qt6Svg.dll`, `iconengines/qsvgicon.dll`, `imageformats/qsvg.dll` | Qt (qtsvg) | 6.10.2 | LGPL-3.0-only | same | **yes** |
| `imageformats/{qicns,qtga,qtiff,qwbmp,qwebp}.dll` | Qt (qtimageformats) | 6.10.2 | LGPL-3.0-only | same | **yes** |
| `Qt6NetworkAuth.dll` | Qt Network Authorization | 6.10.2 | **GPL-3.0-only** (no LGPL option for this module) | `licenses/GPL-3.0.txt` | **yes** |
| inside the Qt DLLs | PCRE2 10.47, zlib 1.3.1, libpng 1.6.54, libjpeg-turbo 3.1.3, libtiff 4.7.1, libwebp 1.6.0, FreeType 2.14.1, HarfBuzz 12.3.0, md4c 0.5.2, TinyCBOR 0.6.1, double-conversion 3.3.1, D3D12 Memory Allocator, Vulkan Memory Allocator 3.2.1, SHA-1/SHA-2/SHA-3/BLAKE2/MD4/MD5/SipHash, Unicode UCD/CLDR data, Public Suffix List (MPL-2.0) and others | as listed | BSD/MIT/Zlib/FTL/IJG/libpng/libtiff/Unicode/MPL-2.0/CC0 | `licenses/qt/qt-6.10.2-third-party.txt` (all 44 entries, with their licence texts) | in the Qt source |
| `lzo2.dll` | LZO | 2.10 (vcpkg port-version 9) | **GPL-2.0-or-later** | `licenses/prism-libs/lzo-2.10-COPYING.txt` | **yes** |
| `qrencode.dll` | libqrencode | 4.1.1 (commit `715e29fd`, vcpkg port-version 3) | **LGPL-2.1-or-later** | `licenses/prism-libs/libqrencode-4.1.1-COPYING.txt` | **yes** |
| `archive.dll` | libarchive | 3.8.2 | BSD-2-Clause (see its COPYING) | `licenses/prism-libs/libarchive-3.8.2-COPYING.txt` | no |
| `bz2.dll` | bzip2 | 1.0.8 | bzip2-1.0.6 | `licenses/prism-libs/bzip2-1.0.8-LICENSE.txt` | no |
| `liblzma.dll` | xz (liblzma) | 5.8.1 | 0BSD | `licenses/prism-libs/xz-5.8.1-COPYING*.txt` | no |
| `lz4.dll` | lz4 | 1.10.0 | BSD-2-Clause | `licenses/prism-libs/lz4-1.10.0-LICENSE.txt` | no |
| `zstd.dll` | zstd | 1.5.7 | BSD-3-Clause (dual GPL-2.0; we use BSD) | `licenses/prism-libs/zstd-1.5.7-LICENSE.txt` | no |
| `zlib1.dll` | zlib | 1.3.1 | Zlib | `licenses/prism-libs/zlib-1.3.1-LICENSE.txt` | no |
| `cmark.dll` | cmark | 0.31.1 | BSD-2-Clause (+ notices in its COPYING) | `licenses/prism-libs/cmark-0.31.1-COPYING.txt` | no |
| `tomlplusplus-3.dll` | toml++ | 3.4.0 | MIT | `licenses/prism-libs/tomlplusplus-3.4.0-LICENSE.txt` | no |
| `icuuc.dll` | forwarder to Windows' own `icu.dll` (no ICU code inside) | - | part of the Prism build | - | - |

Prism's MSVC build needs the Microsoft Visual C++ 2015-2022 runtime (`msvcp140.dll`, `vcruntime140*.dll`), which is
**not** shipped; it is part of Windows' common redistributables and is installed by Steam, Epic and the games.

### 3.1 How we meet the GPL/LGPL source obligations for Prism, Qt, LZO and libqrencode

We convey these binaries ourselves (from Melty's servers), so GPL-3.0 §6, GPL-2.0 §3 and LGPL-2.1 §6 / LGPL-3.0 apply
to us, not only to their authors. Prism's own source tarball does not contain the Qt, LZO or libqrencode sources,
so pointing at Prism alone is not enough. Our plan:

1. **Mirror with every release.** `python3 packaging/build_package.py --sources` downloads every upstream source
   listed below, checks each against the size and hash pinned in `packaging/pins.json` (git sources against their
   pinned tree hashes), adds a `git archive` of this repository at the release commit, and writes
   `CraftTheftHalo-<ver>-sources.zip`. That zip is uploaded as an asset of the GitHub release `v<ver>` of
   https://github.com/reflex03/fort-halo-theft-auto-5, next to the two files Melty installs, and stays there for at
   least three years after the release.
2. **Clear directions next to the object code.** `SOURCE.txt` at the root of the bundle names every GPL/LGPL
   program, its licence, the upstream URL with its hash, and the mirror URL.
3. **Fallback.** Anyone can open an issue on the repository to get the source by other means.
4. *(Recommended, see `docs/requests/packaging.md`)* also attach the sources zip to the Melty release as a
   non-installed `extra` component, so the source is offered from the same place as the binaries (the strictest
   reading of GPL-2.0 §3 for `lzo2.dll`). Melty's validator accepts that recipe.

| Mirrored source | Upstream | Pin |
|---|---|---|
| Prism Launcher 11.1.1 | https://github.com/PrismLauncher/PrismLauncher/releases/download/11.1.1/PrismLauncher-11.1.1.tar.gz | SHA-256 `9ea53f93714cd2a59dc0f38153690a59a3f06f439934d1410ec364cc7f363eb5` |
| Qt 6.10.2 qtbase | https://download.qt.io/archive/qt/6.10/6.10.2/submodules/qtbase-everywhere-src-6.10.2.tar.xz | SHA-256 `aeb78d29291a2b5fd53cb55950f8f5065b4978c25fb1d77f627d695ab9adf21e` (MD5 matches Qt's `md5sums.txt`) |
| Qt 6.10.2 qtsvg | .../qtsvg-everywhere-src-6.10.2.tar.xz | SHA-256 `f07ff80f38caf235187200345392ca7479445ddf49a36c3694cd52a735dad6e1` |
| Qt 6.10.2 qtimageformats | .../qtimageformats-everywhere-src-6.10.2.tar.xz | SHA-256 `8b8f9c718638081e7b3c000e7f31910140b1202a98e98df5d1b496fe6f639d67` |
| Qt 6.10.2 qtnetworkauth | .../qtnetworkauth-everywhere-src-6.10.2.tar.xz | SHA-256 `4f29fd9e4b505f5714fc42296b04c701f66ced185c49de4d520cb8de4b1981b3` |
| LZO 2.10 | https://www.oberhumer.com/opensource/lzo/download/lzo-2.10.tar.gz | SHA-256 `c0f892943208266f9b6543b3ae308fab6284c5c90e627931446fb49b4221a072` (= vcpkg's SHA-512) |
| libqrencode 4.1.1 | https://github.com/fukuchi/libqrencode commit `715e29fd4cd71b6e452ae0f4e36d917b43122ce8` | tree `ceec5110f2bd582f990b36480046c6d829213461` |
| vcpkg ports `lzo`, `libqrencode`, `vcpkg-cmake`, `vcpkg-cmake-config`, `scripts/cmake`, `scripts/buildsystems`, `triplets/x64-windows.cmake` (the build scripts) | https://github.com/microsoft/vcpkg commit `2d6a6cf3ac9a7cc93942c3d289a2f9c661a6f4a7` (MIT) | tree hashes in `packaging/pins.json` |

The Qt binaries are The Qt Company's official MSVC 2022 x64 build of 6.10.2 (Prism's CI installs them with
aqtinstall; `Qt6Core.dll` reports "Qt 6.10.2 ... by MSVC 2022"), so the official source tarballs are their source.

## 4. Fabric API (`Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar`)

Fabric API 0.116.17+1.21.1 by FabricMC, **Apache-2.0**, shipped unmodified (SHA-512 `98c47821...411857a8`, the
Modrinth/Fabric Maven release). Its licence is inside the jar (`LICENSE-fabric-api`) and in
`licenses/Apache-2.0.txt`. Source: https://github.com/FabricMC/fabric (tag `0.116.17+1.21.1`).

## 5. Inside `bin/cthextract.exe`

`cthextract.exe` is a self-contained single-file .NET 10 program. Everything compiled into it is listed below;
the build lists the exact set again in `licenses/extractor/nuget-packages.txt`, read from the executable itself.

### 5.1 .NET runtime

Microsoft .NET runtime 10.0.12 (`Microsoft.NETCore.App`, win-x64), **MIT**, Copyright (c) .NET Foundation and
Contributors. Its licence and its own third-party notices are in `licenses/dotnet/` (taken from the runtime pack the
exe was built with). Source: https://github.com/dotnet/dotnet.

### 5.2 Vendored source libraries (`extractor/vendor/<name>/`, each with its `LICENSE` and `VENDORED.md`)

| Library | Upstream, commit | Licence | Changes (details in `VENDORED.md`) |
|---|---|---|---|
| Reclaimer (`Reclaimer.Core`, `Reclaimer.Blam`) | https://github.com/Gravemind2401/Reclaimer `36224d7a` (v2.1.1644) | **GPL-3.0** (this is why the extractor is GPL-3.0) | stripped (xcompress DLLs, signing keys, exporter scripts; Reach/H4 AES key strings zeroed), patched |
| CUE4Parse + CUE4Parse-Conversion | https://github.com/FabianFG/CUE4Parse `1e3293d4` + `5ca634b5` | Apache-2.0, with upstream's `NOTICE` | stripped (native DLLs, game key files and other games' hard-coded keys zeroed, MS-PL `CRC32.cs`, downloaders, OodleSharp/Oodle.NET/Zlib-ng.NET), patched |
| Fmod5Sharp | https://github.com/SamboyCoding/Fmod5Sharp `28951c6f` | MIT, with upstream's `NOTICE` (vgmstream, ISC) | lazy reads; Vorbis setup table regenerated with libvorbis 1.3.7 / libogg 1.3.5 |
| MeshDecimator | https://github.com/Whinarn/MeshDecimator `a1e85d43` | MIT (includes Sven Forstmann's MIT Fast-Quadric-Mesh-Simplification) | none (unmodified sources) |
| gta-toolkit (3 files) | https://github.com/Neodymium146/gta-toolkit `f287c66a` | MIT (per-file headers, copied into its `LICENSE`) | stripped/patched; holds only SHA1 hashes of key material, no keys |
| ooz (ported to C#, `CthExtract.Oodle`; notes in `extractor/vendor/ooz`) | https://github.com/powzix/ooz `05038060` (v7.0) | **GPL-3.0-or-later** | function-by-function port of the decoder, with bounds checks |
| vgmstream Bink Audio decoder (ported to C#, `VgmstreamBinka`, in `extractor/vendor/vgmstream-binka`) | https://github.com/vgmstream/vgmstream `7c520e8f` | ISC-style (vgmstream `COPYING`) | port of `binka_dec`, `binka_transform`, `ueba` |

All of them are GPL-3.0-compatible. In the bundle: `licenses/extractor/vendor/<name>/` (each library's `LICENSE`,
plus `NOTICE` where upstream has one, and its `VENDORED.md`).

#### 5.2.1 What each `VENDORED.md` records

Summarised from `extractor/vendor/<name>/VENDORED.md` (the full record, with the exact patch, ships next to each licence).

- **Reclaimer** (GPL-3.0; upstream `LICENSE` copied unchanged). Commit `36224d7a4581f2a1f255099bf706aa56be9f6e21`
  (tag `v2.1.1644`). Projects `Reclaimer.Core` and `Reclaimer.Blam` only; rebuilt from a clone by `vendor.py`.
  *Stripped:* the proprietary Microsoft `xcompress32/64.dll`, the strong-name keys `*.snk`, `Reclaimer.Blam.ruleset`,
  the 3ds Max/Blender exporter scripts. *Patched:* `net10.0` + C# 13, no WPF (`#if RECLAIMER_WPF` around the WPF-only
  code), unsigned; all 18 read sites open game files with `FileShare.ReadWrite | FileShare.Delete`; the helper that
  opened a map for writing (`CacheFactory.CreateWriter`) removed; the hard-coded Halo: Reach / Halo 4 cache AES key
  strings zeroed (no game key is shipped; Halo 3 never uses them). Used by `CthExtract.Halo`.
- **CUE4Parse + CUE4Parse-Conversion** (Apache-2.0; `LICENSE` and upstream `NOTICE` copied unchanged). Commit
  `1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562` plus upstream `5ca634b56a2f67ab8531d21d24be0eabe2d23843` ("Some FN fixes");
  rebuilt by `vendor.py` + `cthextract.patch`. *Game keys:* the hard-coded decryption keys, key tables, key
  strings and XOR keys of ~40 other games (and the FMOD FSB5 / CRI HCA keys listed in doc comments) are zeroed by
  `vendor.py` before the patch is applied, so neither the repository nor `cthextract.exe` carries a game key; those
  games' data no longer decrypts, the Fortnite path is unchanged. *Stripped:* `ProSpiDecryptor.dll.gz`, `ACE7Key.bin`,
  `PUBGNameHashMap.json`, `BL4SoundTagsMap.json`, `ShaderHashedNames.json`, the embedded `Detex.dll`, `crunch.dll`,
  `tegra_swizzle_x64.dll`, the MS-PL `CRC32.cs` (replaced by `System.IO.Hashing.Crc32`), the download helpers
  `OodleHelper`, `ZlibHelper`, `UnluacHelper`, `HttpUtils`, and the packages OodleSharp, Oodle.NET, Zlib-ng.NET.
  *Patched:* Oodle only through a registered decoder (`Compression.UseOodle`, ours: `CthExtract.Oodle`); the IoStore
  on-demand downloader and the Rocket League key download make no network requests; no native texture library is
  written or loaded (managed AssetRipper decoder by default); SkiaSharp without its native assets; `Fmod5Sharp` is a
  project reference to the vendored copy below. Left as upstream: the decryption *algorithms* for other games under
  `CUE4Parse/GameTypes/*/Encryption` (their keys zeroed as above), which only run when CUE4Parse is told to read those
  games; the Fortnite path never does. Used by `CthExtract.Fortnite`.
- **Fmod5Sharp** (MIT; `LICENSE` and `NOTICE` with vgmstream's ISC text copied unchanged). Commit
  `28951c6fb625522dcac4a129e28a53f33b6a3ba8` (NuGet 3.1.0 + IMA ADPCM fix); `fmod5sharp-cthextract.patch` is the full
  diff. *Changed:* `net10.0` only; lazy per-sample reads with shared, read-only file opens; clearer missing-setup-header
  errors; the PCM/WAV rebuilder returns finished files; a thread race in `InitBlockFlags` fixed; setup headers found in
  the player's own install can be registered for one run. *Replaced:* the Vorbis setup-header table that upstream
  dumped from an FMOD binary is **not shipped**; ours (247 entries) is generated from libvorbis 1.3.7 + libogg 1.3.5
  (BSD-3-Clause, Xiph.Org) by `tools/regen_vorbis_table.py` / `tools/vorbis_setup_sweep.c` (ours, GPL-3.0). Used by
  `CthExtract.Halo`, and by CUE4Parse (one Fmod5Sharp assembly per exe).
- **MeshDecimator** (MIT, Copyright (c) 2017-2018 Mattias Edlund; `LICENSE.md` unchanged; includes Sven Forstmann's MIT
  Fast-Quadric-Mesh-Simplification notice in `FastQuadricMeshSimplification.cs`). Commit
  `a1e85d43c0bea241f1952e7c093f83bb8d3ad1d8`. Library sources copied byte for byte; only the old project file,
  `AssemblyInfo.cs`, the OBJ tool and the Unity example were left out. No patches. Used by `CthExtract.Core` (`Decimate`).
- **gta-toolkit** (MIT, Copyright (c) 2015 Neodymium, carried as a per-file header and copied into `LICENSE`). Commit
  `f287c66a38315fa6eb36b721fc090c62285ead1d`. Three files: `GTA5Constants.cs` stripped to `GTA5HashConstants` (SHA1
  hashes only, "There is NO key"), `GTA5Encryption.cs` and `GTA5Hash.cs` patched to take the tables/LUT as parameters;
  algorithms unchanged; BOM/CRLF normalised. Used by `CthExtract.Gta` (key verification, NG key index, reference cipher).
- **ooz** (GPL-3.0-or-later; the repository has no `LICENSE` file, so `LICENSE` is the FSF GPL-3.0 text). Commit
  `05038060aa68f9187ae9923b2388ca8db40e58d1` (tag `v7.0`). No C++ file is in the repository: `CthExtract.Oodle` is a
  function-by-function C# port of the decoder half of `kraken.cpp` (Kraken, Mermaid/Selkie, Leviathan; LZNA and
  BitKnit left out), with fixes from the GPL-3.0 fork https://github.com/zao/ooz (`f9583cb`, `02bdf0f`, `dd51c63`)
  and bounds checks marked `[CTH]` so corrupt input cannot crash it. Each ported file keeps "Copyright (C) 2016,
  Powzix" and the GPL notice. Never uses, loads or downloads an Oodle library. Used by `CthExtract.Fortnite`.
- **vgmstream Bink Audio decoder** (ISC-style; `LICENSE` = vgmstream's `COPYING`, unchanged). Commit
  `7c520e8f9b078dfde2f84f1b5bcbad0c6c415806`. C# port of `binka_dec.c/.h`, `binka_transform.c`, `binka_data.h`, the
  read side of `bitstream_lsb.h`, `ueba.c` and the packet framing of `binka_decoder.c` (`VgmstreamBinka`); tables
  printed from the C header by `tools/dump_tables.c` (ours). vgmstream's decoder is its authors' own reverse-engineered
  implementation; no RAD or Epic code is involved. Used by `CthExtract.Fortnite`.

### 5.3 NuGet packages compiled in

Full licence texts and notices: `licenses/extractor/nuget-packages.txt` (generated from the exe at build time).

| Package | Version | Licence | Copyright / author | Project |
|---|---|---|---|---|
| AssetRipper.TextureDecoder | 2.6.2 | MIT | Copyright (c) 2022 - 2026 ds5678 | https://github.com/AssetRipper/TextureDecoder |
| BCnEncoder.Net | 2.3.0 | MIT OR Unlicense (used under MIT) | Nominom | https://github.com/Nominom/BCnEncoder.NET |
| Blake3 | 3.0.2 | BSD-2-Clause | Alexandre Mutel | https://github.com/xoofx/Blake3.NET |
| BouncyCastle.Cryptography | 2.6.2 | MIT | Copyright (c) Legion of the Bouncy Castle Inc. 2000-2025 | https://www.bouncycastle.org |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | (c) .NET Foundation and Contributors | https://github.com/CommunityToolkit/dotnet |
| FixedMathSharp | 4.0.1 | MIT | Copyright (c) 2024-present David Oravsky | https://github.com/mrdav30/FixedMathSharp |
| GenericReader | 2.3.0 | MIT | Copyright (c) 2025 NotOfficer | https://github.com/NotOfficer/GenericReader |
| Infrablack.UE4Config | 0.7.2.97 | MIT | Patrick Michael Hopf (C) 2020 | https://github.com/Wortex17/UE4Config |
| K4os.Compression.LZ4, K4os.Compression.LZ4.Streams | 1.3.8 | MIT | Milosz Krajewski | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 |
| K4os.Hash.xxHash | 1.0.8 | MIT | Milosz Krajewski | https://github.com/MiloszKrajewski/K4os.Hash.xxHash |
| LZMA-SDK | 22.1.1 | MIT | (c) 2023 Mihir Mone (LZMA SDK by Igor Pavlov, public domain) | https://github.com/monemihir/LZMA-SDK |
| MemoryPack.Core | 1.21.4 | MIT | Cysharp, Inc. | https://github.com/Cysharp/MemoryPack |
| Microsoft.Win32.SystemEvents | 10.0.9 | MIT | Microsoft Corporation | https://github.com/dotnet/runtime |
| NAudio.Core | 2.3.0 | MIT | Mark Heath | https://github.com/naudio/NAudio |
| Newtonsoft.Json | 13.0.4 | MIT | Copyright (c) James Newton-King 2008 | https://www.newtonsoft.com/json |
| NVorbis | 0.10.5 | MIT | Copyright (c) 2020 Andrew Ward | https://github.com/NVorbis/NVorbis |
| OffiUtils | 3.2.0 | MIT | Copyright (c) 2026 NotOfficer | https://github.com/NotOfficer/Utils |
| OggVorbisEncoder | 1.2.2 | MIT; a C# port of libvorbis, so also the Xiph.Org BSD-3-Clause notice | Steve Lillis; Xiph.Org Foundation | https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder |
| Serilog, Serilog.Sinks.Console | 4.3.1, 6.1.1 | Apache-2.0 | Serilog Contributors | https://serilog.net |
| SharpGLTF.Core, SharpGLTF.Runtime, SharpGLTF.Toolkit | 1.0.6 | MIT | Copyright (c) 2025 Vicente Penades | https://github.com/vpenades/SharpGLTF |
| SixLabors.ImageSharp | 3.1.12 | Six Labors Split License 1.0: Apache-2.0 for use in open-source software such as this GPL-3.0 extractor | Six Labors | https://github.com/SixLabors/ImageSharp |
| SkiaSharp (managed part only; no native library is shipped) | 2.88.9 | MIT | Microsoft Corporation | https://github.com/mono/SkiaSharp |
| SubstreamSharp | 1.0.3 | MIT | Connor Haigh 2021 | https://github.com/connorhaigh/SubstreamSharp |
| System.Drawing.Common | 10.0.9 | MIT | Microsoft Corporation | https://github.com/dotnet/winforms |
| System.IO.Hashing | 10.0.8 | MIT | Microsoft Corporation | https://github.com/dotnet/runtime |
| System.Numerics.Tensors | 10.0.10 | MIT | Microsoft Corporation | https://github.com/dotnet/runtime |
| VGAudio | 2.2.1 | MIT | Copyright (c) 2016 Alex Barney | https://github.com/Thealexbarney/VGAudio |
| ZstdSharp.Port | 0.8.8 | MIT | Oleg Stepanischev | https://github.com/oleg-st/ZstdSharp |

Xiph.Org BSD-3-Clause notices for libvorbis 1.3.7 and libogg 1.3.5 (OggVorbisEncoder; Fmod5Sharp's regenerated
Vorbis setup table): `licenses/extractor/xiph-libvorbis-1.3.7-COPYING.txt`, `xiph-libogg-1.3.5-COPYING.txt`.

Deliberately **not** shipped and never downloaded: OodleSharp, Oodle.NET, Zlib-ng.NET, Epic's `oo2core`/
`oodle-data-shared` libraries, Detex/crunch/tegra_swizzle, ProSpiDecryptor, the SkiaSharp native library,
RAD's Bink/RAD Audio decoders, CodeWalker code, and any game key or `.usmap` file.

Test-only, never shipped: xunit.v3 3.2.2 and xunit.runner.visualstudio 3.1.5 (Apache-2.0), Microsoft.NET.Test.Sdk
18.6.0 (MIT); CUE4Parse's own Apache-2.0 test fixtures, read from a local clone (`CTH_REFS`) by the Fortnite tests
and `extractor/tests/e2e.sh`, never copied into the repository; `fabric/tools/CthSynth` (ours, the mod's synthetic
cache tool). Build-time only, not linked: libvorbis 1.3.7 and libogg 1.3.5 (BSD-3-Clause) to generate Fmod5Sharp's
setup-header table.

### 5.4 Data the extractor downloads at run time (not shipped)

On the player's PC, while it reads their games, `cthextract.exe` may fetch:

- **Fortnite:** the current game version's public AES keys and `.usmap` property mappings, from
  `api.fortniteporting.app`, `uedb.dev` and `fortnite-api.com` (cached in `cache/keys/fortnite`). Without network
  access the Fortnite part is reported as missing and retried later.
- **GTA V:** CodeWalker's `magic.dat` (`raw.githubusercontent.com/dexyfex/CodeWalker/485d56be.../magic.dat`,
  SHA-256 `dc35981f822e892ced3aa81d31e7a96927d573ee28f67417592b5afeaf330832`), held in memory only; every key and
  table decoded from it must match gta-toolkit's SHA1 list before use. The player's own AES key is read from their
  `GTA5.exe` and cached in `cache/keys`.

These files are not part of the release and are not redistributed by us. With `--offline` nothing is downloaded
(Fortnite uses only keys cached by an earlier run; GTA V reports its NG-encrypted items as missing).

## 6. Source code

`SOURCE.txt` (in the bundle) gives the exact commit of this repository and the upstream source of every GPL/LGPL
program, as described in section 3.1. The corresponding source of `cthextract.exe` is the `extractor/` folder of
this repository at that commit, including `extractor/vendor/` and `extractor/build.sh`.

## 7. Trademarks

Craft Theft Halo is a fan project. It is not affiliated with or endorsed by Mojang Studios or Microsoft
(Minecraft), 343 Industries / Halo Studios or Microsoft (Halo), Rockstar Games or Take-Two Interactive (Grand Theft
Auto), or Epic Games (Fortnite). All trademarks belong to their owners. Players need their own legitimate copies of
all four games.
