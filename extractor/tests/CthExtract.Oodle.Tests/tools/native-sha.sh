#!/usr/bin/env bash
# Decodes every *.ooz in a folder with a native ooz build and writes "<name> <sha256 of output>" lines (or
# "<name> FAIL") to <folder>/native-ooz.sha256, which ExternalVectorTests.UnrealEngineBlocks_DecodeLikeNativeOoz reads.
#   tools/native-sha.sh <ooz-ref binary> <folder>
set -euo pipefail
OOZ=${1:?native ooz}; DIR=${2:?folder}
out="$DIR/native-ooz.sha256"; : > "$out"
for f in $(cd "$DIR" && ls *.ooz | LC_ALL=C sort); do
  # pipefail: a failing ooz fails the whole pipeline
  if h=$("$OOZ" -d -c -q "$DIR/$f" 2>/dev/null | sha256sum | cut -d' ' -f1); then
    echo "$f $h" >> "$out"
  else
    echo "$f FAIL" >> "$out"
  fi
done
echo "$(grep -vc FAIL "$out") decoded, $(grep -c FAIL "$out" || true) failed -> $out"
