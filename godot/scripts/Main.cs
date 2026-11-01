using Godot;

namespace AzerothVR;

public partial class Main : Node3D
{
    private const string ClientPathArgument = "--client-path=";

    public override void _Ready()
    {
        ApplyClientPathArgument();
        LogClientPath();
        AddChild(LoadPlayer());
    }

    private static Node LoadPlayer()
    {
        var vr = XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
        GD.Print($"Mode: {(vr ? "VR" : "desktop")}");

        var scene = vr ? "res://scenes/XRPlayer.tscn" : "res://scenes/DesktopPlayer.tscn";
        return GD.Load<PackedScene>(scene).Instantiate();
    }

    private static void ApplyClientPathArgument()
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(ClientPathArgument))
            {
                AppSettings.SaveClientPath(argument[ClientPathArgument.Length..]);
            }
        }
    }

    private static void LogClientPath()
    {
        var clientPath = AppSettings.ClientPath;
        if (clientPath.Length == 0)
        {
            GD.Print("Client path: not set");
        }
        else
        {
            var found = System.IO.Directory.Exists(System.IO.Path.Combine(clientPath, "Data"));
            GD.Print($"Client path: {clientPath} (Data folder {(found ? "found" : "NOT found")})");
        }
    }
}
