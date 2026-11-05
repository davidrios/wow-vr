using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Converters;
using Godot;

namespace WowVR.World;

/// <summary>
/// Where the tracked body is this frame, in the world: the eyes, as the headset's camera, and each
/// wrist with the fingers along its +Y and the thumb's side along +Z, as XR Tools' hands have them.
/// Grip and trigger go from 0 to 1.
/// </summary>
public readonly record struct VrPose(
    Transform3D Eyes,
    Transform3D LeftWrist,
    Transform3D RightWrist,
    float LeftGrip,
    float LeftTrigger,
    float RightGrip,
    float RightTrigger);

/// <summary>
/// Poses the player's own WoW body from the headset and the two hands, the way VRChat's avatars
/// follow their players. The head goes where the eyes are and turns with them. The hips drop and
/// the spine bends to reach it, so crouching and bowing look like it, and the legs bend to keep the
/// feet where the walk or stand put them. The shoulders rise and reach forward with the hands. Each
/// arm is a hinge: the elbow goes where the wrist's turn says the forearm runs, blended with down and
/// out, the forearm takes half the wrist's twist, and the hand turns as the real one does. Grip and
/// trigger close the fingers.
/// <para>
/// Works in the skeleton's space, where the model faces +X with Y up and its left at -Z, and every
/// bone's rest has no turn, so a bone's turn in that space is from the rest pose.
/// </para>
/// </summary>
public sealed class VrBodyIk
{
    // Key bones, as in WoWModelViewer's enums.h.
    private const int KeyArmLeft = 0;
    private const int KeyArmRight = 1;
    private const int KeyShoulderLeft = 2;
    private const int KeyShoulderRight = 3;
    private const int KeySpineLow = 4;
    private const int KeyWaist = 5;
    private const int KeyHead = 6;
    private const int KeyFirstRightFinger = 8;
    private const int KeyFirstLeftFinger = 13;
    private const int FingersPerHand = 5;

    /// <summary>A hand's fingers among its five key bones: the index first, the thumb last.</summary>
    private const int IndexFinger = 0;
    private const int MiddleFinger = 1;
    private const int Thumb = 4;

    /// <summary>The share of the head's turn from the body that the spine takes, shared out up it.</summary>
    private const float SpineTwistShare = 0.45f;
    private const float MaxSpineTwist = 1.3f;

    /// <summary>The share of the hand's twist about the forearm that the forearm takes, as a real one does.</summary>
    private const float ForearmTwistShare = 0.5f;

    /// <summary>How much the wrist's turn decides the elbow, against plain down and out.</summary>
    private const float ElbowFromWrist = 0.7f;

    /// <summary>How far toward the body's middle the elbow may point, as the cosine's share.</summary>
    private const float ElbowInward = 0.15f;

    private static readonly float MaxShoulderLift = Mathf.DegToRad(28f);
    private static readonly float MaxShoulderReach = Mathf.DegToRad(20f);

    /// <summary>How far the hips may rise above standing, as a share of the spine, for reaching up on tiptoe.</summary>
    private const float MaxHipRise = 0.05f;

    /// <summary>How far they may drop, as a share of their height: about to kneeling.</summary>
    private const float MaxHipDrop = 0.65f;

    /// <summary>The steepest the spine leans when the head is far ahead of the hips, as the cosine's least share.</summary>
    private const float LeastSpineUpright = 0.35f;

    /// <summary>Maps the model's axes (forward +X, up +Y, right +Z) to a camera's (forward -Z, up +Y, right +X).</summary>
    private static readonly Basis ModelToCamera = new(new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0));

    private readonly Skeleton3D _skeleton;
    private readonly int _hips;
    private readonly int[] _spine;
    private readonly int _head;
    private readonly Arm _left;
    private readonly Arm _right;
    private readonly Leg[] _legs;

    /// <summary>The eyes, at rest in the skeleton's space, and the head's pivot.</summary>
    private readonly Vector3 _eyeRest;
    private readonly Vector3 _headRest;

    /// <summary>From the lowest spine bone to the head's pivot, standing.</summary>
    public float SpineLength { get; }

    /// <summary>How high the eyes are at rest, in the model's units.</summary>
    public float EyeHeight => _eyeRest.Y;

    /// <summary>How high the head's pivot is at rest, in the model's units.</summary>
    public float HeadHeight => _headRest.Y;

    /// <summary>Bones that checks measure: the head, each arm's shoulder joint, elbow and wrist, the feet, then the hips.</summary>
    public (int Head, int[] Left, int[] Right, int[] Feet, int Hips) Bones =>
        (_head, [_left.Upper, _left.Lower, _left.Hand], [_right.Upper, _right.Lower, _right.Hand], [.. _legs.Select(l => l.Foot)], _hips);

    /// <summary>The fingers each hand closes, for checks.</summary>
    public int[] FingerBones(bool left) => [.. (left ? _left : _right).Fingers.Select(f => f.Bone)];

    /// <summary>The direction the hand's fingers point at rest, in its own space.</summary>
    public Vector3 FingersRest(bool left) => (left ? _left : _right).HandRest.X;

    /// <summary>The bones of each arm, as found, for checks to print.</summary>
    public string Description =>
        $"spine {string.Join('-', _spine)} head {_head} hips {_hips}; left arm {_left}; right arm {_right}; legs {string.Join(", ", _legs.Select(l => l.ToString()))}";

    /// <summary>
    /// One arm: the collarbone (or -1), the upper arm, the forearm and the hand, with the rest
    /// directions that say how the elbow bends and how the hand lies, and the fingers to close.
    /// </summary>
    private sealed record Arm(int Clavicle, int Upper, int Lower, int Hand, bool Left)
    {
        public required Vector3 UpperRest { get; init; }
        public required Vector3 LowerRest { get; init; }
        public required Vector3 HingeRest { get; init; }

        /// <summary>The hand's rest frame from its fingers' and thumb's directions.</summary>
        public required Basis HandRest { get; init; }

        /// <summary>From the hand's pivot to the elbow's, at rest.</summary>
        public required Vector3 ElbowFromHand { get; init; }

        /// <summary>The fingers' fist turns, by which finger: 0 the index to 4 the thumb.</summary>
        public required (int Bone, int Finger, Quaternion Fist)[] Fingers { get; init; }

        public override string ToString() => $"clavicle {Clavicle} upper {Upper} forearm {Lower} hand {Hand}, {Fingers.Length} fingers";
    }

    private sealed record Leg(int Thigh, int Knee, int Foot)
    {
        public override string ToString() => $"{Thigh}-{Knee}-{Foot}";
    }

    private VrBodyIk(Skeleton3D skeleton, int hips, int[] spine, int head, Arm left, Arm right, Leg[] legs, Vector3 eyeRest)
    {
        _skeleton = skeleton;
        _hips = hips;
        _spine = spine;
        _head = head;
        _left = left;
        _right = right;
        _legs = legs;
        _eyeRest = eyeRest;
        _headRest = RestOf(head);
        SpineLength = RestOf(spine[0]).DistanceTo(_headRest);
    }

    /// <summary>
    /// Finds the spine, arms and legs by their key bones, or null for a model without them.
    /// <paramref name="headTop"/> is where a helmet sits at rest, which says where the eyes are.
    /// </summary>
    public static VrBodyIk? Create(Skeleton3D skeleton, CreatureSkeletonAsset asset, Vector3? headTop)
    {
        var keys = asset.KeyBones;
        int Key(int key) => Array.IndexOf(keys, key);

        var spineLow = Key(KeySpineLow);
        var head = Key(KeyHead);
        var waist = Key(KeyWaist);
        if (spineLow < 0 || head < 0 || !IsBelow(skeleton, head, spineLow))
        {
            return null;
        }

        // The hips hold the spine and the legs both, so lowering them lowers the whole body.
        var hips = skeleton.GetBoneParent(spineLow);
        if (hips < 0)
        {
            return null;
        }

        // From the lowest spine bone up to the one the head hangs on.
        var spine = new List<int>();
        for (var bone = skeleton.GetBoneParent(head); bone >= 0; bone = skeleton.GetBoneParent(bone))
        {
            spine.Insert(0, bone);
            if (bone == spineLow)
            {
                break;
            }
        }

        var left = FindArm(skeleton, keys, Key(KeyShoulderLeft), Key(KeyArmLeft), KeyFirstLeftFinger, asset.LeftFist, left: true);
        var right = FindArm(skeleton, keys, Key(KeyShoulderRight), Key(KeyArmRight), KeyFirstRightFinger, asset.RightFist, left: false);
        if (left is null || right is null)
        {
            return null;
        }

        var headRest = skeleton.GetBoneGlobalRest(head).Origin;

        // The eyes are a quarter of the way up from the head's pivot to the top, and in front of
        // where a helmet sits. For a human, that's between the bones that move the eyelids.
        var top = headTop is { } t && t.Y - headRest.Y > 0.05f ? t : headRest + new Vector3(0.06f, 0.18f, 0);
        var rise = top.Y - headRest.Y;
        var eyes = new Vector3(top.X + (0.27f * rise), headRest.Y + (0.25f * rise), headRest.Z);

        return new VrBodyIk(skeleton, hips, spine.ToArray(), head, left, right, waist >= 0 ? FindLegs(skeleton, waist) : [], eyes);
    }

    private static Arm? FindArm(Skeleton3D skeleton, int[] keys, int clavicle, int upper, int firstFinger, FingerPose[] fist, bool left)
    {
        if (upper < 0)
        {
            return null;
        }

        var fingers = Enumerable.Range(0, FingersPerHand).Select(f => Array.IndexOf(keys, firstFinger + f)).ToArray();
        var middle = fingers[MiddleFinger] >= 0 ? fingers[MiddleFinger] : fingers.FirstOrDefault(f => f >= 0, -1);
        if (middle < 0)
        {
            return null;
        }

        // The hand is what the fingers hang on, and the forearm is the bone below the upper arm on the way to it.
        var hand = skeleton.GetBoneParent(middle);
        var lower = hand;
        while (lower >= 0 && skeleton.GetBoneParent(lower) != upper)
        {
            lower = skeleton.GetBoneParent(lower);
        }

        if (hand < 0 || lower < 0 || lower == hand)
        {
            return null;
        }

        var shoulderAt = RestOf(skeleton, upper);
        var elbowAt = RestOf(skeleton, lower);
        var wristAt = RestOf(skeleton, hand);
        var upperRest = elbowAt - shoulderAt;
        var lowerRest = wristAt - elbowAt;

        // The elbow bends the forearm forward. Hanging nearly straight, the hinge's axis would be
        // noise, so it's taken as across the body, which is what that bend means.
        var hinge = upperRest.Cross(lowerRest);
        hinge = hinge.Length() > 0.05f * upperRest.Length() * lowerRest.Length() ? hinge.Normalized() : Vector3.Back;

        var fingerTips = fingers.Take(Thumb).Where(f => f >= 0).Select(f => RestOf(skeleton, f)).ToArray();
        var along = (fingerTips.Aggregate(Vector3.Zero, (sum, p) => sum + p) / fingerTips.Length) - wristAt;
        var thumbSide = fingers[Thumb] >= 0 ? RestOf(skeleton, fingers[Thumb]) - wristAt : Vector3.Right;

        var fistTurns = new List<(int, int, Quaternion)>();
        foreach (var pose in fist)
        {
            var finger = Array.IndexOf(fingers, pose.Bone);
            if (finger >= 0)
            {
                fistTurns.Add((pose.Bone, finger, pose.Rotation.Normalized()));
            }
        }

        return new Arm(clavicle, upper, lower, hand, left)
        {
            UpperRest = upperRest,
            LowerRest = lowerRest,
            HingeRest = hinge,
            HandRest = Frame(along, thumbSide),
            ElbowFromHand = elbowAt - wristAt,
            Fingers = fistTurns.ToArray(),
        };
    }

    /// <summary>
    /// Each leg is the waist's child that reaches lowest on its side, with the knee the bone on the
    /// way down nearest halfway and the foot the one below it.
    /// </summary>
    private static Leg[] FindLegs(Skeleton3D skeleton, int waist)
    {
        var legs = new List<Leg>();
        foreach (var side in new[] { -1f, 1f })
        {
            var lowest = -1;
            for (var bone = 0; bone < skeleton.GetBoneCount(); bone++)
            {
                var at = RestOf(skeleton, bone);
                if (bone != waist && IsBelow(skeleton, bone, waist) && at.Z * side > 0.02f && (lowest < 0 || at.Y < RestOf(skeleton, lowest).Y))
                {
                    lowest = bone;
                }
            }

            if (lowest < 0)
            {
                continue;
            }

            var path = new List<int>();
            for (var bone = lowest; bone >= 0 && bone != waist; bone = skeleton.GetBoneParent(bone))
            {
                path.Add(bone);
            }

            if (path.Count < 3)
            {
                continue;
            }

            var thigh = path[^1];
            var halfway = (RestOf(skeleton, thigh).Y + RestOf(skeleton, lowest).Y) / 2;
            var knee = path.Skip(1).SkipLast(1).MinBy(b => Mathf.Abs(RestOf(skeleton, b).Y - halfway));
            var foot = path[path.IndexOf(knee) - 1];
            legs.Add(new Leg(thigh, knee, foot));
        }

        return legs.ToArray();
    }

    /// <summary>Where the head's pivot goes for eyes there, in the world, with the model as big as <paramref name="worldScale"/> makes it.</summary>
    public Vector3 HeadPivotFor(Transform3D eyes, float worldScale) =>
        eyes.Origin + (eyes.Basis.Orthonormalized() * ModelToCamera * (_headRest - _eyeRest) * worldScale);

    /// <summary>
    /// Poses the body for this frame, after the animation has posed it, standing or walking. The
    /// skeleton must already be where the body stands and facing its way.
    /// </summary>
    public void Solve(VrPose pose)
    {
        UndoWhatTheAnimationKept();
        var toSkeleton = _skeleton.GlobalTransform.AffineInverse();
        var turn = toSkeleton.Basis.Orthonormalized();
        var headTurn = turn * pose.Eyes.Basis.Orthonormalized() * ModelToCamera;
        var headTarget = (toSkeleton * pose.Eyes.Origin) + (headTurn * (_headRest - _eyeRest));

        PlaceHips(headTarget);
        BendSpine(headTarget, headTurn);
        SetGlobalRotation(_head, headTurn);

        SolveArm(_left, toSkeleton * pose.LeftWrist.Origin, turn * pose.LeftWrist.Basis.Orthonormalized(), pose.LeftGrip, pose.LeftTrigger);
        SolveArm(_right, toSkeleton * pose.RightWrist.Origin, turn * pose.RightWrist.Basis.Orthonormalized(), pose.RightGrip, pose.RightTrigger);
        RememberWhatWasWritten();
    }

    /// <summary>
    /// The hips and legs as the animation last posed them, and as this last left them. The upper
    /// body is put back to standing before each solve, but these aren't, and a clip without keys
    /// for one leaves it where this put it, which would add up frame after frame.
    /// </summary>
    private Vector3 _hipsAnimated;
    private Vector3? _hipsWritten;
    private (Quaternion Animated, Quaternion? Written)[]? _legTurns;

    private int LegBone(int i) => i % 3 == 0 ? _legs[i / 3].Thigh : i % 3 == 1 ? _legs[i / 3].Knee : _legs[i / 3].Foot;

    private void UndoWhatTheAnimationKept()
    {
        var hips = _skeleton.GetBonePosePosition(_hips);
        _hipsAnimated = _hipsWritten is { } written && hips.IsEqualApprox(written) ? _hipsAnimated : hips;
        _skeleton.SetBonePosePosition(_hips, _hipsAnimated);

        _legTurns ??= new (Quaternion, Quaternion?)[_legs.Length * 3];
        for (var i = 0; i < _legTurns.Length; i++)
        {
            var bone = LegBone(i);
            var now = _skeleton.GetBonePoseRotation(bone);
            var animated = _legTurns[i].Written is { } wrote && now.IsEqualApprox(wrote) ? _legTurns[i].Animated : now;
            _skeleton.SetBonePoseRotation(bone, animated);
            _legTurns[i] = (animated, null);
        }
    }

    private void RememberWhatWasWritten()
    {
        _hipsWritten = _skeleton.GetBonePosePosition(_hips);
        for (var i = 0; i < _legTurns!.Length; i++)
        {
            _legTurns[i].Written = _skeleton.GetBonePoseRotation(LegBone(i));
        }
    }

    /// <summary>
    /// Lowers the hips so the spine, at its standing length, can reach the head: straight down for a
    /// crouch, less when the head is ahead, as in a bow. The legs bend to keep the feet put.
    /// </summary>
    private void PlaceHips(Vector3 headTarget)
    {
        var low = Position(_spine[0]);
        var ahead = new Vector2(headTarget.X - low.X, headTarget.Z - low.Z);
        var length = SpineLength;

        // Far ahead or aside, the hips follow along the ground too.
        var shift = Vector3.Zero;
        if (ahead.Length() > 0.9f * length)
        {
            var move = ahead.Normalized() * (ahead.Length() - (0.9f * length));
            shift = new Vector3(move.X, 0, move.Y);
            ahead -= move;
        }

        var upright = Mathf.Sqrt(Mathf.Max((length * length) - ahead.LengthSquared(), LeastSpineUpright * LeastSpineUpright * length * length));
        var hips = Position(_hips);
        // Models that stand hunched, as undead and trolls, may straighten up to their rest height.
        var rise = Mathf.Max(0f, RestOf(_hips).Y - hips.Y) + (MaxHipRise * length);
        var drop = Mathf.Clamp(low.Y + upright - headTarget.Y, -rise, MaxHipDrop * hips.Y);
        if (Mathf.Abs(drop) < 1e-4f && shift == Vector3.Zero)
        {
            return;
        }

        Span<(Vector3 At, Basis Turn)> feet = stackalloc (Vector3, Basis)[_legs.Length];
        for (var i = 0; i < _legs.Length; i++)
        {
            var foot = _skeleton.GetBoneGlobalPose(_legs[i].Foot);
            feet[i] = (foot.Origin, foot.Basis.Orthonormalized());
        }

        SetGlobalPosition(_hips, hips + shift + (Vector3.Down * drop));
        for (var i = 0; i < _legs.Length; i++)
        {
            var leg = _legs[i];
            var knee = Position(leg.Knee);
            var thigh = Position(leg.Thigh);
            var foot = Position(leg.Foot);

            // The knee keeps bending the way the animation bends it, or forward if it's straight.
            var line = (foot - thigh).Normalized();
            var bend = knee - thigh - (line * (knee - thigh).Dot(line));
            var pole = bend.LengthSquared() > 1e-6f ? bend : Vector3.Right;
            TwoBone(leg.Thigh, leg.Knee, leg.Foot, feet[i].At, pole);
            SetGlobalRotation(leg.Foot, feet[i].Turn);
        }
    }

    /// <summary>
    /// Bends the spine through the head's target with FABRIK, starting from standing bent a little
    /// back so it rounds as a back does, then shares out the head's turn from the body along it.
    /// </summary>
    private void BendSpine(Vector3 headTarget, Basis headTurn)
    {
        var count = _spine.Length;
        Span<Vector3> joints = stackalloc Vector3[count + 1];
        Span<float> lengths = stackalloc float[count];
        for (var i = 0; i < count; i++)
        {
            joints[i] = Position(_spine[i]);
        }

        joints[count] = Position(_head);
        var total = 0f;
        for (var i = 0; i < count; i++)
        {
            lengths[i] = joints[i].DistanceTo(joints[i + 1]);
            total += lengths[i];
        }

        for (var i = 1; i < count; i++)
        {
            joints[i] += Vector3.Left * 0.04f * total;
        }

        var root = joints[0];
        for (var iteration = 0; iteration < 12; iteration++)
        {
            joints[count] = headTarget;
            for (var i = count - 1; i >= 0; i--)
            {
                joints[i] = joints[i + 1] + ((joints[i] - joints[i + 1]).Normalized() * lengths[i]);
            }

            joints[0] = root;
            for (var i = 0; i < count; i++)
            {
                joints[i + 1] = joints[i] + ((joints[i + 1] - joints[i]).Normalized() * lengths[i]);
            }

            if (joints[count].DistanceSquaredTo(headTarget) < 1e-6f)
            {
                break;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var from = Position(_spine[i]);
            var next = i + 1 < count ? Position(_spine[i + 1]) : Position(_head);
            Turn(_spine[i], Rotation(next - from, joints[i + 1] - from));
        }

        // The head's turn from the body, about the up axis, shared up the spine, more toward the top.
        var look = headTurn.X;
        var flat = new Vector2(look.X, look.Z);
        if (flat.LengthSquared() < 0.04f)
        {
            // Looking straight up or down, the top of the head says which way it faces.
            var up = headTurn.Y * (look.Y < 0 ? 1 : -1);
            flat = new Vector2(up.X, up.Z);
        }

        var yaw = Mathf.Clamp(Mathf.Atan2(-flat.Y, flat.X), -MaxSpineTwist, MaxSpineTwist);
        var weights = count * (count + 1) / 2f;
        for (var i = 0; i < count; i++)
        {
            var from = Position(_spine[i]);
            var next = i + 1 < count ? Position(_spine[i + 1]) : Position(_head);
            var axis = next - from;
            if (axis.LengthSquared() > 1e-8f)
            {
                Turn(_spine[i], new Quaternion(axis.Normalized(), yaw * SpineTwistShare * (i + 1) / weights));
            }
        }
    }

    private void SolveArm(Arm arm, Vector3 wrist, Basis wristTurn, float grip, float trigger)
    {
        // The hand as the real one lies: fingers along the wrist's Y, the thumb's side along its Z.
        var handTurn = Frame(wristTurn.Y, wristTurn.Z) * arm.HandRest.Inverse();
        var chest = _skeleton.GetBoneGlobalPose(_spine[^1]).Basis.Orthonormalized();

        var upperLength = Position(arm.Upper).DistanceTo(Position(arm.Lower));
        var lowerLength = Position(arm.Lower).DistanceTo(Position(arm.Hand));
        if (arm.Clavicle >= 0)
        {
            RaiseShoulder(arm, wrist, chest, upperLength + lowerLength);
        }

        var shoulder = Position(arm.Upper);
        var elbowHint = wrist + (handTurn * arm.ElbowFromHand);
        var side = arm.Left ? -1f : 1f;
        var down = chest * new Vector3(-0.3f, -1f, 0.5f * side);

        // A hand below the shoulder hangs from it: whatever the wrist says, the elbow doesn't go up.
        // Raised above, the elbow may rise, as it does with the hands overhead.
        // Nor does it point in toward the middle of the body, where it would go into the chest.
        var raised = Mathf.Clamp(1.5f * (wrist - shoulder).Dot(chest.Y) / (upperLength + lowerLength), 0f, 1f);
        var elbow = PlaceElbow(shoulder, wrist, upperLength, lowerLength, elbowHint - shoulder, down, out var reached, (chest.Y, raised), (chest.Z * -side, ElbowInward));

        // A hinge: the upper arm and forearm both keep the elbow's axis across the plane they bend in.
        var hinge = (elbow - shoulder).Cross(reached - elbow);
        hinge = hinge.LengthSquared() > 1e-10f ? hinge.Normalized() : (down.Cross(wrist - shoulder)).Normalized();
        // Where each bone's child is, in its own frame, as standing has it: a bone between the
        // forearm and the hand may be turned, which moves the hand off the rest direction.
        var upperAlong = _skeleton.GetBoneGlobalPose(arm.Upper).Basis.Orthonormalized().Inverse() * (Position(arm.Lower) - shoulder);
        var lowerAlong = _skeleton.GetBoneGlobalPose(arm.Lower).Basis.Orthonormalized().Inverse() * (Position(arm.Hand) - Position(arm.Lower));
        var upperTurn = Frame(elbow - shoulder, hinge) * Frame(upperAlong, arm.HingeRest).Inverse();
        var lowerTurn = Frame(reached - elbow, hinge) * Frame(lowerAlong, arm.HingeRest).Inverse();

        // The forearm takes some of the hand's twist about it, so the wrist doesn't wring.
        var axis = lowerAlong.Normalized();
        var relative = (lowerTurn.Inverse() * handTurn).GetRotationQuaternion();
        var along = new Vector3(relative.X, relative.Y, relative.Z).Dot(axis);
        var twist = new Quaternion(axis.X * along, axis.Y * along, axis.Z * along, relative.W);
        if (twist.LengthSquared() > 1e-8f)
        {
            twist = twist.Normalized();
            var angle = 2f * Mathf.Atan2(new Vector3(twist.X, twist.Y, twist.Z).Dot(axis), twist.W);
            lowerTurn *= new Basis(axis, Mathf.Wrap(angle, -Mathf.Pi, Mathf.Pi) * ForearmTwistShare);
        }

        SetGlobalRotation(arm.Upper, upperTurn);
        SetGlobalRotation(arm.Lower, lowerTurn);
        SetGlobalRotation(arm.Hand, handTurn);
        CloseFingers(arm, grip, trigger);
    }

    /// <summary>The collarbone lifts the shoulder for a hand above it, and swings it forward for one reaching ahead.</summary>
    private void RaiseShoulder(Arm arm, Vector3 wrist, Basis chest, float armLength)
    {
        var reach = chest.Inverse() * (wrist - Position(arm.Upper));
        var stretch = Mathf.Clamp(((reach.Length() / armLength) - 0.85f) / 0.15f, 0f, 1f);
        var up = Mathf.Clamp((reach.Y / armLength) + 0.1f, 0f, 1f);
        var ahead = Mathf.Clamp(((reach.X / armLength) - 0.4f) / 0.6f, 0f, 1f);
        var lift = MaxShoulderLift * Mathf.Max(up, reach.Y > 0 ? 0.3f * stretch : 0f);
        var forward = MaxShoulderReach * Mathf.Max(ahead, stretch * Mathf.Clamp(reach.X / Mathf.Max(reach.Length(), 1e-4f), 0f, 1f));
        if (lift <= 0f && forward <= 0f)
        {
            return;
        }

        // About the body's forward axis lifts the left shoulder with a positive turn; about up, swings it back.
        var side = arm.Left ? -1f : 1f;
        Turn(arm.Clavicle, new Quaternion(chest.X, -side * lift) * new Quaternion(chest.Y, side * forward));
    }

    /// <summary>
    /// Where the elbow goes for the hand to reach the wrist: on the circle the arm's lengths allow,
    /// toward the hint, blended with <paramref name="down"/>. <paramref name="reached"/> is where the
    /// hand ends up, short of the wrist when it's out of reach.
    /// </summary>
    private static Vector3 PlaceElbow(Vector3 shoulder, Vector3 wrist, float upperLength, float lowerLength, Vector3 hint, Vector3 down, out Vector3 reached, params (Vector3 Axis, float Allowed)[] limits)
    {
        var reach = wrist - shoulder;
        var toGoal = reach.LengthSquared() > 1e-8f ? reach.Normalized() : Vector3.Down;
        var distance = Mathf.Clamp(reach.Length(), Mathf.Abs(upperLength - lowerLength) + 1e-3f, (upperLength + lowerLength) * 0.9999f);
        reached = shoulder + (toGoal * distance);

        Vector3 Across(Vector3 v) => v - (toGoal * v.Dot(toGoal));
        var fallback = Across(down);
        fallback = fallback.LengthSquared() > 1e-8f ? fallback.Normalized() : Across(Vector3.Back).Normalized();
        var fromHint = Across(hint);
        var pole = fromHint.LengthSquared() > 1e-6f
            ? ((fromHint.Normalized() * ElbowFromWrist) + (fallback * (1f - ElbowFromWrist))).Normalized()
            : fallback;

        // At most this much of the way along each limit's axis.
        foreach (var (axis, allowed) in limits)
        {
            if (pole.Dot(axis) > allowed)
            {
                var held = Across(pole - (axis * (pole.Dot(axis) - allowed)));
                pole = held.LengthSquared() > 1e-6f ? held.Normalized() : fallback;
            }
        }

        var cos =Mathf.Clamp(((upperLength * upperLength) + (distance * distance) - (lowerLength * lowerLength)) / (2 * upperLength * distance), -1f, 1f);
        return shoulder + (toGoal * upperLength * cos) + (pole * upperLength * Mathf.Sqrt(1 - (cos * cos)));
    }

    /// <summary>A two-bone chain bent toward the pole so its end reaches the target, keeping each bone's twist.</summary>
    private void TwoBone(int upper, int lower, int end, Vector3 target, Vector3 pole)
    {
        var a = Position(upper);
        var upperLength = a.DistanceTo(Position(lower));
        var lowerLength = Position(lower).DistanceTo(Position(end));
        if (upperLength < 1e-4f || lowerLength < 1e-4f)
        {
            return;
        }

        var elbow = PlaceElbow(a, target, upperLength, lowerLength, pole, pole, out var reached);
        Turn(upper, Rotation(Position(lower) - a, elbow - a));
        Turn(lower, Rotation(Position(end) - elbow, reached - elbow));
    }

    private void CloseFingers(Arm arm, float grip, float trigger)
    {
        foreach (var (bone, finger, fist) in arm.Fingers)
        {
            var amount = finger == IndexFinger ? trigger : grip;
            if (amount > 0.01f)
            {
                _skeleton.SetBonePoseRotation(bone, _skeleton.GetBonePoseRotation(bone).Slerp(fist, Mathf.Clamp(amount, 0f, 1f)));
            }
        }
    }

    private Vector3 Position(int bone) => _skeleton.GetBoneGlobalPose(bone).Origin;

    private Vector3 RestOf(int bone) => RestOf(_skeleton, bone);

    private static Vector3 RestOf(Skeleton3D skeleton, int bone) => skeleton.GetBoneGlobalRest(bone).Origin;

    /// <summary>A turn whose X is along <paramref name="along"/> and whose Y is <paramref name="side"/>, made square to it.</summary>
    private static Basis Frame(Vector3 along, Vector3 side)
    {
        var x = along.Normalized();
        var y = (side - (x * side.Dot(x))).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    private static Quaternion Rotation(Vector3 from, Vector3 to)
    {
        if (from.LengthSquared() < 1e-10f || to.LengthSquared() < 1e-10f)
        {
            return Quaternion.Identity;
        }

        var f = from.Normalized();
        var t = to.Normalized();
        return f.Dot(t) > 0.999999f ? Quaternion.Identity : new Quaternion(f, t);
    }

    /// <summary>Turns a bone by <paramref name="turn"/> in the skeleton's space, about its own pivot.</summary>
    private void Turn(int bone, Quaternion turn) =>
        SetGlobalRotation(bone, new Basis(turn) * _skeleton.GetBoneGlobalPose(bone).Basis.Orthonormalized());

    /// <summary>Sets a bone's pose so its turn in the skeleton's space is <paramref name="global"/>, keeping its scale.</summary>
    private void SetGlobalRotation(int bone, Basis global)
    {
        var parent = _skeleton.GetBoneParent(bone);
        var parentBasis = parent >= 0 ? _skeleton.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
        _skeleton.SetBonePoseRotation(bone, (parentBasis.Inverse() * global).Orthonormalized().GetRotationQuaternion());
    }

    private void SetGlobalPosition(int bone, Vector3 global)
    {
        var parent = _skeleton.GetBoneParent(bone);
        var local = parent >= 0 ? _skeleton.GetBoneGlobalPose(parent).AffineInverse() * global : global;
        _skeleton.SetBonePosePosition(bone, local);
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
}
