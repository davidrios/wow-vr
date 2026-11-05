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

/// <summary>A place to start, in WoW coordinates. Yaw turns left from north, in degrees, and only the desktop player uses it.</summary>
public sealed record LastPlace(string Map, double WowX, double WowY, float WowZ, float Yaw, bool Flying);

/// <summary>An account on an AzerothCore server, and which of its characters to play, or the first.</summary>
public sealed record ServerLogin(string Host, string Account, string Password, string? Character);

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

    public const float MinInterfaceScale = 0.75f;
    public const float MaxInterfaceScale = 3f;

    /// <summary>
    /// Desktop only: how big the interface is drawn. Without a saved one, the screen's height over
    /// 1080, so it's the same size on a 4K screen as on 1080p.
    /// </summary>
    public static float InterfaceScale
    {
        get => Read(InterfaceSection, "scale", ScreenInterfaceScale()).AsSingle();
        set => WriteMovement(InterfaceSection, "scale", Mathf.Clamp(value, MinInterfaceScale, MaxInterfaceScale));
    }

    private static float ScreenInterfaceScale()
    {
        var height = DisplayServer.ScreenGetSize(DisplayServer.WindowGetCurrentScreen()).Y;
        return Mathf.Clamp(Mathf.Round(height / 1080f * 4f) / 4f, 1f, MaxInterfaceScale);
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

    public const float MinFieldOfView = 50f;
    public const float MaxFieldOfView = 100f;

    /// <summary>
    /// Desktop only, in degrees from the top of the view to the bottom. A wide window shows more
    /// across, and stretches the edges more. A headset's lenses fix its own.
    /// </summary>
    public static float FieldOfView
    {
        get => Read(VideoSection, "field_of_view", 75f).AsSingle(); // Godot's own default, which it was before.
        set => WriteMovement(VideoSection, "field_of_view", Mathf.Clamp(value, MinFieldOfView, MaxFieldOfView));
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

    private const string LastPlaceSection = "last_place";

    /// <summary>Where the player was when last seen, in WoW coordinates, to start there next time. Null before there's one.</summary>
    public static LastPlace? LastPlace
    {
        get
        {
            var map = Read(LastPlaceSection, "map", "").AsString();
            return map.Length == 0
                ? null
                : new LastPlace(
                    map,
                    Read(LastPlaceSection, "x", 0.0).AsDouble(),
                    Read(LastPlaceSection, "y", 0.0).AsDouble(),
                    Read(LastPlaceSection, "z", 0f).AsSingle(),
                    Read(LastPlaceSection, "yaw", 0f).AsSingle(),
                    Read(LastPlaceSection, "flying", false).AsBool());
        }
        set
        {
            if (value is not { } place || place == LastPlace)
            {
                return;
            }

            // Written together and saved once, without raising Changed, since nothing reacts to it.
            _batching = true;
            try
            {
                Write(LastPlaceSection, "map", place.Map);
                Write(LastPlaceSection, "x", place.WowX);
                Write(LastPlaceSection, "y", place.WowY);
                Write(LastPlaceSection, "z", place.WowZ);
                Write(LastPlaceSection, "yaw", place.Yaw);
                Write(LastPlaceSection, "flying", place.Flying);
            }
            finally
            {
                _batching = false;
            }

            Save();
        }
    }

    private const string LoginSection = "login";

    /// <summary>What the login screen fills in: the last server, account, realm and character. Never the password.</summary>
    public static string LoginHost
    {
        get => Read(LoginSection, "host", "").AsString();
        set => Write(LoginSection, "host", value);
    }

    public static string LoginAccount
    {
        get => Read(LoginSection, "account", "").AsString();
        set => Write(LoginSection, "account", value);
    }

    /// <summary>Whether the password is kept in the system's own store, which <c>CredentialStore</c> chooses.</summary>
    public static bool RememberPassword
    {
        get => Read(LoginSection, "remember_password", false).AsBool();
        set => Write(LoginSection, "remember_password", value);
    }

    public static string LastRealm
    {
        get => Read(LoginSection, "realm", "").AsString();
        set => Write(LoginSection, "realm", value);
    }

    public static string LastCharacter
    {
        get => Read(LoginSection, "character", "").AsString();
        set => Write(LoginSection, "character", value);
    }

    private const string HudSection = "hud";

    /// <summary>Where the player moved a piece of the HUD, in its own pixels, or null where it was never moved.</summary>
    public static Vector2? HudPosition(string name) =>
        Config.HasSectionKey(HudSection, name) ? Read(HudSection, name, Vector2.Zero).AsVector2() : null;

    public static void SetHudPosition(string name, Vector2 position) => Write(HudSection, name, position);

    private const string ServerSection = "server";

    /// <summary>
    /// The AzerothCore server to play on, from <c>[server]</c>'s host, account, password and
    /// character, or null without a host. The password is kept as written.
    /// </summary>
    public static ServerLogin? Server
    {
        get
        {
            var host = Read(ServerSection, "host", "").AsString();
            var character = Read(ServerSection, "character", "").AsString();
            return host.Length == 0
                ? null
                : new ServerLogin(host, Read(ServerSection, "account", "").AsString(), Read(ServerSection, "password", "").AsString(), character.Length > 0 ? character : null);
        }
    }

    /// <summary>What a preset puts in each video setting. The user's own changes after it stay.</summary>
    public static void ApplyPreset(VideoPreset preset)
    {
        var (scale, shadows, occlusion, grass, creatures, detail, particles) = preset switch
        {
            VideoPreset.Low => (0.7f, false, false, false, 70f, 0.4f, false),
            VideoPreset.Medium => (0.85f, true, false, true, 100f, 0.7f, true),
            _ => (1f, true, true, true, MaxCreatureRange, 1f, true),
        };

        _batching = true;
        try
        {
            RenderScale = scale;
            Shadows = shadows;
            AmbientOcclusion = occlusion;
            GroundCover = grass;
            CreatureRange = creatures;
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
