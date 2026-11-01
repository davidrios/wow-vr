using Godot;

namespace AzerothVR.Xr;

/// <summary>The VR player. Main only creates it once OpenXR is running.</summary>
public partial class XrPlayer : XROrigin3D
{
    private const float Speed = 10f;
    private const float FastSpeed = 50f;
    private const float SnapTurnAngle = Mathf.Pi / 4;
    private const float SnapTurnPress = 0.7f;

    /// <summary>Below this the stick counts as released, so holding it only turns once.</summary>
    private const float SnapTurnRelease = 0.3f;

    /// <summary>Sticks rarely rest at exactly zero, which would cause slow drifting.</summary>
    private const float StickDeadzone = 0.2f;

    private static readonly StringName Stick = "primary";
    private const string StickClick = "primary_click";

    private OpenXRInterface _openXr = null!;
    private XRCamera3D _camera = null!;
    private XRController3D _leftController = null!;
    private XRController3D _rightController = null!;
    private bool _fast;
    private bool _snapTurnReady = true;

    public override void _Ready()
    {
        _openXr = (OpenXRInterface)XRServer.FindInterface("OpenXR");
        _openXr.SessionBegun += OnSessionBegun;

        _camera = GetNode<XRCamera3D>("XRCamera3D");
        _leftController = GetNode<XRController3D>("LeftController");
        _rightController = GetNode<XRController3D>("RightController");
        _leftController.ButtonPressed += OnLeftButtonPressed;

        // The headset paces rendering, so the desktop window must not.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var viewport = GetViewport();
        viewport.UseXR = true;

        // Godot doesn't support mouse picking in stereo and warns if it's left on.
        viewport.PhysicsObjectPicking = false;
    }

    public override void _Process(double delta)
    {
        Fly(ReadStick(_leftController), (float)delta);
        SnapTurn(_rightController.GetVector2(Stick).X);
    }

    /// <summary>Flies where the head is looking, including up and down.</summary>
    private void Fly(Vector2 stick, float delta)
    {
        var direction = _camera.GlobalBasis * new Vector3(stick.X, 0, -stick.Y);
        GlobalPosition += direction * (_fast ? FastSpeed : Speed) * delta;
    }

    private void SnapTurn(float stick)
    {
        if (Mathf.Abs(stick) < SnapTurnRelease)
        {
            _snapTurnReady = true;
        }
        else if (_snapTurnReady && Mathf.Abs(stick) > SnapTurnPress)
        {
            _snapTurnReady = false;
            TurnAroundHead(-Mathf.Sign(stick) * SnapTurnAngle);
        }
    }

    /// <summary>Turns around the head rather than the play space's center, which would also move the player.</summary>
    private void TurnAroundHead(float angle)
    {
        var pivot = _camera.GlobalPosition;
        var transform = GlobalTransform;
        transform.Origin -= pivot;
        transform = transform.Rotated(Vector3.Up, angle);
        transform.Origin += pivot;
        GlobalTransform = transform;
    }

    private static Vector2 ReadStick(XRController3D controller)
    {
        var stick = controller.GetVector2(Stick);
        return stick.Length() < StickDeadzone ? Vector2.Zero : stick;
    }

    private void OnLeftButtonPressed(string name)
    {
        if (name == StickClick)
        {
            _fast = !_fast;
            GD.Print($"Fast flying: {(_fast ? "on" : "off")}");
        }
    }

    public override void _ExitTree()
    {
        _openXr.SessionBegun -= OnSessionBegun;
        _leftController.ButtonPressed -= OnLeftButtonPressed;
    }

    private void OnSessionBegun()
    {
        var refreshRate = _openXr.DisplayRefreshRate;
        if (refreshRate > 0)
        {
            Engine.PhysicsTicksPerSecond = Mathf.RoundToInt(refreshRate);
            GD.Print($"Headset refresh rate: {refreshRate} Hz");
        }
    }
}
