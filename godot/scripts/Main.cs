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
using WowVR.Formats.Textures;
using WowVR.Formats.World;
using WowVR.Ui;
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
    private const string LoadingCheckArgument = "--loading-check";
    private const string HoldLoadingArgument = "--hold-loading";
    private const string BenchArgument = "--bench";

    /// <summary>Kept open across reloads, because tile and model workers can still be reading when the old world goes.</summary>
    private static MpqAssetSource? _openSource;

    /// <summary>Where the next load of this scene starts, after a continent's button.</summary>
    private static Landmark? _nextStart;

    private Node3D _player = null!;

    /// <summary>Null on tours, whose frame times need the world streaming in as it does when walking.</summary>
    private LoadingScreen? _loading;

    /// <summary>How the player is put at the start: on the highest surface, flying where they are, or on the floor at about their height.</summary>
    private enum Placement
    {
        Surface,
        Flying,
        NearHeight,
    }

    /// <summary>How often the place the player is in is saved, in seconds. It's saved on quitting too.</summary>
    private const double LastPlaceSeconds = 5;

    /// <summary>Checks, tours and benchmarks start where they say, or in Goldshire, and leave no place behind.</summary>
    private static readonly string[] TestArguments = ["--tour", "-check", BenchArgument, ScreenshotArgument, SampleSpawnsArgument, HoldLoadingArgument];

    private static bool IsTestRun => OS.GetCmdlineUserArgs().Any(a => TestArguments.Any(a.Contains));

    private TileManager? _tiles;
    private double _sinceLastPlace;

    /// <summary>
    /// <c>--start=map,x,y,z[,pitch[,yaw]]</c> gives a map, WoW position and view in degrees, and
    /// starts flying so the view stays put. Yaw turns left from north. Without it, the player starts
    /// where they last were, or in Goldshire the first time and in checks.
    /// </summary>
    private static (string Map, System.Numerics.Vector3 Position, float Pitch, float Yaw, Placement Placement) ReadStart()
    {
        if (_nextStart is { } landmark)
        {
            return (landmark.Map, new System.Numerics.Vector3(landmark.WowX, landmark.WowY, 0f), 0f, 0f, Placement.Surface);
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
                    Placement.Flying);
            }
        }

        if (!IsTestRun && AppSettings.LastPlace is { } last)
        {
            return (last.Map, new System.Numerics.Vector3((float)last.WowX, (float)last.WowY, last.WowZ), 0f, last.Yaw, last.Flying ? Placement.Flying : Placement.NearHeight);
        }

        return ("Azeroth", new System.Numerics.Vector3(-9464f, 62f, 57f), 0f, 0f, Placement.Surface);
    }

    public override void _Process(double delta)
    {
        _sinceLastPlace += delta;
        if (_sinceLastPlace >= LastPlaceSeconds)
        {
            _sinceLastPlace = 0;
            SaveLastPlace();
        }
    }

    /// <summary>Remembers where the player is, once they're standing or flying somewhere rather than being placed.</summary>
    private void SaveLastPlace()
    {
        if (_tiles is null || IsTestRun || !IsInstanceValid(_tiles) || !_player.IsInsideTree() || _tiles.PlacingPlayer)
        {
            return;
        }

        var player = (IPlayer)_player;
        var (map, x, y, z) = _tiles.PlayerLocation;
        var yaw = _player is DesktopPlayer ? _player.RotationDegrees.Y : 0f;
        AppSettings.LastPlace = new LastPlace(map, Math.Round(x, 2), Math.Round(y, 2), MathF.Round(z, 2), MathF.Round(yaw, 1), player.Flying);
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
        AppSettings.Changed += ApplyRenderScale;
        ApplyRenderScale();

        if (AppSettings.ClientPath.Length > 0)
        {
            var arguments = OS.GetCmdlineUserArgs();
            if (Array.IndexOf(arguments, TourArgument) < 0 && Array.IndexOf(arguments, WalkTourArgument) < 0)
            {
                _loading = new LoadingScreen { Name = "LoadingScreen", Hold = Array.IndexOf(arguments, HoldLoadingArgument) >= 0 };
                AddChild(_loading);
            }

            _ = StartWorldAsync(AppSettings.ClientPath);
        }
    }

    public override void _ExitTree() => AppSettings.Changed -= ApplyRenderScale;

    public override void _Notification(int what)
    {
        // Leaving the tree is too late: the player has left it first, and has no position by then.
        if (what == NotificationWMCloseRequest)
        {
            SaveLastPlace();
        }
    }

    /// <summary>
    /// Draws the world at less than the screen's resolution, which only the GPU feels. Desktop
    /// upscales with FSR. VR scales each eye bilinearly: with no anti-aliasing, FSR's sharpening
    /// makes edges crawl, differently in each eye, and the headset resamples the image again anyway.
    /// </summary>
    private void ApplyRenderScale()
    {
        var scale = AppSettings.RenderScale;
        var viewport = GetViewport();
        viewport.Scaling3DMode = scale < 1f && !viewport.UseXR ? Viewport.Scaling3DModeEnum.Fsr : Viewport.Scaling3DModeEnum.Bilinear;
        viewport.Scaling3DScale = scale;
    }

    /// <summary>The grey floor stays up until the client is open, or if it can't be opened.</summary>
    private async Task StartWorldAsync(string clientPath)
    {
        try
        {
            var (startMap, startPosition, startPitch, startYaw, placement) = ReadStart();
            var opening = await Task.Run(() =>
            {
                var opened = _openSource ??= MpqAssetSource.Open(clientPath);
                var record = MapRecord.ReadAll(opened)
                    .First(m => m.Directory.Equals(startMap, StringComparison.OrdinalIgnoreCase));
                return (Source: opened, Record: record, Picture: ReadLoadingPicture(opened, record));
            });
            _loading?.SetPlace(opening.Record.Name, opening.Picture is null ? null : ImageTexture.CreateFromImage(opening.Picture));

            var source = opening.Source;
            var mapId = opening.Record.Id;
            var (map, lights, zones, liquids, displays, footsteps) = await Task.Run(() =>
                (WdtMap.Read(source, startMap), LightTable.Read(source), ZoneTable.Read(source), new LiquidLibrary(source), CreatureDisplayTable.Read(source), FootstepTable.Read(source)));
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

            // Only when it's on, so that a saved setting of off doesn't undo --disable-vsync.
            if (AppSettings.UnlockFps)
            {
                AppSettings.ApplyFpsLimit();
            }

            var tiles = new TileManager { Name = "Tiles" };
            var sky = new SkyController { Name = "Sky" };
            AddChild(sky);
            sky.Start(lights, tiles, GetNode<WorldEnvironment>("WorldEnvironment").Environment, GetNode<DirectionalLight3D>("Sun"), mapId, liquids);

            AddChild(tiles);
            _tiles = tiles;
            tiles.Start(source, map, liquids, player, start.X, start.Z, ChooseGameObjectSource(OS.GetCmdlineUserArgs(), mapId));
            sky.SetCelestialTextures(tiles.Models);

            var ambience = new ZoneAmbience { Name = "ZoneAmbience" };
            AddChild(ambience);
            ambience.Start(source, zones, tiles, sky);

            player.Menu.SetSky(sky);

            player.Menu.SetLandmarks(Landmarks.On(startMap), l => BehindLoadingScreen(l.Name, () => tiles.TeleportTo(l.WowX, l.WowY)));
            player.Menu.SetContinents(startMap, TravelTo);

            var arguments = OS.GetCmdlineUserArgs();
            var creatures = new CreatureManager { Name = "Creatures" };
            AddChild(creatures);
            var creatureSounds = new CreatureSoundPlayer { Name = "CreatureSounds" };
            AddChild(creatureSounds);
            creatureSounds.Start(source);
            creatures.Start(new CreatureLibrary(source, displays, tiles.Models), tiles, ChooseSpawnSource(arguments, displays, mapId, startPosition), creatureSounds, footsteps);
            _loading?.Watch(tiles, creatures, player);

            var groundCover = new GroundCoverManager { Name = "GroundCover" };
            AddChild(groundCover);
            groundCover.Start(GroundEffectTable.Read(source), tiles);

            var water = new PlayerWater { Name = "PlayerWater" };
            AddChild(water);
            water.Start(source, tiles, player, sky, groundCover);
            if (Array.IndexOf(arguments, "--water-check") >= 0)
            {
                AddChild(new WaterCheck { Tiles = tiles, Player = player, Water = water });
            }

            var walkTour = Array.IndexOf(arguments, WalkTourArgument) >= 0;
            var flyTour = Array.IndexOf(arguments, TourArgument) >= 0;
            if (placement == Placement.Flying || flyTour)
            {
                player.Flying = true;
            }
            else
            {
                tiles.PlacePlayerOnSurface(placement == Placement.NearHeight ? start.Y : null);
            }

            if (Array.IndexOf(arguments, ContinentCheckArgument) >= 0)
            {
                AddChild(new ContinentCheck { Tiles = tiles, Player = player, Map = startMap });
            }

            // --open-menu=video opens it at the video settings.
            var openMenu = arguments.FirstOrDefault(a => a.StartsWith(OpenMenuArgument));
            if (openMenu is not null)
            {
                player.SetMenuOpen(true);
                player.Menu.ShowVideoPage(openMenu == OpenMenuArgument + "=video");
            }

            if (Array.IndexOf(arguments, MovementCheckArgument) >= 0)
            {
                AddChild(new MovementCheck { Tiles = tiles, Player = player });
            }

            foreach (var argument in arguments)
            {
                if (argument.StartsWith(ScreenshotArgument))
                {
                    AddChild(new ScreenshotTaker { Tiles = tiles, Creatures = creatures, Loading = _loading, Path = argument[ScreenshotArgument.Length..] });
                }
            }

            if (_loading is not null && Array.IndexOf(arguments, LoadingCheckArgument) >= 0)
            {
                AddChild(new LoadingCheck { Tiles = tiles, Player = player, Creatures = creatures, Screen = _loading });
            }

            if (Array.IndexOf(arguments, CreatureCheckArgument) >= 0)
            {
                AddChild(new CreatureCheck { Tiles = tiles, Creatures = creatures });
            }

            if (Array.IndexOf(arguments, BenchArgument) >= 0)
            {
                AddChild(new Benchmark { Tiles = tiles, Player = _player, Creatures = creatures, Loading = _loading });
            }

            if (walkTour || flyTour)
            {
                AddChild(new WorldTour { Tiles = tiles, Player = player, Walk = walkTour });
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Could not open the client at {clientPath}: {e.Message}");
            _loading?.Dismiss();
        }
    }

    /// <summary>The picture the game itself shows for a map, or null without one.</summary>
    private static Image? ReadLoadingPicture(MpqAssetSource source, MapRecord map)
    {
        try
        {
            var path = LoadingScreenRecord.ReadAll(source).FirstOrDefault(s => s.Id == map.LoadingScreenId)?.Path;
            return path is not null && source.Read(path) is { Length: > 0 } data
                ? TextureConverter.ToImage(BlpDecoder.Decode(data))
                : null;
        }
        catch (Exception e) when (e is System.IO.IOException or WowAlchemyException)
        {
            GD.PushWarning($"No loading picture for {map.Directory}: {e.Message}");
            return null;
        }
    }

    private void BehindLoadingScreen(string title, Action action)
    {
        if (_loading is null)
        {
            action();
        }
        else
        {
            _loading.Cover(title, action);
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
        SaveLastPlace();
        _nextStart = Landmarks.On(continent.Map).First();
        BehindLoadingScreen(continent.Name, () => GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene));
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
