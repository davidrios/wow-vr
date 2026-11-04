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
- Godot: our own build of the pinned `third_party/godot` with `patches/godot` applied, made by `scripts/build-godot.sh` into `third_party/godot/bin` (see `patches/godot/README.md`). `godot` in the commands below means that build's editor. Stock 4.7.2 also runs the game, only slower. Engine changes go in the patch queue, never straight into the submodule, and must not change Godot's API
- Build: `dotnet build WowVR.sln` (also builds the native library, which needs `cargo` and `../wow-alchemy`)
- Tests: while developing, run only what's relevant: `dotnet test --project src/WowVR.Formats.Tests -- --filter-class "*Name*"`. Run them all with `WOWVR_CLIENT_PATH` set only before committing, since those with client data are slow and skip without it. The same goes for the Godot checks below.
- Terrain flight test: `godot --path godot -- --tour` (flies Goldshire to Northshire, prints frame times, quits); `--tour=walk` walks it through physics. Its report splits frame time by phase and system and lists slow frames; add `--no-shadows` or `--no-ao` to compare, and `--disable-vsync` before the `--` for uncapped times
- Benchmark: `godot --path godot --resolution 3840x2050 --disable-vsync -- --bench` (waits for the world to load, turns once on the spot, prints frame, render and GPU times with a breakdown, then quits); add `--start=` for another spot and `--settings=<file.cfg>` to compare video settings without touching the saved ones. `--bench=still` holds the view instead and lists slow frames, collector pauses and what allocates. `--no-interior-culling` keeps drawing the outside from inside a WMO, to compare when something seems missing indoors
- Movement check: `godot --path godot -- --movement-check` (prints PASS or FAIL for standing, flying, teleports and recovery, then quits); add `--xr-player` to run the VR player's scene, physics and controllers without a headset, using simulated trackers (not menu clicks)
- Water check: `godot --path godot -- --start=Azeroth,-9409,-328,80 --water-check` (puts the player in deep water, and prints PASS or FAIL for floating, going under and rising again, then quits); add `--xr-player` for the VR player
- Continent check: `godot --path godot -- --continent-check` (presses each continent's menu button, which reloads the world, and prints PASS or FAIL for standing on each, then quits); add `--xr-player` for the VR player
- Creature check: `godot --path godot -- --creature-check` (places sample creatures, prints PASS or FAIL for animating, moving, staying on the ground and frame time, then quits); add `--xr-player` for the VR player, or `--sample-spawns=400` to test the cap with a crowd
- Loading check: `godot --path godot -- --loading-check` (prints PASS or FAIL for the loading screen hiding the world until it has loaded, at the start and after a landmark's button, then quits); add `--xr-player` for the VR player. `--hold-loading` keeps the screen up, to look at it, also with `--screenshot=`. Tours run without the screen
- Sample creatures: `godot --path godot -- --sample-spawns` shows made-up creatures around the start, which needs no spawn export. Exported files are read from the repo's `spawns/`, then `user://spawns`, or `--spawns=<folder>`
- Spawn files: `python tools/spawn-exporter/export_spawns.py` turns `data/azerothcore/*.json` into `spawns/`, creatures and objects both, and `python -m unittest discover tools/spawn-exporter` tests it
- Desktop keys, as WoW's: the mouse is a cursor; holding the right button turns the character and looks, the left orbits the camera, both run forward. Left click selects, right click talks, loots, attacks or opens. W/S forward and back, A/D turn (strafe with the right button held), Q/E strafe, Space jumps (and goes up while flying or swimming, X down), Num Lock runs, Tab picks the next enemy, Enter or / types in chat (`/s`, `/y`, `/w Name`, `.command`), B opens the bags, Shift goes faster, F toggles flying, the wheel moves the camera out or into first person, and Esc closes windows, then clears the target, then opens the game menu (back to the character list, log out, exit, then Options and Video) (or start with `-- --open-menu`, or `--open-menu=video` for its video settings). Offline, Tab still opens the menu
- VR controls: left stick moves, left Y toggles flying, left stick click boosts flight, left trigger teleports, left X opens the wrist menu, right stick turns, right trigger clicks in the menu. On a server the HUD is above the left wrist, the right trigger also selects what the laser points at, right A uses it (or the target), and right B types in chat with the virtual keyboard
- Debug keys: F2 prints the location and textures under the camera; F3 toggles a checkerboard of vertex-color-only chunks (or start with `-- --checkerboard`); F4 swaps vertex colors between BGRA and RGBA (or `-- --swap-vertex-colors`)
- Start elsewhere: `godot --path godot -- --start=Northrend,5800,650,650` (map, then WoW X, Y, Z, and optionally pitch and yaw; starts flying). Without it the game starts where the player last was, saved in `user://settings.cfg` under `[last_place]`; checks, tours, benchmarks and screenshots ignore that and don't save one
- Look at one moment: `godot --path godot -- --time=20:10` holds that time, and `--screenshot=<file.png>` saves a frame once the world has loaded and quits (don't commit the output)
- Texture viewer: `godot --path godot res://scenes/debug/TextureViewer.tscn -- --texture=<file>`
- Inspect tool: `dotnet run --project tools/inspect -- <command> <args>`
- Login screen: the game opens on it, to log in to an AzerothCore server, pick a realm and character, make or delete one, and enter the world in its body; "Explore offline" plays from spawn files. `--offline` or `--start=` skip it, `--server=host --character=Name` (with `WOWVR_AC_USER`/`WOWVR_AC_PASS`) go straight in, and checks and tours skip it unless given `--login`. Login check: `godot --path godot -- --server=<host> --login-check[=Character] [--shots=<folder>]` (with `WOWVR_AC_USER`/`WOWVR_AC_PASS`; logs in, visits the new character page, enters the world, and prints PASS or FAIL once the server's creatures and the body are there); add `--xr-player` for the VR player, or `--relog` to then press the game menu's Character selection and pass back at the list (needs a character the server lets out at once, as in an inn)
- Play check: `godot --path godot -- --server=<host> --character=Name --play-check[=Place]` (with `WOWVR_AC_USER`/`WOWVR_AC_PASS` for a GM account; turns GM mode on and revives, then prints PASS or FAIL for the server's position matching the client's standing, after walking and after a teleport, talking to the nearest NPC and chat). With `=Place` it also sends `.tele Place`, or the command itself if it starts with a dot (`"--play-check=.go xyz 54.23 0.28 -18.34 34"` goes into the Stockade), and checks again after the world reloads on another map; add `--xr-player` for the VR player
- Server probe: `dotnet run --project tools/ac-probe -- --host=<host> [--list] [--char=Name] [--moves] [--walk] ["--say=.gps;hello"]` logs in without Godot and prints the characters, or what the server shows around one, and with `--say` sends chat or commands and prints the replies. `WOWVR_NET_LOG_MOVES=1` logs every movement packet sent, in the game too
- Run the game (desktop): open `godot/project.godot` in Godot 4 (.NET), or `godot --path godot`
- Run the game (VR): `godot --path godot --xr-mode on`
- Headless server (from M7): `godot --path godot --headless -- --server`

## When unsure
- Check format details against the wowdev wiki and compare output with reference tools rather than guessing. Prefer WoWModelViewer (tag `0.7.0.6` for 3.3.5a), then wow.export and Noggit.
- If a format behaves differently than expected, write a failing test with the real file (skipped without client data) before fixing.
- Ask before adding new dependencies or changing the architecture in PLAN.md.
