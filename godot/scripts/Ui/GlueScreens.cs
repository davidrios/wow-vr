using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats.World;
using WowVR.Net;
using WowVR.Desktop;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The screens before the world: log in, pick a realm, then pick, make or delete a character and
/// enter the world with it, which hands the open session to <see cref="Entered"/>. The chosen
/// character stands on a small stage. On desktop the panel is on screen beside them; in VR it
/// floats in front of the player, as VRChat's menus do, with XR Tools' keyboard under it while a
/// text field has focus.
/// </summary>
public partial class GlueScreens : Node3D
{
    /// <summary>XR Tools' pointer collides with this layer, as the wrist menu's does.</summary>
    private const uint PointerLayer = 5242880;

    /// <summary>In VR, the panel's width in meters, and how far in front of the eyes and below them it floats.</summary>
    private const float PanelMeters = 0.62f;
    private const float PanelDistance = 0.75f;
    private const float PanelDrop = 0.12f;
    private const float PanelLeft = 0.38f;

    /// <summary>Where the character stands, and where the viewer is: VR's feet, and desktop's camera.</summary>
    private static readonly Vector3 CharacterSpot = new(0, 0, 0);
    private static readonly Vector3 ViewerSpot = new(0, 0, 2.6f);
    private const float DesktopPanelScale = 0.5f;
    private static readonly Vector3 DesktopCamera = new(-0.75f, 1.25f, 3.3f);
    private static readonly Vector3 DesktopLookAt = new(-0.75f, 1.0f, 0f);

    private GluePanel _panel = null!;
    private IPlayer _player = null!;
    private Node3D _playerNode = null!;
    private CreatureLibrary? _library;
    private CreatureDisplayTable? _displays;
    private CredentialStore _credentials = null!;
    private Node3D? _vrPanel;
    private Node3D? _keyboard;
    private CanvasLayer? _desktopLayer;
    private Camera3D? _camera;
    private CreatureNode? _preview;
    private MeshInstance3D? _platform;
    private int _previewVersion;
    private float _previewYaw;

    private string _host = "";
    private AuthClient? _auth;
    private List<Realm> _realms = [];
    private RealmSession? _session;
    private List<CharacterInfo> _characters = [];

    /// <summary>The session is in the world with this character.</summary>
    public event Action<RealmSession>? Entered;

    /// <summary>The player chose to explore without a server.</summary>
    public event Action? Offline;

    public GluePanel Panel => _panel;

    public void Start(IPlayer player, Node3D playerNode)
    {
        _player = player;
        _playerNode = playerNode;
        _credentials = CredentialStore.ForThisSystem(ProjectSettings.GlobalizePath("user://credentials"));
        _hiddenOverlays = [.. GetViewport().GetCamera3D()?.GetChildren().OfType<Debug.FpsDisplay>() ?? []];
        foreach (var overlay in _hiddenOverlays)
        {
            overlay.Visible = false;
        }

        _player.SetUiMode(true);
        _player.PlaceAt(ViewerSpot);

        var light = new OmniLight3D { Position = new Vector3(0.6f, 2.4f, 1.6f), OmniRange = 8, LightEnergy = 1.6f };
        AddChild(light);
        _platform = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 1f, Height = 0.1f },
            Position = CharacterSpot + new Vector3(0, -0.05f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.14f, 0.18f), Roughness = 0.6f },

            // Only if the planetarium's floor can't be had.
            Visible = false,
        };
        AddChild(_platform);
        if (GetParent().GetNodeOrNull<Node3D>("Floor") is { } grey)
        {
            grey.Visible = false;
        }

        if (GetParent().GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment is { } environment)
        {
            environment.BackgroundColor = new Color(0.01f, 0.01f, 0.03f);
            environment.AmbientLightColor = new Color(0.55f, 0.5f, 0.7f);
        }

        if (playerNode is DesktopPlayer)
        {
            _camera = new Camera3D { Position = DesktopCamera, Current = true, Fov = 50 };
            AddChild(_camera);
            _camera.LookAt(DesktopLookAt);
            _desktopLayer = new CanvasLayer();
            AddChild(_desktopLayer);
            _panel = GD.Load<PackedScene>("res://scenes/GluePanel.tscn").Instantiate<GluePanel>();
            _desktopLayer.AddChild(_panel);

            // The panel is laid out at the size VR's viewport draws it, which on a desktop is right
            // for a 4K screen. Half of it is right for 1080p, and the interface scale does the rest.
            _panel.Scale = Vector2.One * DesktopPanelScale;
            _panel.Position = new Vector2(24, 20);
        }
        else
        {
            _vrPanel = GD.Load<PackedScene>("res://addons/godot-xr-tools/objects/viewport_2d_in_3d.tscn").Instantiate<Node3D>();
            _vrPanel.Set("screen_size", new Vector2(PanelMeters, PanelMeters * GluePanel.Height / GluePanel.Width));
            _vrPanel.Set("viewport_size", new Vector2(GluePanel.Width, GluePanel.Height));
            _vrPanel.Set("collision_layer", PointerLayer);
            _vrPanel.Set("scene", GD.Load<PackedScene>("res://scenes/GluePanel.tscn"));
            AddChild(_vrPanel);
            _vrPanel.Set("unshaded", true);
            _panel = _vrPanel.Call("get_scene_instance").As<GluePanel>();

            _keyboard = GD.Load<PackedScene>("res://addons/godot-xr-tools/objects/virtual_keyboard.tscn").Instantiate<Node3D>();
            _keyboard.Set("collision_layer", PointerLayer);
            _keyboard.Visible = false;
            AddChild(_keyboard);
            _keyboard.Scale = Vector3.One * 0.45f;

            // Placed once the headset has said where the head is.
            GetTree().CreateTimer(0.5).Timeout += PlaceVrPanel;
        }

        Connect();
        ShowLogin();
    }

    /// <summary>What a login gave, for coming back to the character list or realms from the world.</summary>
    public (AuthClient? Auth, List<Realm> Realms, string Host) Login => (_auth, _realms, _host);

    /// <summary>Back from the world to the character list, on the session the character logged out of.</summary>
    public void ResumeAt(RealmSession session, AuthClient? auth, List<Realm> realms, string host)
    {
        (_session, _auth, _realms, _host) = (session, auth, realms, host);
        _ = ResumeAsync();
    }

    private async Task ResumeAsync()
    {
        _panel.ShowBusy("Back to the character list…");
        await RefreshCharactersAsync(null);
    }

    private const string StageFloor = @"World\Expansion02\Doodads\Ulduar\UL_SkyRoom_Floor.M2";
    private const float StageFloorRadius = 2.2f;

    /// <summary>The client's tables, once open, for drawing characters and the void around them. Until then the stage is empty.</summary>
    public void SetCharacterData(CreatureLibrary library, CreatureDisplayTable displays, ModelLibrary models, VoidLook look)
    {
        _library = library;
        _displays = displays;
        BuildCosmos(models, look);
        if (_panel.Page == "characters" && _selected is { } selected)
        {
            Preview(AppearanceOf(selected));
        }
    }

    private CharacterInfo? _selected;

    /// <summary>Hellfire Peninsula's sky around, its fog below, and the planetarium's floor underfoot.</summary>
    private void BuildCosmos(ModelLibrary models, VoidLook look)
    {
        var sky = new CosmicSky { Name = "Sky" };
        AddChild(sky);
        sky.Load(models, look);
        if (GetParent().GetNodeOrNull<WorldEnvironment>("WorldEnvironment")?.Environment is { } environment)
        {
            environment.BackgroundColor = look.Fog;
        }

        Task.Run(() => models.GetM2(StageFloor)).ContinueWith(loaded => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !loaded.IsCompletedSuccessfully)
            {
                return;
            }

            var floor = loaded.Result;
            if (floor is null)
            {
                _platform?.Show();
            }
            else
            {
                AddChild(new MeshInstance3D
                {
                    Name = "Floor",
                    Mesh = floor.Mesh,
                    Position = CharacterSpot,
                    Scale = Vector3.One * (StageFloorRadius / Mathf.Max(floor.Radius, 0.01f)),
                });
                _platform?.QueueFree();
            }
        }).CallDeferred());
    }

    private void Connect()
    {
        _panel.LoginRequested += (host, account, password, remember) => _ = LogInAsync(host, account, password, remember);
        _panel.OfflineRequested += () => Finish(() => Offline?.Invoke());
        _panel.RealmChosen += realm => _ = OpenRealmAsync(realm);
        _panel.LogoutRequested += () =>
        {
            CloseSession();
            _auth = null;
            ShowLogin();
        };
        _panel.ChangeRealmRequested += () =>
        {
            CloseSession();
            if (_auth is null || _realms.Count == 0)
            {
                // Entered with --server=, so there's no login to list the realms from.
                ShowLogin();
                return;
            }

            _panel.ShowRealms(_realms);
        };
        _panel.CharacterSelected += c =>
        {
            _selected = c;
            AppSettings.LastCharacter = c.Name;
            Preview(AppearanceOf(c));
        };
        _panel.EnterRequested += c => _ = EnterAsync(c);
        _panel.DeleteRequested += c => _ = DeleteAsync(c);
        _panel.CreatePageOpened += () =>
        {
            if (_displays is null)
            {
                _panel.SetStatus("Still opening the game files…");
                return;
            }

            _panel.ShowCreate((race, sex, deathKnight) => _displays.ChoicesFor(race, sex, deathKnight));
        };
        _panel.CreationChanged += c =>
        {
            // Dressed as the class starts out, as the game's own screen shows them.
            var (gear, types) = _displays?.StartOutfit(c.Race, c.Class, c.Sex) ?? (new int[(int)EquipmentSlot.Count], new int[(int)EquipmentSlot.Count]);
            Preview(new CharacterAppearance(c.Race, c.Sex, c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair, gear) { InventoryTypes = types });
        };
        _panel.CreateCancelled += ShowCharacters;
        _panel.CreateRequested += c => _ = CreateAsync(c);
        _panel.TextFocusChanged += focused =>
        {
            if (_keyboard is not null)
            {
                _keyboard.Visible = focused;
                _keyboard.Set("enabled", focused);
            }
        };
    }

    private static string CredentialKey(string host, string account) => $"{host.ToLowerInvariant()}/{account.ToUpperInvariant()}";

    private void ShowLogin(string? error = null)
    {
        var host = AppSettings.LoginHost;
        var account = AppSettings.LoginAccount;
        var remember = AppSettings.RememberPassword;
        string? password = null;
        if (remember && _credentials.IsAvailable && host.Length > 0 && account.Length > 0)
        {
            try
            {
                password = _credentials.Load(CredentialKey(host, account));
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                GD.PushWarning($"Could not read the remembered password: {e.Message}");
            }
        }

        _panel.ShowLogin(host, account, password, remember, _credentials.IsAvailable ? _credentials.Name : null, error);
        Preview(null);
    }

    private async Task LogInAsync(string host, string account, string password, bool remember)
    {
        _panel.ShowBusy($"Logging in to {host}…");
        try
        {
            (_auth, _realms) = await AuthClient.LoginAsync(host, 3724, account, password);
        }
        catch (IOException e)
        {
            ShowLogin(e.Message);
            return;
        }

        _host = host;
        AppSettings.LoginHost = host;
        AppSettings.LoginAccount = account;
        AppSettings.RememberPassword = remember;
        try
        {
            if (remember)
            {
                _credentials.Save(CredentialKey(host, account), password);
            }
            else
            {
                _credentials.Delete(CredentialKey(host, account));
            }
        }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            GD.PushWarning($"Could not keep the password in {_credentials.Name}: {e.Message}");
        }

        if (_realms.Count == 0)
        {
            ShowLogin($"{host} lists no realms.");
            return;
        }

        // One realm, or the last one used, needn't be asked about.
        var last = _realms.Find(r => r.Name == AppSettings.LastRealm && !r.Offline);
        if (_realms.Count == 1 || last is not null)
        {
            await OpenRealmAsync(last ?? _realms[0]);
        }
        else
        {
            _panel.ShowRealms(_realms);
        }
    }

    private async Task OpenRealmAsync(Realm realm)
    {
        if (_auth is null)
        {
            ShowLogin();
            return;
        }

        _panel.ShowBusy($"Connecting to {realm.Name}…");
        try
        {
            CloseSession();
            _session = await RealmSession.ConnectAsync(_host, _auth, realm, message => GD.Print(message));
            AppSettings.LastRealm = realm.Name;
            _characters = await _session.ListCharactersAsync();
            ShowCharacters();
        }
        catch (IOException e)
        {
            CloseSession();
            _panel.ShowRealms(_realms);
            _panel.SetStatus(e.Message, true);
        }
    }

    private void ShowCharacters()
    {
        if (_session is null)
        {
            return;
        }

        _panel.ShowCharacters(_session.Realm.Name, _characters, AppSettings.LastCharacter);
        if (_characters.Count == 0)
        {
            Preview(null);
        }
    }

    private async Task RefreshCharactersAsync(string? select, string? error = null)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _characters = await _session.ListCharactersAsync();
            if (select is not null)
            {
                AppSettings.LastCharacter = select;
            }

            ShowCharacters();
            if (error is not null)
            {
                _panel.SetStatus(error, true);
            }
        }
        catch (IOException e)
        {
            CloseSession();
            ShowLogin(e.Message);
        }
    }

    private async Task CreateAsync(CharacterCreation creation)
    {
        if (_session is null)
        {
            return;
        }

        _panel.ShowBusy($"Making {creation.Name}…");
        string? problem;
        try
        {
            problem = await _session.CreateCharacterAsync(creation);
        }
        catch (IOException e)
        {
            CloseSession();
            ShowLogin(e.Message);
            return;
        }

        if (problem is null)
        {
            await RefreshCharactersAsync(creation.Name);
        }
        else
        {
            _panel.ShowCreate((race, sex, deathKnight) => _displays!.ChoicesFor(race, sex, deathKnight));
            _panel.SetStatus(problem, true);
        }
    }

    private async Task DeleteAsync(CharacterInfo character)
    {
        if (_session is null)
        {
            return;
        }

        _panel.ShowBusy($"Deleting {character.Name}…");
        try
        {
            var problem = await _session.DeleteCharacterAsync(character);
            await RefreshCharactersAsync(null, problem);
        }
        catch (IOException e)
        {
            CloseSession();
            ShowLogin(e.Message);
        }
    }

    private async Task EnterAsync(CharacterInfo character)
    {
        if (_session is not { } session)
        {
            return;
        }

        _panel.ShowBusy($"Entering the world as {character.Name}…");
        try
        {
            await session.EnterWorldAsync(character);
        }
        catch (IOException e)
        {
            await RefreshCharactersAsync(character.Name, e.Message);
            return;
        }

        _session = null;
        Finish(() => Entered?.Invoke(session));
    }

    private Node3D[] _hiddenOverlays = [];

    private void Finish(Action then)
    {
        foreach (var overlay in _hiddenOverlays)
        {
            if (IsInstanceValid(overlay))
            {
                overlay.Visible = true;
            }
        }

        _player.SetUiMode(false);
        if (_camera is not null)
        {
            _camera.Current = false;
        }

        QueueFree();
        then();
    }

    private void CloseSession()
    {
        _session?.Dispose();
        _session = null;
    }

    public override void _ExitTree() => _session?.Dispose();

    public static CharacterAppearance AppearanceOf(CharacterInfo c)
    {
        var displays = new int[(int)EquipmentSlot.Count];
        var types = new int[(int)EquipmentSlot.Count];
        for (var slot = 0; slot < displays.Length && slot < c.Equipment.Length; slot++)
        {
            displays[slot] = c.Equipment[slot].DisplayId;
            types[slot] = c.Equipment[slot].InventoryType;
        }

        return new CharacterAppearance(c.Race, c.Sex, c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair, displays)
        {
            InventoryTypes = types,
            HideHelm = c.HidesHelm,
            HideCloak = c.HidesCloak,
        };
    }

    /// <summary>Puts a character on the stage, built off the main thread. The latest asked for wins.</summary>
    private void Preview(CharacterAppearance? appearance)
    {
        var version = ++_previewVersion;
        if (appearance is null || _library is null)
        {
            _preview?.QueueFree();
            _preview = null;
            return;
        }

        var library = _library;
        Task.Run(() => library.BuildCharacter(appearance)).ContinueWith(built => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || version != _previewVersion || !IsInsideTree())
            {
                return;
            }

            _preview?.QueueFree();
            _preview = null;
            if (built.Result is not { } asset)
            {
                return;
            }

            _preview = new CreatureNode { Name = "Preview" };
            _preview.Build(asset, "");
            AddChild(_preview);
            _preview.StartIdle();
            _preview.Scale = Vector3.One;
            Frame(asset.Skeleton.Bounds.End.Y * asset.Scale);
        }).CallDeferred());
    }

    public override void _Process(double delta)
    {
        if (_preview is not null)
        {
            // Faces the viewer down +Z, an orientation of -pi, since a model faces its +X and its
            // yaw is the orientation plus a quarter turn. It turns a little either way.
            _previewYaw += (float)delta * 0.25f;
            _preview.Move(CharacterSpot, -Mathf.Pi + Mathf.Sin(_previewYaw) * 0.5f, false, 0, 0, delta);
            _preview.Animate(delta, 0);
        }
    }

    /// <summary>
    /// Moves the desktop camera so a character this tall fills most of the screen's height, still
    /// looking past the panel's side. VR keeps the player where they stand: a Tauren is big.
    /// </summary>
    private void Frame(float height)
    {
        if (_camera is null || height <= 0.1f)
        {
            return;
        }

        var scale = height / DefaultHeight;
        _camera.Position = new Vector3(DesktopCamera.X * scale, DesktopCamera.Y * scale, DesktopCamera.Z * scale);
        _camera.LookAt(new Vector3(DesktopLookAt.X * scale, DesktopLookAt.Y * scale, DesktopLookAt.Z));
    }

    /// <summary>The height <see cref="DesktopCamera"/> frames, about a human's.</summary>
    private const float DefaultHeight = 2f;

    /// <summary>In front of the eyes, a little low and to the left, turned to face them, with the keyboard below it.</summary>
    private void PlaceVrPanel()
    {
        if (_vrPanel is null || !IsInsideTree())
        {
            return;
        }

        var eyes = _player.Eyes;
        var forward = -eyes.Basis.Z;
        forward = new Vector3(forward.X, 0, forward.Z).Normalized();
        if (forward.LengthSquared() < 0.01f)
        {
            forward = Vector3.Forward;
        }

        var left = forward.Cross(Vector3.Up) * -1;
        var center = eyes.Origin + forward * PanelDistance + left * PanelLeft + Vector3.Down * PanelDrop;
        _vrPanel.GlobalPosition = center;
        _vrPanel.LookAt(eyes.Origin with { Y = center.Y }, Vector3.Up);
        _vrPanel.RotateObjectLocal(Vector3.Up, Mathf.Pi);

        if (_keyboard is not null)
        {
            var below = eyes.Origin + forward * (PanelDistance - 0.15f) + Vector3.Down * 0.55f;
            _keyboard.GlobalPosition = below;
            _keyboard.LookAt(eyes.Origin with { Y = below.Y }, Vector3.Up);
            _keyboard.RotateObjectLocal(Vector3.Up, Mathf.Pi);
            _keyboard.RotateObjectLocal(Vector3.Right, -0.6f);
        }
    }
}
