#!/usr/bin/env bash
# Builds our Godot: the pinned submodule with patches/godot applied, with .NET.
#
#   scripts/build-godot.sh                 the editor, which also runs the game (default)
#   scripts/build-godot.sh template_release template_debug
#                                          export templates, for packaging
#
# Output goes to third_party/godot/bin. The editor's C# glue and assemblies are
# rebuilt next to it. The game keeps using the official Godot.NET.Sdk package of
# the same version, which works because the patches don't change Godot's API.
# Needs Python with SCons (pip install scons) and, on Windows, Visual Studio's
# C++ tools; see Godot's "Compiling" docs for other platforms.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="$ROOT/third_party/godot"
TARGETS=("${@:-editor}")

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) PLATFORM=windows; PYTHON=python ;;
  Darwin) PLATFORM=macos; PYTHON=python3 ;;
  *) PLATFORM=linuxbsd; PYTHON=python3 ;;
esac

"$ROOT/scripts/prepare-godot.sh"

# The screen reader driver and Direct3D 12 need extra SDKs and nothing here uses them.
EXTRA=(accesskit=no)
[ "$PLATFORM" = windows ] && EXTRA+=(d3d12=no)

JOBS="$(nproc 2>/dev/null || echo 8)"
for target in "${TARGETS[@]}"; do
  echo "==> building $target"
  (cd "$GODOT" && "$PYTHON" -m SCons platform="$PLATFORM" target="$target" module_mono_enabled=yes debug_symbols=yes "${EXTRA[@]}" -j"$JOBS")
done

if [[ " ${TARGETS[*]} " == *" editor "* ]]; then
  # The console build on Windows, so its output reaches this terminal.
  shopt -s nullglob
  editors=("$GODOT"/bin/godot.*.editor.*.mono.console.exe "$GODOT"/bin/godot.*.editor.*.mono)
  shopt -u nullglob
  [ ${#editors[@]} -gt 0 ] || { echo "error: no editor in $GODOT/bin" >&2; exit 1; }
  editor="${editors[0]}"
  echo "==> generating C# glue with $(basename "$editor")"
  (cd "$GODOT" && "$editor" --headless --generate-mono-glue modules/mono/glue)
  echo "==> building C# assemblies"
  (cd "$GODOT" && "$PYTHON" modules/mono/build_scripts/build_assemblies.py --godot-output-dir=./bin --godot-platform="$PLATFORM")
fi

echo "==> done: $(ls "$GODOT"/bin | grep -v -e '\.exp$' -e '\.lib$' | tr '\n' ' ')"
