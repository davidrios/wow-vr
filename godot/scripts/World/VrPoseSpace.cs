using WowVR.Net;
using Godot;
using NQuaternion = System.Numerics.Quaternion;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace WowVR.World;

/// <summary>
/// Turns a VR body's frame into the character's space other players draw it in, and back:
/// origin at the feet where the server has the character, turned as its orientation, at the
/// character's own size (<c>docs/vr-multiplayer-protocol.md</c>). In the scene that frame is the
/// model's own, before its scale: forward +X, up +Y, right +Z; the character's space is WoW's:
/// forward +X, left +Y, up +Z.
/// </summary>
public static class VrPoseSpace
{
    /// <summary>The model's axes to the character's: X stays, Y up becomes Z, Z right becomes -Y.</summary>
    private static readonly Basis CharacterAxes = new(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0));

    /// <summary>Maps the model's axes to a camera's, as <see cref="VrBodyIk"/> does for the head.</summary>
    private static readonly Basis ModelToCamera = new(new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0));

    /// <summary>A hand at rest in the protocol: fingers forward, back of the hand up, so the left thumb points right, the right one left.</summary>
    private static Vector3 ThumbAtRest(bool left) => left ? Vector3.Back : Vector3.Forward;

    /// <summary>
    /// A frame's pose for others: <paramref name="feet"/> and <paramref name="yaw"/> are the
    /// character as the server has it, <paramref name="scale"/> how much bigger than the
    /// character the player's own body is drawn, and <paramref name="stand"/> where it stands.
    /// </summary>
    public static VrPoseMessage Encode(VrPose pose, Vector3 feet, float yaw, float scale, Vector3 stand, ushort sequence, ushort time)
    {
        var inverse = new Basis(Vector3.Up, yaw).Inverse();
        Vector3 Local(Vector3 world) => inverse * (world - feet) / scale;

        var head = inverse * pose.Eyes.Basis.Orthonormalized() * ModelToCamera;
        var body = Local(stand);
        return new VrPoseMessage
        {
            Sequence = sequence,
            Time = time,
            State = VrState.InVr | VrState.LeftTracked | VrState.RightTracked,
            Body = (new NVector2(body.X, -body.Z), 0f),
            Head = new VrJoint(ToCharacter(Local(pose.Eyes.Origin)), ToCharacter(head)),
            LeftHand = Hand(pose.LeftWrist, inverse, Local, left: true),
            RightHand = Hand(pose.RightWrist, inverse, Local, left: false),
            Triggers = new VrTriggers(Byte(pose.LeftGrip), Byte(pose.LeftTrigger), Byte(pose.RightGrip), Byte(pose.RightTrigger)),
        };
    }

    private static VrJoint Hand(Transform3D wrist, Basis inverse, System.Func<Vector3, Vector3> local, bool left)
    {
        // A wrist has its fingers along Y and its thumb's side along Z, as XR Tools' hands do.
        var basis = wrist.Basis.Orthonormalized();
        var turn = Frame(inverse * basis.Y, inverse * basis.Z) * Frame(Vector3.Right, ThumbAtRest(left)).Inverse();
        return new VrJoint(ToCharacter(local(wrist.Origin)), ToCharacter(turn));
    }

    /// <summary>How far from the character's place a received pose may put its head, hands and stance, across and up, in yards.</summary>
    private const float MaxReach = 3f;
    private const float MaxHeight = 4f;

    /// <summary>
    /// A received pose, back in the scene, on a character at <paramref name="feet"/> turned by
    /// <paramref name="yaw"/>, at its own size. Null without a head; untracked hands hang from it.
    /// </summary>
    public static (VrPose Pose, Vector3 Stand)? Decode(VrPoseMessage message, Vector3 feet, float yaw)
    {
        if (message.Head is not { } head)
        {
            return null;
        }

        var turn = new Basis(Vector3.Up, yaw);

        // A play space and arms' reach: a pose from another player can't draw them far from where the server has them.
        Vector3 World(NVector3 position) => feet + (turn * FromCharacter(NVector3.Clamp(position, new NVector3(-MaxReach, -MaxReach, -1f), new NVector3(MaxReach, MaxReach, MaxHeight))));

        var eyes = new Transform3D(turn * FromCharacter(head.Rotation) * ModelToCamera.Inverse(), World(head.Position));
        var restingLeft = new VrJoint(head.Position + new NVector3(0.15f, 0.25f, -0.8f), NQuaternion.CreateFromAxisAngle(NVector3.UnitY, 1.2f));
        var restingRight = restingLeft with { Position = restingLeft.Position * new NVector3(1, -1, 1) };
        var triggers = message.Triggers ?? default;
        var pose = new VrPose(
            eyes,
            Wrist((message.State & VrState.LeftTracked) != 0 ? message.LeftHand ?? restingLeft : restingLeft, turn, World, left: true),
            Wrist((message.State & VrState.RightTracked) != 0 ? message.RightHand ?? restingRight : restingRight, turn, World, left: false),
            triggers.LeftGrip / 255f,
            triggers.LeftTrigger / 255f,
            triggers.RightGrip / 255f,
            triggers.RightTrigger / 255f);
        var stand = message.Body is { } body ? World(new NVector3(body.Offset.X, body.Offset.Y, 0)) : feet;
        return (pose, stand);
    }

    private static Transform3D Wrist(VrJoint joint, Basis turn, System.Func<NVector3, Vector3> world, bool left)
    {
        var local = FromCharacter(joint.Rotation);
        var fingers = turn * (local * Vector3.Right);
        var thumb = turn * (local * ThumbAtRest(left));
        var y = fingers.Normalized();
        var z = (thumb - (y * thumb.Dot(y))).Normalized();
        return new Transform3D(new Basis(y.Cross(z), y, z), world(joint.Position));
    }

    /// <summary>Between two poses, a share <paramref name="t"/> of the way, for drawing smoothly between arrivals.</summary>
    public static VrPoseMessage Between(VrPoseMessage a, VrPoseMessage b, float t)
    {
        VrJoint? Joint(VrJoint? x, VrJoint? y) => x is { } p && y is { } q
            ? new VrJoint(NVector3.Lerp(p.Position, q.Position, t), NQuaternion.Slerp(p.Rotation, q.Rotation, t))
            : y ?? x;

        return b with
        {
            Head = Joint(a.Head, b.Head),
            LeftHand = Joint(a.LeftHand, b.LeftHand),
            RightHand = Joint(a.RightHand, b.RightHand),
            Body = a.Body is { } p && b.Body is { } q ? (NVector2.Lerp(p.Offset, q.Offset, t), Mathf.Lerp(p.Yaw, q.Yaw, t)) : b.Body,
        };
    }

    private static NVector3 ToCharacter(Vector3 v) => new(v.X, -v.Z, v.Y);

    private static Vector3 FromCharacter(NVector3 v) => new(v.X, v.Z, -v.Y);

    private static NQuaternion ToCharacter(Basis turn)
    {
        var q = (CharacterAxes * turn.Orthonormalized() * CharacterAxes.Inverse()).GetRotationQuaternion();
        return new NQuaternion(q.X, q.Y, q.Z, q.W);
    }

    private static Basis FromCharacter(NQuaternion q) =>
        CharacterAxes.Inverse() * new Basis(new Quaternion(q.X, q.Y, q.Z, q.W).Normalized()) * CharacterAxes;

    /// <summary>A turn whose X is along <paramref name="along"/> and whose Y is <paramref name="side"/>, made square to it.</summary>
    private static Basis Frame(Vector3 along, Vector3 side)
    {
        var x = along.Normalized();
        var y = (side - (x * side.Dot(x))).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    private static byte Byte(float amount) => (byte)Mathf.Clamp(Mathf.RoundToInt(amount * 255), 0, 255);
}
