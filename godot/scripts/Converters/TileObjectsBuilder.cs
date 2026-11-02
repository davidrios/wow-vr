using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using Godot;

namespace WowVR.Converters;

/// <summary>Every copy of one model in one part of a tile.</summary>
public sealed record DoodadBatch(MultiMesh MultiMesh, float VisibleRange, Vector3 Center, M2Asset Model, Transform3D[] Transforms);

/// <summary>A WMO on a tile, relative to the tile's corner.</summary>
public sealed record WmoInstance(WmoAsset Asset, Transform3D Transform);

/// <summary>What a tile needs added to the scene.</summary>
public sealed record TileObjects(List<DoodadBatch> Doodads, List<WmoInstance> Wmos);

/// <summary>
/// Turns a tile's placements into batches. Runs off the main thread. Tiles a model overlaps
/// all list it, so each draws only the models whose origin it holds, which a test checks.
/// </summary>
public sealed class TileObjectsBuilder(ModelLibrary library, WdtMap map)
{
    /// <summary>Doodads are grouped into this many cells per side, so far ones can be hidden.</summary>
    private const int CellsPerSide = 4;

    /// <summary>How far away a doodad shows, per yard of its size.</summary>
    private const float VisibleRangePerYard = 40f;
    private const float MinVisibleRange = 150f;
    private const float MaxVisibleRange = 1200f;

    private static readonly double CellSize = WowCoords.TileSize / CellsPerSide;

    public TileObjects Build(AdtTerrain terrain, int tileX, int tileY)
    {
        library.TrimCaches();

        var cells = new Dictionary<(int X, int Z, string Model), List<PlacementTransform>>();
        var wmos = new List<WmoInstance>();
        var tile = (tileX, tileY);

        foreach (var placement in terrain.Doodads)
        {
            if (OwnedBy(placement, tile))
            {
                var transform = WowCoords.TilePlacement(placement.Position, placement.Rotation, placement.Scale, tileX, tileY);
                AddDoodad(cells, placement.Model, transform);
            }
        }

        foreach (var placement in terrain.Wmos)
        {
            if (!OwnedBy(placement, tile) || library.GetWmo(placement.Model) is not { } wmo)
            {
                continue;
            }

            var transform = WowCoords.TilePlacement(placement.Position, placement.Rotation, 1f, tileX, tileY);
            wmos.Add(new WmoInstance(wmo, ToTransform3D(transform)));

            foreach (var doodad in wmo.Root.DoodadsInSet(placement.DoodadSet))
            {
                AddDoodad(cells, doodad.Model, transform.Compose(doodad.Transform));
            }
        }

        var batches = new List<DoodadBatch>();
        foreach (var (cell, transforms) in cells)
        {
            if (library.GetM2(cell.Model) is { } model)
            {
                batches.Add(BuildBatch(model, transforms));
            }
        }

        return new TileObjects(batches, wmos);
    }

    private bool OwnedBy(ModelPlacement placement, (int X, int Y) tile)
    {
        var origin = placement.OriginTile;
        return origin == tile || !map.HasTile(origin.X, origin.Y);
    }

    private static void AddDoodad(Dictionary<(int X, int Z, string Model), List<PlacementTransform>> cells, string model, in PlacementTransform transform)
    {
        var key = (
            System.Math.Clamp((int)(transform.Origin.X / CellSize), 0, CellsPerSide - 1),
            System.Math.Clamp((int)(transform.Origin.Z / CellSize), 0, CellsPerSide - 1),
            model.ToLowerInvariant());

        if (!cells.TryGetValue(key, out var transforms))
        {
            cells[key] = transforms = [];
        }

        transforms.Add(transform);
    }

    private static DoodadBatch BuildBatch(M2Asset model, List<PlacementTransform> transforms)
    {
        // Twelve floats per copy: the transform's three rows.
        var buffer = new float[transforms.Count * 12];
        var scale = 0f;
        var center = Vector3.Zero;
        for (var i = 0; i < transforms.Count; i++)
        {
            var t = transforms[i];
            var row = i * 12;
            buffer[row] = t.AxisX.X;
            buffer[row + 1] = t.AxisY.X;
            buffer[row + 2] = t.AxisZ.X;
            buffer[row + 3] = t.Origin.X;
            buffer[row + 4] = t.AxisX.Y;
            buffer[row + 5] = t.AxisY.Y;
            buffer[row + 6] = t.AxisZ.Y;
            buffer[row + 7] = t.Origin.Y;
            buffer[row + 8] = t.AxisX.Z;
            buffer[row + 9] = t.AxisY.Z;
            buffer[row + 10] = t.AxisZ.Z;
            buffer[row + 11] = t.Origin.Z;
            scale = Mathf.Max(scale, t.AxisX.Length());
            center += ToGodot(t.Origin);
        }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = model.Mesh,
            InstanceCount = transforms.Count,
            Buffer = buffer,
        };

        var range = Mathf.Clamp(model.Radius * scale * VisibleRangePerYard, MinVisibleRange, MaxVisibleRange);
        return new DoodadBatch(
            multiMesh,
            range,
            center / transforms.Count,
            model,
            model.CollisionFaces.Length > 0 ? transforms.Select(t => ToTransform3D(t)).ToArray() : []);
    }

    private static Transform3D ToTransform3D(in PlacementTransform t) => new(
        new Basis(ToGodot(t.AxisX), ToGodot(t.AxisY), ToGodot(t.AxisZ)),
        ToGodot(t.Origin));

    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
