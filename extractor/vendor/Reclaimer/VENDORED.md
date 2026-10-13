# Reclaimer (vendored, patched)

| | |
|---|---|
| Upstream | https://github.com/Gravemind2401/Reclaimer |
| Commit | `36224d7a4581f2a1f255099bf706aa56be9f6e21` (tag `v2.1.1644`, 2026-09-20) |
| Licence | GPL-3.0 (`LICENSE`, copied unchanged). The extractor is GPL-3.0 because it links this code. |
| Projects | `Reclaimer.Core`, `Reclaimer.Blam` only (no WPF app, no plugins, no tests, no RMF importer) |
| Used by | `extractor/src/CthExtract.Halo` (MCC Halo 3 `.map` reading, `mode` geometry, `bitm` decoding) |

## How this copy is made

`python3 -I vendor.py <clone of the upstream repo>` exports the tracked files of the pinned commit with
`git archive` and applies the patch below. Re-run it to update the pin; review the diff afterwards.

## What was stripped

- `Reclaimer.Blam/Resources/xcompress32.dll`, `xcompress64.dll` (proprietary Microsoft Xbox SDK binaries; Xbox 360
  LZX only, never used for MCC caches, whose resources are Deflate) and the `ContentWithTargetPath` items copying them.
- `Reclaimer.Core.snk`, `Reclaimer.Blam.snk` (strong-name keys; assemblies are built unsigned) and
  `Reclaimer.Blam.ruleset`.
- `Reclaimer.Blam/Resources/3dsMax AMF2.ms`, `Blender AMF2.py` (exporter scripts for other tools, not referenced by
  the build).
- `Reclaimer.Blam/Resources/Halo1BumpPalette.bin`, `Halo2BumpPalette.bin`, `StubbsBumpPalette.bin` (1 KiB binary
  lookup tables for the Xbox P8 bump-map format of Halo 1, Halo 2 and Stubbs; provenance unclear, probably taken from
  those games, and never used for Halo 3), with their `Resources.resx` entries and `Resources.Designer.cs`
  accessors. The two P8-bump decode sites (`Blam/Halo1/BitmapTag.cs`, `Blam/Halo2/BitmapTag.cs`) throw
  `NotSupportedException` instead (removed during integration, 2026-10-13; `vendor.py` reproduces it).

## What was patched

1. Both `.csproj`: `net9.0-windows` → `net10.0` plus `<LangVersion>13</LangVersion>` (C# 14's first-class span
   conversions break the `MemoryMarshal.Cast<T, byte>(array)` calls in `DdsConvert.cs` / `BlamUtils.cs`);
   `<UseWPF>` removed; `SignAssembly` false; `CA1416`, `CS0618`, `CS0162` (upstream obsolete/unreachable code) added to
   `NoWarn`.
2. `Reclaimer.Core/Drawing/DdsConvert.cs`: the WPF usings, the `WriteToDisk`/`WriteToStream`/`ToBitmapSource` region
   and `UnwrapCubemapSource` are wrapped in `#if RECLAIMER_WPF` (never defined). The managed decoders
   (`AsUncompressed`, `Decompress*`) are untouched.
3. `Reclaimer.Core/Drawing/DdsOutputArgs.cs`: whole file wrapped in `#if RECLAIMER_WPF`.
4. `Reclaimer.Core/Geometry/Utilities/SceneWriter.cs`: embedded-PNG export (WPF) throws `NotSupportedException`
   inside the existing `try { } catch { }`. The extractor never writes RMF; one test writes an RMF without embedded
   textures to cross-check MeshBuilder against Reclaimer's own RMF reader.
5. Every `new FileStream(path, FileMode.Open, FileAccess.Read)` (18 sites: cache files, `shared.map` resource pages,
   chunk streams, modules) now passes `FileShare.ReadWrite | FileShare.Delete`, so reading never blocks the game or a
   Steam/Xbox update that holds the file.
6. `Reclaimer.Blam/Blam/Common/CacheFactory.cs`: `CreateWriter(this ICacheFile cache)` removed (it opened the game's
   map for writing). The overloads that take a caller-supplied stream remain.
7. `Reclaimer.Blam/Blam/HaloReach/CacheFile.cs`, `Reclaimer.Blam/Blam/Halo4/CacheFile.cs`: the hard-coded Halo: Reach /
   Halo 4 cache-file AES key strings (`BetaKey`, `FileNamesKey`, `StringsKey`, `LocalesKey`, `NetworkKey`) are zeroed:
   the project never commits or ships a game key. Halo 3 caches are not encrypted and never use them; encrypted
   Reach/H4 tables simply no longer decrypt. (Added by the licence/safety review, 2026-10-13.)

`tests/CthExtract.Halo.Tests/VendorTests.cs` fails the build if a read-write open, a non-shared open, a DLL/key
file or a non-zero `...Key` string constant comes back.

## Verification

- Builds on Linux for net10.0 (synthetic-test: `dotnet build`, 0 errors; upstream warnings only).
- `ReclaimerMap.Open` on a junk `.map` returns Reclaimer's "not a valid map file" error and releases the file
  (synthetic-test).
- The embedded MCC Halo 3 string-id and vertex-buffer XML load from the Linux-built assembly (synthetic-test).
- Real MCC caches: **untested here** (no game files in this environment). The unpatched upstream reads MCC Halo 3
  (cache type `MccHalo3U13`) per the creator report cited in the research notes.
