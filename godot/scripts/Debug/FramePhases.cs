using System;
using System.Diagnostics;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Splits a frame into Godot's phases by when its first and last nodes run: physics, then
/// process, then drawing. A pair of these goes in the tree, one first and one last.
/// </summary>
public partial class FramePhases : Node
{
    private static long _processEnd;
    private static long _physicsStart;
    private static TimeSpan _gcPause;
    private static bool _sawPhysics;

    public bool IsLast { get; init; }

    public override void _Ready()
    {
        ProcessPriority = IsLast ? int.MaxValue : int.MinValue;
        ProcessPhysicsPriority = int.MinValue;
        SetPhysicsProcess(!IsLast);
    }

    private static void Add(string name, long from, long to)
    {
        if (from != 0)
        {
            FrameProbe.AddTicks(name, to - from);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_sawPhysics)
        {
            _sawPhysics = true;
            _physicsStart = Stopwatch.GetTimestamp();
            Add("engine draw", _processEnd, _physicsStart);
        }
    }

    public override void _Process(double delta)
    {
        var now = Stopwatch.GetTimestamp();
        if (IsLast)
        {
            _processEnd = now;
            Add("all of process", ProcessStart, now);
            return;
        }

        if (_sawPhysics)
        {
            Add("engine physics", _physicsStart, now);
        }
        else
        {
            Add("engine draw", _processEnd, now);
        }

        _sawPhysics = false;
        ProcessStart = now;

        var pause = GC.GetTotalPauseDuration();
        if (pause > _gcPause)
        {
            FrameProbe.AddTicks("gc pause", (long)((pause - _gcPause).TotalSeconds * Stopwatch.Frequency));
        }

        _gcPause = pause;
    }

    private static long ProcessStart { get; set; }
}
