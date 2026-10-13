#!/usr/bin/env python3
"""Re-create the vendored, stripped CUE4Parse + CUE4Parse-Conversion in this folder from an upstream clone.

usage: python3 -I vendor.py <path to a git clone of https://github.com/FabianFG/CUE4Parse> [--no-patch]
       python3 -I vendor.py --neutralize-only <folder>   (step 4 alone, on a copy; used to regenerate the patch)

Steps (VENDORED.md explains each change):
  1. git archive of the pinned commit (CUE4Parse, CUE4Parse-Conversion, LICENSE, NOTICE, README.md, Directory.*.props);
  2. upstream commit 5ca634b5 "Some FN fixes" (UE6.0 material shader map order, Fortnite types) applied on top;
  3. stripped files deleted (native DLLs, game keys/maps, MS-PL CRC32.cs, the OodleUE/zlib-ng/unluac download helpers);
  4. game key material neutralised in place (KEY_FILES below): hard-coded decryption keys, key tables, key strings
     and per-game XOR keys of games other than Fortnite are zeroed, so no game key is in this repository, in
     cthextract.patch or in cthextract.exe. Those games' decryption then fails; the Fortnite path never uses them;
  5. cthextract.patch applied (project files, Oodle hook, no network, no native extraction).

Regenerating cthextract.patch after editing the vendored sources: run steps 1-4 into an empty copy of this folder
(`vendor.py <clone> --no-patch`), then `diff -ruN <that copy> <this folder>` restricted to the vendored files.
Never write a game key into the patch: step 4 runs before it, so context lines only ever show zeroed values.
"""
import io
import pathlib
import re
import shutil
import subprocess
import sys
import tarfile

PIN = "1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562"       # = NuGet CUE4Parse 1.2.2.202610
CHERRY = "5ca634b56a2f67ab8531d21d24be0eabe2d23843"    # "Some FN fixes", 2026-10-04

STRIPPED = [
    "CUE4Parse/Resources/ACE7Key.bin",
    "CUE4Parse/Resources/BL4SoundTagsMap.json",
    "CUE4Parse/Resources/PUBGNameHashMap.json",
    "CUE4Parse/Resources/ProSpiDecryptor.dll.gz",
    "CUE4Parse/Resources/ShaderHashedNames.json",
    "CUE4Parse-Conversion/Resources/Detex.dll",
    "CUE4Parse-Conversion/Resources/crunch.dll",
    "CUE4Parse-Conversion/Resources/tegra_swizzle_x64.dll",
    "CUE4Parse/Utils/CRC32.cs",
    "CUE4Parse/Compression/OodleHelper.cs",
    "CUE4Parse/Compression/ZlibHelper.cs",
    "CUE4Parse/Utils/HttpUtils.cs",
    "CUE4Parse/UE4/Lua/unluac/UnluacHelper.cs",
]

# ---------------------------------------------------------------------------------------------- step 4: game keys
#
# Which rules run on which file. Every file here belongs to a game other than Fortnite, except the four core files at
# the end, where only the other games' key literals are touched.
#   runs    every bracketed list of >= 4 numbers (array / collection initialisers, Vector.Create(...)) -> all zeros
#   hex     every hex number of >= 8 digits outside such a list -> a small index of the same C# literal type
#           (keeps switch arms distinct and the literal's int/uint/long/ulong type)
#   wide    like hex, but only numbers of >= 12 digits (64-bit XOR keys) - for core files
#   hexstr  every string of >= 16 hex digits (with or without 0x) -> zeros
#   strings every string literal of >= 8 characters without whitespace that is not a dotted name -> zeros
#           (key strings, salts, passwords, base64 keys, also when they only appear in comments)
#   getbytes Encoding.X.GetBytes("...") literals -> zeros
#   fsbkeys the "Known encryption keys" list in a doc comment -> names kept, keys removed
ALL = ("runs", "hex", "hexstr", "strings")
GT = "CUE4Parse/GameTypes/"
KEY_FILES = {
    GT + "ABI/Encryption/SM4/ABIDecryption.cs": ALL,
    GT + "ABI/Encryption/SM4/SM4.cs": ALL,
    GT + "ABI/UE4/Lua/ABILua.cs": ALL + ("getbytes",),
    GT + "Aion2/Encryption/Dat/Aion2DatFileEncryption.cs": ("hex",),
    GT + "Aion2/Objects/FAion2DatFileArchive.cs": ("runs",),
    GT + "Aion2/Objects/FAion2KeyManifestFile.cs": ("runs",),
    GT + "ApexMobile/Encryption/Aes/ApexLegendsMobileAes.cs": ALL,
    GT + "Century/Encryption/CenturyDecryptPWC.cs": ALL,
    GT + "CodeVein2/Encryption/CodeVein2StringEncryption.cs": ALL,
    GT + "DQXI/Encryption/DQXIpakFileReader.cs": ("runs", "hex", "hexstr"),
    GT + "EOTU/Encryption/EOTUStringEncryption.cs": ALL,
    GT + "FSR/Encryption/Aes/FSRAes.cs": ALL,
    GT + "FunkoFusion/Encryption/Aes/FunkoFusionAes.cs": ALL,
    GT + "LordOfMysteries/UE4/Lua/LoMLua.cs": ALL + ("getbytes",),
    GT + "NMZ/Encryption/Aes/AssaultFireFutureAes.cs": ALL,
    GT + "NTE/Encryption/NevernessToEvernessIniEncryption.cs": ALL + ("getbytes",),
    GT + "NTE/Encryption/NevernessToEvernessLocresEncryption.cs": ALL,
    GT + "NetEase/MAR/Encryption/Aes/NetEaseAes.cs": ALL,
    GT + "Netmarble/NiNoKuni/UE4/Encryption/NiNoKuniCsv.cs": ("getbytes",),
    GT + "OPA/Encryption/OnePieceAmbitionEncryption.cs": ("runs", "hex"),
    GT + "PUBG/UE4/Lua/GameForPeaceLua.cs": ALL,
    GT + "PUBG/UE4/Pak/GameForPeacePakFileReader.cs": ("runs", "hex", "hexstr"),
    GT + "ProSpi/Encryption/Aes/CipherResolver.cs": ("runs", "hex"),
    GT + "ProSpi/Encryption/Aes/DescriptorTable.cs": ("runs", "hex"),
    GT + "ProSpi/Encryption/Aes/ProSpiEncryption.cs": ("hexstr",),  # SHA-256 of that game's exe (its DLL is stripped)
    GT + "RL/Encryption/Aes/RLAes.cs": ALL,
    GT + "Rennsport/Encryption/Aes/RennsportAes.cs": ALL,
    GT + "SD/Encryption/Aes/SpectreDivideAes.cs": ALL,
    GT + "SilverPalace/Encryption/SilverPalaceAes.cs": ALL,
    GT + "Snowbreak/Encryption/Lua/SnowbreakLua.cs": ALL + ("getbytes",),
    GT + "Strinova/Lua/StrinovaLua.cs": ALL,
    GT + "THPS/Encryption/Aes/THPS12.cs": ALL,
    GT + "Tencent/GangstarMirageCity/Encryption/GangstarMirageCityAes.cs": ALL,
    GT + "Tencent/PUBGMobile/Encryption/RSA/PUBGMobileRSA.cs": ALL,
    GT + "Tencent/PUBGMobile/Encryption/SM4/PUBGMobileSM4.cs": ALL + ("getbytes",),
    GT + "Tencent/PUBGMobile/Lua/PUBGMobileLua.cs": ALL,
    GT + "Tencent/RocoKingdomWorld/Encryption/FConfigurableCrypto.cs": ALL,
    GT + "Tencent/RocoKingdomWorld/Encryption/RocoKingdomWorldEncryption.cs": ("hex",),
    GT + "Tencent/RocoKingdomWorld/Lua/NRCLua.cs": ALL,
    GT + "Tencent/RocoKingdomWorld/Lua/NRCLuaReader.cs": ("runs",),
    GT + "Tencent/ValorantSource/Encryption/Aes/ValorantSourceAes.cs": ALL,
    GT + "Tencent/ValorantSource/Encryption/RSA/ValorantSourceRSA.cs": ALL,
    GT + "Tencent/ValorantSource/Lua/ValorantSourceLua.cs": ALL,
    GT + "Theia/Encryption/TheiaDecryptor.cs": ("runs", "hex", "hexstr"),
    GT + "UDWN/Encryption/Aes/ToaaAes.cs": ALL,
    GT + "UDWN/Lua/UndawnLua.cs": ALL,
    GT + "UWO/Encryption/Aes/UnchartedWatersOrigin.cs": ALL,
    # core files: only other games' keys (The Finals / ARC Raiders / NFS Mobile TOC keys, PUBG / Game for Peace /
    # PMA / Wild Assault / Silver Palace pak-footer XOR keys, CRI HCA and FMOD FSB5 keys listed in doc comments)
    "CUE4Parse/UE4/IO/Objects/FIoStoreTocResource.cs": ("hexstr",),
    "CUE4Parse/UE4/Pak/Objects/FPakInfo.cs": ("runs", "wide"),
    "CUE4Parse/UE4/CriWare/CriWareProvider.cs": ("wide",),
    "CUE4Parse/UE4/FMod/Fsb5Decryption.cs": ("fsbkeys",),
}

NUM = r"(?:0[xX][0-9A-Fa-f]+|\d+)(?:[uU][lL]?|[lL][uU]?)?"
NUM_RE = re.compile(r"(?<![\w.])" + NUM + r"(?![\w.])")
INNER = re.compile(r"([\[{(])([^\[\]{}()]*)([\]})])")
COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)
COMMENT_RUN = re.compile(NUM + r"(?:[ \t]*,[ \t]*" + NUM + r"){3,}")
ONLY_NUMS = re.compile(r"^\s*(?:" + NUM + r"\s*,\s*)*" + NUM + r"\s*,?\s*$")
HEX_RE = re.compile(r"(?<![\w.])0[xX]([0-9A-Fa-f]+)((?:[uU][lL]?|[lL][uU]?)?)(?![\w.])")
ESC_RE = re.compile(r"\\x[0-9A-Fa-f]{1,4}|\\u[0-9A-Fa-f]{4}|\\U[0-9A-Fa-f]{8}|\\.|.", re.S)
HEXSTR_RE = re.compile(r'"(0[xX])?([0-9A-Fa-f]{16,})"')
GETBYTES_RE = re.compile(r'(GetBytes\(\s*)"((?:[^"\\\n]|\\.)*)"')
DOTTED = re.compile(r"^[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+$")
FSB_KEY_LINE = re.compile(r"^(\s*/// - [^\n:]+(?:: [^\n:]+)?): [^\n]*$", re.M)


def _zero_num(tok: str) -> str:
    m = re.fullmatch(r"(0[xX])([0-9A-Fa-f]+)(.*)", tok)
    if m:
        return m.group(1) + "0" * len(m.group(2)) + m.group(3)
    m = re.fullmatch(r"(\d+)(.*)", tok)
    return "0" * len(m.group(1)) + m.group(2)


def _zero_runs(t: str) -> tuple[str, int]:
    count = 0

    def repl(m):
        nonlocal count
        body = m.group(2)
        bare = COMMENT.sub(" ", body)
        if not ONLY_NUMS.match(bare) or len(NUM_RE.findall(bare)) < 4:
            return m.group(0)
        count += 1
        zeroed = NUM_RE.sub(lambda n: _zero_num(n.group(0)), body)
        values = [_num_value(n) for n in NUM_RE.findall(bare)]
        if m.group(1) == "(" and max(values) > 0x7F and max(values) <= 0xFF:
            # a call such as Vector128.Create(0x4E, ...): all-zero int constants would pick the sbyte overload
            zeroed = NUM_RE.sub(lambda n: "(byte)" + n.group(0), zeroed, count=1)
        return m.group(1) + zeroed + m.group(3)

    t = INNER.sub(repl, t)

    def in_comment(m):  # commented-out key bytes ("// 0x92, 0x8C, ...")
        nonlocal count
        tok = m.group(0)
        if not (tok.startswith("//") or tok.startswith("/*")):
            return tok

        def run(r):
            nonlocal count
            count += 1
            return NUM_RE.sub(lambda n: _zero_num(n.group(0)), r.group(0))
        return COMMENT_RUN.sub(run, tok)

    return TOKEN.sub(in_comment, t), count


def _num_value(n: str) -> int:
    n = n.rstrip("uUlL")
    return int(n, 16) if n.lower().startswith("0x") else int(n)


def _index_hex(t: str, min_digits: int) -> tuple[str, int]:
    """Non-zero hex numbers of >= min_digits digits -> base + index, where base keeps the literal's C# type."""
    count = 0

    def repl(m):
        nonlocal count
        digits, suffix = m.group(1), m.group(2)
        value = int(digits, 16)
        if len(digits) < min_digits or value == 0:
            return m.group(0)
        count += 1
        if len(digits) > 16:  # longer than any C# literal: a key quoted in a comment
            return "0x" + "0" * len(digits) + suffix
        base = 0 if value <= 0x7FFFFFFF else 0x80000000 if value <= 0xFFFFFFFF else \
            0x100000000 if value <= 0x7FFFFFFFFFFFFFFF else 0x8000000000000000
        if suffix:  # an explicit suffix fixes the type already; keep values small but non-zero
            base = 0
        return "0x" + format(base + count, "0%dX" % len(digits)) + suffix

    return HEX_RE.sub(repl, t), count


def _zero_string_content(s: str) -> str:
    return "0" * len(ESC_RE.findall(s))


TOKEN = re.compile(r'//[^\n]*|/\*.*?\*/|\$?@\$?"(?:[^"]|"")*"|\$"(?:[^"\\\n]|\\.)*"|"(?:[^"\\\n]|\\.)*"(?:u8)?'
                   r"|'(?:[^'\\\n]|\\.)+'", re.S)
QUOTED_IN_COMMENT = re.compile(r'"((?:[^"\\\n]|\\.)*)"')


def _secret_string(s: str) -> bool:
    n = len(ESC_RE.findall(s))
    return n >= 8 and not re.search(r"\s", s) and not DOTTED.match(s) and not set(s) <= {"0"}


def _zero_strings(t: str) -> tuple[str, int]:
    """Zero key-like regular string literals (and quoted key-like text inside comments). A small C# tokenizer keeps
    comments, verbatim/interpolated strings and char literals apart, so code between two strings is never touched."""
    count = 0

    def repl(m):
        nonlocal count
        tok = m.group(0)
        if tok.startswith("//") or tok.startswith("/*"):
            def inner(q):
                nonlocal count
                if not _secret_string(q.group(1)):
                    return q.group(0)
                count += 1
                return '"' + _zero_string_content(q.group(1)) + '"'
            return QUOTED_IN_COMMENT.sub(inner, tok)
        if not tok.startswith('"'):
            return tok  # verbatim / interpolated strings, char literals: left alone
        u8 = tok.endswith("u8")
        body = tok[1:-3] if u8 else tok[1:-1]
        if not _secret_string(body):
            return tok
        count += 1
        return '"' + _zero_string_content(body) + '"' + ("u8" if u8 else "")

    return TOKEN.sub(repl, t), count


def neutralize_text(t: str, rules) -> tuple[str, dict]:
    counts = {}
    if "fsbkeys" in rules:
        t, counts["fsbkeys"] = FSB_KEY_LINE.subn(r"\1: (key removed in this vendored copy)", t)
    if "getbytes" in rules:
        t, counts["getbytes"] = GETBYTES_RE.subn(lambda m: m.group(1) + '"' + _zero_string_content(m.group(2)) + '"', t)
    if "hexstr" in rules:
        t, counts["hexstr"] = HEXSTR_RE.subn(lambda m: '"' + (m.group(1) or "") + "0" * len(m.group(2)) + '"', t)
    if "strings" in rules:
        t, counts["strings"] = _zero_strings(t)
    if "runs" in rules:
        t, counts["runs"] = _zero_runs(t)
    if "hex" in rules:
        t, counts["hex"] = _index_hex(t, 8)
    if "wide" in rules:
        t, counts["wide"] = _index_hex(t, 12)
    return t, counts


def residual_problems(t: str, rules) -> list[str]:
    """What still looks like key material after neutralisation (must be empty)."""
    out = []
    if "runs" in rules:
        for m in INNER.finditer(t):
            bare = COMMENT.sub(" ", m.group(2))
            if ONLY_NUMS.match(bare) and len(NUM_RE.findall(bare)) >= 4 and any(
                    _num_value(n) for n in NUM_RE.findall(bare)):
                out.append("non-zero list " + m.group(0)[:40])
        for m in TOKEN.finditer(t):
            tok = m.group(0)
            if tok.startswith("//") or tok.startswith("/*"):
                for r in COMMENT_RUN.finditer(tok):
                    if any(_num_value(n) for n in NUM_RE.findall(r.group(0))):
                        out.append("non-zero list in a comment " + r.group(0)[:40])
    for kind, digits in (("hex", 8), ("wide", 12)):
        if kind in rules:
            for m in HEX_RE.finditer(t):
                v = int(m.group(1), 16)
                if len(m.group(1)) >= digits and v != 0 and (len(m.group(1)) > 16 or (v & 0x7FFFFFFF) >= 0x10000):
                    out.append("hex number " + m.group(0))
    if "hexstr" in rules:
        out += ["hex string " + m.group(0)[:24] for m in HEXSTR_RE.finditer(t) if set(m.group(2)) != {"0"}]
    if "fsbkeys" in rules and re.search(r"^\s*/// - [^\n]*: (?!\(key removed)[^\n:]*$", t, re.M):
        out.append("FSB key list line")
    if "strings" in rules:
        for m in TOKEN.finditer(t):
            tok = m.group(0)
            if tok.startswith('"'):
                body = tok[1:-3] if tok.endswith("u8") else tok[1:-1]
                if _secret_string(body):
                    out.append("string " + tok[:24])
    return out


def neutralize_tree(root: pathlib.Path, verbose=True) -> None:
    for rel, rules in KEY_FILES.items():
        f = root / rel
        raw = f.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        t = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
        n, counts = neutralize_text(t, rules)
        if n == t:
            raise SystemExit(f"step 4: nothing neutralised in {rel} (upstream changed? review KEY_FILES)")
        problems = residual_problems(n, rules)
        if problems:
            raise SystemExit(f"step 4: {rel} still has key-like data: {problems[:3]}")
        f.write_bytes((b"\xef\xbb\xbf" if bom else b"") + n.encode("utf-8"))
        if verbose:
            print(f"  keys neutralised: {rel} {counts}")


def main(argv):
    here = pathlib.Path(__file__).resolve().parent
    if argv[:1] == ["--neutralize-only"]:
        neutralize_tree(pathlib.Path(argv[1]).resolve())
        return
    src = pathlib.Path(argv[0]).resolve()
    apply_patch = "--no-patch" not in argv

    for p in ("CUE4Parse", "CUE4Parse-Conversion"):
        if (here / p).exists():
            shutil.rmtree(here / p)

    tar_bytes = subprocess.run(["git", "-C", str(src), "archive", "--format=tar", PIN,
                                "CUE4Parse", "CUE4Parse-Conversion", "LICENSE", "NOTICE", "README.md",
                                "Directory.Build.props", "Directory.Packages.props"],
                               check=True, capture_output=True).stdout
    with tarfile.open(fileobj=io.BytesIO(tar_bytes)) as tf:
        for m in tf.getmembers():
            if m.isfile():
                out = here / m.name
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(tf.extractfile(m).read())

    cherry = subprocess.run(["git", "-C", str(src), "diff", "--binary", CHERRY + "^", CHERRY],
                            check=True, capture_output=True).stdout
    subprocess.run(["git", "apply", "--whitespace=nowarn", "-"], input=cherry, cwd=here, check=True)

    for rel in STRIPPED:
        (here / rel).unlink()
    for d in ("CUE4Parse/Resources", "CUE4Parse-Conversion/Resources"):
        if (here / d).exists() and not any((here / d).iterdir()):
            (here / d).rmdir()

    neutralize_tree(here)

    if apply_patch:
        subprocess.run(["git", "apply", "--whitespace=nowarn", str(here / "cthextract.patch")], cwd=here, check=True)
    print("vendored", PIN, "+", CHERRY[:8], "into", here, "" if apply_patch else "(patch not applied)")


if __name__ == "__main__":
    main(sys.argv[1:])
