# vgmstream Bink Audio decoder, C# port (vendored)

| | |
|---|---|
| Upstream | https://github.com/vgmstream/vgmstream |
| Commit | `7c520e8f9b078dfde2f84f1b5bcbad0c6c415806` |
| Licence | ISC-style (`LICENSE` = vgmstream's `COPYING`, copied unchanged). The port keeps that licence and its copyright notice. |
| Ported files | `src/coding/libs/binka_dec.c` + `binka_dec.h` → `BinkaDecoder.cs`; `src/coding/libs/binka_transform.c` → `BinkaTransform.cs`; `src/coding/libs/binka_data.h` → `BinkaData.cs`; `src/util/bitstream_lsb.h` (read side) → `BitstreamLsb` in `BinkaDecoder.cs`; `src/meta/ueba.c` + packet framing of `src/coding/binka_decoder.c` → `UebaReader.cs` |
| Used by | `extractor/src/CthExtract.Fortnite` (SoundWaves whose format is `BINKA`, i.e. Unreal's Bink Audio 2 "UEBA") |

vgmstream's decoder is described upstream as "reversed from scratch" from binkawin.asi/radutil.dll and explicitly uses
nothing from RAD's or Epic's sources. No RAD/Epic code is involved here either. RAD Audio (`RADA`) has no open decoder and
is not handled.

## How the port was made

- `BinkaTransform.cs` was converted mechanically from `binka_transform.c` (`unsafe` pointers, same statement order) and
  then fixed by hand where C and C# differ: the two `for` loops with comma operators in their conditions
  (`transform_256_a/_b`) became `for (;;)` + explicit assignments + `break` with the same evaluation order; the four
  places where C silently narrows a `double` expression (`0.5 - table[...]`, `* 0.5`) got explicit `(float)` casts on
  the same double expression; `const` locals became plain locals; `int indexes[64]` became `stackalloc`.
- `BinkaData.cs`: the float tables were printed from the C header by `tools/dump_tables.c` with `%.9g` (round-trips every
  float exactly; C initialises the float arrays from double literals, so printing the stored floats avoids double
  rounding in C#).
- `BinkaDecoder.cs` / `UebaReader.cs`: hand port. Decoder output is float in PCM16 scale like vgmstream's `F16` buffers;
  `UebaAudio.ToPcm16()` converts like vgmstream's CLI (`(int)f`, clamp). Like vgmstream, decoding renders exactly the
  header's sample count and fills the rest with silence after a broken packet; `UebaAudio.Error` / `DecodedSamples`
  report how far it got.

## Verification (synthetic-test, 2026-10-13)

- Oracle: vgmstream-cli built here from the same commit (`cmake -DUSE_*=OFF`, gcc 13, x86-64).
- CUE4Parse's UE6.0 fixture `SW_Format_BinkAudio` (mono, 48 kHz, 2 s): our PCM16 output is **byte-identical** to
  vgmstream-cli's (SHA-256 `01FAF390…BDAD3`), and correlates > 0.99 with the fixture's PCM version of the same source.
- Synthetic UEBA streams written by our own bitstream writer (`tests/CthExtract.Fortnite.Tests/Synth/SynthUeba.cs`:
  random coefficients, RLE batches, SEEK chunks, the 0xFFFF last-packet marker): **40/40** configurations (8, 16,
  22.05, 24, 32, 44.1, 48, 96 kHz × 1, 2, 3, 6, 8 channels) byte-identical to vgmstream-cli. Eight of the hashes are
  pinned in `BinkaTests`; with `CTH_VGMSTREAM_CLI` set, the tests also compare against a live vgmstream-cli.
- Independently of vgmstream: a DC-only packet decodes to the constant `dc · 2/√N`, and single-coefficient packets match
  a plain DCT-III `scale · (c0 + Σ c_k cos(πk(2n+1)/2N))` to < 0.01 (PCM16 units) at N = 512, 1024, 2048.
- Not tested: real Fortnite Bink Audio (no game files here); Bink Audio 1 (`BCF1`, RDFT mode) streams — the RDFT path is
  ported but no test data exists for it.
