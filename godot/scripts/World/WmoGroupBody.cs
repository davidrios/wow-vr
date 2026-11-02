using WowVR.Converters;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.World;

/// <summary>A WMO group's collision, which can say what light is baked into the floor a ray hit.</summary>
public partial class WmoGroupBody : StaticBody3D
{
    public WmoGroupAsset Group { get; init; } = null!;

    /// <summary>The WMO's <c>MOHD</c> ambient, for a floor with no vertex colors.</summary>
    public System.Numerics.Vector3 Ambient { get; init; }

    /// <summary>Gamma-space light for a creature at a point on this group's floor. Null if the group is lit by the sky.</summary>
    public System.Numerics.Vector3? CreatureLightAt(Vector3 globalPoint)
    {
        if (!Group.Indoor)
        {
            return null;
        }

        var local = GetParent<Node3D>().ToLocal(globalPoint);
        return WmoFloorLight.ForCreature(Group.FloorLight?.Sample(new System.Numerics.Vector3(local.X, local.Y, local.Z)) ?? Ambient);
    }
}
