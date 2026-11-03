using System.Numerics;

namespace WowVR.Formats.World;

/// <summary>
/// The client's clouds, which are made rather than read from a file: 3D value noise, with time as
/// the third axis, so they change shape without drifting. See formats.md. Safe to run on any thread,
/// but only one call at a time.
/// </summary>
public sealed class SkyClouds
{
    public const int Size = 256;

    /// <summary>Noise units per second along the time axis, where a lattice cell is 256.</summary>
    public const float UnitsPerSecond = 2f;

    private const int CellSize = 256;
    private const int Octaves = 4;

    /// <summary>Noise units per texel in the first octave, each doubling, for this <see cref="Size"/>.</summary>
    private const int FirstStep = 8;

    /// <summary>Only this many octaves shape the surface the light falls on.</summary>
    private const int ShapeOctaves = 3;

    private const float NoiseScale = 64f;
    private const float NoiseMiddle = 128f;
    private const float Sharpness = 0.96f;

    /// <summary>How far above the texture the light is, in texels. The client's is 64 at its size of 128.</summary>
    private const float LightHeight = 64f * Size / 128;

    private readonly byte[] _permutation = new byte[CellSize * 2 + 2];
    private readonly float[] _values = new float[CellSize];
    private readonly float[] _fade = new float[CellSize];
    private readonly byte[] _density = new byte[CellSize];
    private readonly float[] _shapeAbove = new float[Size];

    public SkyClouds(int seed = 12340)
    {
        var random = new Random(seed);
        var order = Enumerable.Range(0, CellSize).Select(i => (byte)i).ToArray();
        random.Shuffle(order);
        for (var i = 0; i < _permutation.Length; i++)
        {
            _permutation[i] = order[i % CellSize];
        }

        for (var i = 0; i < CellSize; i++)
        {
            _values[i] = random.NextSingle() * 2f - 1f;
            _fade[i] = (1f - MathF.Cos(i * MathF.PI / CellSize)) * 0.5f;
            _density[i] = (byte)MathF.Round(255f - 255f * MathF.Pow(Sharpness, i));
        }
    }

    /// <summary>What decides a frame's look. Colors are in gamma space, from 0 to 1.</summary>
    public readonly record struct Look(float Glow, Vector3 SunLit, Vector3 Shaded, Vector3 Ambient, Vector2 Light);

    /// <summary>Where a light shows in the texture: the middle is straight up, and the edge is 45 degrees from it.</summary>
    public static Vector2 LightPosition(Vector3 toLight)
    {
        var flat = new Vector2(toLight.X, toLight.Z);
        var length = flat.Length();
        if (length < 1e-4f)
        {
            return new Vector2(0.5f);
        }

        var fromTop = MathF.Min(MathF.Atan2(length, toLight.Y), MathF.PI / 4f);
        return new Vector2(0.5f) + flat / length * (fromTop / (MathF.PI / 4f) * 0.5f);
    }

    /// <summary>Fills RGBA bytes, row by row, at a moment in noise units.</summary>
    public void Render(byte[] into, float time, in Look look)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, Size * Size * 4);
        var skipped = (1f - look.Glow) * 255f;
        var lightX = look.Light.X * Size;
        var lightY = look.Light.Y * Size;
        var slopeScale = (float)Size / 128;
        Array.Clear(_shapeAbove);
        var at = 0;
        for (var y = 0; y < Size; y++)
        {
            var shapeBefore = 0f;
            for (var x = 0; x < Size; x++, at += 4)
            {
                var noise = 0f;
                var shape = 0f;
                var step = FirstStep;
                var amplitude = 1f;
                for (var octave = 0; octave < Octaves; octave++)
                {
                    noise += Lattice(x * step, y * step, time) * amplitude;
                    if (octave == ShapeOctaves - 1)
                    {
                        shape = noise;
                    }

                    step *= 2;
                    amplitude *= 0.5f;
                }

                var slopeX = x == 0 ? 0f : (shapeBefore - shape) * slopeScale;
                var slopeY = y == 0 ? 0f : (_shapeAbove[x] - shape) * slopeScale;
                shapeBefore = shape;
                _shapeAbove[x] = shape;

                var thickness = (int)MathF.Round(noise * NoiseScale + NoiseMiddle) - (int)skipped;
                var alpha = thickness < 0 ? (byte)0 : _density[Math.Min(thickness, CellSize - 1)];
                if (alpha == 0)
                {
                    // Keeps the neighbor's color, so filtering doesn't fringe a cloud's edge.
                    if (x > 0)
                    {
                        into[at] = into[at - 4];
                        into[at + 1] = into[at - 3];
                        into[at + 2] = into[at - 2];
                    }

                    into[at + 3] = 0;
                    continue;
                }

                var color = look.Shaded * (((255 - alpha) >> 1) + 64) / 255f + look.Ambient;
                var toLight = new Vector3(lightX - x, lightY - y, LightHeight);
                var facing = Vector3.Dot(Vector3.Normalize(new Vector3(slopeX, slopeY, 1f)), Vector3.Normalize(toLight));
                if (facing > 0f)
                {
                    color += look.SunLit * facing;
                }

                into[at] = ToByte(color.X);
                into[at + 1] = ToByte(color.Y);
                into[at + 2] = ToByte(color.Z);
                into[at + 3] = alpha;
            }
        }
    }

    private static byte ToByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private float Lattice(int x, int y, float time)
    {
        var (cellX, cellY) = ((x >> 8) & 0xFF, (y >> 8) & 0xFF);
        var cellZ = (int)MathF.Floor(time / CellSize);
        var fadeX = _fade[x & 0xFF];
        var fadeY = _fade[y & 0xFF];
        var fadeZ = _fade[(int)(time - cellZ * CellSize) & 0xFF];
        cellZ &= 0xFF;

        var (near, far) = (_permutation[cellZ], _permutation[cellZ + 1]);
        var front = Mix(Row(near + cellY, cellX, fadeX), Row(near + cellY + 1, cellX, fadeX), fadeY);
        var back = Mix(Row(far + cellY, cellX, fadeX), Row(far + cellY + 1, cellX, fadeX), fadeY);
        return Mix(front, back, fadeZ);
    }

    private float Row(int y, int cellX, float fadeX)
    {
        var row = _permutation[y & 0xFF];
        return Mix(_values[_permutation[row + cellX]], _values[_permutation[row + cellX + 1]], fadeX);
    }

    private static float Mix(float a, float b, float amount) => a + (b - a) * amount;
}
