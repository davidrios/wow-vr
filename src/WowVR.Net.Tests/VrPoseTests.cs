using System.Numerics;
using Xunit;

namespace WowVR.Net.Tests;

public class VrPoseTests
{
    private static readonly VrPoseMessage Full = new()
    {
        Sequence = 65535,
        Time = 1234,
        State = VrState.InVr | VrState.LeftTracked | VrState.RightTracked,
        Body = (new Vector2(-0.25f, 0.125f), -0.5f),
        Head = new VrJoint(new Vector3(0.1f, 0f, 1.85f), Quaternion.CreateFromYawPitchRoll(0.3f, -0.2f, 0.1f)),
        LeftHand = new VrJoint(new Vector3(0.3f, 0.25f, 1.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 2.5f)),
        RightHand = new VrJoint(new Vector3(0.3f, -0.25f, -31.9f), Quaternion.Identity),
        Triggers = new VrTriggers(0, 12, 200, 255),
        Fingers = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10],
        Pointer = (1, 12.5f),
        Hips = new VrJoint(new Vector3(0, 0, 1.1f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.4f)),
        Chest = new VrJoint(new Vector3(0, 0, 1.5f), Quaternion.Identity),
        LeftFoot = new VrJoint(new Vector3(0.1f, 0.15f, 0.08f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.2f)),
        RightFoot = new VrJoint(new Vector3(0.1f, -0.15f, 0.08f), Quaternion.Identity),
        LeftKnee = new Vector3(0.15f, 0.12f, 0.55f),
        RightKnee = new Vector3(0.15f, -0.12f, 0.55f),
        LeftElbow = new Vector3(-0.1f, 0.3f, 1.25f),
        RightElbow = new Vector3(-0.1f, -0.3f, 1.25f),
    };

    /// <summary>The spec's sizes: a 7-byte header and 117 bytes of parts.</summary>
    [Fact]
    public void AFullPoseIs124Bytes() => Assert.Equal(124, Full.Write().Length);

    [Fact]
    public void PosesReadBackWhatWasWritten()
    {
        var read = VrPoseMessage.Read(Full.Write())!;

        Assert.Equal(Full.Sequence, read.Sequence);
        Assert.Equal(Full.Time, read.Time);
        Assert.Equal(Full.State, read.State);
        Assert.Equal(Full.Body!.Value.Offset.X, read.Body!.Value.Offset.X, 3);
        Assert.Equal(Full.Body.Value.Yaw, read.Body.Value.Yaw, 4);
        Assert.Equal(Full.Head!.Value.Position.Z, read.Head!.Value.Position.Z, 3);
        Assert.Equal(Full.RightHand!.Value.Position.Z, read.RightHand!.Value.Position.Z, 3);
        Assert.Equal(Full.Triggers, read.Triggers);
        Assert.Equal(Full.Fingers, read.Fingers);
        Assert.Equal(Full.Pointer!.Value.Length, read.Pointer!.Value.Length, 2);
        Assert.Equal(Full.LeftFoot!.Value.Position.Y, read.LeftFoot!.Value.Position.Y, 3);
        Assert.Equal(Full.RightElbow!.Value.Z, read.RightElbow!.Value.Z, 3);
        Assert.Equal(Full.LeftKnee!.Value.X, read.LeftKnee!.Value.X, 3);
        foreach (var (written, back) in new[] { (Full.Head, read.Head), (Full.LeftHand, read.LeftHand), (Full.RightHand, read.RightHand), (Full.Hips, read.Hips), (Full.LeftFoot, read.LeftFoot) })
        {
            // The same turn either way round, to about a tenth of a degree.
            Assert.True(MathF.Abs(Quaternion.Dot(written!.Value.Rotation, back!.Value.Rotation)) > 0.99999f);
        }
    }

    [Fact]
    public void MissingPartsStayMissing()
    {
        var read = VrPoseMessage.Read(new VrPoseMessage { State = VrState.InVr, Head = Full.Head }.Write())!;
        Assert.Null(read.Body);
        Assert.Null(read.LeftHand);
        Assert.Null(read.Fingers);
        Assert.Null(read.Hips);
        Assert.Null(read.RightKnee);
        Assert.NotNull(read.Head);
    }

    [Fact]
    public void BadPosesAreRefused()
    {
        var bytes = Full.Write();
        Assert.Null(VrPoseMessage.Read(bytes.AsSpan(0, bytes.Length - 1)));

        // Bit 15 isn't a part.
        bytes[6] |= 0x80;
        Assert.Null(VrPoseMessage.Read(bytes));
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 1f)]
    [InlineData(0.5f, -0.5f, 0.5f, -0.5f)]
    [InlineData(0.9f, 0.1f, -0.3f, 0.2f)]
    public void QuaternionsPackToAboutATenthOfADegree(float x, float y, float z, float w)
    {
        var q = Quaternion.Normalize(new Quaternion(x, y, z, w));
        var back = VrQuaternion.Unpack(VrQuaternion.Pack(q));
        var degrees = 2 * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(q, back)))) * 180 / MathF.PI;
        Assert.True(degrees < 0.2f, $"{degrees} degrees off");
    }
}
