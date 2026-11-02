using System;
using System.Collections.Concurrent;
using System.Linq;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.Converters;

/// <summary>One emitter, ready to place any number of times. Particles simulate on the GPU, in the world's space.</summary>
public sealed record ParticleAsset(Vector3 Position, int Bone, int Amount, float Lifetime, ParticleProcessMaterial Process, QuadMesh Quad, Aabb Bounds)
{
    /// <summary>Starts part way through, so a fire is already burning when it comes into range.</summary>
    private const double MaxPreprocessSeconds = 3;

    public GpuParticles3D NewNode(Vector3 position) => new()
    {
        Position = position,
        Amount = Amount,
        Lifetime = Lifetime,
        Preprocess = Math.Min(Lifetime, MaxPreprocessSeconds),
        ProcessMaterial = Process,
        DrawPass1 = Quad,
        VisibilityAabb = Bounds,
        LocalCoords = false,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };
}

/// <summary>Turns emitters into particle assets, sharing what equal emitters can.</summary>
public sealed class ParticleLibrary(Func<string, ImageTexture?> textures)
{
    private const int MaxAmount = 256;

    /// <summary>The file's blend mode for alpha-tested particles. See docs/formats.md for the rest.</summary>
    private const uint BlendAlphaKey = 3;

    /// <summary>A quad reaches this far either side of a particle's position, per unit of its size.</summary>
    private const float QuadSize = 2f;

    private readonly ConcurrentDictionary<bool, Shader> _shaders = new();
    private readonly ConcurrentDictionary<(string Texture, bool Keyed, int Rows, int Columns, string Cells), ShaderMaterial?> _materials = new();

    public ParticleAsset[] Build(System.Collections.Generic.IEnumerable<M2Emitter> emitters) =>
        emitters.Select(Build).OfType<ParticleAsset>().ToArray();

    /// <summary>Null when its texture can't be read.</summary>
    private ParticleAsset? Build(M2Emitter emitter)
    {
        if (MaterialFor(emitter) is not { } material)
        {
            return null;
        }

        var peak = emitter.Sizes.Max();
        var speed = emitter.Speed * (1f + emitter.SpeedVariation);
        var longest = emitter.Lifespan + emitter.LifespanVariation;
        var reach = Mathf.Abs(speed) * longest + Mathf.Abs(emitter.Gravity) * longest * longest / 2f + peak * QuadSize + Mathf.Max(emitter.AreaLength, emitter.AreaWidth);
        return new ParticleAsset(
            new Vector3(emitter.Position.X, emitter.Position.Y, emitter.Position.Z),
            emitter.Bone,
            Math.Clamp(Mathf.CeilToInt(emitter.Rate), 1, MaxAmount),
            emitter.Lifespan,
            ProcessOf(emitter, peak),
            new QuadMesh { Size = Vector2.One * QuadSize, Material = material },
            new Aabb(-Vector3.One * reach, Vector3.One * reach * 2f));
    }

    private static ParticleProcessMaterial ProcessOf(M2Emitter emitter, float peak)
    {
        var (low, high) = (emitter.Speed * (1f - emitter.SpeedVariation), emitter.Speed * (1f + emitter.SpeedVariation));
        var process = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = Mathf.Clamp(Mathf.RadToDeg(emitter.Spread), 0f, 180f),
            InitialVelocityMin = Mathf.Min(low, high),
            InitialVelocityMax = Mathf.Max(low, high),
            Gravity = Vector3.Down * emitter.Gravity,
            LifetimeRandomness = Mathf.Clamp(emitter.LifespanVariation / emitter.Lifespan, 0f, 1f),
            ScaleMin = peak,
            ScaleMax = peak,
            ScaleCurve = new CurveTexture { Curve = SizeCurve(emitter, peak) },
            ColorRamp = new GradientTexture1D { Gradient = ColorRamp(emitter) },

            // The shader takes this as which tile of the texture a particle shows.
            AnimOffsetMin = 0f,
            AnimOffsetMax = 1f,
        };

        if (emitter.Sphere)
        {
            process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
            process.EmissionSphereRadius = Mathf.Max(emitter.AreaLength, emitter.AreaWidth) / 2f;
        }
        else if (emitter.AreaLength > 0f || emitter.AreaWidth > 0f)
        {
            process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
            process.EmissionBoxExtents = new Vector3(emitter.AreaWidth / 2f, 0f, emitter.AreaLength / 2f);
        }

        return process;
    }

    private static Curve SizeCurve(M2Emitter emitter, float peak)
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0f, emitter.Sizes[0] / peak));
        curve.AddPoint(new Vector2(emitter.Mid, emitter.Sizes[1] / peak));
        curve.AddPoint(new Vector2(1f, emitter.Sizes[2] / peak));
        return curve;
    }

    private static Gradient ColorRamp(M2Emitter emitter) => new()
    {
        Offsets = [0f, emitter.Mid, 1f],
        Colors = emitter.Colors.Select(c => new Color(c.X, c.Y, c.Z, c.W)).ToArray(),
    };

    private ShaderMaterial? MaterialFor(M2Emitter emitter)
    {
        var keyed = emitter.Blend == BlendAlphaKey;
        var cells = string.Join(',', emitter.CellTimes.Concat(emitter.CellValues));
        return _materials.GetOrAdd((emitter.Texture, keyed, emitter.Rows, emitter.Columns, cells), key =>
        {
            if (textures(key.Texture) is not { } texture)
            {
                return null;
            }

            var material = new ShaderMaterial { Shader = ShaderOf(key.Keyed) };
            material.SetShaderParameter("tex", texture);
            material.SetShaderParameter("rows", key.Rows);
            material.SetShaderParameter("columns", key.Columns);
            material.SetShaderParameter("cell_count", emitter.CellTimes.Length);
            material.SetShaderParameter("cell_times", Keys(emitter.CellTimes));
            material.SetShaderParameter("cell_values", Keys(emitter.CellValues));
            return material;
        });
    }

    private static Vector4 Keys(float[] keys) =>
        new(keys.ElementAtOrDefault(0), keys.ElementAtOrDefault(1), keys.ElementAtOrDefault(2), keys.ElementAtOrDefault(3));

    private Shader ShaderOf(bool keyed) => _shaders.GetOrAdd(keyed, k => new Shader
    {
        Code = $"shader_type spatial;\nrender_mode unshaded, cull_disabled, shadows_disabled{(k ? "" : ", blend_add, depth_draw_never")};\n{(k ? "#define ALPHA_KEY\n" : "")}#include \"res://shaders/m2_particle.gdshaderinc\"\n",
    });
}
