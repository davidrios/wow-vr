# 0004: Terrain collision and the ground guard

**Status:** accepted (2026-11-01)

## Decision

Terrain collision is one `ConcavePolygonShape3D` per chunk, on its own `StaticBody3D`, kept only for the chunks within 4 of the player and added at most 4 per frame. Physics runs on Jolt, which ships with Godot.

- **Trimesh, not `HeightMapShape3D`.** A chunk's 145 vertices aren't a regular grid, and height maps can't leave holes open. The trimesh is the surface you see.
- **Per chunk, not per tile.** Jolt builds a shape on the main thread when it's first used, so a 65,000-triangle tile would be a hitch.
- **Bodies are children of the tile's node**, so the floating origin moves them with the mesh.

`GroundGuard` handles what streaming makes possible. It holds a walking player still until the ground underfoot has loaded, places them on the surface after a teleport, and recovers them when a ray finds nothing below their feet: up to the surface if they're under it, otherwise back to the last solid ground.

## Consequences

- Terrain holes bounce the player back until M4 adds what's in them. Recovery only acts when nothing at all is below the player, so caves and cellars will work once they have floors.
- Players must stay off the terrain's physics layer, or the guard's rays hit the player. Both use layer 20.
- Fast flight can outrun collision, so flying into unloaded terrain is possible. The guard lifts the player out when it loads.
