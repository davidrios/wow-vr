using WowVR.Ui;
using Godot;

namespace WowVR.Desktop;

/// <summary>
/// Click to capture the mouse, Esc to release it. WASD walks, Shift goes faster, F toggles
/// flying, Q/E go down/up while flying, and Tab opens the menu.
/// </summary>
public partial class DesktopPlayer : CharacterBody3D, IPlayer
{
    private const float MouseSensitivity = 0.0025f;
    private const float EyeHeightMeters = 1.6f;
    private const float BodyHeightMeters = 1.75f;
    private const float BodyRadiusMeters = 0.3f;
    private const float FastWalkFactor = 2f;
    private const float FastFlyFactor = 5f;
    private const float MaxSlopeDegrees = 50f;
    private const float GroundSnapLength = 1f;
    private const float Gravity = 19f;

    private Camera3D _camera = null!;
    private CapsuleShape3D _capsule = null!;
    private CollisionShape3D _shape = null!;
    private CanvasLayer _menuLayer = null!;
    private float _pitch;
    private bool _frozen;

    public WorldMenu Menu { get; private set; } = null!;

    public Vector3 Feet => GlobalPosition;

    public bool Flying { get; set; }

    public bool OnGround => IsOnFloor();

    public bool Frozen
    {
        set => _frozen = value;
    }

    /// <summary>Used by scripted walks: a world direction that replaces the keyboard.</summary>
    public Vector3? ScriptedWalk { get; set; }

    public float PitchDegrees
    {
        set
        {
            _pitch = Mathf.DegToRad(value);
            _camera.Rotation = new Vector3(_pitch, 0, 0);
        }
    }

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera");
        _shape = GetNode<CollisionShape3D>("Collision");
        _capsule = (CapsuleShape3D)_shape.Shape;
        _menuLayer = GetNode<CanvasLayer>("MenuLayer");
        Menu = _menuLayer.GetNode<WorldMenu>("WorldMenu");
        Menu.Player = this;
        Menu.CloseRequested += () => SetMenuOpen(false);

        FloorMaxAngle = Mathf.DegToRad(MaxSlopeDegrees);
        FloorSnapLength = GroundSnapLength;

        // Otherwise the camera, which follows physics, stutters on faster screens.
        var refreshRate = DisplayServer.ScreenGetRefreshRate();
        if (refreshRate > 0)
        {
            Engine.PhysicsTicksPerSecond = Mathf.RoundToInt(refreshRate);
        }

        AppSettings.Changed += ApplySettings;
        ApplySettings();
    }

    public override void _ExitTree() => AppSettings.Changed -= ApplySettings;

    public void PlaceAt(Vector3 feet, bool keepVelocity = false)
    {
        GlobalPosition = feet;
        Velocity = keepVelocity ? Velocity : Vector3.Zero;
    }

    public void SetMenuOpen(bool open)
    {
        _menuLayer.Visible = open;
        Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    private void ApplySettings()
    {
        var scale = AppSettings.WorldScale;
        _camera.Position = new Vector3(0, EyeHeightMeters * scale, 0);
        _capsule.Radius = BodyRadiusMeters * scale;
        _capsule.Height = BodyHeightMeters * scale;
        _shape.Position = new Vector3(0, _capsule.Height / 2, 0);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true } when !_menuLayer.Visible:
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Escape }:
                _menuLayer.Visible = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;

            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.Tab }:
                SetMenuOpen(!_menuLayer.Visible);
                break;

            case InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.F }:
                Flying = !Flying;
                break;

            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                RotateY(-motion.Relative.X * MouseSensitivity);
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -Mathf.Pi / 2, Mathf.Pi / 2);
                _camera.Rotation = new Vector3(_pitch, 0, 0);
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_frozen && !Flying)
        {
            Velocity = Vector3.Zero;
            return;
        }

        if (Flying)
        {
            Fly();
        }
        else
        {
            Walk((float)delta);
        }

        MoveAndSlide();
    }

    private void Fly()
    {
        var input = _menuLayer.Visible
            ? Vector3.Zero
            : new Vector3(Axis(Key.A, Key.D), Axis(Key.Q, Key.E), Axis(Key.W, Key.S));
        var speed = AppSettings.FlySpeed * (Input.IsPhysicalKeyPressed(Key.Shift) ? FastFlyFactor : 1f);

        Velocity = input == Vector3.Zero ? Vector3.Zero : _camera.GlobalBasis * input.Normalized() * speed;
    }

    private void Walk(float delta)
    {
        var direction = ScriptedWalk ?? WalkInput();
        var speed = AppSettings.WalkSpeed * (Input.IsPhysicalKeyPressed(Key.Shift) ? FastWalkFactor : 1f);

        Velocity = new Vector3(
            direction.X * speed,
            IsOnFloor() ? 0 : Velocity.Y - Gravity * delta,
            direction.Z * speed);
    }

    private Vector3 WalkInput()
    {
        if (_menuLayer.Visible)
        {
            return Vector3.Zero;
        }

        var input = new Vector3(Axis(Key.A, Key.D), 0, Axis(Key.W, Key.S));
        return input == Vector3.Zero ? input : GlobalBasis * input.Normalized();
    }

    private static float Axis(Key negative, Key positive) =>
        (Input.IsPhysicalKeyPressed(positive) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(negative) ? 1f : 0f);
}
