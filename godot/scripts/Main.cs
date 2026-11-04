using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using WowVR.Net;
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
    private const string ServerArgument = "--server=";
    private const string CharacterArgument = "--character=";
    private const string OfflineArgument = "--offline";
    private const string LoginArgument = "--login";
    private const string LoginCheckArgument = "--login-check";

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
            if (ShowsLogin())
            {
                ShowLogin();
            }
            else if (_carry is { } carry)
            {
                // The server moved the character to another map: the same session goes on there.
                _carry = null;
                _login = (carry.Auth, carry.Realms, carry.Host);
                BeginWorld(carry.Session, askServer: false);
            }
            else
            {
                BeginWorld(null, askServer: true);
            }
        }
    }

    /// <summary>
    /// The login screen comes first, unless the run says how to start: checks, tours, benchmarks,
    /// screenshots, <c>--start=</c>, <c>--server=</c> and <c>--offline</c> go straight to the world.
    /// <c>--login</c> shows it anyway.
    /// </summary>
    private static bool ShowsLogin()
    {
        if (_carry is not null)
        {
            return false;
        }

        if (_return is not null)
        {
            return true;
        }

        var arguments = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(arguments, LoginArgument) >= 0 || arguments.Any(a => a.StartsWith(LoginCheckArgument)))
        {
            return true;
        }

        return _nextStart is null && !IsTestRun && !arguments.Any(a =>
            a.StartsWith(StartArgument) || a.StartsWith(ServerArgument) || a == OfflineArgument);
    }

    private void ShowLogin()
    {
        var glue = new GlueScreens { Name = "Glue" };
        AddChild(glue);
        glue.Start((IPlayer)_player, _player);
        glue.Entered += session =>
        {
            _login = glue.Login;
            BeginWorld(session, askServer: false);
        };
        glue.Offline += () => BeginWorld(null, askServer: false);
        if (_return is { } back)
        {
            _return = null;
            if (back.Session is { } session)
            {
                glue.ResumeAt(session, back.Auth, back.Realms, back.Host);
            }
        }
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(LoginCheckArgument)) is { } check)
        {
            AddChild(new LoginCheck { Screens = glue, Character = check.Contains('=') ? check[(check.IndexOf('=') + 1)..] : null });
        }

        var clientPath = AppSettings.ClientPath;
        _ = Task.Run(() =>
        {
            var source = OpenSource(clientPath);
            var displays = CreatureDisplayTable.Read(source);
            var models = _uiModels ??= new ModelLibrary(source, new LiquidLibrary(source));
            var look = _voidLook ??= ReadVoidLook(source);
            return (new CreatureLibrary(source, displays, models), displays, source.Read(GlueFont), models, look);
        }).ContinueWith(opened =>
        {
            if (opened.IsCompletedSuccessfully)
            {
                Callable.From(() =>
                {
                    if (opened.Result.Item3 is { Length: > 0 } font)
                    {
                        GlueTheme.UseFont(new FontFile { Data = font });
                    }

                    if (IsInstanceValid(glue))
                    {
                        glue.SetCharacterData(opened.Result.Item1, opened.Result.Item2, opened.Result.Item4, opened.Result.Item5);
                    }
                }).CallDeferred();
            }
            else
            {
                GD.PushError($"Could not open the client for the character screen: {opened.Exception?.GetBaseException().Message}");
            }
        });
    }

    /// <summary>What the next load of this scene goes back to: the character list on a session, or the login screen.</summary>
    private sealed record Return(RealmSession? Session, AuthClient? Auth, List<Realm> Realms, string Host);

    private static Return? _return;

    /// <summary>A session going on to another map, which the next load of this scene enters.</summary>
    private sealed record Carry(RealmSession Session, AuthClient? Auth, List<Realm> Realms, string Host);

    private static Carry? _carry;

    /// <summary>The server moved the character to another map: the scene loads again with it there.</summary>
    private void ChangeMap(WorldChanged changed)
    {
        if (_server?.Release() is not { } session)
        {
            return;
        }

        SaveLastPlace();
        _carry = new Carry(session, _login.Auth, _login.Realms, _login.Host);
        ((IPlayer)_player).SetMenuOpen(false);
        BehindLoadingScreen("", () => GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene));
    }

    /// <summary>The login the world was entered from, to go back to.</summary>
    private (AuthClient? Auth, List<Realm> Realms, string Host) _login = (null, [], "");

    private ServerWorld? _server;
    private CancellationTokenSource? _logoutCancel;

    /// <summary>The game menu's own buttons: back to the characters or login, or out of the game.</summary>
    private void SetGameMenu(IPlayer player, RealmSession? session)
    {
        player.Menu.SetGameStatus("");
        player.Menu.SetGameActions(session is null
            ? [("Login screen", () => LeaveWorld(null, "Login")), ("Exit game", ExitGame)]
            : [("Character selection", () => _ = LogOutAsync(player, session, toCharacters: true)), ("Log out", () => _ = LogOutAsync(player, session, toCharacters: false)), ("Exit game", ExitGame)]);
    }

    /// <summary>
    /// Logs the character out, which the server does at once in an inn or city, or after 20 seconds
    /// that the menu counts down with a Cancel, then goes to the character list or login screen.
    /// </summary>
    private async Task LogOutAsync(IPlayer player, RealmSession session, bool toCharacters)
    {
        var menu = player.Menu;
        menu.SetGameStatus("Logging out…");
        LogoutAnswer answer;
        try
        {
            answer = await session.RequestLogoutAsync();
        }
        catch (System.IO.IOException e)
        {
            menu.SetGameStatus(e.Message, true);
            return;
        }

        if (answer.Refused is { } why)
        {
            menu.SetGameStatus(why, true);
            return;
        }

        using var cancel = new CancellationTokenSource();
        _logoutCancel = cancel;
        if (!answer.Instant)
        {
            menu.SetGameActions([("Cancel logout", () =>
            {
                session.CancelLogout();
                cancel.Cancel();
            })]);
            _ = CountDownAsync(menu, cancel.Token);
        }

        try
        {
            await session.WaitForLogoutAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            SetGameMenu(player, session);
            return;
        }
        catch (System.IO.IOException e)
        {
            menu.SetGameStatus(e.Message, true);
            return;
        }
        finally
        {
            _logoutCancel = null;
        }

        _server?.Release();
        if (toCharacters)
        {
            LeaveWorld(session, "Character selection");
        }
        else
        {
            session.Dispose();
            LeaveWorld(null, "Logging out");
        }
    }

    private static async Task CountDownAsync(WorldMenu menu, CancellationToken cancel)
    {
        for (var left = 20; left > 0 && !cancel.IsCancellationRequested; left--)
        {
            menu.SetGameStatus($"Logging out in {left} s");
            try
            {
                await Task.Delay(1000, cancel);
            }
            catch (OperationCanceledException)
            {
                menu.SetGameStatus("");
                return;
            }
        }
    }

    /// <summary>Reloads the scene into the character list on <paramref name="session"/>, or into the login screen.</summary>
    private void LeaveWorld(RealmSession? session, string title)
    {
        SaveLastPlace();
        _return = new Return(session, _login.Auth, _login.Realms, _login.Host);
        ((IPlayer)_player).SetMenuOpen(false);
        BehindLoadingScreen(title, () => GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene));
    }

    /// <summary>Quits, logging the character out on the way.</summary>
    private void ExitGame()
    {
        SaveLastPlace();
        GetTree().Quit();
    }

    private static readonly object OpenLock = new();

    /// <summary>Models for the screens before and between worlds: the void's sky and the stage. Kept across reloads like the archives.</summary>
    private static ModelLibrary? _uiModels;

    /// <summary>The sky and fog of the voids behind the screens, read once from the client.</summary>
    private static VoidLook? _voidLook;

    private static VoidLook ReadVoidLook(MpqAssetSource source)
    {
        try
        {
            return VoidLook.Hellfire(source);
        }
        catch (Exception e) when (e is System.IO.IOException or WowAlchemyException)
        {
            GD.PushWarning($"No Hellfire sky for the screens: {e.Message}");
            return VoidLook.Fallback;
        }
    }

    /// <summary>The game's own font for its menus, read from the player's client.</summary>
    private const string GlueFont = @"Fonts\FRIZQT__.TTF";

    /// <summary>The client's archives, opened once and kept, from whichever thread asks first.</summary>
    private static MpqAssetSource OpenSource(string clientPath)
    {
        lock (OpenLock)
        {
            return _openSource ??= MpqAssetSource.Open(clientPath);
        }
    }

    /// <summary>The loading screen, then the world, played on <paramref name="session"/> or from spawn files.</summary>
    private void BeginWorld(RealmSession? session, bool askServer)
    {
        var arguments = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(arguments, TourArgument) < 0 && Array.IndexOf(arguments, WalkTourArgument) < 0)
        {
            _loading = new LoadingScreen { Name = "LoadingScreen", Hold = Array.IndexOf(arguments, HoldLoadingArgument) >= 0 };
            AddChild(_loading);
        }

        if (_uiModels is not null && _voidLook is not null)
        {
            _loading?.SetSky(_uiModels, _voidLook);
        }

        _ = StartWorldAsync(AppSettings.ClientPath, session, askServer);
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
    private async Task StartWorldAsync(string clientPath, RealmSession? session, bool askServer)
    {
        try
        {
            var (startMap, startPosition, startPitch, startYaw, placement) = ReadStart();
            session ??= askServer ? await ConnectAsync() : null;
            var opening = await Task.Run(() =>
            {
                var opened = OpenSource(clientPath);
                var record = MapRecord.ReadAll(opened)
                    .First(m => session is null
                        ? m.Directory.Equals(startMap, StringComparison.OrdinalIgnoreCase)
                        : m.Id == session.MapId);
                return (Source: opened, Record: record, Picture: ReadLoadingPicture(opened, record));
            });

            // The server says where the character is, on its feet. A --start on the same map moves it
            // there instead, since the server follows where the player goes.
            var startGiven = OS.GetCmdlineUserArgs().Any(a => a.StartsWith(StartArgument));
            if (session is not null && !(startGiven && startMap.Equals(opening.Record.Directory, StringComparison.OrdinalIgnoreCase)))
            {
                if (startGiven)
                {
                    GD.PushWarning($"--start is ignored: {session.Character.Name} is on {opening.Record.Directory}, and only the server can change map.");
                }

                startPosition = session.StartPosition;
                (startPitch, startYaw, placement) = (0f, Mathf.RadToDeg(WowCoords.CameraYawFromOrientation(session.StartOrientation)), Placement.NearHeight);
            }

            startMap = opening.Record.Directory;
            _uiModels ??= new ModelLibrary(opening.Source, new LiquidLibrary(opening.Source));
            _voidLook ??= await Task.Run(() => ReadVoidLook(opening.Source));
            _loading?.SetSky(_uiModels, _voidLook);
            if (!GlueTheme.HasFont && opening.Source.Read(GlueFont) is { Length: > 0 } font)
            {
                GlueTheme.UseFont(new FontFile { Data = font });
            }
            _loading?.SetPlace(opening.Record.Name, opening.Picture is null ? null : ImageTexture.CreateFromImage(opening.Picture));

            var source = opening.Source;
            var mapId = opening.Record.Id;
            var (map, lights, zones, liquids, displays, footsteps) = await Task.Run(() =>
                (WdtMap.Read(source, startMap), LightTable.Read(source), ZoneTable.Read(source), new LiquidLibrary(source), CreatureDisplayTable.Read(source), FootstepTable.Read(source)));
            var (factions, triggers) = session is null ? ([], []) : await Task.Run(() =>
                (FactionTemplateRecord.ReadAll(source), AreaTriggerRecord.ReadAll(source).ToList()));
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
            // On a server, its own objects are drawn instead, as it shows them.
            tiles.Start(source, map, liquids, player, start.X, start.Z, session is null ? ChooseGameObjectSource(OS.GetCmdlineUserArgs(), mapId) : new NoGameObjects());
            sky.SetCelestialTextures(tiles.Models);

            var ambience = new ZoneAmbience { Name = "ZoneAmbience" };
            AddChild(ambience);
            ambience.Start(source, zones, tiles, sky);

            player.Menu.SetSky(sky);

            player.Menu.SetLandmarks(Landmarks.On(startMap), l => BehindLoadingScreen(l.Name, () => tiles.TeleportTo(l.WowX, l.WowY)));
            SetGameMenu(player, session);
            // On a server, only the server can move the character to another map.
            if (session is null)
            {
                player.Menu.SetContinents(startMap, TravelTo);
            }

            var arguments = OS.GetCmdlineUserArgs();
            var creatures = new CreatureManager { Name = "Creatures" };
            AddChild(creatures);
            var creatureSounds = new CreatureSoundPlayer { Name = "CreatureSounds" };
            AddChild(creatureSounds);
            creatureSounds.Start(source);
            var library = new CreatureLibrary(source, displays, tiles.Models);
            creatures.Start(library, tiles, session is null ? ChooseSpawnSource(arguments, displays, mapId, startPosition) : new NoSpawns(), creatureSounds, footsteps);
            if (session is not null)
            {
                var server = new ServerWorld { Name = "Server" };
                AddChild(server);
                var transports = new Transports { Name = "Transports" };
                AddChild(transports);
                transports.Start(tiles, player, _player, source, mapId);
                server.Transports = transports;
                var serverObjects = new ServerObjects { Name = "ServerObjects" };
                AddChild(serverObjects);
                serverObjects.Start(tiles, source, library);
                server.Objects = serverObjects;
                server.Start(session, creatures, tiles, displays, _player, factions, triggers, source);
                server.Library = library;
                server.WorldChanging += ChangeMap;
                _server = server;

                // The character's own body, as the character list showed it.
                var avatar = new PlayerAvatar { Name = "Avatar" };
                AddChild(avatar);
                avatar.Start(library, GlueScreens.AppearanceOf(session.Character), player, _player);
                avatar.Server = server;
                server.Avatar = avatar;
                AddChild(new SpellEffects { Name = "SpellEffects", World = server, Creatures = creatures, Library = library, Sounds = creatureSounds, Tiles = tiles });
                AddHud(server, new Interaction(server, creatures, tiles), tiles);
                if (Array.IndexOf(arguments, "--spell-check") >= 0)
                {
                    AddChild(new SpellCheck { Server = server });
                }

                if (arguments.Any(a => a.StartsWith("--play-check") || a.StartsWith("--ride-check")))
                {
                    AddChild(new PlayCheck { Server = server, Tiles = tiles, Player = _player, Creatures = creatures, Transports = transports, Objects = serverObjects });
                }
            }

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

            if (arguments.Any(a => a.StartsWith(BenchArgument)))
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

    /// <summary>
    /// The server session to play on, or null to play from spawn files. <c>--server=host</c> and
    /// <c>--character=</c> override settings.cfg's <c>[server]</c>, and <c>WOWVR_AC_USER</c> and
    /// <c>WOWVR_AC_PASS</c> its account. Checks and tours stay offline unless given <c>--server=</c>.
    /// </summary>
    private async Task<RealmSession?> ConnectAsync()
    {
        var arguments = OS.GetCmdlineUserArgs();
        var host = arguments.FirstOrDefault(a => a.StartsWith(ServerArgument))?[ServerArgument.Length..];
        var saved = AppSettings.Server;
        if (Array.IndexOf(arguments, OfflineArgument) >= 0 || (host is null && (saved is null || IsTestRun)))
        {
            return null;
        }

        host ??= saved!.Host;
        var account = System.Environment.GetEnvironmentVariable("WOWVR_AC_USER") is { Length: > 0 } user ? user : saved?.Account ?? "";
        var password = System.Environment.GetEnvironmentVariable("WOWVR_AC_PASS") is { Length: > 0 } pass ? pass : saved?.Password ?? "";
        var character = arguments.FirstOrDefault(a => a.StartsWith(CharacterArgument))?[CharacterArgument.Length..] ?? saved?.Character;
        if (account.Length == 0)
        {
            GD.PushError($"No account for {host}: set [server] account and password in settings.cfg, or WOWVR_AC_USER and WOWVR_AC_PASS. Playing offline.");
            return null;
        }

        _loading?.SetPlace($"Connecting to {host}", null);
        try
        {
            var session = await RealmSession.OpenAsync(host, account, password, character, message => GD.Print(message));
            GD.Print($"On {host} as {session.Character.Name}: map {session.MapId} at {session.StartPosition}");
            return session;
        }
        catch (Exception e) when (e is System.IO.IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            GD.PushError($"Could not play on {host}: {e.Message} Playing offline.");
            return null;
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

    /// <summary>
    /// The HUD over the world: on desktop a layer over the window, in VR a panel above the left
    /// wrist with a keyboard for chat. The desktop player clicks the world through it, and the
    /// VR player points at it with the right hand.
    /// </summary>
    private void AddHud(ServerWorld server, Interaction interaction, TileManager tiles)
    {
        var hud = new GameHud { Name = "Hud", Hidden = () => tiles.Covered };
        System.Numerics.Vector3 Here()
        {
            var (_, x, y, z) = tiles.PlayerLocation;
            return new System.Numerics.Vector3((float)x, (float)y, z);
        }

        var player = (IPlayer)_player;
        hud.MenuRequested += () => player.SetMenuOpen(true);
        var ground = new GroundTargeter
        {
            Name = "GroundTargeter",
            World = server,
            Tiles = tiles,
            Feet = () => player.Feet,
            Pointer = _player switch
            {
                DesktopPlayer d => () => d.PointerRay,
                Xr.XrPlayer x => () => x.PointerRay,
                _ => () => null,
            },
        };
        AddChild(ground);
        interaction.Ground = ground;
        if (_player is DesktopPlayer desktop)
        {
            var layer = new CanvasLayer { Name = "HudLayer" };
            AddChild(layer);
            layer.AddChild(hud);
            desktop.Hud = hud;
            desktop.Interaction = interaction;
        }
        else if (_player is Xr.XrPlayer xr)
        {
            xr.AddHud(hud, interaction);
        }

        hud.Bind(server, Here);
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
