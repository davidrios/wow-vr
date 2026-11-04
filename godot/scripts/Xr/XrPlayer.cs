using WowVR.Ui;
using Godot;

namespace WowVR.Xr;

/// <summary>
/// The VR player. Left stick moves and left Y toggles flying; clicking the stick boosts flight.
/// Left trigger aims a teleport, right stick turns, left X opens the wrist menu, and right
/// trigger clicks in it. Movement itself is XR Tools' providers under the controllers.
/// </summary>
public partial class XrPlayer : XROrigin3D, IPlayer
{
    // XR Tools' properties and methods, by name. Made once: passing a string makes a new StringName
    // each call, which is garbage, and some of these are read or set every frame or physics tick.
    private static readonly StringName IsActive = "is_active";
    private static readonly StringName FloatHeight = "float_height";
    private static readonly StringName OnGroundName = "on_ground";
    private static readonly StringName Enabled = "enabled";
    private static readonly StringName ControllerName = "controller";
    private static readonly StringName DefaultPhysics = "default_physics";
    private static readonly StringName StopOnSlope = "stop_on_slope";
    private static readonly StringName StepSize = "step_size";
    private static readonly StringName GetSceneInstance = "get_scene_instance";
    private static readonly StringName Teleport = "teleport";
    private static readonly StringName MaxSpeed = "max_speed";
    private static readonly StringName TurnMode = "turn_mode";
    private static readonly StringName StepTurnAngle = "step_turn_angle";
    private static readonly StringName Speed = "speed";
    private static readonly StringName Radius = "radius";

    private const float SnapTurnDegrees = 45f;
    private const float BoostFactor = 5f;

    /// <summary>
    /// Length of each of the teleport arc's 25 steps. XR Tools' 0.5 stops at 12.5 yards,
    /// which is short for this world and misses the ground when aiming level.
    /// </summary>
    private const float TeleportStepSize = 2f;

    private const int SnapTurnMode = 1;
    private const int SmoothTurnMode = 2;

    /// <summary>
    /// The radius at the smallest size setting. The shader scales radius by the square root
    /// of 2 in screen space, and headset lenses don't show the screen's edges, so much above
    /// this can't be seen.
    /// </summary>
    private const float VignetteWidestRadius = 0.7f;

    /// <summary>The radius at the largest size setting.</summary>
    private const float VignetteNarrowestRadius = 0.25f;

    /// <summary>How long the vignette shows after changing its size, since moving is what normally shows it.</summary>
    private const double VignettePreviewSeconds = 1.5;
    private const float VignetteResponse = 6f;

    /// <summary>Below this the vignette stays open, so slow head movement doesn't trigger it.</summary>
    private const float VignetteMinSpeed = 1f;

    private OpenXRInterface? _openXr;
    private XRController3D _leftController = null!;
    private CharacterBody3D _body = null!;
    private Node3D _walk = null!;
    private Node3D _turn = null!;
    private Node3D _fly = null!;
    private Node3D _swim = null!;
    private Node3D _teleport = null!;
    private Node3D _pointer = null!;
    private Node3D _vignette = null!;
    private Node3D _wristMenu = null!;
    private bool _boost;
    private bool _frozen;
    private bool _held;
    private float _vignetteRadius = 1f;
    private Basis _lastBasis = Basis.Identity;
    private float _lastVignetteSize = -1f;
    private double _vignettePreview;

    /// <summary>1 when the vignette is open.</summary>
    public float CurrentVignetteRadius => _vignetteRadius;

    public WorldMenu Menu { get; private set; } = null!;

    public Vector3 Feet => _body.GlobalPosition;

    public void SetSwimming(bool swimming, float floatHeight)
    {
        _swim.Set(IsActive, swimming);
        _swim.Set(FloatHeight, floatHeight);
    }

    public bool Flying
    {
        get => _fly.Get(IsActive).AsBool();
        set
        {
            _fly.Set(IsActive, value);
            _body.Velocity = Vector3.Zero;
        }
    }

    public bool OnGround => _body.Get(OnGroundName).AsBool();

    public bool Frozen
    {
        set
        {
            _frozen = value;
            ApplyBodyEnabled();
        }
    }

    public bool Held
    {
        set
        {
            _held = value;
            ApplyBodyEnabled();
            _teleport.Set(Enabled, !value && !_wristMenu.Visible);
        }
    }

    private void ApplyBodyEnabled()
    {
        var enabled = !_held && (!_frozen || Flying);
        if (_body.Get(Enabled).AsBool() != enabled)
        {
            _body.Set(Enabled, enabled);
        }
    }

    public override void _Ready()
    {
        _leftController = GetNode<XRController3D>("LeftController");
        _body = GetNode<CharacterBody3D>("PlayerBody");
        _walk = GetNode<Node3D>("LeftController/MovementDirect");
        _teleport = GetNode<Node3D>("LeftController/FunctionTeleport");
        _wristMenu = GetNode<Node3D>("LeftController/WristMenu");
        _turn = GetNode<Node3D>("RightController/MovementTurn");
        _pointer = GetNode<Node3D>("RightController/FunctionPointer");
        _fly = GetNode<Node3D>("MovementFly");
        _swim = GetNode<Node3D>("MovementSwim");
        _swim.Set(ControllerName, _leftController);
        _vignette = GetNode<Node3D>("XRCamera3D/Vignette");
        _fly.Set(ControllerName, _leftController);

        // XR Tools' way of not sliding down slopes pulls the body toward the ground's normal, which
        // leans uphill, and Godot moves a body standing on a floor along it by that pull's level
        // part: standing still, the player crept uphill at about 0.18 yards a second. With plain
        // gravity, Godot's own floor_stop_on_slope keeps them put, as for the desktop player.
        _body.Get(DefaultPhysics).AsGodotObject().Set(StopOnSlope, false);
        _teleport.Set(StepSize, TeleportStepSize);

        Menu = _wristMenu.Call(GetSceneInstance).As<WorldMenu>();
        Menu.Player = this;
        Menu.CloseRequested += () => SetMenuOpen(false);
        SetMenuOpen(false);

        _leftController.ButtonPressed += OnLeftButtonPressed;
        AppSettings.Changed += ApplySettings;
        ApplySettings();

        _openXr = XRServer.FindInterface("OpenXR") as OpenXRInterface;
        if (_openXr?.IsInitialized() != true)
        {
            // Only the --xr-player check gets here, to exercise this scene without a headset.
            _openXr = null;
            return;
        }

        _openXr.SessionBegun += OnSessionBegun;

        // The headset paces rendering, so the desktop window must not.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var viewport = GetViewport();
        viewport.UseXR = true;

        // Godot doesn't support mouse picking in stereo and warns if it's left on.
        viewport.PhysicsObjectPicking = false;
    }

    public void PlaceAt(Vector3 feet, bool keepVelocity = false)
    {
        _body.Velocity = keepVelocity ? _body.Velocity : Vector3.Zero;
        _body.Call(Teleport, new Transform3D(_body.GlobalBasis, feet));
    }

    private bool _uiMode;

    public Transform3D Eyes => GetNode<XRCamera3D>("XRCamera3D").GlobalTransform;

    public void SetUiMode(bool on)
    {
        _uiMode = on;
        Held = on;
        SetMenuOpen(false);
        _pointer.Set(Enabled, on);
        _teleport.Set(Enabled, !on && !_held);
    }

    public void SetMenuOpen(bool open)
    {
        if (open && !_wristMenu.Visible)
        {
            Menu.ShowGamePage();
        }

        _wristMenu.Visible = open;
        _wristMenu.Set(Enabled, open);
        _pointer.Set(Enabled, open || _uiMode);
        _teleport.Set(Enabled, !open && !_held);
    }

    private void ApplySettings()
    {
        var scale = AppSettings.WorldScale;
        XRServer.WorldScale = scale;

        // XR Tools multiplies walking input by the world scale, so this is in real meters.
        _walk.Set(MaxSpeed, AppSettings.WalkSpeed / scale);

        _turn.Set(TurnMode, AppSettings.SmoothTurn ? SmoothTurnMode : SnapTurnMode);
        _turn.Set(StepTurnAngle, SnapTurnDegrees);

        // The menu is a child of the controller, whose position is scaled but whose size isn't.
        _wristMenu.Scale = Vector3.One * scale;
        ApplyFlySpeed();

        var size = AppSettings.VignetteSize;
        _vignettePreview = _lastVignetteSize >= 0f && size != _lastVignetteSize ? VignettePreviewSeconds : _vignettePreview;
        _lastVignetteSize = size;
    }

    private void ApplyFlySpeed() => _fly.Set(Speed, AppSettings.FlySpeed * (_boost ? BoostFactor : 1f));

    public override void _Process(double delta)
    {
        // Not XR Tools' automatic mode: it watches the origin, so re-centering would trigger it.
        // Snap turns are instant, so there's no motion to soften.
        var turning = AppSettings.SmoothTurn && !GlobalBasis.IsEqualApprox(_lastBasis);
        _lastBasis = GlobalBasis;
        _vignettePreview = Mathf.Max(_vignettePreview - delta, 0);
        var moving = _lastVignetteSize > 0f
            && (turning || _vignettePreview > 0 || _body.Velocity.Length() > VignetteMinSpeed);
        var weight = Mathf.Clamp(VignetteResponse * (float)delta, 0f, 1f);
        var target = moving ? Mathf.Lerp(VignetteWidestRadius, VignetteNarrowestRadius, _lastVignetteSize) : 1f;
        _vignetteRadius = !moving && _vignetteRadius > 0.99f ? 1f : Mathf.Lerp(_vignetteRadius, target, weight);
        _vignette.Set(Radius, _vignetteRadius);
    }

    private void OnLeftButtonPressed(string name)
    {
        switch (name)
        {
            case "by_button":
                Flying = !Flying;
                break;
            case "ax_button":
                // It's in the world's layer, so it can't be seen behind the loading screen.
                if (!_held && !_uiMode)
                {
                    SetMenuOpen(!_wristMenu.Visible);
                }

                break;
            case "primary_click":
                _boost = !_boost;
                ApplyFlySpeed();
                break;
        }
    }

    public override void _ExitTree()
    {
        if (_openXr is not null)
        {
            _openXr.SessionBegun -= OnSessionBegun;
        }

        _leftController.ButtonPressed -= OnLeftButtonPressed;
        AppSettings.Changed -= ApplySettings;
    }

    private void OnSessionBegun()
    {
        var refreshRate = _openXr!.DisplayRefreshRate;
        if (refreshRate > 0)
        {
            Engine.PhysicsTicksPerSecond = Mathf.RoundToInt(refreshRate);
            GD.Print($"Headset refresh rate: {refreshRate} Hz");
        }
    }
}
