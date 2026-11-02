using System;
using System.Collections.Generic;
using WowVR.Converters;
using Godot;

namespace WowVR.World;

/// <summary>
/// Holds a tile's doodads and WMOs until <see cref="TileManager"/> adds them, nearest first,
/// and keeps collision for the WMO groups near the player. It's a child of the tile's node,
/// so it moves and leaves with it.
/// </summary>
public partial class TileObjectsNode : Node3D
{
    /// <summary>Indoor groups can't be seen from far away, so they don't need drawing.</summary>
    private const float IndoorVisibleMargin = 300f;

    /// <summary>A group gets collision this far from its bounds, in yards.</summary>
    private const float CollisionRange = 40f;

    /// <summary>Kept a little further out, so walking back and forth doesn't rebuild.</summary>
    private const float CollisionKeepRange = 70f;

    /// <summary>A shape's first use is slow, so at most this many start per frame.</summary>
    private const int MaxNewBodiesPerFrame = 1;

    /// <summary>The player must move this far before pending items are sorted again.</summary>
    private const float ResortDistance = 32f;

    /// <summary>A doodad gets collision this far away, in yards. They're small, so this is closer than for WMOs.</summary>
    private const float DoodadCollisionRange = 25f;
    private const float DoodadCollisionKeepRange = 40f;
    private const int MaxNewDoodadBodiesPerFrame = 2;

    /// <summary>The group the check finds doodad bodies by.</summary>
    public const string DoodadCollisionGroup = "doodad_collision";

    private readonly record struct CollisionGroup(Node3D Wmo, WmoGroupAsset Group);

    /// <summary>The copies of a model in one cell, with bounds around them to skip the whole cell quickly.</summary>
    private sealed record DoodadCell(M2Asset Model, Transform3D[] Transforms, Aabb Bounds);

    private readonly List<DoodadCell> _doodadCells = [];
    private readonly Dictionary<(DoodadCell Cell, int Copy), StaticBody3D> _doodadBodies = [];

    /// <summary>Something waiting to enter the scene, at a position relative to this node.</summary>
    private readonly record struct PendingItem(Vector3 Center, Action Add);

    private readonly List<PendingItem> _pending = [];
    private readonly List<CollisionGroup> _collision = [];
    private readonly Dictionary<CollisionGroup, StaticBody3D> _bodies = [];
    private Vector3 _sortedFor;
    private bool _sorted;

    public IPlayer Player { get; set; } = null!;

    /// <summary>Everything is in the scene.</summary>
    public bool Finished => _pending.Count == 0;

    public void Fill(TileObjects objects)
    {
        foreach (var batch in objects.Doodads)
        {
            if (batch.Transforms.Length > 0)
            {
                var bounds = new Aabb(batch.Transforms[0].Origin, Vector3.Zero);
                foreach (var transform in batch.Transforms)
                {
                    bounds = bounds.Expand(transform.Origin);
                }

                _doodadCells.Add(new DoodadCell(batch.Model, batch.Transforms, bounds));
            }

            _pending.Add(new PendingItem(batch.Center, () => AddChild(new MultiMeshInstance3D
            {
                Multimesh = batch.MultiMesh,
                VisibilityRangeEnd = batch.VisibleRange,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            })));
        }

        foreach (var wmo in objects.Wmos)
        {
            // Only a transform, so it can go straight in.
            var node = new Node3D { Transform = wmo.Transform };
            AddChild(node);
            foreach (var group in wmo.Asset.Groups)
            {
                if (group.Shape is not null)
                {
                    _collision.Add(new CollisionGroup(node, group));
                }

                if (group.Mesh is { } mesh)
                {
                    var center = wmo.Transform * group.Center;
                    _pending.Add(new PendingItem(center, () => node.AddChild(new MeshInstance3D
                    {
                        Mesh = mesh,
                        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        VisibilityRangeEnd = group.Indoor ? mesh.GetAabb().Size.Length() + IndoorVisibleMargin : 0f,
                    })));
                }
            }
        }
    }

    /// <summary>How far this tile's next item is from the player, or infinity if it has none.</summary>
    public float NextItemDistance(Vector3 feet)
    {
        if (_pending.Count == 0)
        {
            return float.PositiveInfinity;
        }

        // Sorted furthest first, so taking the nearest doesn't shift the list.
        var local = ToLocal(feet);
        if (!_sorted || local.DistanceTo(_sortedFor) > ResortDistance)
        {
            _pending.Sort((a, b) => b.Center.DistanceSquaredTo(local).CompareTo(a.Center.DistanceSquaredTo(local)));
            _sortedFor = local;
            _sorted = true;
        }

        return _pending[^1].Center.DistanceTo(local);
    }

    /// <summary>Adds the nearest pending item to the scene.</summary>
    public void AddNextItem()
    {
        var item = _pending[^1];
        _pending.RemoveAt(_pending.Count - 1);
        item.Add();
    }

    public override void _Process(double delta)
    {
        UpdateCollision();
        UpdateDoodadCollision();
    }

    private void UpdateDoodadCollision()
    {
        var local = ToLocal(Player.Feet);

        var added = 0;
        foreach (var cell in _doodadCells)
        {
            if (added == MaxNewDoodadBodiesPerFrame || !cell.Bounds.Grow(DoodadCollisionRange).HasPoint(local))
            {
                continue;
            }

            for (var copy = 0; copy < cell.Transforms.Length && added < MaxNewDoodadBodiesPerFrame; copy++)
            {
                var transform = cell.Transforms[copy];
                if (transform.Origin.DistanceTo(local) < DoodadCollisionRange && !_doodadBodies.ContainsKey((cell, copy)))
                {
                    _doodadBodies[(cell, copy)] = AddDoodadBody(cell.Model, transform);
                    added++;
                }
            }
        }

        foreach (var (key, body) in _doodadBodies)
        {
            if (key.Cell.Transforms[key.Copy].Origin.DistanceTo(local) > DoodadCollisionKeepRange)
            {
                body.QueueFree();
                _doodadBodies.Remove(key);
            }
        }
    }

    private StaticBody3D AddDoodadBody(M2Asset model, Transform3D transform)
    {
        var body = new StaticBody3D
        {
            Transform = new Transform3D(transform.Basis.Orthonormalized(), transform.Origin),
        };
        body.AddToGroup(DoodadCollisionGroup);
        body.AddChild(new CollisionShape3D { Shape = model.ShapeAt(transform.Basis.X.Length()) });
        AddChild(body);
        return body;
    }

    private void UpdateCollision()
    {
        var feet = Player.Feet;
        var added = 0;
        foreach (var entry in _collision)
        {
            var local = entry.Wmo.ToLocal(feet);
            var has = _bodies.TryGetValue(entry, out var body);
            if (!has && added < MaxNewBodiesPerFrame && entry.Group.Bounds.Grow(CollisionRange).HasPoint(local))
            {
                body = new StaticBody3D();
                body.AddChild(new CollisionShape3D { Shape = entry.Group.Shape });
                entry.Wmo.AddChild(body);
                _bodies[entry] = body;
                added++;
            }
            else if (has && !entry.Group.Bounds.Grow(CollisionKeepRange).HasPoint(local))
            {
                body!.QueueFree();
                _bodies.Remove(entry);
            }
        }
    }
}
