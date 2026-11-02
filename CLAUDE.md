# CLAUDE.md

VR exploration of the WotLK WoW world in Godot 4 (.NET / C#), reading the player's own 3.3.5a (build 12340) client, with spawn data from AzerothCore. See PLAN.md for goals, architecture and milestones. Work milestone by milestone; don't jump ahead.

## Hard rules
- **Never commit or generate Blizzard assets** (game files, extracted textures/models/maps/DBCs, music, or converted versions). They're read at runtime from the player's install. If a test needs game data, read it from `WOWVR_CLIENT_PATH` and skip when it's unset.
- **Never commit exported spawn data** from emulator databases; only the exporter tool. The AzerothCore tables in `data/` and the files in `spawns/` are both gitignored.
- Keep `src/WowVR.Formats` free of any `Godot` dependency.

## Conventions
- Every feature works in both desktop and VR mode; test on desktop first. World systems must not assume XR.
- C# throughout; GDScript only where an addon requires it.
- 1 Godot unit = 1 WoW yard. All WoW↔Godot coordinate conversion goes through `WowCoords` — never inline it.
  `godot = (-wowY, wowZ, -wowX)`
- Parsing and mesh building run off the main thread; only scene-tree changes happen on it.
- File parsing lives in the Rust project `D:\work\wow-alchemy` (public, so keep it general-purpose) and is reached through its C ABI. `src/WowVR.Formats` holds safe C# wrappers. See `docs/decisions/0002-rust-parsers.md`.
- Format fixes go to wow-alchemy. Add notes on using each format to `docs/formats.md`.
- Record significant decisions as short ADRs in `docs/decisions/`.

## Commands
- Build: `dotnet build WowVR.sln` (also builds the native library, which needs `cargo` and `../wow-alchemy`)
- Tests: while developing, run only what's relevant: `dotnet test --project src/WowVR.Formats.Tests -- --filter-class "*Name*"`. Run them all with `WOWVR_CLIENT_PATH` set only before committing, since those with client data are slow and skip without it. The same goes for the Godot checks below.
- Terrain flight test: `godot --path godot -- --tour` (flies Goldshire to Northshire, prints frame times, quits); `--tour=walk` walks it through physics. Its report splits frame time by phase and system and lists slow frames; add `--no-shadows` to compare, and `--disable-vsync` before the `--` for uncapped times
- Movement check: `godot --path godot -- --movement-check` (prints PASS or FAIL for standing, flying, teleports and recovery, then quits); add `--xr-player` to run the VR player's scene, physics and controllers without a headset, using simulated trackers (not menu clicks)
- Continent check: `godot --path godot -- --continent-check` (presses each continent's menu button, which reloads the world, and prints PASS or FAIL for standing on each, then quits); add `--xr-player` for the VR player
- Creature check: `godot --path godot -- --creature-check` (places sample creatures, prints PASS or FAIL for animating, moving, staying on the ground and frame time, then quits); add `--xr-player` for the VR player, or `--sample-spawns=400` to test the cap with a crowd
- Sample creatures: `godot --path godot -- --sample-spawns` shows made-up creatures around the start, which needs no spawn export. Exported files are read from the repo's `spawns/`, then `user://spawns`, or `--spawns=<folder>`
- Spawn files: `python tools/spawn-exporter/export_spawns.py` turns `data/azerothcore/*.json` into `spawns/`, creatures and objects both, and `python -m unittest discover tools/spawn-exporter` tests it
- Desktop keys: WASD walks, Shift goes faster, F toggles flying, Q/E go down/up while flying, Tab opens the menu (or start with `-- --open-menu`)
- VR controls: left stick moves, left Y toggles flying, left stick click boosts flight, left trigger teleports, left X opens the wrist menu, right stick turns, right trigger clicks in the menu
- Debug keys: F2 prints the location and textures under the camera; F3 toggles a checkerboard of vertex-color-only chunks (or start with `-- --checkerboard`); F4 swaps vertex colors between BGRA and RGBA (or `-- --swap-vertex-colors`)
- Start elsewhere: `godot --path godot -- --start=Northrend,5800,650,650` (map, then WoW X, Y, Z, and optionally pitch and yaw; starts flying)
- Look at one moment: `godot --path godot -- --time=20:10` holds that time, and `--screenshot=<file.png>` saves a frame once the world has loaded and quits (don't commit the output)
- Texture viewer: `godot --path godot res://scenes/debug/TextureViewer.tscn -- --texture=<file>`
- Inspect tool: `dotnet run --project tools/inspect -- <command> <args>`
- Run the game (desktop): open `godot/project.godot` in Godot 4 (.NET), or `godot --path godot`
- Run the game (VR): `godot --path godot --xr-mode on`
- Headless server (from M7): `godot --path godot --headless -- --server`

## When unsure
- Check format details against the wowdev wiki and compare output with reference tools rather than guessing. Prefer WoWModelViewer (tag `0.7.0.6` for 3.3.5a), then wow.export and Noggit.
- If a format behaves differently than expected, write a failing test with the real file (skipped without client data) before fixing.
- Ask before adding new dependencies or changing the architecture in PLAN.md.
