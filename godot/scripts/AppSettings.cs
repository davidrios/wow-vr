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

/// <summary>
/// Player settings, stored in user://settings.cfg. For the client path, the
/// WOWVR_CLIENT_PATH environment variable overrides the stored value.
/// </summary>
public static class AppSettings
{
    private const string FilePath = "user://settings.cfg";
    private const string ClientSection = "client";
    private const string MovementSection = "movement";
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
        get => (PlayerScale)Read(MovementSection, "scale", (int)PlayerScale.PlayerSized).AsInt32();
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

    /// <summary>World units per real meter, for the current scale.</summary>
    public static float WorldScale =>
        Scale == PlayerScale.PlayerSized ? YardsPerMeter * PlayerSizedFactor : YardsPerMeter;

    private static Variant Read(string section, string key, Variant fallback)
    {
        var config = new ConfigFile();
        return config.Load(FilePath) == Error.Ok ? config.GetValue(section, key, fallback) : fallback;
    }

    private static void WriteMovement(string key, Variant value)
    {
        Write(MovementSection, key, value);
        Changed?.Invoke();
    }

    private static void Write(string section, string key, Variant value)
    {
        var config = new ConfigFile();
        config.Load(FilePath); // A missing file is fine: we create it below.
        config.SetValue(section, key, value);

        var result = config.Save(FilePath);
        if (result != Error.Ok)
        {
            GD.PushError($"Could not save {FilePath}: {result}");
        }
    }
}
