using System.Collections.Generic;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.World;

/// <summary>
/// Turns a creature's billboarded bones to the camera after its animation has played. Doodads
/// do this in their vertex shader, which can't work here because a skeleton has already moved
/// the vertices by then. It isn't a skeleton modifier, because any modifier makes the skeleton
/// back up and pose every bone a second time at each update.
/// </summary>
public sealed class BillboardBones(IReadOnlyList<(int Bone, M2BillboardKind Kind)> bones)
{
    /// <summary>The camera, set once a frame before creatures animate, rather than looked up by every one.</summary>
    public static Transform3D? Camera { get; set; }

    /// <summary>
    /// Call after the animation moves the skeleton. Each kind keeps the axes it locks and takes the
    /// rest from the camera, so applying it to a pose it already turned gives the same result.
    /// </summary>
    public void Apply(Skeleton3D skeleton)
    {
        if (Camera is not { } camera)
        {
            return;
        }

        var toLocal = skeleton.GlobalTransform.AffineInverse();
        var view = (toLocal.Basis * camera.Basis).Orthonormalized();
        var eye = toLocal * camera.Origin;
        foreach (var (bone, kind) in bones)
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
