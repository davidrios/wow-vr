using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
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
public sealed class TileObjectsBuilder(ModelLibrary library, WdtMap map, IGameObjectSource gameObjects, GameObjectDisplayTable displays)
{
    /// <summary>Doodads are grouped into this many cells per side, so far ones can be hidden.</summary>
    private const int CellsPerSide = 4;

    /// <summary>How far away a doodad shows, per yard of its size.</summary>
    private const float VisibleRangePerYard = 40f;
    private const float MinVisibleRange = 150f;
    private const float MaxVisibleRange = 1200f;

    private static readonly double CellSize = WowCoords.TileSize / CellsPerSide;

    private const float MinLightOffset = 0.01f;

    public TileObjects Build(AdtTerrain terrain, int tileX, int tileY)
    {
        library.TrimCaches();

        var cells = new Dictionary<(int X, int Z, string Model, bool Interior), List<(PlacementTransform Transform, System.Numerics.Vector3 Light, System.Numerics.Vector3 Direction)>>();
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
            var groupCopies = new Dictionary<(int Group, string Model), List<(PlacementTransform Transform, System.Numerics.Vector3 Light, System.Numerics.Vector3 Direction)>>();
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

                    copies.Add((transform.Compose(doodad.Transform), wmo.LightOf(doodad), LightDirection(transform, wmo.Groups[group], doodad)));
                    continue;
                }

                AddDoodad(cells, doodad.Model, transform.Compose(doodad.Transform), interior, wmo.LightOf(doodad));
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

        AddGameObjects(cells, wmos, terrain, tileX, tileY);

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

    /// <summary>Spawned objects join the tile's doodads, which gives them streaming and collision.</summary>
    private void AddGameObjects(Dictionary<(int X, int Z, string Model, bool Interior), List<(PlacementTransform Transform, System.Numerics.Vector3 Light, System.Numerics.Vector3 Direction)>> cells, List<WmoInstance> wmos, AdtTerrain terrain, int tileX, int tileY)
    {
        var spawns = gameObjects.Read(tileX, tileY);
        if (spawns.Count == 0)
        {
            return;
        }

        // Any WMO touching this tile can hold an object, even if another tile draws it.
        var buildings = new List<(WmoAsset Asset, Transform3D ToLocal)>();
        foreach (var placement in terrain.Wmos)
        {
            if (library.GetWmo(placement.Model) is { } wmo)
            {
                buildings.Add((wmo, ToTransform3D(WowCoords.TilePlacement(placement.Position, placement.Rotation, 1f, tileX, tileY)).AffineInverse()));
            }
        }

        foreach (var spawn in spawns)
        {
            if (displays.ModelOf(spawn.DisplayId) is not { } model)
            {
                continue;
            }

            var transform = spawn.Placement(tileX, tileY);
            if (model.EndsWith(".wmo", System.StringComparison.OrdinalIgnoreCase))
            {
                if (library.GetWmo(model) is { } wmo)
                {
                    wmos.Add(new WmoInstance(wmo, ToTransform3D(transform), []));
                }

                continue;
            }

            var light = IndoorLightAt(buildings, ToGodot(transform.Origin));
            AddDoodad(cells, model, transform, light is not null, light ?? default);
        }
    }

    /// <summary>The light of the indoor WMO group holding a point on this tile. Null for a point outdoors.</summary>
    private static System.Numerics.Vector3? IndoorLightAt(List<(WmoAsset Asset, Transform3D ToLocal)> buildings, Vector3 point)
    {
        foreach (var (asset, toLocal) in buildings)
        {
            var local = toLocal * point;
            foreach (var group in asset.Groups)
            {
                if (group.Indoor && group.Bounds.HasPoint(local))
                {
                    var floor = group.FloorLight?.Sample(new System.Numerics.Vector3(local.X, local.Y, local.Z));
                    return WmoFloorLight.ForDoodad(floor ?? asset.Root.Ambient);
                }
            }
        }

        return null;
    }

    private bool OwnedBy(ModelPlacement placement, (int X, int Y) tile)
    {
        var origin = placement.OriginTile;
        return origin == tile || !map.HasTile(origin.X, origin.Y);
    }

    /// <summary>A doodad's light comes outward from the middle of its group, as in acazuc's client. Straight down if it's at the middle.</summary>
    private static System.Numerics.Vector3 LightDirection(in PlacementTransform wmo, WmoGroupAsset group, in WmoDoodad doodad)
    {
        var center = new System.Numerics.Vector3(group.Center.X, group.Center.Y, group.Center.Z);
        var away = doodad.Transform.Origin - center;
        var world = wmo.AxisX * away.X + wmo.AxisY * away.Y + wmo.AxisZ * away.Z;
        return world.LengthSquared() > MinLightOffset ? System.Numerics.Vector3.Normalize(world) : -System.Numerics.Vector3.UnitY;
    }

    private static void AddDoodad(Dictionary<(int X, int Z, string Model, bool Interior), List<(PlacementTransform Transform, System.Numerics.Vector3 Light, System.Numerics.Vector3 Direction)>> cells, string model, in PlacementTransform transform, bool interior, System.Numerics.Vector3 light)
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

        transforms.Add((transform, light, default));
    }

    private DoodadBatch BuildBatch(M2Asset model, List<(PlacementTransform Transform, System.Numerics.Vector3 Light, System.Numerics.Vector3 Direction)> copies, bool interior)
    {
        // Twelve floats per copy for the transform's three rows, and for an interior copy, four for its light and four for where that comes from.
        var stride = interior ? 20 : 12;
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

                // Alpha is how much of the world's light a WMO's vertex takes. A doodad's color is all it has.
                buffer[row + 15] = 0f;
                buffer[row + 16] = copies[i].Direction.X;
                buffer[row + 17] = copies[i].Direction.Y;
                buffer[row + 18] = copies[i].Direction.Z;
            }

            scale = Mathf.Max(scale, t.AxisX.Length());
            center += ToGodot(t.Origin);
        }

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = interior,
            UseCustomData = interior,
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
            model.CollisionFaces.Length > 0 || model.Particles.Length > 0 ? transforms.Select(t => ToTransform3D(t)).ToArray() : []);
    }

    private static Transform3D ToTransform3D(in PlacementTransform t) => new(
        new Basis(ToGodot(t.AxisX), ToGodot(t.AxisY), ToGodot(t.AxisZ)),
        ToGodot(t.Origin));

    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
