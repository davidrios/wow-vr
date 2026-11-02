using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>
/// Finds the baked light on a WMO group's floor at a point, from the vertex colors of the drawn
/// triangle there. Collision triangles aren't drawn and have no colors, so they can't say.
/// Positions are in the same axes as the surfaces, which have Y up.
/// </summary>
public sealed class WmoFloorLight
{
    private const float CellSize = 4f;

    /// <summary>A surface steeper than this isn't something to stand on. The value is the normal's Y, about 72 degrees from flat.</summary>
    private const float MinUpness = 0.3f;

    /// <summary>A floor further than this above or below the point belongs to another storey.</summary>
    private const float MaxGap = 2f;

    /// <summary>How much of a floor's color a creature keeps, and the brightness it's kept within. Picked by eye.</summary>
    private const float CreatureSaturation = 0.3f;
    private const float CreatureMinBrightness = 0.85f;
    private const float CreatureMaxBrightness = 1f;
    private const float DoodadSaturation = 0.6f;
    private const float DoodadMinBrightness = 0.35f;
    private const float DoodadMaxBrightness = 1f;

    /// <summary>
    /// Floors are baked with strong colors that suit wood and stone, but turn a face orange
    /// beside a hearth. A creature takes the floor's brightness and only a hint of its color.
    /// </summary>
    public static Vector3 ForCreature(Vector3 floor) => Limit(floor, CreatureSaturation, CreatureMinBrightness, CreatureMaxBrightness);

    /// <summary>
    /// A doodad's light, from the WMO's ambient and its own color. Those can add up to black, or
    /// past white, or to a strong color that a whole model shows more than a floor does. Doodads
    /// keep more of it than creatures, because their colors are often what warms a room.
    /// </summary>
    public static Vector3 ForDoodad(Vector3 light) => Limit(light, DoodadSaturation, DoodadMinBrightness, DoodadMaxBrightness);

    private static Vector3 Limit(Vector3 light, float saturation, float minBrightness, float maxBrightness)
    {
        var brightness = Vector3.Dot(light, new Vector3(0.299f, 0.587f, 0.114f));
        var color = Vector3.Lerp(new Vector3(brightness), light, saturation);
        var wanted = Math.Clamp(brightness, minBrightness, maxBrightness);
        return brightness > 0f ? Vector3.Min(color * (wanted / brightness), Vector3.One) : new Vector3(wanted);
    }

    private readonly record struct Corner(float X, float Y, float Z, Vector3 Color);

    private readonly record struct Triangle(Corner A, Corner B, Corner C);

    private readonly Dictionary<(int X, int Z), List<Triangle>> _cells = [];

    /// <summary>Null when no walkable surface has colors.</summary>
    public static WmoFloorLight? Build(IReadOnlyList<ModelSurface> surfaces)
    {
        var light = new WmoFloorLight();
        foreach (var surface in surfaces)
        {
            if (surface.Colors is { } colors)
            {
                light.Add(surface, colors);
            }
        }

        return light._cells.Count > 0 ? light : null;
    }

    private void Add(ModelSurface surface, byte[] colors)
    {
        Corner CornerOf(int vertex)
        {
            var p = surface.Positions[vertex];
            var color = new Vector3(colors[vertex * 4], colors[vertex * 4 + 1], colors[vertex * 4 + 2]) / 255f;
            return new Corner(p.X, p.Y, p.Z, color);
        }

        var indices = surface.Indices;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = (surface.Positions[indices[i]], surface.Positions[indices[i + 1]], surface.Positions[indices[i + 2]]);
            var normal = Vector3.Cross(b - a, c - a);
            var length = normal.Length();
            if (length <= 0f || Math.Abs(normal.Y) / length < MinUpness)
            {
                continue;
            }

            var triangle = new Triangle(CornerOf(indices[i]), CornerOf(indices[i + 1]), CornerOf(indices[i + 2]));
            var (minX, maxX) = (CellOf(Math.Min(a.X, Math.Min(b.X, c.X))), CellOf(Math.Max(a.X, Math.Max(b.X, c.X))));
            var (minZ, maxZ) = (CellOf(Math.Min(a.Z, Math.Min(b.Z, c.Z))), CellOf(Math.Max(a.Z, Math.Max(b.Z, c.Z))));
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list))
                    {
                        _cells[(x, z)] = list = [];
                    }

                    list.Add(triangle);
                }
            }
        }
    }

    private static int CellOf(float value) => (int)MathF.Floor(value / CellSize);

    /// <summary>Red, green and blue from 0 to 1 on the nearest floor at this height, or null if there's none.</summary>
    public Vector3? Sample(Vector3 point)
    {
        if (!_cells.TryGetValue((CellOf(point.X), CellOf(point.Z)), out var triangles))
        {
            return null;
        }

        Vector3? best = null;
        var bestGap = MaxGap;
        foreach (var t in triangles)
        {
            var denominator = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
            if (denominator == 0f)
            {
                continue;
            }

            var a = ((t.B.Z - t.C.Z) * (point.X - t.C.X) + (t.C.X - t.B.X) * (point.Z - t.C.Z)) / denominator;
            var b = ((t.C.Z - t.A.Z) * (point.X - t.C.X) + (t.A.X - t.C.X) * (point.Z - t.C.Z)) / denominator;
            var c = 1f - a - b;
            if (a < 0f || b < 0f || c < 0f)
            {
                continue;
            }

            var gap = Math.Abs(a * t.A.Y + b * t.B.Y + c * t.C.Y - point.Y);
            if (gap <= bestGap)
            {
                bestGap = gap;
                best = a * t.A.Color + b * t.B.Color + c * t.C.Color;
            }
        }

        return best;
    }
}
