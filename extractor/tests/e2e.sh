#!/usr/bin/env bash
# End-to-end run of the REAL extractor (Linux build, `dotnet run`, real modules, default options: no injected keys,
# openers or HTTP) on fake game folders built from the modules' own synthetic data, then a contract check of the cache
# against docs/CONTRACTS.md, a --check run, and an --only run that must leave the other games untouched.
#
#   extractor/tests/e2e.sh [work dir]        default: extractor/bin/e2e (git-ignored)
#
# Set CTH_REFS to a folder holding a CUE4Parse clone to give Fortnite real (Apache-2.0 fixture) containers;
# without it the Fortnite folder is an empty Paks folder. Nothing here reads or writes a game install.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="$(realpath -m "${1:-$HERE/bin/e2e}")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
cd "$HERE"
say() { printf '\n== %s\n' "$*"; }
fail=0

say "build (Release)"
dotnet build CthExtract.slnx -c Release --nologo -v quiet >"$ROOT.build.log" 2>&1 || { tail -30 "$ROOT.build.log"; exit 1; }
E2E=(dotnet run --project tests/CthExtract.E2E -c Release --no-build --)
CLI=(dotnet run --project src/CthExtract -c Release --no-build --)

say "fake game folders in $ROOT/games"
mkdir -p "$ROOT"
"${E2E[@]}" make "$ROOT" | tee "$ROOT/make.txt"
HALO="$(sed -n 's/^HALO=//p' "$ROOT/make.txt")"; GTA="$(sed -n 's/^GTA=//p' "$ROOT/make.txt")"
FN="$(sed -n 's/^FORTNITE=//p' "$ROOT/make.txt")"; SEED="$(sed -n 's/^FORTNITE_KEYS_SEED=//p' "$ROOT/make.txt")"

CACHE="$ROOT/cache"
rm -rf "$CACHE"; mkdir -p "$CACHE"
if [[ -n "$SEED" ]]; then mkdir -p "$CACHE/keys"; cp -r "$SEED" "$CACHE/keys/fortnite"; fi

say "run 1: all games (--offline: Fortnite uses the seeded key cache only; GTA needs no download, its archives are OPEN)"
start=$(date +%s)
"${CLI[@]}" --halo "$HALO" --gta "$GTA" --fortnite "$FN" --out "$CACHE" --offline --console >"$ROOT/run1.out" 2>&1
rc=$?
echo "exit $rc after $(( $(date +%s) - start )) s (expected 1 = partial)"
[[ $rc -eq 1 ]] || { echo "UNEXPECTED exit code"; fail=1; }
grep -E "^.*summary (halo|gta|fortnite):" "$CACHE/extract.log" | sed 's/^/  /'

say "contract check of the cache"
"${E2E[@]}" verify "$CACHE" --report "$ROOT/contract-run1.json" | tee "$ROOT/contract-run1.txt"
[[ ${PIPESTATUS[0]} -eq 0 ]] || fail=1

say "--check"
"${CLI[@]}" --check --halo "$HALO" --gta "$GTA" --fortnite "$FN" --out "$CACHE" --console >"$ROOT/check.out" 2>&1
crc=$?
echo "check exit $crc: $(cat "$CACHE/check.json" 2>/dev/null)"

say "run 2: --only gta (halo and fortnite must stay byte-identical)"
snap() { (cd "$CACHE" && find packs/halo packs/fortnite stats -type f 2>/dev/null | sort | xargs -r sha256sum; python3 - <<'PY'
import json
m = json.load(open("manifest.json"))
for g in ("halo", "fortnite"):
    print(g, json.dumps(m["games"][g], sort_keys=True))
for k, v in sorted(m["assets"].items()):
    if not k.startswith("gta_"): print(k, json.dumps(v, sort_keys=True))
PY
); }
snap > "$ROOT/before.txt"
"${CLI[@]}" --halo "$HALO" --gta "$GTA" --fortnite "$FN" --out "$CACHE" --offline --only gta --console >"$ROOT/run2.out" 2>&1
rc2=$?
snap > "$ROOT/after.txt"
echo "exit $rc2"
if cmp -s "$ROOT/before.txt" "$ROOT/after.txt"; then echo "halo/fortnite packs, stats and manifest entries unchanged"; else echo "CHANGED:"; diff "$ROOT/before.txt" "$ROOT/after.txt" | head -20; fail=1; fi
"${E2E[@]}" verify "$CACHE" --report "$ROOT/contract-run2.json" | tail -1
[[ ${PIPESTATUS[0]} -eq 0 ]] || fail=1

say "cache tree"
(cd "$CACHE" && find . -type f | sort | sed 's#^\./#  #')
say "rows"
python3 - "$CACHE/manifest.json" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
for k, v in m["assets"].items():
    print(f"  {k:32} {v['status']:13} {(v.get('detail') or '')[:150]}")
PY
[[ $fail -eq 0 ]] && echo "E2E PASS" || echo "E2E FAIL"
exit $fail
