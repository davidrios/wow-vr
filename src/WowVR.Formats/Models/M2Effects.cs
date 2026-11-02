using System.Numerics;
using WowVR.Formats.Native;

namespace WowVR.Formats.Models;

/// <summary>Where a texture's coordinates come from.</summary>
public enum M2UvSource
{
    First = 0,
    Second = 1,
    SphereMap = 2,
}

/// <summary>
/// How a batch's textures combine, numbered as <c>godot/shaders/m2_combiners.gdshaderinc</c> expects.
/// One texture uses its combiner's number, two use 10 + first * 10 + second, and the special cases are 100 up.
/// </summary>
public static class M2Combiners
{
    public const int Opaque = 0;
    public const int Mod = 1;
    public const int Decal = 2;
    public const int Add = 3;
    public const int Mod2x = 4;
    public const int Fade = 5;
    public const int ModNA2x = 6;
    public const int AddNA = 7;

    public const int TwoTextures = 10;
    public const int OpaqueMod2xNAAlpha = 100;
    public const int OpaqueAddAlpha = 101;
    public const int OpaqueAddAlphaAlpha = 102;

    public static int Pair(int first, int second) => TwoTextures + first * 10 + second;
}

/// <summary>A batch's stored fields that decide how it draws.</summary>
public sealed class M2ShaderBatch
{
    public required int ShaderId { get; set; }
    public required int TextureCount { get; set; }
    public required int MaterialIndex { get; init; }
    public required int Layer { get; init; }
    public required int BlendMode { get; init; }
    public required uint RenderFlags { get; init; }
    public required int Coord0 { get; init; }
    public required int Coord1 { get; init; }
    public required int TextureWeight { get; init; }
    public required uint[] Textures { get; set; }
    public required int[] Transforms { get; set; }

    /// <summary>Set by another layer that this one merged into. Merged layers aren't drawn.</summary>
    public bool Merged => ShaderId == M2ShaderSelection.NoShader;
}

/// <summary>
/// The 3.3.5a client works out each batch's shader when it loads a skin, instead of reading it:
/// this follows the wiki's port of its functions, at <c>M2/.skin/WotLK_shader_selection</c>.
/// </summary>
public static class M2ShaderSelection
{
    public const int NoShader = 0x8000;
    private const int FlagModern = 0x8000;
    private const int FlagSecondUv = 0x4000;
    private const int EnvStage = 8;
    private const int OpaqueMod2xNAEnv = 0xE;
    private const int ShaderMod2xNAAlpha = 0x8001;
    private const int ShaderAddAlpha = 0x8002;
    private const int ShaderAddAlphaAlpha = 0x8003;
    private const int BlendOpaque = 0;
    private const int BlendAlphaKey = 1;
    private const int BlendAlpha = 2;
    private const int BlendAdd = 4;
    private const int BlendMod2x = 6;
    private const int FallbackShader = 0x11;

    /// <param name="overrides">The model's combiner table, or null when its flag says it has none.</param>
    public static void Select(IReadOnlyList<M2ShaderBatch> batches, IReadOnlyList<ushort>? overrides)
    {
        foreach (var batch in batches)
        {
            InitialShader(batch, overrides);
        }

        MergeLayers(batches);
    }

    private static void InitialShader(M2ShaderBatch batch, IReadOnlyList<ushort>? overrides)
    {
        if ((batch.ShaderId & FlagModern) != 0)
        {
            return;
        }

        if (overrides is null)
        {
            var shader = 0;
            if (batch.BlendMode != BlendOpaque)
            {
                shader = 1 | (batch.Coord0 == -1 ? EnvStage : 0);
            }

            batch.ShaderId = (shader * 16) | (batch.Coord0 == 1 ? FlagSecondUv : 0);
            return;
        }

        if (batch.TextureCount == 0)
        {
            return;
        }

        var stages = new int[2];
        var result = 0;
        var count = Math.Min(batch.TextureCount, 2);
        for (var op = 0; op < count; op++)
        {
            var index = batch.ShaderId + op;
            var mode = index < overrides.Count ? overrides[index] : 0;
            if (op == 0 && batch.BlendMode == BlendOpaque)
            {
                mode = 0;
            }

            var coord = op == 0 ? batch.Coord0 : batch.Coord1;
            stages[op] = mode | (coord == -1 ? EnvStage : 0);
            if (coord == 1 && op + 1 == count)
            {
                result |= FlagSecondUv;
            }
        }

        batch.ShaderId = result | stages[1] | (stages[0] * 16);
    }

    private static void MergeLayers(IReadOnlyList<M2ShaderBatch> batches)
    {
        if (batches.All(b => b.Layer <= 0))
        {
            return;
        }

        var (state0, state1) = (0, 0);
        var sharedMaterials = false;
        M2ShaderBatch? first = null;
        M2ShaderBatch? previous = null;
        foreach (var current in batches)
        {
            if (previous is not null && current.MaterialIndex == previous.MaterialIndex)
            {
                sharedMaterials = true;
                continue;
            }

            previous = current;
            var stage2 = current.ShaderId & 7;
            if (current.Layer == 0)
            {
                (state0, state1) = (0, 0);
                if (current.TextureCount >= 1 && current.BlendMode == BlendOpaque)
                {
                    current.ShaderId &= 0xFF8F;
                }

                first = current;
            }

            first ??= current;
            var sameWeight = first.TextureWeight == current.TextureWeight;
            var sameUnlit = ((first.RenderFlags ^ current.RenderFlags) & 1) == 0;
            var sameTexture = first.Textures[0] == current.Textures[0];
            var alphaBlended = current.BlendMode is BlendAlpha or BlendAlphaKey;

            var skipFirst = false;
            if (state0 != 0)
            {
                if (state0 == 1)
                {
                    if (alphaBlended && current.TextureCount == 1 && sameUnlit && sameTexture && sameWeight)
                    {
                        current.ShaderId = NoShader;
                        first.ShaderId = ShaderMod2xNAAlpha;
                        state0 = 3;
                        continue;
                    }

                    state0 = 0;
                }
                else
                {
                    skipFirst = true;
                }
            }

            if (!skipFirst
                && current.BlendMode == BlendOpaque
                && current.TextureCount == 2
                && stage2 is 4 or 6
                && current.Coord0 == 0
                && current.Coord1 == -1)
            {
                state0 = 1;
            }

            if (state1 == 1)
            {
                if (current.BlendMode is not (BlendAdd or BlendMod2x)
                    || current.TextureCount != 1
                    || current.Coord0 is >= 0 and <= 2)
                {
                    state1 = 0;
                }
                else if (sameWeight)
                {
                    state1 = 2;
                    current.ShaderId = NoShader;
                    first.ShaderId = current.BlendMode != BlendAdd ? OpaqueMod2xNAEnv : ShaderAddAlpha;
                    first.TextureCount = 2;
                    first.Textures = [first.Textures[0], current.Textures[0]];
                    first.Transforms = [first.Transforms[0], current.Transforms[0]];
                    continue;
                }
            }
            else if (state1 != 0)
            {
                if (state1 != 2)
                {
                    continue;
                }

                if (!alphaBlended || current.TextureCount != 1 || !sameUnlit || !sameTexture)
                {
                    state1 = 0;
                }
                else if (sameWeight)
                {
                    state1 = 3;
                    current.ShaderId = NoShader;
                    first.ShaderId = first.ShaderId == ShaderAddAlpha ? ShaderAddAlphaAlpha : ShaderMod2xNAAlpha;
                    continue;
                }
            }

            if (current.BlendMode == BlendOpaque && current.TextureCount == 1 && current.Coord0 == 0)
            {
                state1 = 1;
            }
        }

        if (sharedMaterials)
        {
            previous = null;
            foreach (var current in batches)
            {
                if (previous is not null && current.MaterialIndex == previous.MaterialIndex)
                {
                    current.ShaderId = previous.ShaderId;
                    current.TextureCount = previous.TextureCount;
                    current.Textures = previous.Textures;
                    current.Transforms = previous.Transforms;
                }
                else
                {
                    previous = current;
                }
            }
        }
    }

    /// <summary>A selected batch's combiner and each texture's coordinates.</summary>
    public static (int Combiner, M2UvSource Uv0, M2UvSource Uv1) Resolve(M2ShaderBatch batch)
    {
        if ((batch.ShaderId & FlagModern) != 0)
        {
            var combiner = (batch.ShaderId & 0x7FFF) switch
            {
                2 => M2Combiners.OpaqueAddAlpha,
                3 => M2Combiners.OpaqueAddAlphaAlpha,
                _ => M2Combiners.OpaqueMod2xNAAlpha,
            };
            return (combiner, M2UvSource.First, M2UvSource.SphereMap);
        }

        var first = (batch.ShaderId >> 4) & 7;
        var second = batch.ShaderId & 7;
        var firstEnv = ((batch.ShaderId >> 4) & EnvStage) != 0;
        var secondEnv = (batch.ShaderId & EnvStage) != 0;
        var secondUv = (batch.ShaderId & FlagSecondUv) != 0;

        if (batch.TextureCount <= 1)
        {
            var single = first <= M2Combiners.Fade ? first : M2Combiners.Mod;
            return (single, firstEnv ? M2UvSource.SphereMap : secondUv ? M2UvSource.Second : M2UvSource.First, M2UvSource.First);
        }

        // Shaders the client has no entry for fall back to Mod_Mod, so this does too.
        var known = (first, second) switch
        {
            (0, 0 or 1 or 3 or 4 or 6 or 7) => true,
            (1, 0 or 1 or 3 or 4 or 6 or 7) => true,
            (3, 1) or (4, 4) => true,
            _ => false,
        };
        if (!known)
        {
            (first, second) = ((FallbackShader >> 4) & 7, FallbackShader & 7);
        }

        return (M2Combiners.Pair(first, second),
            firstEnv ? M2UvSource.SphereMap : M2UvSource.First,
            secondEnv ? M2UvSource.SphereMap : secondUv ? M2UvSource.Second : M2UvSource.First);
    }
}

/// <summary>Keys that play on their own clock, independent of whatever a creature is doing.</summary>
public sealed class M2EffectTrack<T>(float[] times, T[] values, float period, bool step, Func<T, T, float, T> lerp)
{
    public bool Changes { get; } = values.Length > 1 && values.Any(v => !EqualityComparer<T>.Default.Equals(v, values[0]));

    public IReadOnlyList<T> Values => values;

    public T Sample(double clock)
    {
        if (!Changes || period <= 0f)
        {
            return values[0];
        }

        var time = (float)(clock - Math.Floor(clock / period) * period);
        var after = Array.BinarySearch(times, time);
        after = after >= 0 ? after : ~after;
        if (after == 0)
        {
            return values[0];
        }

        if (after >= times.Length || step)
        {
            return values[after - 1];
        }

        var span = times[after] - times[after - 1];
        return span > 0f ? lerp(values[after - 1], values[after], (time - times[after - 1]) / span) : values[after];
    }
}

/// <summary>A texture coordinate transform: u' = A u + B v + X, v' = C u + D v + Y.</summary>
public readonly record struct M2UvMatrix(float A, float B, float C, float D, float X, float Y)
{
    public static readonly M2UvMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public Vector2 Apply(Vector2 uv) => new(A * uv.X + B * uv.Y + X, C * uv.X + D * uv.Y + Y);
}

/// <summary>A texture's animated transform. Rotation is around the texture's middle, as the wiki says.</summary>
public sealed record M2UvAnimation(M2EffectTrack<Vector3>? Translation, M2EffectTrack<Quaternion>? Rotation, M2EffectTrack<Vector3>? Scale)
{
    public bool Changes => Translation?.Changes == true || Rotation?.Changes == true || Scale?.Changes == true;

    public M2UvMatrix At(double clock)
    {
        var t = Translation?.Sample(clock) ?? Vector3.Zero;
        var q = Rotation?.Sample(clock) ?? Quaternion.Identity;
        var s = Scale?.Sample(clock) ?? Vector3.One;

        var a = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var b = 2 * (q.X * q.Y - q.W * q.Z);
        var c = 2 * (q.X * q.Y + q.W * q.Z);
        var d = 1 - 2 * (q.X * q.X + q.Z * q.Z);
        return new M2UvMatrix(
            a * s.X,
            b * s.Y,
            c * s.X,
            d * s.Y,
            t.X + 0.5f - (a + b) * 0.5f,
            t.Y + 0.5f - (c + d) * 0.5f);
    }
}

/// <summary>What a batch needs beyond one still texture. Each batch has its own, since materials animate.</summary>
public sealed class M2Effect
{
    public required int Combiner { get; init; }
    public required M2UvSource Uv0 { get; init; }
    public required M2UvSource Uv1 { get; init; }

    /// <summary>The second texture's file, when it names one.</summary>
    public string? Texture2Name { get; init; }

    /// <summary>The second texture's M2 type, for one chosen at runtime.</summary>
    public int Texture2Type { get; init; }
    public bool Repeat2 { get; init; }
    public M2UvAnimation? Uv0Animation { get; init; }
    public M2UvAnimation? Uv1Animation { get; init; }
    public M2EffectTrack<Vector3>? Color { get; init; }
    public M2EffectTrack<float>? ColorAlpha { get; init; }
    public M2EffectTrack<float>? Weight { get; init; }

    public bool UsesSecondUv => Uv0 == M2UvSource.Second || Uv1 == M2UvSource.Second;

    public bool HasTwoTextures => Combiner >= M2Combiners.TwoTextures;

    /// <summary>A color track replaces lighting, as WoWModelViewer draws it.</summary>
    public bool Tinted => Color is not null;

    /// <summary>Its alpha can drop below 1, so even an opaque blend mode has to blend.</summary>
    public bool Fades => (ColorAlpha?.Values.Any(v => v < 1f) ?? false) || (Weight?.Values.Any(v => v < 1f) ?? false);

    /// <summary>Anything that changes over time.</summary>
    public bool Animated => Uv0Animation?.Changes == true || Uv1Animation?.Changes == true
        || Color?.Changes == true || ColorAlpha?.Changes == true || Weight?.Changes == true;

    public Vector3 TintAt(double clock) => Color?.Sample(clock) ?? Vector3.One;

    public float AlphaAt(double clock) => (ColorAlpha?.Sample(clock) ?? 1f) * (Weight?.Sample(clock) ?? 1f);
}

/// <summary>How one batch draws.</summary>
public sealed record M2BatchLook(bool Hidden, ModelBlend Blend, bool Unlit, bool TwoSided, int TextureType, string? Texture, bool Repeat, M2Effect? Effect);

internal static unsafe class M2BatchLooks
{
    private const uint BatchStatic = 0x10;
    private const uint FlagUnlit = 0x1;
    private const uint FlagTwoSided = 0x4;
    private const uint WrapX = 0x1;
    private const uint WrapY = 0x2;

    private const uint FamilyColor = 0;
    private const uint FamilyWeight = 1;
    private const uint FamilyTransform = 2;

    /// <summary>One per batch, in the mesh's batch order.</summary>
    public static M2BatchLook[] Read(nint model, uint batchCount, Func<uint, ModelBlend> blendOf)
    {
        var raw = new WaM2Material[batchCount];
        var batches = new M2ShaderBatch[batchCount];
        for (var i = 0u; i < batchCount; i++)
        {
            ModelNativeMethods.M2Material(model, i, out var material);
            raw[i] = material;
            batches[i] = new M2ShaderBatch
            {
                ShaderId = (int)material.ShaderId,
                TextureCount = (int)material.TextureCount,
                MaterialIndex = (int)material.MaterialIndex,
                Layer = (int)material.MaterialLayer,
                BlendMode = (int)material.BlendMode,
                RenderFlags = material.Flags,
                Coord0 = material.TexCoords[0],
                Coord1 = material.TexCoords[1],
                TextureWeight = material.TextureWeight,
                Textures = [material.Textures[0], material.Textures[1]],
                Transforms = [material.TextureTransforms[0], material.TextureTransforms[1]],
            };
        }

        ushort[]? overrides = null;
        if (ModelNativeMethods.M2CombinerOverrides(model, out var table, out var tableCount) != 0)
        {
            overrides = new ReadOnlySpan<ushort>(table, (int)tableCount).ToArray();
        }

        M2ShaderSelection.Select(batches, overrides);

        var effects = ModelNativeMethods.M2EffectsOpen(model);
        if (effects == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        try
        {
            ModelNativeMethods.M2GlobalSequences(model, out var globalLengths, out var globalCount);
            var periods = new float[globalCount];
            for (var i = 0; i < periods.Length; i++)
            {
                periods[i] = globalLengths[i] / 1000f;
            }

            var firstSequence = ModelNativeMethods.M2FirstSequenceMs(model) / 1000f;
            var looks = new M2BatchLook[batchCount];
            for (var i = 0; i < looks.Length; i++)
            {
                looks[i] = LookOf(model, effects, raw[i], batches[i], periods, firstSequence, blendOf);
            }

            return looks;
        }
        finally
        {
            ModelNativeMethods.M2EffectsFree(effects);
        }
    }

    private static M2BatchLook LookOf(nint model, nint effects, in WaM2Material raw, M2ShaderBatch batch, float[] periods, float firstSequence, Func<uint, ModelBlend> blendOf)
    {
        var unlit = (raw.Flags & FlagUnlit) != 0;
        var twoSided = (raw.Flags & FlagTwoSided) != 0;
        var blend = blendOf(raw.BlendMode);
        var (texture, type, repeat) = TextureOf(model, batch.Textures[0]);

        var color = raw.Color >= 0 ? ReadVector(effects, FamilyColor, raw.Color, 0, periods, firstSequence) : null;
        var colorAlpha = raw.Color >= 0 ? ReadFloat(effects, FamilyColor, raw.Color, 1, periods, firstSequence) : null;
        var weight = raw.TextureWeight >= 0 ? ReadFloat(effects, FamilyWeight, raw.TextureWeight, 0, periods, firstSequence) : null;

        // Fully transparent for good means it's never drawn, whatever its blend mode.
        var hidden = batch.Merged || IsAlwaysZero(colorAlpha) || IsAlwaysZero(weight);

        // Tracks only animate a batch not marked as static, as WoWModelViewer has it.
        var animates = (raw.BatchFlags & BatchStatic) == 0;
        var uv0 = animates ? ReadUv(effects, batch.Transforms[0], periods, firstSequence) : null;
        var uv1 = animates ? ReadUv(effects, batch.Transforms[1], periods, firstSequence) : null;

        var (combiner, source0, source1) = M2ShaderSelection.Resolve(batch);
        var plain = combiner is M2Combiners.Opaque or M2Combiners.Mod
            && source0 == M2UvSource.First
            && uv0 is null
            && color is null
            && !(colorAlpha?.Values.Any(v => v < 1f) ?? false)
            && !(weight?.Values.Any(v => v < 1f) ?? false);
        if (plain)
        {
            return new M2BatchLook(hidden, blend, unlit, twoSided, type, texture, repeat, null);
        }

        var (texture2, type2, repeat2) = combiner >= M2Combiners.TwoTextures ? TextureOf(model, batch.Textures[1]) : (null, 0, false);
        var effect = new M2Effect
        {
            Combiner = combiner,
            Uv0 = source0,
            Uv1 = source1,
            Texture2Name = texture2,
            Texture2Type = type2,
            Repeat2 = repeat2,
            Uv0Animation = uv0,
            Uv1Animation = combiner >= M2Combiners.TwoTextures ? uv1 : null,
            Color = color,
            ColorAlpha = colorAlpha,
            Weight = weight,
        };
        return new M2BatchLook(hidden, blend, unlit, twoSided, type, texture, repeat, effect);
    }

    private static bool IsAlwaysZero(M2EffectTrack<float>? track) => track is not null && track.Values.All(v => v <= 0f);

    private static (string? Name, int Type, bool Repeat) TextureOf(nint model, uint index)
    {
        if (index == ModelNativeMethods.NoTexture
            || ModelNativeMethods.M2TextureType(model, index, out var type, out var flags) != WaStatus.Ok)
        {
            return (null, 0, false);
        }

        string? name = null;
        if (ModelNativeMethods.M2TextureName(model, index, out var text, out var length) == WaStatus.Ok && length > 0)
        {
            name = MeshReader.Text(text, length);
        }

        return (name, (int)type, (flags & (WrapX | WrapY)) != 0);
    }

    private static M2UvAnimation? ReadUv(nint effects, int index, float[] periods, float firstSequence)
    {
        if (index < 0 || index >= ModelNativeMethods.M2EffectsCount(effects, FamilyTransform))
        {
            return null;
        }

        var animation = new M2UvAnimation(
            ReadVector(effects, FamilyTransform, index, 0, periods, firstSequence),
            ReadRotation(effects, index, periods, firstSequence),
            ReadVector(effects, FamilyTransform, index, 2, periods, firstSequence));
        return animation.Translation is null && animation.Rotation is null && animation.Scale is null ? null : animation;
    }

    private static M2EffectTrack<T>? ReadTrack<T>(nint effects, uint family, int index, uint kind, int stride, float[] periods, float firstSequence, Func<IntPtr, int, T> read, Func<T, T, float, T> lerp)
    {
        if (ModelNativeMethods.M2EffectsTrack(effects, family, (uint)index, kind, out var track) != WaStatus.Ok || track.KeyCount == 0)
        {
            return null;
        }

        var count = (int)track.KeyCount;
        var times = new float[count];
        var values = new T[count];
        for (var key = 0; key < count; key++)
        {
            times[key] = track.Times[key] / 1000f;
            values[key] = read((IntPtr)track.Values, key * stride);
        }

        var period = track.GlobalSequence >= 0 && track.GlobalSequence < periods.Length ? periods[track.GlobalSequence] : firstSequence;
        return new M2EffectTrack<T>(times, values, period, track.Interpolation == 0, lerp);
    }

    private static M2EffectTrack<Vector3>? ReadVector(nint effects, uint family, int index, uint kind, float[] periods, float firstSequence) =>
        ReadTrack(effects, family, index, kind, 3, periods, firstSequence,
            (p, i) => { var v = (float*)p; return new Vector3(v[i], v[i + 1], v[i + 2]); },
            Vector3.Lerp);

    private static M2EffectTrack<float>? ReadFloat(nint effects, uint family, int index, uint kind, float[] periods, float firstSequence) =>
        ReadTrack(effects, family, index, kind, 1, periods, firstSequence,
            (p, i) => ((float*)p)[i],
            float.Lerp);

    private static M2EffectTrack<Quaternion>? ReadRotation(nint effects, int index, float[] periods, float firstSequence) =>
        ReadTrack(effects, FamilyTransform, index, 1, 4, periods, firstSequence,
            (p, i) => { var v = (float*)p; return new Quaternion(v[i], v[i + 1], v[i + 2], v[i + 3]); },
            Quaternion.Slerp);
}
