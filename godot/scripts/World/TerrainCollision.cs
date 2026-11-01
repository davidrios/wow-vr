using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using Godot;

namespace WowVR.World;

/// <summary>
/// Keeps physics bodies for the terrain chunks around the player. One body per chunk, added
/// a few per frame, because the physics engine builds a shape when it's first used, on the
/// main thread, which would be a visible hitch for a whole tile.
/// </summary>
public partial class TerrainCollision : Node
{
    /// <summary>Chunks kept in each direction, which covers a teleport arc.</summary>
    private const int ChunkRadius = 4;

    /// <summary>Kept a little further out, so walking back and forth doesn't rebuild.</summary>
    private const int UnloadRadius = ChunkRadius + 2;

    private const int MaxNewBodiesPerFrame = 4;

    /// <summary>Offsets to check, nearest first, so the ground underfoot is always added first.</summary>
    private static readonly (int X, int Z)[] Offsets =
        (from x in Enumerable.Range(-ChunkRadius, ChunkRadius * 2 + 1)
         from z in Enumerable.Range(-ChunkRadius, ChunkRadius * 2 + 1)
         orderby x * x + z * z
         select (x, z)).ToArray();

    private readonly Dictionary<(int X, int Y), (Node3D Node, TerrainCollisionData Data)> _tiles = [];
    private readonly Dictionary<(int X, int Z), StaticBody3D> _bodies = [];

    public void AddTile((int X, int Y) tile, Node3D node, TerrainCollisionData data) => _tiles[tile] = (node, data);

    /// <summary>The bodies are children of the tile's node, so they go with it.</summary>
    public void RemoveTile((int X, int Y) tile)
    {
        _tiles.Remove(tile);
        foreach (var chunk in _bodies.Keys.Where(c => TileOf(c) == tile).ToList())
        {
            _bodies.Remove(chunk);
        }
    }

    public bool HasChunk((int X, int Z) chunk) => _bodies.ContainsKey(chunk);

    public void Update((int X, int Z) center)
    {
        var added = 0;
        foreach (var offset in Offsets)
        {
            var chunk = (center.X + offset.X, center.Z + offset.Z);
            if (chunk.Item1 >= 0 && chunk.Item2 >= 0
                && !_bodies.ContainsKey(chunk) && _tiles.TryGetValue(TileOf(chunk), out var tile))
            {
                AddBody(chunk, tile.Node, tile.Data);
                if (++added == MaxNewBodiesPerFrame)
                {
                    break;
                }
            }
        }

        foreach (var chunk in _bodies.Keys.Where(c => Distance(c, center) > UnloadRadius).ToList())
        {
            _bodies[chunk].QueueFree();
            _bodies.Remove(chunk);
        }
    }

    private void AddBody((int X, int Z) chunk, Node3D tileNode, TerrainCollisionData data)
    {
        var faces = data.ChunkFaces(chunk.X % WowCoords.ChunksPerTile, chunk.Z % WowCoords.ChunksPerTile);
        var shape = new ConcavePolygonShape3D
        {
            Data = faces.Select(f => new Vector3(f.X, f.Y, f.Z)).ToArray(),
        };

        var body = new StaticBody3D();
        body.AddChild(new CollisionShape3D { Shape = shape });
        tileNode.AddChild(body);
        _bodies[chunk] = body;
    }

    private static (int X, int Y) TileOf((int X, int Z) chunk) =>
        (chunk.X / WowCoords.ChunksPerTile, chunk.Z / WowCoords.ChunksPerTile);

    private static int Distance((int X, int Z) a, (int X, int Z) b) =>
        System.Math.Max(System.Math.Abs(a.X - b.X), System.Math.Abs(a.Z - b.Z));
}
