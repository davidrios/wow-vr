# Godot patch queue

Every change wow VR makes to Godot. The tree is the pinned submodule `third_party/godot` at 4.7.2-stable, plus the patches in this directory, applied in filename order. Each is meant to go upstream on its own, and each row says when it can be dropped. See `docs/decisions/0012-patched-godot.md` for why.

The patches must not change Godot's API: no new or changed bound methods, properties, signals or classes. The game builds against the official `Godot.NET.Sdk` package of the same version, which only works with an engine whose API matches it.

## Building

```
git submodule update --init third_party/godot
scripts/build-godot.sh
```

That prepares the tree, builds the .NET editor with symbols into `third_party/godot/bin`, and builds the editor's C# glue and assemblies. `scripts/build-godot.sh template_release template_debug` builds export templates. It needs Python with SCons (`pip install scons`) and Visual Studio's C++ tools on Windows. The first build takes a while, and later ones only rebuild what the patches touch.

## How prepare builds the tree

`scripts/prepare-godot.sh` redoes everything on each run:

1. **Restore.** Every tracked file any patch touches is checked out from the pinned commit, and every file a patch creates is deleted.
2. **Apply.** The patches, in filename order, with `git apply`. A patch that doesn't apply stops the run. Names carry three digits, since the glob sorts as text.

Never `git checkout` files inside `third_party/godot` by hand between runs, and don't rely on "already applied" checks. Each patch starts with a paragraph of what and why, which `git apply` ignores.

## Editing or adding a patch

A patch is a `git diff` (with `a/` and `b/` prefixes) that applies to the tree the patches before it produce.

1. Run `scripts/prepare-godot.sh`, and stage the result in the submodule: `git -C third_party/godot add -A`.
2. Edit files in `third_party/godot`. `git -C third_party/godot diff` is now only your change. Write it after a paragraph of what and why.
3. When editing patch N with later patches touching the same files, take those out of the queue, regenerate N, then put each later one back by applying its change on top and diffing again.
4. Prove it from pristine: run prepare twice (both must apply everything), then build. A reverse check against an edited tree proves nothing.

## Updating Godot

Move the submodule to the new tag, run prepare, and for each patch that fails: drop it if the change is already upstream (its row says what to look for), otherwise regenerate it. Bump `Godot.NET.Sdk` in `godot/WowVR.csproj` to the same version.

## The patches

Measured with `--bench` in Stormwind at 1280x720, all 60 creatures animated at full rate and Low graphics otherwise, on a Ryzen 9 5900X: 3.82 ms a frame on stock 4.7.2 and 3.07 ms with all of them. The engine's `Skeleton3D`, `AnimationMixer` and `Animation` are what animating creatures costs.

| Patch | What | Drop when |
| --- | --- | --- |
| 001 | `Skeleton3D` updates dirty bones in one pass over the nested set, not one per root bone. | The loop in `_force_update_all_bone_transforms` runs once upstream. |
| 002 | `Skeleton3D` keeps poses without overrides only once a global pose override is used: a transform multiply per bone less. | Upstream stops computing `pose_global_no_override` unconditionally. |
| 003 | `Skeleton3D` keeps its pose backup buffers between updates. | godotengine/godot#113441 ("Optimize Skeleton3D") or an equivalent is in. |
| 004 | Modifiers that can't change anything are skipped, including the compatibility `PhysicalBoneSimulator3D` every skeleton has, which is active by default: skeletons with no other modifier no longer back up, restore and re-walk their bones at each update. Adds `SkeletonModifier3D::can_modify()`, which isn't bound. | Upstream skips idle modifiers, or stops making the compatibility simulator active. |
| 005 | `PhysicalBoneSimulator3D` copies the skeleton's poses when they're read, rather than at every pose update. | Upstream does the same, or drops the copy. |
| 006 | `Animation::interpolate_via_rest()` skips the slerp at full weight. | Upstream has the fast path. |
| 007 | `Animation::_find()` searches with plain comparisons and checks for an approximate match once. | Upstream rewrites `_find`. |
| 008 | `Animation::_interpolate()` counts keys within the length without a second search when the last key is within it. | Upstream drops the per-call `_find(p_keys, length)`. |
| 009 | `AnimationMixer` skips post-processing rotation and scale keys when no script overrides `_post_process_key_value`. | Upstream has the fast path. |

None has been proposed upstream yet. They need a benchmark on a release build with freely licensed models first.
