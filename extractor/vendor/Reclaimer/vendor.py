#!/usr/bin/env python3
"""Re-create the vendored, patched Reclaimer.Core + Reclaimer.Blam in this folder from an upstream clone.

usage: python3 -I vendor.py <path to a git clone of https://github.com/Gravemind2401/Reclaimer> [commit]

The result is what is committed next to this script. VENDORED.md lists every change. The patch keeps upstream
behaviour for everything we call, and only:
  - retargets to plain net10.0 (LangVersion 13: C# 14 first-class spans break MemoryMarshal.Cast calls), no WPF;
  - drops the proprietary Xbox SDK xcompress DLLs, the strong-name keys and the AMF exporter scripts;
  - drops the three binary P8 bump palettes (Halo 1 / Halo 2 / Stubbs Xbox bitmaps only; tables of unclear,
    probably game-derived provenance; Halo 3 never uses them): such bitmaps then fail with NotSupportedException;
  - guards the WPF-only image writers with #if RECLAIMER_WPF;
  - opens every game file read-only with FileShare.ReadWrite | FileShare.Delete (never blocks a game update);
  - removes the one helper that opened a map for writing (CacheFactory.CreateWriter(ICacheFile));
  - zeroes the hard-coded Halo: Reach / Halo 4 cache-file AES key strings (no game key is committed or shipped;
    Halo 3 caches are not encrypted and never use them; Reach/H4 encrypted tables then fail to decrypt).
"""
import pathlib
import re
import shutil
import subprocess
import sys
import tarfile
import io

PIN = "36224d7a4581f2a1f255099bf706aa56be9f6e21"  # tag v2.1.1644

here = pathlib.Path(__file__).resolve().parent
src = pathlib.Path(sys.argv[1]).resolve()
commit = sys.argv[2] if len(sys.argv) > 2 else PIN

for p in ("Reclaimer.Core", "Reclaimer.Blam"):
    if (here / p).exists():
        shutil.rmtree(here / p)

# export exactly the tracked files of the pinned commit (no bin/obj, no local edits)
tar_bytes = subprocess.run(["git", "-C", str(src), "archive", "--format=tar", commit,
                            "Reclaimer.Core", "Reclaimer.Blam", "LICENSE"],
                           check=True, capture_output=True).stdout
with tarfile.open(fileobj=io.BytesIO(tar_bytes)) as tf:
    for m in tf.getmembers():
        if not m.isfile():
            continue
        out = here / m.name
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(tf.extractfile(m).read())

removed = []
for rel in ("Reclaimer.Blam/Resources/xcompress32.dll", "Reclaimer.Blam/Resources/xcompress64.dll",
            "Reclaimer.Blam/Reclaimer.Blam.snk", "Reclaimer.Core/Reclaimer.Core.snk",
            "Reclaimer.Blam/Reclaimer.Blam.ruleset",
            "Reclaimer.Blam/Resources/3dsMax AMF2.ms", "Reclaimer.Blam/Resources/Blender AMF2.py",
            "Reclaimer.Blam/Resources/Halo1BumpPalette.bin", "Reclaimer.Blam/Resources/Halo2BumpPalette.bin",
            "Reclaimer.Blam/Resources/StubbsBumpPalette.bin"):
    f = here / rel
    if f.exists():
        f.unlink()
        removed.append(rel)


def read(f):
    """Text with its BOM and line endings kept, so patched files differ from upstream only where we changed them."""
    raw = f.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    t = raw[3:].decode("utf-8") if bom else raw.decode("utf-8")
    crlf = "\r\n" in t
    return t.replace("\r\n", "\n"), bom, crlf


def write(f, t, bom, crlf):
    if crlf:
        t = t.replace("\n", "\r\n")
    f.write_bytes((b"\xef\xbb\xbf" if bom else b"") + t.encode("utf-8"))


def edit(rel, fn):
    f = here / rel
    t, bom, crlf = read(f)
    n = fn(t)
    assert n != t, f"no change in {rel}"
    write(f, n, bom, crlf)


def fix_csproj(t):
    t = t.replace("<TargetFramework>net9.0-windows</TargetFramework>",
                  "<TargetFramework>net10.0</TargetFramework>\n        <LangVersion>13</LangVersion>")
    t = t.replace("<UseWPF>true</UseWPF>", "")
    t = re.sub(r"\s*<ContentWithTargetPath Include=\"Resources\\xcompress(32|64)\.dll\">.*?</ContentWithTargetPath>", "", t, flags=re.S)
    t = re.sub(r"\s*<ItemGroup>\s*</ItemGroup>", "", t)
    t = t.replace("<SignAssembly>true</SignAssembly>", "<SignAssembly>false</SignAssembly>")
    t = re.sub(r"\s*<AssemblyOriginatorKeyFile>.*?</AssemblyOriginatorKeyFile>", "", t)
    # CA1416: Windows-only API notes; CS0618/CS0162: upstream obsolete/unreachable code, kept as is
    t = t.replace("<NoWarn>$(NoWarn);", "<NoWarn>$(NoWarn);CA1416;CS0618;CS0162;")
    return t


edit("Reclaimer.Core/Reclaimer.Core.csproj", fix_csproj)
edit("Reclaimer.Blam/Reclaimer.Blam.csproj", fix_csproj)


def dds(t):
    t = t.replace("using System.Windows;\n", "#if RECLAIMER_WPF\nusing System.Windows;\n", 1).replace(
        "using System.Windows.Media.Imaging;\n", "using System.Windows.Media.Imaging;\n#endif\n", 1)
    a = t.index("        #region WriteToDisk")
    b = t.index("        /// <summary>\n        /// Returns a copy of the image in 32bit BGRA format.")
    t = t[:a] + "#if RECLAIMER_WPF\n" + t[a:b] + "#endif\n" + t[b:]
    a = t.index("        private WriteableBitmap UnwrapCubemapSource")
    b = t.index("        #region Standard Decompression Methods")
    t = t[:a] + "#if RECLAIMER_WPF\n" + t[a:b] + "#endif\n" + t[b:]
    return t


edit("Reclaimer.Core/Drawing/DdsConvert.cs", dds)
edit("Reclaimer.Core/Drawing/DdsOutputArgs.cs", (lambda t: "#if RECLAIMER_WPF\n" + t + "\n#endif\n"))
edit("Reclaimer.Core/Geometry/Utilities/SceneWriter.cs", (lambda t: t.replace(
    "dds.WriteToStream(ms, System.Drawing.Imaging.ImageFormat.Png);",
    "throw new NotSupportedException(\"PNG embedding needs WPF\"); // CthExtract: WPF removed")))

# no binary P8 bump palettes (removed above): their resources, accessors and the two Xbox P8-bump decode sites
PALETTES = ("Halo1BumpPalette", "Halo2BumpPalette", "StubbsBumpPalette")


def resx(t):
    for name in PALETTES:
        t, k = re.subn(r'  <data name="%s" type="System\.Resources\.ResXFileRef, System\.Windows\.Forms">\n.*?</data>\n' % name,
                       "", t, count=1, flags=re.S)
        assert k == 1, name
    return t


def designer(t):
    for name in PALETTES:
        t, k = re.subn(r"        \n        /// <summary>\n(?:        ///[^\n]*\n)*?        internal static byte\[\] %s \{\n.*?\n        \}\n" % name,
                       "", t, count=1, flags=re.S)
        assert k == 1, name
    return t


edit("Reclaimer.Blam/Properties/Resources.resx", resx)
edit("Reclaimer.Blam/Properties/Resources.Designer.cs", designer)
for game in ("Halo1", "Halo2"):
    edit(f"Reclaimer.Blam/Blam/{game}/BitmapTag.cs", (lambda t, g=game: t.replace(
        f"Array.Copy(Properties.Resources.{g}BumpPalette, indices[i] * 4, data, i * 4, 4);",
        f"throw new NotSupportedException(\"P8 bump palette not vendored (CthExtract: Halo 1/2 Xbox bitmaps only)\");", 1)))

# never write into a game folder: drop the map-for-writing helper
edit("Reclaimer.Blam/Blam/Common/CacheFactory.cs", (lambda t: re.sub(
    r"        public static EndianWriterEx CreateWriter\(this ICacheFile cache\)\n        \{\n.*?\n        \}\n\n",
    "        // CthExtract: CreateWriter(ICacheFile) removed; it opened the game's map file for writing.\n\n",
    t, count=1, flags=re.S)))

# every read-only open of a game file: let other processes keep writing/deleting (Steam updates, the game itself)
pattern = re.compile(r"FileMode\.Open, FileAccess\.Read\)")
share_count = 0
for f in sorted(list((here / "Reclaimer.Core").rglob("*.cs")) + list((here / "Reclaimer.Blam").rglob("*.cs"))):
    t, bom, crlf = read(f)
    n, k = pattern.subn("FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)", t)
    if k:
        write(f, n, bom, crlf)
        share_count += k

# never commit or ship a game key: zero the Halo: Reach / Halo 4 AES key strings (Halo 3 never uses them)
KEY_CONST = re.compile(r'(public const string (?:BetaKey|FileNamesKey|StringsKey|LocalesKey|NetworkKey) = )"([^"]*)";')
key_count = 0
for rel in ("Reclaimer.Blam/Blam/HaloReach/CacheFile.cs", "Reclaimer.Blam/Blam/Halo4/CacheFile.cs"):
    f = here / rel
    t, bom, crlf = read(f)
    n, k = KEY_CONST.subn(lambda m: m.group(1) + '"' + "0" * len(m.group(2)) + '";', t)
    assert k >= 4, f"expected the AES key constants in {rel}"
    write(f, n, bom, crlf)
    key_count += k

print(f"vendored Reclaimer {commit} into {here}")
print(f"removed: {removed}")
print(f"FileShare patched opens: {share_count}")
print(f"game key constants zeroed: {key_count}")
