using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// The game objects the server shows, drawn as it says: chests, herbs and ore, mailboxes, signs,
/// doors and the like come and go as it sends and destroys them, and an open door gets out of the
/// way. Each hangs under its tile's node, so it moves with the world when the origin re-centers,
/// and has collision. Ships and zeppelins are <see cref="Transports"/>' instead.
/// </summary>
public partial class ServerObjects : Node
{
    private const uint DoorType = 0;

    /// <summary>A door in this state is open.</summary>
    private const uint ActiveState = 0;

    /// <summary>Building a model is slow, so at most this many objects join the scene per frame.</summary>
    private const int MaxAddsPerFrame = 4;

    private sealed record Placed(GameObjectInfo Info, (int X, int Y) Tile, Node3D Node);

    private TileManager _tiles = null!;
    private GameObjectDisplayTable _displays = null!;
    private readonly Dictionary<ulong, Placed> _placed = [];
    private readonly Dictionary<ulong, GameObjectInfo> _wanted = [];
    private readonly Dictionary<(int X, int Y), Node3D> _containers = [];
    private readonly ConcurrentQueue<(GameObjectInfo Info, M2Asset? M2, WmoAsset? Wmo)> _built = new();
    private readonly HashSet<ulong> _building = [];

    public int Count => _placed.Count;

    /// <summary>What the server shows, placed or not.</summary>
    public int Shown => _wanted.Count;

    public void Start(TileManager tiles, Formats.Assets.IAssetSource source)
    {
        _tiles = tiles;
        Task.Run(() => GameObjectDisplayTable.Read(source)).ContinueWith(read => Callable.From(() =>
        {
            if (IsInstanceValid(this) && read.IsCompletedSuccessfully)
            {
                _displays = read.Result;
                foreach (var info in _wanted.Values)
                {
                    Build(info);
                }
            }
        }).CallDeferred());
        tiles.TileAdded += OnTileAdded;
        tiles.TileRemoved += OnTileRemoved;
    }

    public override void _ExitTree()
    {
        if (_tiles is not null)
        {
            _tiles.TileAdded -= OnTileAdded;
            _tiles.TileRemoved -= OnTileRemoved;
        }
    }

    /// <summary>The server shows an object, or says it changed, as a door opening.</summary>
    public void Show(GameObjectInfo info)
    {
        if (info.Type == Transports.MapObjectTransport || info.DisplayId == 0)
        {
            return;
        }

        var previous = _wanted.GetValueOrDefault(info.Guid);
        _wanted[info.Guid] = info;

        // Only a change it shows needs it built again.
        if (previous is not null && previous.DisplayId == info.DisplayId && previous.Position == info.Position
            && previous.Rotation == info.Rotation && Hidden(previous) == Hidden(info) && _placed.ContainsKey(info.Guid))
        {
            return;
        }

        Remove(info.Guid, keepWanted: true);
        if (_displays is not null)
        {
            Build(info);
        }
    }

    public void Hide(ulong guid) => Remove(guid, keepWanted: false);

    /// <summary>An open door is out of the way: the client has no door animation to swing it open.</summary>
    private static bool Hidden(GameObjectInfo info) => info.Type == DoorType && info.State == ActiveState;

    private void Remove(ulong guid, bool keepWanted)
    {
        if (!keepWanted)
        {
            _wanted.Remove(guid);
        }

        if (_placed.Remove(guid, out var placed))
        {
            placed.Node.QueueFree();
        }
    }

    private void Build(GameObjectInfo info)
    {
        if (Hidden(info) || _displays.ModelOf((int)info.DisplayId) is not { } model || !_building.Add(info.Guid))
        {
            return;
        }

        var library = _tiles.Models;
        Task.Run(() =>
        {
            try
            {
                if (model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
                {
                    _built.Enqueue((info, null, library.GetWmo(model)));
                }
                else
                {
                    _built.Enqueue((info, library.GetM2(model), null));
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"Could not build object {info.Name} ({model}): {e.Message}");
                _built.Enqueue((info, null, null));
            }
        });
    }

    public override void _Process(double delta)
    {
        for (var i = 0; i < MaxAddsPerFrame && _built.TryDequeue(out var built); i++)
        {
            _building.Remove(built.Info.Guid);

            // Gone, changed or hidden while it was building.
            var wanted = _wanted.GetValueOrDefault(built.Info.Guid);
            if (wanted is null || wanted != built.Info || Hidden(wanted))
            {
                if (wanted is not null && wanted != built.Info)
                {
                    Build(wanted);
                }

                continue;
            }

            Place(built.Info, built.M2, built.Wmo);
        }
    }

    private void Place(GameObjectInfo info, M2Asset? m2, WmoAsset? wmo)
    {
        if (m2 is null && wmo is null)
        {
            return;
        }

        var tile = WowCoords.TileAt(info.Position.X, info.Position.Y);
        var node = new Node3D { Name = $"Object_{info.Guid:X}" };
        var transform = ToTransform(info, tile);
        node.Transform = new Transform3D(transform.Basis.Orthonormalized(), transform.Origin);
        var scale = transform.Basis.X.Length();
        if (m2 is not null)
        {
            node.AddChild(new MeshInstance3D { Mesh = m2.Mesh, Scale = Vector3.One * scale });

            // Portals' swirls, braziers' flames: the server only shows what's near, so they all run.
            if (AppSettings.Particles && m2.Particles.Length > 0)
            {
                var holder = new Node3D { Scale = Vector3.One * scale };
                foreach (var particle in m2.Particles)
                {
                    holder.AddChild(particle.NewNode(particle.Position));
                }

                node.AddChild(holder);
            }
            if (m2.CollisionFaces.Length > 0)
            {
                var body = new StaticBody3D { CollisionLayer = TileManager.TerrainLayer };
                body.AddToGroup(TileObjectsNode.DoodadCollisionGroup);
                body.AddChild(new CollisionShape3D { Shape = m2.ShapeAt(scale) });
                node.AddChild(body);
            }
        }
        else
        {
            foreach (var group in wmo!.Groups)
            {
                if (group.Mesh is { } mesh)
                {
                    node.AddChild(new MeshInstance3D { Mesh = mesh });
                }

                if (group.Shape is { } shape)
                {
                    var body = new StaticBody3D { CollisionLayer = TileManager.TerrainLayer };
                    body.AddChild(new CollisionShape3D { Shape = shape });
                    node.AddChild(body);
                }
            }
        }

        Container(tile).AddChild(node);
        _placed[info.Guid] = new Placed(info, tile, node);
    }

    /// <summary>Where it goes on its tile, from its position and its rotation as the server packs it.</summary>
    private static Transform3D ToTransform(GameObjectInfo info, (int X, int Y) tile)
    {
        var rotation = info.Rotation;
        var spawn = new GameObjectSpawn
        {
            Guid = 0,
            DisplayId = (int)info.DisplayId,
            Pos = [info.Position.X, info.Position.Y, info.Position.Z],
            Rotation = [rotation.X, rotation.Y, rotation.Z, rotation.W],
            Scale = info.Scale > 0 ? info.Scale : 1f,
        };
        var p = spawn.Placement(tile.X, tile.Y);
        static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        return new Transform3D(new Basis(V(p.AxisX), V(p.AxisY), V(p.AxisZ)), V(p.Origin));
    }

    /// <summary>A tile's node for objects, made when first needed; it waits outside the scene until the tile loads.</summary>
    private Node3D Container((int X, int Y) tile)
    {
        if (!_containers.TryGetValue(tile, out var container))
        {
            container = new Node3D { Name = "ServerObjects" };
            _containers[tile] = container;
        }

        return container;
    }

    private void OnTileAdded((int X, int Y) tile, Node3D node, TerrainHeights heights)
    {
        var container = Container(tile);
        if (container.GetParent() is null)
        {
            node.AddChild(container);
        }

        Refill(tile);
    }

    private void OnTileRemoved((int X, int Y) tile)
    {
        // The container left with the tile's node, which frees it; its objects are built again when the tile comes back.
        if (!_containers.Remove(tile, out var container))
        {
            return;
        }

        if (container.GetParent() is null)
        {
            container.QueueFree();
        }

        foreach (var (guid, placed) in new List<KeyValuePair<ulong, Placed>>(_placed))
        {
            if (placed.Tile == tile)
            {
                _placed.Remove(guid);
            }
        }
    }

    /// <summary>A tile that came back gets its objects built again.</summary>
    private void Refill((int X, int Y) tile)
    {
        foreach (var info in _wanted.Values)
        {
            if (WowCoords.TileAt(info.Position.X, info.Position.Y) == tile && !_placed.ContainsKey(info.Guid))
            {
                Build(info);
            }
        }
    }
}
