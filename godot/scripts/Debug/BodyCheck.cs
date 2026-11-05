// The simulated trackers are XRControllerTrackers, which is what this warning asks for.
#pragma warning disable CS0618

using System.Linq;
using System.Threading.Tasks;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--body-check --xr-player</c> stands the player's own body offline and moves a simulated
/// headset and hands through poses: standing, reaching, hands up and out, palms up, crouching,
/// bowing, looking aside, a hand on the chest and fists. For each it prints PASS or FAIL for the
/// hands reaching the wrists and lying as they do, the head at the eyes, the elbows bending down
/// and out rather than into the body, and the feet staying on the ground, then quits.
/// <c>--shots=folder</c> saves each pose from the front and the side (don't commit them).
/// <c>--body-check=race,sex</c> picks the body, as 2,1 for an orc woman; a human man without. A
/// third number wears that shoulders display, as 1060, which only the pictures show.
/// </summary>
public partial class BodyCheck : Node
{
    public required PlayerAvatar Avatar { get; init; }
    public required Xr.XrPlayer Player { get; init; }
    public required TileManager Tiles { get; init; }

    /// <summary>The simulated player's eye height, in real meters.</summary>
    private const float EyeHeight = 1.65f;

    private int _failures;
    private float? _standingHips;
    private string? _shots;
    private Camera3D _camera = null!;
    private XRPositionalTracker _head = null!;
    private XRControllerTracker _left = null!;
    private XRControllerTracker _right = null!;

    /// <summary>A pose: the head and each hand in real meters from the play space's origin, turned by degrees of pitch, yaw and roll.</summary>
    private sealed record Pose(string Name, Vector3 Head, Vector3 HeadTurn, Vector3 Left, Vector3 LeftTurn, Vector3 Right, Vector3 RightTurn)
    {
        public bool InFront { get; init; } = true;
        public bool ElbowsDown { get; init; } = true;
        public float Grip { get; init; }
    }

    private static readonly Vector3 Level = Vector3.Zero;

    private static readonly Pose[] Poses =
    [
        new("standing", new(0, EyeHeight, 0), Level, new(-0.22f, 1.0f, -0.25f), new(-40, 0, 0), new(0.22f, 1.0f, -0.25f), new(-40, 0, 0)),
        new("reaching forward", new(0, EyeHeight, 0), Level, new(-0.18f, 1.35f, -0.55f), Level, new(0.18f, 1.35f, -0.55f), Level),
        new("hands up", new(0, EyeHeight, 0), Level, new(-0.25f, 1.95f, -0.15f), new(80, 0, 0), new(0.25f, 1.95f, -0.15f), new(80, 0, 0)) { InFront = false, ElbowsDown = false },
        new("arms out", new(0, EyeHeight, 0), Level, new(-0.72f, 1.4f, -0.05f), new(0, 90, 0), new(0.72f, 1.4f, -0.05f), new(0, -90, 0)) { InFront = false, ElbowsDown = false },
        new("palms up", new(0, EyeHeight, 0), Level, new(-0.2f, 1.2f, -0.4f), new(0, 0, 90), new(0.2f, 1.2f, -0.4f), new(0, 0, -90)),
        new("crouching", new(0, 1.0f, -0.1f), new(-20, 0, 0), new(-0.25f, 0.75f, -0.4f), new(-10, 0, 0), new(0.25f, 0.75f, -0.4f), new(-10, 0, 0)),
        new("bowing", new(0, 1.35f, -0.6f), new(-60, 0, 0), new(-0.2f, 0.8f, -0.65f), new(-70, 0, 0), new(0.2f, 0.8f, -0.65f), new(-70, 0, 0)),
        new("looking left", new(0, EyeHeight, 0), new(0, 70, 0), new(-0.22f, 1.0f, -0.25f), new(-40, 0, 0), new(0.22f, 1.0f, -0.25f), new(-40, 0, 0)),
        new("hand on chest", new(0, EyeHeight, 0), Level, new(-0.22f, 1.0f, -0.25f), new(-40, 0, 0), new(-0.02f, 1.3f, -0.2f), new(0, 90, 0)),
        new("fists", new(0, EyeHeight, 0), Level, new(-0.22f, 1.0f, -0.25f), new(-40, 0, 0), new(0.22f, 1.0f, -0.25f), new(-40, 0, 0)) { Grip = 1 },
    ];

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        _shots = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots="))?["--shots=".Length..];
        _head = new XRPositionalTracker { Name = "head", Type = XRServer.TrackerType.Head };
        XRServer.AddTracker(_head);
        _left = new XRControllerTracker { Name = "left_hand", Hand = XRPositionalTracker.TrackerHand.Left };
        _right = new XRControllerTracker { Name = "right_hand", Hand = XRPositionalTracker.TrackerHand.Right };
        XRServer.AddTracker(_left);
        XRServer.AddTracker(_right);
        Apply(Poses[0]);

        var started = Time.GetTicksMsec();
        while ((Tiles.PlacingPlayer || !Player.OnGround || Avatar.Ik is null) && Time.GetTicksMsec() - started < 30000)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        Check("The body has a spine, arms and legs", Avatar.Ik is not null, Avatar.Ik?.Description ?? "none found");
        if (Avatar.Ik is not { } ik || Avatar.Body is not { } body)
        {
            Finish();
            return;
        }

        Avatar.ShowHead = _shots is not null;
        _camera = new Camera3D { Fov = 50, Far = 500 };
        AddChild(_camera);

        // Standing first, so the body takes the player's height from it.
        await Seconds(1.5);
        var eyes = Player.Eyes.Origin.Y - Player.Feet.Y;
        var bodyEyes = ik.EyeHeight * body.Skeleton.GlobalTransform.Basis.Scale.Y;
        Check("The body is scaled to the player", Mathf.Abs(eyes - bodyEyes) < 0.05f, $"scale {Avatar.BodyScale:F2}: its eyes {bodyEyes:F2} yards up, the player's {eyes:F2}");

        var ground = body.Skeleton.GlobalTransform * body.Skeleton.GetBoneGlobalPose(ik.Bones.Feet.FirstOrDefault()).Origin;
        Quaternion[]? openFingers = null;
        foreach (var pose in Poses)
        {
            Apply(pose);
            await Seconds(1.0);
            Measure(pose, ik, body.Skeleton, ground.Y);
            var fingers = ik.FingerBones(left: false).Select(b => body.Skeleton.GetBonePoseRotation(b)).ToArray();
            if (pose.Grip > 0 && openFingers is not null)
            {
                var closed = fingers.Zip(openFingers).Count(f => f.First.AngleTo(f.Second) > 0.2f);
                Check("Grip closes the fingers", closed == fingers.Length && closed > 0, $"{closed} of {fingers.Length} turned");
            }

            openFingers ??= fingers;
            await Shoot(pose.Name, body);
        }

        // As the player has it: the head and the shoulder pieces hidden.
        Avatar.ShowHead = false;
        await Seconds(0.2);
        await Shoot("head hidden", body);

        Finish();
    }

    private void Measure(Pose pose, VrBodyIk ik, Skeleton3D skeleton, float ground)
    {
        var toWorld = skeleton.GlobalTransform;
        Vector3 At(int bone) => toWorld * skeleton.GetBoneGlobalPose(bone).Origin;
        var scale = toWorld.Basis.Scale.X;
        var (leftWrist, rightWrist) = Player.Wrists;
        var bones = ik.Bones;

        var headError = At(bones.Head).DistanceTo(ik.HeadPivotFor(Player.Eyes, scale)) / AppSettings.WorldScale;
        Check($"{pose.Name}: the head is at the eyes", headError < 0.03f, $"{headError * 100:F1} cm off");

        foreach (var (arm, wrist, left) in new[] { (bones.Left, leftWrist, true), (bones.Right, rightWrist, false) })
        {
            var side = left ? "left" : "right";
            var shoulder = At(arm[0]);
            var elbow = At(arm[1]);
            var hand = At(arm[2]);
            var miss = hand.DistanceTo(wrist.Origin) / AppSettings.WorldScale;
            var length = (shoulder.DistanceTo(elbow) + elbow.DistanceTo(hand)) / AppSettings.WorldScale;
            var outOfReach = shoulder.DistanceTo(wrist.Origin) / AppSettings.WorldScale > length * 0.995f;
            Check($"{pose.Name}: the {side} hand is at the wrist", miss < 0.02f || outOfReach, $"{miss * 100:F1} cm off{(outOfReach ? ", out of reach" : "")}");

            // The fingers point where the real ones do.
            var fingers = (toWorld.Basis.Orthonormalized() * skeleton.GetBoneGlobalPose(arm[2]).Basis.Orthonormalized() * ik.FingersRest(left)).Normalized();
            var off = Mathf.RadToDeg(fingers.AngleTo(wrist.Basis.Y));
            Check($"{pose.Name}: the {side} hand lies as the real one", off < 5f, $"{off:F1} degrees off");

            // In the body's own frame: forward +X, up +Y, its left -Z.
            var body = skeleton.GlobalTransform.AffineInverse();
            var (s, e, h) = (body * shoulder, body * elbow, body * hand);
            var outward = left ? -1f : 1f;
            // Inside the line from the shoulder to the hand is into the body.
            var inside = Mathf.Min(s.Z * outward, h.Z * outward) - (e.Z * outward);
            var detail = $"elbow {e.Y - s.Y:F2} above the shoulder, {inside:F2} inside it, {h.X - e.X:F2} behind the hand";
            // Out of reach, the arm is straight, and any elbow will do.
            var natural = outOfReach || (inside < 0.05f * (s.DistanceTo(e) + e.DistanceTo(h)) && (!pose.ElbowsDown || e.Y < s.Y) && (!pose.InFront || e.X < h.X + 0.02f));
            Check($"{pose.Name}: the {side} elbow bends naturally", natural, detail);
        }

        // A bow bends at the waist, and a crouch bends the knees.
        var hips = (At(bones.Hips).Y - ground) / AppSettings.WorldScale;
        _standingHips ??= hips;
        if (pose.Name is "bowing" or "crouching")
        {
            var share = hips / _standingHips.Value;
            var bow = pose.Name == "bowing";
            Check($"{pose.Name}: {(bow ? "bends at the waist" : "lowers the hips")}", bow ? share > 0.85f : share < 0.8f, $"hips at {share * 100:F0}% of standing");
        }

        foreach (var foot in bones.Feet)
        {
            var height = (At(foot).Y - ground) / AppSettings.WorldScale;
            Check($"{pose.Name}: foot {foot} stays on the ground", Mathf.Abs(height) < 0.05f, $"{height * 100:F1} cm above where it stood");
        }
    }

    private void Apply(Pose pose)
    {
        SetPose(_head, pose.Head, pose.HeadTurn);
        SetPose(_left, pose.Left, pose.LeftTurn);
        SetPose(_right, pose.Right, pose.RightTurn);
        foreach (var hand in new[] { _left, _right })
        {
            hand.SetInput("grip", pose.Grip);
            hand.SetInput("trigger", pose.Grip);
        }
    }

    private static void SetPose(XRPositionalTracker tracker, Vector3 position, Vector3 degrees) =>
        tracker.SetPose(
            "default",
            new Transform3D(Basis.FromEuler(new Vector3(Mathf.DegToRad(degrees.X), Mathf.DegToRad(degrees.Y), Mathf.DegToRad(degrees.Z))), position),
            Vector3.Zero,
            Vector3.Zero,
            XRPose.TrackingConfidenceEnum.High);

    /// <summary>From in front of the body and from its right side, looking at its chest.</summary>
    private async Task Shoot(string name, CreatureNode body)
    {
        if (_shots is null)
        {
            return;
        }

        var facing = body.Skeleton.GlobalBasis.Orthonormalized();
        var forward = facing * Vector3.Right;
        var right = facing * Vector3.Back;
        var size = body.Height;
        var chest = body.GlobalPosition + (Vector3.Up * size * 0.6f);
        foreach (var (view, from) in new[] { ("front", forward), ("side", right) })
        {
            _camera.GlobalPosition = chest + (from * size * 1.6f) + (Vector3.Up * size * 0.1f);
            _camera.LookAt(chest, Vector3.Up);
            _camera.MakeCurrent();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var path = System.IO.Path.Combine(_shots, $"body-{name.Replace(' ', '-')}-{view}.png");
            GetViewport().GetTexture().GetImage().SavePng(path);
        }

        Player.GetNode<XRCamera3D>("XRCamera3D").MakeCurrent();
    }

    private void Finish()
    {
        XRServer.RemoveTracker(_head);
        XRServer.RemoveTracker(_left);
        XRServer.RemoveTracker(_right);
        GD.Print(_failures == 0 ? "Body check passed." : $"Body check FAILED: {_failures}.");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Check(string name, bool passed, string detail)
    {
        GD.Print($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        _failures += passed ? 0 : 1;
    }

    private async Task Seconds(double seconds) =>
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
