using System;
using Godot;

namespace WowVR;

public enum PlayerScale
{
    /// <summary>One yard in the world is one real yard. WoW's buildings feel huge.</summary>
    TrueScale,

    /// <summary>Bigger, so doors and furniture feel the way they do to a WoW character.</summary>
    PlayerSized,
}

public enum VideoPreset
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Player settings, stored in user://settings.cfg. For the client path, the
/// WOWVR_CLIENT_PATH environment variable overrides the stored value.
/// </summary>
public static class AppSettings
{
    private const string DefaultFilePath = "user://settings.cfg";
    private const string SettingsArgument = "--settings=";

    /// <summary><c>--settings=file</c> reads and saves another file, to compare settings without touching the player's own.</summary>
    private static readonly string FilePath =
        Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith(SettingsArgument))?[SettingsArgument.Length..] ?? DefaultFilePath;
    private const string ClientSection = "client";
    private const string MovementSection = "movement";
    private const string AudioSection = "audio";
    private const string InterfaceSection = "interface";
    private const string VideoSection = "video";
    private const string PathKey = "path";
    private const string EnvironmentVariable = "WOWVR_CLIENT_PATH";

    private const float YardsPerMeter = 1.0936f;

    /// <summary>A starting point for playtesting, not a measurement.</summary>
    private const float PlayerSizedFactor = 1.4f;

    public const float MinFlySpeed = 10f;
    public const float MaxFlySpeed = 200f;

    /// <summary>A WoW character's running speed, in yards per second.</summary>
    public const float WalkSpeed = 7f;

    /// <summary>Raised when a movement setting changes.</summary>
    public static event Action? Changed;

    public static string ClientPath
    {
        get
        {
            var fromEnvironment = System.Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            return Read(ClientSection, PathKey, "").AsString();
        }
    }

    public static void SaveClientPath(string path) => Write(ClientSection, PathKey, path);

    public static float FlySpeed
    {
        get => Read(MovementSection, "fly_speed", 30f).AsSingle();
        set => WriteMovement("fly_speed", Mathf.Clamp(value, MinFlySpeed, MaxFlySpeed));
    }

    public static PlayerScale Scale
    {
        get => (PlayerScale)Read(MovementSection, "scale", (int)PlayerScale.TrueScale).AsInt32();
        set => WriteMovement("scale", (int)value);
    }

    public static bool SmoothTurn
    {
        get => Read(MovementSection, "smooth_turn", false).AsBool();
        set => WriteMovement("smooth_turn", value);
    }

    /// <summary>How far the comfort vignette closes in, from 0 (off) to 1.</summary>
    public static float VignetteSize
    {
        get => Read(MovementSection, "vignette_size", 0.65f).AsSingle();
        set => WriteMovement("vignette_size", Mathf.Clamp(value, 0f, 1f));
    }

    public static bool Shadows
    {
        get => Read(MovementSection, "shadows", true).AsBool();
        set => WriteMovement("shadows", value);
    }

    /// <summary>Desktop only: draws as fast as it can, rather than waiting for the monitor. A headset paces itself.</summary>
    public static bool UnlockFps
    {
        get => Read(MovementSection, "unlock_fps", false).AsBool();
        set
        {
            WriteMovement("unlock_fps", value);
            ApplyFpsLimit();
        }
    }

    public static void ApplyFpsLimit()
    {
        if (XRServer.PrimaryInterface?.IsInitialized() != true)
        {
            DisplayServer.WindowSetVsyncMode(UnlockFps ? DisplayServer.VSyncMode.Disabled : DisplayServer.VSyncMode.Enabled);
        }
    }

    public const float MaxOcclusionStrength = 16f;
    public const float MinOcclusionSize = 0.1f;
    public const float MaxOcclusionSize = 4f;

    public static float OcclusionStrength
    {
        get => Read(MovementSection, "occlusion_strength", 4.75f).AsSingle();
        set => WriteMovement("occlusion_strength", Mathf.Clamp(value, 0f, MaxOcclusionStrength));
    }

    /// <summary>In yards.</summary>
    public static float OcclusionSize
    {
        get => Read(MovementSection, "occlusion_size", 0.3f).AsSingle();
        set => WriteMovement("occlusion_size", Mathf.Clamp(value, MinOcclusionSize, MaxOcclusionSize));
    }

    public static bool AmbientOcclusion
    {
        get => Read(MovementSection, "ambient_occlusion", true).AsBool();
        set => WriteMovement("ambient_occlusion", value);
    }

    public static bool FpsDisplay
    {
        get => Read(MovementSection, "fps_display", true).AsBool();
        set => WriteMovement("fps_display", value);
    }

    public static bool GroundCover
    {
        get => Read(MovementSection, "ground_cover", true).AsBool();
        set => WriteMovement("ground_cover", value);
    }

    private static bool? _namePlates;

    /// <summary>Kept in memory because creatures read it every frame.</summary>
    public static bool NamePlates
    {
        get => _namePlates ??= Read(InterfaceSection, "name_plates", true).AsBool();
        set
        {
            _namePlates = value;
            Write(InterfaceSection, "name_plates", value);
        }
    }

    private static float? _musicVolume;
    private static float? _ambienceVolume;

    /// <summary>From 0 to 1. Kept in memory because sound fades read it every frame.</summary>
    public static float MusicVolume
    {
        get => _musicVolume ??= Read(AudioSection, "music", 0.6f).AsSingle();
        set
        {
            _musicVolume = Mathf.Clamp(value, 0f, 1f);
            Write(AudioSection, "music", _musicVolume.Value);
        }
    }

    /// <summary>From 0 to 1. Kept in memory because sound fades read it every frame.</summary>
    public static float AmbienceVolume
    {
        get => _ambienceVolume ??= Read(AudioSection, "ambience", 0.8f).AsSingle();
        set
        {
            _ambienceVolume = Mathf.Clamp(value, 0f, 1f);
            Write(AudioSection, "ambience", _ambienceVolume.Value);
        }
    }

    /// <summary>World units per real meter, for the current scale.</summary>
    public static float WorldScale =>
        Scale == PlayerScale.PlayerSized ? YardsPerMeter * PlayerSizedFactor : YardsPerMeter;

    public const float MinRenderScale = 0.5f;

    /// <summary>
    /// The share of the screen's resolution the world is drawn at, upscaled with FSR below 1.
    /// Only the GPU's work depends on it.
    /// </summary>
    public static float RenderScale
    {
        get => Read(VideoSection, "render_scale", 1f).AsSingle();
        set => WriteMovement(VideoSection, "render_scale", Mathf.Clamp(value, MinRenderScale, 1f));
    }

    public const float MinCreatureRange = 50f;
    public const float MaxCreatureRange = 150f;

    /// <summary>Yards from the player that creatures show.</summary>
    public static float CreatureRange
    {
        get => Read(VideoSection, "creature_range", MaxCreatureRange).AsSingle();
        set => WriteMovement(VideoSection, "creature_range", Mathf.Clamp(value, MinCreatureRange, MaxCreatureRange));
    }

    public const int MinAnimatedCreatures = 10;
    public const int MaxAnimatedCreatures = 60;

    /// <summary>How many of the nearest creatures move and animate. Further ones stand still, and cost little.</summary>
    public static int AnimatedCreatures
    {
        get => Read(VideoSection, "animated_creatures", MaxAnimatedCreatures).AsInt32();
        set => WriteMovement(VideoSection, "animated_creatures", Mathf.Clamp(value, MinAnimatedCreatures, MaxAnimatedCreatures));
    }

    public const float MinDetailDistance = 0.3f;

    /// <summary>Multiplies how far away doodads show, from <see cref="MinDetailDistance"/> to 1, the full range.</summary>
    public static float DetailDistance
    {
        get => Read(VideoSection, "detail_distance", 1f).AsSingle();
        set => WriteMovement(VideoSection, "detail_distance", Mathf.Clamp(value, MinDetailDistance, 1f));
    }

    /// <summary>
    /// Animates every creature every frame. Off, those further than 25 yards update 12 to 30 times a
    /// second, which in a city is about 2.5 ms a frame less.
    /// </summary>
    public static bool FullRateAnimation
    {
        get => Read(VideoSection, "full_rate_animation", false).AsBool();
        set => WriteMovement(VideoSection, "full_rate_animation", value);
    }

    /// <summary>Doodads' particles: torches' flames, smoke and the like. Creatures and objects keep theirs.</summary>
    public static bool Particles
    {
        get => Read(VideoSection, "particles", true).AsBool();
        set => WriteMovement(VideoSection, "particles", value);
    }

    /// <summary>What a preset puts in each video setting. The user's own changes after it stay.</summary>
    public static void ApplyPreset(VideoPreset preset)
    {
        var (scale, shadows, occlusion, grass, creatures, animated, detail, particles) = preset switch
        {
            VideoPreset.Low => (0.7f, false, false, false, 70f, 15, 0.4f, false),
            VideoPreset.Medium => (0.85f, true, false, true, 100f, 30, 0.7f, true),
            _ => (1f, true, true, true, MaxCreatureRange, MaxAnimatedCreatures, 1f, true),
        };

        _batching = true;
        try
        {
            RenderScale = scale;
            Shadows = shadows;
            AmbientOcclusion = occlusion;
            GroundCover = grass;
            CreatureRange = creatures;
            AnimatedCreatures = animated;
            DetailDistance = detail;
            Particles = particles;

            // Off even on High, which it would take below 180 FPS in cities on a fast PC.
            FullRateAnimation = false;
        }
        finally
        {
            _batching = false;
        }

        Save();
        Changed?.Invoke();
    }

    private static ConfigFile? _config;
    private static bool _batching;

    /// <summary>Read once and kept, because some settings are read several times a second.</summary>
    private static ConfigFile Config
    {
        get
        {
            if (_config is null)
            {
                _config = new ConfigFile();
                _config.Load(FilePath); // A missing file is fine: it's made on the first save.
            }

            return _config;
        }
    }

    private static Variant Read(string section, string key, Variant fallback) => Config.GetValue(section, key, fallback);

    private static void WriteMovement(string key, Variant value) => WriteMovement(MovementSection, key, value);

    private static void WriteMovement(string section, string key, Variant value)
    {
        Write(section, key, value);
        if (!_batching)
        {
            Changed?.Invoke();
        }
    }

    private static void Write(string section, string key, Variant value)
    {
        Config.SetValue(section, key, value);
        if (!_batching)
        {
            Save();
        }
    }

    private static void Save()
    {
        var result = Config.Save(FilePath);
        if (result != Error.Ok)
        {
            GD.PushError($"Could not save {FilePath}: {result}");
        }
    }
}
