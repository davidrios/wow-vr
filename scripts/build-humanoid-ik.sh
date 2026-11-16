#!/usr/bin/env bash
# Builds the HumanoidIK3D GDExtension (extensions/humanoid_ik) into godot/addons/humanoid_ik/bin,
# against the pinned godot-cpp submodule and Godot 4.7's API. Needs Python with SCons
# (pip install scons) and a C++ compiler: Visual Studio's on Windows, as for the engine.
# Usage: scripts/build-humanoid-ik.sh [template_debug|template_release]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-template_debug}"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) PYTHON=python ;;
  *) PYTHON=python3 ;;
esac

git -C "$ROOT" submodule update --init third_party/godot-cpp
cd "$ROOT/extensions/humanoid_ik"
"$PYTHON" -m SCons api_version=4.7 target="$TARGET" -j"$(nproc 2>/dev/null || echo 8)"
