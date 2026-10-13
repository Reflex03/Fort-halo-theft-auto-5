#!/usr/bin/env bash
# Runs the scripted end-to-end scenario (src/harness) under Xvfb and collects screenshots and logs.
#
#   scripts/run_harness.sh <mode> <launch> [maxSeconds]
#     mode   all      empty cache + the synthetic stand-in extractor (scripts/fake-extractor.sh): first-start
#                     extraction behind the progress screen, a failed game (gta) re-read in the background,
#                     then the whole game on synthetic content
#            nocache  no cache and no extractor: the jar's placeholders only
#     launch dev      Loom runHarnessClient (named classes)
#            prod     Loom prodHarnessClient (remapped release jar + remapped harness jar + Fabric API jar, KnotClient)
#
# Output: fabric/run-shots/<mode>-<launch>/ (screenshots, summary.log, latest.log, melty-checks.txt).
# Needs: Xvfb, ImageMagick import (optional), dotnet (mode all), Java 21. Nothing here touches any game install.
# Mode all uses the REAL extractor's output from extractor/bin/e2e/cache (extractor/tests/e2e.sh) where it has a row;
# CTH_SYNTH_REAL_CACHE=<dir> picks another cache, CTH_SYNTH_REAL_CACHE= (empty) turns it off.
set -u
FABRIC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:?mode all|nocache}"; LAUNCH="${2:?launch dev|prod}"; MAX="${3:-1800}"
TAG="$MODE-$LAUNCH"
OUT="$FABRIC/run-shots/$TAG"
WORK="$FABRIC/build/harness-work/$TAG"
DISP="${DISP:-$((90 + RANDOM % 9))}"
INIT="$FABRIC/scripts/mirror.init.gradle"
rm -rf "$OUT" "$WORK"; mkdir -p "$OUT" "$WORK"

if [[ "$LAUNCH" == dev ]]; then RUN="$FABRIC/run-harness"; TASK=runHarnessClient; else RUN="$FABRIC/run-prod"; TASK=prodHarnessClient; fi
rm -rf "$RUN/saves" "$RUN/screenshots" "$RUN/options.txt" "$RUN/logs" "$RUN/config/crafttheft.properties" "$RUN/config/crafttheft-guard.properties"
mkdir -p "$RUN/config"

CACHE="$WORK/cache"
if [[ "$MODE" == all ]]; then
  EXTRACTOR="$FABRIC/scripts/fake-extractor.sh"
  if [[ ! -x "$FABRIC/tools/CthSynth/bin/Release/net10.0/CthSynth" ]]; then
    (cd "$FABRIC/tools/CthSynth" && dotnet build -c Release --nologo -v quiet) || { echo "CthSynth build failed"; exit 1; }
  fi
else
  EXTRACTOR="$WORK/no-such-extractor.exe"
fi
# what Melty writes before every Play (docs/CONTRACTS.md); the game folders are dummies, nothing reads them
cat > "$RUN/config/crafttheft.properties" <<EOF
# written by run_harness.sh ($TAG)
halo_dir=$WORK/games/halo
gta_dir=$WORK/games/gta
fortnite_dir=$WORK/games/fortnite
cache_dir=$CACHE
extractor=$EXTRACTOR
EOF

# Melty test mode: the mod reports its checks into this file (MeltyTest)
export LOCALAPPDATA="$WORK/localappdata"
mkdir -p "$LOCALAPPDATA/Melty/test"
printf '{"checks":"%s"}\n' "$WORK/melty-checks.txt" > "$LOCALAPPDATA/Melty/test/craft-theft-halo.json"
export CTH_SYNTH_FAIL=gta CTH_SYNTH_SLOW_MS=250
# Rows the REAL extractor produced on synthetic game folders (extractor/tests/e2e.sh) replace the synthetic ones
# (CthSynth RealCache): real Halo sounds and real GTA cars/siren/horn/star go through the whole game.
REAL_CACHE="${CTH_SYNTH_REAL_CACHE-$FABRIC/../extractor/bin/e2e/cache}"
if [[ "$MODE" == all && -n "$REAL_CACHE" && -f "$REAL_CACHE/manifest.json" ]]; then
  export CTH_SYNTH_REAL_CACHE="$(cd "$REAL_CACHE" && pwd)"
  echo "real extractor cache: $CTH_SYNTH_REAL_CACHE" | tee "$OUT/real-cache.txt"
else
  unset CTH_SYNTH_REAL_CACHE
  echo "real extractor cache: none (run extractor/tests/e2e.sh first to use one)" | tee "$OUT/real-cache.txt"
fi

# OpenAL Soft "wave" device: the sound engine runs (and reports what it plays) without a sound card
cat > "$WORK/alsoft.conf" <<EOF
[general]
drivers = wave
frequency = 22050
channels = mono
[wave]
file = $WORK/audio_capture.wav
EOF
export ALSOFT_CONF="$WORK/alsoft.conf"

# dev: our own Xvfb (also used for the progress screenshots below); prod: Loom's task wraps the game in xvfb-run
XPID=""
if [[ "$LAUNCH" == dev ]]; then
  Xvfb ":$DISP" -screen 0 1280x720x24 +extension GLX -nolisten tcp > "$WORK/xvfb.log" 2>&1 &
  XPID=$!
  sleep 2
fi
cd "$FABRIC"
( DISPLAY=":$DISP" ./gradlew --no-daemon --console=plain --init-script "$INIT" "-Pharness=$MODE" "$TASK" > "$OUT/client.log" 2>&1 ) &
GPID=$!
T0=$(date +%s); N=0
while kill -0 $GPID 2>/dev/null; do
  sleep 5
  EL=$(( $(date +%s) - T0 ))
  if [ $EL -ge "$MAX" ]; then echo "timeout after ${EL}s"; break; fi
  if grep -q "\[harness\] RESULT" "$OUT/client.log" 2>/dev/null; then
    RT=${RT:-$EL}
    if [ $(( EL - RT )) -ge 240 ]; then echo "game still running ${EL}s in, 240s after RESULT: hung on exit, killed"; break; fi
  fi
  if [[ -n "$XPID" ]] && [ $(( EL / 120 )) -gt $N ]; then N=$(( EL / 120 )); DISPLAY=":$DISP" import -window root "$OUT/progress_$N.png" 2>/dev/null; fi
done
echo "client finished/stopped after $(( $(date +%s) - T0 ))s"
pkill -f "crafttheft.harness=$MODE" 2>/dev/null; sleep 2; pkill -9 -f "crafttheft.harness=$MODE" 2>/dev/null
kill $GPID 2>/dev/null; wait $GPID 2>/dev/null
[[ -n "$XPID" ]] && kill $XPID 2>/dev/null
cp -f "$RUN"/screenshots/*.png "$OUT"/ 2>/dev/null
cp -f "$RUN"/logs/latest.log "$OUT"/latest.log 2>/dev/null
cp -f "$RUN"/logs/crafttheft-extractor.log "$OUT"/ 2>/dev/null
cp -f "$WORK"/melty-checks.txt "$OUT"/ 2>/dev/null
cp -f "$CACHE"/manifest.json "$OUT"/manifest.json 2>/dev/null
ls -la "$WORK"/audio_capture.wav 2>/dev/null | awk '{print "audio capture bytes: " $5}' > "$OUT/audio.txt"
rm -f "$WORK"/audio_capture.wav
grep -E "\[harness\]|\[melty\]|\[cache-guard\]|\[cache-pack\]|\[first-run\]|\[stats\]|\[shield\]|\[bus\]|\[skydive\]|\[wanted\]|\[police\]|\[loot\]|\[world\]|\[mesh\] [0-9]+ meshes|ERROR|Exception" "$OUT/client.log" \
  | grep -v "No data fixer registered" > "$OUT/summary.log"
grep -E "CHECK .*: FAIL|RESULT" "$OUT/summary.log"
tail -1 "$OUT/summary.log"
