using System.Numerics;

namespace WowVR.Net;

/// <summary>
/// A path the server has a unit follow, in WoW coordinates: straight lines between its points at
/// a steady speed, taking <see cref="Duration"/> seconds in all. A path of one point stands still.
/// </summary>
public sealed class MovePath
{
    private readonly Vector3[] _points;
    private readonly float[] _distances;

    /// <param name="facing">Where it faces at the end, in radians turning left from north, if the server says.</param>
    /// <param name="cyclic">Starts over at the end, as flying patrols do.</param>
    /// <param name="holdFacing">Faces <paramref name="facing"/> all along, as a player strafing or walking backwards does.</param>
    public MovePath(IReadOnlyList<Vector3> points, double duration, float? facing = null, bool cyclic = false, bool holdFacing = false)
    {
        HoldsFacing = holdFacing && facing is not null;
        // The server's own copies of the end points, which keep its splines' maths simple, would make zero-length legs.
        var kept = new List<Vector3>(points.Count);
        foreach (var point in points)
        {
            if (kept.Count == 0 || kept[^1] != point)
            {
                kept.Add(point);
            }
        }

        if (kept.Count == 0)
        {
            throw new ArgumentException("A path needs a point.", nameof(points));
        }

        _points = [.. kept];
        _distances = new float[_points.Length];
        for (var i = 1; i < _points.Length; i++)
        {
            _distances[i] = _distances[i - 1] + Vector3.Distance(_points[i - 1], _points[i]);
        }

        Duration = Math.Max(duration, 0);
        Facing = facing;
        Cyclic = cyclic && _points.Length > 1;
    }

    public double Duration { get; }

    public float? Facing { get; }

    public bool Cyclic { get; }

    public bool HoldsFacing { get; }

    public IReadOnlyList<Vector3> Points => _points;

    public Vector3 End => _points[^1];

    public float Length => _distances[^1];

    /// <summary>Yards per second along the path, or 0 for one that stands.</summary>
    public float Speed => Duration > 0 ? (float)(Length / Duration) : 0f;

    /// <summary>A unit standing at a point.</summary>
    public static MovePath Standing(Vector3 at, float facing) => new([at], 0, facing);

    /// <summary>Where it is this many seconds after setting off, which way it faces, and whether it's still going.</summary>
    public (Vector3 Position, float Facing, bool Moving) At(double seconds)
    {
        if (_points.Length == 1 || Duration <= 0 || Length <= 0)
        {
            return (_points[^1], Facing ?? 0f, false);
        }

        if (Cyclic)
        {
            seconds -= Math.Floor(seconds / Duration) * Duration;
        }
        else if (seconds >= Duration)
        {
            return (_points[^1], Facing ?? FacingAlong(_points[^2], _points[^1]), false);
        }

        var distance = (float)(Math.Max(seconds, 0) / Duration) * Length;
        var leg = 1;
        while (leg < _points.Length - 1 && _distances[leg] < distance)
        {
            leg++;
        }

        var from = _points[leg - 1];
        var to = _points[leg];
        var span = _distances[leg] - _distances[leg - 1];
        var t = span > 0 ? (distance - _distances[leg - 1]) / span : 1f;
        return (Vector3.Lerp(from, to, t), HoldsFacing ? Facing!.Value : FacingAlong(from, to), true);
    }

    /// <summary>WoW's Y runs west, so the angle from X toward Y turns left from north.</summary>
    private static float FacingAlong(Vector3 from, Vector3 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);
}
