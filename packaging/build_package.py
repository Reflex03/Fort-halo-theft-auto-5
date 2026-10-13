#!/usr/bin/env python3
"""Build the Melty release files of Craft Theft Halo.

    python3 packaging/build_package.py [--version 0.1.0] [--out packaging/out] [--sources]

Makes two files in --out (default packaging/out):

  crafttheft-<ver>.jar                Melty component "main": the Fabric mod, copied from fabric/build/libs.
                                      Melty puts it in the instance's .minecraft/mods.
  CraftTheftHalo-Minecraft-<ver>.zip  Melty component "minecraft": unpacked to {localappdata}/CraftTheftHalo.
      Prism/                          Prism Launcher 11.1.1 portable (unmodified) + LICENSE-PrismLauncher.txt,
                                      COPYING-PrismLauncher.md, prismlauncher_update.cfg (self-updater off)
      Prism/instances/CraftTheftHalo/ instance.cfg, mmc-pack.json, .minecraft/mods/fabric-api.jar
      bin/cthextract.exe              the extractor, from extractor/dist
      licenses/                       every licence text and notice (generated index: licenses/README.txt)
      THIRD-PARTY-NOTICES.md, SOURCE.txt, README.txt, bundle-version.txt

and entries.json (the {component: [{path, size}]} lists Melty's validate_recipe / one_click_check take),
release.json (sizes and SHA-256 of every file) and SHA256SUMS. The entries, sizes and hashes are printed too.

With --sources it also makes CraftTheftHalo-<ver>-sources.zip: the corresponding source of every GPL/LGPL
binary in the bundle (Prism, Qt, LZO, libqrencode, the vcpkg scripts) and of this repository, to be uploaded
with each release (see SOURCE.txt).

Every download is pinned by size and hash in packaging/pins.json. Zips are reproducible: sorted entries,
forward slashes, fixed timestamps and attributes. Inputs that do not exist yet (the mod jar, the extractor) are
reported together, with how to build them, before anything is downloaded.

Exit codes: 0 ok, 1 a check of the built files failed, 2 an input is missing or invalid (nothing built),
3 a download failed or did not match its pin, 4 bad arguments.

Python 3.9+, standard library only (git is needed for --sources and to stamp the commit).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import ssl
import string
import struct
import subprocess
import sys
import tarfile
import time
import urllib.error
import urllib.request
import zipfile
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_REPO = HERE.parent

INSTANCE_ID = "CraftTheftHalo"          # Prism instance id = folder name (prismlauncher.exe --launch CraftTheftHalo)
INSTANCE_DIR = f"Prism/instances/{INSTANCE_ID}"
MODS_DIR = f"{INSTANCE_DIR}/.minecraft/mods"
MOD_ID = "crafttheft"
GITHUB_REPO = "https://github.com/reflex03/fort-halo-theft-auto-5"
ZIP_NAME = "CraftTheftHalo-Minecraft-{version}.zip"
JAR_NAME = "crafttheft-{version}.jar"
SOURCES_NAME = "CraftTheftHalo-{version}-sources.zip"
FIXED_TIME = (2026, 1, 1, 0, 0, 0)       # every zip entry gets this timestamp (reproducible builds)
# Gradle/Loom side jars that are never the release jar; "harness" is the scripted test mod (fabric/src/harness).
JAR_CLASSIFIERS = ("sources", "dev", "javadoc", "all", "shadow", "slim", "api", "harness", "test", "tests")
VERSION_RE = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+.][0-9A-Za-z.+-]+)?$")

# Never in the bundle: the player's sign-in and launcher settings (Melty writes them), their game options,
# the extracted-content cache, the settings file Melty writes, and game archives of any of the four games.
FORBIDDEN_NAMES = {"accounts.json", "prismlauncher.cfg", "options.txt", "crafttheft.properties"}
FORBIDDEN_PREFIXES = ("cache/", "Prism/libraries/", "Prism/assets/", "Prism/meta/", "Prism/java/", "Prism/logs/")
FORBIDDEN_EXTS = {".rpf", ".map", ".pak", ".utoc", ".ucas", ".fsb", ".bk2", ".ytd", ".yft", ".cthm"}

EXIT_OK, EXIT_CHECK, EXIT_INPUT, EXIT_DOWNLOAD, EXIT_ARGS = 0, 1, 2, 3, 4

# Projects compiled into cthextract.exe (from its deps.json) -> the vendor folder whose LICENSE/NOTICE covers them.
# None = our own code (GPL-3.0-only, extractor/LICENSE). A project missing here stops the build: add it, and add
# its licence to THIRD-PARTY-NOTICES.md.
PROJECT_VENDOR = {
    "cthextract": None,
    "CthExtract.Core": None,
    "CthExtract.Fortnite": None,
    "CthExtract.Halo": None,
    "CthExtract.Gta": "gta-toolkit",     # compiles extractor/vendor/gta-toolkit/*.cs (MIT headers)
    "CthExtract.Oodle": "ooz",           # C# port of ooz (GPL-3.0-or-later)
    "CUE4Parse": "CUE4Parse",
    "CUE4Parse-Conversion": "CUE4Parse",
    "Fmod5Sharp": "Fmod5Sharp",
    "MeshDecimator": "MeshDecimator",
    "Reclaimer.Blam": "Reclaimer",
    "Reclaimer.Core": "Reclaimer",
    "VgmstreamBinka": "vgmstream-binka",
}
# NuGet packages whose nuspec has only a licenseUrl (no SPDX expression, no licence file). Checked by hand against
# the upstream repository's LICENSE (source inspection, 2026-10-13).
NUGET_LICENSE_OVERRIDES = {
    "K4os.Compression.LZ4": "MIT",
    "K4os.Compression.LZ4.Streams": "MIT",
    "K4os.Hash.xxHash": "MIT",
    "VGAudio": "MIT",
}
# .NET single-file bundle marker (Microsoft.NET.HostModel BundleSignature): the int64 bundle-header offset sits
# right before it in the host executable.
BUNDLE_SIGNATURE = bytes([
    0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
    0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae])

MIT_TEXT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions
of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
"""
BSD2_TEXT = """Redistribution and use in source and binary forms, with or without modification, are permitted provided
that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
   following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT,
INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING
IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
"""
SPDX_TEMPLATES = {"MIT": MIT_TEXT, "BSD-2-Clause": BSD2_TEXT}
SPDX_SHARED_FILES = {"Apache-2.0": "licenses/Apache-2.0.txt", "LGPL-2.1-or-later": "licenses/LGPL-2.1.txt",
                     "LGPL-3.0-only": "licenses/LGPL-3.0.txt", "GPL-2.0-or-later": "licenses/GPL-2.0.txt",
                     "GPL-3.0-only": "licenses/GPL-3.0.txt"}


class BuildError(Exception):
    def __init__(self, code: int, message: str):
        super().__init__(message)
        self.code = code


def log(msg: str) -> None:
    print(f"[package] {msg}", file=sys.stderr, flush=True)


# ---------------------------------------------------------------------------------------------- hashing, pins

def file_hashes(path: Path, algos=("sha256",)) -> dict:
    hs = {a: hashlib.new(a) for a in algos}
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            for h in hs.values():
                h.update(chunk)
    return {a: h.hexdigest() for a, h in hs.items()}


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def load_pins(path: Path) -> dict:
    try:
        pins = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        raise BuildError(EXIT_INPUT, f"cannot read pins file {path}: {e}")
    for key in ("prism", "prismLicense", "prismCopying", "fabricApi"):
        pin = pins.get("bundle", {}).get(key)
        if not pin or not pin.get("urls") or not pin.get("sha256") or not pin.get("file"):
            raise BuildError(EXIT_INPUT, f"{path}: bundle.{key} needs file, urls and sha256")
    return pins


def verify_file(path: Path, pin: dict) -> str | None:
    """None when the file matches every size/hash the pin gives, else what differs."""
    size = path.stat().st_size
    if "size" in pin and size != pin["size"]:
        return f"size {size} != pinned {pin['size']}"
    algos = [a for a in ("sha256", "sha512", "md5") if a in pin]
    if not algos:
        return "the pin has no hash"
    got = file_hashes(path, algos)
    for a in algos:
        if got[a] != pin[a].lower():
            return f"{a} {got[a]} != pinned {pin[a]}"
    return None


def _ssl_context() -> ssl.SSLContext:
    ctx = ssl.create_default_context()
    for var in ("SSL_CERT_FILE", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE"):
        ca = os.environ.get(var)
        if ca and os.path.isfile(ca):
            try:
                ctx.load_verify_locations(cafile=ca)
            except (OSError, ssl.SSLError):
                pass
    return ctx


def _download(url: str, dest: Path) -> None:
    req = urllib.request.Request(url, headers={"User-Agent": f"CraftTheftHalo-packaging/1 (+{GITHUB_REPO})"})
    kwargs = {"timeout": 120}
    if url.lower().startswith("https:"):
        kwargs["context"] = _ssl_context()
    with urllib.request.urlopen(req, **kwargs) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)


def fetch(pin: dict, downloads: Path, offline: bool = False) -> Path:
    """Return a verified local copy of a pinned file: the cached one if it matches, else a fresh download."""
    downloads.mkdir(parents=True, exist_ok=True)
    dest = downloads / pin["file"]
    if dest.exists():
        err = verify_file(dest, pin)
        if err is None:
            log(f"have {pin['file']} ({dest.stat().st_size:,} B, verified)")
            return dest
        log(f"cached {dest} does not match its pin ({err}); deleting it")
        dest.unlink()
    if offline:
        raise BuildError(EXIT_DOWNLOAD, f"{pin['file']} is not in {downloads} and --offline was given "
                                        f"(fetch it from {pin['urls'][0]})")
    tried = []
    for url in pin["urls"]:
        for attempt in (1, 2, 3):
            part = dest.with_name(f"{dest.name}.part{os.getpid()}")
            try:
                log(f"download {url} (try {attempt})")
                _download(url, part)
                err = verify_file(part, pin)
                if err:
                    tried.append(f"{url}: downloaded, but {err}")
                    log(f"  {url} does not match the pin ({err}); trying the next URL")
                    part.unlink()
                    break                                    # same bytes again would not help: next URL
                os.replace(part, dest)
                log(f"  got {pin['file']} ({dest.stat().st_size:,} B, verified)")
                return dest
            except (urllib.error.URLError, OSError, ValueError) as e:   # HTTPError is a URLError
                tried.append(f"{url} try {attempt}: {e}")
                log(f"  failed: {e}")
                if part.exists():
                    part.unlink()
                if isinstance(e, urllib.error.HTTPError) and e.code in (403, 404, 410):
                    break
                time.sleep(attempt)
    raise BuildError(EXIT_DOWNLOAD, f"could not get {pin['file']} matching its pin:\n  " + "\n  ".join(tried))


# ---------------------------------------------------------------------------------------------- git helpers

def git(args, cwd: Path, check=True, capture=True, env=None) -> subprocess.CompletedProcess:
    e = dict(os.environ, GIT_TERMINAL_PROMPT="0", **(env or {}))
    return subprocess.run(["git", *args], cwd=str(cwd), check=check, env=e,
                          stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE)


def repo_commit(repo: Path) -> tuple[str, bool]:
    """(commit sha or 'unknown', dirty?) of the project repository."""
    try:
        sha = git(["rev-parse", "HEAD"], repo).stdout.decode().strip()
        # untracked (not ignored) files count: an exe built from a source file that was never committed would have
        # no corresponding source in the release's commit (GPL-3.0 section 6)
        dirty = bool(git(["status", "--porcelain", "--untracked-files=normal"], repo).stdout.strip())
        return sha, dirty
    except (OSError, subprocess.CalledProcessError):
        return "unknown", False


def extractor_stamp_problem(repo: Path, exe: Path) -> "str | None":
    """--release: the exe must have been built by extractor/build.sh from exactly this commit's extractor/ folder,
    so the commit SOURCE.txt names is its corresponding source (GPL-3.0 section 6). build.sh writes the stamp."""
    stamp = exe.parent / "cthextract.source.json"
    try:
        s = json.loads(stamp.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return (f"{stamp} is missing or unreadable: rebuild the extractor with extractor/build.sh, which records "
                f"the source it was built from")
    try:
        tree = git(["rev-parse", "HEAD:extractor"], repo).stdout.decode().strip()
    except (OSError, subprocess.CalledProcessError):
        return "cannot read the extractor/ tree of HEAD"
    if s.get("sha256") != file_hashes(exe)["sha256"]:
        return f"{stamp.name} describes another build than {exe}: rebuild with extractor/build.sh"
    if s.get("dirty") is not False:
        return f"{exe.name} was built from uncommitted extractor sources: commit, then rebuild with extractor/build.sh"
    if s.get("extractorTree") != tree:
        return (f"{exe.name} was built from extractor tree {s.get('extractorTree')}, but this commit's extractor/ is "
                f"{tree}: rebuild with extractor/build.sh")
    return None


def git_source_tar(src: dict, downloads: Path, offline: bool) -> Path:
    """`git archive` of a pinned commit (only the pinned paths), after checking every pinned tree hash."""
    dest = downloads / src["file"]
    repo = downloads / "git" / f"{src['id']}.git"
    commit = src["commit"]
    if not repo.exists():
        if offline:
            raise BuildError(EXIT_DOWNLOAD, f"{src['id']}: no local clone in {repo} and --offline was given")
        repo.mkdir(parents=True)
        git(["init", "-q", "--bare"], repo)
        git(["remote", "add", "origin", src["git"]], repo)
    have = git(["cat-file", "-e", f"{commit}^{{commit}}"], repo, check=False).returncode == 0
    if not have:
        if offline:
            raise BuildError(EXIT_DOWNLOAD, f"{src['id']}: commit {commit} not in {repo} and --offline was given")
        log(f"git fetch {src['git']} {commit}")
        r = git(["fetch", "-q", "--depth", "1", "--filter=blob:none", "origin", commit], repo, check=False)
        if r.returncode != 0:
            raise BuildError(EXIT_DOWNLOAD, f"{src['id']}: git fetch failed: {r.stderr.decode(errors='replace')}")
    paths = []
    for path, tree in sorted(src["trees"].items()):
        rev = f"{commit}^{{tree}}" if path == "" else f"{commit}:{path}"
        got = git(["rev-parse", rev], repo).stdout.decode().strip()
        if got != tree:
            raise BuildError(EXIT_DOWNLOAD, f"{src['id']}: {path or '/'} is {got} at {commit}, pinned {tree}")
        if path:
            paths.append(path)
    log(f"git archive {src['id']} {commit[:12]} ({', '.join(paths) or 'whole tree'}; trees verified)")
    tmp = dest.with_name(dest.name + f".part{os.getpid()}")
    with open(tmp, "wb") as f:
        r = subprocess.run(["git", "archive", "--format=tar", f"--prefix={src['id']}-{commit[:12]}/", commit,
                            "--", *paths], cwd=str(repo), stdout=f, stderr=subprocess.PIPE,
                           env=dict(os.environ, GIT_TERMINAL_PROMPT="0"))
    if r.returncode != 0:
        tmp.unlink()
        raise BuildError(EXIT_DOWNLOAD, f"{src['id']}: git archive failed: {r.stderr.decode(errors='replace')}")
    os.replace(tmp, dest)
    return dest


# ---------------------------------------------------------------------------------------------- inputs

def find_mod_jar(libs: Path, version: str | None) -> tuple[Path | None, str | None, list[str]]:
    """Pick fabric/build/libs/crafttheft-<ver>.jar (not -sources/-dev/...). Returns (jar, version, log lines)."""
    notes = []
    if not libs.is_dir():
        return None, None, [f"{libs} does not exist"]
    cands = []
    for p in sorted(libs.glob(f"{MOD_ID}-*.jar")):
        stem = p.name[len(MOD_ID) + 1:-4]
        if any(stem.endswith("-" + c) for c in JAR_CLASSIFIERS):
            notes.append(f"ignored {p.name} (classifier jar)")
            continue
        if not VERSION_RE.match(stem):
            notes.append(f"ignored {p.name} (no version in the name)")
            continue
        cands.append((stem, p))
    notes.append(f"candidates in {libs}: {[p.name for _, p in cands] or 'none'}")
    if version:
        for v, p in cands:
            if v == version:
                return p, v, notes + [f"picked {p.name} (--version {version})"]
        return None, None, notes + [f"none is version {version}"]
    if len(cands) == 1:
        return cands[0][1], cands[0][0], notes + [f"picked {cands[0][1].name}"]
    if len(cands) > 1:
        return None, None, notes + ["more than one version: pass --version or clean fabric/build/libs"]
    return None, None, notes


def version_from_name(p: Path) -> str | None:
    m = re.match(rf"^{MOD_ID}-(.+)\.jar$", p.name)
    return m.group(1) if m and VERSION_RE.match(m.group(1)) else None


def check_mod_jar(jar: Path, version: str) -> list[str]:
    """Problems with the mod jar (empty list = fine)."""
    problems = []
    try:
        with zipfile.ZipFile(jar) as z:
            names = z.namelist()
            if "fabric.mod.json" not in names:
                return [f"{jar.name} has no fabric.mod.json: is it the remapped mod jar?"]
            try:
                fmj = json.loads(z.read("fabric.mod.json").decode("utf-8"))
            except (ValueError, UnicodeDecodeError) as e:
                return [f"{jar.name}: fabric.mod.json is not valid JSON ({e})"]
            if fmj.get("id") != MOD_ID:
                problems.append(f"{jar.name}: fabric.mod.json id is {fmj.get('id')!r}, expected {MOD_ID!r}")
            if fmj.get("version") != version:
                problems.append(f"{jar.name}: fabric.mod.json version is {fmj.get('version')!r}, the file name says "
                                f"{version!r} (an unexpanded ${{version}} means processResources did not run)")
            bad = [n for n in names if Path(n).suffix.lower() in FORBIDDEN_EXTS - {".cthm"}]
            if bad:
                problems.append(f"{jar.name} contains game archive files, which must never ship: {bad[:5]}")
            bad = z.testzip()
            if bad:
                problems.append(f"{jar.name}: corrupt entry {bad}")
    except (zipfile.BadZipFile, OSError) as e:
        problems.append(f"{jar}: not a readable jar ({e})")
    return problems


def check_pe_gui_x64(path: Path) -> list[str]:
    try:
        with open(path, "rb") as f:
            b = f.read(4096)
    except OSError as e:
        return [f"{path}: {e}"]
    if b[:2] != b"MZ":
        return [f"{path} is not a Windows executable (no MZ header)"]
    pe = struct.unpack_from("<I", b, 0x3C)[0]
    if pe + 24 + 70 > len(b) or b[pe:pe + 4] != b"PE\0\0":
        return [f"{path}: no PE signature"]
    problems = []
    machine = struct.unpack_from("<H", b, pe + 4)[0]
    subsystem = struct.unpack_from("<H", b, pe + 24 + 68)[0]
    if machine != 0x8664:
        problems.append(f"{path.name}: machine 0x{machine:04x}, expected x64 (0x8664)")
    if subsystem != 2:
        problems.append(f"{path.name}: PE subsystem {subsystem}, expected 2 (GUI; a console exe flashes a window)")
    return problems


def _read_7bit_str(data: bytes, p: int) -> tuple[str, int]:
    n = shift = 0
    while True:
        b = data[p]
        p += 1
        n |= (b & 0x7F) << shift
        shift += 7
        if b < 0x80:
            break
    return data[p:p + n].decode("utf-8"), p + n


def read_single_file_bundle(path: Path) -> dict | None:
    """Manifest of a .NET single-file bundle: {version, files:[{path,type,size,compressed}], deps, runtimeconfig}.
    None if the file is not a bundle. Types: 1 assembly, 2 native, 3 deps.json, 4 runtimeconfig.json, 5 symbols."""
    data = path.read_bytes()
    i = data.find(BUNDLE_SIGNATURE)
    if i < 8:
        return None
    hdr = struct.unpack_from("<q", data, i - 8)[0]
    if not 0 < hdr < len(data) - 12:
        return None
    major, minor, count = struct.unpack_from("<IIi", data, hdr)
    if not (1 <= major <= 20 and 0 <= count < 100000):
        return None
    p = hdr + 12
    _bundle_id, p = _read_7bit_str(data, p)
    if major >= 2:
        p += 8 * 4 + 8                      # deps.json and runtimeconfig.json offset/size, flags
    files = []
    blobs = {}
    for _ in range(count):
        off, size = struct.unpack_from("<qq", data, p)
        p += 16
        csize = 0
        if major >= 6:
            csize = struct.unpack_from("<q", data, p)[0]
            p += 8
        ftype = data[p]
        p += 1
        name, p = _read_7bit_str(data, p)
        files.append({"path": name, "type": ftype, "size": size, "compressed": csize})
        if ftype in (3, 4):
            raw = data[off:off + (csize or size)]
            blobs[ftype] = zlib.decompress(raw, -15) if csize else raw
    try:
        deps = json.loads(blobs[3].decode("utf-8-sig")) if 3 in blobs else None
        rc = json.loads(blobs[4].decode("utf-8-sig")) if 4 in blobs else None
    except (ValueError, zlib.error):
        deps = rc = None
    return {"version": f"{major}.{minor}", "files": files, "deps": deps, "runtimeconfig": rc}


def check_extractor(exe: Path) -> tuple[list[str], dict | None]:
    problems = check_pe_gui_x64(exe)
    side = exe.with_name(exe.name + ".sha256")
    if side.exists():
        words = side.read_text(encoding="utf-8", errors="replace").split()
        want = words[0].lower() if words else ""
        got = file_hashes(exe)["sha256"]
        if want != got:
            problems.append(f"{exe.name} sha256 {got} != {side.name} ({want}): rebuild with extractor/build.sh")
        else:
            log(f"{exe.name}: sha256 matches {side.name}")
    else:
        log(f"note: no {side.name} next to {exe.name} (extractor/build.sh writes one)")
    others = sorted(p.name for p in exe.parent.iterdir() if p.is_file() and p.name not in (exe.name, side.name, "cthextract.source.json"))
    if others:
        log(f"note: {exe.parent} also holds {others}; only {exe.name} is packaged (single-file build)")
    bundle = None
    if not problems:
        bundle = read_single_file_bundle(exe)
        if bundle is None or bundle["deps"] is None:
            problems.append(f"{exe.name} is not a .NET single-file bundle with a deps.json, so its third-party "
                            f"notices cannot be checked (build it with extractor/build.sh)")
            bundle = None
        else:
            log(f"{exe.name}: single-file bundle v{bundle['version']}, {len(bundle['files'])} embedded files")
    return problems, bundle


def check_instance_files(cfg_text: str, pack: dict, pins: dict) -> list[str]:
    problems = []
    keys = {}
    for line in cfg_text.splitlines():
        if "=" in line and not line.lstrip().startswith(("#", "[")):
            k, v = line.split("=", 1)
            keys[k.strip()] = v.strip()
        if "#" in line:
            problems.append(f"instance.cfg: '#' starts a comment in Prism's old-format parser: {line!r}")
    if "ConfigVersion" in keys:
        problems.append("instance.cfg must not have ConfigVersion (Prism would read it with QSettings, where "
                        "commas in values turn into lists)")
    for k, v in {"InstanceType": "OneSix", "LowMemWarning": "false", "OverrideMemory": "true",
                 "MaxMemAlloc": "4096"}.items():
        if keys.get(k) != v:
            problems.append(f"instance.cfg: {k}={keys.get(k)!r}, expected {v!r}")
    if pack.get("formatVersion") != 1:
        problems.append("mmc-pack.json: formatVersion must be 1")
    vers = pins["versions"]
    want = {"org.lwjgl3": vers["lwjgl"], "net.minecraft": vers["minecraft"],
            "net.fabricmc.intermediary": vers["minecraft"], "net.fabricmc.fabric-loader": vers["fabricLoader"]}
    got = {c.get("uid"): c.get("version") for c in pack.get("components", [])}
    for uid, v in want.items():
        if got.get(uid) != v:
            problems.append(f"mmc-pack.json: {uid} is {got.get(uid)!r}, pins.json says {v!r}")
    extra = set(got) - set(want)
    if extra:
        problems.append(f"mmc-pack.json: unexpected components {sorted(extra)}")
    if not any(c.get("uid") == "net.minecraft" and c.get("important") for c in pack.get("components", [])):
        problems.append("mmc-pack.json: net.minecraft should be marked important")
    return problems


def strip_single_top_folder(names: list[str]) -> str:
    tops = {n.split("/", 1)[0] for n in names}
    if len(tops) == 1 and all("/" in n for n in names):
        return tops.pop() + "/"
    return ""


# ---------------------------------------------------------------------------------------------- licences

def committed_licenses(packaging: Path) -> tuple[dict, list[str]]:
    """Licence texts committed in packaging/licenses, checked against licenses/SOURCES.json."""
    lic = packaging / "licenses"
    out, problems = {}, []
    try:
        spec = json.loads((lic / "SOURCES.json").read_text(encoding="utf-8"))
    except (OSError, ValueError) as e:
        return {}, [f"cannot read {lic / 'SOURCES.json'}: {e}"]
    for f in spec["files"]:
        p = lic / f["path"]
        if not p.is_file():
            problems.append(f"missing licence text packaging/licenses/{f['path']} (python3 packaging/tools/"
                            f"fetch_licenses.py)")
            continue
        got = file_hashes(p)["sha256"]
        if got != f["sha256"]:
            problems.append(f"packaging/licenses/{f['path']} changed (sha256 {got}, SOURCES.json {f['sha256']}); "
                            f"re-run python3 packaging/tools/fetch_licenses.py")
            continue
        if f.get("bundle", True):
            out[f["path"]] = {"src": p, "covers": f.get("covers", "")}
    return out, problems


def nuget_root(arg: str | None) -> Path:
    if arg:
        return Path(arg)
    if os.environ.get("NUGET_PACKAGES"):
        return Path(os.environ["NUGET_PACKAGES"])
    return Path.home() / ".nuget" / "packages"


def _nuspec(pkg_dir: Path) -> dict:
    out = {}
    specs = sorted(pkg_dir.glob("*.nuspec"))
    if not specs:
        return out
    text = specs[0].read_text(encoding="utf-8", errors="replace")
    for tag in ("id", "version", "authors", "copyright", "projectUrl", "licenseUrl"):
        m = re.search(rf"<{tag}>(.*?)</{tag}>", text, re.S)
        if m:
            out[tag] = m.group(1).strip()
    m = re.search(r'<license type="(expression|file)"[^>]*>(.*?)</license>', text, re.S)
    if m:
        out["licenseType"], out["license"] = m.group(1), m.group(2).strip()
    m = re.search(r'<repository [^>]*url="([^"]+)"', text)
    if m:
        out["repository"] = m.group(1)
    return out


def _unescape_xml(s: str) -> str:
    return s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", '"').replace("&apos;", "'").replace("&amp;", "&")


def extractor_notices(bundle: dict, nuget: Path, notices_md: str, vendor: Path) -> tuple[dict, list[str], dict]:
    """From the exe's own deps.json: a notice for every NuGet package with a file in the bundle, the vendor
    licences of every project, and the .NET runtime pack version. Returns (files for licenses/, problems, info)."""
    deps = bundle["deps"]
    problems, files = [], {}
    bundled = {Path(f["path"]).name.lower() for f in bundle["files"]}
    target = None
    for name, t in deps.get("targets", {}).items():
        if "/" in name:                      # the RID-specific target, e.g. .NETCoreApp,Version=v10.0/win-x64
            target = t
    target = target or next(iter(deps.get("targets", {}).values()), {})
    libs = deps.get("libraries", {})
    runtime_version = None
    accounted = {"cthextract.deps.json", "cthextract.runtimeconfig.json"}
    sections, projects, packages = [], [], []
    for key in sorted(libs, key=str.lower):
        meta = libs[key]
        name, ver = key.split("/", 1)
        assets = target.get(key, {})
        shipped = sorted({Path(a).name for kind in ("runtime", "native") for a in assets.get(kind, {})
                          if Path(a).name.lower() in bundled})
        accounted.update(s.lower() for s in shipped)
        kind = meta.get("type")
        if kind == "runtimepack" or name.startswith("runtimepack."):
            runtime_version = ver
            continue
        if kind == "project":
            if name not in PROJECT_VENDOR:
                problems.append(f"cthextract.exe contains project {name}, which build_package.py does not know: add "
                                f"it to PROJECT_VENDOR and its licence to THIRD-PARTY-NOTICES.md")
            else:
                projects.append((name, ver, PROJECT_VENDOR[name], shipped))
            continue
        if not shipped:
            continue                          # analyzers, meta-packages, natives not bundled
        pkg_dir = nuget / meta.get("path", f"{name.lower()}/{ver.lower()}")
        if not pkg_dir.is_dir():
            problems.append(f"NuGet package {name} {ver} is not in {nuget} (restore the extractor first, or pass "
                            f"--nuget): its licence text cannot be included")
            continue
        spec = _nuspec(pkg_dir)
        lic_text, lic_id, extra = None, None, []
        if spec.get("licenseType") == "file":
            lf = pkg_dir / spec["license"]
            if lf.is_file():
                lic_text = lf.read_text(encoding="utf-8", errors="replace")
                lic_id = f"see licence file {spec['license']}"
        elif spec.get("licenseType") == "expression":
            lic_id = spec["license"]
        if lic_id is None and name in NUGET_LICENSE_OVERRIDES:
            lic_id = NUGET_LICENSE_OVERRIDES[name]
        if lic_text is None:
            for cand in sorted(pkg_dir.iterdir()):
                if cand.is_file() and re.match(r"(?i)^(licen[cs]e|copying)(\.(txt|md))?$", cand.name):
                    lic_text = cand.read_text(encoding="utf-8", errors="replace")
                    break
        if lic_id is None and lic_text is None:
            problems.append(f"NuGet package {name} {ver}: no licence expression, file or override (licenseUrl "
                            f"{spec.get('licenseUrl')}); check it and add it to NUGET_LICENSE_OVERRIDES")
            continue
        chosen = lic_id or ""
        if " OR " in chosen:                  # dual-licensed: we take the first permissive option
            chosen = next((c.strip("() ") for c in chosen.split(" OR ") if c.strip("() ") in SPDX_TEMPLATES),
                          chosen.split(" OR ")[0].strip("() "))
        if lic_text is None:
            if chosen in SPDX_TEMPLATES:
                holder = _unescape_xml(spec.get("copyright") or f"Copyright (c) {spec.get('authors', name)}")
                if not holder.lower().startswith(("copyright", "(c)", "©")):
                    holder = f"Copyright (c) {holder}"
                lic_text = f"{holder}\n\n{SPDX_TEMPLATES[chosen]}"
            elif chosen in SPDX_SHARED_FILES:
                lic_text = f"Licensed under {chosen}: full text in {SPDX_SHARED_FILES[chosen]}.\n"
            else:
                problems.append(f"NuGet package {name} {ver}: licence {lic_id!r} has no text in the package and no "
                                f"template here; add the text")
                continue
        for cand in sorted(pkg_dir.iterdir()):
            if cand.is_file() and re.match(r"(?i)^(third-?party-?notices|notice)(\.(txt|md))?$", cand.name):
                extra.append((cand.name, cand.read_text(encoding="utf-8", errors="replace")))
        packages.append(name)
        head = [f"{name} {ver}",
                f"  licence: {lic_id}" + (f" (used here under {chosen})" if chosen and chosen != lic_id else ""),
                f"  copyright: {_unescape_xml(spec['copyright'])}" if spec.get("copyright") else None,
                f"  project: {spec.get('projectUrl') or spec.get('repository') or 'https://www.nuget.org/packages/' + name}",
                f"  in cthextract.exe as: {', '.join(shipped)}"]
        body = "\n".join(h for h in head if h) + "\n\n" + lic_text.strip() + "\n"
        for fname, text in extra:
            body += f"\n--- {name} {ver}: {fname} ---\n\n{text.strip()}\n"
        sections.append(body)
    unaccounted = sorted(n for n in bundled - accounted if not n.startswith("cthextract."))
    rt_names = set()
    if runtime_version:
        rt = target.get(f"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/{runtime_version}", {})
        rt_names = {Path(a).name.lower() for kind in ("runtime", "native") for a in rt.get(kind, {})}
    unaccounted = [n for n in unaccounted if n not in rt_names]
    if unaccounted:
        problems.append(f"cthextract.exe embeds files no deps.json library accounts for: {unaccounted}")
    # THIRD-PARTY-NOTICES.md must name every package and vendored project that ships, so it stays complete.
    md = notices_md.lower()
    missing = [p for p in packages if p.lower() not in md]
    missing += sorted({v for _, _, v, _ in projects if v and v.lower() not in md})
    if missing:
        problems.append(f"THIRD-PARTY-NOTICES.md does not mention {missing}: add them (licence + source)")
    header = ("Third-party .NET packages compiled into bin/cthextract.exe\n"
              "===========================================================\n\n"
              "Generated by packaging/build_package.py from the deps.json embedded in cthextract.exe: every NuGet\n"
              "package with at least one file inside the executable, with its licence text as published in the\n"
              "package (or, for packages that publish only an SPDX expression, that licence's standard text with the\n"
              "package's copyright line). Vendored source libraries are in licenses/extractor/vendor/, the .NET\n"
              "runtime's own notices in licenses/dotnet/.\n\n")
    files["licenses/extractor/nuget-packages.txt"] = (header + "\n\n".join(
        "-" * 100 + "\n" + s for s in sections)).encode("utf-8")
    # vendored libraries: LICENSE / NOTICE / COPYING / VENDORED.md of every extractor/vendor/<Name>
    for d in (sorted(p for p in vendor.iterdir() if p.is_dir()) if vendor.is_dir() else []):
        for f in sorted(d.iterdir()):
            if f.is_file() and re.match(r"(?i)^(licen[cs]e|notice|copying|vendored)(\.(txt|md))?$", f.name):
                files[f"licenses/extractor/vendor/{d.name}/{f.name}"] = f
    used = {v for _, _, v, _ in projects if v}
    for v in sorted(used):
        if not (vendor / v).is_dir():
            problems.append(f"cthextract.exe contains {v} code but extractor/vendor/{v} does not exist")
        elif not any(k.startswith(f"licenses/extractor/vendor/{v}/") and Path(k).name.lower().startswith(("lic", "copying"))
                     for k in files):
            problems.append(f"extractor/vendor/{v} has no LICENSE file")
    info = {"runtimeVersion": runtime_version, "packages": packages,
            "projects": [{"name": n, "version": v, "vendor": vd, "files": s} for n, v, vd, s in projects]}
    return files, problems, info


def dotnet_notices(runtime_version: str | None, nuget: Path, packaging: Path) -> tuple[dict, list[str]]:
    if not runtime_version:
        return {}, ["cthextract.exe's deps.json names no .NET runtime pack (is it self-contained?)"]
    tried = []
    for d in (nuget / "microsoft.netcore.app.runtime.win-x64" / runtime_version,
              packaging / "licenses" / "dotnet" / runtime_version):
        lic, tpn = d / "LICENSE.TXT", d / "THIRD-PARTY-NOTICES.TXT"
        tried.append(str(d))
        if lic.is_file() and tpn.is_file():
            log(f".NET runtime {runtime_version} notices from {d}")
            return {f"licenses/dotnet/runtime-{runtime_version}-LICENSE.TXT": lic,
                    f"licenses/dotnet/runtime-{runtime_version}-THIRD-PARTY-NOTICES.TXT": tpn}, []
    return {}, [f".NET runtime {runtime_version} LICENSE.TXT/THIRD-PARTY-NOTICES.TXT not found (tried {tried})"]


# ---------------------------------------------------------------------------------------------- zip writing

def write_reproducible_zip(path: Path, entries: dict, compress=True) -> list[dict]:
    """entries: archive path -> bytes | Path. Sorted, fixed time/attributes. Returns [{path,size,sha256}]."""
    out = []
    tmp = path.with_name(path.name + f".part{os.getpid()}")
    with zipfile.ZipFile(tmp, "w", allowZip64=True) as z:
        for name in sorted(entries):
            src = entries[name]
            if "\\" in name or name.startswith("/") or ".." in name.split("/"):
                raise BuildError(EXIT_CHECK, f"bad archive path {name!r}")
            zi = zipfile.ZipInfo(name, FIXED_TIME)
            zi.create_system = 3
            zi.external_attr = (0o100755 if name.endswith((".exe", ".dll")) else 0o100644) << 16
            store = not compress or name.endswith((".xz", ".gz", ".zip", ".jar"))
            zi.compress_type = zipfile.ZIP_STORED if store else zipfile.ZIP_DEFLATED
            h = hashlib.sha256()
            size = 0
            big = (len(src) if isinstance(src, (bytes, bytearray)) else Path(src).stat().st_size) > (1 << 31) - 1
            with z.open(zi, "w", force_zip64=big) as w:
                if isinstance(src, (bytes, bytearray)):
                    w.write(src)
                    h.update(src)
                    size = len(src)
                else:
                    with open(src, "rb") as f:
                        for chunk in iter(lambda: f.read(1 << 20), b""):
                            w.write(chunk)
                            h.update(chunk)
                            size += len(chunk)
            out.append({"path": name, "size": size, "sha256": h.hexdigest()})
    os.replace(tmp, path)
    return out


def self_check_zip(path: Path, expected: list[dict]) -> list[str]:
    problems = []
    with zipfile.ZipFile(path) as z:
        bad = z.testzip()
        if bad:
            problems.append(f"{path.name}: corrupt entry {bad}")
        infos = z.infolist()
        names = [i.filename for i in infos]
        if names != sorted(names):
            problems.append(f"{path.name}: entries are not sorted")
        if [e["path"] for e in expected] != names:
            problems.append(f"{path.name}: entry list differs from what was written")
        for i in infos:
            n = i.filename
            base = n.rsplit("/", 1)[-1]
            if "\\" in n:
                problems.append(f"{path.name}: backslash in {n}")
            if base in FORBIDDEN_NAMES:
                problems.append(f"{path.name}: must not contain {n}")
            if n.startswith(FORBIDDEN_PREFIXES):
                problems.append(f"{path.name}: must not contain {n} (Prism/Melty make it on the player's PC)")
            if Path(n).suffix.lower() in FORBIDDEN_EXTS:
                problems.append(f"{path.name}: {n} looks like game content, which must never ship")
            if i.date_time != FIXED_TIME:
                problems.append(f"{path.name}: {n} has timestamp {i.date_time}")
    return problems


# ---------------------------------------------------------------------------------------------- the build

def render(template: Path, values: dict) -> bytes:
    text = template.read_text(encoding="utf-8")
    try:
        out = string.Template(text).substitute(values)
    except (KeyError, ValueError) as e:
        raise BuildError(EXIT_INPUT, f"template {template.name}: {e}")
    return out.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8")   # Notepad-friendly


def licenses_index(entries: dict, covers: dict) -> bytes:
    lines = ["Licence texts and notices in this folder", "========================================", "",
             "THIRD-PARTY-NOTICES.md (one folder up) says which licence applies to which file; SOURCE.txt says",
             "where to get the source code of the GPL/LGPL programs. Files here:", ""]
    for name in sorted(n for n in entries if n.startswith("licenses/")):
        c = covers.get(name, "")
        lines.append(f"  {name[len('licenses/'):]}" + (f"\n      {c}" if c else ""))
    return ("\r\n".join(lines) + "\r\n").encode("utf-8")


def build(args) -> int:
    repo = Path(args.repo).resolve()
    packaging = HERE
    pins = load_pins(Path(args.pins) if args.pins else packaging / "pins.json")
    out = Path(args.out).resolve() if args.out else packaging / "out"
    downloads = Path(args.downloads).resolve() if args.downloads else packaging / "downloads"

    if args.fetch_only:
        for pin in pins["bundle"].values():
            fetch(pin, downloads, args.offline)
        for src in pins.get("sources", []) + pins.get("licenseSources", []):
            if "git" in src:
                git_source_tar(src, downloads, args.offline)
            else:
                fetch(src, downloads, args.offline)
        log(f"all pinned files are in {downloads}")
        return EXIT_OK

    # ---- 1. inputs that must exist before anything is downloaded; collect every problem, report them together
    problems = []
    libs = repo / "fabric" / "build" / "libs"
    if args.mod_jar:
        jar = Path(args.mod_jar)
        version = args.version or version_from_name(jar)
        log(f"mod jar: {jar} (--mod-jar)")
        if not jar.is_file():
            problems.append(f"--mod-jar {jar} does not exist")
        if not version:
            problems.append(f"cannot tell the version from {jar.name}; pass --version")
    else:
        jar, version, notes = find_mod_jar(libs, args.version)
        for n in notes:
            log(f"mod jar: {n}")
        if jar is None:
            problems.append(f"the mod jar is missing: no {MOD_ID}-{args.version or '<version>'}.jar in {libs}. "
                            f"Build it first: cd fabric && ./gradlew build")
    if version and not VERSION_RE.match(version):
        problems.append(f"version {version!r} is not like 1.2.3")
    if jar is not None and jar.is_file() and version:
        problems += check_mod_jar(jar, version)

    exe = Path(args.extractor) if args.extractor else repo / "extractor" / "dist" / "cthextract.exe"
    bundle = None
    if not exe.is_file():
        problems.append(f"the extractor is missing: {exe} does not exist. Build it first: extractor/build.sh")
    else:
        p, bundle = check_extractor(exe)
        problems += p

    required = {"LICENSE": "the project licence (CC BY-NC-SA 4.0)",
                "extractor/LICENSE": "the extractor licence (GPL-3.0)",
                "THIRD-PARTY-NOTICES.md": "the third-party notices"}
    for rel, what in required.items():
        if not (repo / rel).is_file():
            problems.append(f"{rel} is missing ({what})")
    lic_files, p = committed_licenses(packaging)
    problems += p

    inst = packaging / "instance"
    try:
        cfg_text = (inst / "instance.cfg").read_text(encoding="utf-8")
        pack_bytes = (inst / "mmc-pack.json").read_bytes()
        if pack_bytes.startswith(b"\xef\xbb\xbf"):
            problems.append("instance/mmc-pack.json starts with a UTF-8 BOM")
        pack = json.loads(pack_bytes.decode("utf-8-sig"))
        problems += check_instance_files(cfg_text, pack, pins)
    except (OSError, ValueError) as e:
        problems.append(f"packaging/instance files unreadable: {e}")
        cfg_text, pack_bytes = "", b""

    nuget = nuget_root(args.nuget)
    extra_files, info = {}, {}
    if bundle is not None and (repo / "THIRD-PARTY-NOTICES.md").is_file():
        notices_md = (repo / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8")
        f1, p1, info = extractor_notices(bundle, nuget, notices_md, repo / "extractor" / "vendor")
        f2, p2 = dotnet_notices(info.get("runtimeVersion"), nuget, packaging)
        extra_files.update(f1)
        extra_files.update(f2)
        problems += p1 + p2

    if problems:
        if args.allow_incomplete_notices:
            notice_like = [x for x in problems if "NuGet" in x or "THIRD-PARTY" in x or ".NET runtime" in x]
            for x in notice_like:
                log(f"WARNING (--allow-incomplete-notices): {x}")
            problems = [x for x in problems if x not in notice_like]
    if problems:
        log("cannot build the package; nothing was downloaded or written:")
        for x in problems:
            log(f"  - {x}")
        return EXIT_INPUT

    commit, dirty = repo_commit(repo)
    if dirty and args.release:
        log("--release: the repository has uncommitted changes; commit them so SOURCE.txt names the exact source")
        return EXIT_INPUT
    if args.release and (stamp_problem := extractor_stamp_problem(repo, exe)):
        log("--release: " + stamp_problem)
        return EXIT_INPUT
    commit_label = commit + ("+dirty" if dirty else "")
    if dirty:
        log(f"note: uncommitted changes in {repo}; SOURCE.txt will say commit {commit_label}")

    # ---- 2. pinned downloads
    prism_zip = fetch(pins["bundle"]["prism"], downloads, args.offline)
    prism_license = fetch(pins["bundle"]["prismLicense"], downloads, args.offline)
    prism_copying = fetch(pins["bundle"]["prismCopying"], downloads, args.offline)
    fabric_api = fetch(pins["bundle"]["fabricApi"], downloads, args.offline)

    # ---- 3. assemble
    entries: dict = {}
    covers: dict = {}
    with zipfile.ZipFile(prism_zip) as pz:
        infos = [i for i in pz.infolist() if not i.is_dir()]
        names = [i.filename.replace("\\", "/") for i in infos]
        top = strip_single_top_folder(names)
        for i, n in zip(infos, names):
            rel = n[len(top):]
            base = rel.rsplit("/", 1)[-1]
            if base in FORBIDDEN_NAMES or rel.startswith("instances/"):
                raise BuildError(EXIT_CHECK, f"the Prism zip contains {rel}: refusing a non-pristine Prism build")
            entries["Prism/" + rel] = pz.read(i)
    for must in ("Prism/prismlauncher.exe", "Prism/portable.txt"):
        if must not in entries:
            raise BuildError(EXIT_CHECK, f"the Prism zip has no {must[6:]}: not a portable build")
    log(f"Prism {pins['versions']['prism']}: {len(infos)} files copied unmodified")
    entries["Prism/LICENSE-PrismLauncher.txt"] = prism_license
    entries["Prism/COPYING-PrismLauncher.md"] = prism_copying
    # Prism's self-updater reads this from its data folder (Prism/ in portable mode); it only runs when Prism's main
    # window opens (never under --launch). Off, so opening Prism by hand never replaces the pinned build.
    entries["Prism/prismlauncher_update.cfg"] = b"[General]\r\nauto_check=false\r\n"
    entries[f"{INSTANCE_DIR}/instance.cfg"] = cfg_text.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8")
    entries[f"{INSTANCE_DIR}/mmc-pack.json"] = pack_bytes
    entries[f"{MODS_DIR}/fabric-api.jar"] = fabric_api
    entries["bin/cthextract.exe"] = exe
    entries["THIRD-PARTY-NOTICES.md"] = repo / "THIRD-PARTY-NOTICES.md"
    entries["licenses/CC-BY-NC-SA-4.0.txt"] = repo / "LICENSE"
    covers["licenses/CC-BY-NC-SA-4.0.txt"] = "Craft Theft Halo: the mod (the crafttheft jar) and our own files here"
    entries["licenses/GPL-3.0.txt"] = repo / "extractor" / "LICENSE"
    covers["licenses/GPL-3.0.txt"] = "bin/cthextract.exe (GPL-3.0-only); Prism Launcher; Qt Network Authorization"
    for rel, meta in lic_files.items():
        entries[f"licenses/{rel}"] = meta["src"]
        covers[f"licenses/{rel}"] = meta["covers"]
    for k, v in extra_files.items():
        entries[k] = v
        if k.endswith("nuget-packages.txt"):
            covers[k] = "NuGet packages compiled into bin/cthextract.exe"
        elif k.startswith("licenses/dotnet/"):
            covers[k] = "the .NET runtime compiled into bin/cthextract.exe"
        elif k.startswith("licenses/extractor/vendor/"):
            covers[k] = f"extractor/vendor/{k.split('/')[3]}, compiled into bin/cthextract.exe"

    exe_sha = file_hashes(exe)["sha256"]
    values = {
        "version": version, "commit": commit_label, "repo": GITHUB_REPO,
        "prism": pins["versions"]["prism"], "minecraft": pins["versions"]["minecraft"],
        "loader": pins["versions"]["fabricLoader"], "fabricapi": pins["versions"]["fabricApi"],
        "lwjgl": pins["versions"]["lwjgl"], "qt": pins["versions"]["qt"],
        "vcpkg": pins["versions"]["vcpkgBaseline"], "exe_sha256": exe_sha,
        "runtime": info.get("runtimeVersion") or "unknown",
        "sources_zip": SOURCES_NAME.format(version=version),
        "sources_list": "\n".join(_source_line(s) for s in pins.get("sources", [])),
    }
    entries["SOURCE.txt"] = render(packaging / "templates" / "SOURCE.txt", values)
    entries["README.txt"] = render(packaging / "templates" / "README.txt", values)
    entries["bundle-version.txt"] = render(packaging / "templates" / "bundle-version.txt", values)
    entries["licenses/README.txt"] = licenses_index(entries, covers)

    # ---- 4. write
    out.mkdir(parents=True, exist_ok=True)
    zip_path = out / ZIP_NAME.format(version=version)
    jar_path = out / JAR_NAME.format(version=version)
    log(f"writing {zip_path} ({len(entries)} entries)")
    zip_entries = write_reproducible_zip(zip_path, entries)
    shutil.copyfile(jar, jar_path)
    problems = self_check_zip(zip_path, zip_entries)
    problems += check_mod_jar(jar_path, version)
    if problems:
        for x in problems:
            log(f"CHECK FAILED: {x}")
        return EXIT_CHECK

    jar_sha = file_hashes(jar_path)["sha256"]
    zip_sha = file_hashes(zip_path)["sha256"]
    artifacts = [
        {"component": "main", "file": jar_path.name, "size": jar_path.stat().st_size, "sha256": jar_sha,
         "entries": [{"path": jar_path.name, "size": jar_path.stat().st_size, "sha256": jar_sha}]},
        {"component": "minecraft", "file": zip_path.name, "size": zip_path.stat().st_size, "sha256": zip_sha,
         "entries": zip_entries},
    ]
    if args.sources:
        src_path = build_sources(pins, version, repo, commit, dirty, downloads, out, args.offline, entries["SOURCE.txt"])
        artifacts.append({"component": None, "file": src_path.name, "size": src_path.stat().st_size,
                          "sha256": file_hashes(src_path)["sha256"], "entries": []})
    melty_entries = {a["component"]: [{"path": e["path"], "size": e["size"]} for e in a["entries"]]
                     for a in artifacts if a["component"]}
    (out / "entries.json").write_text(json.dumps(melty_entries, indent=1) + "\n", encoding="utf-8")
    release = {"version": version, "commit": commit_label, "artifacts": artifacts,
               "pins": {k: v["sha256"] for k, v in pins["bundle"].items()},
               "extractor": {"sha256": exe_sha, "runtime": info.get("runtimeVersion"),
                             "packages": info.get("packages"), "projects": info.get("projects")}}
    (out / "release.json").write_text(json.dumps(release, indent=1) + "\n", encoding="utf-8")
    (out / "SHA256SUMS").write_text("".join(f"{a['sha256']}  {a['file']}\n" for a in artifacts), encoding="utf-8")
    print_report(artifacts, args.json, release)
    return EXIT_OK


def _source_line(s: dict) -> str:
    where = s["urls"][0] if "urls" in s else f"{s['git']} at commit {s['commit']}"
    check = f"sha256 {s['sha256']}" if "sha256" in s else "git trees:" + "".join(
        f"\n      {p or '/'} {t}" for p, t in sorted(s["trees"].items()))
    return (f"- {s['name']} [{s['license']}]\n    for: {s['covers']}\n    {where}\n    {check}\n"
            f"    in the mirror zip as: upstream/{s['file']}")


def build_sources(pins, version, repo, commit, dirty, downloads, out, offline, source_txt) -> Path:
    entries = {}
    for s in pins.get("sources", []):
        p = git_source_tar(s, downloads, offline) if "git" in s else fetch(s, downloads, offline)
        entries[f"upstream/{s['file']}"] = p
    if commit == "unknown":
        raise BuildError(EXIT_INPUT, "--sources needs the project as a git repository (for git archive)")
    if dirty:
        log("WARNING: uncommitted changes are NOT in the project source archive; commit before a release")
    own = downloads / f"fort-halo-theft-auto-5-{commit[:12]}.tar.gz"
    with open(own, "wb") as f:
        r = subprocess.run(["git", "archive", "--format=tar.gz", f"--prefix=fort-halo-theft-auto-5-{commit[:12]}/",
                            commit], cwd=str(repo), stdout=f, stderr=subprocess.PIPE)
    if r.returncode != 0:
        raise BuildError(EXIT_INPUT, f"git archive of the project failed: {r.stderr.decode(errors='replace')}")
    entries[f"crafttheft/{own.name}"] = own
    entries["SOURCE.txt"] = source_txt
    sums = []
    for k in sorted(entries):
        v = entries[k]
        sums.append(f"{sha256_bytes(v) if isinstance(v, bytes) else file_hashes(v)['sha256']}  {k}\n")
    entries["SHA256SUMS"] = "".join(sums).encode("utf-8")
    path = out / SOURCES_NAME.format(version=version)
    log(f"writing {path} ({len(entries)} entries)")
    write_reproducible_zip(path, entries, compress=False)
    return path


def print_report(artifacts, as_json, release) -> None:
    if as_json:
        print(json.dumps(release, indent=1))
        return
    for a in artifacts:
        print(f"{a['file']}  {a['size']:,} bytes  sha256 {a['sha256']}"
              + (f"  (Melty component \"{a['component']}\")" if a["component"] else "  (upload with the release)"))
        if a["entries"] and a["component"] != "main":
            total = sum(e["size"] for e in a["entries"])
            print(f"  {len(a['entries'])} entries, {total:,} bytes unpacked:")
            for e in a["entries"]:
                print(f"  {e['size']:>12,}  {e['sha256']}  {e['path']}")
        print()


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0],
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", help="release version (default: from fabric/build/libs/crafttheft-<version>.jar)")
    ap.add_argument("--out", help="output folder (default packaging/out)")
    ap.add_argument("--downloads", help="download cache (default packaging/downloads)")
    ap.add_argument("--offline", action="store_true", help="never download; use only the download cache")
    ap.add_argument("--fetch-only", action="store_true", help="download and verify every pinned file, then stop")
    ap.add_argument("--sources", action="store_true", help="also build the corresponding-source zip for the release")
    ap.add_argument("--release", action="store_true", help="refuse a repository with uncommitted changes")
    ap.add_argument("--mod-jar", help="use this jar instead of fabric/build/libs/crafttheft-<version>.jar")
    ap.add_argument("--extractor", help="use this exe instead of extractor/dist/cthextract.exe")
    ap.add_argument("--nuget", help="NuGet package folder (default $NUGET_PACKAGES or ~/.nuget/packages)")
    ap.add_argument("--repo", default=str(DEFAULT_REPO), help=argparse.SUPPRESS)
    ap.add_argument("--pins", help=argparse.SUPPRESS)
    ap.add_argument("--allow-incomplete-notices", action="store_true",
                    help="warn instead of failing when NuGet/.NET notices cannot be gathered (never for a release)")
    ap.add_argument("--json", action="store_true", help="print the release report as JSON")
    try:
        args = ap.parse_args(argv)
    except SystemExit as e:
        return EXIT_ARGS if e.code else EXIT_OK
    if args.release and args.allow_incomplete_notices:
        log("--release and --allow-incomplete-notices do not go together")
        return EXIT_ARGS
    if args.version and not VERSION_RE.match(args.version):
        log(f"--version {args.version!r} is not like 1.2.3")
        return EXIT_ARGS
    try:
        return build(args)
    except BuildError as e:
        log(f"ERROR: {e}")
        return e.code


if __name__ == "__main__":
    sys.exit(main())
