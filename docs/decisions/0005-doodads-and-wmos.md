# 0005: Drawing doodads and WMOs

**Status:** accepted (2026-11-01)

## Decision

- **Each tile draws the models whose origin it holds.** Tiles a model overlaps all list it, so this avoids duplicates without shared state. A test confirms the origin's tile lists its models.
- **Doodads are `MultiMesh`es**, one per model per cell, with 4x4 cells per tile. Each hides past a distance based on the model's size, so small things cost nothing far away.
- **WMOs are one mesh per group**, so groups cull separately. Indoor groups also hide at a distance.
- **WMO collision is per group**, kept only within 40 yards, at most one new body per frame, because physics builds a shape on the main thread when it's first used.
- **Portals decide what shows inside WMOs.** Each frame, `WmoPortals` walks a WMO's portal graph from the camera, and groups out of reach hide, along with the doodads and liquid in them. Interiors can stick out of the ground, so distance can't do this. These doodads are batched per group rather than per cell. WMOs with no portals or no indoor groups always show. Collision doesn't change with this.
- **Everything is added nearest first, across all tiles,** at most 8 items per frame, so the world fills in as a circle around the player.
- **A tile counts as ground only once its objects are in**, because buildings can have floors above the terrain.
- **Walking climbs steps of up to 1.2 yards**, which a capsule can't do alone. `DesktopPlayer.TryStepUp` and `movement_step.gd` do the same thing.

## Consequences

- Doodads collide using the simplified mesh in their files, within 25 yards. Scale is baked into a shape shared per scale, because bodies don't scale reliably.
- Models are drawn in their rest pose, without animation or particles. `0008-m2-effects.md` added second texture layers and texture and color animation.
- Indoor groups use their baked vertex colors without any sun, and doodads standing in them a baked color per copy. `docs/formats.md` says what counts as indoor. Creatures don't have this yet, so indoor ones are still lit by the sky.
- Which group the camera is in comes from group boxes, not the BSP tree the client uses, and boxes can overlap, so a little more can show than should. The walk is on the screen rectangle a portal covers with a 20% margin for the other eye in VR, not per eye. Within 2 yards of an indoor group's box, both it and the outside start, so the gaps between rooms that no box covers don't leave holes. Deeper inside a wall can still hide what's around you.
- The surface a teleport finds is the highest collision there, so landing inside a building puts you on its roof.
