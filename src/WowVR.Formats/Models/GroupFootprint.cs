using System.Collections;
using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>
/// The ground a WMO group's geometry covers, seen from above, in the file's axes. A group's box
/// can reach well past its walls when a building stands at an angle, so this tells a point in
/// the room from one in the box's corner. It's cheap enough for every frame.
/// </summary>
public sealed class GroupFootprint
{
    /// <summary>Half a cell's diagonal is how far this reaches past the geometry, which has to stay under the portals' margin.</summary>
    private const float CellSize = 0.5f;
    private static readonly float Reach = CellSize * MathF.Sqrt(2f) / 2f;

    private readonly float _minX;
    private readonly float _minY;
    private readonly int _width;
    private readonly int _height;
    private readonly BitArray _cells;

    private GroupFootprint(Vector2 min, Vector2 max)
    {
        (_minX, _minY) = (min.X, min.Y);
        _width = Math.Max((int)MathF.Ceiling((max.X - min.X) / CellSize), 1);
        _height = Math.Max((int)MathF.Ceiling((max.Y - min.Y) / CellSize), 1);
        _cells = new BitArray(_width * _height);
    }

    /// <summary>From triangles in the file's axes, as (X, Y) from above.</summary>
    public static GroupFootprint Build(IReadOnlyList<(Vector2 A, Vector2 B, Vector2 C)> triangles, Vector2 min, Vector2 max)
    {
        var footprint = new GroupFootprint(min, max);
        foreach (var triangle in triangles)
        {
            footprint.Add(triangle.A, triangle.B, triangle.C);
        }

        return footprint;
    }

    /// <summary>From a group's surfaces, whose positions have Y up and Z as the file's -Y.</summary>
    public static GroupFootprint Build(IEnumerable<ModelSurface> surfaces, (Vector3 Min, Vector3 Max) fileBounds)
    {
        var triangles = new List<(Vector2, Vector2, Vector2)>();
        foreach (var surface in surfaces)
        {
            Vector2 Above(int index) => new(surface.Positions[index].X, -surface.Positions[index].Z);
            for (var i = 0; i + 2 < surface.Indices.Length; i += 3)
            {
                triangles.Add((Above(surface.Indices[i]), Above(surface.Indices[i + 1]), Above(surface.Indices[i + 2])));
            }
        }

        return Build(triangles, new Vector2(fileBounds.Min.X, fileBounds.Min.Y), new Vector2(fileBounds.Max.X, fileBounds.Max.Y));
    }

    public bool Covers(float x, float y)
    {
        var column = (int)MathF.Floor((x - _minX) / CellSize);
        var row = (int)MathF.Floor((y - _minY) / CellSize);
        return column >= 0 && row >= 0 && column < _width && row < _height && _cells[row * _width + column];
    }

    private void Add(Vector2 a, Vector2 b, Vector2 c)
    {
        var fromColumn = Math.Max((int)MathF.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - Reach - _minX) / CellSize), 0);
        var toColumn = Math.Min((int)MathF.Floor((Math.Max(a.X, Math.Max(b.X, c.X)) + Reach - _minX) / CellSize), _width - 1);
        var fromRow = Math.Max((int)MathF.Floor((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - Reach - _minY) / CellSize), 0);
        var toRow = Math.Min((int)MathF.Floor((Math.Max(a.Y, Math.Max(b.Y, c.Y)) + Reach - _minY) / CellSize), _height - 1);

        for (var row = fromRow; row <= toRow; row++)
        {
            for (var column = fromColumn; column <= toColumn; column++)
            {
                var center = new Vector2(_minX + (column + 0.5f) * CellSize, _minY + (row + 0.5f) * CellSize);
                if (IsNear(center, a, b, c))
                {
                    _cells[row * _width + column] = true;
                }
            }
        }
    }

    private static bool IsNear(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        var (ab, bc, ca) = (Side(a, b, point), Side(b, c, point), Side(c, a, point));
        var inside = (ab >= 0f && bc >= 0f && ca >= 0f) || (ab <= 0f && bc <= 0f && ca <= 0f);

        // A wall seen from above is a line, which still counts.
        return inside || DistanceToEdge(point, a, b) <= Reach || DistanceToEdge(point, b, c) <= Reach || DistanceToEdge(point, c, a) <= Reach;
    }

    private static float Side(Vector2 from, Vector2 to, Vector2 point) =>
        (to.X - from.X) * (point.Y - from.Y) - (to.Y - from.Y) * (point.X - from.X);

    private static float DistanceToEdge(Vector2 point, Vector2 from, Vector2 to)
    {
        var edge = to - from;
        var length = edge.LengthSquared();
        var along = length > 0f ? Math.Clamp(Vector2.Dot(point - from, edge) / length, 0f, 1f) : 0f;
        return Vector2.Distance(point, from + edge * along);
    }
}
