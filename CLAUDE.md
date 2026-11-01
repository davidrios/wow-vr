# CLAUDE.md

VR exploration of the WotLK WoW world in Godot 4 (.NET / C#), reading the player's own 3.3.5a (build 12340) client, with spawn data from AzerothCore. See PLAN.md for goals, architecture and milestones. Work milestone by milestone; don't jump ahead.

## Hard rules
- **Never commit or generate Blizzard assets** (game files, extracted textures/models/maps/DBCs, music, or converted versions). They're read at runtime from the player's install. If a test needs game data, read it from `AZVR_CLIENT_PATH` and skip when it's unset.
- **Never commit exported spawn data** from emulator databases; only the exporter tool.
- Keep `src/AzerothVR.Formats` free of any `Godot` dependency.

## Conventions
- Every feature works in both desktop and VR mode; test on desktop first. World systems must not assume XR.
- C# throughout; GDScript only where an addon requires it.
- 1 Godot unit = 1 WoW yard. All WoW↔Godot coordinate conversion goes through `WowCoords` — never inline it.
  `godot = (-wowY, wowZ, -wowX)`
- Parsing and mesh building run off the main thread; only scene-tree changes happen on it.
- File parsing lives in the Rust project `D:\work\wow-alchemy` (public, so keep it general-purpose) and is reached through its C ABI. `src/AzerothVR.Formats` holds safe C# wrappers. See `docs/decisions/0002-rust-parsers.md`.
- Format fixes go to wow-alchemy. Add notes on using each format to `docs/formats.md`.
- Record significant decisions as short ADRs in `docs/decisions/`.

## Commands
- Build: `dotnet build AzerothVR.sln` (also builds the native library, which needs `cargo` and `../wow-alchemy`)
- Tests: `dotnet test --project src/AzerothVR.Formats.Tests`
- Terrain flight test: `godot --path godot -- --tour` (flies Goldshire to Northshire, prints frame times, quits)
- Debug keys: F2 prints the location and textures under the camera; F3 toggles a checkerboard of vertex-color-only chunks (or start with `-- --checkerboard`); F4 swaps vertex colors between BGRA and RGBA (or `-- --swap-vertex-colors`)
- Start elsewhere: `godot --path godot -- --start=Northrend,5800,650,650` (map, then WoW X, Y, Z)
- Texture viewer: `godot --path godot res://scenes/debug/TextureViewer.tscn -- --texture=<file>`
- Inspect tool: `dotnet run --project tools/inspect -- <command> <args>`
- Run the game (desktop): open `godot/project.godot` in Godot 4 (.NET), or `godot --path godot`
- Run the game (VR): `godot --path godot --xr-mode on`
- Headless server (from M7): `godot --path godot --headless -- --server`

## When unsure
- Check format details against the wowdev wiki and compare output with reference tools rather than guessing. Prefer WoWModelViewer (tag `0.7.0.6` for 3.3.5a), then wow.export and Noggit.
- If a format behaves differently than expected, write a failing test with the real file (skipped without client data) before fixing.
- Ask before adding new dependencies or changing the architecture in PLAN.md.
