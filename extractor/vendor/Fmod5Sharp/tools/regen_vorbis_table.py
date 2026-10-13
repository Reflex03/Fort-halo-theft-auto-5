#!/usr/bin/env python3
"""Regenerate ../Fmod5Sharp/Util/vorbis_headers_converted.json from reference libvorbis (BSD-3-Clause).

usage: python3 -I regen_vorbis_table.py <work dir> [--compare <upstream vorbis_headers_converted.json>]

Clones libogg v1.3.5 and libvorbis v1.3.7 (exact commits below) into <work dir>, compiles vorbis_setup_sweep.c against
their sources with the system C compiler and zlib, and writes every distinct setup header it prints in Fmod5Sharp's
format: {"<crc32>": {"headerBytes": <base64>, "seekBit": <int>}}. Nothing from FMOD is used. With --compare it reports
how many entries of another table (e.g. upstream Fmod5Sharp's, which python-fsb5 dumped from an FMOD binary) are
byte-identical to ours.
"""
import base64, json, pathlib, subprocess, sys

OGG = ("https://github.com/xiph/ogg.git", "v1.3.5", "e1774cd77f471443541596e09078e78fdc342e4f")
VORBIS = ("https://github.com/xiph/vorbis.git", "v1.3.7", "0657aee69dec8508a0011f47f3b69d7538e9d262")
here = pathlib.Path(__file__).resolve().parent
work = pathlib.Path(sys.argv[1]).resolve()
work.mkdir(parents=True, exist_ok=True)


def clone(url, tag, commit, dest):
    if not dest.exists():
        subprocess.run(["git", "-c", "advice.detachedHead=false", "clone", "-q", "--depth", "1", "--branch", tag, url, str(dest)], check=True)
    head = subprocess.run(["git", "-C", str(dest), "rev-parse", "HEAD"], check=True, capture_output=True, text=True).stdout.strip()
    if head != commit:
        sys.exit(f"{dest}: HEAD {head} is not the pinned {commit}")


clone(*OGG, work / "ogg")
clone(*VORBIS, work / "vorbis")
(work / "ogg/include/ogg/config_types.h").write_text(
    "#ifndef __CONFIG_TYPES_H__\n#define __CONFIG_TYPES_H__\n#include <stdint.h>\n"
    "typedef int16_t ogg_int16_t; typedef uint16_t ogg_uint16_t; typedef int32_t ogg_int32_t;\n"
    "typedef uint32_t ogg_uint32_t; typedef int64_t ogg_int64_t; typedef uint64_t ogg_uint64_t;\n#endif\n")
lib = work / "vorbis/lib"
srcs = [str(lib / f) for f in ("analysis.c", "bitrate.c", "block.c", "codebook.c", "envelope.c", "floor0.c", "floor1.c",
                              "info.c", "lookup.c", "lpc.c", "lsp.c", "mapping0.c", "mdct.c", "psy.c", "registry.c",
                              "res0.c", "sharedbook.c", "smallft.c", "synthesis.c", "vorbisenc.c", "window.c")]
srcs += [str(work / "ogg/src/bitwise.c"), str(work / "ogg/src/framing.c")]
exe = work / "vorbis_setup_sweep"
subprocess.run(["cc", "-O2", "-w", f"-I{work / 'vorbis/include'}", f"-I{work / 'ogg/include'}", f"-I{lib}",
                str(here / "vorbis_setup_sweep.c"), *srcs, "-lz", "-lm", "-o", str(exe)], check=True)
out = subprocess.run([str(exe)], check=True, capture_output=True, text=True).stdout

table = {}
for line in out.splitlines():
    crc, seek, ch, rate, q, hexdata = line.split()
    table[crc] = {"headerBytes": base64.b64encode(bytes.fromhex(hexdata)).decode(), "seekBit": int(seek)}
table = dict(sorted(table.items(), key=lambda kv: int(kv[0])))
dest = here.parent / "Fmod5Sharp/Util/vorbis_headers_converted.json"
dest.write_text(json.dumps(table, indent=2) + "\n", encoding="utf-8")
print(f"wrote {len(table)} setup headers to {dest}")

if "--compare" in sys.argv:
    other = json.loads(pathlib.Path(sys.argv[sys.argv.index("--compare") + 1]).read_text(encoding="utf-8-sig"))
    same = [k for k in other if k in table and other[k]["headerBytes"] == table[k]["headerBytes"] and other[k]["seekBit"] == table[k]["seekBit"]]
    diff = [k for k in other if k in table and k not in same]
    print(f"compare: {len(other)} entries in the other table, {len(same)} byte-identical here (incl. seekBit), "
          f"{len(diff)} differing, {len(other) - len(same) - len(diff)} not produced by libvorbis")
