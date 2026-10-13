# Fmod5Sharp (vendored, patched)

| | |
|---|---|
| Upstream | https://github.com/SamboyCoding/Fmod5Sharp |
| Commit | `28951c6fb625522dcac4a129e28a53f33b6a3ba8` (= NuGet 3.1.0 + IMA ADPCM fix) |
| Licence | MIT (`LICENSE`, copied unchanged); `NOTICE` (vgmstream ISC notice) copied unchanged |
| Projects | `Fmod5Sharp` only (no tests, no BankExtractor, no HeaderGenerator); `tools/` is ours (GPL-3.0, like the extractor) |
| Used by | `extractor/src/CthExtract.Halo` (MCC `halo3\fmod\pc\sfx.fsb`) |

The assembly name stays `Fmod5Sharp` and the public API stays source-compatible with 3.1.0, so another module of the
extractor (CUE4Parse references the Fmod5Sharp NuGet package) can be pointed at this project instead of the package;
one exe must not carry two `Fmod5Sharp` assemblies.

## What was changed

1. `Fmod5Sharp.csproj`: `net10.0` only; SourceLink, package generation and the `Microsoft.Bcl.Memory` /
   `System.Text.Json` package references (in-box on net10.0) removed. `NAudio.Core` 2.3.0 and `OggVorbisEncoder`
   1.2.2 (both MIT) kept.
2. Lazy per-sample reads (`FsbLoader.cs`, `FmodTypes/FmodSample.cs`, `FmodTypes/FmodSoundBank.cs`):
   - new `FsbLoader.LoadFsbFromStreamLazy(Stream, leaveOpen)` and `LoadFsbFromFileLazy(path)` parse only the header,
     the sample headers and the name table; each sample reads its bytes from the stream when
     `FmodSample.SampleBytes` is used (nothing cached). The file is opened `FileAccess.Read` with
     `FileShare.ReadWrite | FileShare.Delete`;
   - `FmodSample.SampleBytes` became a property (field before); new `DataOffset`, `DataLength`, `IsLazy`;
   - `FmodSoundBank` is `IDisposable` (releases the stream of a lazy bank);
   - the name table is read once instead of one seek per sample;
   - a bank whose declared sizes run past the end of the stream, or a sample with a bad data range, is rejected.
   The eager `LoadFsbFromByteArray` / `LoadFsbFromStream` API behaves as before.
3. `CodecRebuilders/FmodVorbisRebuilder.cs`: a missing Vorbis setup header throws a `KeyNotFoundException` naming the
   crc32 and sample (was a bare dictionary lookup); new `GetSetupHeaderCrc(sample)`, `HasSetupHeader(crc)`,
   `SetupHeaderCount`.
4. `CodecRebuilders/FmodPcmRebuilder.cs`: the WAV writer is closed before the bytes are taken and only the used part
   of the buffer is returned (upstream returned `GetBuffer()` with unfinished RIFF sizes).
5. `RegisterSetupHeader(crc32, headerBytes, seekBit)` (in `FmodVorbisRebuilder`): lets the extractor add, for one
   run, a setup header it found in the player's own install (see below).
6. Thread safety (`Util/FmodVorbisData.cs`, `FmodVorbisRebuilder.cs`): upstream's `InitBlockFlags` set its
   "initialized" flag before computing the flags, so two threads rebuilding samples with the same setup header could
   read empty flags (`IndexOutOfRangeException` in `GetPacketBlockSize`; reproduced here with parallel tests). The flags
   are now computed under a lock, and the header table is loaded once under a lock into a `ConcurrentDictionary`.

`fmod5sharp-cthextract.patch` is the full diff of the code against upstream (the table is listed separately below).

## The Vorbis setup-header table (`Util/vorbis_headers_converted.json`) — replaced

FSB5 stores only the crc32 of each sample's Vorbis setup header, so a rebuild needs a table of setup headers.
Upstream ships 161 entries that python-fsb5 dumped from a proprietary FMOD binary (critique.md L4: the MIT label does
not license FMOD's data). **This copy ships none of that data.** The table here is generated from reference
**libvorbis 1.3.7 + libogg 1.3.5 (BSD-3-Clause)** by our own script:

```
python3 -I tools/regen_vorbis_table.py <empty work dir> [--compare <upstream table>]
```

- `tools/vorbis_setup_sweep.c` encodes nothing; it asks libvorbis for the setup header of every 1–2 channel VBR mode at
  8/11/11.025/12/16/22.05/24/32/44.1/48/88.2/96 kHz and qualities -0.1…1.0 in 0.01 steps, de-duplicates by crc32 and
  takes the mode-count bit offset (`seekBit`) from libvorbis' own mode count. Result: **247 entries**.
- Checked once here with `--compare` against upstream's table: **107 of its 161 entries are byte-identical**
  (header bytes and seekBit) to entries of ours, **0 differ**; the other 54 are FMOD-specific setups that no libvorbis
  version/mode we tried produces (1.2.3, 1.3.2, 1.3.7; VBR, ABR, coupling off, quality steps of 0.0001).
- Runtime fallback for those: the extractor looks for missing setup headers in the binaries of the player's own MCC
  install (FMOD has to carry them to play the game's banks): every `\x05vorbis` occurrence is followed with a running
  crc32 until it matches a wanted crc, and the header's `seekBit` is found from the packet end
  (`CthExtract.Halo.VorbisSetup`). Found headers are registered for that run only; nothing is written or shipped.
  Unverified on a real install: which binary holds them, and whether MCC's banks use any of the 54 at all
  (the extractor logs coverage per bank on the first real run).
- Tests: every shipped entry's crc32 and seekBit are re-checked; the seekBit finder reproduces all 161 upstream
  seekBits (test skips when the upstream clone is absent); a synthetic bank whose setup is not in the table is rebuilt
  after the header is found inside a fake binary.
