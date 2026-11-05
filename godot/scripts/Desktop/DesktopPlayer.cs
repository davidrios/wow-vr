using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Desktop;

/// <summary>
/// WoW's own controls. The mouse is a cursor: holding the right button looks around and turns
/// the character, holding the left button looks around it without turning, and both together run
/// forward. A left click selects what's under the cursor and a right click uses it: talks, loots,
/// attacks or opens. W and S go forward and back, A and D turn (or strafe while the right button
/// is held), Q and E strafe, Space jumps, Num Lock runs on its own, Tab picks the next enemy,
/// Enter or / types in chat, B opens the bags, and the wheel moves the camera out behind the
/// character or into first person. F toggles flying, where Space and X go up and down, and
/// Shift goes faster. Esc closes windows, then clears the target, then opens the game menu.
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
    private const float Gravity = 19.29f;
    private const float SwimSpeed = 4.7f;
    private const float SwimResponse = 4f;
    private const float Buoyancy = 2f;
    private const float MaxRise = 2.5f;

    /// <summary>A jump's upward speed, in yards a second, as the game's.</summary>
    private const float JumpSpeed = 7.96f;

    /// <summary>Turning with A and D, in radians a second: half a turn a second, as the game's.</summary>
    private const float KeyTurnRate = Mathf.Pi;

    /// <summary>A mouse press that moves less than this, in pixels, is a click rather than a drag.</summary>
    private const float ClickSlop = 6f;

    /// <summary>How high a step can be, in yards. A capsule can't climb one by itself.</summary>
    private const float StepHeight = 1.2f;

    /// <summary>How far ahead to look for a step, in yards.</summary>
    private const float StepProbe = 0.3f;

    /// <summary>How far behind the eyes the camera can go, and how far each wheel step moves it, in yards.</summary>
    private const float MaxZoom = 15f;
    private const float ZoomStep = 1f;
    private const float DefaultZoom = 4f;

    private Camera3D _camera = null!;
    private SpringArm3D _boom = null!;
    private float _zoom;
    private CapsuleShape3D _capsule = null!;
    private CollisionShape3D _shape = null!;
    private CanvasLayer _menuLayer = null!;
    private float _pitch;

    /// <summary>How far the camera is turned around the character by the left button, in radians.</summary>
    private float _orbit;
    private bool _frozen;
    private bool _swimming;
    private float _floatHeight;
    private bool _leftHeld;
    private bool _rightHeld;
    private Vector2 _pressAt;
    private float _dragged;
    private Vector2 _cursorBeforeDrag;
    private bool _autoRun;

    public WorldMenu Menu { get; private set; } = null!;

    /// <summary>The HUD while playing on a server, whose chat line takes the keys while typing.</summary>
    public GameHud? Hud { get; set; }

    /// <summary>Selecting and using things under the cursor, while playing on a server.</summary>
    public Interaction? Interaction { get; set; }

    public Vector3 Feet => GlobalPosition;

    public bool Flying { get; set; }

    public bool OnGround => IsOnFloor();

    public bool Frozen
    {
        set => _frozen = value;
    }

    public bool Held { private get; set; }

    /// <summary>Used by checks: held keys as WoW's movement, X left and Y forward, instead of the keyboard.</summary>
    public Vector2? ScriptedIntent { get; set; }

    /// <summary>Used by scripted walks: a world direction that replaces the keyboard.</summary>
    public Vector3? ScriptedWalk { get; set; }

    /// <summary>The server's speeds for this character, in yards a second, or the game's defaults.</summary>
    public float RunSpeed { get; set; } = AppSettings.WalkSpeed;

    public float RunBackSpeed { get; set; } = 4.5f;

    /// <summary>Held in place by the server, as when stunned or dead.</summary>
    public bool Rooted { get; set; }

    /// <summary>How the character is moving against the way it faces, for its animation: X left, Y forward.</summary>
    public Vector2 MoveIntent { get; private set; }

    public bool Jumping { get; private set; }

    public float PitchDegrees
    {
        set
        {
            _pitch = Mathf.DegToRad(value);
            ApplyBoom();
        }
    }

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera");

        // The camera hangs behind the eyes on a boom that stops short of walls, as WoW's does.
        _boom = new SpringArm3D { Name = "Boom", CollisionMask = 1, Margin = 0.2f, Shape = new SphereShape3D { Radius = 0.2f } };
        AddChild(_boom);
        _boom.AddExcludedObject(GetRid());
        RemoveChild(_camera);
        _boom.AddChild(_camera);
        _camera.Position = Vector3.Zero;
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

        Input.MouseMode = Input.MouseModeEnum.Visible;
        AppSettings.Changed += ApplySettings;
        ApplySettings();
    }

    public override void _ExitTree() => AppSettings.Changed -= ApplySettings;

    public void SetSwimming(bool swimming, float floatHeight) => (_swimming, _floatHeight) = (swimming, floatHeight);

    public bool Swimming => _swimming;

    public void PlaceAt(Vector3 feet, bool keepVelocity = false)
    {
        GlobalPosition = feet;
        Velocity = keepVelocity ? Velocity : Vector3.Zero;
    }

    private bool _uiMode;

    public Transform3D Eyes => _camera.GlobalTransform;

    /// <summary>
    /// There's a character to see, so the camera can move out behind it. Without one, as offline,
    /// it stays in first person.
    /// </summary>
    public bool HasBody
    {
        get => _hasBody;
        set
        {
            _hasBody = value;
            Zoom = value ? DefaultZoom : 0f;
        }
    }

    private bool _hasBody;

    /// <summary>How far behind the eyes the camera is, in yards. Under half a yard is first person.</summary>
    public float Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Mathf.Clamp(value, 0f, MaxZoom);
            _boom.SpringLength = _zoom * AppSettings.WorldScale;
        }
    }

    public bool FirstPerson => _zoom < 0.5f;

    public void SetUiMode(bool on)
    {
        _uiMode = on;
        Held = on;
        _camera.Current = !on;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void SetMenuOpen(bool open)
    {
        if (open && !_menuLayer.Visible)
        {
            Menu.ShowGamePage();
        }

        _menuLayer.Visible = open;
        ReleaseMouse();
    }

    private void ApplySettings()
    {
        var scale = AppSettings.WorldScale;
        _boom.Position = new Vector3(0, EyeHeightMeters * scale, 0);
        _camera.Fov = AppSettings.FieldOfView;
        _capsule.Radius = BodyRadiusMeters * scale;
        _capsule.Height = BodyHeightMeters * scale;
        _shape.Position = new Vector3(0, _capsule.Height / 2, 0);
    }

    private void ApplyBoom() => _boom.Rotation = new Vector3(_pitch, _orbit, 0);

    /// <summary>Keys the chat line or a menu has, which shouldn't move the character.</summary>
    private bool KeysTaken => _menuLayer.Visible || _uiMode || Hud?.Typing == true;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_uiMode)
        {
            return;
        }

        switch (@event)
        {
            // With the texture brush on, the left button paints rather than orbiting and selecting.
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button when !_menuLayer.Visible && AppSettings.TextureBrush && (!button.Pressed || PointerRay is not null):
                Painting = button.Pressed;
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right } button when !_menuLayer.Visible:
                OnMouseButton(button);
                break;

            // A list that has scrolled to its end lets the wheel through; over the interface it still isn't the camera's.
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when HasBody && !_menuLayer.Visible:
                if (GetViewport().GuiGetHoveredControl() is null)
                {
                    Zoom = _zoom + (wheel.ButtonIndex == MouseButton.WheelDown ? ZoomStep : -ZoomStep);
                }

                break;

            case InputEventMouseMotion motion when _leftHeld || _rightHeld:
                _dragged += motion.Relative.Length();
                var turn = -motion.Relative.X * MouseSensitivity;
                if (_rightHeld)
                {
                    RotateY(turn);
                }
                else
                {
                    _orbit += turn;
                }

                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -Mathf.Pi / 2, Mathf.Pi / 2);
                ApplyBoom();
                break;

            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key);
                break;
        }
    }

    private void OnKey(InputEventKey key)
    {
        if (key.PhysicalKeycode == Key.Escape)
        {
            if (Hud?.CloseTopWindow() == true)
            {
                return;
            }

            if (!_menuLayer.Visible && Interaction is { } interaction && interaction.World.Target != 0)
            {
                interaction.World.SetTarget(0);
                return;
            }

            SetMenuOpen(!_menuLayer.Visible);
            return;
        }

        if (KeysTaken)
        {
            return;
        }

        switch (key.PhysicalKeycode)
        {
            case Key.Enter or Key.KpEnter when Hud is not null:
                Hud.OpenChat();
                GetViewport().SetInputAsHandled();
                break;
            case Key.Slash when Hud is not null:
                Hud.OpenChat("/");
                GetViewport().SetInputAsHandled();
                break;
            case Key.Tab when Interaction is not null:
                Interaction.TargetNextEnemy(Feet, -GlobalBasis.Z);
                break;
            case Key.Tab:
                SetMenuOpen(!_menuLayer.Visible);
                break;
            case Key.B when Hud is not null:
                Hud.ToggleBags();
                break;
            case Key.P when Hud is not null:
                Hud.ToggleSpellbook();
                break;
            case Key.C when Hud is not null:
                Hud.ToggleCharacter();
                break;
            case (>= Key.Key0 and <= Key.Key9) or Key.Minus or Key.Equal when Hud is not null:
                Hud.PressAction(key.PhysicalKeycode switch
                {
                    Key.Key0 => 9,
                    Key.Minus => 10,
                    Key.Equal => 11,
                    var digit => (int)(digit - Key.Key1),
                });
                break;
            case Key.F:
                Flying = !Flying;
                break;
            case Key.Numlock:
                _autoRun = !_autoRun;
                break;
            case Key.Space when !Flying && !_swimming && IsOnFloor() && !Rooted && !Held:
                Velocity = Velocity with { Y = JumpSpeed };
                Jumping = true;
                break;
        }
    }

    private void OnMouseButton(InputEventMouseButton button)
    {
        var wasHeld = _leftHeld || _rightHeld;
        if (button.ButtonIndex == MouseButton.Left)
        {
            _leftHeld = button.Pressed;
        }
        else
        {
            _rightHeld = button.Pressed;
            if (button.Pressed && _orbit != 0)
            {
                // Turning with the right button faces the character where the camera looks, as in WoW.
                RotateY(_orbit);
                _orbit = 0;
                ApplyBoom();
            }
        }

        if (button.Pressed && !wasHeld)
        {
            _pressAt = button.Position;
            _dragged = 0;
            _cursorBeforeDrag = GetViewport().GetMousePosition();
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (!button.Pressed && !_leftHeld && !_rightHeld)
        {
            ReleaseMouse();
            if (_dragged < ClickSlop)
            {
                Click(button.ButtonIndex == MouseButton.Right);
            }
        }
    }

    private void ReleaseMouse()
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            // The viewport's warp turns the interface's units back into the window's pixels.
            GetViewport().WarpMouse(_cursorBeforeDrag);
        }

        _leftHeld = _rightHeld = false;
    }

    /// <summary>Whether the left button is held with the texture brush on.</summary>
    public bool Painting { get; private set; }

    /// <summary>The ray under the mouse cursor, while it's free and over the world rather than a window.</summary>
    public (Vector3 From, Vector3 Direction)? PointerRay
    {
        get
        {
            if (Input.MouseMode != Input.MouseModeEnum.Visible || GetViewport().GuiGetHoveredControl() is not null)
            {
                return null;
            }

            var at = GetViewport().GetMousePosition();
            return (_camera.ProjectRayOrigin(at), _camera.ProjectRayNormal(at));
        }
    }

    /// <summary>A click without dragging: selects with the left button, uses with the right.</summary>
    private void Click(bool right)
    {
        if (Interaction is null)
        {
            return;
        }

        var from = _camera.ProjectRayOrigin(_pressAt);
        var direction = _camera.ProjectRayNormal(_pressAt);

        // Aiming a ground spell: the left button casts it where the cursor is, the right puts it away.
        if (Interaction.Ground is { Aiming: true } ground)
        {
            if (right)
            {
                Interaction.World.CancelAiming();
            }
            else
            {
                ground.Confirm(from, direction);
            }

            return;
        }

        if (right)
        {
            Interaction.Use(from, direction);
        }
        else
        {
            Interaction.Select(from, direction);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Held || (_frozen && !Flying))
        {
            Velocity = Vector3.Zero;
            MoveIntent = Vector2.Zero;
            return;
        }

        TurnWithKeys((float)delta);
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
        if (IsOnFloor() && Velocity.Y <= 0)
        {
            Jumping = false;
        }
    }

    /// <summary>A and D turn the character, unless the right button is held, when they strafe.</summary>
    private void TurnWithKeys(float delta)
    {
        if (KeysTaken || _rightHeld || Rooted)
        {
            return;
        }

        var turn = Axis(Key.D, Key.A);
        if (turn != 0)
        {
            RotateY(turn * KeyTurnRate * delta);
        }
    }

    /// <summary>Forward and back, and strafing, from the keys and mouse buttons: Y forward, X left.</summary>
    private Vector2 Intent()
    {
        if (ScriptedIntent is { } scripted)
        {
            return scripted;
        }

        if (KeysTaken || Rooted)
        {
            return Vector2.Zero;
        }

        var forward = Axis(Key.S, Key.W);
        if (forward != 0)
        {
            _autoRun = false;
        }

        if ((_leftHeld && _rightHeld) || _autoRun)
        {
            forward = 1f;
        }

        var left = Axis(Key.E, Key.Q);
        if (_rightHeld)
        {
            left += Axis(Key.D, Key.A);
        }

        return new Vector2(Mathf.Clamp(left, -1, 1), forward);
    }

    private void Fly()
    {
        var intent = Intent();
        var up = KeysTaken ? 0f : Axis(Key.X, Key.Space);
        MoveIntent = intent;
        var speed = AppSettings.FlySpeed * (Input.IsPhysicalKeyPressed(Key.Shift) ? FastFlyFactor : 1f);

        // Forward goes where the camera looks, as the game's flying does; Space and X go straight up and down.
        var direction = (_camera.GlobalBasis * new Vector3(-intent.X, 0, -intent.Y)) + (Vector3.Up * up);
        Velocity = direction == Vector3.Zero ? Vector3.Zero : direction.Normalized() * speed;
    }

    /// <summary>Goes where the camera looks, and floats up when left alone. The numbers match movement_swim.gd.</summary>
    private void Swim(float delta)
    {
        var intent = Intent();
        MoveIntent = intent;
        var up = KeysTaken ? 0f : Axis(Key.X, Key.Space);
        var input = new Vector3(-intent.X, 0, -intent.Y);
        if (ScriptedWalk is { } scripted)
        {
            input = GlobalBasis.Inverse() * scripted;
        }

        var direction = (_camera.GlobalBasis * input) + (Vector3.Up * up);
        var target = direction == Vector3.Zero ? Vector3.Zero : direction.Normalized() * (intent.Y < 0 ? SwimSpeed / 2 : SwimSpeed);
        var feet = GlobalPosition.Y;
        if (direction == Vector3.Zero)
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
        Vector3 direction;
        float speed;
        if (ScriptedWalk is { } scripted)
        {
            direction = scripted;
            speed = AppSettings.WalkSpeed;
            MoveIntent = Vector2.Up;
        }
        else
        {
            var intent = Intent();
            MoveIntent = intent;
            direction = intent == Vector2.Zero ? Vector3.Zero : GlobalBasis * new Vector3(-intent.X, 0, -intent.Y).Normalized();
            speed = intent.Y < 0 ? RunBackSpeed : RunSpeed;
            if (Input.IsPhysicalKeyPressed(Key.Shift) && !KeysTaken)
            {
                speed *= FastWalkFactor;
            }
        }

        // In the air, the way it was going carries on, as the game's jumps do.
        if (!IsOnFloor() && Jumping)
        {
            Velocity = Velocity with { Y = Velocity.Y - Gravity * delta };
            return;
        }

        Velocity = new Vector3(
            direction.X * speed,
            IsOnFloor() ? Mathf.Max(Velocity.Y, 0) : Velocity.Y - Gravity * delta,
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

    private static float Axis(Key negative, Key positive) =>
        (Input.IsPhysicalKeyPressed(positive) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(negative) ? 1f : 0f);
}
