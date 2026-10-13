# gta-toolkit (vendored subset)

- Upstream: https://github.com/Neodymium146/gta-toolkit
- Commit: `f287c66a38315fa6eb36b721fc090c62285ead1d` (2017-06-17, "Last commit"; the project is discontinued)
- Licence: MIT, carried only as a per-file header (the repository has no LICENSE file). The header is kept
  verbatim at the top of every vendored file and copied into `LICENSE` here.
- Compiled into `CthExtract.Gta` through `<Compile Include="../../vendor/gta-toolkit/*.cs" />`.

## Files

| File here | Upstream path | What was kept / changed |
|---|---|---|
| `GTA5Constants.cs` | `RageLib.GTA5/Cryptography/GTA5Constants.cs` | **Stripped.** Only the `GTA5HashConstants` class is kept: the SHA1 hashes of the PC AES key, the 101 NG keys, the 272 NG decryption tables and the hash LUT ("This file contains only SHA1 hashes of all interesting values. There is NO key."). The `GTA5Constants` class (key generation from an exe, load/save of key files) and the `using` directives it needed were removed. The 7,500 hash bytes are unchanged (checked by script against upstream). |
| `GTA5Encryption.cs` | `RageLib.GTA5/Cryptography/GTA5Encryption.cs` | **Patched.** `GTA5Crypto` no longer implements `IEncryptionAlgorithm`, the `Key` property and the instance `Decrypt(byte[])` were removed, and the NG decryption tables are a parameter (`Decrypt(data, key, tables)`, `DecryptBlock(data, key, tables)`) instead of the global `GTA5Constants.PC_NG_DECRYPT_TABLES`. The algorithm itself is unchanged. |
| `GTA5Hash.cs` | `RageLib.GTA5/Cryptography/GTA5Hash.cs` | **Patched.** The static constructor that copied `GTA5Constants.PC_LUT` was removed; the LUT is a parameter: `CalculateHash(string text, byte[] LUT)`. The algorithm is unchanged. |

All three files: UTF-8 BOM removed and CRLF line endings converted to LF; nothing else changed.

## How they are used

- `GTA5HashConstants`: the default `KeyHashes.Gta5Pc` (`src/CthExtract.Gta/Crypto/KeyHashes.cs`). Every piece of key
  material (the AES key found in the player's exe, every NG key, every NG table and the LUT decoded from the fetched
  blob) must match one of these hashes before it is used.
- `GTA5Hash.CalculateHash`: the NG key index of an archive or entry (`NgCrypto.KeyIndex`).
- `GTA5Crypto`: the reference implementation of the NG block cipher. Production code uses our own allocation-free
  `NgCrypto.DecryptInPlace`; the unit test `Ng_decrypt_is_bit_identical_to_vendored_gta_toolkit_reference` checks the two
  give identical output.

No other gta-toolkit file is used. The RPF7 / resource / fragment / texture readers in `src/CthExtract.Gta` are our own
code, written from gta-toolkit's (MIT) structure layouts and public format notes.

## MIT header (verbatim from the vendored files)

```
    Copyright(c) 2015 Neodymium

    Permission is hereby granted, free of charge, to any person obtaining a copy
    of this software and associated documentation files (the "Software"), to deal
    in the Software without restriction, including without limitation the rights
    to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    copies of the Software, and to permit persons to whom the Software is
    furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in
    all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
    THE SOFTWARE.
```
