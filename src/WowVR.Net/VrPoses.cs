using System.Numerics;

namespace WowVR.Net;

/// <summary>What a VR player is doing, as <c>docs/vr-multiplayer-protocol.md</c> defines it.</summary>
[Flags]
public enum VrState : byte
{
    None = 0,
    InVr = 1,
    Seated = 2,
    LeftTracked = 4,
    RightTracked = 8,
    Pointing = 16,
}

/// <summary>Grip and trigger of each hand, 0 open to 255 closed.</summary>
public readonly record struct VrTriggers(byte LeftGrip, byte LeftTrigger, byte RightGrip, byte RightTrigger);

/// <summary>A head or a wrist in the character's space: position in yards at its own size, and turn.</summary>
public readonly record struct VrJoint(Vector3 Position, Quaternion Rotation);

/// <summary>
/// One pose of a VR player, in the character's space: its origin at the feet where the server has
/// it, <c>+X</c> forward along its orientation, <c>+Y</c> to its left, <c>+Z</c> up, in yards at
/// the character's own size. Each part may be missing. See <c>docs/vr-multiplayer-protocol.md</c>.
/// </summary>
public sealed record VrPoseMessage
{
    public const int Version = 1;

    /// <summary>A position further than this, in yards, can't be written.</summary>
    public const float MaxOffset = 32f;

    private const int HeaderBytes = 7;
    private const float PositionScale = 1000f;
    private const float YawScale = 10000f;
    private const float LengthScale = 100f;

    [Flags]
    private enum Parts : ushort
    {
        Body = 1 << 0,
        Head = 1 << 1,
        LeftHand = 1 << 2,
        RightHand = 1 << 3,
        Triggers = 1 << 4,
        Fingers = 1 << 5,
        Pointer = 1 << 6,
        Hips = 1 << 7,
        Chest = 1 << 8,
        LeftFoot = 1 << 9,
        RightFoot = 1 << 10,
        LeftKnee = 1 << 11,
        RightKnee = 1 << 12,
        LeftElbow = 1 << 13,
        RightElbow = 1 << 14,
        Known = (1 << 15) - 1,
    }

    public ushort Sequence { get; init; }

    /// <summary>The sender's clock in milliseconds, wrapping.</summary>
    public ushort Time { get; init; }

    public VrState State { get; init; }

    /// <summary>Where the body stands, when the player leans and it stays behind, and its facing from the orientation in radians.</summary>
    public (Vector2 Offset, float Yaw)? Body { get; init; }

    public VrJoint? Head { get; init; }
    public VrJoint? LeftHand { get; init; }
    public VrJoint? RightHand { get; init; }
    public VrTriggers? Triggers { get; init; }

    /// <summary>Each finger's curl, left hand then right, thumb to little finger, 0 straight to 255 curled: 10 of them.</summary>
    public byte[]? Fingers { get; init; }

    /// <summary>A ray from a hand (0 left, 1 right) along its fingers, in yards, 0 for unending.</summary>
    public (byte Hand, float Length)? Pointer { get; init; }

    /// <summary>Full body trackers' joints: the middle of the hip joints, the top of the chest, and the ankles.</summary>
    public VrJoint? Hips { get; init; }
    public VrJoint? Chest { get; init; }
    public VrJoint? LeftFoot { get; init; }
    public VrJoint? RightFoot { get; init; }

    /// <summary>Where the knees and elbows are, which says which way the legs and arms bend.</summary>
    public Vector3? LeftKnee { get; init; }
    public Vector3? RightKnee { get; init; }
    public Vector3? LeftElbow { get; init; }
    public Vector3? RightElbow { get; init; }

    public byte[] Write()
    {
        var parts = (Body is null ? 0 : Parts.Body) | (Head is null ? 0 : Parts.Head) | (LeftHand is null ? 0 : Parts.LeftHand)
            | (RightHand is null ? 0 : Parts.RightHand) | (Triggers is null ? 0 : Parts.Triggers) | (Fingers is null ? 0 : Parts.Fingers)
            | (Pointer is null ? 0 : Parts.Pointer) | (Hips is null ? 0 : Parts.Hips) | (Chest is null ? 0 : Parts.Chest)
            | (LeftFoot is null ? 0 : Parts.LeftFoot) | (RightFoot is null ? 0 : Parts.RightFoot) | (LeftKnee is null ? 0 : Parts.LeftKnee)
            | (RightKnee is null ? 0 : Parts.RightKnee) | (LeftElbow is null ? 0 : Parts.LeftElbow) | (RightElbow is null ? 0 : Parts.RightElbow);
        var w = new PacketWriter().U16(Sequence).U16(Time).U8((byte)State).U16((ushort)parts);
        if (Body is { } body)
        {
            w.U16((ushort)Fixed(body.Offset.X, PositionScale)).U16((ushort)Fixed(body.Offset.Y, PositionScale)).U16((ushort)Fixed(body.Yaw, YawScale));
        }

        void Position(Vector3 p) =>
            w.U16((ushort)Fixed(p.X, PositionScale)).U16((ushort)Fixed(p.Y, PositionScale)).U16((ushort)Fixed(p.Z, PositionScale));

        void Joint(VrJoint? joint)
        {
            if (joint is { } j)
            {
                Position(j.Position);
                w.U32(VrQuaternion.Pack(j.Rotation));
            }
        }

        Joint(Head);
        Joint(LeftHand);
        Joint(RightHand);

        if (Triggers is { } t)
        {
            w.U8(t.LeftGrip).U8(t.LeftTrigger).U8(t.RightGrip).U8(t.RightTrigger);
        }

        if (Fingers is { } fingers)
        {
            if (fingers.Length != 10)
            {
                throw new ArgumentException("A pose has 10 finger curls.", nameof(Fingers));
            }

            w.Bytes(fingers);
        }

        if (Pointer is { } p)
        {
            w.U8(p.Hand).U16((ushort)Math.Clamp(MathF.Round(p.Length * LengthScale), 0, ushort.MaxValue));
        }

        Joint(Hips);
        Joint(Chest);
        Joint(LeftFoot);
        Joint(RightFoot);
        foreach (var bend in new[] { LeftKnee, RightKnee, LeftElbow, RightElbow })
        {
            if (bend is { } b)
            {
                Position(b);
            }
        }

        return w.ToArray();
    }

    /// <summary>Reads a pose, or null for one whose length doesn't match its parts or that has parts this version doesn't know.</summary>
    public static VrPoseMessage? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes)
        {
            return null;
        }

        var bytes = data.ToArray();
        var r = new PacketReader(bytes);
        var sequence = r.U16();
        var time = r.U16();
        var state = (VrState)r.U8();
        var parts = (Parts)r.U16();
        if ((parts & ~Parts.Known) != 0 || bytes.Length != HeaderBytes + SizeOf(parts))
        {
            return null;
        }

        float Pos() => (short)r.U16() / PositionScale;
        VrJoint? Joint(Parts part) => (parts & part) != 0 ? new VrJoint(new Vector3(Pos(), Pos(), Pos()), VrQuaternion.Unpack(r.U32())) : null;

        (Vector2, float)? body = (parts & Parts.Body) != 0 ? (new Vector2(Pos(), Pos()), (short)r.U16() / YawScale) : null;
        var head = Joint(Parts.Head);
        var left = Joint(Parts.LeftHand);
        var right = Joint(Parts.RightHand);
        VrTriggers? triggers = (parts & Parts.Triggers) != 0 ? new VrTriggers(r.U8(), r.U8(), r.U8(), r.U8()) : null;
        var fingers = (parts & Parts.Fingers) != 0 ? r.Bytes(10) : null;
        (byte, float)? pointer = (parts & Parts.Pointer) != 0 ? (r.U8(), r.U16() / LengthScale) : null;
        var hips = Joint(Parts.Hips);
        var chest = Joint(Parts.Chest);
        var leftFoot = Joint(Parts.LeftFoot);
        var rightFoot = Joint(Parts.RightFoot);
        Vector3? Bend(Parts part) => (parts & part) != 0 ? new Vector3(Pos(), Pos(), Pos()) : null;
        var leftKnee = Bend(Parts.LeftKnee);
        var rightKnee = Bend(Parts.RightKnee);
        var leftElbow = Bend(Parts.LeftElbow);
        var rightElbow = Bend(Parts.RightElbow);
        return new VrPoseMessage
        {
            Sequence = sequence,
            Time = time,
            State = state,
            Body = body,
            Head = head,
            LeftHand = left,
            RightHand = right,
            Triggers = triggers,
            Fingers = fingers,
            Pointer = pointer,
            Hips = hips,
            Chest = chest,
            LeftFoot = leftFoot,
            RightFoot = rightFoot,
            LeftKnee = leftKnee,
            RightKnee = rightKnee,
            LeftElbow = leftElbow,
            RightElbow = rightElbow,
        };
    }

    private static int SizeOf(Parts parts) =>
        ((parts & Parts.Body) != 0 ? 6 : 0) + ((parts & Parts.Head) != 0 ? 10 : 0) + ((parts & Parts.LeftHand) != 0 ? 10 : 0)
        + ((parts & Parts.RightHand) != 0 ? 10 : 0) + ((parts & Parts.Triggers) != 0 ? 4 : 0) + ((parts & Parts.Fingers) != 0 ? 10 : 0)
        + ((parts & Parts.Pointer) != 0 ? 3 : 0)
        + (10 * BitOperations.PopCount((uint)(parts & (Parts.Hips | Parts.Chest | Parts.LeftFoot | Parts.RightFoot))))
        + (6 * BitOperations.PopCount((uint)(parts & (Parts.LeftKnee | Parts.RightKnee | Parts.LeftElbow | Parts.RightElbow))));

    private static short Fixed(float value, float scale) =>
        (short)Math.Clamp(MathF.Round(value * scale), short.MinValue + 1, short.MaxValue);
}

/// <summary>A unit quaternion in 32 bits, as "smallest three": the largest component's index, then the other three in 10 bits each.</summary>
public static class VrQuaternion
{
    private const float Range = 0.70710678f;
    private const int Steps = 511;

    public static uint Pack(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        Span<float> c = [q.X, q.Y, q.Z, q.W];
        var largest = 0;
        for (var i = 1; i < 4; i++)
        {
            largest = MathF.Abs(c[i]) > MathF.Abs(c[largest]) ? i : largest;
        }

        var sign = c[largest] < 0 ? -1f : 1f;
        uint packed = (uint)largest << 30;
        var shift = 20;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
            {
                continue;
            }

            var value = (int)MathF.Round(Math.Clamp(c[i] * sign / Range, -1f, 1f) * Steps) + 512;
            packed |= (uint)value << shift;
            shift -= 10;
        }

        return packed;
    }

    public static Quaternion Unpack(uint packed)
    {
        var largest = (int)(packed >> 30);
        Span<float> c = stackalloc float[4];
        var shift = 20;
        var sum = 0f;
        for (var i = 0; i < 4; i++)
        {
            if (i == largest)
            {
                continue;
            }

            c[i] = ((int)((packed >> shift) & 0x3FF) - 512) / (float)Steps * Range;
            sum += c[i] * c[i];
            shift -= 10;
        }

        c[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
        return Quaternion.Normalize(new Quaternion(c[0], c[1], c[2], c[3]));
    }
}
