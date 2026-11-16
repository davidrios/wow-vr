# 0017: The VR body's solver as a GDExtension

**Status:** accepted (2026-11-09). Moves 0014's solver out of the game.

## Decision

- **The body solver is `HumanoidIK3D`, a GDExtension in C++** (`extensions/humanoid_ik`), vendored
  here until it gets its own repository. It knows nothing of WoW: bones are set by name, rests may
  turn, the model may face any axis, and its targets are `Node3D`s in Godot's conventions. The game
  keeps what's WoW's: finding bones by key bone, where the eyes are, the fist poses, and fitting
  the body to the player's height and lean (`VrBodyIk`, `PlayerAvatar`).
- **C++ rather than C# or GDScript**, so it serves every Godot project, not only .NET ones, and can
  be ported into the engine beside `TwoBoneIK3D` with little more than a move. It's written in the
  engine's style, with its types and the `p_` names.
- **A `SkeletonModifier3D`**, so it composes with Godot's other modifiers, and since Godot puts the
  animated pose back after each skinning, nothing it does builds up: the old solver's bookkeeping of
  what the animation left behind, and holding the upper body still, are gone. Bodies it poses
  process their modifiers each frame, which other creatures don't.
- **godot-cpp is a submodule**, pinned to an exact commit, built against Godot 4.7's API; `dotnet
  build` builds the extension with SCons, as it builds wow-alchemy with Cargo.
