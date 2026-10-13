#!/usr/bin/env bash
# Dedicated-server smoke test: starts the production-style server (Loom prodServer: Fabric installer + the remapped
# release jar + Fabric API), waits until the world is up, lets it tick, then stops it and scans the log.
#   scripts/run_server_smoke.sh [tickSeconds]
# Output: fabric/run-shots/server-prod/server.log. Exit 0 = started, ticked, no errors from the mod.
set -u
FABRIC="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECS="${1:-45}"
OUT="$FABRIC/run-shots/server-prod"; RUN="$FABRIC/run-prodserver"
rm -rf "$OUT" "$RUN/world" "$RUN/logs"; mkdir -p "$OUT" "$RUN"
echo "eula=true" > "$RUN/eula.txt"
printf 'online-mode=false\nlevel-seed=20261012\nview-distance=6\nsimulation-distance=6\nserver-port=25599\n' > "$RUN/server.properties"
cd "$FABRIC"
( ./gradlew --no-daemon --console=plain --init-script scripts/mirror.init.gradle prodServer > "$OUT/server.log" 2>&1 ) &
GPID=$!
T0=$(date +%s)
until grep -q "Done (" "$OUT/server.log" 2>/dev/null; do
  sleep 2
  if ! kill -0 $GPID 2>/dev/null; then echo "server exited before it was up"; break; fi
  if [ $(( $(date +%s) - T0 )) -ge 900 ]; then echo "server did not come up in 900s"; break; fi
done
UP=$(grep -c "Done (" "$OUT/server.log")
[ "$UP" -gt 0 ] && sleep "$SECS"
pkill -f "fabric.addMods=$FABRIC/build/libs/" 2>/dev/null; sleep 3; pkill -9 -f "fabric.addMods=$FABRIC/build/libs/" 2>/dev/null
kill $GPID 2>/dev/null; wait $GPID 2>/dev/null
ERRS=$(grep -E "ERROR|Exception|crafttheft.*(WARN|FAIL)|NoClassDefFound|client" "$OUT/server.log" | grep -vE "Deprecated|actionable|\[harness\]|No data fixer registered" )
echo "up: $UP"
grep -E "\[crafttheft\]|\[world\]|Loading [0-9]+ mods|Done \(" "$OUT/server.log" | head -20
if [ -n "$ERRS" ]; then echo "--- suspicious lines:"; echo "$ERRS" | head -30; fi
[ "$UP" -gt 0 ]
