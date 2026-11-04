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
/// doors and the like come and go as it sends and destroys them. Doors, chests and levers whose
/// models have them play their open and close animations as the server changes their state, and
/// an open door stops blocking the way; models whose standing animation moves animate as doodads
/// do. Each hangs under its tile's node, so it moves with the world when the origin re-centers, and
/// has collision. Ships and zeppelins are <see cref="Transports"/>' instead.
/// </summary>
public partial class ServerObjects : Node
{
    /// <summary>Types that open and close: doors, buttons and levers, chests, and goobers such as cages.</summary>
    private static readonly uint[] OpeningTypes = [0, 1, 3, 10];

    /// <summary>In this state a door is open, a chest has been opened, a lever pulled.</summary>
    private const uint ActiveState = 0;

    /// <summary>Building a model is slow, so at most this many objects join the scene per frame.</summary>
    private const int MaxAddsPerFrame = 4;

    private sealed class Placed(GameObjectInfo info, (int X, int Y) tile, Node3D node, CreatureNode? animated, List<CollisionObject3D> bodies)
    {
        public GameObjectInfo Info { get; set; } = info;
        public (int X, int Y) Tile { get; } = tile;
        public Node3D Node { get; } = node;
        public CreatureNode? Animated { get; } = animated;
        public List<CollisionObject3D> Bodies { get; } = bodies;
    }

    private sealed record Built(GameObjectInfo Info, M2Asset? M2, WmoAsset? Wmo, CreatureAsset? Animated);

    private TileManager _tiles = null!;
    private CreatureLibrary _creatures = null!;
    private GameObjectDisplayTable _displays = null!;
    private readonly Dictionary<ulong, Placed> _placed = [];
    private readonly Dictionary<ulong, GameObjectInfo> _wanted = [];
    private readonly Dictionary<(int X, int Y), Node3D> _containers = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private readonly HashSet<ulong> _building = [];

    public int Count => _placed.Count;

    /// <summary>What the server shows, placed or not.</summary>
    public int Shown => _wanted.Count;

    /// <summary>The objects that open and close, with the clip each plays, for checks.</summary>
    public IEnumerable<(ulong Guid, string Name, uint State, string Clip)> Animated
    {
        get
        {
            foreach (var (guid, placed) in _placed)
            {
                if (placed.Animated is { } node)
                {
                    yield return (guid, placed.Info.Name, placed.Info.State, node.Clip);
                }
            }
        }
    }

    public void Start(TileManager tiles, Formats.Assets.IAssetSource source, CreatureLibrary creatures)
    {
        _tiles = tiles;
        _creatures = creatures;
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
        var same = previous is not null && previous.DisplayId == info.DisplayId && previous.Position == info.Position && previous.Rotation == info.Rotation;
        if (same && _placed.TryGetValue(info.Guid, out var placed))
        {
            // An animated one opens or closes where it is; a still door that opens is taken away.
            if (placed.Animated is not null)
            {
                if (IsOpen(previous!) != IsOpen(info))
                {
                    ApplyState(placed, info, atEnd: false);
                }

                placed.Info = info;
                return;
            }

            if (Opens(previous!) == Opens(info))
            {
                placed.Info = info;
                return;
            }
        }
        else if (same && _building.Contains(info.Guid))
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

    private static bool IsOpen(GameObjectInfo info) => Array.IndexOf(OpeningTypes, info.Type) >= 0 && info.State == ActiveState;

    /// <summary>A door without an opening animation is taken away while open, out of the player's way.</summary>
    private static bool Opens(GameObjectInfo info) => info.Type == 0 && info.State == ActiveState;

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
        if (_displays.ModelOf((int)info.DisplayId) is not { } model || !_building.Add(info.Guid))
        {
            return;
        }

        var library = _tiles.Models;
        var opening = Array.IndexOf(OpeningTypes, info.Type) >= 0;
        Task.Run(() =>
        {
            try
            {
                if (model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
                {
                    _built.Enqueue(new Built(info, null, library.GetWmo(model), null));
                    return;
                }

                // Doors, chests and levers with their own open and close animations get a skeleton to play them.
                var m2 = library.GetM2(model);
                CreatureAsset? animated = null;
                if (opening && _creatures.BuildModel(model) is { } asset
                    && (asset.Skeleton.Clips.ContainsKey(M2SequenceIds.Open) || asset.Skeleton.Clips.ContainsKey(M2SequenceIds.Close)))
                {
                    animated = asset;
                }

                _built.Enqueue(new Built(info, m2, null, animated));
            }
            catch (Exception e)
            {
                GD.PushWarning($"Could not build object {info.Name} ({model}): {e.Message}");
                _built.Enqueue(new Built(info, null, null, null));
            }
        });
    }

    public override void _Process(double delta)
    {
        for (var i = 0; i < MaxAddsPerFrame && _built.TryDequeue(out var built); i++)
        {
            _building.Remove(built.Info.Guid);

            // Placed as it is now, in case it changed while it was building.
            if (_wanted.GetValueOrDefault(built.Info.Guid) is { } wanted && !_placed.ContainsKey(wanted.Guid))
            {
                Place(wanted, built);
            }
        }

        foreach (var placed in _placed.Values)
        {
            placed.Animated?.Animate(delta, 0);
        }
    }

    private void Place(GameObjectInfo info, Built built)
    {
        if (built.M2 is null && built.Wmo is null && built.Animated is null)
        {
            return;
        }

        if (built.Animated is null && Opens(info))
        {
            return;
        }

        var tile = WowCoords.TileAt(info.Position.X, info.Position.Y);
        var node = new Node3D { Name = $"Object_{info.Guid:X}" };
        var transform = ToTransform(info, tile);
        node.Transform = new Transform3D(transform.Basis.Orthonormalized(), transform.Origin);
        var scale = transform.Basis.X.Length();
        var bodies = new List<CollisionObject3D>();
        CreatureNode? animated = null;
        if (built.Animated is { } asset)
        {
            animated = new CreatureNode { Name = "Model", Scale = Vector3.One * scale };
            animated.Build(asset, "");
            node.AddChild(animated);
        }
        else if (built.M2 is { } m2)
        {
            node.AddChild(new MeshInstance3D { Mesh = m2.Mesh, Scale = Vector3.One * scale });
            AddParticles(node, m2, scale);
        }

        if (built.M2 is { CollisionFaces.Length: > 0 } solid)
        {
            var body = new StaticBody3D { CollisionLayer = TileManager.TerrainLayer };
            body.AddToGroup(TileObjectsNode.DoodadCollisionGroup);
            body.AddChild(new CollisionShape3D { Shape = solid.ShapeAt(scale) });
            node.AddChild(body);
            bodies.Add(body);
        }
        else if (built.Wmo is { } wmo)
        {
            foreach (var group in wmo.Groups)
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
                    bodies.Add(body);
                }
            }
        }

        Container(tile).AddChild(node);
        var placed = new Placed(info, tile, node, animated, bodies);
        _placed[info.Guid] = placed;
        if (animated is not null)
        {
            animated.StartIdle();
            if (IsOpen(info))
            {
                ApplyState(placed, info, atEnd: true);
            }
        }
    }

    /// <summary>Opens or closes an animated object, and lets the player through an open door.</summary>
    private static void ApplyState(Placed placed, GameObjectInfo info, bool atEnd)
    {
        var open = IsOpen(info);
        if (!placed.Animated!.PlayAndHold(open ? M2SequenceIds.Open : M2SequenceIds.Close, atEnd) && !open)
        {
            placed.Animated.PlayAndHold(M2SequenceIds.Stand, atEnd);
        }

        if (info.Type == 0)
        {
            foreach (var body in placed.Bodies)
            {
                body.CollisionLayer = open ? 0 : TileManager.TerrainLayer;
            }
        }
    }

    /// <summary>Portals' swirls, braziers' flames: the server only shows what's near, so they all run.</summary>
    private static void AddParticles(Node3D node, M2Asset m2, float scale)
    {
        if (!AppSettings.Particles || m2.Particles.Length == 0)
        {
            return;
        }

        var holder = new Node3D { Scale = Vector3.One * scale };
        foreach (var particle in m2.Particles)
        {
            holder.AddChild(particle.NewNode(particle.Position));
        }

        node.AddChild(holder);
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
