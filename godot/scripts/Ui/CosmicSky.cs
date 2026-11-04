using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.World;
using Godot;

namespace WowVR.Ui;

/// <summary>A zone's sky for the voids: its skybox model and the colors of its sky, horizon and fog.</summary>
public sealed record VoidLook(string SkyModel, Color Top, Color Horizon, Color Fog)
{
    /// <summary>Hellfire Peninsula's, if the client's light tables have it.</summary>
    public const string HellfireSky = @"Environments\Stars\HellfireSkyBox.m2";

    public static readonly VoidLook Fallback = new(HellfireSky, new Color(0.14f, 0f, 0.07f), new Color(0.5f, 0.33f, 0.26f), new Color(0.16f, 0f, 0.12f));

    /// <summary>
    /// Hellfire Peninsula in the afternoon, from the client's light tables: the same sky and fog as
    /// flying there, near Honor Hold and Thrallmar.
    /// </summary>
    public static VoidLook Hellfire(IAssetSource source)
    {
        const int Outland = 530;
        var sky = new SkyState();
        var position = new System.Numerics.Vector3((float)WowCoords.MapOrigin - 2600f, 0, (float)WowCoords.MapOrigin + 400f);
        LightTable.Read(source).Sample(Outland, position, 14f * LightTable.TicksPerDay / 24f, sky);
        static Color Of(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
        var model = sky.Skyboxes.OrderByDescending(s => s.Value).Select(s => s.Key.Path).FirstOrDefault(p => p.Length > 0) ?? HellfireSky;
        return new VoidLook(model, Of(sky[SkyBand.SkyTop]), Of(sky[SkyBand.SkyHorizon]), Of(sky[SkyBand.Fog]));
    }
}

/// <summary>
/// A zone's sky around the head, at infinity, with a thick fog in its colors where the sky has no
/// ground, for the voids behind the login and loading screens. It shows once the client is open.
/// </summary>
public partial class CosmicSky : Node3D
{
    private const float FogRadius = 300f;

    private (ShaderMaterial Material, M2Effect Effect)[] _materials = [];
    private ShaderMaterial? _fog;
    private MeshInstance3D? _fogSphere;
    private double _seconds;
    private float _weight = 1f;

    /// <summary>The render layers it's drawn in.</summary>
    public uint Layers { get; init; } = 1;

    /// <summary>
    /// Its sky's render priority, to draw it among other see-through things, with the fog one above,
    /// or null to stay behind everything with the fog behind what's solid.
    /// </summary>
    public int? Priority { get; init; }

    /// <summary>How much it shows, from 0 to 1.</summary>
    public float Weight
    {
        get => _weight;
        set
        {
            _weight = value;
            foreach (var (material, _) in _materials)
            {
                material.SetShaderParameter("skybox_weight", value);
            }

            _fog?.SetShaderParameter("fade", value);
        }
    }

    public void Load(ModelLibrary models, VoidLook look)
    {
        // Over everything, as the loading screen is, or behind what's solid, as on the login stage.
        _fog = new ShaderMaterial { Shader = GD.Load<Shader>(Priority is null ? "res://shaders/void_fog.gdshader" : "res://shaders/loading_fog.gdshader") };
        // Just after the sky either way: otherwise, centered on the eye, it would sort in front of
        // everything else see-through, like the stage's glowing floor.
        _fog.RenderPriority = Priority is { } priority ? priority + 1 : (int)RenderingServer.MaterialRenderPriorityMin + 1;

        _fog.SetShaderParameter("horizon_color", look.Horizon);
        _fog.SetShaderParameter("fog_color", look.Fog);
        _fogSphere = new MeshInstance3D
        {
            Name = "Fog",
            Mesh = new SphereMesh { Radius = FogRadius, Height = FogRadius * 2f, Material = _fog },
            Layers = Layers,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(_fogSphere);
        Weight = _weight;

        Task.Run(() => models.GetM2(look.SkyModel) is { } asset ? models.SkyboxMeshOf(asset) : ((ArrayMesh, (ShaderMaterial, M2Effect)[])?)null)
            .ContinueWith(loaded => Callable.From(() =>
            {
                if (!IsInstanceValid(this) || !loaded.IsCompletedSuccessfully || loaded.Result is not { } sky)
                {
                    return;
                }

                _materials = sky.Item2;
                foreach (var (material, _) in _materials)
                {
                    if (Priority is { } priority)
                    {
                        material.RenderPriority = priority;
                    }
                }

                AddChild(new MeshInstance3D { Mesh = sky.Item1, Name = "Sky", Layers = Layers, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                Weight = _weight;
            }).CallDeferred());
    }

    public override void _Process(double delta)
    {
        _seconds += delta;
        foreach (var (material, effect) in _materials)
        {
            if (effect.Animated)
            {
                ModelLibrary.ApplyEffect(material, effect, _seconds);
            }
        }

        // Around the head wherever it goes, as the sky is.
        if (_fogSphere is not null && GetViewport().GetCamera3D() is { } camera)
        {
            _fogSphere.GlobalPosition = camera.GlobalPosition;
        }
    }
}
