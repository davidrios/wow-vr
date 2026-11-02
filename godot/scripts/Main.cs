using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Debug;
using WowVR.Desktop;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using WowVR.World;
using Godot;

namespace WowVR;

public partial class Main : Node3D
{
    private const string ClientPathArgument = "--client-path=";
    private const string TourArgument = "--tour";
    private const string WalkTourArgument = "--tour=walk";
    private const string XrPlayerArgument = "--xr-player";
    private const string MovementCheckArgument = "--movement-check";
    private const string OpenMenuArgument = "--open-menu";
    private const string StartArgument = "--start=";
    private const string ScreenshotArgument = "--screenshot=";
    private const string SpawnsArgument = "--spawns=";
    private const string SampleSpawnsArgument = "--sample-spawns";
    private const string CreatureCheckArgument = "--creature-check";

    /// <summary>Where tools/spawn-exporter writes, next to the Godot project, when running from a checkout.</summary>
    private const string ProjectSpawnFolder = "res://../spawns";

    /// <summary>Where a player's exported spawn files go otherwise.</summary>
    private const string DefaultSpawnFolder = "user://spawns";

    private const string ContinentCheckArgument = "--continent-check";

    /// <summary>Kept open across reloads, because tile and model workers can still be reading when the old world goes.</summary>
    private static MpqAssetSource? _openSource;

    /// <summary>Where the next load of this scene starts, after a continent's button.</summary>
    private static Landmark? _nextStart;

    private Node3D _player = null!;

    /// <summary>
    /// Standing in Goldshire, unless <c>--start=map,x,y,z[,pitch[,yaw]]</c> gives another map,
    /// WoW position and view in degrees, which starts flying so the view stays put. Yaw turns
    /// left from north.
    /// </summary>
    private static (string Map, System.Numerics.Vector3 Position, float Pitch, float Yaw, bool Explicit) ReadStart()
    {
        if (_nextStart is { } landmark)
        {
            return (landmark.Map, new System.Numerics.Vector3(landmark.WowX, landmark.WowY, 0f), 0f, 0f, false);
        }

        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(StartArgument))
            {
                var parts = argument[StartArgument.Length..].Split(',');
                return (parts[0], new System.Numerics.Vector3(
                    float.Parse(parts[1], CultureInfo.InvariantCulture),
                    float.Parse(parts[2], CultureInfo.InvariantCulture),
                    float.Parse(parts[3], CultureInfo.InvariantCulture)),
                    parts.Length > 4 ? float.Parse(parts[4], CultureInfo.InvariantCulture) : 0f,
                    parts.Length > 5 ? float.Parse(parts[5], CultureInfo.InvariantCulture) : 0f,
                    true);
            }
        }

        return ("Azeroth", new System.Numerics.Vector3(-9464f, 62f, 57f), 0f, 0f, false);
    }

    public override void _Ready()
    {
        // Loading makes a lot of garbage on other threads, and a blocking collection stops every frame for tens of milliseconds.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

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
            var (startMap, startPosition, startPitch, startYaw, explicitStart) = ReadStart();
            var (source, map, lights, zones, liquids, mapId, displays, footsteps) = await Task.Run(() =>
            {
                var opened = _openSource ??= MpqAssetSource.Open(clientPath);
                var id = MapRecord.ReadAll(opened)
                    .First(m => m.Directory.Equals(startMap, StringComparison.OrdinalIgnoreCase)).Id;
                return (opened, WdtMap.Read(opened, startMap), LightTable.Read(opened), ZoneTable.Read(opened), new LiquidLibrary(opened), id, CreatureDisplayTable.Read(opened), FootstepTable.Read(opened));
            });
            GD.Print($"Client opened: {map.TileCountWithData} tiles in {startMap}");

            GetNode("Floor").QueueFree();

            var start = WowCoords.ToGodot(startPosition);
            var player = (IPlayer)_player;
            player.PlaceAt(new Vector3(0, start.Y, 0));
            if (_player is DesktopPlayer desktop)
            {
                desktop.PitchDegrees = startPitch;
                desktop.RotationDegrees = new Vector3(0, startYaw, 0);
            }

            var tiles = new TileManager { Name = "Tiles" };
            var sky = new SkyController { Name = "Sky" };
            AddChild(sky);
            sky.Start(lights, tiles, GetNode<WorldEnvironment>("WorldEnvironment").Environment, GetNode<DirectionalLight3D>("Sun"), mapId, liquids);

            AddChild(tiles);
            tiles.Start(source, map, liquids, player, start.X, start.Z, ChooseGameObjectSource(OS.GetCmdlineUserArgs(), mapId));
            sky.SetCelestialTextures(tiles.Models);

            var ambience = new ZoneAmbience { Name = "ZoneAmbience" };
            AddChild(ambience);
            ambience.Start(source, zones, tiles, sky);

            player.Menu.SetSky(sky);

            player.Menu.SetLandmarks(Landmarks.On(startMap), l => tiles.TeleportTo(l.WowX, l.WowY));
            player.Menu.SetContinents(startMap, TravelTo);

            var arguments = OS.GetCmdlineUserArgs();
            var creatures = new CreatureManager { Name = "Creatures" };
            AddChild(creatures);
            var creatureSounds = new CreatureSoundPlayer { Name = "CreatureSounds" };
            AddChild(creatureSounds);
            creatureSounds.Start(source);
            creatures.Start(new CreatureLibrary(source, displays, tiles.Models), tiles, ChooseSpawnSource(arguments, displays, mapId, startPosition), creatureSounds, footsteps);

            var groundCover = new GroundCoverManager { Name = "GroundCover" };
            AddChild(groundCover);
            groundCover.Start(GroundEffectTable.Read(source), tiles);

            var water = new PlayerWater { Name = "PlayerWater" };
            AddChild(water);
            water.Start(source, tiles, player, sky);
            if (Array.IndexOf(arguments, "--water-check") >= 0)
            {
                AddChild(new WaterCheck { Tiles = tiles, Player = player, Water = water });
            }

            var walkTour = Array.IndexOf(arguments, WalkTourArgument) >= 0;
            var flyTour = Array.IndexOf(arguments, TourArgument) >= 0;
            if (explicitStart || flyTour)
            {
                player.Flying = true;
            }
            else
            {
                tiles.PlacePlayerOnSurface();
            }

            if (Array.IndexOf(arguments, ContinentCheckArgument) >= 0)
            {
                AddChild(new ContinentCheck { Tiles = tiles, Player = player, Map = startMap });
            }

            if (Array.IndexOf(arguments, OpenMenuArgument) >= 0)
            {
                player.SetMenuOpen(true);
            }

            if (Array.IndexOf(arguments, MovementCheckArgument) >= 0)
            {
                AddChild(new MovementCheck { Tiles = tiles, Player = player });
            }

            foreach (var argument in arguments)
            {
                if (argument.StartsWith(ScreenshotArgument))
                {
                    AddChild(new ScreenshotTaker { Tiles = tiles, Creatures = creatures, Path = argument[ScreenshotArgument.Length..] });
                }
            }

            if (Array.IndexOf(arguments, CreatureCheckArgument) >= 0)
            {
                AddChild(new CreatureCheck { Tiles = tiles, Creatures = creatures });
            }

            if (walkTour || flyTour)
            {
                AddChild(new WorldTour { Tiles = tiles, Player = player, Walk = walkTour });
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Could not open the client at {clientPath}: {e.Message}");
        }
    }

    /// <summary>
    /// Files exported from the database, or <c>--sample-spawns[=count]</c>'s made-up creatures around
    /// the start, which need no database.
    /// </summary>
    private static ISpawnSource ChooseSpawnSource(string[] arguments, CreatureDisplayTable displays, int mapId, System.Numerics.Vector3 start)
    {
        foreach (var argument in arguments)
        {
            if (argument.StartsWith(SampleSpawnsArgument) || argument == CreatureCheckArgument)
            {
                var count = argument.StartsWith(SampleSpawnsArgument + "=")
                    ? int.Parse(argument[(SampleSpawnsArgument.Length + 1)..], CultureInfo.InvariantCulture)
                    : SampleSpawns.DefaultCount;
                return new SampleSpawns(displays, start.X, start.Y, count);
            }
        }

        var folder = SpawnFolder(arguments);
        GD.Print($"Spawn files: {folder}");
        return new SpawnFiles(folder, mapId);
    }

    /// <summary>The same files' objects. Sample spawns stand in for creatures alone, so they come without any.</summary>
    private static IGameObjectSource ChooseGameObjectSource(string[] arguments, int mapId) =>
        arguments.Any(a => a.StartsWith(SampleSpawnsArgument) || a == CreatureCheckArgument)
            ? new NoGameObjects()
            : new GameObjectFiles(SpawnFolder(arguments), mapId);

    private static string SpawnFolder(string[] arguments)
    {
        var folder = arguments.FirstOrDefault(a => a.StartsWith(SpawnsArgument))?[SpawnsArgument.Length..];
        if (folder is not null)
        {
            return folder;
        }

        var project = ProjectSettings.GlobalizePath(ProjectSpawnFolder);
        return System.IO.Directory.Exists(project) ? project : ProjectSettings.GlobalizePath(DefaultSpawnFolder);
    }

    /// <summary>Another map needs other tiles, sky and spawns throughout, so the whole world reloads.</summary>
    private void TravelTo(Continent continent)
    {
        _nextStart = Landmarks.On(continent.Map).First();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private static Node3D LoadPlayer()
    {
        var vr = XRServer.FindInterface("OpenXR")?.IsInitialized() == true;
        GD.Print($"Mode: {(vr ? "VR" : "desktop")}");

        // Checks the VR player's scene and physics without a headset.
        var xrScene = vr || Array.IndexOf(OS.GetCmdlineUserArgs(), XrPlayerArgument) >= 0;
        var scene = xrScene ? "res://scenes/XRPlayer.tscn" : "res://scenes/DesktopPlayer.tscn";
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
