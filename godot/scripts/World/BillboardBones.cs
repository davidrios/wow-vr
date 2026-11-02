using System.Collections.Generic;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.World;

/// <summary>
/// Turns a creature's billboarded bones to the camera after its animation has played. Doodads
/// do this in their vertex shader, which can't work here because a skeleton has already moved
/// the vertices by then.
/// </summary>
public partial class BillboardBones : SkeletonModifier3D
{
    public required IReadOnlyList<(int Bone, M2BillboardKind Kind)> Bones { get; init; }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        var camera = GetViewport().GetCamera3D();
        if (skeleton is null || camera is null)
        {
            return;
        }

        var toLocal = skeleton.GlobalTransform.AffineInverse();
        var view = (toLocal.Basis * camera.GlobalBasis).Orthonormalized();
        var eye = toLocal * camera.GlobalPosition;
        foreach (var (bone, kind) in Bones)
        {
            var pose = skeleton.GetBoneGlobalPose(bone);
            var facing = (eye - pose.Origin).Normalized();
            Vector3 x, y, z;
            switch (kind)
            {
                case M2BillboardKind.LockModelX:
                    x = pose.Basis.X.Normalized();
                    z = x.Cross(facing).Normalized();
                    y = z.Cross(x);
                    break;
                case M2BillboardKind.LockModelY:
                    z = pose.Basis.Z.Normalized();
                    x = (facing - z * facing.Dot(z)).Normalized();
                    y = z.Cross(x);
                    break;
                case M2BillboardKind.LockModelZ:
                    y = pose.Basis.Y.Normalized();
                    x = (facing - y * facing.Dot(y)).Normalized();
                    z = x.Cross(y);
                    break;
                default:
                    x = view.Z;
                    y = view.Y;
                    z = x.Cross(y);
                    break;
            }

            var scale = pose.Basis.Scale;
            skeleton.SetBoneGlobalPose(bone, new Transform3D(new Basis(x * scale.X, y * scale.Y, z * scale.Z), pose.Origin));
        }
    }
}
