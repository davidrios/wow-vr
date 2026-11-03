using System;
using WowVR.Desktop;
using WowVR.Formats;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>A creature following a path the server gave, from the moment on the creatures' clock it set off.</summary>
public sealed class ServerMotion(MovePath path, double startClock) : ICreatureMotion
{
    public float MoveSpeed => path.Speed > 0f ? path.Speed : CreatureMotion.WalkSpeed;

    public CreatureMotionState At(double clock)
    {
        var (position, facing, moving) = path.At(clock - startClock);
        return new CreatureMotionState(position.X, position.Y, position.Z, facing, moving);
    }
}

/// <summary>
/// Plays on an AzerothCore server: the creatures it shows around the player come and go, and
/// follow its paths, and the player's position goes back to it so it shows what's near them.
/// See <c>docs/decisions/0013-azerothcore-server.md</c>.
/// </summary>
public partial class ServerWorld : Node
{
    /// <summary>The server is told where the player is this often while they move, and at least every few seconds.</summary>
    private const double MovingSendSeconds = 0.25;
    private const double StillSendSeconds = 5;
    private const float MovedYards = 0.5f;

    /// <summary>A crowd arriving at once is spread over frames.</summary>
    private const int MaxEventsPerFrame = 200;

    private RealmSession _session = null!;
    private CreatureManager _creatures = null!;
    private TileManager _tiles = null!;
    private CreatureDisplayTable _displays = null!;
    private Node3D _player = null!;
    /// <summary>How often the log says what the server shows.</summary>
    private const double ReportSeconds = 10;

    private double _sinceSent;
    private double _sinceReport = ReportSeconds - 3;
    private int _moves;
    private System.Numerics.Vector3 _lastSent;

    public int Creatures { get; private set; }

    public void Start(RealmSession session, CreatureManager creatures, TileManager tiles, CreatureDisplayTable displays, Node3D player)
    {
        _session = session;
        _creatures = creatures;
        _tiles = tiles;
        _displays = displays;
        _player = player;
        _lastSent = session.StartPosition;
    }

    public override void _Process(double delta)
    {
        if (_session is null)
        {
            return;
        }

        for (var i = 0; i < MaxEventsPerFrame && _session.Events.TryDequeue(out var e); i++)
        {
            Apply(e);
        }

        SendPosition(delta);

        _sinceReport += delta;
        if (_sinceReport >= ReportSeconds)
        {
            _sinceReport = 0;
            var (_, x, y, z) = _tiles.PlayerLocation;
            GD.Print($"Server: {Creatures} creatures in view, {_creatures.NodeCount} placed, {_moves} moves since the last report; player at ({x:F0}, {y:F0}, {z:F0}); nearest {_creatures.NearestLive}");
            _moves = 0;
        }
    }

    public override void _ExitTree() => _session?.Dispose();

    private void Apply(SessionEvent e)
    {
        switch (e)
        {
            case UnitAppeared { Unit: var unit }:
                _creatures.AddLive(ToSpawn(unit), new ServerMotion(unit.Path, _creatures.Clock - unit.PathElapsed));
                Creatures++;
                break;
            case UnitMoved moved:
                _creatures.SetLiveMotion((long)moved.Guid, new ServerMotion(moved.Path, _creatures.Clock - moved.PathElapsed));
                _moves++;
                break;
            case UnitGone gone:
                _creatures.RemoveLive((long)gone.Guid);
                Creatures--;
                break;
            case SessionClosed closed:
                GD.PushWarning($"The server closed the session: {closed.Reason}. Creatures stay as they were.");
                break;
        }
    }

    private CreatureSpawn ToSpawn(UnitInfo unit)
    {
        var (position, facing, _) = unit.Path.At(unit.PathElapsed);
        return new CreatureSpawn
        {
            Guid = (long)unit.Guid,
            Entry = (int)unit.Entry,
            Name = unit.Name,
            DisplayId = (int)unit.DisplayId,
            Pos = [position.X, position.Y, position.Z],
            Orientation = facing,
            Scale = unit.Scale,
            WalkSpeed = unit.Path.Speed > 0f ? unit.Path.Speed : CreatureMotion.WalkSpeed,
            Weapons = [_displays.ItemDisplayOf((int)unit.Items[0]), _displays.ItemDisplayOf((int)unit.Items[1])],
        };
    }

    private void SendPosition(double delta)
    {
        _sinceSent += delta;
        if (_tiles.PlacingPlayer || !_player.IsInsideTree())
        {
            return;
        }

        var (_, x, y, z) = _tiles.PlayerLocation;
        var here = new System.Numerics.Vector3((float)x, (float)y, z);
        var moved = System.Numerics.Vector3.Distance(here, _lastSent) > MovedYards;
        if ((moved && _sinceSent >= MovingSendSeconds) || _sinceSent >= StillSendSeconds)
        {
            _sinceSent = 0;
            _lastSent = here;

            // Only the desktop player turns its body.
            var yaw = _player is DesktopPlayer ? _player.Rotation.Y : 0f;
            _session.SendPosition(here, WowCoords.OrientationFromCameraYaw(yaw));
        }
    }
}
