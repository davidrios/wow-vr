using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Database;
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

    /// <summary>Stars fade in as the sun goes this far below the horizon.</summary>
    private const float FullStarsSunHeight = -0.25f;

    /// <summary>
    /// The tables' direct light is far too strong to use whole: Elwynn's noon is pure orange,
    /// meant to add to its blue ambient. The client scales it by an intensity from another file,
    /// which isn't read yet, so this is picked by eye.
    /// </summary>
    private const float DiffuseIntensity = 0.5f;

    /// <summary>Multiplies the tables' fog distance. At 1, fog is where the client's tables put it.</summary>
    private const float FogDistanceScale = 1f;

    private const float MoonRoadStrength = 0.35f;

    private const string TimeArgument = "--time=";
    private const float OcclusionLightAffect = 0.7f;

    private Action? _onSettings;

    /// <summary>Yards from the camera that shadows reach, which is most of what they cost.</summary>
    private const float ShadowDistance = 150f;

    /// <summary>The tables' ambient is blue, so full shadow turns the ground blue. Picked by eye.</summary>
    private const float ShadowOpacity = 0.75f;

    private const string SunTexture = @"textures\SunCenter.blp";
    private const string SunGlareTexture = @"textures\SunGlare.blp";
    private const string MoonTexture = @"textures\moon.blp";
    private const string MoonGlareTexture = @"textures\moonglare.blp";

    private static readonly Shader SkyShader = GD.Load<Shader>("res://shaders/sky.gdshader");

    private readonly SkyState _state = new();
    private readonly ShaderMaterial _skyMaterial = new() { Shader = SkyShader };
    private readonly ConcurrentQueue<(ShaderMaterial Material, LiquidKind Kind)> _newLiquids = new();
    private readonly List<(ShaderMaterial Material, LiquidKind Kind)> _liquids = [];

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
        _skyMaterial.SetShaderParameter("sun_texture", textures.GetTexture(SunTexture));
        _skyMaterial.SetShaderParameter("sun_glare_texture", textures.GetTexture(SunGlareTexture));
        _skyMaterial.SetShaderParameter("moon_texture", textures.GetTexture(MoonTexture));
        _skyMaterial.SetShaderParameter("moon_glare_texture", textures.GetTexture(MoonGlareTexture));
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
        RenderingServer.GlobalShaderParameterSet("world_sun", new Vector3(diffuse.X, diffuse.Y, diffuse.Z));
        RenderingServer.GlobalShaderParameterSet("world_to_sun", toLight);
        _sun.LightEnergy = 1f;

        _environment.AmbientLightColor = ToColor(SkyBand.Ambient);
        var ambientGamma = ToColor(SkyBand.Ambient);
        RenderingServer.GlobalShaderParameterSet("world_ambient", new Vector3(ambientGamma.R, ambientGamma.G, ambientGamma.B));
        var ambient = ToColor(SkyBand.Ambient).SrgbToLinear();
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
                LiquidKind.Ocean => (ToColor(SkyBand.OceanDeep), 0f, 60f),
                LiquidKind.Magma => (new Color(0.6f, 0.15f, 0.02f), 0f, 8f),
                LiquidKind.Slime => (new Color(0.15f, 0.3f, 0.05f), 0f, 15f),
                _ => (ToColor(SkyBand.RiverDeep), 0f, 45f),
            };
            _environment.FogSkyAffect = 1f;
        }

        RenderingServer.GlobalShaderParameterSet("fog_range", new Vector2(_environment.FogDepthBegin, _environment.FogDepthEnd));

        _skyMaterial.SetShaderParameter("top_color", ToColor(SkyBand.SkyTop));
        _skyMaterial.SetShaderParameter("middle_color", ToColor(SkyBand.SkyMiddle));
        _skyMaterial.SetShaderParameter("low_color", ToColor(SkyBand.SkyLow));
        _skyMaterial.SetShaderParameter("above_horizon_color", ToColor(SkyBand.SkyAboveHorizon));
        _skyMaterial.SetShaderParameter("horizon_color", ToColor(SkyBand.SkyHorizon));
        _skyMaterial.SetShaderParameter("fog_color", ToColor(SkyBand.Fog));
        _skyMaterial.SetShaderParameter("sun_color", ToColor(SkyBand.Sun));
        _skyMaterial.SetShaderParameter("halo_color", ToColor(SkyBand.SunHalo));
        _skyMaterial.SetShaderParameter("sun_direction", sunDirection);
        _skyMaterial.SetShaderParameter("moon_direction", new Vector3(-sunDirection.X, -sunDirection.Y, sunDirection.Z));
        _skyMaterial.SetShaderParameter("star_brightness", Mathf.Clamp(sunDirection.Y / FullStarsSunHeight, 0f, 1f));

        foreach (var (material, kind) in _liquids)
        {
            ColorLiquid(material, kind);
        }
    }

    private void ColorLiquid(ShaderMaterial material, LiquidKind kind)
    {
        var ocean = kind == LiquidKind.Ocean;
        material.SetShaderParameter("shallow_color", ToColor(ocean ? SkyBand.OceanShallow : SkyBand.RiverShallow));
        material.SetShaderParameter("deep_color", ToColor(ocean ? SkyBand.OceanDeep : SkyBand.RiverDeep));
        material.SetShaderParameter("shallow_alpha", ocean ? _state.OceanShallowAlpha : _state.RiverShallowAlpha);
        material.SetShaderParameter("deep_alpha", ocean ? _state.OceanDeepAlpha : _state.RiverDeepAlpha);
        material.SetShaderParameter("sky_top", ToColor(SkyBand.SkyMiddle));
        material.SetShaderParameter("sky_horizon", ToColor(SkyBand.SkyAboveHorizon));
        material.SetShaderParameter("sun_color", ToColor(SkyBand.Sun));

        // At night the water shows a dimmer road of moonlight from the same light.
        var toSun = SunPath.Direction((float)Ticks);
        var night = toSun.Y < 0;
        material.SetShaderParameter("sun_direction", night ? new Vector3(-toSun.X, -toSun.Y, toSun.Z) : new Vector3(toSun.X, toSun.Y, toSun.Z));
        material.SetShaderParameter("road_strength", night ? MoonRoadStrength : 1f);
        material.SetShaderParameter("texture_light", ToColor(SkyBand.Ambient) + new Color(0.5f, 0.5f, 0.5f));
    }

    /// <summary>In sRGB, as Godot's colors are, because the tables hold the colors WoW shows on screen.</summary>
    private Color ToColor(SkyBand band)
    {
        var c = _state[band];
        return new Color(c.X, c.Y, c.Z);
    }
}
