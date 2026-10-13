#!/usr/bin/env bash
# Test stand-in for cthextract.exe: runs the synthetic cache tool (fabric/tools/CthSynth) with the same command line
# (--config <crafttheft.properties> [--only <game>]). Used only by the mod's test harness; never shipped.
HERE="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
exec "$HERE/../tools/CthSynth/bin/Release/net10.0/CthSynth" "$@"
