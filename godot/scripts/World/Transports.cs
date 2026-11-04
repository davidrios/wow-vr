using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Desktop;
using WowVR.Formats;
using WowVR.Formats.Database;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// The server's ships and zeppelins: each one's WMO moves along its taxi path in time with the
/// server's cycle, with collision to stand on, and carries the player standing on its deck. The
/// player's movement then goes to the server relative to it, which is what has the server take
/// them along to the other continent.
/// </summary>
public partial class Transports : Node3D
{
    /// <summary>Game object type 15: a transport that runs between docks, often across maps.</summary>
    public const uint MapObjectTransport = 15;

    /// <summary>How far below the feet the deck can be and still carry the player, in yards.</summary>
    private const float DeckReach = 1.5f;

    private sealed class Ride
    {
        public required GameObjectInfo Info { get; init; }
        public required TransportPath Path { get; init; }
        public required ulong StartProgress { get; init; }
        public required double StartClock { get; init; }
        public required Node3D Node { get; init; }
        public required List<CollisionObject3D> Bodies { get; init; }
        public System.Numerics.Vector3 Position;
        public float Orientation;
        public bool Here;
        public bool Placed;
    }

    /// <summary>The fastest a transport turns, in radians a second.</summary>
    private const float MaxTurnRate = 0.4f;

    private readonly Dictionary<ulong, Ride> _rides = [];
    private TileManager _tiles = null!;
    private IPlayer _player = null!;
    private Node3D _playerNode = null!;
    private ILookup<int, TaxiPathNodeRecord> _paths = null!;
    private GameObjectDisplayTable _displays = null!;
    private int _map;
    private double _clock;
    private RayCast3D? _ray;

    /// <summary>The transport the player stands on, or null.</summary>
    public ulong? Riding { get; private set; }

    /// <summary>How fast the deck under the player moves, in yards a second, which isn't the player walking.</summary>
    public Vector3 DeckVelocity { get; private set; }

    /// <summary>Once the server says the player arrived on a transport, where on it, in its own WoW axes.</summary>
    private (ulong Guid, System.Numerics.Vector3 Offset)? _boarding;

    public void Start(TileManager tiles, IPlayer player, Node3D playerNode, Formats.Assets.IAssetSource source, int map)
    {
        (_tiles, _player, _playerNode, _map) = (tiles, player, playerNode, map);
        Task.Run(() => (TaxiPathNodeRecord.ReadAll(source), GameObjectDisplayTable.Read(source)))
            .ContinueWith(read => Callable.From(() =>
            {
                if (IsInstanceValid(this) && read.IsCompletedSuccessfully)
                {
                    (_paths, _displays) = read.Result;
                    foreach (var waiting in _waiting)
                    {
                        Add(waiting);
                    }

                    _waiting.Clear();
                }
            }).CallDeferred());
    }

    private readonly List<GameObjectInfo> _waiting = [];

    /// <summary>The server shows a ship or zeppelin. Its model loads off the main thread.</summary>
    public void Add(GameObjectInfo info)
    {
        if (_paths is null)
        {
            _waiting.Add(info);
            return;
        }

        var nodes = _paths[(int)info.Data[0]].ToList();
        var path = TransportPath.Build(nodes, info.Data[1] > 0 ? info.Data[1] : 30f, info.Data[2] > 0 ? info.Data[2] : 1f);
        if (path is null || _displays.ModelOf((int)info.DisplayId) is not { } model)
        {
            GD.PushWarning($"Transport {info.Name} has no path ({info.Data[0]}) or model ({info.DisplayId})");
            return;
        }

        var startClock = _clock;
        Task.Run(() => _tiles.Models.GetWmo(model)).ContinueWith(built => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !built.IsCompletedSuccessfully || built.Result is not { } wmo)
            {
                return;
            }

            var node = new Node3D { Name = $"Transport_{info.Name}" };
            AddChild(node);
            var bodies = new List<CollisionObject3D>();
            foreach (var group in wmo.Groups)
            {
                if (group.Mesh is { } mesh)
                {
                    node.AddChild(new MeshInstance3D { Mesh = mesh });
                }

                if (group.Shape is { } shape)
                {
                    // Static, not animatable: Godot would carry a character on a moving platform as well, and the world re-centering would fling them.
                    var body = new StaticBody3D { CollisionLayer = TileManager.TerrainLayer };
                    body.AddChild(new CollisionShape3D { Shape = shape });
                    node.AddChild(body);
                    bodies.Add(body);
                }
            }

            AddDoodads(node, wmo);
            var ride = new Ride
            {
                Info = info,
                Path = path,
                StartProgress = info.PathProgress,
                StartClock = startClock,
                Node = node,
                Bodies = bodies,
            };
            _rides[info.Guid] = ride;
            Move(ride);
            GD.Print($"Transport {info.Name}: path {info.Data[0]} of {path.Period / 1000} s, now at ({ride.Position.X:F0}, {ride.Position.Y:F0}, {ride.Position.Z:F0}) on map {(ride.Here ? "here" : "elsewhere")}");
        }).CallDeferred());
    }

    /// <summary>The ship's own fittings: cannons, barrels, the zeppelin's balloon rigging.</summary>
    private void AddDoodads(Node3D node, WmoAsset wmo)
    {
        foreach (var doodad in wmo.Root.DoodadsInSet(0))
        {
            if (_tiles.Models.GetM2(doodad.Model) is not { } m2)
            {
                continue;
            }

            var t = doodad.Transform;
            static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
            node.AddChild(new MeshInstance3D
            {
                Mesh = m2.Mesh,
                Transform = new Transform3D(new Basis(V(t.AxisX), V(t.AxisY), V(t.AxisZ)), V(t.Origin)),
            });
        }
    }

    public void Remove(ulong guid)
    {
        if (_rides.Remove(guid, out var ride))
        {
            ride.Node.QueueFree();
            if (Riding == guid)
            {
                Riding = null;
            }
        }
    }

    public bool Has(ulong guid) => _rides.ContainsKey(guid);

    /// <summary>Each transport: its name, where it is in WoW coordinates, whether it's on this map, and whether it's standing at a dock.</summary>
    public IEnumerable<(ulong Guid, string Name, System.Numerics.Vector3 Position, bool Here, bool Docked)> All =>
        _rides.Values.Select(r =>
        {
            var progress = r.StartProgress + (ulong)Math.Max((_clock - r.StartClock) * 1000, 0);
            var docked = System.Numerics.Vector3.Distance(r.Path.At(progress).Position, r.Path.At(progress + 10000).Position) < 0.01f;
            return (r.Info.Guid, r.Info.Name, r.Position, r.Here, docked);
        });

    /// <summary>The player arrived on another map aboard a transport, at this offset on it.</summary>
    public void Board(ulong guid, System.Numerics.Vector3 offset) => _boarding = (guid, offset);

    /// <summary>Where the player stands on the transport they ride, in its own WoW axes, and the way they face on it.</summary>
    public (ulong Guid, System.Numerics.Vector3 Offset, float Orientation)? RideOffset(System.Numerics.Vector3 playerWow, float facing)
    {
        if (Riding is not { } guid || !_rides.TryGetValue(guid, out var ride))
        {
            return null;
        }

        var local = Rotate(playerWow - ride.Position, -ride.Orientation);
        return (guid, local, WowCoords.NormalizeOrientation(facing - ride.Orientation));
    }

    private static System.Numerics.Vector3 Rotate(System.Numerics.Vector3 v, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return new System.Numerics.Vector3((v.X * cos) - (v.Y * sin), (v.X * sin) + (v.Y * cos), v.Z);
    }

    public override void _PhysicsProcess(double delta)
    {
        _clock += delta;
        if (_tiles is null)
        {
            return;
        }

        DeckVelocity = Vector3.Zero;
        foreach (var ride in _rides.Values)
        {
            var (wasAt, wasFacing, wasHere) = (ride.Position, ride.Orientation, ride.Here);
            Move(ride);
            if (Riding == ride.Info.Guid && wasHere && ride.Here)
            {
                var feet = _player.Feet;
                Carry(wasAt, wasFacing, ride);
                DeckVelocity = delta > 0 ? (_player.Feet - feet) / (float)delta : Vector3.Zero;
            }
        }

        if (_boarding is { } boarding && _rides.TryGetValue(boarding.Guid, out var aboard) && aboard.Here)
        {
            // Put the player back where they stood on it, then let the ground placement find its deck.
            var at = aboard.Position + Rotate(boarding.Offset, aboard.Orientation);
            GD.Print($"Aboard {aboard.Info.Name} at ({at.X:F1}, {at.Y:F1}, {at.Z:F1})");
            _tiles.TeleportTo(at.X, at.Y, at.Z + 0.5f);
            _boarding = null;
            Riding = boarding.Guid;
        }

        Riding = StandingOn();
    }

    /// <summary>Moves a transport to where its cycle has it now, and hides it while it's on another map.</summary>
    private void Move(Ride ride)
    {
        var progress = ride.StartProgress + (ulong)Math.Max((_clock - ride.StartClock) * 1000, 0);
        var (map, position, orientation) = ride.Path.At(progress);
        var arrived = ride.Here != (map == _map);
        ride.Here = map == _map;
        ride.Position = position;

        // Ships and zeppelins turn slowly; a jump in heading would throw whoever stands on deck.
        if (float.IsFinite(orientation))
        {
            var turn = Mathf.AngleDifference(ride.Orientation, orientation);
            var limit = MaxTurnRate * (float)Math.Max(GetPhysicsProcessDeltaTime(), 1e-3);
            ride.Orientation = arrived || !ride.Placed ? orientation : ride.Orientation + Mathf.Clamp(turn, -limit, limit);
            ride.Placed = true;
        }
        if (ride.Node.Visible != ride.Here)
        {
            ride.Node.Visible = ride.Here;
            foreach (var body in ride.Bodies)
            {
                body.CollisionLayer = ride.Here ? TileManager.TerrainLayer : 0;
            }
        }

        if (!ride.Here)
        {
            return;
        }

        var godot = WowCoords.ToGodot(position);
        ride.Node.Position = _tiles.WorldToLocal(godot.X, godot.Y, godot.Z);
        ride.Node.Rotation = new Vector3(0, WowCoords.YawFromOrientation(orientation), 0);
    }

    /// <summary>
    /// Moves and turns the player with the deck under them. Worked out in WoW coordinates, which
    /// stay put when the world re-centers its origin under a travelling player.
    /// </summary>
    private void Carry(System.Numerics.Vector3 wasAt, float wasFacing, Ride ride)
    {
        var feet = _tiles.LocalToWow(_player.Feet);
        var onDeck = Rotate(feet - wasAt, -wasFacing);
        var now = ride.Position + Rotate(onDeck, ride.Orientation);
        var godot = WowCoords.ToGodot(now);
        _player.PlaceAt(_tiles.WorldToLocal(godot.X, godot.Y, godot.Z), keepVelocity: true);
        var turn = Mathf.AngleDifference(wasFacing, ride.Orientation);
        if (_playerNode is DesktopPlayer desktop && Mathf.Abs(turn) > 1e-6f)
        {
            desktop.RotateY(turn);
        }
    }

    /// <summary>The transport whose deck is just under the player's feet, if any.</summary>
    private ulong? StandingOn()
    {
        if (_rides.Count == 0 || !_playerNode.IsInsideTree())
        {
            return null;
        }

        if (_ray is null)
        {
            _ray = new RayCast3D { TopLevel = true, Enabled = false, CollisionMask = TileManager.TerrainLayer };
            AddChild(_ray);
        }

        var feet = _player.Feet;
        _ray.GlobalPosition = feet + (Vector3.Up * 0.5f);
        _ray.TargetPosition = Vector3.Down * (DeckReach + 0.5f);
        _ray.ForceRaycastUpdate();
        if (!_ray.IsColliding() || _ray.GetCollider() is not CollisionObject3D hit)
        {
            return null;
        }

        foreach (var (guid, ride) in _rides)
        {
            if (ride.Here && ride.Bodies.Contains(hit))
            {
                return guid;
            }
        }

        return null;
    }
}
