# 0012: Our own Godot build, with a patch queue

**Status:** accepted (2026-11-03)

## Context

Profiling creatures in a release-like setting showed most of their cost was in Godot itself: `Skeleton3D` re-walking bones once per root, every skeleton processing an idle compatibility `PhysicalBoneSimulator3D` as an active modifier, and `AnimationMixer` and `Animation` searching keys twice per track and slerping at full weight. These are fixable in a few lines each, but upstream takes months, and an open pull request on the same code (#113441) has waited since December 2025.

## Decision

- **Godot is a submodule,** `third_party/godot`, pinned at the release we use (4.7.2-stable), shallow.
- **Our changes are a queue of patches,** `patches/godot/NNN-*.patch`, applied in order by `scripts/prepare-godot.sh` onto the pinned tree, which it restores first. Each patch is one change meant for upstream, with a paragraph of what and why, and the queue's README says when each can be dropped. This follows 2ksbox's QEMU and DXVK queues.
- **`scripts/build-godot.sh` builds it,** the .NET editor by default, with symbols, plus the C# glue and assemblies. Export templates come from the same script.
- **Patches keep Godot's API unchanged,** so the game still builds against the official `Godot.NET.Sdk` package and runs on stock Godot of the same version too, only slower. A change that needs new API would need our own NuGet packages, which is a separate decision.

## Consequences

- Running the game for development uses `third_party/godot/bin`'s editor. Stock 4.7.2 still works.
- Updating Godot means moving the submodule and regenerating or dropping patches.
- The first build takes tens of minutes and needs SCons and a C++ toolchain.
- Each patch should go upstream, and comes out when it, or an equivalent, is in the release we pin.
