#!/usr/bin/env bash
# Exports the Windows build of the game into a folder, ready to zip: WowVR.exe, its pack, the
# .NET assemblies with wow-alchemy's library, and the HumanoidIK3D extension. It uses our patched
# Godot's release template (scripts/build-godot.sh template_release) through the preset's custom
# template, and any Godot 4.7.2 .NET editor to export with: the patches don't change Godot's API.
#
#   scripts/export-windows.sh [output folder]      default: build/windows
#
# GODOT_EDITOR is the editor to export with (default: our build in third_party/godot/bin), and
# GODOT_TEMPLATE the release template (default: our build's). HUMANOID_IK_PREBUILT=1 uses the
# extension's release library as it is. wow-alchemy is built as for `dotnet build`: next to this
# repo, or wherever WowAlchemyDir says.
set -euo pipefail

# The export runs dotnet, whose MSBuild worker nodes, build server and compiler server would
# otherwise stay alive after it, holding Godot's output open, so it waits for them to time out:
# 15 minutes after each export.
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 UseSharedCompilation=false

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:-$ROOT/build/windows}"
EDITOR="${GODOT_EDITOR:-$ROOT/third_party/godot/bin/godot.windows.editor.x86_64.mono.console.exe}"
TEMPLATE="${GODOT_TEMPLATE:-$ROOT/third_party/godot/bin/godot.windows.template_release.x86_64.mono.exe}"

# Godot takes a relative export path as relative to the project, not to here: every path it's
# given is made absolute.
absolute() { case "$1" in /*|[A-Za-z]:*) echo "$1" ;; *) echo "$PWD/$1" ;; esac; }
OUT="$(absolute "$OUT")"
EDITOR="$(absolute "$EDITOR")"
TEMPLATE="$(absolute "$TEMPLATE")"

[ -f "$EDITOR" ] || { echo "error: no Godot editor at $EDITOR (set GODOT_EDITOR)" >&2; exit 1; }
[ -f "$TEMPLATE" ] || { echo "error: no release template at $TEMPLATE (scripts/build-godot.sh template_release, or set GODOT_TEMPLATE)" >&2; exit 1; }

# CI restores the library from its cache and says so, as SCons would build it again without its objects.
if [ "${HUMANOID_IK_PREBUILT:-}" != 1 ]; then
  echo "==> HumanoidIK3D for release"
  "$ROOT/scripts/build-humanoid-ik.sh" template_release
fi

# Godot wants Windows paths in the preset and on its command line.
win() { cygpath -m "$1"; }

# The preset points at our template only for this export, and is put back after. So is the
# project file, which the editor's import rewrites (to uid:// paths the repo doesn't keep).
PRESET="$ROOT/godot/export_presets.cfg"
PROJECT="$ROOT/godot/project.godot"
cp "$PRESET" "$PRESET.bak"
cp "$PROJECT" "$PROJECT.bak"
# The import also writes a .uid file beside every script and shader, which the repo doesn't keep:
# those it adds go again after.
untracked_uids() { git -C "$ROOT" ls-files --others --exclude-standard -- '*.uid' | sort; }
UIDS_BEFORE="$(untracked_uids)"
cleanup() {
  mv -f "$PRESET.bak" "$PRESET"
  mv -f "$PROJECT.bak" "$PROJECT"
  comm -13 <(printf '%s\n' "$UIDS_BEFORE") <(untracked_uids) | while read -r uid; do
    [ -n "$uid" ] && rm -f "$ROOT/$uid"
  done
}
trap cleanup EXIT
sed -i "s|^custom_template/release=.*|custom_template/release=\"$(win "$TEMPLATE")\"|" "$PRESET"

rm -rf "$OUT"
mkdir -p "$OUT"
# A fresh checkout has nothing imported yet.
echo "==> importing with $(basename "$EDITOR")"
"$EDITOR" --headless --path "$(win "$ROOT/godot")" --import

echo "==> exporting with $(basename "$EDITOR")"
"$EDITOR" --headless --path "$(win "$ROOT/godot")" --export-release "Windows Desktop" "$(win "$OUT")/WowVR.exe"

# What a player needs to read, next to the game, and the licenses of what it's made of.
cp "$ROOT/docs/playing.md" "$OUT/README.md"
LICENSES="$OUT/licenses"
WOW_ALCHEMY="${WowAlchemyDir:-$ROOT/../wow-alchemy/}"
DOTNET_ROOT_DIR="$(dirname "$(command -v dotnet)")"
mkdir -p "$LICENSES"
notice() {
  [ -f "$1" ] || { echo "error: no license file at $1" >&2; exit 1; }
  cp "$1" "$LICENSES/$2"
}
notice "$ROOT/LICENSE-MIT" "wow-vr-LICENSE-MIT.txt"
notice "$ROOT/LICENSE-APACHE" "wow-vr-LICENSE-APACHE.txt"
notice "$WOW_ALCHEMY/LICENSE-MIT" "wow-alchemy-LICENSE-MIT.txt"
notice "$ROOT/third_party/godot/LICENSE.txt" "godot-LICENSE.txt"
notice "$ROOT/third_party/godot/COPYRIGHT.txt" "godot-COPYRIGHT.txt"
notice "$ROOT/third_party/godot-cpp/LICENSE.md" "godot-cpp-LICENSE.md"
notice "$ROOT/godot/addons/godot-xr-tools/LICENSE" "godot-xr-tools-LICENSE.txt"
notice "$DOTNET_ROOT_DIR/LICENSE.txt" "dotnet-LICENSE.txt"
notice "$DOTNET_ROOT_DIR/ThirdPartyNotices.txt" "dotnet-ThirdPartyNotices.txt"

# The export must have taken what the game loads at run time.
missing=0
for needed in WowVR.exe WowVR.pck; do
  [ -f "$OUT/$needed" ] || { echo "error: the export has no $needed" >&2; missing=1; }
done
compgen -G "$OUT/data_*/wow_alchemy_ffi.dll" >/dev/null || { echo "error: wow_alchemy_ffi.dll isn't next to the assemblies" >&2; missing=1; }
compgen -G "$OUT/libhumanoid_ik.windows.template_release.x86_64.dll" >/dev/null \
  || compgen -G "$OUT/**/libhumanoid_ik.windows.template_release.x86_64.dll" >/dev/null \
  || { echo "error: the export has no HumanoidIK3D library" >&2; missing=1; }
[ "$missing" = 0 ] || exit 1

echo "==> exported to $OUT"
ls "$OUT"
