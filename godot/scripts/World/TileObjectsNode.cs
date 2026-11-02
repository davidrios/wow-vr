using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Models;
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

    /// <summary>A WMO whose indoor groups show only through its portals, with what each group has in the scene.</summary>
    private sealed class PortalWmo(Node3D node, WmoPortals portals, Aabb bounds)
    {
        public Node3D Node { get; } = node;
        public WmoPortals Portals { get; } = portals;
        public Aabb Bounds { get; } = bounds;
        public List<Node3D>[] Members { get; } = Enumerable.Range(0, portals.GroupCount).Select(_ => new List<Node3D>()).ToArray();
        public bool[] Visible { get; } = new bool[portals.GroupCount];
        public bool[] Shown { get; } = Enumerable.Repeat(true, portals.GroupCount).ToArray();
    }

    private readonly List<PortalWmo> _portalWmos = [];
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
            AddDoodadCell(batch);

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

            PortalWmo? portalWmo = null;
            if (wmo.Asset.Portals is { } portals)
            {
                portalWmo = new PortalWmo(node, portals, BoundsOf(wmo.Asset));
                _portalWmos.Add(portalWmo);
            }

            void Track(Node3D item, int groupIndex)
            {
                if (portalWmo is not null)
                {
                    portalWmo.Members[groupIndex].Add(item);
                    item.Visible = portalWmo.Shown[groupIndex];
                }
            }

            foreach (var (groupIndex, batches) in wmo.GroupDoodads)
            {
                foreach (var batch in batches)
                {
                    AddDoodadCell(batch);

                    // Their transforms already include the WMO's, so they go beside its node.
                    _pending.Add(new PendingItem(batch.Center, () =>
                    {
                        var item = new MultiMeshInstance3D
                        {
                            Multimesh = batch.MultiMesh,
                            VisibilityRangeEnd = batch.VisibleRange,
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        };
                        AddChild(item);
                        Track(item, groupIndex);
                    }));
                }
            }

            for (var groupIndex = 0; groupIndex < wmo.Asset.Groups.Length; groupIndex++)
            {
                var index = groupIndex;
                var group = wmo.Asset.Groups[groupIndex];
                if (group.Shape is not null)
                {
                    _collision.Add(new CollisionGroup(node, group));
                }

                if (group.Mesh is { } mesh)
                {
                    var center = wmo.Transform * group.Center;
                    _pending.Add(new PendingItem(center, () =>
                    {
                        var item = new MeshInstance3D
                        {
                            Mesh = mesh,
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                            VisibilityRangeEnd = group.Indoor ? mesh.GetAabb().Size.Length() + IndoorVisibleMargin : 0f,
                        };
                        node.AddChild(item);
                        Track(item, index);
                    }));
                }

                if (group.LiquidMesh is { } liquid)
                {
                    var center = wmo.Transform * liquid.GetAabb().GetCenter();
                    _pending.Add(new PendingItem(center, () =>
                    {
                        var item = new MeshInstance3D
                        {
                            Mesh = liquid,
                            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                        };
                        node.AddChild(item);
                        Track(item, index);
                    }));
                }
            }
        }
    }

    /// <summary>Collision stays whether or not a doodad's group is showing.</summary>
    private void AddDoodadCell(DoodadBatch batch)
    {
        if (batch.Transforms.Length == 0)
        {
            return;
        }

        var bounds = new Aabb(batch.Transforms[0].Origin, Vector3.Zero);
        foreach (var transform in batch.Transforms)
        {
            bounds = bounds.Expand(transform.Origin);
        }

        _doodadCells.Add(new DoodadCell(batch.Model, batch.Transforms, bounds));
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

    private static Aabb BoundsOf(WmoAsset asset)
    {
        var (min, max) = asset.Root.Bounds;
        var a = new Vector3(min.X, min.Z, -min.Y);
        var b = new Vector3(max.X, max.Z, -max.Y);
        return new Aabb(a, Vector3.Zero).Expand(b);
    }

    /// <summary>Shows each portal WMO's groups that can be seen from the camera, in every mode the same way.</summary>
    private void UpdatePortals()
    {
        if (_portalWmos.Count == 0 || GetViewport().GetCamera3D() is not { } camera)
        {
            return;
        }

        var projection = camera.GetCameraProjection();
        var view = camera.GlobalTransform.AffineInverse();
        foreach (var wmo in _portalWmos)
        {
            var fromWmo = view * wmo.Node.GlobalTransform;

            // Portals are in the file's axes, Z up, so a point goes to Godot's first.
            (float, float, float) ToClip(System.Numerics.Vector3 file)
            {
                var godot = WowCoords.ModelToGodot(file);
                var inView = fromWmo * new Vector3(godot.X, godot.Y, godot.Z);
                var clip = projection * new Vector4(inView.X, inView.Y, inView.Z, 1f);
                return (clip.X, clip.Y, clip.W);
            }

            var cameraLocal = wmo.Node.ToLocal(camera.GlobalPosition);
            var cameraFile = new System.Numerics.Vector3(cameraLocal.X, -cameraLocal.Z, cameraLocal.Y);
            wmo.Portals.Compute(cameraFile, ToClip, wmo.Visible);
            for (var group = 0; group < wmo.Visible.Length; group++)
            {
                if (wmo.Visible[group] != wmo.Shown[group])
                {
                    wmo.Shown[group] = wmo.Visible[group];
                    foreach (var item in wmo.Members[group])
                    {
                        item.Visible = wmo.Visible[group];
                    }
                }
            }
        }
    }

    public override void _Process(double delta)
    {
        UpdatePortals();
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
