using System;
using System.Collections.Generic;
using System.Linq;
using AzerothVR.Formats;
using AzerothVR.World;
using Godot;

namespace AzerothVR.Debug;

/// <summary>
/// Flies the player from Goldshire to Northshire at running speed, then reports
/// frame times and quits. Run with: godot --path godot -- --tour
/// </summary>
public partial class WorldTour : Node
{
    private const float Speed = 7f;
    private const float FlightHeight = 110f;
    private const int TilesBeforeStart = 9;

    private static readonly System.Numerics.Vector3 Northshire = new(-8914f, -133f, FlightHeight);

    private readonly List<double> _frames = [];

    public required TileManager Tiles { get; init; }
    public required Node3D Player { get; init; }

    private bool _started;

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

        var wow = WowCoords.ToGodot(Northshire);
        var target = Tiles.WorldToLocal(wow.X, wow.Y, wow.Z);
        var offset = new Vector3(target.X - Player.GlobalPosition.X, 0, target.Z - Player.GlobalPosition.Z);
        if (offset.Length() < 5f)
        {
            Report();
            GetTree().Quit();
            return;
        }

        var position = Player.GlobalPosition + offset.Normalized() * Speed * (float)delta;
        position.Y = FlightHeight;
        Player.GlobalPosition = position;
    }

    private void Report()
    {
        var sorted = _frames.Order().ToList();
        GD.Print($"Tour finished: {sorted.Count} frames, {Tiles.LoadedTileCount} tiles loaded.");
        GD.Print($"Frame ms: median {sorted[sorted.Count / 2]:F1}, 99% {sorted[(int)(sorted.Count * 0.99)]:F1}, worst {sorted[^1]:F1}");
        GD.Print($"Frames over 11.1 ms: {sorted.Count(f => f > 11.1)}, over 22.2 ms: {sorted.Count(f => f > 22.2)}, over 50 ms: {sorted.Count(f => f > 50)}");
    }
}
