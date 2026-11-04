using System;
using WowVR.Converters;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// Hides the world while it loads: a starry void around the head, with Outland's sky in it once
/// the client is open, and, floating in it, a panel
/// with the map's picture and a progress bar. It stays where it is as the head turns, and comes
/// back round if the player looks well away. It's all in 3D, so it works the same in both modes.
/// </summary>
public partial class LoadingScreen : Node3D
{
    private const uint ScreenLayer = 1u << 19;
    private const int BackdropPriority = 124;

    /// <summary>The sky draws over the stars, and its fog, one higher, over the sky's lower edge.</summary>
    private const int SkyPriority = 125;
    private const int PanelPriority = 127;

    private const float BackdropRadius = 400f;

    /// <summary>In meters, so they scale with the world like the player does.</summary>
    private const float PanelDistance = 2.4f;
    private const float PanelWidth = 2f;

    private const float PanelBelowEyes = 0.05f;
    private const int PageWidth = 1280;
    private const int PageHeight = 960;

    /// <summary>How far the head can turn from the panel, then how close it comes back.</summary>
    private const float FollowStartDegrees = 55f;
    private const float FollowStopDegrees = 4f;
    private const float FollowRate = 2.5f;

    /// <summary>Within this, in meters, the panel keeps still as the head leans.</summary>
    private const float LeanDistance = 0.5f;

    /// <summary>Further than this, in meters, and the head has been moved rather than leaned.</summary>
    private const float SnapDistance = 3f;

    private const double CoverSeconds = 0.3;
    private const double RevealSeconds = 0.9;

    /// <summary>The world draws behind the opaque screen for this long first, so its first frames' hitches don't show.</summary>
    private const double WarmSeconds = 0.5;

    /// <summary>The world must stay ready this long, since creatures only look for what's near every quarter second.</summary>
    private const double SettleSeconds = 0.5;
    private const double GiveUpSeconds = 180;
    private const float BarRate = 0.8f;

    /// <summary>Of the bar, what opening the client takes up before any tile.</summary>
    private const float OpeningShare = 0.05f;

    private enum Stage
    {
        Hidden,
        Covering,
        Loading,
        Warming,
        Revealing,
    }

    private Stage _stage;
    private double _stageTime;
    private double _settled;
    private double _loadingTime;
    private float _bar;
    private float _fade;
    private bool _panelPlaced;
    private bool _following;
    private float _panelYaw;
    private Vector3 _panelHead;
    private Action? _whenCovered;
    private Camera3D? _camera;
    private uint _worldMask;
    private bool _maskSaved;

    private TileManager? _tiles;
    private CreatureManager? _creatures;
    private IPlayer? _player;

    private MeshInstance3D _backdrop = null!;
    private CosmicSky? _sky;
    private MeshInstance3D _panel = null!;
    private ShaderMaterial _backdropMaterial = null!;
    private ShaderMaterial _panelMaterial = null!;
    private SubViewport _page = null!;
    private TextureRect _picture = null!;
    private Label _title = null!;
    private Label _status = null!;
    private ProgressBar _progress = null!;

    /// <summary>Stays up however ready the world is, for <c>--hold-loading</c>.</summary>
    public bool Hold { get; init; }

    /// <summary>The world can't be seen, at least in part.</summary>
    public bool Showing => _stage != Stage.Hidden;

    /// <summary>The world is entirely behind the screen, and isn't being drawn.</summary>
    public bool WorldHidden => _stage == Stage.Loading;

    /// <summary>Slowest frame while the world wasn't being drawn, in milliseconds. For checks.</summary>
    public double WorstLoadingFrame { get; set; }

    public override void _Ready()
    {
        TopLevel = true;
        _backdropMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/loading_backdrop.gdshader"), RenderPriority = BackdropPriority };
        _backdrop = new MeshInstance3D
        {
            Name = "Backdrop",
            Mesh = new SphereMesh { Radius = BackdropRadius, Height = BackdropRadius * 2f, Material = _backdropMaterial },
            Layers = ScreenLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(_backdrop);

        BuildPage();

        _panelMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/loading_panel.gdshader"), RenderPriority = PanelPriority };
        _panelMaterial.SetShaderParameter("page", _page.GetTexture());
        var height = PanelWidth * PageHeight / PageWidth;
        _panelMaterial.SetShaderParameter("size", new Vector2(PanelWidth, height));
        _panel = new MeshInstance3D
        {
            Name = "Panel",
            Mesh = new QuadMesh { Size = new Vector2(PanelWidth, height), Material = _panelMaterial },
            Layers = ScreenLayer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(_panel);

        Show(Stage.Loading);
        SetFade(1f);
        SetVolume(0f);
    }

    /// <summary>
    /// Puts a zone's sky from the client in the void, Hellfire Peninsula's, over its stars and under the
    /// panel, with a fog in its colors below, and tints the void to match.
    /// </summary>
    public void SetSky(ModelLibrary models, VoidLook look)
    {
        if (_sky is not null)
        {
            return;
        }

        _backdropMaterial.SetShaderParameter("top_color", look.Top);
        _backdropMaterial.SetShaderParameter("horizon_color", look.Horizon);
        _backdropMaterial.SetShaderParameter("bottom_color", look.Fog);
        _sky = new CosmicSky { Name = "Sky", Layers = ScreenLayer, Priority = SkyPriority, Weight = _fade };
        AddChild(_sky);
        _sky.Load(models, look);
    }

    /// <summary>What the bar and the wait for the world follow, once there is one.</summary>
    public void Watch(TileManager tiles, CreatureManager creatures, IPlayer player)
    {
        (_tiles, _creatures, _player) = (tiles, creatures, player);
        _tiles.Covered = Showing;
        _player.Held = Showing;
    }

    public void SetPlace(string title, Texture2D? picture)
    {
        _title.Text = title;
        if (picture is not null)
        {
            _picture.Texture = picture;
        }
    }

    /// <summary>Fades the world out, then acts behind the screen, such as a teleport or a reload.</summary>
    public void Cover(string title, Action whenCovered)
    {
        _title.Text = title;
        _whenCovered = whenCovered;
        _bar = 0f;
        _settled = 0;
        _loadingTime = 0;
        Show(Stage.Covering);
        if (_tiles is not null)
        {
            _tiles.Covered = true;
        }

        if (_player is not null)
        {
            _player.Held = true;
            _player.SetMenuOpen(false);
        }
    }

    /// <summary>There's no world to wait for, so whatever is there can be seen, like the grey floor.</summary>
    public void Dismiss()
    {
        Show(Stage.Revealing);
        _stageTime = RevealSeconds;
        Release();
    }

    public override void _ExitTree()
    {
        // A reload's next screen starts silent again by itself, and otherwise the sound must come back.
        if (_stage == Stage.Hidden)
        {
            SetVolume(1f);
        }
    }

    public override void _Process(double delta)
    {
        if (_stage == Stage.Hidden)
        {
            return;
        }

        _stageTime += delta;
        Follow(delta);

        switch (_stage)
        {
            case Stage.Covering:
                var covered = (float)Mathf.Min(_stageTime / CoverSeconds, 1.0);
                SetFade(Mathf.Max(_fade, covered));
                SetVolume(1f - covered);
                if (covered >= 1f)
                {
                    Enter(Stage.Loading);
                    SetWorldDrawn(false);
                    var action = _whenCovered;
                    _whenCovered = null;
                    action?.Invoke();
                }

                break;

            case Stage.Loading:
                SetWorldDrawn(false);
                WorstLoadingFrame = Math.Max(WorstLoadingFrame, delta * 1000);
                _loadingTime += delta;
                UpdateProgress(delta);
                _settled = IsWorldReady() ? _settled + delta : 0;
                if (_settled >= SettleSeconds && _tiles!.PlaceAgainAfterWarp())
                {
                    _settled = 0;
                }

                if (!Hold && (_settled >= SettleSeconds || _loadingTime > GiveUpSeconds))
                {
                    if (_settled < SettleSeconds)
                    {
                        GD.PushWarning("The world is taking too long to load, so showing it as it is.");
                    }

                    GD.Print($"Loading screen: ready after {_loadingTime:F1} s, slowest frame {WorstLoadingFrame:F0} ms");
                    SetWorldDrawn(true);
                    Enter(Stage.Warming);
                }

                break;

            case Stage.Warming:
                UpdateProgress(delta);
                if (_stageTime >= WarmSeconds)
                {
                    Enter(Stage.Revealing);
                    Release();
                }

                break;

            case Stage.Revealing:
                var shown = (float)Mathf.Min(_stageTime / RevealSeconds, 1.0);
                SetFade(1f - shown * shown * (3f - 2f * shown));
                SetVolume(shown);
                if (shown >= 1f)
                {
                    Hide();
                }

                break;
        }
    }

    private void Show(Stage stage)
    {
        Visible = true;
        _page.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _panelPlaced = false;
        Enter(stage);
        Follow(0);
    }

    private new void Hide()
    {
        Enter(Stage.Hidden);
        Visible = false;
        _page.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        SetVolume(1f);
        SetCameraSeesScreen(false);
    }

    private void Enter(Stage stage) => (_stage, _stageTime) = (stage, 0);

    /// <summary>The player can go, though the world is still fading in.</summary>
    private void Release()
    {
        if (_tiles is not null)
        {
            _tiles.Covered = false;
        }

        if (_player is not null)
        {
            _player.Held = false;
        }

        SetWorldDrawn(true);
    }

    private bool IsWorldReady() =>
        _tiles is { HasStarted: true, LoadedFraction: >= 1f, IsBusy: false, PlacingOnSurface: false, CollisionSettled: true }
        && _creatures?.IsBusy == false;

    private void UpdateProgress(double delta)
    {
        var target = OpeningShare;
        var status = "Opening the game files";
        if (_tiles is { HasStarted: true })
        {
            var loaded = _tiles.LoadedFraction;
            target = OpeningShare + (1f - OpeningShare) * loaded;
            status = loaded < 0.5f ? "Raising the land" : loaded < 1f ? "Placing the trees and buildings" : "Waking the creatures";
        }

        if (_stage == Stage.Warming)
        {
            (target, status) = (1f, "Welcome");
        }

        // Tiles leaving can lower what's loaded, and a bar shouldn't go back.
        _bar = Mathf.MoveToward(_bar, Mathf.Max(_bar, target), BarRate * (float)delta);
        _progress.Value = _bar;
        _status.Text = status;
    }

    private void SetFade(float fade)
    {
        _fade = fade;
        _backdropMaterial.SetShaderParameter("fade", fade);
        _sky?.Weight = fade;

        // Fades quicker than the void, so the panel never hangs in the open world.
        _panelMaterial.SetShaderParameter("fade", Mathf.Clamp(fade * 2f - 1f, 0f, 1f));
    }

    private static void SetVolume(float volume) => AudioServer.SetBusVolumeLinear(0, volume);

    /// <summary>The screen is always seen while it's up, and the world only when not fully behind it.</summary>
    private void SetWorldDrawn(bool drawn)
    {
        if (!SetCameraSeesScreen(true))
        {
            return;
        }

        _camera!.CullMask = drawn ? _worldMask | ScreenLayer : ScreenLayer;
    }

    private bool SetCameraSeesScreen(bool sees)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera is null)
        {
            return false;
        }

        if (camera != _camera || !_maskSaved)
        {
            _camera = camera;
            _worldMask = camera.CullMask & ~ScreenLayer;
            _maskSaved = true;
        }

        camera.CullMask = sees ? camera.CullMask | ScreenLayer : _worldMask;
        return true;
    }

    /// <summary>The void stays on the head. The panel stays put unless looked well away from.</summary>
    private void Follow(double delta)
    {
        if (!SetCameraSeesScreen(true))
        {
            return;
        }

        var head = _camera!.GlobalPosition;
        var scale = AppSettings.WorldScale;
        _backdrop.GlobalPosition = head;

        var forward = -_camera.GlobalBasis.Z;
        var flat = new Vector2(forward.X, forward.Z);
        var lookingUpOrDown = flat.LengthSquared() < 0.01f;
        var yaw = lookingUpOrDown ? _panelYaw : Mathf.Atan2(-forward.X, -forward.Z);
        var off = Mathf.Abs(Mathf.AngleDifference(_panelYaw, yaw));

        // The player being put on the ground moves the head by more than leaning can.
        if (_panelPlaced && head.DistanceTo(_panelHead) > LeanDistance * scale)
        {
            _panelHead = _panelHead.Lerp(head, Mathf.Clamp(FollowRate * (float)delta, 0f, 1f));
        }

        if (!_panelPlaced || head.DistanceTo(_panelHead) > SnapDistance * scale)
        {
            (_panelYaw, _panelHead, _panelPlaced, _following) = (yaw, head, true, false);
        }
        else if (_following)
        {
            var weight = Mathf.Clamp(FollowRate * (float)delta, 0f, 1f);
            _panelYaw = Mathf.LerpAngle(_panelYaw, yaw, weight);
            _following = off > Mathf.DegToRad(FollowStopDegrees);
        }
        else
        {
            _following = off > Mathf.DegToRad(FollowStartDegrees);
        }

        var basis = new Basis(Vector3.Up, _panelYaw).Scaled(Vector3.One * scale);
        var center = _panelHead + basis * new Vector3(0, -PanelBelowEyes, -PanelDistance);
        _panel.GlobalTransform = new Transform3D(basis, center);
    }

    /// <summary>The map's picture fills the page, with its name and the bar over a dark band along the bottom.</summary>
    private void BuildPage()
    {
        _page = new SubViewport
        {
            Name = "Page",
            Size = new Vector2I(PageWidth, PageHeight),
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            GuiDisableInput = true,
        };
        AddChild(_page);

        var root = new Control { Size = new Vector2(PageWidth, PageHeight), Theme = GlueTheme.Theme };
        _page.AddChild(root);

        root.AddChild(new ColorRect { Color = new Color(0.03f, 0.04f, 0.09f), Size = root.Size });

        _picture = new TextureRect
        {
            Size = root.Size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,

            // The client's pictures are stored square and stretched to its 4:3 screen, like this page.
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        root.AddChild(_picture);

        var scrim = new Gradient { Offsets = [0f, 0.45f, 1f], Colors = [new Color(0, 0, 0, 0), new Color(0.01f, 0.015f, 0.04f, 0.82f), new Color(0.01f, 0.015f, 0.04f, 0.96f)] };
        const float scrimHeight = 400f;
        root.AddChild(new TextureRect
        {
            Texture = new GradientTexture2D { Gradient = scrim, Width = 4, Height = 256, Fill = GradientTexture2D.FillEnum.Linear, FillFrom = Vector2.Zero, FillTo = Vector2.Down },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Position = new Vector2(0, PageHeight - scrimHeight),
            Size = new Vector2(PageWidth, scrimHeight),
        });

        const float margin = 64f;
        _title = new Label
        {
            Text = "Loading",
            Position = new Vector2(margin, PageHeight - 268f),
            Size = new Vector2(PageWidth - margin * 2f, 110f),
        };
        _title.AddThemeFontSizeOverride("font_size", 84);
        _title.AddThemeColorOverride("font_color", GlueTheme.Text);
        _title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f));
        _title.AddThemeConstantOverride("shadow_offset_y", 4);
        root.AddChild(_title);

        _status = new Label
        {
            Position = new Vector2(margin, PageHeight - 158f),
            Size = new Vector2(PageWidth - margin * 2f, 50f),
        };
        _status.AddThemeFontSizeOverride("font_size", 38);
        _status.AddThemeColorOverride("font_color", GlueTheme.Muted);
        root.AddChild(_status);

        _progress = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.001,
            ShowPercentage = false,
            Position = new Vector2(margin, PageHeight - 92f),
            Size = new Vector2(PageWidth - margin * 2f, 24f),
        };
        _progress.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.14f), CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12, CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12 });
        _progress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.7f, 0.08f, 0.04f), CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12, CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12 });
        root.AddChild(_progress);
    }
}
