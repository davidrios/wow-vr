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

    /// <summary>
    /// How much each placed emitter's period differs, either way. A creature's emitters all start
    /// together, so equal ones would fire together, and this lets them drift apart. Godot's own
    /// randomness doesn't help, since its seed is the same for every node. By eye.
    /// </summary>
    private const double PeriodFuzz = 0.1;

    /// <summary>Where a doodad's animation takes it. A creature's emitters ride on its skeleton instead.</summary>
    public M2EmitterPath? Path { get; init; }

    /// <summary>The emitter, when it switches on and off in its animation or starts off.</summary>
    public M2Emitter? Switching { get; init; }

    /// <summary>
    /// A placed copy. <paramref name="fresh"/> starts it empty, as a spell's effect appearing,
    /// rather than part way through, as a fire already burning.
    /// </summary>
    public GpuParticles3D NewNode(Vector3 position, bool fresh = false)
    {
        var node = NewParticles(position);
        if (fresh)
        {
            node.Preprocess = 0;
        }

        if (Path is not null || Switching is not null)
        {
            node.AddChild(new EmitterFollower { Path = Path, Emitter = Switching });
        }

        return node;
    }

    private GpuParticles3D NewParticles(Vector3 position) => new()
    {
        Position = position,
        Amount = Amount,
        Lifetime = Lifetime * (1.0 + (Random.Shared.NextDouble() * 2.0 - 1.0) * PeriodFuzz),
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

    /// <summary>A particle goes out over this much of its cycle after its life, rather than popping.</summary>
    private const float HiddenEdge = 0.001f;

    private readonly ConcurrentDictionary<bool, Shader> _shaders = new();
    private readonly ConcurrentDictionary<(string Texture, bool Keyed, int Rows, int Columns, string Cells, float Share), ShaderMaterial?> _materials = new();

    public ParticleAsset[] Build(System.Collections.Generic.IEnumerable<M2Emitter> emitters, Func<M2Emitter, M2EmitterPath?>? pathOf = null) =>
        emitters.Select(e => Build(e, pathOf?.Invoke(e))).OfType<ParticleAsset>().ToArray();

    /// <summary>Null when its texture can't be read.</summary>
    private ParticleAsset? Build(M2Emitter emitter, M2EmitterPath? path)
    {
        var peak = emitter.Sizes.Max();
        var speed = emitter.Speed * (1f + emitter.SpeedVariation);
        var longest = emitter.Lifespan + emitter.LifespanVariation;

        // Godot emits Amount per Lifetime, a whole number of them, where the file's rate can be
        // anything per lifespan. So a cycle is made longer to fit, with a particle showing for
        // only its real lifespan at the start of its cycle. Dragons' backs have rates of 0.3.
        var amount = Math.Clamp(Mathf.CeilToInt(emitter.Rate), 1, MaxAmount);
        var share = Mathf.Min(emitter.Rate / amount, 1f);
        var reach = Mathf.Abs(speed) * longest + Mathf.Abs(emitter.Gravity) * longest * longest / 2f + peak * QuadSize + Mathf.Max(emitter.AreaLength, emitter.AreaWidth);
        reach += path?.Points.Max(p => System.Numerics.Vector3.Distance(p, path.Points[0])) ?? 0f;
        if (MaterialFor(emitter, share) is not { } material)
        {
            return null;
        }

        return new ParticleAsset(
            new Vector3(emitter.Position.X, emitter.Position.Y, emitter.Position.Z),
            emitter.Bone,
            amount,
            emitter.Lifespan / share,
            ProcessOf(emitter, peak, share),
            new QuadMesh { Size = Vector2.One * QuadSize, Material = material },
            new Aabb(-Vector3.One * reach, Vector3.One * reach * 2f))
        {
            Path = path,
            Switching = !emitter.StartsOn || emitter.Switches.Any(s => !s.On) ? emitter : null,
        };
    }

    private static ParticleProcessMaterial ProcessOf(M2Emitter emitter, float peak, float share)
    {
        var (low, high) = (emitter.Speed * (1f - emitter.SpeedVariation), emitter.Speed * (1f + emitter.SpeedVariation));
        var process = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = Mathf.Clamp(Mathf.RadToDeg(emitter.Spread), 0f, 180f),
            InitialVelocityMin = Mathf.Min(low, high),
            InitialVelocityMax = Mathf.Max(low, high),
            Gravity = Vector3.Down * emitter.Gravity,
            LifetimeRandomness = Mathf.Clamp(emitter.LifespanVariation / emitter.Lifespan * share, 0f, 1f),
            ScaleMin = peak,
            ScaleMax = peak,
            ScaleCurve = new CurveTexture { Curve = SizeCurve(emitter, peak, share) },
            ColorRamp = new GradientTexture1D { Gradient = ColorRamp(emitter, share) },

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

    private static Curve SizeCurve(M2Emitter emitter, float peak, float share)
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0f, emitter.Sizes[0] / peak));
        curve.AddPoint(new Vector2(emitter.Mid * share, emitter.Sizes[1] / peak));
        curve.AddPoint(new Vector2(share, emitter.Sizes[2] / peak));
        if (share < 1f)
        {
            curve.AddPoint(new Vector2(1f, emitter.Sizes[2] / peak));
        }

        return curve;
    }

    /// <summary>Past its share of the cycle, a particle can't be seen.</summary>
    private static Gradient ColorRamp(M2Emitter emitter, float share)
    {
        var colors = emitter.Colors.Select(c => new Color(c.X, c.Y, c.Z, c.W)).ToList();
        var offsets = new System.Collections.Generic.List<float> { 0f, emitter.Mid * share, share };
        if (share < 1f)
        {
            var last = colors[^1];
            offsets.Add(Mathf.Min(share + HiddenEdge, 1f));
            colors.Add(last with { A = 0f });
            offsets.Add(1f);
            colors.Add(last with { A = 0f });
        }

        return new Gradient { Offsets = offsets.ToArray(), Colors = colors.ToArray() };
    }

    private ShaderMaterial? MaterialFor(M2Emitter emitter, float share)
    {
        var keyed = emitter.Blend == BlendAlphaKey;
        var cells = string.Join(',', emitter.CellTimes.Concat(emitter.CellValues));
        return _materials.GetOrAdd((emitter.Texture, keyed, emitter.Rows, emitter.Columns, cells, emitter.CellTimes.Length > 0 ? share : 1f), key =>
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
            material.SetShaderParameter("life_share", key.Share);
            material.SetShaderParameter("cell_times", Keys(emitter.CellTimes));
            material.SetShaderParameter("cell_values", Keys(emitter.CellValues));
            return material;
        });
    }

    private static Vector4 Keys(float[] keys) =>
        new(keys.ElementAtOrDefault(0), keys.ElementAtOrDefault(1), keys.ElementAtOrDefault(2), keys.ElementAtOrDefault(3));

    private Shader ShaderOf(bool keyed) => _shaders.GetOrAdd(keyed, k => new Shader
    {
        Code = $"shader_type spatial;\nrender_mode unshaded, cull_disabled, shadows_disabled{(k ? "" : ", blend_add, depth_draw_never, fog_disabled")};\n{(k ? "#define ALPHA_KEY\n" : "")}#include \"res://shaders/m2_particle.gdshaderinc\"\n",
    });
}
