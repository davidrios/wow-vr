using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using WowVR.Formats.Native;

namespace WowVR.Formats.Models;

/// <summary>The same layout as wa_m2_emitter's.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Emitter
{
    public uint Flags;
    public fixed float Position[3];
    public int Bone;
    public uint Texture;
    public uint Blend;
    public uint EmitterType;
    public uint ParticleType;
    public uint Rows;
    public uint Columns;
    public float Speed;
    public float SpeedVariation;
    public float VerticalRange;
    public float HorizontalRange;
    public float Gravity;
    public float Lifespan;
    public float LifespanVariation;
    public float Rate;
    public float RateVariation;
    public float AreaLength;
    public float AreaWidth;
    public float Mid;
    public fixed float Colors[9];
    public fixed float Alphas[3];
    public fixed float Sizes[3];
    public uint Enabled;
    public uint CellCount;
    public fixed float CellTimes[4];
    public fixed float CellValues[4];
}

/// <summary>A particle emitter, with animated settings at their first key. Positions are in Godot axes.</summary>
public sealed record M2Emitter
{
    private const uint SphereEmitter = 2;

    /// <summary>The texture's path. Emitters using a texture chosen elsewhere aren't read.</summary>
    public required string Texture { get; init; }
    public required int Bone { get; init; }
    public required Vector3 Position { get; init; }
    public required uint Blend { get; init; }
    public required bool Sphere { get; init; }
    public required int Rows { get; init; }
    public required int Columns { get; init; }
    public required float Speed { get; init; }
    public required float SpeedVariation { get; init; }
    public required float Spread { get; init; }
    public required float Gravity { get; init; }
    public required float Lifespan { get; init; }
    public required float LifespanVariation { get; init; }
    public required float Rate { get; init; }
    public required float AreaLength { get; init; }
    public required float AreaWidth { get; init; }

    /// <summary>When the middle of the three keys below happens, from 0 to 1 of a particle's life.</summary>
    public required float Mid { get; init; }

    /// <summary>Red, green, blue and alpha from 0 to 1, in gamma space.</summary>
    public required Vector4[] Colors { get; init; }
    public required float[] Sizes { get; init; }

    /// <summary>Which tile shows over a particle's life, as times from 0 to 1 and tiles. Empty for a random, fixed tile.</summary>
    public required float[] CellTimes { get; init; }
    public required float[] CellValues { get; init; }

    public bool CanEmit => Rate > 0f && Lifespan > 0f && Sizes.Any(s => s > 0f) && Colors.Any(c => c.W > 0f);

    /// <summary>Whether it's on at the start of the model's first sequence.</summary>
    public bool StartsOn { get; init; } = true;

    /// <summary>
    /// When it switches on and off in the model's first sequence, as seconds into it, as a meteor's
    /// glow lighting only as it lands. Empty for one that stays as it starts.
    /// </summary>
    public (float Seconds, bool On)[] Switches { get; init; } = [];

    /// <summary>When its switches loop on a global sequence rather than the clip, that loop's length in seconds; otherwise 0.</summary>
    public float SwitchLoopSeconds { get; init; }

    /// <summary>Whether it's on that far into the sequence.</summary>
    public bool OnAt(double seconds)
    {
        if (SwitchLoopSeconds > 0)
        {
            seconds %= SwitchLoopSeconds;
        }

        var on = StartsOn;
        foreach (var (time, value) in Switches)
        {
            if (time > seconds)
            {
                break;
            }

            on = value;
        }

        return on;
    }

    private const int MaxSwitches = 64;

    internal static unsafe IReadOnlyList<M2Emitter> ReadAll(nint model)
    {
        var count = ModelNativeMethods.M2EmitterCount(model);
        var emitters = new List<M2Emitter>();
        var times = stackalloc uint[MaxSwitches];
        var values = stackalloc byte[MaxSwitches];
        ModelNativeMethods.M2GlobalSequences(model, out var globalLengths, out var globalCount);
        for (var i = 0u; i < count; i++)
        {
            if (ModelNativeMethods.M2Emitter(model, i, out var raw) != WaStatus.Ok)
            {
                continue;
            }

            // One that starts off is kept when it switches on later.
            var global = -1;
            var switchCount = (int)Math.Min(ModelNativeMethods.M2EmitterSwitches(model, i, 0, times, values, MaxSwitches, &global), MaxSwitches);
            var loop = global >= 0 && global < globalCount ? globalLengths[global] / 1000f : 0f;
            var switches = new (float Seconds, bool On)[switchCount];
            for (var key = 0; key < switchCount; key++)
            {
                switches[key] = (times[key] / 1000f, values[key] != 0);
            }

            if (raw.Enabled == 0 && !switches.Any(s => s.On))
            {
                continue;
            }

            if (ModelNativeMethods.M2TextureName(model, raw.Texture, out var text, out var length) != WaStatus.Ok || length == 0)
            {
                continue;
            }

            var colors = new Vector4[3];
            var sizes = new float[3];
            for (var key = 0; key < 3; key++)
            {
                colors[key] = new Vector4(raw.Colors[key * 3] / 255f, raw.Colors[key * 3 + 1] / 255f, raw.Colors[key * 3 + 2] / 255f, raw.Alphas[key]);
                sizes[key] = raw.Sizes[key];
            }

            var cellTimes = new float[raw.CellCount];
            var cellValues = new float[raw.CellCount];
            for (var key = 0; key < cellTimes.Length; key++)
            {
                cellTimes[key] = raw.CellTimes[key];
                cellValues[key] = raw.CellValues[key];
            }

            var emitter = new M2Emitter
            {
                Texture = Encoding.UTF8.GetString(text, checked((int)length)),
                Bone = raw.Bone,
                Position = WowCoords.ModelToGodot(new Vector3(raw.Position[0], raw.Position[1], raw.Position[2])),
                Blend = raw.Blend,
                Sphere = raw.EmitterType == SphereEmitter,
                Rows = (int)raw.Rows,
                Columns = (int)raw.Columns,
                Speed = raw.Speed,
                SpeedVariation = raw.SpeedVariation,

                // The horizontal range turns around the emitter's axis, so it's often a full circle.
                Spread = raw.VerticalRange,
                Gravity = raw.Gravity,
                Lifespan = raw.Lifespan,
                LifespanVariation = raw.LifespanVariation,
                Rate = raw.Rate,
                AreaLength = raw.AreaLength,
                AreaWidth = raw.AreaWidth,
                Mid = raw.Mid,
                Colors = colors,
                Sizes = sizes,
                CellTimes = cellTimes,
                CellValues = cellValues,
                StartsOn = raw.Enabled != 0,
                Switches = switches,
                SwitchLoopSeconds = loop,
            };
            if (emitter.CanEmit)
            {
                emitters.Add(emitter);
            }
        }

        return emitters;
    }
}
