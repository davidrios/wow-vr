using Godot;

namespace AzerothVR.Desktop;

/// <summary>
/// Free-flying desktop camera. Click to capture the mouse, Esc to release it.
/// WASD moves, Q/E go down/up, Shift moves faster.
/// </summary>
public partial class DesktopPlayer : Camera3D
{
    private const float MouseSensitivity = 0.0025f;
    private const float Speed = 10f;
    private const float FastSpeed = 50f;

    private float _yaw;
    private float _pitch;

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true }:
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;

            case InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }:
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;

            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -Mathf.Pi / 2, Mathf.Pi / 2);
                Rotation = new Vector3(_pitch, _yaw, 0);
                break;
        }
    }

    public override void _Process(double delta)
    {
        var direction = new Vector3(
            Axis(Key.A, Key.D),
            Axis(Key.Q, Key.E),
            Axis(Key.W, Key.S));
        if (direction == Vector3.Zero)
        {
            return;
        }

        var speed = Input.IsPhysicalKeyPressed(Key.Shift) ? FastSpeed : Speed;
        Position += Transform.Basis * direction.Normalized() * speed * (float)delta;
    }

    private static float Axis(Key negative, Key positive) =>
        (Input.IsPhysicalKeyPressed(positive) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(negative) ? 1f : 0f);
}
