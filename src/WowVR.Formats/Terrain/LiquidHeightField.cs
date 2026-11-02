using System.Numerics;

namespace WowVR.Formats.Terrain;

/// <summary>Finds the height of a WMO group's liquid at a point, in the same axes as its surface, which have Y up.</summary>
public sealed class LiquidHeightField
{
    private const float CellSize = LiquidSurface.CellSize;

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);

    private readonly Dictionary<(int X, int Z), List<Triangle>> _cells = [];

    public LiquidHeightField(LiquidSurface surface)
    {
        var (positions, indices) = (surface.Positions, surface.Indices);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var triangle = new Triangle(positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
            var (minX, maxX) = (CellOf(Math.Min(triangle.A.X, Math.Min(triangle.B.X, triangle.C.X))), CellOf(Math.Max(triangle.A.X, Math.Max(triangle.B.X, triangle.C.X))));
            var (minZ, maxZ) = (CellOf(Math.Min(triangle.A.Z, Math.Min(triangle.B.Z, triangle.C.Z))), CellOf(Math.Max(triangle.A.Z, Math.Max(triangle.B.Z, triangle.C.Z))));
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

    /// <summary>The surface's height over a point, or null if there's no liquid there.</summary>
    public float? HeightAt(float x, float z)
    {
        if (!_cells.TryGetValue((CellOf(x), CellOf(z)), out var triangles))
        {
            return null;
        }

        foreach (var t in triangles)
        {
            var denominator = (t.B.Z - t.C.Z) * (t.A.X - t.C.X) + (t.C.X - t.B.X) * (t.A.Z - t.C.Z);
            if (denominator == 0f)
            {
                continue;
            }

            var a = ((t.B.Z - t.C.Z) * (x - t.C.X) + (t.C.X - t.B.X) * (z - t.C.Z)) / denominator;
            var b = ((t.C.Z - t.A.Z) * (x - t.C.X) + (t.A.X - t.C.X) * (z - t.C.Z)) / denominator;
            var c = 1f - a - b;
            if (a >= 0f && b >= 0f && c >= 0f)
            {
                return a * t.A.Y + b * t.B.Y + c * t.C.Y;
            }
        }

        return null;
    }
}
