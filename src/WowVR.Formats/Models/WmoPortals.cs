using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>
/// Which of a WMO's groups can be seen from where the camera is. Groups connect through
/// portals: from the groups the camera starts in, each portal in view shows the group behind
/// it, but only through the part of the screen the portal covers. Parts of interiors that
/// poke out of the ground, such as Undercity's chains, stay hidden from outside this way.
/// </summary>
public sealed class WmoPortals
{
    private const uint FlagExterior = 0x8;
    private const uint FlagInterior = 0x2000;
    private const float InsideMargin = 0.5f;
    private const float PlaneMargin = 1f;

    /// <summary>
    /// Within this of an indoor group's box, the camera may be in the gaps between rooms that
    /// no box covers, where the outer walls can't be seen from inside. So that group and the
    /// outside both start, which shows too much rather than leaving holes.
    /// </summary>
    private const float NearMargin = 2f;

    /// <summary>How far past the screen's edge counts, as a share of it, for the frame's lag and a headset's other eye.</summary>
    private const float ScreenMargin = 0.2f;
    private const float NearClip = 0.05f;

    private readonly record struct Rect(float MinX, float MinY, float MaxX, float MaxY)
    {
        public bool IsEmpty => MinX >= MaxX || MinY >= MaxY;

        public Rect Intersect(in Rect o) => new(Math.Max(MinX, o.MinX), Math.Max(MinY, o.MinY), Math.Min(MaxX, o.MaxX), Math.Min(MaxY, o.MaxY));

        public bool Contains(in Rect o) => o.MinX >= MinX && o.MinY >= MinY && o.MaxX <= MaxX && o.MaxY <= MaxY;

        public Rect Union(in Rect o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    }

    private readonly record struct Link(int Other, Vector3[] Corners, Vector3 Normal, float Offset, int Side);

    private static readonly Rect Screen = new(-1f - ScreenMargin, -1f - ScreenMargin, 1f + ScreenMargin, 1f + ScreenMargin);

    private static readonly Rect Nothing = new(1f, 1f, 0f, 0f);

    private readonly Link[][] _links;
    private readonly (Vector3 Min, Vector3 Max)[] _bounds;
    private readonly bool[] _exterior;
    private readonly bool[] _interior;
    private readonly bool _hasExterior;
    private readonly Rect?[] _seen;
    private readonly Stack<(int Group, Rect View)> _stack = new();

    /// <param name="flags">Each group's MOGP flags.</param>
    /// <param name="bounds">Each group's box, in the same space as the portals.</param>
    /// <param name="vertices">MOPV.</param>
    /// <param name="portals">MOPT: the first vertex, how many, and the plane, where dot(normal, point) + offset is 0 on it.</param>
    /// <param name="references">MOPR: which portal, the group it leads to from the group that lists it, and the side it's seen from.</param>
    public WmoPortals(
        uint[] flags,
        (Vector3 Min, Vector3 Max)[] bounds,
        Vector3[] vertices,
        (int Start, int Count, Vector3 Normal, float Offset)[] portals,
        (int Group, int Portal, int Other, int Side)[] references)
    {
        _bounds = bounds;
        _exterior = flags.Select(f => (f & FlagExterior) != 0).ToArray();
        _interior = flags.Select(f => (f & FlagInterior) != 0 && (f & FlagExterior) == 0).ToArray();
        _hasExterior = _exterior.Any(e => e);
        _seen = new Rect?[flags.Length];

        var links = flags.Select(_ => new List<Link>()).ToArray();
        foreach (var (group, portal, other, side) in references)
        {
            if (group >= links.Length || other >= links.Length || portal >= portals.Length)
            {
                continue;
            }

            var (start, count, normal, offset) = portals[portal];
            if (start + count <= vertices.Length)
            {
                links[group].Add(new Link(other, vertices[start..(start + count)], normal, offset, side));
            }
        }

        _links = links.Select(l => l.ToArray()).ToArray();
    }

    public int GroupCount => _links.Length;

    /// <summary>
    /// Fills in which groups show. Allocates nothing, because it runs every frame.
    /// </summary>
    /// <param name="camera">The camera, in the same space as the portals.</param>
    /// <param name="toClip">A point in that space to clip space: X, Y and W.</param>
    public void Compute(Vector3 camera, Func<Vector3, (float X, float Y, float W)> toClip, bool[] visible)
    {
        Array.Clear(visible);
        Array.Clear(_seen);
        _stack.Clear();

        var inside = false;
        for (var i = 0; i < _bounds.Length; i++)
        {
            if (_interior[i] && Contains(_bounds[i], camera, InsideMargin))
            {
                inside = true;
                _stack.Push((i, Screen));
            }
        }

        if (!inside)
        {
            for (var i = 0; i < _bounds.Length; i++)
            {
                if (_interior[i] && Contains(_bounds[i], camera, NearMargin))
                {
                    _stack.Push((i, Screen));
                }
            }
        }

        if (!inside)
        {
            for (var i = 0; i < _bounds.Length; i++)
            {
                // With no outside to start from, nothing could ever show from out here.
                if (_exterior[i] || !_hasExterior)
                {
                    _stack.Push((i, Screen));
                }
            }
        }

        while (_stack.TryPop(out var item))
        {
            var (group, view) = item;
            if (_seen[group] is { } seen)
            {
                if (seen.Contains(view))
                {
                    continue;
                }

                view = view.Union(seen);
            }

            _seen[group] = view;
            visible[group] = true;

            foreach (var link in _links[group])
            {
                var distance = Vector3.Dot(link.Normal, camera) + link.Offset;
                if (link.Side != 0 && distance * link.Side < -PlaneMargin)
                {
                    continue;
                }

                var through = view.Intersect(Project(link.Corners, toClip));
                if (!through.IsEmpty)
                {
                    _stack.Push((link.Other, through));
                }
            }
        }
    }

    private static bool Contains(in (Vector3 Min, Vector3 Max) box, Vector3 point, float margin) =>
        point.X >= box.Min.X - margin && point.Y >= box.Min.Y - margin && point.Z >= box.Min.Z - margin
        && point.X <= box.Max.X + margin && point.Y <= box.Max.Y + margin && point.Z <= box.Max.Z + margin;

    /// <summary>The part of the screen a portal covers. One crossing the camera's plane could cover anything, and one wholly behind it nothing.</summary>
    private static Rect Project(Vector3[] corners, Func<Vector3, (float X, float Y, float W)> toClip)
    {
        var (minX, minY, maxX, maxY) = (float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
        var (ahead, behind) = (false, false);
        foreach (var corner in corners)
        {
            var (x, y, w) = toClip(corner);
            if (w <= NearClip)
            {
                behind = true;
                continue;
            }

            ahead = true;
            (x, y) = (x / w, y / w);
            (minX, minY, maxX, maxY) = (Math.Min(minX, x), Math.Min(minY, y), Math.Max(maxX, x), Math.Max(maxY, y));
        }

        if (behind)
        {
            return ahead ? Screen : Nothing;
        }

        return new Rect(minX - ScreenMargin, minY - ScreenMargin, maxX + ScreenMargin, maxY + ScreenMargin);
    }
}
