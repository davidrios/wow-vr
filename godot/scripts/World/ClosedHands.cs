using System.Collections.Generic;
using WowVR.Converters;
using Godot;

namespace WowVR.World;

/// <summary>Holds the fingers of a hand with something in it closed, whatever the animation does with them.</summary>
public partial class ClosedHands : SkeletonModifier3D
{
    public required IReadOnlyList<FingerPose> Fingers { get; init; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (GetSkeleton() is not { } skeleton)
        {
            return;
        }

        foreach (var finger in Fingers)
        {
            skeleton.SetBonePosePosition(finger.Bone, finger.Position);
            skeleton.SetBonePoseRotation(finger.Bone, finger.Rotation);
            skeleton.SetBonePoseScale(finger.Bone, finger.Scale);
        }
    }
}
