using System.Numerics;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;

namespace WowVR.Formats.World;

/// <summary>The rows of LightIntBand that a light's parameters use, one per color.</summary>
public enum SkyBand
{
    Diffuse = 0,
    Ambient = 1,
    SkyTop = 2,
    SkyMiddle = 3,
    SkyLow = 4,
    SkyAboveHorizon = 5,
    SkyHorizon = 6,
    Fog = 7,
    Sun = 9,
    SunHalo = 10,
    Cloud = 12,
    OceanShallow = 14,
    OceanDeep = 15,
    RiverShallow = 16,
    RiverDeep = 17,
}

/// <summary>The sky at one place and time. Colors are red, green, blue from 0 to 1, in gamma space.</summary>
public sealed class SkyState
{
    public Vector3[] Colors { get; } = new Vector3[LightTable.BandsPerParams];

    /// <summary>Where fog becomes solid, in yards.</summary>
    public float FogEnd { get; set; }

    /// <summary>Where fog starts, as a fraction of <see cref="FogEnd"/>.</summary>
    public float FogStart { get; set; }

    public float RiverShallowAlpha { get; set; }
    public float RiverDeepAlpha { get; set; }
    public float OceanShallowAlpha { get; set; }
    public float OceanDeepAlpha { get; set; }

    public Vector3 this[SkyBand band] => Colors[(int)band];
}

/// <summary>
/// Sky colors from Light, LightParams, LightIntBand and LightFloatBand. Each map has a global
/// light and smaller lights that blend in around their positions, which are stored 36 times
/// larger than yards, as east, up and south from the map's north-west corner.
/// </summary>
public sealed class LightTable
{
    public const int BandsPerParams = 18;
    public const int FloatBandsPerParams = 6;

    /// <summary>Bands store time in half minutes.</summary>
    public const int TicksPerDay = 2880;

    private const int MaxBandEntries = 16;
    private const float UnitsPerYard = 36f;

    private const int FogEndFloatBand = 0;
    private const int FogStartFloatBand = 1;

    private readonly record struct Curve(int[] Times, Vector3[] Colors);

    private readonly record struct FloatCurve(int[] Times, float[] Values);

    private sealed record Params(Curve[] Bands, FloatCurve[] Floats, float RiverShallow, float RiverDeep, float OceanShallow, float OceanDeep);

    private sealed record Light(Vector3 Position, float Inner, float Outer, Params Clear)
    {
        public bool Global => Position == Vector3.Zero;
    }

    private readonly Dictionary<int, List<Light>> _byMap = [];

    private LightTable()
    {
    }

    public static LightTable Read(IAssetSource source)
    {
        var table = new LightTable();
        var ints = DbcTables.Read(source, @"DBFilesClient\LightIntBand.dbc", ReadCurves);
        var floats = DbcTables.Read(source, @"DBFilesClient\LightFloatBand.dbc", ReadFloatCurves);
        var parameters = new Dictionary<int, Params>();

        DbcTables.Read<object?>(source, @"DBFilesClient\LightParams.dbc", dbc =>
        {
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                var id = dbc.GetInt32(row, 0);
                var bands = new Curve[BandsPerParams];
                for (var band = 0; band < BandsPerParams; band++)
                {
                    bands[band] = ints.GetValueOrDefault((id - 1) * BandsPerParams + band + 1, new Curve([], []));
                }

                var floatBands = new FloatCurve[FloatBandsPerParams];
                for (var band = 0; band < FloatBandsPerParams; band++)
                {
                    floatBands[band] = floats.GetValueOrDefault((id - 1) * FloatBandsPerParams + band + 1, new FloatCurve([], []));
                }

                parameters[id] = new Params(
                    bands,
                    floatBands,
                    dbc.GetSingle(row, 5),
                    dbc.GetSingle(row, 6),
                    dbc.GetSingle(row, 7),
                    dbc.GetSingle(row, 8));
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\Light.dbc", dbc =>
        {
            for (var row = 0; row < dbc.RecordCount; row++)
            {
                if (!parameters.TryGetValue(dbc.GetInt32(row, 7), out var clear))
                {
                    continue;
                }

                var map = dbc.GetInt32(row, 1);
                if (!table._byMap.TryGetValue(map, out var lights))
                {
                    table._byMap[map] = lights = [];
                }

                lights.Add(new Light(
                    new Vector3(dbc.GetSingle(row, 2), dbc.GetSingle(row, 3), dbc.GetSingle(row, 4)) / UnitsPerYard,
                    dbc.GetSingle(row, 5) / UnitsPerYard,
                    dbc.GetSingle(row, 6) / UnitsPerYard,
                    clear));
            }

            return null;
        });

        // Smallest first and the global light last, as blending needs.
        foreach (var lights in table._byMap.Values)
        {
            lights.Sort((a, b) => (a.Global, b.Global) switch
            {
                (true, false) => 1,
                (false, true) => -1,
                _ => a.Outer.CompareTo(b.Outer),
            });
        }

        return table;
    }

    /// <summary>
    /// Works out the sky on a map. The position is east, up and south from the map's north-west
    /// corner, in yards, and time is in half minutes since midnight.
    /// </summary>
    public void Sample(int mapId, Vector3 position, float ticks, SkyState into)
    {
        Array.Clear(into.Colors);
        into.FogEnd = into.FogStart = 0;
        into.RiverShallowAlpha = into.RiverDeepAlpha = into.OceanShallowAlpha = into.OceanDeepAlpha = 0;

        // A map with no lights of its own borrows the first global light, as Noggit does.
        if (!_byMap.TryGetValue(mapId, out var lights) || lights.Count == 0)
        {
            var fallback = _byMap.Values.SelectMany(l => l).FirstOrDefault(l => l.Global);
            if (fallback is not null)
            {
                Add(fallback.Clear, 1f, ticks, into);
            }

            return;
        }

        // Each light takes its share of what's left, starting from the smallest.
        var remaining = 1f;
        foreach (var light in lights)
        {
            var weight = 1f;
            if (!light.Global && lights[^1] != light)
            {
                var distance = Vector3.Distance(position, light.Position);
                if (distance >= light.Outer)
                {
                    continue;
                }

                weight = distance <= light.Inner
                    ? 1f
                    : 1f - (distance - light.Inner) / MathF.Max(light.Outer - light.Inner, 1e-3f);
            }

            Add(light.Clear, remaining * weight, ticks, into);
            remaining *= 1f - weight;
            if (remaining <= 0f)
            {
                return;
            }
        }
    }

    private static void Add(Params parameters, float weight, float ticks, SkyState into)
    {
        for (var band = 0; band < BandsPerParams; band++)
        {
            into.Colors[band] += ColorAt(parameters.Bands[band], ticks) * weight;
        }

        into.FogEnd += ValueAt(parameters.Floats[FogEndFloatBand], ticks) / UnitsPerYard * weight;
        into.FogStart += ValueAt(parameters.Floats[FogStartFloatBand], ticks) * weight;
        into.RiverShallowAlpha += parameters.RiverShallow * weight;
        into.RiverDeepAlpha += parameters.RiverDeep * weight;
        into.OceanShallowAlpha += parameters.OceanShallow * weight;
        into.OceanDeepAlpha += parameters.OceanDeep * weight;
    }

    private static Vector3 ColorAt(Curve curve, float ticks)
    {
        if (curve.Times.Length == 0)
        {
            return Vector3.Zero;
        }

        var (before, after, amount) = Between(curve.Times, ticks);
        return Vector3.Lerp(curve.Colors[before], curve.Colors[after], amount);
    }

    private static float ValueAt(FloatCurve curve, float ticks)
    {
        if (curve.Times.Length == 0)
        {
            return 0f;
        }

        var (before, after, amount) = Between(curve.Times, ticks);
        return curve.Values[before] + (curve.Values[after] - curve.Values[before]) * amount;
    }

    /// <summary>The keys either side of a time, wrapping around midnight.</summary>
    private static (int Before, int After, float Amount) Between(int[] times, float ticks)
    {
        var last = times.Length - 1;
        var before = last;
        for (var i = 0; i <= last; i++)
        {
            if (times[i] <= ticks)
            {
                before = i;
            }
        }

        var after = before == last ? 0 : before + 1;
        var start = (float)times[before];
        var end = (float)times[after];
        if (after <= before)
        {
            end += TicksPerDay;
        }

        var now = ticks < start ? ticks + TicksPerDay : ticks;
        var length = end - start;
        return (before, after, length > 0 ? Math.Clamp((now - start) / length, 0f, 1f) : 0f);
    }

    private static Dictionary<int, Curve> ReadCurves(DbcFile dbc)
    {
        var curves = new Dictionary<int, Curve>(dbc.RecordCount);
        for (var row = 0; row < dbc.RecordCount; row++)
        {
            var count = Math.Clamp(dbc.GetInt32(row, 1), 0, MaxBandEntries);
            var times = new int[count];
            var colors = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                times[i] = dbc.GetInt32(row, 2 + i);
                var color = dbc.GetUInt32(row, 2 + MaxBandEntries + i);
                colors[i] = new Vector3((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF) / 255f;
            }

            curves[dbc.GetInt32(row, 0)] = new Curve(times, colors);
        }

        return curves;
    }

    private static Dictionary<int, FloatCurve> ReadFloatCurves(DbcFile dbc)
    {
        var curves = new Dictionary<int, FloatCurve>(dbc.RecordCount);
        for (var row = 0; row < dbc.RecordCount; row++)
        {
            var count = Math.Clamp(dbc.GetInt32(row, 1), 0, MaxBandEntries);
            var times = new int[count];
            var values = new float[count];
            for (var i = 0; i < count; i++)
            {
                times[i] = dbc.GetInt32(row, 2 + i);
                values[i] = dbc.GetSingle(row, 2 + MaxBandEntries + i);
            }

            curves[dbc.GetInt32(row, 0)] = new FloatCurve(times, values);
        }

        return curves;
    }
}

/// <summary>
/// Where the sun is. The client's light tables brighten around 5:30 and go dark around 20:30, so
/// the sun rises and sets then. This is fitted to the look of those tables, not read from a file.
/// </summary>
public static class SunPath
{
    public const int SunriseTicks = 660;
    public const int SunsetTicks = 2460;

    /// <summary>How far south of straight overhead the sun passes, so noon has some shading.</summary>
    private const float SouthTilt = 0.35f;

    /// <summary>A direction towards the sun in Godot axes (X east, Y up, Z south), below the horizon at night.</summary>
    public static Vector3 Direction(float ticks)
    {
        ticks = (ticks % LightTable.TicksPerDay + LightTable.TicksPerDay) % LightTable.TicksPerDay;
        var dayLength = SunsetTicks - SunriseTicks;

        float east, up;
        if (ticks >= SunriseTicks && ticks <= SunsetTicks)
        {
            var angle = MathF.PI * (ticks - SunriseTicks) / dayLength;
            (east, up) = (MathF.Cos(angle), MathF.Sin(angle));
        }
        else
        {
            var sinceSunset = (ticks - SunsetTicks + LightTable.TicksPerDay) % LightTable.TicksPerDay;
            var angle = MathF.PI * sinceSunset / (LightTable.TicksPerDay - dayLength);
            (east, up) = (-MathF.Cos(angle), -MathF.Sin(angle));
        }

        return Vector3.Normalize(new Vector3(east, up, SouthTilt));
    }
}
