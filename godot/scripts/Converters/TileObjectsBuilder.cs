using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using Godot;

namespace WowVR.Converters;

/// <summary>Every copy of one model in one part of a tile.</summary>
public sealed record DoodadBatch(MultiMesh MultiMesh, float VisibleRange, Vector3 Center, M2Asset Model, Transform3D[] Transforms);

/// <summary>A WMO on a tile, relative to the tile's corner.</summary>
/// <param name="GroupDoodads">Doodads that show and hide with a group, by group.</param>
public sealed record WmoInstance(WmoAsset Asset, Transform3D Transform, Dictionary<int, List<DoodadBatch>> GroupDoodads);

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

        var cells = new Dictionary<(int X, int Z, string Model, bool Interior), List<(PlacementTransform Transform, System.Numerics.Vector3 Light)>>();
        var wmos = new List<WmoInstance>();
        var tile = (tileX, tileY);

        foreach (var placement in terrain.Doodads)
        {
            if (OwnedBy(placement, tile))
            {
                var transform = WowCoords.TilePlacement(placement.Position, placement.Rotation, placement.Scale, tileX, tileY);
                AddDoodad(cells, placement.Model, transform, false, default);
            }
        }

        foreach (var placement in terrain.Wmos)
        {
            if (!OwnedBy(placement, tile) || library.GetWmo(placement.Model) is not { } wmo)
            {
                continue;
            }

            var transform = WowCoords.TilePlacement(placement.Position, placement.Rotation, 1f, tileX, tileY);
            var groupCopies = new Dictionary<(int Group, string Model), List<(PlacementTransform Transform, System.Numerics.Vector3 Light)>>();
            foreach (var doodad in wmo.Root.DoodadsInSet(placement.DoodadSet))
            {
                var interior = wmo.IsInterior(doodad);
                var group = wmo.GroupOf(doodad);
                if (group >= 0)
                {
                    var key = (group, doodad.Model.ToLowerInvariant());
                    if (!groupCopies.TryGetValue(key, out var copies))
                    {
                        groupCopies[key] = copies = [];
                    }

                    copies.Add((transform.Compose(doodad.Transform), wmo.Root.Ambient + doodad.Color));
                    continue;
                }

                AddDoodad(cells, doodad.Model, transform.Compose(doodad.Transform), interior, wmo.Root.Ambient + doodad.Color);
            }

            var groupDoodads = new Dictionary<int, List<DoodadBatch>>();
            foreach (var (key, copies) in groupCopies)
            {
                if (library.GetM2(key.Model) is { } model)
                {
                    if (!groupDoodads.TryGetValue(key.Group, out var batchList))
                    {
                        groupDoodads[key.Group] = batchList = [];
                    }

                    batchList.Add(BuildBatch(model, copies, interior: true));
                }
            }

            wmos.Add(new WmoInstance(wmo, ToTransform3D(transform), groupDoodads));
        }

        var batches = new List<DoodadBatch>();
        foreach (var (cell, transforms) in cells)
        {
            if (library.GetM2(cell.Model) is { } model)
            {
                batches.Add(BuildBatch(model, transforms, cell.Interior));
            }
        }

        return new TileObjects(batches, wmos);
    }

    private bool OwnedBy(ModelPlacement placement, (int X, int Y) tile)
    {
        var origin = placement.OriginTile;
        return origin == tile || !map.HasTile(origin.X, origin.Y);
    }

    private static void AddDoodad(Dictionary<(int X, int Z, string Model, bool Interior), List<(PlacementTransform Transform, System.Numerics.Vector3 Light)>> cells, string model, in PlacementTransform transform, bool interior, System.Numerics.Vector3 light)
    {
        var key = (
            System.Math.Clamp((int)(transform.Origin.X / CellSize), 0, CellsPerSide - 1),
            System.Math.Clamp((int)(transform.Origin.Z / CellSize), 0, CellsPerSide - 1),
            model.ToLowerInvariant(),
            interior);

        if (!cells.TryGetValue(key, out var transforms))
        {
            cells[key] = transforms = [];
        }

        transforms.Add((transform, light));
    }

    private DoodadBatch BuildBatch(M2Asset model, List<(PlacementTransform Transform, System.Numerics.Vector3 Light)> copies, bool interior)
    {
        // Twelve floats per copy for the transform's three rows, and four for an interior copy's light.
        var stride = interior ? 16 : 12;
        var transforms = copies.Select(c => c.Transform).ToList();
        var buffer = new float[transforms.Count * stride];
        var scale = 0f;
        var center = Vector3.Zero;
        for (var i = 0; i < transforms.Count; i++)
        {
            var t = transforms[i];
            var row = i * stride;
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
            if (interior)
            {
                var light = copies[i].Light;
                buffer[row + 12] = Mathf.Min(light.X, 1f);
                buffer[row + 13] = Mathf.Min(light.Y, 1f);
                buffer[row + 14] = Mathf.Min(light.Z, 1f);
                buffer[row + 15] = 1f;
            }

            scale = Mathf.Max(scale, t.AxisX.Length());
            center += ToGodot(t.Origin);
        }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = interior,
            Mesh = interior ? library.InteriorMeshOf(model) : model.Mesh,
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
