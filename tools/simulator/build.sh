#!/usr/bin/env bash
# Builds the RocketSim bridge (native/build/librsbridge.so) and the match simulator.
# Set ROCKETSIM_SOURCE_DIR to reuse a local RocketSim checkout instead of fetching the pinned one.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cmake_args=(-S "$here/native" -B "$here/native/build" -DCMAKE_BUILD_TYPE=Release)
if [[ -n "${ROCKETSIM_SOURCE_DIR:-}" ]]; then
  cmake_args+=(-DROCKETSIM_SOURCE_DIR="$ROCKETSIM_SOURCE_DIR")
fi
if command -v ninja >/dev/null 2>&1; then
  cmake_args+=(-G Ninja)
fi

cmake "${cmake_args[@]}"
cmake --build "$here/native/build" --parallel
dotnet build "$here/Stardust.Simulator/Stardust.Simulator.csproj" --configuration Release
