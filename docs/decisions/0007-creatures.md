# 0007: Creatures

**Status:** accepted (2026-11-02)

## Decision

- **Movement is a function of the world clock.** `CreatureMotion.At(clock)` keeps no state, and is seeded by the spawn's guid. M7 can then share the clock and send no creature data. Only turning and height smoothing depend on the frame, and they're cosmetic.
- **Wandering** divides time into periods long enough to cross the radius and pause. In each, the creature walks to that period's random point, which is the next one's starting point.
- **Paths** walk at walking speed, wait at each point, and go straight from the last point back to the first. The database has more path behaviors than this, which the exporter work should look at.
- **Each creature gets its own `Skeleton3D` and `AnimationPlayer`,** and shares its mesh, skin and animations with others of its display or model. Godot's own animation system keeps this simple, and the cap below pays for it.
- **Bones follow the file's formula exactly,** with rest poses at their pivots and no rest rotation. `docs/formats.md` shows why that's equivalent.
- **Only near creatures exist.** Spawns within 150 yards get a node, at most 150 and at most 2 new per frame, and leave past 175. The nearest 60 animate and move, and the rest hold their pose until they rank in the nearest 60. The distance and count are video settings, and far ones animate less often (see `0011-video-settings.md`).
- **Height comes from the data where it can.** Paths give it. Standing creatures keep their spawn's. A wanderer follows `TerrainHeights`, which needs no physics, unless its spawn is more than 3 yards from the terrain: that means a floor, bridge or cave, so it keeps its spawn's height. Within 35 yards of the player, physics also has buildings, so it asks with a ray.
- **Characters use their baked texture,** plus geosets and worn head and shoulder models, instead of composing textures. That's how the client's NPC data is meant to be drawn.
- **Sample spawns stand in for the database.** `--sample-spawns` makes up a few creatures around the start, looking displays up in the player's client, so nothing derived from the database is in the repo.

## Spawn files

`tools/spawn-exporter/export_spawns.py` turns AzerothCore's tables, as JSON files in `data/azerothcore/`, into `spawns/<map id>/<tile x>_<tile y>.json`. Both folders are gitignored and must stay out of the repo. The game reads that folder, then `user://spawns`, or another given with `--spawns=`. Each file is an array of these rows, in WoW coordinates:

```json
{ "guid": 7, "entry": 1423, "name": "Stormwind Guard", "displayId": 3167,
  "pos": [-9450.5, 60.25, 56.75], "orientation": 3.14, "wanderRadius": 0,
  "movementType": 2, "scale": 1, "walkSpeed": 2.5, "weapons": [1542, 0],
  "waypoints": [{ "x": 1, "y": 2, "z": 3, "delayMs": 500 }] }
```

`movementType` is 0 to stand, 1 to wander and 2 to follow waypoints, as AzerothCore's `MovementType` is. `scale` multiplies the display's, and `weapons` are the right and left hands' `ItemDisplayInfo` rows. A row's tile is the one holding its position. The example's values are made up.

- **Sources:** `creature`, `creature_template`, `creature_equip_template`, `item_template` and `waypoint_data` come from a 2024 export. `game_event_creature`, `creature_addon`, `gameobject_template` and `game_event_gameobject` came later, from AzerothCore's `data/sql/base/db_world` on 2026-11-02, so their guids don't all match. `data/azerothcore/` has no converter for these: re-exporting them all from one version would be better.
- **Paths** come from `creature_addon`, falling back on AzerothCore's convention of the guid times 10. The two disagree for 204 spawns, and either finds a path for about 6,000 of the 6,220 with `MovementType` 2. The rest stand still.
- **Left out:** triggers (`flags_extra` `0x80`), `[DND]` dummies, and anything not in the normal phase.
- **Events are skipped.** `game_event_creature` lists them, with a positive event id. Because of the guid drift it only reaches 8,470 of its 10,415 spawns here, and 3,125 of 3,723 Wild Turkeys, so a creature with at least half its spawns listed goes entirely, and so does a spawn within a yard of an older one of another kind. Event spawns the table misses, of creatures it mostly doesn't list, can still appear.
- **Choices:** a creature with several entries or models picks one by its guid, so it always looks the same. The client's model probabilities aren't in these tables.

## Game objects

The exporter also writes `spawns/<map id>/objects/<tile x>_<tile y>.json`, from `gameobject` and `gameobject_template`. The game adds them to the tile's doodad batches, so they get streaming and collision. One inside an indoor WMO group takes the floor's light.

- **Rotation:** a row's quaternion, in WoW's axes, or its orientation if the quaternion is all zeros.
- **Left out:** gathering nodes and chests (type 3) and fishing holes, because a server shows only some of a pool at once, so showing them all would be far too many. Also traps, moving transports, invisible helpers, and anything not in the normal phase.
- **Holidays:** `game_event_gameobject` lists them, with the same guid drift as creatures, so an object with at least half its spawns listed goes entirely. A list of names in the exporter catches what's left, and a holiday object not on it can still appear.
- **Static:** doors stay as modeled, nothing can be used, and animation is whatever the doodad path gives. Flames are particles, which aren't drawn yet.

## Consequences

- Idle plays stand's variations, weighted by their frequency, which covers the fidgets each model has. The plan's emotes aren't otherwise played, and without the database nothing says which creature would use which.
- Everything walks. Nothing runs yet, because nothing chases anything. `Run` is only a fallback for models with no walk.
- Weapons are held rather than sheathed, with open hands, since nothing's in combat and hand closing isn't done.
- Global sequences repeat through each clip rather than running on their own clock, so a tail or wing can jump when a clip restarts.
- Creatures hold a pose past the animation cap, and can slide while doing so until they rank in the nearest 60.
- Not drawn: particles, display alpha (ghosts) and `CreatureGeosetData`, so non-character models show all their geosets.
- A spawn doesn't say which WMO group it's in, so within 40 yards of the player a ray finds what a creature stands on. On an indoor group it takes the `MOCV` of the drawn triangle under it, with most of the color removed, and fades over about a second. Further away, or until a ray has hit a group, it's lit by the sky. How much color it keeps is by eye, and nothing confirms the client does this.
- Creatures don't collide with anything, and never block the player.
- On this PC's RTX 3090 on desktop, with no vsync: 12 sample creatures run at 344 FPS, and a crowd of 400 spawns, giving 151 nodes with 60 animated, at 107 FPS. That crowd is 6.4 ms over the 12's, which is too much of VR's 11.1 ms, so a real town's numbers need checking in the headset. If they're too slow, lower `MaxNodes` and `MaxAnimated` first. (Superseded by `0011-video-settings.md`, which found most of that cost was skeleton modifiers running every frame.)
