# Handoff

Where the project stands, as of 2026-11-02. `PLAN.md` has the goals and milestones, `CLAUDE.md` the rules and commands, and `docs/decisions/` the reasoning.

## State

M0 to M5 have passed. M6 (creatures) is built and running from real data, and is waiting on the checks below.

Everything is pushed, to `wow-vr` `main` and `wow-alchemy` `ffi-crate` (`c6e2dec` or later). `wow-vr` builds against `../wow-alchemy`, so that checkout needs the matching branch.

## Setup that isn't in git

- `WOWVR_CLIENT_PATH=D:\Games\wow_335a_orig`.
- `data/azerothcore/` holds the AzerothCore tables as JSON, and `spawns/` the files made from them by `python tools/spawn-exporter/export_spawns.py`. Both are gitignored and must stay out of the repo.
- The original tables are in `D:\stuff\wow.export_exports\database`, from 2024. `game_event_creature` and `creature_addon` came later from AzerothCore's repo, so some guids don't match (see `decisions/0007-creatures.md`).

## Open problems, most important first

1. **Performance.** Real spawns and portal culling raised the uncapped tour's median from 2.1 ms to about 5 ms, and the 60 Hz tour now misses frames: 45 over 22 ms on its last capped run, before shadows, against 3 before these changes. It hasn't been profiled.
2. **Hitches.** One-off frames of 50 to 150 ms appear on the tour. New shaders compiling on first use is the likely cause, but this isn't confirmed.
3. **No headset session since M5.** Creatures, sounds, effects, billboards, shadows and portal culling have only run on desktop, or through `--xr-player` without a headset. Billboards and portal culling use one camera or a margin for both eyes, which most needs checking.
4. **M6's check is by screenshot only.** Marshal Dughan and the guards stand where the data says. No kobold in Fargodeep has been seen clearly, and nobody has judged whether movement looks natural.
5. **Some motion and sound is unconfirmed.** Texture scrolling, doodad animation and creature sounds pass tests and checks, but nobody has watched or listened to them. Texture rotation direction has no reference.

## Guesses to revisit

- Indoor doodads' light is `MOHD` ambient plus `MODD` color.
- Color tracks replace lighting with a tint, following WoWModelViewer.
- Sun and moon sizes and `ShadowOpacity` (0.75) are by eye.
- Some event spawns can still slip through, which exporting all tables from one version would fix.

## Known gaps

- Creatures indoors are lit by the sky, because a spawn doesn't say which WMO group it's in.
- Not drawn or played: particles and ribbons (so fires have no flames), terrain's baked shadows (`MCSH`), `CreatureGeosetData`, ghost alpha, combat sounds, doodad sound events and player footsteps.
- Weapons are held with open hands, never sheathed.
- Some music seemed wrong in M5's playtest, and the cause isn't known.
- Portal culling finds the camera's group with boxes rather than the BSP tree. Deep inside a wall, what's around you can vanish.
- The desktop menu is cut off below Music volume at the default 1152x648 window.

## Where to look for answers

The checked-out references can be behind, so check their newer versions too.

- **WoWModelViewer:** tag `0.7.0.6` is the 3.3.5a code. Upstream's `develop` is far past the local clone, and has a World Viewer whose `docs/world-viewer/10-light-sky-fog.md` works out sky, fog and light from the client's shaders. Interior lighting is planned there as a later stage.
- **wow.export:** read it with `gh api repos/Kruithne/wow.export`, since the local clone stops at 2025-09-06.
- **wowdev.wiki:** needs a browser, and its bot check usually clears on a retry. `M2/Rendering` and `M2/.skin/WotLK_shader_selection` were the best sources for how M2s draw.

## Next

1. Profile, then fix performance and hitches.
2. Do a headset session and M6's hands-on check, then mark M6 in `PLAN.md`.
3. Start M7, multiplayer. Creature movement already depends only on `CreatureManager.Clock`, which M7's server should take over.
