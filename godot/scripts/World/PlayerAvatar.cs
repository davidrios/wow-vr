using System.Collections.Generic;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Desktop;
using WowVR.Formats.Models;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// The player's own character, standing at their feet. On desktop it faces where the character
/// turns and shows whole behind it, or headless in first person; it runs, walks backwards,
/// strafes turned toward where it goes as WoW's characters do, swims and jumps. In VR the player
/// stands in it, as in VRChat: it's scaled so its eyes are at theirs, its head is hidden, it
/// doesn't fidget, and only its legs walk. <see cref="VrBodyIk"/> poses the rest from the headset
/// and hands. The body turns to follow where they look once the head has turned far, or as they
/// move, and stays put while they lean, until the head is too far from the hips or they stand up
/// straight again.
/// </summary>
public partial class PlayerAvatar : Node3D
{
    /// <summary>Under this many yards a second, it stands.</summary>
    private const float StandSpeed = 0.4f;

    /// <summary>More than this in a frame is a teleport or the world re-centering, not walking.</summary>
    private const float JumpYards = 5f;

    private const float SpeedSmoothing = 8f;

    /// <summary>Faster than this, in yards a second, runs rather than walks.</summary>
    private const float RunAbove = 4.5f;

    /// <summary>In VR, how far the head turns before the body follows, and how fast it does.</summary>
    private const float VrTurnSlack = 0.8f;
    private const float VrTurnRate = 4f;

    /// <summary>How fast the body turns toward a strafe, in radians a second.</summary>
    private const float StrafeTurnRate = 10f;

    /// <summary>How far the head may lean from over the hips before the body steps after it, as a share of the spine.</summary>
    private const float LeanSlack = 0.45f;

    /// <summary>With the eyes this near their standing height, the player is upright, and the body comes back under the head.</summary>
    private const float UprightShare = 0.92f;
    private const float RecenterRate = 1.5f;

    /// <summary>How fast the standing height rises to a new highest, so a moment on tiptoe hardly counts.</summary>
    private const float StandingRise = 0.7f;

    private const float MinBodyScale = 0.3f;
    private const float MaxBodyScale = 3f;

    /// <summary>The attachment a helmet sits on, at the top of the head.</summary>
    private const int HelmetAttachment = 11;

    private IPlayer _player = null!;
    private Node3D _playerNode = null!;
    private CreatureNode? _body;
    private Vector3 _lastFeet;
    private Vector3 _lastAt;
    private float _speed;
    private float _facing;
    private float _strafeTurn;
    private bool _placed;
    private VrBodyIk? _ik;
    private float _assetScale = 1f;
    private float _bodyScale = 1f;
    private float _standingEye;
    private Vector3 _root;
    private bool _rooted;
    private readonly List<(int Bone, Quaternion Rotation)> _upperPose = [];
    private bool _upperCaptured;

    /// <summary>The VR solver, once the body is built, for checks.</summary>
    public VrBodyIk? Ik => _ik;

    /// <summary>How much bigger than the model the VR body is drawn, so its eyes are at the player's.</summary>
    public float BodyScale => _bodyScale;

    /// <summary>For checks' pictures: keeps the head on in VR, where the player would see inside it.</summary>
    public bool ShowHead { get; set; }

    /// <summary>The clip the body plays, named by sequence id and variation, as <c>13_0</c> for walking backwards.</summary>
    public string BodyClip => _body?.Clip ?? "";

    /// <summary>The WoW orientation the body faces, which the server hears in VR.</summary>
    public float Facing => _facing;

    /// <summary>The body's model, once built, for spells' animations and effects.</summary>
    public CreatureNode? Body => _body;

    /// <summary>The server, whose word decides whether the character is dead.</summary>
    public ServerWorld? Server { get; set; }

    private bool Vr => _playerNode is not DesktopPlayer;

    private CreatureLibrary _library = null!;
    private int _buildVersion;

    public void Start(CreatureLibrary library, CharacterAppearance appearance, IPlayer player, Node3D playerNode)
    {
        _player = player;
        _playerNode = playerNode;
        _library = library;
        Build(appearance);
    }

    /// <summary>The character put something on or took it off: the body is built again in its new looks.</summary>
    public void Redress(CharacterAppearance appearance) => Build(appearance);

    private void Build(CharacterAppearance appearance)
    {
        var version = ++_buildVersion;
        var library = _library;
        Task.Run(() => library.BuildCharacter(appearance)).ContinueWith(built => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || !IsInsideTree() || version != _buildVersion || built.Result is not { } asset)
            {
                return;
            }

            // The old body goes only once the new one is ready, so the character never vanishes.
            if (_body is not null)
            {
                RemoveChild(_body);
                _body.QueueFree();
                _body = null;
                _upperPose.Clear();
                _upperCaptured = false;
                _ik = null;
            }

            _body = new CreatureNode { Name = "Body", NoIdle = Vr };
            _body.Build(asset, "");
            AddChild(_body);
            _body.StartIdle();
            _assetScale = asset.Scale;
            if (_playerNode is DesktopPlayer desktop)
            {
                desktop.HasBody = true;
            }
            else
            {
                StartVr(asset);
            }
        }).CallDeferred());
    }

    /// <summary>Finds the body's spine, arms and legs for the solver, and notes which bones are the upper body.</summary>
    private void StartVr(CreatureAsset asset)
    {
        var skeleton = _body!.Skeleton;
        Vector3? headTop = asset.Attachments.TryGetValue(HelmetAttachment, out var helmet) && skeleton.FindBone(helmet.Bone) is >= 0 and var bone
            ? skeleton.GetBoneGlobalRest(bone).Origin + helmet.Offset
            : null;
        _ik = VrBodyIk.Create(skeleton, asset.Skeleton, headTop);
        GD.Print(_ik is null ? "VR body: no spine, arms or fingers found, so it only stands" : $"VR body: {_ik.Description}");
        if (_playerNode is Xr.XrPlayer xr)
        {
            xr.ControllerHandsVisible = _ik is null;
        }

        var spine = asset.Skeleton.SpineBone;
        if (spine >= 0)
        {
            for (var b = 0; b < skeleton.GetBoneCount(); b++)
            {
                if (IsBelow(skeleton, b, spine))
                {
                    _upperPose.Add((b, Quaternion.Identity));
                }
            }

            // The hips hold the spine as well as the legs, and their sway and twist while walking
            // would carry the whole upper body. The legs hang from the waist, which keeps walking.
            if (skeleton.GetBoneParent(spine) is >= 0 and var hips)
            {
                _upperPose.Add((hips, Quaternion.Identity));
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_body is null || !_playerNode.IsInsideTree())
        {
            return;
        }

        var feet = _player.Feet;
        var jumped = !_placed || new Vector3(feet.X - _lastFeet.X, 0, feet.Z - _lastFeet.Z).Length() > JumpYards;
        _lastFeet = feet;
        _placed = true;

        var dead = Server?.Self is { IsCorpse: true };
        _body.SetState(dead, false);
        _body.HeadHidden = !ShowHead && (_playerNode is not DesktopPlayer desktop || desktop.FirstPerson);
        if (_playerNode is DesktopPlayer player)
        {
            UpdateSpeed(feet, jumped, delta);
            AnimateDesktop(player, (float)delta);
            _body.Move(feet, _facing + _strafeTurn, false, _speed, 0, delta);
            _body.Animate(delta, 0);
            return;
        }

        // In VR the pace is the body's, not the feet's: leaning moves the feet under the head, but
        // the body stays where it stood.
        var stand = PlaceVr(feet, jumped, (float)delta);
        UpdateSpeed(stand, jumped, delta);
        AnimateVr((float)delta);
        _body.Move(stand, _facing, false, _speed / _bodyScale, 0, delta);
        _body.Animate(delta, 0);
        if (!dead)
        {
            HoldUpperBody();
            if (_ik is not null && _playerNode is Xr.XrPlayer xr)
            {
                var (left, right) = xr.Wrists;
                var fingers = xr.Fingers;
                _ik.Solve(new VrPose(_player.Eyes, left, right, fingers.LeftGrip, fingers.LeftTrigger, fingers.RightGrip, fingers.RightTrigger));
            }
        }
    }

    /// <summary>How fast the body goes, smoothed, from where it stands each frame.</summary>
    private void UpdateSpeed(Vector3 at, bool jumped, double delta)
    {
        var moved = new Vector3(at.X - _lastAt.X, 0, at.Z - _lastAt.Z).Length();
        _lastAt = at;
        if (!jumped && delta > 0)
        {
            _speed = Mathf.Lerp(_speed, moved / (float)delta, 1f - Mathf.Exp(-SpeedSmoothing * (float)delta));
        }
    }

    /// <summary>
    /// Scales the body so its eyes are at the player's standing height, and says where it stands:
    /// under the head, but staying put while the player leans, until the head is too far from the
    /// hips or they stand straight again.
    /// </summary>
    private Vector3 PlaceVr(Vector3 feet, bool jumped, float delta)
    {
        if (_ik is null)
        {
            return feet;
        }

        var eyes = _player.Eyes;
        var eyeHeight = eyes.Origin.Y - feet.Y;
        if (eyeHeight > 0.5f * AppSettings.WorldScale)
        {
            _standingEye = _standingEye <= 0f ? eyeHeight
                : eyeHeight > _standingEye ? Mathf.Lerp(_standingEye, eyeHeight, 1f - Mathf.Exp(-StandingRise * delta))
                : _standingEye;
        }

        if (_standingEye > 0f)
        {
            _bodyScale = Mathf.Clamp(_standingEye / (_ik.EyeHeight * _assetScale), MinBodyScale, MaxBodyScale);
            _body!.Scale = Vector3.One * _bodyScale;
        }

        // Where it stands is kept in the play space, which walking with the stick and turning move,
        // and the player's own steps and leaning don't. XR Tools moves the feet under the head, so
        // they can't say whether the player leans.
        var space = _playerNode.GlobalTransform;
        var size = _bodyScale * _assetScale;
        var pivot = _ik.HeadPivotFor(eyes, size);
        var neck = space.AffineInverse() * pivot;
        neck.Y = 0;
        if (jumped || !_rooted)
        {
            _root = neck;
            _rooted = true;
        }

        // Leaning, the body stays where it stood, up to the slack; past it, it follows the head. A
        // head that's dropped may be as far ahead as the spine reaches bent at the hips by that much,
        // measured at its pivot, which a nod moves less than the eyes.
        var spine = _ik.SpineLength * size;
        var standingPivot = _standingEye - ((_ik.EyeHeight - _ik.HeadHeight) * size);
        var dropped = Mathf.Clamp(standingPivot - (pivot.Y - feet.Y), 0f, spine);
        var bowed = 0.97f * Mathf.Sqrt((spine * spine) - ((spine - dropped) * (spine - dropped)));
        var slack = Mathf.Max(LeanSlack * spine, bowed);
        var lean = neck - _root;
        if (lean.Length() > slack)
        {
            _root = neck - (lean.Normalized() * slack);
        }

        if (eyeHeight > UprightShare * _standingEye)
        {
            _root = _root.Lerp(neck, 1f - Mathf.Exp(-RecenterRate * delta));
        }

        var stand = space * _root;
        return new Vector3(stand.X, feet.Y, stand.Z);
    }

    /// <summary>
    /// WoW's movement clips: running forward, strafing with the body turned toward where it goes,
    /// walking backwards turned the other way, swimming, and in the air, jumping.
    /// </summary>
    private void AnimateDesktop(DesktopPlayer player, float delta)
    {
        _facing = Formats.WowCoords.OrientationFromCameraYaw(player.Rotation.Y);
        var intent = player.MoveIntent;
        var moving = intent != Vector2.Zero && _speed > StandSpeed;
        var back = intent.Y < 0;

        // Forward and to the side turns toward it, up to a quarter turn; back and to the side the other way.
        var turn = !moving || player.Swimming || player.Flying ? 0f
            : back ? Mathf.Atan2(-intent.X, -intent.Y)
            : Mathf.Atan2(intent.X, intent.Y);
        _strafeTurn = Mathf.LerpAngle(_strafeTurn, turn, 1f - Mathf.Exp(-StrafeTurnRate * delta));

        if (player.Swimming || player.Flying)
        {
            var sequence = !moving ? M2SequenceIds.SwimIdle
                : back ? M2SequenceIds.SwimBackwards
                : intent.Y == 0 && intent.X > 0 ? M2SequenceIds.SwimLeft
                : intent.Y == 0 && intent.X < 0 ? M2SequenceIds.SwimRight
                : M2SequenceIds.Swim;
            _body!.PlayMotion(sequence, _speed);
        }
        else if (!player.OnGround && (player.Jumping || player.Velocity.Y < -4f))
        {
            _body!.PlayMotion(player.Jumping ? M2SequenceIds.Jump : M2SequenceIds.Fall, 0, 1f);
        }
        else if (!moving)
        {
            _body!.PlayMotion(M2SequenceIds.Stand, 0);
        }
        else if (back)
        {
            _body!.PlayMotion(M2SequenceIds.WalkBackwards, _speed);
        }
        else
        {
            _body!.PlayMotion(_speed > RunAbove ? M2SequenceIds.Run : M2SequenceIds.Walk, _speed);
        }
    }

    /// <summary>Stands still, or walks with the legs at the pace the player moves.</summary>
    private void AnimateVr(float delta)
    {
        var walking = _player.OnGround && !_player.Flying && _speed > StandSpeed;
        UpdateVrFacing(delta, walking);
        _strafeTurn = 0;
        // The legs are scaled with the body, so the same pace is a slower stride.
        _body!.PlayMotion(walking ? (_speed > RunAbove ? M2SequenceIds.Run : M2SequenceIds.Walk) : M2SequenceIds.Stand, _speed / _bodyScale);
    }

    /// <summary>
    /// The WoW orientation the body faces. A camera's yaw is that orientation, as <c>WowCoords</c>
    /// says. Looking straight down or up, the top of the head says which way the player faces.
    /// </summary>
    private void UpdateVrFacing(float delta, bool walking)
    {
        var eyes = _player.Eyes.Basis;
        var forward = -eyes.Z;
        var flat = new Vector2(forward.X, forward.Z);
        if (flat.LengthSquared() < 0.25f)
        {
            var up = eyes.Y * (forward.Y < 0 ? 1 : -1);
            flat += new Vector2(up.X, up.Z);
        }

        if (flat.LengthSquared() < 1e-4f)
        {
            return;
        }

        var look = Formats.WowCoords.OrientationFromCameraYaw(Mathf.Atan2(-flat.X, -flat.Y));
        var gap = Mathf.AngleDifference(_facing, look);
        if (!walking && Mathf.Abs(gap) < VrTurnSlack)
        {
            return;
        }

        _facing = Mathf.LerpAngle(_facing, look, 1f - Mathf.Exp(-VrTurnRate * delta));
    }

    private static bool IsBelow(Skeleton3D skeleton, int bone, int ancestor)
    {
        for (var b = bone; b >= 0; b = skeleton.GetBoneParent(b))
        {
            if (b == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Puts the upper body back to standing after each animation step, so only the legs walk.</summary>
    private void HoldUpperBody()
    {
        var skeleton = _body!.Skeleton;
        if (!_upperCaptured)
        {
            for (var i = 0; i < _upperPose.Count; i++)
            {
                _upperPose[i] = (_upperPose[i].Bone, skeleton.GetBonePoseRotation(_upperPose[i].Bone));
            }

            _upperCaptured = _upperPose.Count > 0;
            return;
        }

        foreach (var (bone, rotation) in _upperPose)
        {
            skeleton.SetBonePoseRotation(bone, rotation);
        }
    }
}
