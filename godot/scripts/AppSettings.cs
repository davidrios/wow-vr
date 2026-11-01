using Godot;

namespace WowVR;

/// <summary>
/// Player settings, stored in user://settings.cfg. For the client path, the
/// WOWVR_CLIENT_PATH environment variable overrides the stored value.
/// </summary>
public static class AppSettings
{
    private const string FilePath = "user://settings.cfg";
    private const string Section = "client";
    private const string PathKey = "path";
    private const string EnvironmentVariable = "WOWVR_CLIENT_PATH";

    public static string ClientPath
    {
        get
        {
            var fromEnvironment = System.Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }

            var config = new ConfigFile();
            return config.Load(FilePath) == Error.Ok
                ? config.GetValue(Section, PathKey, "").AsString()
                : "";
        }
    }

    public static void SaveClientPath(string path)
    {
        var config = new ConfigFile();
        config.Load(FilePath); // A missing file is fine: we create it below.
        config.SetValue(Section, PathKey, path);

        var result = config.Save(FilePath);
        if (result != Error.Ok)
        {
            GD.PushError($"Could not save {FilePath}: {result}");
        }
    }
}
