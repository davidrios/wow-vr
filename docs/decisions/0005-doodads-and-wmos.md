# 0005: Drawing doodads and WMOs

**Status:** accepted (2026-11-01)

## Decision

- **Each tile draws the models whose origin it holds.** Tiles a model overlaps all list it, so this avoids duplicates without shared state. A test confirms the origin's tile lists its models.
- **Doodads are `MultiMesh`es**, one per model per cell, with 4x4 cells per tile. Each hides past a distance based on the model's size, so small things cost nothing far away.
- **WMOs are one mesh per group**, so groups cull separately. Indoor groups also hide at a distance.
- **WMO collision is per group**, kept only within 40 yards, at most one new body per frame, because physics builds a shape on the main thread when it's first used.
- **Everything is added nearest first, across all tiles,** at most 8 items per frame, so the world fills in as a circle around the player.
- **A tile counts as ground only once its objects are in**, because buildings can have floors above the terrain.
- **Walking climbs steps of up to 1.2 yards**, which a capsule can't do alone. `DesktopPlayer.TryStepUp` and `movement_step.gd` do the same thing.

## Consequences

- Doodads collide using the simplified mesh in their files, within 25 yards. Scale is baked into a shape shared per scale, because bodies don't scale reliably.
- Models are drawn in their rest pose, without animation, particles or second texture layers. Texture animation and M6's skinning build on this.
- Sky lighting is still a fixed sun. Indoor groups use their baked vertex colors without any sun. M5 replaces this.
- The surface a teleport finds is the highest collision there, so landing inside a building puts you on its roof.
