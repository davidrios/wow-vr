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
/// A zone's skybox around the head, at infinity, for the voids behind the login and loading screens.
/// It shows once the client is open.
/// </summary>
public partial class CosmicSky : Node3D
{
    private (ShaderMaterial Material, M2Effect Effect)[] _materials = [];
    private double _seconds;
    private float _weight = 1f;

    /// <summary>The render layers it's drawn in.</summary>
    public uint Layers { get; init; } = 1;

    /// <summary>
    /// Its first batch's render priority, to draw it among other see-through things, or null to stay
    /// behind everything. Each batch takes the next one up.
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
        }
    }

    public void Load(ModelLibrary models, VoidLook look)
    {
        var firstPriority = Priority ?? (int)RenderingServer.MaterialRenderPriorityMin;
        Task.Run(() => models.GetM2(look.SkyModel) is { } asset ? models.SkyboxMeshOf(asset, firstPriority: firstPriority) : ((ArrayMesh, (ShaderMaterial, M2Effect)[])?)null)
            .ContinueWith(loaded => Callable.From(() =>
            {
                if (!IsInstanceValid(this) || !loaded.IsCompletedSuccessfully || loaded.Result is not { } sky)
                {
                    return;
                }

                _materials = sky.Item2;
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
    }
}
