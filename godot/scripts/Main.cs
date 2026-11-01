using System;
using System.Globalization;
using System.Threading.Tasks;
using WowVR.Debug;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Terrain;
using WowVR.World;
using Godot;

namespace WowVR;

public partial class Main : Node3D
{
    private const string ClientPathArgument = "--client-path=";
    private const string TourArgument = "--tour";
    private const string StartArgument = "--start=";

    private Node3D _player = null!;

    /// <summary>Goldshire, unless <c>--start=map,x,y,z[,pitch]</c> gives another map, WoW position and camera pitch in degrees.</summary>
    private static (string Map, System.Numerics.Vector3 Position, float Pitch) ReadStart()
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(StartArgument))
            {
                var parts = argument[StartArgument.Length..].Split(',');
                return (parts[0], new System.Numerics.Vector3(
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture)),
                    parts.Length > 4 ? float.Parse(parts[4], CultureInfo.InvariantCulture) : 0f);
            }
        }

        return ("Azeroth", new System.Numerics.Vector3(-9464f, 62f, 57f), 0f);
    }

    public override void _Ready()
    {
        ApplyClientPathArgument();
        LogClientPath();

        _player = LoadPlayer();
        AddChild(_player);
        GetViewport().GetCamera3D()?.AddChild(new FpsDisplay());

        if (AppSettings.ClientPath.Length > 0)
        {
            _ = StartWorldAsync(AppSettings.ClientPath);
        }
    }

    /// <summary>The grey floor stays up until the client is open, or if it can't be opened.</summary>
    private async Task StartWorldAsync(string clientPath)
    {
        try
        {
            var (startMap, startPosition, startPitch) = ReadStart();
            var (source, map) = await Task.Run(() =>
            {
                var opened = MpqAssetSource.Open(clientPath);
                return (opened, WdtMap.Read(opened, startMap));
            });
            GD.Print($"Client opened: {map.TileCountWithData} tiles in {startMap}");

            GetNode("Floor").QueueFree();

            var start = WowCoords.ToGodot(startPosition);
            _player.GlobalPosition += new Vector3(0, start.Y, 0);
            if (_player is Camera3D)
            {
                _player.RotationDegrees = new Vector3(startPitch, 0, 0);
            }

            var tiles = new TileManager { Name = "Tiles" };
            AddChild(tiles);
            tiles.Start(source, map, _player, start.X, start.Z);

            if (Array.IndexOf(OS.GetCmdlineUserArgs(), TourArgument) >= 0)
            {
                AddChild(new WorldTour { Tiles = tiles, Player = _player });
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Could not open the client at {clientPath}: {e.Message}");
        }
    }

    private static Node3D LoadPlayer()
    {
        var vr = XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
        GD.Print($"Mode: {(vr ? "VR" : "desktop")}");

        var scene = vr ? "res://scenes/XRPlayer.tscn" : "res://scenes/DesktopPlayer.tscn";
        return GD.Load<PackedScene>(scene).Instantiate<Node3D>();
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
