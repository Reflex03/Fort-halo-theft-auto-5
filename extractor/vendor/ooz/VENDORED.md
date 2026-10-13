# ooz (ported to C#, not vendored as source)

| | |
|---|---|
| Upstream | https://github.com/powzix/ooz |
| Commit | `05038060aa68f9187ae9923b2388ca8db40e58d1` (tag `v7.0`, 2019-02-11; the upstream HEAD) |
| Licence | GPL-3.0-or-later (header of `kraken.cpp`: "either version 3 of the License, or (at your option) any later version"). The repository has no LICENSE file; `LICENSE` here is the unmodified GPL-3.0 text from the FSF. |
| Port | `extractor/src/CthExtract.Oodle` (namespace `CthExtract.Oodle`), tests in `extractor/tests/CthExtract.Oodle.Tests` |
| Used by | the Fortnite module, to decode Oodle-compressed IoStore/pak blocks without any native code (register `Ooz.TryDecompress` with CUE4Parse's `Compression.UseOodle`) |

No C++ file from ooz is in this repository. `CthExtract.Oodle` is a function-by-function C# translation of the
decoder half of ooz's `kraken.cpp`, so it is a derivative work and is distributed under GPL-3.0-or-later, the same
licence as the extractor. Each ported file carries the ooz copyright line ("Copyright (C) 2016, Powzix") and the
GPL notice.

The port was written only from ooz's GPL source (plus the zao/ooz fixes below). No Oodle SDK source, no OodleSharp
code and no Oodle binary was read or used to write it, and it never uses, loads or downloads `oo2core_*.dll` /
`oodle-data-shared.dll`. (ooz itself is its author's independent open-source decoder; the Oodle SDK source is not
public. Its provenance is discussed in the project's research notes, `fortnite.md` section 1.4.)

## What was ported

From `kraken.cpp`: the block/quantum headers and `Kraken_DecodeStep`/`Kraken_Decompress`; the entropy layer
(`Kraken_DecodeBytes`: raw, Huffman with both code-length formats and the Golomb-Rice length coder, tANS, RLE,
recursive and multi-array blocks); Kraken LZ (`Kraken_ReadLzTable`, `Kraken_UnpackOffsets`, both
`Kraken_ProcessLzRuns` modes); Mermaid/Selkie LZ (one bitstream; `Mermaid_ReadLzTable`, `Mermaid_Mode0/1`); Leviathan
LZ (`Leviathan_ReadLzTable`, `Leviathan_ProcessLz` with all six literal modes and multi-command streams).

Left out: LZNA (`lzna.cpp`) and BitKnit (`bitknit.cpp`), which Unreal Engine does not use (they are detected and
reported as `OozErrorCode.UnsupportedCodec`); `Kraken_CopyWholeMatch` (LZNA only); the command-line tool, file I/O
and Oodle DLL loading; ooz has no compressor of its own.

## Fixes taken from zao/ooz

https://github.com/zao/ooz (GPL-3.0 fork used for Path of Exile bundles), commits on `kraken.cpp`:

- `f9583cb` multi-array blocks can declare up to 63 entropy arrays (ooz has room for 32: stack overflow);
- `02bdf0f` the interval-index array of a multi-array block is always copied (ooz could write into the input);
- `dd51c63` Mermaid's temp area gets 0x4000 more bytes, enough for a tANS table (ooz fails on small Mermaid chunks
  that use tANS);
- `abf4746` (8-byte copies through a temporary) is how C# behaves anyway.

## Changes against ooz (marked `[CTH]` in the source)

ooz says of itself "not fuzz safe, so please trust the input". This port must not crash on a corrupt game file, so
it adds checks. Each one only rejects states that a valid stream never reaches (in ooz they lead to out-of-bounds
reads/writes before ooz's own end-of-chunk checks would reject the stream), so valid streams decode exactly as in
ooz:

1. Memory model: the public API copies the input into a buffer with 64 zero bytes before and after it and decodes
   into a buffer with 64 spare bytes (ooz's `SAFE_SPACE`), then copies the result out. Every over-read and
   over-write the C++ code does (8/16/32-byte copies, 4-byte bit-buffer refills) is bounded well inside those pads,
   and inside a 4 KiB pad after the 0x6C000-byte scratch.
2. LZ copy loops (Kraken, Mermaid, Leviathan): every stream cursor (output, literals, commands, offsets, lengths)
   is checked after each command instead of only after the loop; long literal runs and long matches are compared
   with signed arithmetic (ooz's unsigned compares pass when the cursor is already a few bytes past the end).
3. Kraken long matches: the length must come from inside the length stream and be at least 17 (a stale value read
   past the stream made ooz's unsigned `matchlen -= 8` loop wrap around: found by our guard-page fuzzer).
4. Mermaid: near offsets (`off16`) must not point before the window start; long near/far commands are
   length-checked; the far-offset stream end is `start + size` (ooz adds `size * 4` to a `uint32*`, which disables
   its own check); the final literals must exist.
5. Leviathan: each of the 8 command streams is bounded separately; order-1 literal runs are bounded.
6. Multi-array: the forward/backward varbit readers must not cross by more than their look-ahead; a destination
   in scratch needs at least 0xC000 bytes (ooz's split would move backwards).
7. tANS: table weights are summed in signed arithmetic (ooz's unsigned sum lets an over-full table through and then
   overflows the lookup table); the lookup table is filled with per-quarter bounds checks and must come out complete
   with every state below L; the two bit streams must not cross by more than their look-ahead.
8. RLE / Huffman / Golomb-Rice: as in ooz (already bounded), plus a 512-entry bound on the u32 length array.
9. Entropy blocks may nest (recursive, RLE prefix, multi-array); the depth is limited to 16 (ooz recurses without
   limit; real data nests 1-2 levels).
10. Quantum checksums are not verified. ooz calls `Kraken_GetCrc`, an unimplemented stub returning 0, so it rejects
    every checksummed quantum whose CRC is non-zero; the real CRC algorithm is unknown. Unreal Engine does not
    enable Oodle checksums.
11. An entropy-only chunk stored as a raw (type 0) block is copied to the output; ooz only points at it and leaves
    the output unwritten.
12. `Huff_ReadCodeLengthsOld`/`New`, `Tans_DecodeTable`, `Huff_MakeLut`: unchanged, except that the reversed
    11-bit lookup table is stored as one `ushort` per entry (length | symbol << 8) instead of two byte arrays, and
    `ReverseBitsArray2048` (an SSE2 transpose) is computed as `out[k] = in[reverse11(k)]` (checked exhaustively
    against the C++ routine).

Behaviour kept on purpose: a Huffman block with a single symbol is rejected (ooz returns `src - src_end`), the stream
must be consumed exactly (`TrailingInput`), and an empty output needs an empty input.

## How it was checked (all on Linux x64; no game files involved)

The reference is ooz itself, built with `extractor/tests/CthExtract.Oodle.Tests/tools/build-native-ooz.sh` (g++;
kraken.cpp's decoder compiled as published, only MSVC-intrinsic shims added; a second binary adds zao/ooz's
`dd51c63`). `tools/native-sha.sh` records native output hashes for a folder of blocks.

- **synthetic-test**, real Oodle output: ooz's own `testdata` (12 Silesia files x Kraken, Mermaid, Selkie,
  Leviathan, compressed by RAD's Oodle; 48 vectors, 848 MB decoded) decode to the Silesia originals byte for byte,
  in native ooz and in the port.
- **synthetic-test**, real Unreal Engine blocks: all 1050 Oodle blocks of CUE4Parse's UE5.8/UE6.0 IoStore test
  fixtures (Apache-2.0; cooked by Unreal with Epic's Oodle; all Kraken) decode to the same SHA-256 in native ooz and
  in the port.
- **synthetic-test**, generated vectors: 1478 streams made locally with the compressor of the rarten/ooz fork
  (Kraken levels -1..5, Mermaid -1..5, Selkie -4..6, Leviathan 1..5; 70 inputs from 1 byte to 51 MB: Silesia files,
  sizes around the 64/128/256 KiB boundaries, random, runs, small alphabets, ...). Native ooz and the port both
  reproduce every input. That compressor's files say "This file is not GPL. It may be used for educational purposes
  only", so neither it nor anything it produced is in this repository; it was only run locally as a vector
  generator. Together these three sets execute 87.7 % of the port's lines, including every decoding path real
  Oodle data uses (tANS, both Huffman length formats, Golomb-Rice, full multi-array, all Leviathan modes).
- **synthetic-test**, our own encoder (`tests/CthExtract.Oodle.Tests/Synth/`, written from the decoder source so the
  committed tests need no external data): 2700 streams over every literal mode, entropy coder (incl. tANS,
  Golomb-Rice lengths, full multi-array), offset coding and framing variant, plus 13 MB windows. Native ooz with
  `dd51c63` reproduces all of them; unmodified ooz fails 4 small Mermaid chunks that use tANS/Huffman (the bug that
  commit fixes). The committed tests alone (no external data) execute 94 % of the port's lines and 85 % of its
  branches.
- **synthetic-test**, fuzzing: mutated streams (bit flips, splices, truncations, wrong sizes, random structured
  streams) with the input, the output and the decoder's memory each placed against `PROT_NONE` guard pages:
  about 5.4 million iterations over all of the above vector sets without a crash, hang or managed exception (the slowest
  single decode took 98 ms, on a loaded machine). One real bug was found this way and fixed (change 3 above).
- **synthetic-test**, speed (best of 15, Release, this 4-core VM; native = the reference build with g++ -O2):

  | Stream (ooz testdata) | Port | Native ooz |
  |---|---|---|
  | dickens.kraken | 264 MB/s | 282 MB/s |
  | mozilla.kraken | 367 MB/s | 360 MB/s |
  | x-ray.kraken | 171 MB/s | 197 MB/s |
  | webster.kraken | 278 MB/s | 273 MB/s |
  | ooffice.kraken | 282 MB/s | 293 MB/s |
  | dickens.mermaid | 599 MB/s | 627 MB/s |
  | mozilla.mermaid | 786 MB/s | 643 MB/s |
  | dickens.selkie | 861 MB/s | 765 MB/s |
  | mozilla.selkie | 1189 MB/s | 889 MB/s |
  | dickens.leviathan | 222 MB/s | 235 MB/s |
  | mozilla.leviathan | 284 MB/s | 299 MB/s |
  | x-ray.leviathan | 179 MB/s | 204 MB/s |
  | 1050 Unreal blocks (avg 17 KiB) | 705 MB/s | - |

  The port runs at 0.87-1.34x the speed of the C++ build. Absolute numbers depend on the CPU (this VM is slow even
  for native ooz); the ratio to native is the useful figure.
- Not checked: Windows or ARM64 builds (no such machine here); real Fortnite pak/IoStore files (not available);
  streams with Oodle checksums from a real encoder.
