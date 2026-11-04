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
/// stands in it: its head is hidden, it doesn't fidget, only its legs move when walking, its
/// hands reach for the controllers, and the body turns to follow where they look once the head
/// has turned far, or as they move.
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

    private IPlayer _player = null!;
    private Node3D _playerNode = null!;
    private CreatureNode? _body;
    private Vector3 _lastFeet;
    private float _speed;
    private float _facing;
    private float _strafeTurn;
    private bool _placed;
    private ArmChain? _leftArm;
    private ArmChain? _rightArm;
    private readonly List<(int Bone, Quaternion Rotation)> _upperPose = [];
    private bool _upperCaptured;

    /// <summary>The clip the body plays, named by sequence id and variation, as <c>13_0</c> for walking backwards.</summary>
    public string BodyClip => _body?.Clip ?? "";

    /// <summary>The WoW orientation the body faces, which the server hears in VR.</summary>
    public float Facing => _facing;

    /// <summary>The server, whose word decides whether the character is dead.</summary>
    public ServerWorld? Server { get; set; }

    private bool Vr => _playerNode is not DesktopPlayer;

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

            _body = new CreatureNode { Name = "Body", NoIdle = Vr };
            _body.Build(asset, "");
            AddChild(_body);
            _body.StartIdle();
            if (_playerNode is DesktopPlayer desktop)
            {
                desktop.HasBody = true;
            }
            else
            {
                FindArms(asset.Skeleton);
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

        _body.SetState(Server?.Self is { IsCorpse: true }, false);
        _body.HeadHidden = _playerNode is not DesktopPlayer desktop || desktop.FirstPerson;
        if (_playerNode is DesktopPlayer player)
        {
            AnimateDesktop(player, (float)delta);
        }
        else
        {
            AnimateVr((float)delta);
        }

        _body.Move(feet, _facing + _strafeTurn, false, _speed, 0, delta);
        _body.Animate(delta, 0);
        if (Vr)
        {
            HoldUpperBody();
            ReachForControllers();
        }
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
        _body!.PlayMotion(walking ? (_speed > RunAbove ? M2SequenceIds.Run : M2SequenceIds.Walk) : M2SequenceIds.Stand, _speed);
    }

    /// <summary>The WoW orientation the body faces. A camera's yaw is that orientation, as <c>WowCoords</c> says.</summary>
    private void UpdateVrFacing(float delta, bool walking)
    {
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

    /// <summary>Three bones of an arm, shoulder to hand, which two-bone IK bends to reach a point.</summary>
    private sealed record ArmChain(int Upper, int Lower, int Hand, bool Left);

    /// <summary>
    /// Finds each arm from the bone its palm's attachment hangs on, up through the forearm and the
    /// upper arm, skipping the short helper bones between, and notes which bones are the upper body.
    /// </summary>
    private void FindArms(CreatureSkeletonAsset asset)
    {
        var skeleton = _body!.Skeleton;
        _rightArm = ChainFrom(skeleton, asset.RightPalmBone, left: false);
        _leftArm = ChainFrom(skeleton, asset.LeftPalmBone, left: true);
        GD.Print($"VR body arms: left {_leftArm}, right {_rightArm}");

        if (asset.SpineBone >= 0)
        {
            for (var bone = 0; bone < skeleton.GetBoneCount(); bone++)
            {
                if (IsBelow(skeleton, bone, asset.SpineBone))
                {
                    _upperPose.Add((bone, Quaternion.Identity));
                }
            }
        }
    }

    private static ArmChain? ChainFrom(Skeleton3D skeleton, int hand, bool left)
    {
        if (hand < 0)
        {
            return null;
        }

        // Helper bones sit close to their parent; real arm bones are well apart.
        const float minLength = 0.12f;
        var lower = Ancestor(skeleton, hand, minLength);
        var upper = lower >= 0 ? Ancestor(skeleton, lower, minLength) : -1;
        return upper >= 0 ? new ArmChain(upper, lower, hand, left) : null;
    }

    /// <summary>The first ancestor that's at least <paramref name="minLength"/> away from the bone at rest.</summary>
    private static int Ancestor(Skeleton3D skeleton, int bone, float minLength)
    {
        var origin = skeleton.GetBoneGlobalRest(bone).Origin;
        for (var parent = skeleton.GetBoneParent(bone); parent >= 0; parent = skeleton.GetBoneParent(parent))
        {
            if (skeleton.GetBoneGlobalRest(parent).Origin.DistanceTo(origin) >= minLength)
            {
                return parent;
            }
        }

        return -1;
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

    /// <summary>Bends each arm so its hand is at the controller, with the elbow down and out.</summary>
    private void ReachForControllers()
    {
        if (_playerNode is not Xr.XrPlayer xr)
        {
            return;
        }

        var (left, right) = xr.Controllers;
        if (_leftArm is { } l)
        {
            Reach(l, left.GlobalPosition);
        }

        if (_rightArm is { } r)
        {
            Reach(r, right.GlobalPosition);
        }
    }

    /// <summary>Two-bone IK in the skeleton's space: the elbow goes where the arm's lengths and a pole say, then the hand onto the goal.</summary>
    private void Reach(ArmChain arm, Vector3 target)
    {
        var skeleton = _body!.Skeleton;
        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        var goal = toSkeleton * target;

        var upper = skeleton.GetBoneGlobalPose(arm.Upper);
        var lower = skeleton.GetBoneGlobalPose(arm.Lower);
        var hand = skeleton.GetBoneGlobalPose(arm.Hand);
        var a = upper.Origin;
        var b = lower.Origin;
        var c = hand.Origin;
        var upperLength = a.DistanceTo(b);
        var lowerLength = b.DistanceTo(c);
        var reach = goal - a;
        if (reach.LengthSquared() < 1e-6f || upperLength < 1e-4f || lowerLength < 1e-4f)
        {
            return;
        }

        var distance = Mathf.Clamp(reach.Length(), Mathf.Abs(upperLength - lowerLength) + 0.01f, upperLength + lowerLength - 0.001f);
        var toGoal = reach.Normalized();

        // The elbow bends down, and a little back and out to its own side. In the skeleton's space
        // the model faces +X with Y up, and its left is -Z.
        var pole = new Vector3(-0.3f, -1f, arm.Left ? -0.5f : 0.5f);
        var poleAcross = pole - (toGoal * pole.Dot(toGoal));
        poleAcross = poleAcross.LengthSquared() < 1e-6f ? Vector3.Down : poleAcross.Normalized();
        var cosAngle = Mathf.Clamp(((upperLength * upperLength) + (distance * distance) - (lowerLength * lowerLength)) / (2 * upperLength * distance), -1f, 1f);
        var elbow = a + (toGoal * upperLength * cosAngle) + (poleAcross * upperLength * Mathf.Sqrt(1 - (cosAngle * cosAngle)));

        // Turn the upper arm so the elbow goes there, then the forearm so the hand reaches the goal.
        var turnUpper = Rotation(b - a, elbow - a);
        SetGlobalRotation(skeleton, arm.Upper, new Basis(turnUpper) * upper.Basis);

        var handAfter = a + (turnUpper * (c - a));
        var turnLower = Rotation(handAfter - elbow, a + (toGoal * distance) - elbow);
        SetGlobalRotation(skeleton, arm.Lower, new Basis(turnLower * turnUpper) * lower.Basis);
    }

    private static Quaternion Rotation(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-8f || to.LengthSquared() < 1e-8f)
        {
            return Quaternion.Identity;
        }

        var f = from.Normalized();
        var t = to.Normalized();
        if (f.Dot(t) > 0.99999f)
        {
            return Quaternion.Identity;
        }

        return new Quaternion(f, t);
    }

    /// <summary>Sets a bone's pose so its rotation in the skeleton's space is <paramref name="global"/>.</summary>
    private static void SetGlobalRotation(Skeleton3D skeleton, int bone, Basis global)
    {
        var parent = skeleton.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? skeleton.GetBoneGlobalPose(parent).Basis : Basis.Identity;
        var local = parentBasis.Inverse() * global;
        skeleton.SetBonePoseRotation(bone, local.Orthonormalized().GetRotationQuaternion());
    }
}
