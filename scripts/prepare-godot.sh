#!/usr/bin/env bash
# Prepares the Godot submodule tree: restores every tracked file our patch queue
# touches to the pinned commit, deletes every file it creates, then applies
# patches/godot/*.patch in filename order. Deterministic and idempotent: run it
# as often as you like. Never `git checkout` files inside third_party/godot by
# hand between runs, and don't rely on "already applied" checks.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="$ROOT/third_party/godot"
PATCHES="$ROOT/patches/godot"

[ -f "$GODOT/SConstruct" ] || { echo "Godot submodule missing (git submodule update --init third_party/godot)"; exit 1; }

# Patches are plain `git diff` output (a/ b/ prefixes, applied with -p1).
patched_files() { sed -n 's|^+++ b/||p' "$@" | sort -u; }
created_files() { awk '/^--- \/dev\/null/ { getline; sub(/^\+\+\+ b\//, ""); print }' "$@" | sort -u; }

shopt -s nullglob
queue=("$PATCHES"/*.patch)

echo "==> restoring files touched by patches/godot"
if [ ${#queue[@]} -gt 0 ]; then
  patched_files "${queue[@]}" | while read -r f; do
    if git -C "$GODOT" ls-files --error-unmatch "$f" >/dev/null 2>&1; then
      git -C "$GODOT" checkout -q HEAD -- "$f"
    fi
  done
  created_files "${queue[@]}" | while read -r f; do rm -f "$GODOT/$f"; done
fi

echo "==> applying the patch queue"
for p in "${queue[@]}"; do
  echo "    $(basename "$p")"
  if ! git -C "$GODOT" apply "$p"; then
    echo "error: $(basename "$p") does not apply; see patches/godot/README.md on regenerating it" >&2
    exit 1
  fi
done

echo "==> Godot tree ready: $(git -C "$GODOT" describe --tags --always) + ${#queue[@]} patches"
