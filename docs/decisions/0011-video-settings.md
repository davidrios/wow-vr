# 0011: Frame rate and video settings

**Status:** accepted (2026-11-03)

## Context

In the headset the frame rate fell below 90, and a maximized 4K desktop window ran at about 120 FPS. `--bench` showed it was the CPU, not the GPU: frame times were the same at 720p and 4K, and the GPU took about 2 ms of a 7 ms frame. Removing spawns brought Stormwind from 143 to 411 FPS, and creatures alone made up the difference.

## Decision

- **Creature skeletons don't run modifiers on their own.** A `Skeleton3D` with any `SkeletonModifier3D` (closed hands, billboarded bones) re-poses every bone and re-uploads its skin every frame, even when nothing moves, which every guard holding a weapon did. Modifiers are set to manual, so they run only when the animation has moved a bone.
- **Animation is advanced by hand, less often further away:** every frame within 25 yards, 30 times a second to 50, 20 to 100 and 12 beyond. Each creature starts at a random point in its interval so they don't all update in the same frame. **Full-rate far animation** turns this off: Stormwind goes from 299 to 178 FPS. Presets leave it off.
- **Creatures don't animate on worker threads.** Making each a sub-thread process group, so Godot runs them in parallel, was slower than the main thread (Stormwind 145 against 172 FPS, all at full rate): every bone a worker uploads goes through the engine's locked command queue, and every transform it changes is handed back to the main thread. Real parallelism would mean sampling and posing skeletons ourselves.
- **Creatures set their transform only when it changes,** because setting it notifies every bone attachment and mesh below, even when it's the same.
- **A Video settings page** holds what costs frame time, with Low, Medium and High presets. High is what the world looked like before. Measured with `--bench` at 3840x2050 on an RTX 3090 and Ryzen 9 5900X, in Goldshire and Stormwind:

  | Setting | Saves |
  | --- | --- |
  | Detail distance at 50% | 0.5 ms of CPU, a third of draw calls |
  | Creature distance 80 yd, 20 animated | 0.3 ms of CPU |
  | Shadows off | 0.2 to 0.3 ms, a third of draw calls |
  | Ambient occlusion off | 0.4 ms of GPU |
  | Render scale 70% (FSR 1) | 0.4 ms of GPU |
  | Grass off, particles off | 0.1 to 0.2 ms each |

  Presets, as FPS in Stormwind and Undercity: High 287 to 300, Medium 338 to 356, Low about 470. Before this work High was 132 to 165.
- **Render scale upscales with FSR 1 on desktop only.** In VR each eye is scaled bilinearly: there's no anti-aliasing for FSR to build on, so its sharpening makes edges crawl, differently in each eye, and the headset resamples the image for its lenses anyway.
- **Inside a WMO with no view out, the outside isn't drawn.** Portal culling knows when the camera is in an interior group and no exterior group is in view. Then the camera stops drawing the outside's render layer (terrain, liquids, outdoor doodads and groups, grass, skyboxes), other WMOs are hidden, the sun goes out (indoor groups have their own light, and creatures there were in the ground's shadow), and creatures not in a group in view are hidden and not posed. In Undercity on High this took a frame from 4.29 to 2.49 ms on desktop, and draw calls from 2,440 to 730.
- **Settings are read from memory,** not the file each time, because creatures read theirs several times a second. `--settings=<file>` uses another file, so benchmarks don't touch the player's own.

## Consequences

- Far creatures' animation is visibly steppier up close to 50 yards, if looked for.
- Every creature in the scene animates. A cap on how many did, with the rest frozen in place, was dropped: with the engine patches of `0012-patched-godot.md` and far creatures updating less often, animating all 150 rather than 60 cost 0.06 ms in Goldshire and 0.15 ms in Stormwind. Creature distance is what's left to turn down.
- Desktop has a field of view setting, Godot's 75 degrees by default, since a wide window stretches the edges. A headset's lenses fix its own.
- The render scale hasn't been tried in the headset, where it applies per eye.
- On this PC the GPU is far from the limit, so the GPU settings were measured but matter little here. A slower GPU or a headset at a high resolution is where they help.
