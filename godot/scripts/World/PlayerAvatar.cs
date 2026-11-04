using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Desktop;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// The player's own character, standing at their feet. On desktop it faces where the camera
/// turns and shows whole behind it, or headless in first person. In VR the player stands in it:
/// its head is hidden, and the body turns to follow where they look once the head has turned far,
/// or as they move.
/// </summary>
public partial class PlayerAvatar : Node3D
{
    /// <summary>Under this many yards a second, it stands.</summary>
    private const float StandSpeed = 0.4f;

    /// <summary>More than this in a frame is a teleport or the world re-centering, not walking.</summary>
    private const float JumpYards = 5f;

    private const float SpeedSmoothing = 8f;

    /// <summary>In VR, how far the head turns before the body follows, and how fast it does.</summary>
    private const float VrTurnSlack = 0.8f;
    private const float VrTurnRate = 4f;

    private IPlayer _player = null!;
    private Node3D _playerNode = null!;
    private CreatureNode? _body;
    private Vector3 _lastFeet;
    private float _speed;
    private float _facing;
    private bool _placed;

    public void Start(CreatureLibrary library, CharacterAppearance appearance, IPlayer player, Node3D playerNode)
    {
        _player = player;
        _playerNode = playerNode;
        Task.Run(() => library.BuildCharacter(appearance)).ContinueWith(built => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree() || built.Result is not { } asset)
            {
                return;
            }

            _body = new CreatureNode { Name = "Body" };
            _body.Build(asset, "");
            AddChild(_body);
            _body.StartIdle();
            if (_playerNode is DesktopPlayer desktop)
            {
                desktop.HasBody = true;
            }
        }).CallDeferred());
    }

    public override void _Process(double delta)
    {
        if (_body is null || !_playerNode.IsInsideTree())
        {
            return;
        }

        var feet = _player.Feet;
        var moved = new Vector3(feet.X - _lastFeet.X, 0, feet.Z - _lastFeet.Z).Length();
        _lastFeet = feet;
        if (!_placed || moved > JumpYards || delta <= 0)
        {
            _placed = true;
            moved = 0;
        }
        else
        {
            var speed = moved / (float)delta;
            _speed = Mathf.Lerp(_speed, speed, 1f - Mathf.Exp(-SpeedSmoothing * (float)delta));
        }

        // Legs walk on the ground only. Flying and swimming hold their idle for now.
        var walking = _player.OnGround && !_player.Flying && _speed > StandSpeed;
        UpdateFacing((float)delta, walking);
        _body.HeadHidden = _playerNode is not DesktopPlayer desktop || desktop.FirstPerson;
        _body.Move(feet, _facing, walking, _speed, 0, delta);
        _body.Animate(delta, 0);
    }

    /// <summary>The WoW orientation the body faces. A camera's yaw is that orientation, as <c>WowCoords</c> says.</summary>
    private void UpdateFacing(float delta, bool walking)
    {
        if (_playerNode is DesktopPlayer desktop)
        {
            _facing = Formats.WowCoords.OrientationFromCameraYaw(desktop.Rotation.Y);
            return;
        }

        var forward = -_player.Eyes.Basis.Z;
        if (new Vector2(forward.X, forward.Z).LengthSquared() < 1e-4f)
        {
            return;
        }

        var look = Formats.WowCoords.OrientationFromCameraYaw(Mathf.Atan2(-forward.X, -forward.Z));
        var gap = Mathf.AngleDifference(_facing, look);
        if (!walking && Mathf.Abs(gap) < VrTurnSlack)
        {
            return;
        }

        _facing = Mathf.LerpAngle(_facing, look, 1f - Mathf.Exp(-VrTurnRate * delta));
    }
}
