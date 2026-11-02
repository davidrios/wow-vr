using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Desktop;
using WowVR.Formats;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Takes the player from Goldshire to Northshire at running speed, then reports frame
/// times and quits. <c>--tour</c> flies over the terrain; <c>--tour=walk</c> walks on it,
/// through physics, on desktop.
/// </summary>
public partial class WorldTour : Node
{
    private const float Speed = 7f;
    private const float FlightHeight = 110f;
    private const int TilesBeforeStart = 9;
    private const double StuckSeconds = 10;
    private const float WalkArrivalDistance = 15f;

    private static readonly System.Numerics.Vector3 Northshire = new(-8914f, -133f, FlightHeight);

    /// <summary>Walking goes straight through the gate, because the valley's walls stop a direct line.</summary>
    private static readonly System.Numerics.Vector3[] WalkRoute =
    [
        new(-9090f, -44f, FlightHeight),
        new(-9030f, -44f, FlightHeight),
    ];

    private readonly List<double> _frames = [];

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }
    public bool Walk { get; init; }

    private bool _started;
    private int _waypoint;
    private double _sinceProgress;
    private float _closest = float.MaxValue;

    public override void _Process(double delta)
    {
        if (!_started)
        {
            _started = Tiles.LoadedTileCount >= TilesBeforeStart;
            if (_started)
            {
                GD.Print("Tour started.");
            }

            return;
        }

        _frames.Add(delta * 1000);

        var wow = WowCoords.ToGodot(Walk && _waypoint < WalkRoute.Length ? WalkRoute[_waypoint] : Northshire);
        var target = Tiles.WorldToLocal(wow.X, wow.Y, wow.Z);
        var feet = Player.Feet;
        var offset = new Vector3(target.X - feet.X, 0, target.Z - feet.Z);
        if (Walk && _waypoint < WalkRoute.Length && offset.Length() < 5f)
        {
            _waypoint++;
            _closest = float.MaxValue;
            _sinceProgress = 0;
            return;
        }

        // The marker itself is beside a wall, which walking can't always get past.
        if (offset.Length() < (Walk ? WalkArrivalDistance : 5f))
        {
            Report();
            GetTree().Quit();
            return;
        }

        if (Walk)
        {
            WalkTowards(offset, delta);
            return;
        }

        var position = feet + offset.Normalized() * Speed * (float)delta;
        position.Y = FlightHeight;
        Player.PlaceAt(position);
    }

    private void WalkTowards(Vector3 offset, double delta)
    {
        ((DesktopPlayer)Player).ScriptedWalk = offset.Normalized();

        _sinceProgress += delta;
        if (offset.Length() < _closest - 1f)
        {
            _closest = offset.Length();
            _sinceProgress = 0;
        }

        if (_sinceProgress > StuckSeconds)
        {
            GD.PrintErr($"Tour stuck {_closest:F0} yards from the end, at {Player.Feet}.");
            Report();
            GetTree().Quit(1);
        }
    }

    private void Report()
    {
        var sorted = _frames.Order().ToList();
        GD.Print($"Tour finished: {sorted.Count} frames, {Tiles.LoadedTileCount} tiles loaded.");
        GD.Print($"Frame ms: median {sorted[sorted.Count / 2]:F1}, 99% {sorted[(int)(sorted.Count * 0.99)]:F1}, worst {sorted[^1]:F1}");
        GD.Print($"Frames over 11.1 ms: {sorted.Count(f => f > 11.1)}, over 22.2 ms: {sorted.Count(f => f > 22.2)}, over 50 ms: {sorted.Count(f => f > 50)}");
    }
}
