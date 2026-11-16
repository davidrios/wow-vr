# HumanoidIK3D

A Godot 4.7 GDExtension that poses a humanoid skeleton from a few tracked points, the way social
VR avatars follow their players: the head and hands, and with full body trackers the hips, chest,
feet, knees and elbows. Vendored here for now; it's meant to become its own repository, and later
perhaps an engine class beside `TwoBoneIK3D`, so the code follows the engine's style and uses only
its types.

## What it does

`HumanoidIK3D` is a `SkeletonModifier3D`: put it under a `Skeleton3D`, name its bones, and point its
targets at `Node3D`s. Each update, after the animation:

1. The upper body starts from rest (`freeze_upper_body`), so a walk's sway doesn't show through.
2. Without a hips target, the hips drop so the spine, at its standing length, reaches the head:
   straight down for a crouch, less for a bow. With one, they go there. The legs bend to keep the
   feet where the animation put them, or reach their targets, bending toward the knee targets.
3. The spine bends through the head with FABRIK and takes `spine_twist` of the head's turn from
   the body. With a chest target, the spine below the chest reaches that, and the chest turns as it.
4. Each collarbone lifts for a hand above the shoulder and swings forward for one reaching ahead.
5. Each arm is a hinge. The elbow goes toward its target, or where the wrist's turn says the
   forearm runs, blended (`elbow_from_wrist`) with down and out, never up while the hand is below
   the shoulder, and never far toward the body's middle. The forearm takes `forearm_twist` of the
   hand's twist about it, and the hand turns as its target.

Like every modifier, its result lasts until the mesh is skinned: outside it, the bones read as
animated. Connect to `modification_processed` to read the solved pose.

To fit a model to its player, scale it by `get_upright_eye_height()`: where its eyes would be with
the spine straight up. Since the spine bends to reach the head, a model whose rest or animation is
hunched stands up straight in VR, and sized by its hunched eye height it would come out too big and
crouch to make up for it. `get_head_pivot_for()` and `get_spine_length()` help keep the body under
the head while the player leans.

## Conventions

- **The skeleton** has up along +Y and faces `forward_axis` (+Z, as Godot's humanoid imports, by
  default). Rests may have any rotation. The skeleton's origin is at the floor.
- **Targets** are `Node3D`s in Godot's convention, -Z forward and +Y up:
  - `head_target`: the eyes, such as an `XRCamera3D`. Required. `eye_position` says where the eyes
    are on the model at rest, in the skeleton's space; left at zero, it's guessed.
  - `left_hand_target`, `right_hand_target`: the wrists, as OpenXR's hand joints: -Z along the
    fingers, +Y out of the back of the hand.
  - `hips_target` (the middle of the hip joints), `chest_target` (the top of the chest), and the
    feet (the ankles: -Z along the toes, +Y up from the top of the foot): their turn counts.
  - `left_knee_target`, `right_knee_target`, `left_elbow_target`, `right_elbow_target`: only their
    position counts, which says which way the limb bends.
- **Bones** are set by name: the hips (which hold both the spine and the legs), `spine_bones` from
  the lowest up to the one the head hangs on, the head, and for each arm the collarbone (optional),
  upper arm, forearm, hand, a middle finger bone and a thumb bone (whose rests say how the hand
  lies), and for each leg the thigh, shin and foot (optional).

## Building

From this folder, with Python's SCons and a C++ compiler, against the `godot-cpp` submodule at
`../../third_party/godot-cpp`:

```
python -m SCons api_version=4.7 [target=template_release]
```

The library goes into `../../godot/addons/humanoid_ik/bin`, which `humanoid_ik.gdextension` there
loads. In WowVR, `dotnet build` does this (`scripts/build-humanoid-ik.sh` too).
