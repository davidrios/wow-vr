# 0010: Loading screen

**Status:** accepted (2026-11-02)

## Decision

- **The world is hidden until it has all loaded,** at the start, after a landmark's button and when changing continent. "Loaded" is all of the 5x5 window of tiles, with their objects, plus the creatures near the player. Streaming while walking or flying is unchanged, and has no screen.
- **It's a place, not an overlay.** A starry void stays around the head and a panel floats in it, in 3D, so it works the same on desktop and in both eyes. The panel stays put as the head turns, and comes back round past 55 degrees.
- **The panel shows the map's own picture,** through `Map.dbc` and `LoadingScreens.dbc`, read from the player's client at runtime.
- **The camera's cull mask drops the world while it's covered,** which saves drawing it. It comes back for half a second behind the opaque screen before the fade, so the first frames' hitches don't show.
- **A warp places the player twice.** After everything settles the player is placed again and it settles again, because the first placement can come before the collision near where it lands, leaving the player under a floor. Starts with `--start=` fly, so they aren't placed.
- **The player is held, and sound fades out,** for as long as the screen is up. Up to 40 objects per frame enter the scene behind it, rather than 8.
- **Tours skip it,** to keep their frame times comparable with earlier runs and to measure streaming.

## Consequences

- Slowest frames behind the screen are about 150 ms, from adding objects faster. In a headset the runtime's reprojection has to cover these, which hasn't been tried.
- The wrist menu and hands are in the world's layer, so they can't be seen while it's up, and the wrist menu can't be opened.
- It gives up and shows the world after 3 minutes, so a bug in loading can't leave the player in the void.
