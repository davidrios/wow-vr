using System.Numerics;
using WowVR.Formats.Database;

namespace WowVR.Formats.World;

/// <summary>
/// Where a ship or zeppelin is along its taxi path at any moment of its cycle, worked out as
/// AzerothCore's transport manager does, since the server sends only how far into the cycle it
/// was: Catmull-Rom splines through the path's nodes, a stop at each node flagged 2 for its delay,
/// speeding up and slowing down around stops at the template's rate, and a jump to the next map
/// where the path changes map. Positions are WoW coordinates.
/// </summary>
public sealed class TransportPath
{
    /// <summary>The server's splines measure each segment with only this many steps.</summary>
    private const int StepsPerSegment = 3;

    private readonly List<Frame> _frames;
    private readonly float _speed;
    private readonly float _accel;
    private readonly float _accelTime;
    private readonly float _accelDistance;

    private sealed class Frame(TaxiPathNodeRecord node)
    {
        public TaxiPathNodeRecord Node { get; } = node;
        public bool Teleport { get; set; }
        public int Index { get; set; }
        public float DistFromPrev { get; set; }
        public float NextDistFromPrev { get; set; }
        public float DistSinceStop { get; set; }
        public float DistUntilStop { get; set; }
        public float TimeTo { get; set; }
        public float TimeFrom { get; set; }
        public uint ArriveTime { get; set; }
        public uint DepartureTime { get; set; }
        public uint NextArriveTime { get; set; }
        public Spline Spline { get; set; } = null!;

        public bool IsStop => Node.Flags == 2;
    }

    /// <summary>A Catmull-Rom spline with a copy of each end point outside it, as the server's.</summary>
    private sealed class Spline
    {
        private readonly Vector3[] _points;
        private readonly double[] _lengths;

        public Spline(IReadOnlyList<Vector3> controls)
        {
            _points = new Vector3[controls.Count + 2];
            for (var i = 0; i < controls.Count; i++)
            {
                _points[i + 1] = controls[i];
            }

            _points[0] = controls[0];
            _points[^1] = controls[^1];
            _lengths = new double[_points.Length];
            for (var i = 1; i < controls.Count; i++)
            {
                _lengths[i + 1] = _lengths[i] + SegmentLength(i);
            }
        }

        /// <summary>From control point <paramref name="from"/> to <paramref name="to"/>, counting from 1.</summary>
        public float Length(int from, int to) => (float)(_lengths[Math.Clamp(to, 0, _lengths.Length - 1)] - _lengths[Math.Clamp(from, 0, _lengths.Length - 1)]);

        private double SegmentLength(int index)
        {
            var current = _points[index];
            double length = 0;
            for (var i = 1; i <= StepsPerSegment; i++)
            {
                var next = Evaluate(index, (float)i / StepsPerSegment);
                length += Vector3.Distance(current, next);
                current = next;
            }

            return length;
        }

        /// <summary>The point a fraction <paramref name="t"/> along segment <paramref name="index"/>, between control points index and index + 1.</summary>
        public Vector3 Evaluate(int index, float t)
        {
            index = Math.Clamp(index, 1, _points.Length - 3);
            var (p0, p1, p2, p3) = (_points[index - 1], _points[index], _points[index + 1], _points[index + 2]);
            var t2 = t * t;
            var t3 = t2 * t;
            return 0.5f * ((2 * p1) + ((p2 - p0) * t) + (((2 * p0) - (5 * p1) + (4 * p2) - p3) * t2) + (((3 * p1) - p0 - (3 * p2) + p3) * t3));
        }

        public Vector3 Derivative(int index, float t)
        {
            index = Math.Clamp(index, 1, _points.Length - 3);
            var (p0, p1, p2, p3) = (_points[index - 1], _points[index], _points[index + 1], _points[index + 2]);
            var t2 = t * t;
            return 0.5f * ((p2 - p0) + (2 * ((2 * p0) - (5 * p1) + (4 * p2) - p3) * t) + (3 * ((3 * p1) - p0 - (3 * p2) + p3) * t2));
        }
    }

    private TransportPath(List<Frame> frames, float speed, float accel)
    {
        _frames = frames;
        _speed = speed;
        _accel = accel;
        _accelTime = speed / accel;
        _accelDistance = 0.5f * speed * speed / accel;
    }

    /// <summary>The length of a whole cycle, in milliseconds.</summary>
    public uint Period => _frames[^1].DepartureTime;

    /// <summary>The maps the path visits.</summary>
    public IEnumerable<int> Maps => _frames.Select(f => f.Node.Map).Distinct();

    /// <summary>Null when the path is too short to move along.</summary>
    public static TransportPath? Build(IReadOnlyList<TaxiPathNodeRecord> path, float speed, float accel)
    {
        if (path.Count < 4 || speed <= 0 || accel <= 0)
        {
            return null;
        }

        var frames = new List<Frame>();
        var splinePoints = new List<Vector3>();
        var mapChange = false;
        for (var i = 0; i < path.Count; i++)
        {
            if (mapChange)
            {
                mapChange = false;
                continue;
            }

            var node = path[i];
            if (i != path.Count - 1 && ((node.Flags & 1) != 0 || node.Map != path[i + 1].Map))
            {
                if (frames.Count > 0)
                {
                    frames[^1].Teleport = true;
                }

                mapChange = true;
            }
            else
            {
                frames.Add(new Frame(node));
                splinePoints.Add(node.Position);
            }
        }

        if (splinePoints.Count >= 2)
        {
            // The server's ends are the spline's extra control points, not frames.
            splinePoints.RemoveAt(0);
            frames.RemoveAt(0);
            splinePoints.RemoveAt(splinePoints.Count - 1);
            frames.RemoveAt(frames.Count - 1);
        }

        if (frames.Count < 2)
        {
            return null;
        }

        frames[^1].Teleport = true;
        var result = new TransportPath(frames, speed, accel);
        result.Time(splinePoints);
        return result;
    }

    private void Time(List<Vector3> splinePoints)
    {
        var frames = _frames;
        var firstStop = -1;
        var lastStop = -1;
        frames[0].DistFromPrev = 0;
        frames[0].Index = 1;
        if (frames[0].IsStop)
        {
            firstStop = lastStop = 0;
        }

        var start = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i - 1].Teleport || i + 1 == frames.Count)
            {
                var extra = frames[i - 1].Teleport ? 0 : 1;
                var spline = new Spline(splinePoints.GetRange(start, i - start + extra));
                for (var j = start; j < i + extra; j++)
                {
                    frames[j].Index = j - start + 1;
                    frames[j].DistFromPrev = spline.Length(j - start, j + 1 - start);
                    if (j > 0)
                    {
                        frames[j - 1].NextDistFromPrev = frames[j].DistFromPrev;
                    }

                    frames[j].Spline = spline;
                }

                if (frames[i - 1].Teleport)
                {
                    frames[i].Index = i - start + 1;
                    frames[i].DistFromPrev = 0;
                    frames[i - 1].NextDistFromPrev = 0;
                    frames[i].Spline = spline;
                }

                start = i;
            }

            if (frames[i].IsStop)
            {
                if (firstStop == -1)
                {
                    firstStop = i;
                }

                lastStop = i;
            }
        }

        frames[^1].NextDistFromPrev = frames[0].DistFromPrev;
        if (firstStop == -1 || lastStop == -1)
        {
            firstStop = lastStop = 0;
        }

        var distance = 0f;
        for (var i = 0; i < frames.Count; i++)
        {
            var j = (i + lastStop) % frames.Count;
            distance = frames[j].IsStop || j == lastStop ? 0 : distance + frames[j].DistFromPrev;
            frames[j].DistSinceStop = distance;
        }

        distance = 0;
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var j = (i + firstStop) % frames.Count;
            distance += frames[(j + 1) % frames.Count].DistFromPrev;
            frames[j].DistUntilStop = distance;
            if (frames[j].IsStop || j == firstStop)
            {
                distance = 0;
            }
        }

        foreach (var frame in frames)
        {
            var total = frame.DistSinceStop + frame.DistUntilStop;
            if (total < 2 * _accelDistance)
            {
                frame.TimeTo = frame.DistSinceStop < frame.DistUntilStop
                    ? (2f * MathF.Sqrt(total / _accel)) - MathF.Sqrt(2 * frame.DistSinceStop / _accel)
                    : MathF.Sqrt(2 * frame.DistUntilStop / _accel);
            }
            else if (frame.DistSinceStop < _accelDistance)
            {
                frame.TimeTo = (total / _speed) + (_speed / _accel) - MathF.Sqrt(2 * frame.DistSinceStop / _accel);
            }
            else if (frame.DistUntilStop < _accelDistance)
            {
                frame.TimeTo = MathF.Sqrt(2 * frame.DistUntilStop / _accel);
            }
            else
            {
                frame.TimeTo = (frame.DistUntilStop / _speed) + (0.5f * _speed / _accel);
            }
        }

        var segmentTime = 0f;
        for (var i = 0; i < frames.Count; i++)
        {
            var j = (i + lastStop) % frames.Count;
            if (frames[j].IsStop || j == lastStop)
            {
                segmentTime = frames[j].TimeTo;
            }

            frames[j].TimeFrom = segmentTime - frames[j].TimeTo;
        }

        frames[0].ArriveTime = 0;
        var pathTime = 0f;
        if (frames[0].IsStop)
        {
            pathTime = frames[0].Node.Delay;
            frames[0].DepartureTime = (uint)(pathTime * 1000);
        }

        for (var i = 1; i < frames.Count; i++)
        {
            pathTime += frames[i - 1].TimeTo;
            if (frames[i].IsStop)
            {
                frames[i].ArriveTime = (uint)(pathTime * 1000);
                frames[i - 1].NextArriveTime = frames[i].ArriveTime;
                pathTime += frames[i].Node.Delay;
                frames[i].DepartureTime = (uint)(pathTime * 1000);
            }
            else
            {
                pathTime -= frames[i].TimeTo;
                frames[i].ArriveTime = (uint)(pathTime * 1000);
                frames[i - 1].NextArriveTime = frames[i].ArriveTime;
                frames[i].DepartureTime = frames[i].ArriveTime;
            }
        }

        frames[^1].NextArriveTime = frames[^1].DepartureTime;
    }

    /// <summary>
    /// Where the transport is <paramref name="progress"/> milliseconds into its run, which wraps
    /// around the cycle: its map, position, and orientation in radians turning left from north.
    /// </summary>
    public (int Map, Vector3 Position, float Orientation) At(ulong progress)
    {
        var period = Math.Max(Period, 1u);
        var timer = (uint)(progress % period);

        // The frame it's at: stopped at it, or between leaving it and reaching the next.
        var index = _frames.Count - 1;
        for (var i = 0; i < _frames.Count; i++)
        {
            var frame = _frames[i];
            var next = i + 1 < _frames.Count ? _frames[i + 1].ArriveTime : period;
            if (timer >= frame.ArriveTime && timer < Math.Max(next, frame.DepartureTime))
            {
                index = i;
                break;
            }
        }

        var current = _frames[index];
        if (timer < current.DepartureTime || current.NextDistFromPrev <= 0)
        {
            return (current.Node.Map, current.Spline.Evaluate(current.Index, 0), Facing(current, 0));
        }

        var t = Math.Clamp(SegmentPosition(current, timer * 0.001f), 0f, 1f);
        return (current.Node.Map, current.Spline.Evaluate(current.Index, t), Facing(current, t));
    }

    /// <summary>
    /// Which way the path heads at a point, plus a half turn as the server has it, or NaN where it
    /// doesn't head anywhere, as at a dock whose path lists the same point twice.
    /// </summary>
    private float Facing(Frame frame, float t)
    {
        // Along the segment first, then the one leaving it, for a stop whose own segment has no length.
        for (var step = 0; step <= 4; step++)
        {
            var direction = frame.Spline.Derivative(frame.Index, Math.Min(t + (step * 0.25f), 1f));
            if (new Vector2(direction.X, direction.Y).LengthSquared() > 1e-4f)
            {
                return MathF.Atan2(direction.Y, direction.X) + MathF.PI;
            }
        }

        var index = _frames.IndexOf(frame);
        for (var i = 1; i < _frames.Count; i++)
        {
            var other = _frames[(index - i + _frames.Count) % _frames.Count];
            var direction = other.Spline.Derivative(other.Index, 1f);
            if (other.Node.Map == frame.Node.Map && new Vector2(direction.X, direction.Y).LengthSquared() > 1e-4f)
            {
                return MathF.Atan2(direction.Y, direction.X) + MathF.PI;
            }
        }

        return float.NaN;
    }

    /// <summary>How far along its frame's segment the transport is, counted from the nearest stop as the server does.</summary>
    private float SegmentPosition(Frame frame, float now)
    {
        var sinceStop = frame.TimeFrom + (now - (frame.DepartureTime * 0.001f));
        var untilStop = frame.TimeTo - (now - (frame.DepartureTime * 0.001f));
        float position;
        if (sinceStop < untilStop)
        {
            var distance = sinceStop < _accelTime ? 0.5f * _accel * sinceStop * sinceStop : _accelDistance + ((sinceStop - _accelTime) * _speed);
            position = distance - frame.DistSinceStop;
        }
        else
        {
            var distance = untilStop < _accelTime ? 0.5f * _accel * untilStop * untilStop : _accelDistance + ((untilStop - _accelTime) * _speed);
            position = frame.DistUntilStop - distance;
        }

        return position / frame.NextDistFromPrev;
    }
}
