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
/// A WoW body posed from the headset and the two hands, the way VRChat's avatars follow their
/// players. The solving is the <c>HumanoidIK3D</c> extension's (<c>extensions/humanoid_ik</c>),
/// which knows nothing of WoW; this finds a WoW skeleton's bones by their key bones and names them
/// to it, puts its targets where the pose says each frame, and closes the fingers into the model's
/// own fist with the grip and trigger.
/// <para>
/// The extension runs as a skeleton modifier, after the animation, and Godot puts the animated
/// pose back once the mesh is skinned. So the skeleton's bones read as animated outside the
/// modifier; <see cref="Pose"/> gives the solved pose, for checks.
/// </para>
/// </summary>
public sealed class VrBodyIk
{
    /// <summary>The extension's class, which isn't there unless it's been built.</summary>
    public const string ModifierClass = "HumanoidIK3D";
    private const string ExtensionPath = "res://addons/humanoid_ik/humanoid_ik.gdextension";

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

    /// <summary>The attachment a helmet sits on, at the top of the head.</summary>
    private const int HelmetAttachment = 11;

    /// <summary>The extension's <c>forward_axis</c> for a WoW model, which faces +X.</summary>
    private const int ForwardPlusX = 0;

    private static readonly StringName ProcessedSignal = "modification_processed";
    private static readonly StringName GetHeadPivotFor = "get_head_pivot_for";
    private static readonly StringName GetUprightEyeHeight = "get_upright_eye_height";

    private readonly Skeleton3D _skeleton;
    private readonly SkeletonModifier3D _modifier;
    private readonly Node3D _head;
    private readonly Node3D _leftHand;
    private readonly Node3D _rightHand;
    private readonly int _hips;
    private readonly int[] _spine;
    private readonly int _headBone;
    private readonly Arm _left;
    private readonly Arm _right;
    private readonly Leg[] _legs;
    private readonly Vector3 _eyeRest;
    private readonly Vector3 _headRest;
    private Transform3D[]? _solved;
    private Callable? _keep;

    /// <summary>One arm's bones, the fingers' fist turns, and which way the fingers point at rest.</summary>
    private sealed record Arm(int Clavicle, int Upper, int Lower, int Hand, int FingersBone, int ThumbBone, Vector3 FingersRest)
    {
        /// <summary>The fingers' fist turns, by which finger: 0 the index to 4 the thumb.</summary>
        public required (int Bone, int Finger, Quaternion Fist)[] Fingers { get; init; }

        public override string ToString() => $"clavicle {Clavicle} upper {Upper} forearm {Lower} hand {Hand}, {Fingers.Length} fingers";
    }

    private sealed record Leg(int Thigh, int Knee, int Foot)
    {
        public override string ToString() => $"{Thigh}-{Knee}-{Foot}";
    }

    /// <summary>From the lowest spine bone to the head's pivot, standing, in the model's units.</summary>
    public float SpineLength { get; }

    /// <summary>How high the eyes are at rest, in the model's units.</summary>
    public float EyeHeight => _eyeRest.Y;

    /// <summary>
    /// How high the eyes would be with the spine straight up, in the model's units. The solver
    /// straightens a hunched model's back to reach the player's head, so this is what fits it to them.
    /// </summary>
    public float UprightEyeHeight => _modifier.Call(GetUprightEyeHeight).AsSingle();

    /// <summary>How high the head's pivot is at rest, in the model's units.</summary>
    public float HeadHeight => _headRest.Y;

    /// <summary>Bones that checks measure: the head, each arm's shoulder joint, elbow and wrist, the feet, then the hips.</summary>
    public (int Head, int[] Left, int[] Right, int[] Feet, int Hips) Bones =>
        (_headBone, [_left.Upper, _left.Lower, _left.Hand], [_right.Upper, _right.Lower, _right.Hand], [.. _legs.Select(l => l.Foot)], _hips);

    /// <summary>The fingers each hand closes, for checks.</summary>
    public int[] FingerBones(bool left) => [.. (left ? _left : _right).Fingers.Select(f => f.Bone)];

    /// <summary>The direction the hand's fingers point at rest, in the skeleton's space.</summary>
    public Vector3 FingersRest(bool left) => (left ? _left : _right).FingersRest;

    /// <summary>The bones of each arm, as found, for checks to print.</summary>
    public string Description =>
        $"spine {string.Join('-', _spine)} head {_headBone} hips {_hips}; left arm {_left}; right arm {_right}; legs {string.Join(", ", _legs.Select(l => l.ToString()))}";

    /// <summary>For checks: keeps each solved pose, which <see cref="Pose"/> then gives.</summary>
    public bool KeepSolvedPose
    {
        set
        {
            var keep = _keep ??= Callable.From(KeepPose);
            var connected = _modifier.IsConnected(ProcessedSignal, keep);
            if (value && !connected)
            {
                _modifier.Connect(ProcessedSignal, keep);
            }
            else if (!value && connected)
            {
                _modifier.Disconnect(ProcessedSignal, keep);
                _solved = null;
            }
        }
    }

    private VrBodyIk(Skeleton3D skeleton, SkeletonModifier3D modifier, int hips, int[] spine, int head, Arm left, Arm right, Leg[] legs, Vector3 eyeRest)
    {
        (_skeleton, _modifier, _hips, _spine, _headBone, _left, _right, _legs, _eyeRest) = (skeleton, modifier, hips, spine, head, left, right, legs, eyeRest);
        _headRest = RestOf(skeleton, head);
        SpineLength = RestOf(skeleton, spine[0]).DistanceTo(_headRest);

        string Name(int bone) => bone >= 0 ? skeleton.GetBoneName(bone) : "";
        modifier.Set("forward_axis", ForwardPlusX);
        modifier.Set("eye_position", eyeRest);
        modifier.Set("hips_bone", Name(hips));
        modifier.Set("head_bone", Name(head));
        modifier.Set("spine_bones", new string[spine.Length].Select((_, i) => Name(spine[i])).ToArray());
        foreach (var (arm, side) in new[] { (left, "left"), (right, "right") })
        {
            modifier.Set($"{side}_shoulder_bone", Name(arm.Clavicle));
            modifier.Set($"{side}_upper_arm_bone", Name(arm.Upper));
            modifier.Set($"{side}_lower_arm_bone", Name(arm.Lower));
            modifier.Set($"{side}_hand_bone", Name(arm.Hand));
            modifier.Set($"{side}_fingers_bone", Name(arm.FingersBone));
            modifier.Set($"{side}_thumb_bone", Name(arm.ThumbBone));
        }

        foreach (var (leg, side) in legs.Zip(new[] { "left", "right" }))
        {
            modifier.Set($"{side}_upper_leg_bone", Name(leg.Thigh));
            modifier.Set($"{side}_lower_leg_bone", Name(leg.Knee));
            modifier.Set($"{side}_foot_bone", Name(leg.Foot));
        }

        _head = Target(modifier, "Eyes", "head_target");
        _leftHand = Target(modifier, "LeftWrist", "left_hand_target");
        _rightHand = Target(modifier, "RightWrist", "right_hand_target");
    }

    private static Node3D Target(SkeletonModifier3D modifier, string name, string property)
    {
        var node = new Node3D { Name = name };
        modifier.AddChild(node);
        modifier.Set(property, modifier.GetPathTo(node));
        return node;
    }

    /// <summary>
    /// For a built creature: finds its spine, arms and legs by their key bones and puts the
    /// extension on its skeleton, with the eyes placed by where its helmet sits. Null for a model
    /// without them, or when the extension isn't built.
    /// </summary>
    public static VrBodyIk? Create(Skeleton3D skeleton, CreatureAsset asset)
    {
        // Godot only loads extensions its editor has listed, which a check run straight from the
        // command line hasn't, so it's loaded here if need be.
        if (!ClassDB.ClassExists(ModifierClass) && Godot.FileAccess.FileExists(ExtensionPath))
        {
            GDExtensionManager.LoadExtension(ExtensionPath);
        }

        if (!ClassDB.ClassExists(ModifierClass))
        {
            GD.PushWarning($"{ModifierClass} isn't loaded: build it with scripts/build-humanoid-ik.sh. The VR body only stands.");
            return null;
        }

        var keys = asset.Skeleton.KeyBones;
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

        var left = FindArm(skeleton, keys, Key(KeyShoulderLeft), Key(KeyArmLeft), KeyFirstLeftFinger, asset.Skeleton.LeftFist);
        var right = FindArm(skeleton, keys, Key(KeyShoulderRight), Key(KeyArmRight), KeyFirstRightFinger, asset.Skeleton.RightFist);
        if (left is null || right is null)
        {
            return null;
        }

        // The eyes are a quarter of the way up from the head's pivot to the top, and in front of
        // where a helmet sits. For a human, that's between the bones that move the eyelids.
        Vector3? headTop = asset.Attachments.TryGetValue(HelmetAttachment, out var helmet) && skeleton.FindBone(helmet.Bone) is >= 0 and var helmetBone
            ? skeleton.GetBoneGlobalRest(helmetBone).Origin + helmet.Offset
            : null;
        var headRest = RestOf(skeleton, head);
        var top = headTop is { } t && t.Y - headRest.Y > 0.05f ? t : headRest + new Vector3(0.06f, 0.18f, 0);
        var rise = top.Y - headRest.Y;
        var eyes = new Vector3(top.X + (0.27f * rise), headRest.Y + (0.25f * rise), headRest.Z);

        // A modifier only runs when its skeleton processes them, which creatures do by hand.
        var modifier = ClassDB.Instantiate(ModifierClass).As<SkeletonModifier3D>();
        modifier.Name = "HumanoidIK";
        skeleton.AddChild(modifier);
        skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Idle;
        return new VrBodyIk(skeleton, modifier, hips, [.. spine], head, left, right, waist >= 0 ? FindLegs(skeleton, waist) : [], eyes);
    }

    private static Arm? FindArm(Skeleton3D skeleton, int[] keys, int clavicle, int upper, int firstFinger, FingerPose[] fist)
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

        // The extension takes the fingers' direction from the middle finger's bone.
        var along = RestOf(skeleton, middle) - RestOf(skeleton, hand);

        var fistTurns = new List<(int, int, Quaternion)>();
        foreach (var pose in fist)
        {
            var finger = Array.IndexOf(fingers, pose.Bone);
            if (finger >= 0)
            {
                fistTurns.Add((pose.Bone, finger, pose.Rotation.Normalized()));
            }
        }

        return new Arm(clavicle, upper, lower, hand, middle, fingers[Thumb], along.Normalized()) { Fingers = [.. fistTurns] };
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

        return [.. legs];
    }

    /// <summary>Where the head's pivot goes for eyes there, in the world, with the model as big as <paramref name="worldScale"/> makes it.</summary>
    public Vector3 HeadPivotFor(Transform3D eyes, float worldScale) =>
        _modifier.Call(GetHeadPivotFor, eyes, worldScale).AsVector3();

    /// <summary>
    /// Poses the body this frame: the extension's targets go where the pose says, and the fingers
    /// close as far as grip and trigger are pressed. The extension solves at the frame's end.
    /// </summary>
    public void Solve(VrPose pose)
    {
        _modifier.Active = true;
        _head.GlobalTransform = pose.Eyes;
        _leftHand.GlobalTransform = Wrist(pose.LeftWrist, left: true);
        _rightHand.GlobalTransform = Wrist(pose.RightWrist, left: false);
        CloseFingers(_left, pose.LeftGrip, pose.LeftTrigger);
        CloseFingers(_right, pose.RightGrip, pose.RightTrigger);
    }

    /// <summary>Gives the body back to its animation, as a remote player leaving VR.</summary>
    public void Release()
    {
        _modifier.Active = false;
        foreach (var (mesh, _, _) in _held)
        {
            if (GodotObject.IsInstanceValid(mesh))
            {
                mesh.Basis = Basis.Identity;
            }
        }
    }

    private IReadOnlyList<(MeshInstance3D Mesh, int Bone, bool Left)> _held = [];
    private Callable? _turnHeld;

    /// <summary>
    /// Turns what the hands hold, while the body is posed, by the VR alignment page's weapon grip
    /// (<c>AppSettings.GripPitch</c>, <c>GripRoll</c> and <c>GripYaw</c>). WoW's models hold a
    /// sword square to the forearm and, with the arm out in front, leaning 31° out; the body check
    /// (<c>--body-check=1,0,0,862</c>) says where the blade points with them.
    /// </summary>
    public void TurnHeldItems(IReadOnlyList<(MeshInstance3D Mesh, int Bone, bool Left)> items)
    {
        _held = items;
        var turn = _turnHeld ??= Callable.From(TurnHeld);
        if (items.Count > 0 && !_modifier.IsConnected(ProcessedSignal, turn))
        {
            _modifier.Connect(ProcessedSignal, turn);
        }
    }

    /// <summary>
    /// Once the solver has posed the hands: the turn is about the wrist target's own axes (the
    /// back of the hand along +Y, the fingers along -Z), carried into the item's bone's space.
    /// </summary>
    private void TurnHeld()
    {
        var skeleton = _skeleton.GlobalTransform.Basis.Orthonormalized();
        var (pitch, roll, yaw) = (AppSettings.GripPitch, AppSettings.GripRoll, AppSettings.GripYaw);
        foreach (var (mesh, bone, left) in _held)
        {
            if (bone < 0 || !GodotObject.IsInstanceValid(mesh))
            {
                continue;
            }

            var hand = skeleton * _skeleton.GetBoneGlobalPose(bone).Basis.Orthonormalized();
            mesh.Basis = GripTurn(hand, WristTarget(left), left, pitch, roll, yaw);
        }
    }

    /// <summary>Where the solver was told a wrist is, in the world, with the back of the hand along +Y and the fingers along -Z.</summary>
    public Basis WristTarget(bool left) => (left ? _leftHand : _rightHand).GlobalBasis.Orthonormalized();

    /// <summary>
    /// An item's turn in its bone's space (the hand at <paramref name="hand"/> in the world), about
    /// the wrist target's axes: tipped forward, turned and rolled toward the body's middle.
    /// </summary>
    public static Basis GripTurn(Basis hand, Basis wrist, bool left, float pitchDegrees, float rollDegrees, float yawDegrees = 0f)
    {
        // The back of the hand (+Y) faces out on both sides, so tipping forward is about -Y on the
        // right and +Y on the left, and rolling about the fingers mirrors the same way. +X runs
        // down the right hand and up the left, so turning about -X is toward the middle on both.
        var side = left ? -1f : 1f;
        var inWrist = new Basis(Vector3.Down * side, Mathf.DegToRad(pitchDegrees))
            * new Basis(Vector3.Left, Mathf.DegToRad(yawDegrees))
            * new Basis(Vector3.Back * side, Mathf.DegToRad(rollDegrees));
        return hand.Inverse() * wrist * inWrist * wrist.Inverse() * hand;
    }

    /// <summary>
    /// A bone's pose in the skeleton's space as last solved, with <see cref="KeepSolvedPose"/> on;
    /// otherwise as the animation has it.
    /// </summary>
    public Transform3D Pose(int bone) => _solved is { } solved && bone < solved.Length ? solved[bone] : _skeleton.GetBoneGlobalPose(bone);

    private void KeepPose()
    {
        _solved ??= new Transform3D[_skeleton.GetBoneCount()];
        for (var bone = 0; bone < _solved.Length; bone++)
        {
            _solved[bone] = _skeleton.GetBoneGlobalPose(bone);
        }
    }

    /// <summary>
    /// The extension's wrist, from XR Tools' (fingers along +Y, the thumb's side along +Z): OpenXR's
    /// hand joints', fingers along -Z and the back of the hand along +Y.
    /// </summary>
    private static Transform3D Wrist(Transform3D xr, bool left)
    {
        var basis = xr.Basis.Orthonormalized();
        var fingers = basis.Y;
        var thumb = basis.Z;
        var back = left ? thumb.Cross(fingers) : fingers.Cross(thumb);
        var z = -fingers;
        var y = (back - (z * back.Dot(z))).Normalized();
        return new Transform3D(new Basis(y.Cross(z), y, z), xr.Origin);
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

    private static Vector3 RestOf(Skeleton3D skeleton, int bone) => skeleton.GetBoneGlobalRest(bone).Origin;

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
