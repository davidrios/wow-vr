using WowVR.Ui;
using Godot;

namespace WowVR.Desktop;

/// <summary>
/// Click to capture the mouse, Esc to release it. WASD walks, Shift goes faster, F toggles
/// flying, Q/E go down/up while flying or swimming, and Tab opens the menu.
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
    private const float SwimSpeed = 4.7f;
    private const float SwimResponse = 4f;
    private const float Buoyancy = 2f;
    private const float MaxRise = 2.5f;

    /// <summary>How high a step can be, in yards. A capsule can't climb one by itself.</summary>
    private const float StepHeight = 1.2f;

    /// <summary>How far ahead to look for a step, in yards.</summary>
    private const float StepProbe = 0.3f;

    private Camera3D _camera = null!;
    private CapsuleShape3D _capsule = null!;
    private CollisionShape3D _shape = null!;
    private CanvasLayer _menuLayer = null!;
    private float _pitch;
    private bool _frozen;
    private bool _swimming;
    private float _floatHeight;

    public WorldMenu Menu { get; private set; } = null!;

    public Vector3 Feet => GlobalPosition;

    public bool Flying { get; set; }

    public bool OnGround => IsOnFloor();

    public bool Frozen
    {
        set => _frozen = value;
    }

    public bool Held { private get; set; }

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

    public void SetSwimming(bool swimming, float floatHeight) => (_swimming, _floatHeight) = (swimming, floatHeight);

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
        _camera.Fov = AppSettings.FieldOfView;
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
        if (Held || (_frozen && !Flying))
        {
            Velocity = Vector3.Zero;
            return;
        }

        if (Flying)
        {
            Fly();
        }
        else if (_swimming)
        {
            Swim((float)delta);
        }
        else
        {
            Walk((float)delta);
            TryStepUp();
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

    /// <summary>Goes where the camera looks, and floats up when left alone. The numbers match movement_swim.gd.</summary>
    private void Swim(float delta)
    {
        var input = _menuLayer.Visible
            ? Vector3.Zero
            : new Vector3(Axis(Key.A, Key.D), Axis(Key.Q, Key.E), Axis(Key.W, Key.S));
        if (ScriptedWalk is { } scripted)
        {
            input = GlobalBasis.Inverse() * scripted;
        }

        var target = input == Vector3.Zero ? Vector3.Zero : _camera.GlobalBasis * input.Normalized() * SwimSpeed;
        var feet = GlobalPosition.Y;
        if (input == Vector3.Zero)
        {
            target.Y = Mathf.Clamp((_floatHeight - feet) * Buoyancy, -MaxRise, MaxRise);
        }
        else if (feet >= _floatHeight)
        {
            // Can't swim up out of the water, only along its surface.
            target.Y = Mathf.Min(target.Y, (_floatHeight - feet) * Buoyancy);
        }

        Velocity = Velocity.Lerp(target, Mathf.Clamp(SwimResponse * delta, 0f, 1f));
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

    /// <summary>Lifts the player onto a step just ahead. Matches movement_step.gd.</summary>
    private void TryStepUp()
    {
        var direction = new Vector3(Velocity.X, 0, Velocity.Z);
        if (!IsOnFloor() || direction.Length() < 0.1f)
        {
            return;
        }

        var start = GlobalTransform;
        var ahead = direction.Normalized() * StepProbe;
        var up = Vector3.Up * StepHeight;

        // Only a face too steep to walk up is a step. Slopes look the same to this test, and
        // lifting the player on those would make them faster uphill.
        var blocker = new KinematicCollision3D();
        if (!TestMove(start, ahead, blocker) || blocker.GetAngle(0, Vector3.Up) <= FloorMaxAngle || TestMove(start, up))
        {
            return;
        }

        var raised = start.Translated(up);
        if (TestMove(raised, ahead))
        {
            return;
        }

        var over = raised.Translated(ahead);
        var landing = new KinematicCollision3D();
        if (TestMove(over, Vector3.Down * StepHeight, landing) && landing.GetAngle(0, Vector3.Up) <= FloorMaxAngle)
        {
            GlobalPosition = over.Origin + landing.GetTravel();
        }
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
