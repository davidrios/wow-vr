using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Xr;

/// <summary>
/// The VR player. Left stick moves and left Y toggles flying; clicking the stick boosts flight.
/// Left trigger aims a teleport, right stick turns, left X opens the wrist menu, and right
/// trigger clicks in it. On a server, the HUD floats above the left wrist, the right trigger
/// also selects what the laser points at in the world, right A uses it (talks, loots, attacks,
/// opens), and right B types in chat with the virtual keyboard. Movement itself is XR Tools'
/// providers under the controllers.
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
    private XRController3D _rightController = null!;
    private Node3D? _hudPanel;
    private Node3D? _keyboard;
    private GameHud? _hud;
    private Interaction? _interaction;

    /// <summary>The panels' layer, which the laser pointer collides with.</summary>
    private const uint PointerLayer = 5242880;

    /// <summary>Where each controller is, for the body's hands to follow.</summary>
    public (Node3D Left, Node3D Right) Controllers => (_leftController, _rightController);

    private static readonly StringName GripAction = "grip";
    private static readonly StringName TriggerAction = "trigger";

    private Node3D _leftHand = null!;
    private Node3D _rightHand = null!;
    private (Skeleton3D Skeleton, int Bone)? _leftWrist;
    private (Skeleton3D Skeleton, int Bone)? _rightWrist;

    /// <summary>
    /// Each wrist in the world, with the fingers along +Y and the thumb's side along +Z: where XR
    /// Tools' hand puts its wrist bone, which is tuned to how a controller is held.
    /// </summary>
    public (Transform3D Left, Transform3D Right) Wrists
    {
        get
        {
            // XR Tools places its two hands differently, and its right one sits bent back. So the
            // right wrist is the left's, mirrored, which looks right in the headset.
            var left = WristOf(_leftController, _leftWrist, -1f);
            var inController = _leftController.GlobalTransform.AffineInverse() * left;
            var mirror = new Basis(new Vector3(-1, 0, 0), Vector3.Up, Vector3.Back);
            var mirrored = new Transform3D(mirror * inController.Basis * mirror, inController.Origin * new Vector3(-1, 1, 1));
            return (left, _rightController.GlobalTransform * mirrored);
        }
    }

    /// <summary>How far each grip and trigger is pressed, from 0 to 1.</summary>
    public (float LeftGrip, float LeftTrigger, float RightGrip, float RightTrigger) Fingers =>
        (_leftController.GetFloat(GripAction), _leftController.GetFloat(TriggerAction), _rightController.GetFloat(GripAction), _rightController.GetFloat(TriggerAction));

    /// <summary>XR Tools' own hands, hidden while the body's hands are where they are.</summary>
    public bool ControllerHandsVisible
    {
        set => _leftHand.Visible = _rightHand.Visible = value;
    }

    private static (Skeleton3D, int)? FindWrist(Node3D hand, string bone) =>
        hand.FindChild("Skeleton3D", true, false) is Skeleton3D skeleton && skeleton.FindBone(bone) is >= 0 and var index ? (skeleton, index) : null;

    private static Transform3D WristOf(XRController3D controller, (Skeleton3D Skeleton, int Bone)? wrist, float side)
    {
        if (wrist is { } w)
        {
            var at = w.Skeleton.GlobalTransform * w.Skeleton.GetBoneGlobalPose(w.Bone);
            return new Transform3D(at.Basis.Orthonormalized(), at.Origin);
        }

        // Where XR Tools' left hand has it, mirrored for the right: behind and below the grip, the fingers forward.
        var offset = new Vector3(0.03f * side, -0.05f, 0.177f) * AppSettings.WorldScale;
        return controller.GlobalTransform * new Transform3D(new Basis(Vector3.Right, -Mathf.Pi / 2), offset);
    }
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
        _rightController = GetNode<XRController3D>("RightController");
        _rightController.ButtonPressed += OnRightButtonPressed;
        _leftHand = GetNode<Node3D>("LeftController/LeftHand");
        _rightHand = GetNode<Node3D>("RightController/RightHand");
        _leftWrist = FindWrist(_leftHand, "Wrist_L");
        _rightWrist = FindWrist(_rightHand, "Wrist_R");
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
        _pointer.Set(Enabled, open || _uiMode || _hud is not null || AppSettings.TextureBrush);
        _teleport.Set(Enabled, !open && !_held);
    }

    private void ApplySettings()
    {
        // The texture brush needs the laser, even offline, where nothing else does.
        if (_wristMenu is not null && IsNodeReady())
        {
            _pointer.Set(Enabled, _wristMenu.Visible || _uiMode || _hud is not null || AppSettings.TextureBrush);
        }

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

    /// <summary>
    /// The HUD on a panel above the left wrist, turned toward the face when the wrist is raised,
    /// with the laser on to point at it and at the world.
    /// </summary>
    public void AddHud(GameHud hud, Interaction interaction)
    {
        (_hud, _interaction) = (hud, interaction);
        var panel = GD.Load<PackedScene>("res://addons/godot-xr-tools/objects/viewport_2d_in_3d.tscn").Instantiate<Node3D>();
        panel.Name = "HudPanel";
        panel.Set("screen_size", new Vector2(0.48f, 0.48f * GameHud.PanelSize.Y / GameHud.PanelSize.X));
        panel.Set("viewport_size", (Vector2)GameHud.PanelSize);
        panel.Set("collision_layer", PointerLayer);
        _leftController.AddChild(panel);
        panel.Set("transparent", 1);
        panel.Set("unshaded", true);
        panel.Position = new Vector3(0.05f, 0.22f, -0.1f);
        panel.RotationDegrees = new Vector3(-35f, 0f, 0f);
        panel.Scale = Vector3.One * AppSettings.WorldScale;
        panel.GetNode<SubViewport>("Viewport").TransparentBg = true;
        panel.GetNode<SubViewport>("Viewport").AddChild(hud);
        _hudPanel = panel;

        _keyboard = GD.Load<PackedScene>("res://addons/godot-xr-tools/objects/virtual_keyboard.tscn").Instantiate<Node3D>();
        _keyboard.Set("collision_layer", PointerLayer);
        _keyboard.Visible = false;
        AddChild(_keyboard);
        hud.TextFocusChanged += focused =>
        {
            _keyboard.Visible = focused;
            _keyboard.Set(Enabled, focused);
            if (focused)
            {
                PlaceKeyboard();
            }
        };
        hud.MenuRequested += () => SetMenuOpen(true);
        _pointer.Set(Enabled, true);
    }

    /// <summary>Below and in front of the eyes, tilted up, where a keyboard would be on a desk.</summary>
    private void PlaceKeyboard()
    {
        var eyes = Eyes;
        var forward = -eyes.Basis.Z with { Y = 0 };
        forward = forward.LengthSquared() < 1e-4f ? Vector3.Forward : forward.Normalized();
        var scale = AppSettings.WorldScale;
        _keyboard!.GlobalPosition = eyes.Origin + (forward * 0.45f * scale) + (Vector3.Down * 0.45f * scale);
        _keyboard.LookAt(eyes.Origin with { Y = _keyboard.GlobalPosition.Y }, Vector3.Up);
        _keyboard.RotateObjectLocal(Vector3.Up, Mathf.Pi);
        _keyboard.RotateObjectLocal(Vector3.Right, -0.6f);
        _keyboard.Scale = Vector3.One * 0.45f * scale;
    }

    /// <summary>Whether the right trigger is held with the texture brush on, which paints rather than selects.</summary>
    public bool Painting => AppSettings.TextureBrush && _rightController.IsButtonPressed(TriggerClick) && PointerRay is not null;

    private static readonly StringName TriggerClick = "trigger_click";

    /// <summary>The right controller's laser, while it isn't on a panel or the menu.</summary>
    public (Vector3 From, Vector3 Direction)? PointerRay
    {
        get
        {
            if ((_interaction is null && !AppSettings.TextureBrush) || PointingAtPanel || _wristMenu.Visible || _uiMode)
            {
                return null;
            }

            var aim = _rightController.GlobalTransform;
            return (aim.Origin, -aim.Basis.Z);
        }
    }

    /// <summary>Whether the laser is on a panel or the keyboard, which takes its clicks.</summary>
    private bool PointingAtPanel => _pointer.Get("target").AsGodotObject() is CollisionObject3D target && (target.CollisionLayer & PointerLayer) != 0;

    private void OnRightButtonPressed(string name)
    {
        if (_interaction is null || _uiMode || _held)
        {
            return;
        }

        var aim = _rightController.GlobalTransform;
        switch (name)
        {
            // Aiming a ground spell: the trigger casts it where the laser meets the ground, B puts it away.
            case "trigger_click" when !PointingAtPanel && !_wristMenu.Visible && _interaction.Ground is { Aiming: true } ground:
                ground.Confirm(aim.Origin, -aim.Basis.Z);
                break;
            case "by_button" when _interaction.Ground is { Aiming: true }:
                _interaction.World.CancelAiming();
                break;
            case "trigger_click" when AppSettings.TextureBrush:
                break;
            case "trigger_click" when !PointingAtPanel && !_wristMenu.Visible:
                _interaction.Select(aim.Origin, -aim.Basis.Z);
                break;
            case "ax_button":
                if (!_interaction.Use(aim.Origin, -aim.Basis.Z) && _interaction.World.Target != 0)
                {
                    _interaction.World.Use(_interaction.World.Target);
                }

                break;
            case "by_button":
                _hud?.OpenChat();
                break;
        }
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
        _rightController.ButtonPressed -= OnRightButtonPressed;
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
