"""Tests for packaging/build_package.py and the committed packaging files.

    python3 -m unittest discover -s packaging/tests -v

Every fixture is generated here (fake Prism zip, fake Fabric API jar, a fake PE + .NET single-file bundle, a fake
NuGet folder, local git repositories as "upstream"); downloads go through file:// URLs. No network, no game files.
The real committed files (packaging/instance, packaging/licenses, packaging/templates, packaging/melty.recipe.json) are checked too.
"""
from __future__ import annotations

import contextlib
import fnmatch
import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

PACKAGING = Path(__file__).resolve().parent.parent
REPO = PACKAGING.parent
sys.path.insert(0, str(PACKAGING))
import build_package as bp  # noqa: E402

VERSION = "0.1.0"
HAVE_GIT = shutil.which("git") is not None


def sha256(b: bytes) -> str:
    return hashlib.sha256(b).hexdigest()


def zip_bytes(files: dict) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for n, d in files.items():
            z.writestr(n, d)
    return buf.getvalue()


def str7(s: str) -> bytes:
    b = s.encode("utf-8")
    n, out = len(b), bytearray()
    while True:
        byte = n & 0x7F
        n >>= 7
        out.append(byte | (0x80 if n else 0))
        if not n:
            break
    return bytes(out) + b


def fake_pe(subsystem=2, machine=0x8664) -> bytearray:
    b = bytearray(0x200)
    b[0:2] = b"MZ"
    struct.pack_into("<I", b, 0x3C, 0x80)
    b[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<H", b, 0x84, machine)
    struct.pack_into("<H", b, 0x80 + 24 + 68, subsystem)
    return b


def fake_deps(extra_project=None) -> dict:
    t = {
        "cthextract/0.1.0": {"dependencies": {}, "runtime": {"cthextract.dll": {}}},
        "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": {
            "runtime": {"System.Private.CoreLib.dll": {}, "System.Runtime.dll": {}}},
        "Newtonsoft.Json/13.0.4": {"runtime": {"lib/net6.0/Newtonsoft.Json.dll": {}}},
        "GenericReader/2.3.0": {"runtime": {"lib/net9.0/GenericReader.dll": {}}},
        "MemoryPack.Generator/1.21.4": {},
        "CthExtract.Core/0.1.0": {"runtime": {"CthExtract.Core.dll": {}}},
        "Reclaimer.Core/1.0.0": {"runtime": {"Reclaimer.Core.dll": {}}},
    }
    libs = {
        "cthextract/0.1.0": {"type": "project", "serviceable": False, "sha512": ""},
        "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": {"type": "runtimepack", "serviceable": False},
        "Newtonsoft.Json/13.0.4": {"type": "package", "path": "newtonsoft.json/13.0.4"},
        "GenericReader/2.3.0": {"type": "package", "path": "genericreader/2.3.0"},
        "MemoryPack.Generator/1.21.4": {"type": "package", "path": "memorypack.generator/1.21.4"},
        "CthExtract.Core/0.1.0": {"type": "project"},
        "Reclaimer.Core/1.0.0": {"type": "project"},
    }
    if extra_project:
        t[f"{extra_project}/1.0.0"] = {"runtime": {f"{extra_project}.dll": {}}}
        libs[f"{extra_project}/1.0.0"] = {"type": "project"}
    return {"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64"},
            "targets": {".NETCoreApp,Version=v10.0": {}, ".NETCoreApp,Version=v10.0/win-x64": t},
            "libraries": libs}


def fake_bundle_exe(subsystem=2, extra_project=None, stray_file=None) -> bytes:
    deps = fake_deps(extra_project)
    rc = {"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [
        {"name": "Microsoft.NETCore.App", "version": "10.0.12"}]}}
    files = [("cthextract.dll", 1, b"MZ main"), ("System.Private.CoreLib.dll", 1, b"MZ corelib"),
             ("System.Runtime.dll", 1, b"MZ rt"), ("Newtonsoft.Json.dll", 1, b"MZ json"),
             ("GenericReader.dll", 1, b"MZ gr"), ("CthExtract.Core.dll", 1, b"MZ core"),
             ("Reclaimer.Core.dll", 1, b"MZ reclaimer"),
             ("cthextract.deps.json", 3, json.dumps(deps).encode()),
             ("cthextract.runtimeconfig.json", 4, json.dumps(rc).encode())]
    if extra_project:
        files.append((f"{extra_project}.dll", 1, b"MZ extra"))
    if stray_file:
        files.append((stray_file, 1, b"MZ stray"))
    data = fake_pe(subsystem)
    marker = len(data)
    data += b"\0" * 8 + bp.BUNDLE_SIGNATURE + b"\0" * 64
    entries = []
    for name, t, content in files:
        entries.append((len(data), len(content), t, name))
        data += content
    hdr = len(data)
    dj = next(e for e in entries if e[2] == 3)
    rce = next(e for e in entries if e[2] == 4)
    h = struct.pack("<IIi", 6, 0, len(entries)) + str7("synthetic-bundle")
    h += struct.pack("<qqqqQ", dj[0], dj[1], rce[0], rce[1], 0)
    for off, size, t, name in entries:
        h += struct.pack("<qqq", off, size, 0) + bytes([t]) + str7(name)
    data += h
    struct.pack_into("<q", data, marker, hdr)
    return bytes(data)


def mod_jar(version=VERSION, mod_id="crafttheft") -> bytes:
    fmj = {"schemaVersion": 1, "id": mod_id, "version": version, "license": "CC-BY-NC-SA-4.0"}
    return zip_bytes({"fabric.mod.json": json.dumps(fmj), "dev/crafttheft/Main.class": b"\xca\xfe\xba\xbe"})


NOTICES_MD = "# notices\nNewtonsoft.Json, GenericReader, Reclaimer\n"


class Fixture:
    """A fake repository + pins + NuGet folder in a temp dir."""

    def __init__(self, root: Path, prism_files=None, jar=True, exe=True, exe_kwargs=None, notices=NOTICES_MD):
        self.root = root
        self.repo = root / "repo"
        self.up = root / "upstream"
        self.nuget = root / "nuget"
        self.downloads = root / "downloads"
        self.out = root / "out"
        for d in (self.repo, self.up, self.nuget):
            d.mkdir(parents=True, exist_ok=True)
        r = self.repo
        (r / "LICENSE").write_text("CC BY-NC-SA 4.0 (fixture)\n")
        (r / "extractor" / "vendor" / "Reclaimer").mkdir(parents=True)
        (r / "extractor" / "LICENSE").write_text("GPL-3.0 (fixture)\n")
        (r / "extractor" / "vendor" / "Reclaimer" / "LICENSE").write_text("GPL-3.0 Reclaimer (fixture)\n")
        (r / "extractor" / "vendor" / "Reclaimer" / "VENDORED.md").write_text("# Reclaimer (fixture)\n")
        (r / "THIRD-PARTY-NOTICES.md").write_text(notices)
        if exe:
            dist = r / "extractor" / "dist"
            dist.mkdir(parents=True)
            e = fake_bundle_exe(**(exe_kwargs or {}))
            (dist / "cthextract.exe").write_bytes(e)
            (dist / "cthextract.exe.sha256").write_text(f"{sha256(e)}  cthextract.exe\n")
        if jar:
            libs = r / "fabric" / "build" / "libs"
            libs.mkdir(parents=True)
            (libs / f"crafttheft-{VERSION}.jar").write_bytes(mod_jar())
            (libs / f"crafttheft-{VERSION}-sources.jar").write_bytes(zip_bytes({"a.java": "x"}))
            (libs / f"crafttheft-{VERSION}-harness.jar").write_bytes(mod_jar(mod_id="crafttheft_harness"))
        # NuGet: one package with a licence file, one with only an SPDX expression, the runtime pack
        p = self.nuget / "newtonsoft.json" / "13.0.4"
        p.mkdir(parents=True)
        (p / "newtonsoft.json.nuspec").write_text(
            '<package><metadata><id>Newtonsoft.Json</id><version>13.0.4</version><authors>James</authors>'
            '<license type="expression">MIT</license><copyright>Copyright © James Newton-King 2008</copyright>'
            '<projectUrl>https://www.newtonsoft.com/json</projectUrl></metadata></package>', encoding="utf-8")
        (p / "LICENSE.md").write_text("The MIT License (MIT)\n\nCopyright (c) 2007 James Newton-King\n(fixture)\n")
        g = self.nuget / "genericreader" / "2.3.0"
        g.mkdir(parents=True)
        (g / "genericreader.nuspec").write_text(
            '<package><metadata><id>GenericReader</id><version>2.3.0</version><authors>NotOfficer</authors>'
            '<license type="expression">MIT</license><copyright>Copyright (c) 2025 NotOfficer</copyright>'
            '</metadata></package>')
        rt = self.nuget / "microsoft.netcore.app.runtime.win-x64" / "10.0.12"
        rt.mkdir(parents=True)
        (rt / "LICENSE.TXT").write_text("The MIT License (fixture runtime)\n")
        (rt / "THIRD-PARTY-NOTICES.TXT").write_text(".NET third-party notices (fixture)\n")
        # upstream files behind file:// URLs
        prism = prism_files or {"prismlauncher.exe": b"MZ prism", "portable.txt": b"portable mode\r\n",
                                "Qt6Core.dll": b"MZ qt", "jars/NewLaunch.jar": zip_bytes({"x.class": b"1"})}
        uploads = {
            "prism": ("PrismLauncher-Windows-MSVC-Portable-11.1.1.zip", zip_bytes(prism)),
            "prismLicense": ("PrismLauncher-11.1.1-LICENSE.txt", b"GPL-3.0 text (fixture)\n"),
            "prismCopying": ("PrismLauncher-11.1.1-COPYING.md", b"# Prism notices (fixture)\n"),
            "fabricApi": ("fabric-api-0.116.17+1.21.1.jar", zip_bytes({"fabric.mod.json": '{"id":"fabric-api"}'})),
        }
        real = json.loads((PACKAGING / "pins.json").read_text())
        pins = {"versions": real["versions"], "bundle": {}, "sources": [], "licenseSources": []}
        for key, (name, data) in uploads.items():
            (self.up / name).write_bytes(data)
            pins["bundle"][key] = {"name": key, "license": "x", "file": name,
                                   "urls": [(self.up / name).as_uri()], "size": len(data), "sha256": sha256(data)}
        self.pins_data = pins
        self.pins = root / "pins.json"
        self.write_pins()

    def write_pins(self):
        self.pins.write_text(json.dumps(self.pins_data, indent=1))

    def args(self, *extra):
        return ["--repo", str(self.repo), "--pins", str(self.pins), "--nuget", str(self.nuget),
                "--downloads", str(self.downloads), "--out", str(self.out), *extra]


def run(argv) -> tuple[int, str, str]:
    err, out = io.StringIO(), io.StringIO()
    with contextlib.redirect_stderr(err), contextlib.redirect_stdout(out):
        code = bp.main(argv)
    return code, out.getvalue(), err.getvalue()


def git_init_commit(path: Path, files: dict) -> str:
    path.mkdir(parents=True, exist_ok=True)
    for n, d in files.items():
        f = path / n
        f.parent.mkdir(parents=True, exist_ok=True)
        f.write_bytes(d if isinstance(d, bytes) else d.encode())
    env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@t", GIT_COMMITTER_NAME="t",
               GIT_COMMITTER_EMAIL="t@t", GIT_AUTHOR_DATE="2026-01-01T00:00:00Z",
               GIT_COMMITTER_DATE="2026-01-01T00:00:00Z")
    for cmd in (["init", "-q"], ["add", "-A"], ["commit", "-q", "-m", "fixture"]):
        subprocess.run(["git", *cmd], cwd=path, check=True, env=env, capture_output=True)
    subprocess.run(["git", "config", "uploadpack.allowFilter", "true"], cwd=path, check=True)
    subprocess.run(["git", "config", "uploadpack.allowAnySHA1InWant", "true"], cwd=path, check=True)
    return subprocess.run(["git", "rev-parse", "HEAD"], cwd=path, check=True, capture_output=True,
                          text=True).stdout.strip()


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="cth-pkg-test-"))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_full_build(self):
        fx = Fixture(self.tmp)
        code, out, err = run(fx.args())
        self.assertEqual(code, 0, err)
        z = fx.out / f"CraftTheftHalo-Minecraft-{VERSION}.zip"
        jar = fx.out / f"crafttheft-{VERSION}.jar"
        self.assertTrue(z.is_file() and jar.is_file())
        self.assertIn("picked crafttheft-0.1.0.jar", err)
        self.assertIn("ignored crafttheft-0.1.0-sources.jar", err)
        self.assertIn("ignored crafttheft-0.1.0-harness.jar", err)       # the test mod never ships
        with zipfile.ZipFile(z) as zf:
            names = zf.namelist()
            self.assertEqual(names, sorted(names))
            for must in ("Prism/prismlauncher.exe", "Prism/portable.txt", "Prism/jars/NewLaunch.jar",
                         "Prism/LICENSE-PrismLauncher.txt", "Prism/COPYING-PrismLauncher.md",
                         "Prism/prismlauncher_update.cfg",
                         "Prism/instances/CraftTheftHalo/instance.cfg",
                         "Prism/instances/CraftTheftHalo/mmc-pack.json",
                         "Prism/instances/CraftTheftHalo/.minecraft/mods/fabric-api.jar",
                         "bin/cthextract.exe", "THIRD-PARTY-NOTICES.md", "SOURCE.txt", "README.txt",
                         "bundle-version.txt", "licenses/README.txt", "licenses/CC-BY-NC-SA-4.0.txt",
                         "licenses/GPL-3.0.txt", "licenses/LGPL-3.0.txt", "licenses/qt/qt-6.10.2-third-party.txt",
                         "licenses/extractor/nuget-packages.txt", "licenses/extractor/vendor/Reclaimer/LICENSE",
                         "licenses/dotnet/runtime-10.0.12-THIRD-PARTY-NOTICES.TXT"):
                self.assertIn(must, names)
            for n in names:
                self.assertNotIn("\\", n)
                self.assertFalse(n.endswith(("accounts.json", "prismlauncher.cfg", "options.txt")), n)
                self.assertFalse(n.startswith("cache/"), n)
            self.assertFalse(any("dotnet/10.0.12/" in n for n in names), "fallback copies are not bundled")
            cfg = zf.read("Prism/instances/CraftTheftHalo/instance.cfg")
            self.assertIn(b"\r\n", cfg)
            self.assertNotIn(b"ConfigVersion", cfg)
            self.assertIn(b"LowMemWarning=false", cfg)
            self.assertIn(b"MaxMemAlloc=4096", cfg)
            pack = zf.read("Prism/instances/CraftTheftHalo/mmc-pack.json")
            self.assertFalse(pack.startswith(b"\xef\xbb\xbf"))
            self.assertEqual(json.loads(pack)["formatVersion"], 1)
            nuget = zf.read("licenses/extractor/nuget-packages.txt").decode()
            self.assertIn("Newtonsoft.Json 13.0.4", nuget)
            self.assertIn("(fixture)", nuget)                          # the package's own LICENSE.md
            self.assertIn("Copyright (c) 2025 NotOfficer", nuget)       # synthesised MIT text
            self.assertIn("Permission is hereby granted", nuget)
            self.assertNotIn("MemoryPack.Generator", nuget)              # analyzer: nothing in the exe
            src = zf.read("SOURCE.txt").decode()
            self.assertIn(VERSION, src)
            self.assertIn("Prism Launcher 11.1.1", src)
            idx = zf.read("licenses/README.txt").decode()
            self.assertIn("prism-libs/lzo-2.10-COPYING.txt", idx)
            for i in zf.infolist():
                self.assertEqual(i.date_time, bp.FIXED_TIME)
        ent = json.loads((fx.out / "entries.json").read_text())
        self.assertEqual(set(ent), {"main", "minecraft"})
        self.assertEqual(ent["main"], [{"path": f"crafttheft-{VERSION}.jar", "size": jar.stat().st_size}])
        self.assertTrue(all(set(e) == {"path", "size"} for e in ent["minecraft"]))
        sums = (fx.out / "SHA256SUMS").read_text()
        self.assertIn(f"{sha256(z.read_bytes())}  {z.name}", sums)
        self.assertIn(f"sha256 {sha256(z.read_bytes())}", out)          # the printed report
        self.assertIn("bin/cthextract.exe", out)

    def test_reproducible(self):
        fx = Fixture(self.tmp)
        self.assertEqual(run(fx.args())[0], 0)
        first = (fx.out / f"CraftTheftHalo-Minecraft-{VERSION}.zip").read_bytes()
        fx.out = self.tmp / "out2"
        self.assertEqual(run(fx.args())[0], 0)
        second = (fx.out / f"CraftTheftHalo-Minecraft-{VERSION}.zip").read_bytes()
        self.assertEqual(sha256(first), sha256(second))

    def test_missing_jar_and_exe_reported_together_before_downloading(self):
        fx = Fixture(self.tmp, jar=False, exe=False)
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("the mod jar is missing", err)
        self.assertIn("./gradlew build", err)
        self.assertIn("the extractor is missing", err)
        self.assertIn("extractor/build.sh", err)
        self.assertFalse(fx.downloads.exists() and any(fx.downloads.iterdir()), "nothing may be downloaded")
        self.assertFalse(fx.out.exists())

    def test_hash_mismatch_is_refused(self):
        fx = Fixture(self.tmp)
        fx.pins_data["bundle"]["fabricApi"]["sha256"] = "0" * 64
        fx.write_pins()
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_DOWNLOAD)
        self.assertIn("does not match the pin", err)
        self.assertFalse((fx.downloads / "fabric-api-0.116.17+1.21.1.jar").exists())

    def test_corrupt_cached_file_is_replaced(self):
        fx = Fixture(self.tmp)
        fx.downloads.mkdir()
        (fx.downloads / "fabric-api-0.116.17+1.21.1.jar").write_bytes(b"truncated")
        code, _, err = run(fx.args())
        self.assertEqual(code, 0, err)
        self.assertIn("does not match its pin", err)

    def test_offline_without_cache_fails(self):
        fx = Fixture(self.tmp)
        code, _, err = run(fx.args("--offline"))
        self.assertEqual(code, bp.EXIT_DOWNLOAD)
        self.assertIn("--offline", err)

    def test_offline_with_cache_works(self):
        fx = Fixture(self.tmp)
        self.assertEqual(run(fx.args("--fetch-only"))[0], 0)
        for p in fx.up.iterdir():
            p.unlink()                                   # upstream gone: only the cache is left
        code, _, err = run(fx.args("--offline"))
        self.assertEqual(code, 0, err)

    def test_notices_must_name_every_package(self):
        fx = Fixture(self.tmp, notices="# notices\nReclaimer only\n")
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("THIRD-PARTY-NOTICES.md does not mention", err)
        self.assertIn("Newtonsoft.Json", err)
        code, _, err = run(fx.args("--allow-incomplete-notices"))
        self.assertEqual(code, 0, err)
        self.assertIn("WARNING (--allow-incomplete-notices)", err)

    def test_unknown_project_in_exe_is_refused(self):
        fx = Fixture(self.tmp, exe_kwargs={"extra_project": "Mystery.Lib"})
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("Mystery.Lib", err)

    def test_unaccounted_bundled_file_is_refused(self):
        fx = Fixture(self.tmp, exe_kwargs={"stray_file": "Stray.dll"})
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("Stray.dll".lower(), err.lower())

    def test_missing_nuget_package_is_refused(self):
        fx = Fixture(self.tmp)
        shutil.rmtree(fx.nuget / "genericreader")
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("GenericReader 2.3.0 is not in", err)

    def test_console_exe_is_refused(self):
        fx = Fixture(self.tmp, exe_kwargs={"subsystem": 3})
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("expected 2 (GUI", err)

    def test_exe_hash_sidecar_mismatch_is_refused(self):
        fx = Fixture(self.tmp)
        (fx.repo / "extractor" / "dist" / "cthextract.exe.sha256").write_text("0" * 64 + "  cthextract.exe\n")
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("rebuild with extractor/build.sh", err)

    def test_unexpanded_jar_version_is_refused(self):
        fx = Fixture(self.tmp)
        (fx.repo / "fabric" / "build" / "libs" / f"crafttheft-{VERSION}.jar").write_bytes(mod_jar("${version}"))
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("processResources", err)

    def test_two_jar_versions_need_version_flag(self):
        fx = Fixture(self.tmp)
        (fx.repo / "fabric" / "build" / "libs" / "crafttheft-0.2.0.jar").write_bytes(mod_jar("0.2.0"))
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("more than one version", err)
        code, _, err = run(fx.args("--version", "0.2.0"))
        self.assertEqual(code, 0, err)
        self.assertTrue((fx.out / "CraftTheftHalo-Minecraft-0.2.0.zip").is_file())

    def test_non_pristine_prism_zip_is_refused(self):
        fx = Fixture(self.tmp, prism_files={"prismlauncher.exe": b"MZ", "portable.txt": b"p",
                                             "accounts.json": b"{}"})
        code, _, err = run(fx.args())
        self.assertEqual(code, bp.EXIT_CHECK)
        self.assertIn("accounts.json", err)

    def test_prism_zip_with_top_folder_is_flattened(self):
        fx = Fixture(self.tmp, prism_files={"PrismLauncher/prismlauncher.exe": b"MZ",
                                             "PrismLauncher/portable.txt": b"p"})
        code, _, err = run(fx.args())
        self.assertEqual(code, 0, err)
        with zipfile.ZipFile(fx.out / f"CraftTheftHalo-Minecraft-{VERSION}.zip") as zf:
            self.assertIn("Prism/prismlauncher.exe", zf.namelist())

    def test_bad_arguments(self):
        self.assertEqual(run(["--version", "one"])[0], bp.EXIT_ARGS)
        self.assertEqual(run(["--release", "--allow-incomplete-notices"])[0], bp.EXIT_ARGS)
        self.assertEqual(run(["--no-such-flag"])[0], bp.EXIT_ARGS)

    @unittest.skipUnless(HAVE_GIT, "git not installed")
    def test_sources_zip(self):
        fx = Fixture(self.tmp)
        tarball = self.tmp / "upstream" / "lib-1.0.tar.gz"
        with tarfile.open(tarball, "w:gz") as t:
            data = b"int main(){}\n"
            ti = tarfile.TarInfo("lib-1.0/main.c")
            ti.size = len(data)
            t.addfile(ti, io.BytesIO(data))
        up_git = self.tmp / "gitup"
        commit = git_init_commit(up_git, {"ports/lzo/portfile.cmake": "x", "other/file.txt": "y"})
        tree = subprocess.run(["git", "rev-parse", f"{commit}:ports/lzo"], cwd=up_git, capture_output=True,
                              text=True, check=True).stdout.strip()
        fx.pins_data["sources"] = [
            {"id": "lib", "name": "lib 1.0", "covers": "lib.dll", "license": "GPL-2.0-or-later",
             "file": tarball.name, "urls": [tarball.as_uri()], "size": tarball.stat().st_size,
             "sha256": sha256(tarball.read_bytes())},
            {"id": "ports", "name": "ports", "covers": "build scripts", "license": "MIT",
             "git": up_git.as_uri(), "commit": commit, "trees": {"ports/lzo": tree},
             "file": f"ports-{commit}.tar"}]
        fx.write_pins()
        git_init_commit(fx.repo, {"README.md": "fixture repo"})
        code, _, err = run(fx.args("--sources"))
        self.assertEqual(code, 0, err)
        sz = fx.out / f"CraftTheftHalo-{VERSION}-sources.zip"
        with zipfile.ZipFile(sz) as zf:
            names = zf.namelist()
            self.assertIn("upstream/lib-1.0.tar.gz", names)
            self.assertIn(f"upstream/ports-{commit}.tar", names)
            self.assertIn("SOURCE.txt", names)
            self.assertIn("SHA256SUMS", names)
            self.assertTrue(any(n.startswith("crafttheft/fort-halo-theft-auto-5-") for n in names))
            with tarfile.open(fileobj=io.BytesIO(zf.read(f"upstream/ports-{commit}.tar"))) as t:
                members = t.getnames()
            self.assertTrue(any(m.endswith("ports/lzo/portfile.cmake") for m in members))
            self.assertFalse(any("other/file.txt" in m for m in members), "only pinned paths are archived")
            src = zf.read("SOURCE.txt").decode()
            self.assertIn(f"lib 1.0 [GPL-2.0-or-later]", src)
            sums = zf.read("SHA256SUMS").decode()
            self.assertIn(f"{sha256(tarball.read_bytes())}  upstream/lib-1.0.tar.gz", sums)
        with zipfile.ZipFile(fx.out / f"CraftTheftHalo-Minecraft-{VERSION}.zip") as zf:
            self.assertIn(re.search(r"commit ([0-9a-f]{40})", zf.read("SOURCE.txt").decode()).group(1),
                          subprocess.run(["git", "rev-parse", "HEAD"], cwd=fx.repo, capture_output=True,
                                         text=True).stdout)
        # a wrong tree pin must stop the build
        fx.pins_data["sources"][1]["trees"]["ports/lzo"] = "0" * 40
        fx.write_pins()
        code, _, err = run(fx.args("--sources"))
        self.assertEqual(code, bp.EXIT_DOWNLOAD)
        self.assertIn("pinned 0000", err)

    @unittest.skipUnless(HAVE_GIT, "git not installed")
    def test_release_refuses_dirty_tree(self):
        fx = Fixture(self.tmp)
        git_init_commit(fx.repo, {"README.md": "fixture repo"})
        (fx.repo / "README.md").write_text("changed")
        code, _, err = run(fx.args("--release"))
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("uncommitted changes", err)

    @unittest.skipUnless(HAVE_GIT, "git not installed")
    def test_release_refuses_untracked_source_files(self):
        """An exe built from a never-committed source file would have no corresponding source at the release commit."""
        fx = Fixture(self.tmp)
        git_init_commit(fx.repo, {"README.md": "fixture repo"})
        (fx.repo / "NewModule.cs").write_text("// not committed")
        code, _, err = run(fx.args("--release"))
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("uncommitted changes", err)

    @unittest.skipUnless(HAVE_GIT, "git not installed")
    def test_release_needs_an_exe_built_from_this_commits_extractor(self):
        fx = Fixture(self.tmp)
        (fx.repo / ".gitignore").write_text("extractor/dist/\nout/\ndownloads/\n")
        git_init_commit(fx.repo, {"extractor/src/A.cs": "// v1"})
        dist = fx.repo / "extractor" / "dist"
        exe_sha = sha256((dist / "cthextract.exe").read_bytes())
        tree = subprocess.run(["git", "rev-parse", "HEAD:extractor"], cwd=fx.repo, capture_output=True,
                              text=True).stdout.strip()
        # no stamp: refused
        code, _, err = run(fx.args("--release"))
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("cthextract.source.json", err)
        # stamp of another extractor tree (exe built before the last commit): refused
        stamp = dist / "cthextract.source.json"
        stamp.write_text(json.dumps({"extractorTree": "0" * 40, "dirty": False, "sha256": exe_sha}))
        code, _, err = run(fx.args("--release"))
        self.assertEqual(code, bp.EXIT_INPUT)
        self.assertIn("rebuild with extractor/build.sh", err)
        # built from uncommitted sources: refused
        stamp.write_text(json.dumps({"extractorTree": tree, "dirty": True, "sha256": exe_sha}))
        self.assertIn("uncommitted extractor sources", run(fx.args("--release"))[2])
        # a stamp of another exe build: refused
        stamp.write_text(json.dumps({"extractorTree": tree, "dirty": False, "sha256": "1" * 64}))
        self.assertIn("describes another build", run(fx.args("--release"))[2])
        # the right stamp passes this check
        stamp.write_text(json.dumps({"extractorTree": tree, "dirty": False, "sha256": exe_sha}))
        self.assertIsNone(bp.extractor_stamp_problem(fx.repo, dist / "cthextract.exe"))
        code, _, err = run(fx.args("--release"))
        self.assertNotIn("--release:", err)
        self.assertEqual(code, 0, err)


class BundleParserTests(unittest.TestCase):
    def test_round_trip(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.exe"
            p.write_bytes(fake_bundle_exe())
            b = bp.read_single_file_bundle(p)
            self.assertEqual(b["version"], "6.0")
            self.assertIn("Newtonsoft.Json.dll", [f["path"] for f in b["files"]])
            self.assertEqual(b["runtimeconfig"]["runtimeOptions"]["includedFrameworks"][0]["version"], "10.0.12")
            self.assertIn("Newtonsoft.Json/13.0.4", b["deps"]["libraries"])

    def test_not_a_bundle(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "x.exe"
            p.write_bytes(bytes(fake_pe()) + b"\0" * 100)
            self.assertIsNone(bp.read_single_file_bundle(p))

    def test_real_extractor_if_built(self):
        exe = REPO / "extractor" / "dist" / "cthextract.exe"
        if not exe.is_file():
            self.skipTest("extractor/dist/cthextract.exe not built")
        problems, bundle = bp.check_extractor(exe)
        self.assertEqual(problems, [])
        files, problems, info = bp.extractor_notices(
            bundle, bp.nuget_root(None), (REPO / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8"),
            REPO / "extractor" / "vendor")
        if any("is not in" in p for p in problems):
            self.skipTest("NuGet packages of the extractor are not restored here")
        self.assertEqual(problems, [], "THIRD-PARTY-NOTICES.md or PROJECT_VENDOR is out of date")
        self.assertTrue(info["runtimeVersion"])


class CommittedFilesTests(unittest.TestCase):
    def test_instance_files_pass_checks(self):
        pins = bp.load_pins(PACKAGING / "pins.json")
        cfg = (PACKAGING / "instance" / "instance.cfg").read_text(encoding="utf-8")
        raw = (PACKAGING / "instance" / "mmc-pack.json").read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertEqual(bp.check_instance_files(cfg, json.loads(raw), pins), [])

    def test_instance_check_catches_config_version(self):
        pins = bp.load_pins(PACKAGING / "pins.json")
        pack = json.loads((PACKAGING / "instance" / "mmc-pack.json").read_text())
        cfg = (PACKAGING / "instance" / "instance.cfg").read_text() + "ConfigVersion=1.3\n"
        self.assertTrue(any("ConfigVersion" in p for p in bp.check_instance_files(cfg, pack, pins)))
        pack["components"][3]["version"] = "0.16.0"
        self.assertTrue(any("fabric-loader" in p for p in bp.check_instance_files(cfg, pack, pins)))

    def test_committed_licence_texts_match_sources_json(self):
        files, problems = bp.committed_licenses(PACKAGING)
        self.assertEqual(problems, [])
        self.assertIn("qt/qt-6.10.2-third-party.txt", files)
        self.assertNotIn("dotnet/10.0.12/LICENSE.TXT", files)        # bundle: false

    def test_pins_are_complete(self):
        pins = json.loads((PACKAGING / "pins.json").read_text())
        for key, pin in pins["bundle"].items():
            self.assertRegex(pin["sha256"], r"^[0-9a-f]{64}$", key)
            self.assertTrue(pin["urls"][0].startswith("https://"), key)
            self.assertGreater(pin["size"], 0)
        for s in pins["sources"]:
            self.assertTrue(("sha256" in s and "size" in s) or ("commit" in s and s["trees"]), s["id"])
            self.assertIn("covers", s)
        self.assertEqual(pins["bundle"]["prism"]["sha256"],
                         "ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7")
        self.assertEqual(pins["versions"]["fabricApi"], "0.116.17+1.21.1")
        self.assertEqual(pins["versions"]["fabricLoader"], "0.19.5")

    def test_project_licences(self):
        cc = (REPO / "LICENSE").read_text(encoding="utf-8")
        self.assertTrue(cc.startswith("Attribution-NonCommercial-ShareAlike 4.0 International"))
        gpl = (REPO / "extractor" / "LICENSE").read_text(encoding="utf-8")
        self.assertIn("GNU GENERAL PUBLIC LICENSE", gpl)
        self.assertIn("Version 3, 29 June 2007", gpl)


class MeltyRecipeTests(unittest.TestCase):
    """melty.json must agree with what build_package.py produces."""

    @classmethod
    def setUpClass(cls):
        cls.r = json.loads((REPO / "packaging" / "melty.recipe.json").read_text(encoding="utf-8"))

    def comp(self, cid):
        return next(c for c in self.r["components"] if c["id"] == cid)

    def test_components_match_built_file_names(self):
        self.assertTrue(fnmatch.fnmatchcase(bp.JAR_NAME.format(version="1.2.3"), self.comp("main")["fileName"]))
        self.assertTrue(fnmatch.fnmatchcase(bp.ZIP_NAME.format(version="1.2.3"), self.comp("minecraft")["fileName"]))
        self.assertEqual(self.comp("main")["kind"], "main")
        self.assertEqual(self.comp("minecraft")["kind"], "extra")
        self.assertTrue(self.comp("minecraft")["required"])
        self.assertEqual(len(self.r["components"]), 2)

    def test_paths_match_bundle_layout(self):
        root = "{localappdata}/CraftTheftHalo"
        maps = {m["component"]: m for m in self.r["mappings"]}
        self.assertEqual(maps["minecraft"], {"component": "minecraft", "from": "", "to": root})
        self.assertEqual(maps["main"]["to"], f"{root}/{bp.MODS_DIR}")
        self.assertEqual(self.r["launch"]["path"], f"{root}/Prism/prismlauncher.exe")
        self.assertEqual(self.r["launch"]["args"], ["--launch", bp.INSTANCE_ID])
        setup = self.r["setup"]
        self.assertEqual(setup["launch"]["path"], f"{root}/bin/cthextract.exe")
        a = setup["launch"]["args"]
        self.assertEqual(a[a.index("--halo") + 1], "{game:halo-mcc}")
        self.assertEqual(a[a.index("--gta") + 1], "{game:gta-v}")
        self.assertEqual(a[a.index("--fortnite") + 1], "{game:fortnite}")
        self.assertEqual(a[a.index("--out") + 1], f"{root}/cache")
        self.assertEqual(setup["done"], {"file": f"{root}/cache/manifest.json", "contains": '"cacheSchema":1'})
        self.assertEqual(setup["label"], "game content")
        s = self.r["settings"][0]
        self.assertEqual(s["file"], f"{root}/{bp.INSTANCE_DIR}/.minecraft/config/crafttheft.properties")
        self.assertEqual(set(s["set"]), {"halo_dir", "gta_dir", "fortnite_dir", "cache_dir", "extractor"})
        self.assertEqual(s["set"]["extractor"], setup["launch"]["path"])
        self.assertEqual(s["set"]["cache_dir"], f"{root}/cache")
        self.assertEqual(self.r["runtimeData"], [root])

    def test_games_and_test_block(self):
        games = {g["slug"]: g["role"] for g in self.r["games"]}
        self.assertEqual(games, {"minecraft-java": "primary", "halo-mcc": "companion", "gta-v": "companion",
                                 "fortnite": "companion"})
        self.assertEqual(self.r["requirements"], [])                     # never declare the Fabric loader
        t = self.r["test"]
        self.assertEqual(t["minutes"], 10)
        self.assertLessEqual(len(t["checks"]), 12)
        for c in t["checks"]:
            self.assertRegex(c, r"^[a-z0-9][a-z0-9-]{0,39}$")
        self.assertEqual(len(set(t["checks"])), len(t["checks"]))
        mod_req = REPO / "docs" / "requests" / "mod.md"
        if not mod_req.is_file():
            self.assertEqual(t["checks"], ["mod-loads", "cache-ready", "halo-content", "gta-content",
                                           "fortnite-content", "bus-ride", "landed", "chest-opened",
                                           "weapon-fired", "car-driven", "wanted-stars", "police-chase"])

    def test_notes_fit(self):
        for k, v in self.r["notes"].items():
            self.assertLessEqual(len(v), 200, k)                          # Melty cut firstLaunch at ~200 chars


if __name__ == "__main__":
    unittest.main()
