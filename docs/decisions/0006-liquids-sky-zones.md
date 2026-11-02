# 0006: Liquids, sky and zone sound

**Status:** accepted (2026-11-01)

## Decision

- **Liquids are read by new code, not the old parsers.** `wow_alchemy_adt::read_liquid_layers` and `wow_alchemy_wmo::render::RenderLiquid` read the 3.3.5a layouts. The ADT crate's older `mh2o` module reads the wrong offsets and instance size, and still serves its conversion and editing tools.
- **Each tile has one liquid mesh, with one surface per liquid type.** WMO groups have their own. Materials are shared per liquid type, so the sky recolors them all at once.
- **Water is painted, to match WoW's hand-painted world.** Color and transparency come from the light tables, over soft blotches, patches of thin ripple lines, a pale shore and a broken road of sunlight or moonlight. The client's `lake_a` frames add faint detail, faded into each other so they don't flicker. Magma and slime show their textures.
- **Water suits a headset.** Nothing reads the screen and no geometry moves. Generated noise fades to flat grey in its mipmaps and ripple lines fade with distance, so distant water can't shimmer. Reflections use each eye's own direction, because `VIEW` is the same for both. Positions come from UV, so re-centering the world doesn't move the pattern, and every layer repeats a whole number of times per tile, so tiles join up.
- **The sky is drawn by a shader from the light tables' 5 colors,** with fog below the horizon, as Noggit does. Clouds and the client's skybox models aren't drawn. The sun and moon use the client's `textures\SunCenter`, `SunGlare`, `moon` and `moonglare`, at sizes picked by eye, because which the client draws and how isn't documented. Stars aren't read from files.
- **Colors update twice a second,** since each update re-renders the sky's small cubemap. Reflections are off, so that cubemap costs little.
- **Zone sound uses the area under the player's chunk,** with subzones inheriting their parent's sounds when they have none. Sounds that are the same object keep playing across a border.

## Tuned by eye

These aren't from a file, and are constants in `SkyController` and `SunPath`:

- Direct light is halved. The tables' value is meant to be scaled by an intensity the client reads from elsewhere.
- The sun rises at 5:30 and sets at 20:30, to fit when the tables brighten and darken.
- A game day takes 24 real minutes.

## Consequences

- Swimming and the underwater view came later, and are in `formats.md`. There was no swimming, liquid collision or underwater view at first: you walk along the lakebed, and the lights' underwater settings aren't used.
- Only clear weather's lights are used.
- Northrend's zone lights are hardcoded in the client's executable, so its zones show the global sky until someone transcribes them.
- The sun casts real-time shadows up to 150 yards, in 2 splits, and the menu can turn them off. The client also bakes shadows into terrain (`MCSH`), which aren't read. Light stays at least a little above the horizon so low sun doesn't lose all shading, so shadows at dusk are shorter than the sun's position suggests, and at night the moon casts them. Doodads inside buildings don't cast, since nothing lights them from outside.
- Playtesting found that some music seems wrong at times. The cause isn't known yet.
- WMO areas aren't read, so an inn plays its zone's sounds rather than its own. Zone intro music isn't played either.
- A WMO group with a liquid type but no MLIQ grid should be full of liquid, according to the wiki. These draw nothing.
- Lava and slime don't glow or follow the files' flow directions.
- True scale replaced player-sized as the default, after playtesting found player-sized too big.
