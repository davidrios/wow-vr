using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Database;
using WowVR.Formats.Models;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// Runs the clock and colors the sun, sky, fog and liquids from the client's light tables for
/// where the player is. Nothing here depends on the camera being desktop or VR.
/// </summary>
public partial class SkyController : Node
{
    /// <summary>Real seconds in a game day: a sunset takes a couple of minutes to watch.</summary>
    private const double SecondsPerDay = 24 * 60;

    /// <summary>Colors change slowly, and each change re-renders the sky's small cubemap, so this keeps it rare.</summary>
    private const double UpdateInterval = 0.5;

    /// <summary>The lights' direct color is strongest in the sky, so sunlight this low gives no shading to speak of.</summary>
    private const float MinLightHeight = 0.3f;

    /// <summary>
    /// The tables' direct light is far too strong to use whole: Elwynn's noon is pure orange,
    /// meant to add to its blue ambient. The client scales it by an intensity from another file,
    /// which isn't read yet, so this is picked by eye.
    /// </summary>
    private const float DiffuseIntensity = 0.5f;

    /// <summary>Multiplies the tables' fog distance. At 1, fog is where the client's tables put it.</summary>
    private const float FogDistanceScale = 1f;

    private const float MoonRoadStrength = 0.35f;

    /// <summary>Multiplies the tables' cloud cover, which is all of what's asked for at 1. Lower is a clearer sky. Picked by eye.</summary>
    private const float CloudAmount = 0.65f;

    /// <summary>Real seconds between frames of cloud, which fade into each other meanwhile.</summary>
    private const double CloudFrameInterval = 4.0;

    private const string TimeArgument = "--time=";
    private const float OcclusionLightAffect = 0.7f;

    private Action? _onSettings;

    /// <summary>Yards from the camera that shadows reach, which is most of what they cost.</summary>
    private const float ShadowDistance = 150f;

    /// <summary>The tables' ambient is blue, so full shadow turns the ground blue. Picked by eye.</summary>
    private const float ShadowOpacity = 0.75f;

    private static readonly SkyboxModel StarsModel = new(@"Environments\Stars\Stars.m2", 0);

    private const string SunTexture = @"textures\SunCenter.blp";
    private const string SunGlareTexture = @"textures\SunGlare.blp";
    private const string MoonTexture = @"textures\moon.blp";
    private const string MoonGlareTexture = @"textures\moonglare.blp";

    private static readonly Shader SkyShader = GD.Load<Shader>("res://shaders/sky.gdshader");

    /// <summary>Shader parameters by name, made once: passing a string makes a new StringName each call, and these are set often.</summary>
    private static class Param
    {
        public static readonly StringName AboveHorizonColor = "above_horizon_color";
        public static readonly StringName CloudsFade = "clouds_fade";
        public static readonly StringName CloudsFrom = "clouds_from";
        public static readonly StringName CloudsTo = "clouds_to";
        public static readonly StringName DeepAlpha = "deep_alpha";
        public static readonly StringName DeepColor = "deep_color";
        public static readonly StringName FogColor = "fog_color";
        public static readonly StringName HaloColor = "halo_color";
        public static readonly StringName HorizonColor = "horizon_color";
        public static readonly StringName LowColor = "low_color";
        public static readonly StringName MadeSky = "made_sky";
        public static readonly StringName MiddleColor = "middle_color";
        public static readonly StringName MoonDirection = "moon_direction";
        public static readonly StringName MoonGlareTexture = "moon_glare_texture";
        public static readonly StringName MoonTexture = "moon_texture";
        public static readonly StringName RoadStrength = "road_strength";
        public static readonly StringName ShallowAlpha = "shallow_alpha";
        public static readonly StringName ShallowColor = "shallow_color";
        public static readonly StringName SkyHorizon = "sky_horizon";
        public static readonly StringName SkyTop = "sky_top";
        public static readonly StringName SkyboxDay = "skybox_day";
        public static readonly StringName SkyboxWeight = "skybox_weight";
        public static readonly StringName SunColor = "sun_color";
        public static readonly StringName SunDirection = "sun_direction";
        public static readonly StringName SunGlareTexture = "sun_glare_texture";
        public static readonly StringName SunTexture = "sun_texture";
        public static readonly StringName TextureLight = "texture_light";
        public static readonly StringName TopColor = "top_color";
    }

    private readonly SkyState _state = new();
    private readonly ShaderMaterial _skyMaterial = new() { Shader = SkyShader };
    private readonly ConcurrentQueue<(ShaderMaterial Material, LiquidKind Kind)> _newLiquids = new();
    private readonly List<(ShaderMaterial Material, LiquidKind Kind)> _liquids = [];
    private readonly SkyClouds _clouds = new();
    private readonly byte[] _cloudPixels = new byte[SkyClouds.Size * SkyClouds.Size * 4];
    private readonly ImageTexture[] _cloudFrames = new ImageTexture[2];
    private readonly Dictionary<SkyboxModel, Skybox> _skyboxes = [];
    private ModelLibrary? _models;
    private Task? _cloudTask;
    private double _skyboxSeconds;
    private double _cloudSeconds;
    private double _sinceCloudFrame = double.MaxValue;
    private int _cloudTarget;

    private LightTable _lights = null!;
    private TileManager _tiles = null!;
    private Godot.Environment _environment = null!;
    private DirectionalLight3D _sun = null!;
    private int _mapId;
    private double _sinceUpdate = double.MaxValue;

    /// <summary>Half minutes since midnight.</summary>
    public double Ticks { get; set; } = 12 * 120;

    /// <summary>Which liquid the eyes are under, or null above it, which closes in the fog.</summary>
    public LiquidKind? Underwater { get; set; }

    public bool TimePasses { get; set; } = true;

    /// <summary>Whether it's day as far as zone music is concerned.</summary>
    public bool IsDay => Ticks >= SunPath.SunriseTicks && Ticks < SunPath.SunsetTicks;

    /// <summary>Asks for the sky to catch up now, after the time is set by hand.</summary>
    public void Refresh() => _sinceUpdate = double.MaxValue;

    public void Start(LightTable lights, TileManager tiles, Godot.Environment environment, DirectionalLight3D sun, int mapId, LiquidLibrary liquids)
    {
        _lights = lights;
        _tiles = tiles;
        _environment = environment;
        _sun = sun;
        _mapId = mapId;

        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits;
        _sun.DirectionalShadowMaxDistance = ShadowDistance;
        _sun.ShadowOpacity = ShadowOpacity;
        // For comparing frame times, without touching the player's saved setting.
        var noShadows = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-shadows") >= 0;
        _sun.ShadowEnabled = AppSettings.Shadows && !noShadows;
        var noOcclusion = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-ao") >= 0;
        _onSettings = () =>
        {
            _sun.ShadowEnabled = AppSettings.Shadows && !noShadows;
            _environment.SsaoEnabled = AppSettings.AmbientOcclusion && !noOcclusion;
            _environment.SsaoRadius = AppSettings.OcclusionSize;
            _environment.SsaoIntensity = AppSettings.OcclusionStrength;
        };
        AppSettings.Changed += _onSettings;

        liquids.MaterialCreated += (material, kind) => _newLiquids.Enqueue((material, kind));

        // Ambient comes from the tables, and no reflections means the sky's cubemap costs little.
        var sky = new Sky { SkyMaterial = _skyMaterial, RadianceSize = Sky.RadianceSizeEnum.Size32, ProcessMode = Sky.ProcessModeEnum.Incremental };
        environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        environment.Sky = sky;
        environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        environment.AmbientLightEnergy = 1f;
        environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;
        environment.FogEnabled = true;
        environment.FogMode = Godot.Environment.FogModeEnum.Depth;
        environment.FogDensity = 1f;
        environment.FogDepthCurve = 1f;
        environment.FogSkyAffect = 0f;
        environment.FogSunScatter = 0f;
        environment.FogLightEnergy = 1f;

        // A bit of contact shadow where things meet. Only lit surfaces take it, and light_affect
        // lets it show in sunlight too, not just in shade. The amounts are by eye.
        environment.SsaoRadius = AppSettings.OcclusionSize;
        environment.SsaoIntensity = AppSettings.OcclusionStrength;
        environment.SsaoLightAffect = OcclusionLightAffect;
        environment.SsaoEnabled = AppSettings.AmbientOcclusion && System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--no-ao") < 0;

        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            // --time=19:30 starts there and holds the time, to look at one moment.
            if (argument.StartsWith(TimeArgument) && TimeOnly.TryParse(argument[TimeArgument.Length..], CultureInfo.InvariantCulture, out var time))
            {
                Ticks = time.Hour * 120 + time.Minute * 2;
                TimePasses = false;
            }
        }
    }

    /// <summary>Sets the client's sun and moon on the sky.</summary>
    public void SetCelestialTextures(ModelLibrary textures)
    {
        _models = textures;
        _skyMaterial.SetShaderParameter(Param.SunTexture, textures.GetTexture(SunTexture));
        _skyMaterial.SetShaderParameter(Param.SunGlareTexture, textures.GetTexture(SunGlareTexture));
        _skyMaterial.SetShaderParameter(Param.MoonTexture, textures.GetTexture(MoonTexture));
        _skyMaterial.SetShaderParameter(Param.MoonGlareTexture, textures.GetTexture(MoonGlareTexture));
    }

    public override void _ExitTree() => AppSettings.Changed -= _onSettings;

    public override void _Process(double delta)
    {
        if (_lights is null)
        {
            return;
        }

        if (TimePasses)
        {
            Ticks = (Ticks + delta * LightTable.TicksPerDay / SecondsPerDay) % LightTable.TicksPerDay;
        }

        while (_newLiquids.TryDequeue(out var liquid))
        {
            _liquids.Add(liquid);
            ColorLiquid(liquid.Material, liquid.Kind);
        }

        AnimateSkyboxes(delta);
        _cloudSeconds += delta;
        _sinceCloudFrame += delta;
        _sinceUpdate += delta;
        if (_sinceUpdate < UpdateInterval)
        {
            return;
        }

        _sinceUpdate = 0;
        Apply();
    }

    private void Apply()
    {
        var feet = _tiles.PlayerWorldPosition;
        _lights.Sample(
            _mapId,
            new System.Numerics.Vector3((float)(feet.X + WowCoords.MapOrigin), (float)feet.Y, (float)(feet.Z + WowCoords.MapOrigin)),
            (float)Ticks,
            _state);

        var toSun = SunPath.Direction((float)Ticks);
        var sunDirection = new Vector3(toSun.X, toSun.Y, toSun.Z);

        // At night the same light is the moon, so it stays above the ground either way.
        var toLight = sunDirection.Y >= 0 ? sunDirection : new Vector3(-sunDirection.X, -sunDirection.Y, sunDirection.Z);
        toLight.Y = Mathf.Max(toLight.Y, MinLightHeight);
        toLight = toLight.Normalized();
        _sun.GlobalBasis = Basis.LookingAt(-toLight, Vector3.Forward);
        var diffuse = _state[SkyBand.Diffuse] * DiffuseIntensity;
        _sun.LightColor = new Color(diffuse.X, diffuse.Y, diffuse.Z);
        var sunLinear = _sun.LightColor.SrgbToLinear();
        // Inside is always under a roof, so it gets what a surface in shadow gets, like the rooms open to the sky beside it.
        var shadowed = 1f - ShadowOpacity;
        RenderingServer.GlobalShaderParameterSet("world_sun", new Vector3(sunLinear.R, sunLinear.G, sunLinear.B) * shadowed);
        RenderingServer.GlobalShaderParameterSet("world_to_sun", toLight);
        _sun.LightEnergy = 1f;

        _environment.AmbientLightColor = ToColor(SkyBand.Ambient);
        var ambient = ToColor(SkyBand.Ambient).SrgbToLinear();
        RenderingServer.GlobalShaderParameterSet("world_ambient", new Vector3(ambient.R, ambient.G, ambient.B));
        RenderingServer.GlobalShaderParameterSet("indoor_ambient", new Vector3(ambient.R, ambient.G, ambient.B));
        _environment.FogLightColor = ToColor(SkyBand.Fog);
        var fogEnd = _state.FogEnd * FogDistanceScale;
        _environment.FogDepthBegin = fogEnd * _state.FogStart;
        _environment.FogDepthEnd = Mathf.Max(fogEnd, fogEnd * _state.FogStart + 1f);
        _environment.FogSkyAffect = 0f;
        if (Underwater is { } liquid)
        {
            // How far one can see, in yards, by eye. The bands are what the water's surface is painted with.
            (_environment.FogLightColor, _environment.FogDepthBegin, _environment.FogDepthEnd) = liquid switch
            {
                LiquidKind.Ocean => (ToColor(SkyBand.OceanDeep), 30f, 220f),
                LiquidKind.Magma => (new Color(0.6f, 0.15f, 0.02f), 0f, 8f),
                LiquidKind.Slime => (new Color(0.15f, 0.3f, 0.05f), 0f, 15f),
                _ => (ToColor(SkyBand.RiverDeep), 25f, 180f),
            };
            _environment.FogSkyAffect = 1f;
        }

        RenderingServer.GlobalShaderParameterSet("fog_range", new Vector2(_environment.FogDepthBegin, _environment.FogDepthEnd));

        _skyMaterial.SetShaderParameter(Param.TopColor, ToColor(SkyBand.SkyTop));
        _skyMaterial.SetShaderParameter(Param.MiddleColor, ToColor(SkyBand.SkyMiddle));
        _skyMaterial.SetShaderParameter(Param.LowColor, ToColor(SkyBand.SkyLow));
        _skyMaterial.SetShaderParameter(Param.AboveHorizonColor, ToColor(SkyBand.SkyAboveHorizon));
        _skyMaterial.SetShaderParameter(Param.HorizonColor, ToColor(SkyBand.SkyHorizon));
        _skyMaterial.SetShaderParameter(Param.FogColor, ToColor(SkyBand.Fog));
        _skyMaterial.SetShaderParameter(Param.SunColor, ToColor(SkyBand.Sun));
        _skyMaterial.SetShaderParameter(Param.HaloColor, ToColor(SkyBand.SunHalo));
        _skyMaterial.SetShaderParameter(Param.SunDirection, sunDirection);
        _skyMaterial.SetShaderParameter(Param.MoonDirection, new Vector3(-sunDirection.X, -sunDirection.Y, sunDirection.Z));
        UpdateClouds(toSun);
        UpdateSkyboxes();

        foreach (var (material, kind) in _liquids)
        {
            ColorLiquid(material, kind);
        }
    }

    /// <summary>A skybox model, loading or in the scene.</summary>
    private sealed class Skybox
    {
        public required bool FollowsDay { get; init; }
        public Task<(ArrayMesh Mesh, (ShaderMaterial Material, M2Effect Effect)[] Materials)?>? Loading { get; set; }
        public MeshInstance3D? Node { get; set; }
        public (ShaderMaterial Material, M2Effect Effect)[] Materials { get; set; } = [];
    }

    /// <summary>Runs what's animated in the skyboxes showing.</summary>
    private void AnimateSkyboxes(double delta)
    {
        _skyboxSeconds += delta;
        var day = (float)(Ticks / LightTable.TicksPerDay);
        foreach (var skybox in _skyboxes.Values)
        {
            if (skybox.Node is not { Visible: true })
            {
                continue;
            }

            foreach (var (material, effect) in skybox.Materials)
            {
                if (effect.Animated)
                {
                    ModelLibrary.ApplyEffect(material, effect, skybox.FollowsDay ? day * effect.Period : _skyboxSeconds);
                }

                if (skybox.FollowsDay)
                {
                    material.SetShaderParameter(Param.SkyboxDay, day);
                }
            }
        }
    }

    /// <summary>Called at each sky update. Loads the models the lights here ask for, and fades them in and out with their weight.</summary>
    private void UpdateSkyboxes()
    {
        var hidesSky = false;
        if (!_skyboxes.ContainsKey(StarsModel) && _models is { } starModels)
        {
            _skyboxes[StarsModel] = new Skybox
            {
                FollowsDay = false,
                Loading = Task.Run<(ArrayMesh, (ShaderMaterial, M2Effect)[])?>(() => starModels.GetM2(StarsModel.Path) is { } asset ? starModels.SkyboxMeshOf(asset, behindClouds: true) : null),
            };
        }

        foreach (var (model, weight) in _state.Skyboxes)
        {
            if (!_skyboxes.ContainsKey(model) && _models is { } models)
            {
                _skyboxes[model] = new Skybox
                {
                    FollowsDay = model.FollowsDay,
                    Loading = Task.Run<(ArrayMesh, (ShaderMaterial, M2Effect)[])?>(() => models.GetM2(model.Path) is { } asset ? models.SkyboxMeshOf(asset) : null),
                };
            }

            // As the client does: the made sky is hidden only once a model fully covers it.
            hidesSky |= !model.KeepsSky && weight > 0.99f && _skyboxes[model].Node is not null;
        }

        foreach (var (model, skybox) in _skyboxes)
        {
            if (skybox.Loading is { IsCompleted: true } loading)
            {
                skybox.Loading = null;
                if (loading.IsCompletedSuccessfully && loading.Result is { } loaded)
                {
                    skybox.Materials = loaded.Materials;
                    skybox.Node = new MeshInstance3D { Mesh = loaded.Mesh, Name = "Skybox", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    AddChild(skybox.Node);
                }
            }

            if (skybox.Node is { } node)
            {
                var isStars = ReferenceEquals(model, StarsModel);
                var weight = isStars ? (hidesSky ? 0f : SunPath.StarBrightness((float)Ticks)) : _state.Skyboxes.GetValueOrDefault(model);
                node.Visible = weight > 0f;
                foreach (var (material, _) in skybox.Materials)
                {
                    material.SetShaderParameter(Param.SkyboxWeight, weight);
                    if (isStars)
                    {
                        var toSun = SunPath.Direction((float)Ticks);
                        material.SetShaderParameter(Param.MoonDirection, new Vector3(-toSun.X, -toSun.Y, toSun.Z));
                        material.SetShaderParameter(Param.MoonTexture, _models?.GetTexture(MoonTexture));
                    }

                    if (isStars && _cloudFrames[0] is not null)
                    {
                        material.SetShaderParameter(Param.CloudsFrom, _cloudFrames[1 - _cloudTarget]);
                        material.SetShaderParameter(Param.CloudsTo, _cloudFrames[_cloudTarget]);
                        material.SetShaderParameter(Param.CloudsFade, Mathf.Clamp((float)(_sinceCloudFrame / CloudFrameInterval), 0f, 1f));
                    }
                }
            }
        }

        _skyMaterial.SetShaderParameter(Param.MadeSky, hidesSky ? 0f : 1f);
    }

    /// <summary>Called at each sky update. Makes frames off the main thread, and shows each as it's ready.</summary>
    private void UpdateClouds(System.Numerics.Vector3 toSun)
    {
        if (_cloudTask is { IsCompleted: true } finished)
        {
            _cloudTask = null;
            if (finished.IsCompletedSuccessfully)
            {
                ShowCloudFrame();
            }
            else
            {
                GD.PushWarning($"Could not make clouds: {finished.Exception?.Message}");
            }
        }

        var first = _cloudFrames[0] is null;
        if (_cloudTask is null && (first || _sinceCloudFrame >= CloudFrameInterval))
        {
            var toLight = toSun.Y >= 0 ? toSun : new System.Numerics.Vector3(-toSun.X, -toSun.Y, toSun.Z);
            var look = new SkyClouds.Look(
                _state.Glow * CloudAmount,
                _state[SkyBand.SunHalo],
                _state[SkyBand.CloudShaded],
                _state[SkyBand.Cloud],
                SkyClouds.LightPosition(toLight));
            var time = (float)(_cloudSeconds * SkyClouds.UnitsPerSecond);
            _cloudTask = Task.Run(() => _clouds.Render(_cloudPixels, time, look));
        }

        if (!first)
        {
            _skyMaterial.SetShaderParameter(Param.CloudsFade, Mathf.Clamp((float)(_sinceCloudFrame / CloudFrameInterval), 0f, 1f));
        }
    }

    private void ShowCloudFrame()
    {
        var image = Image.CreateFromData(SkyClouds.Size, SkyClouds.Size, false, Image.Format.Rgba8, _cloudPixels);
        if (_cloudFrames[0] is null)
        {
            _cloudFrames[0] = ImageTexture.CreateFromImage(image);
            _cloudFrames[1] = ImageTexture.CreateFromImage(image);
        }
        else
        {
            _cloudTarget = 1 - _cloudTarget;
            _cloudFrames[_cloudTarget].Update(image);
        }

        _skyMaterial.SetShaderParameter(Param.CloudsFrom, _cloudFrames[1 - _cloudTarget]);
        _skyMaterial.SetShaderParameter(Param.CloudsTo, _cloudFrames[_cloudTarget]);
        _skyMaterial.SetShaderParameter(Param.CloudsFade, 0f);
        _sinceCloudFrame = 0;
    }

    private void ColorLiquid(ShaderMaterial material, LiquidKind kind)
    {
        var ocean = kind == LiquidKind.Ocean;
        material.SetShaderParameter(Param.ShallowColor, ToColor(ocean ? SkyBand.OceanShallow : SkyBand.RiverShallow));
        material.SetShaderParameter(Param.DeepColor, ToColor(ocean ? SkyBand.OceanDeep : SkyBand.RiverDeep));
        material.SetShaderParameter(Param.ShallowAlpha, ocean ? _state.OceanShallowAlpha : _state.RiverShallowAlpha);
        material.SetShaderParameter(Param.DeepAlpha, ocean ? _state.OceanDeepAlpha : _state.RiverDeepAlpha);
        material.SetShaderParameter(Param.SkyTop, ToColor(SkyBand.SkyMiddle));
        material.SetShaderParameter(Param.SkyHorizon, ToColor(SkyBand.SkyAboveHorizon));
        material.SetShaderParameter(Param.SunColor, ToColor(SkyBand.Sun));

        // At night the water shows a dimmer road of moonlight from the same light.
        var toSun = SunPath.Direction((float)Ticks);
        var night = toSun.Y < 0;
        material.SetShaderParameter(Param.SunDirection, night ? new Vector3(-toSun.X, -toSun.Y, toSun.Z) : new Vector3(toSun.X, toSun.Y, toSun.Z));
        material.SetShaderParameter(Param.RoadStrength, night ? MoonRoadStrength : 1f);
        material.SetShaderParameter(Param.TextureLight, ToColor(SkyBand.Ambient) + new Color(0.5f, 0.5f, 0.5f));
    }

    /// <summary>In sRGB, as Godot's colors are, because the tables hold the colors WoW shows on screen.</summary>
    private Color ToColor(SkyBand band)
    {
        var c = _state[band];
        return new Color(c.X, c.Y, c.Z);
    }
}
