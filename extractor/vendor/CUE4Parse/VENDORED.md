# CUE4Parse + CUE4Parse-Conversion (vendored, stripped, patched)

| | |
|---|---|
| Upstream | https://github.com/FabianFG/CUE4Parse |
| Commit | `1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562` (2026-10-01, = NuGet `CUE4Parse` / `CUE4Parse-Conversion` 1.2.2.202610) **plus** upstream commit `5ca634b56a2f67ab8531d21d24be0eabe2d23843` "Some FN fixes" (2026-10-04) |
| Licence | Apache-2.0 (`LICENSE`, copied unchanged). `NOTICE` is upstream's, copied unchanged (Apache-2.0 §4(d): ship it with the extractor). Its DotNetZip, Oodle.NET, Zlib-ng.NET and crunch entries no longer pertain to this copy: that code/those packages were removed (below). |
| Projects | `CUE4Parse`, `CUE4Parse-Conversion` only (no `CUE4Parse-Natives`, no tests, no example) |
| Used by | `extractor/src/CthExtract.Fortnite` (Fortnite IoStore/pak containers, unversioned properties via `.usmap`, meshes, textures, sounds) |

## How this copy is made

`python3 -I vendor.py <clone of the upstream repo>` exports the pinned commit with `git archive`, applies upstream
`5ca634b5`, deletes the stripped files, **neutralises the game key material of other games** (step 4, below) and
applies `cthextract.patch` (our changes, a unified diff against the result of the first four steps, so the patch
itself never shows a key). Re-running it reproduces this folder exactly (checked: `diff -r` identical, 2026-10-13,
after the licence/safety review). To change the vendored code: edit here, run `vendor.py <clone> --no-patch` into
an empty copy of this folder, and regenerate `cthextract.patch` with `diff -ruN <copy> <this folder>`.

Why `5ca634b5` on top of the pin: critique.md C10 — it reorders UE6.0 `FMaterialShaderMap` deserialisation (Fortnite
42.x materials) and fixes four Fortnite types (`UAthenaPickaxeItemDefinition`, `ULevelSaveRecord`,
`UFortPlayerFoleyWaveAssetContainer`, `RemoteServerId`). 51 changed lines, no new dependencies.

## What was stripped

| File | What it was | Why |
|---|---|---|
| `CUE4Parse/Resources/ProSpiDecryptor.dll.gz` | native PE extracted to `%LOCALAPPDATA%/CUE4Parse/Native` and loaded for one game | no source, no licence |
| `CUE4Parse/Resources/ACE7Key.bin` | Ace Combat 7 decryption key table | embedded game key |
| `CUE4Parse/Resources/PUBGNameHashMap.json`, `BL4SoundTagsMap.json` | name/tag maps dumped from other games | embedded game data |
| `CUE4Parse/Resources/ShaderHashedNames.json` | 448 KB of hashed shader names from many games (critique L11) | not needed; names stay hashes |
| `CUE4Parse-Conversion/Resources/Detex.dll`, `crunch.dll`, `tegra_swizzle_x64.dll` | embedded native decoders, written to the working folder and loaded | natives; BCn goes through the managed AssetRipper.TextureDecoder |
| `CUE4Parse/Utils/CRC32.cs` | DotNetZip CRC32, **MS-PL** (critique L1) — not GPL-3.0 compatible | replaced by `System.IO.Hashing.Crc32` (MIT; same function, see `VendorTests.System_io_hashing_crc32_equals_dotnetzip_default_crc`) |
| `CUE4Parse/Compression/OodleHelper.cs` | `Initialize()` downloaded Epic's UE-EULA `oodle-data-shared.dll` from WorkingRobot/OodleUE | never download a native library |
| `CUE4Parse/Compression/ZlibHelper.cs` | downloaded `zlib-ng2.dll.gz` | same; zlib uses `System.IO.Compression` |
| `CUE4Parse/UE4/Lua/unluac/UnluacHelper.cs` | downloaded `unluac.dll.gz` | same (Lua decompilation is not used) |
| `CUE4Parse/Utils/HttpUtils.cs` | the shared download `HttpClient` of the three helpers | no network code |
| Packages `OodleSharp`, `Oodle.NET`, `Zlib-ng.NET` | managed Oodle port "vibe ported from the oodle c++ sdk" (UE-EULA provenance, critique L2) and two P/Invoke wrappers | Oodle now goes through a hook implemented by our clean-room ooz port |

## What was patched (`cthextract.patch`)

1. `CUE4Parse/Compression/Compression.cs`: no default Oodle decoder. New `Compression.UseOodle(DecompressDelegate)` and
   `IsOodleConfigured`; until a decoder is registered an Oodle block throws `FileLoadException("... no Oodle decoder was
   registered ...")`. `UseNativeOodle` / `UseNativeZlib` removed. The extractor registers
   `CthExtract.Oodle.Ooz.Decompress` (`src/CthExtract.Fortnite/Unreal/OodleBridge.cs`).
2. `CUE4Parse/UE4/IO/IoChunkToc.cs`: the IoStore on-demand downloader no longer has an `HttpClient` (upstream streamed
   chunks from Epic's CDN, optionally with the user's bearer token). It only serves chunks already in the local cache
   folder and otherwise throws `FileNotFoundException`, so content the player has not installed is reported missing.
3. `CUE4Parse/GameTypes/RL/Encryption/Aes/RLAes.cs`: no key download from a third-party GitHub file (built-in key only).
4. `CUE4Parse/GameTypes/FN/Assets/Exports/ULevelSaveRecord.cs`: `CRC32` → `System.IO.Hashing.Crc32.HashToUInt32`.
5. `CUE4Parse/GameTypes/ProSpi/Encryption/Aes/ProSpiCipherDLL.cs`: never extracts or loads the (stripped) DLL.
6. `CUE4Parse-Conversion/Textures/TextureNativeLibrary.cs`: throws `NotSupportedException` instead of writing
   `crunch.dll` / `tegra_swizzle_x64.dll` to the working folder (Switch/crunched textures only).
7. `CUE4Parse-Conversion/Textures/TextureDecoder.cs`: `UseAssetRipperTextureDecoder` defaults to **true** (managed BCn;
   upstream's Windows default needed Detex.dll and threw for BC7).
8. `CUE4Parse/UE4/Assets/Exports/Material/HashedNamesProvider.cs`: no error log when `ShaderHashedNames.json` is absent.
9. `CUE4Parse/CUE4Parse.csproj`: no CMake native build, no embedded resources, no OodleSharp/Oodle.NET/Zlib-ng.NET;
   `Fmod5Sharp` is a project reference to `extractor/vendor/Fmod5Sharp` (one Fmod5Sharp per exe; its Vorbis table is
   regenerated from libvorbis, see that folder's VENDORED.md).
10. `CUE4Parse-Conversion/CUE4Parse-Conversion.csproj`: no embedded natives; `SkiaSharp` with
    `ExcludeAssets="native"` (checked: a win-x64 single-file publish of the module has no `libSkiaSharp.dll` next to
    it; our code path is `UTexture.Decode -> CTexture`, never Skia).
11. `Directory.Build.props` / `Directory.Packages.props`: replace upstream's (no packing/SourceLink; upstream warnings
    hidden; the dropped packages removed from central versions).

## Game keys of other games: neutralised (vendor.py step 4)

Upstream carries hard-coded decryption keys, key tables, key strings/salts and per-game XOR keys for some 40 other
games (Arena Breakout, PUBG / PUBG Mobile, Valorant Source, Rocket League, The Finals / ARC Raiders, NFS Mobile,
Neverness to Everness, Snowbreak, Undawn, NetEase, ...), the FMOD FSB5 keys of ~50 games in a doc comment and CRI HCA
keys in another. The project never commits or ships game keys (they were also compiled into `cthextract.exe`), so
`vendor.py` zeroes them in the 52 files listed in its `KEY_FILES` table, with fixed rules: every bracketed list of 4+
numbers becomes zeros; every hex number of 8+ digits becomes a small index of the same C# literal type (so `switch`
arms stay distinct); hex-digit strings, key strings (8+ characters, no whitespace) and `GetBytes("...")` literals
become zeros; the FSB5 key list keeps the game names only. The four core files (`FIoStoreTocResource.cs`,
`FPakInfo.cs`, `CriWareProvider.cs`, `Fsb5Decryption.cs`) are touched only where another game's key sits; the
Fortnite path is unchanged (the Fortnite tests on the IoStore/pak and Oodle fixtures pass: 95 tests, 93 pass + 2 skipped by design, 2026-10-13). Those
games' encrypted data simply no longer decrypts. Left as they are: algorithm constants (AES/SM4/ZUC/HCA/BC tables,
ChaCha/Salsa constants, CRC/FNV/CityHash, magic numbers) and 32-bit-or-smaller format constants.
`VendorTests.Vendored_sources_and_patch_hold_no_game_keys` and `Compiled_parser_carries_no_hex_key_strings` fail if
key material comes back in the sources, the patch or the compiled assembly. (Verified: synthetic-test / scan.)

`Detex.cs` / `Unluac.cs` P/Invoke wrappers remain but can only load a library from a path a caller passes; nothing in
the extractor does.

## Guards

`extractor/tests/CthExtract.Fortnite.Tests/VendorTests.cs` fails when: CUE4Parse/CUE4Parse-Conversion reference any
`System.Net.*` type; `OodleHelper`, `ZlibHelper`, `HttpUtils`, `UnluacHelper`, `CRC32` or `UseNativeOodle` come back;
any vendored assembly references OodleSharp/Oodle.NET/Zlib-ng.NET or embeds a `.dll/.gz/.bin/.so/.exe` or a hash map;
an Oodle/zlib-ng/Skia/Detex/ProSpi binary lands in the build output; a stripped file reappears; or the vendored or module
sources call a download/initialise helper. The module's own HTTP (keys + mappings) goes through `HttpGuard`
(allow-listed hosts, never a `.dll/.so/.exe/.zip/.gz/...` path, redirects re-checked).

## Verification

- synthetic-test: builds for net10.0 on Linux; the Fortnite module's tests (95 after the licence/safety review: 93 pass + 2 skipped by design) run on CUE4Parse's own UE6.0 fixtures
  (uncompressed, zlib-free IoStore + pak; Oodle-compressed and AES-encrypted IoStore through `CthExtract.Oodle`):
  meshes, textures (BC1/BC3/BC7/BGRA8/G8 against the fixture's source PNGs), blueprints, sounds, registry.
- Real Fortnite containers: **untested here** (no game files in this environment).
