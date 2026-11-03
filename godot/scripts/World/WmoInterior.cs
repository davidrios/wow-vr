using WowVR.Formats.Models;
using Godot;

namespace WowVR.World;

/// <summary>
/// The inside of a WMO the camera is in, when none of the outside can be seen from there: then
/// only its groups in view need drawing, and only what's in them. See <see cref="TileManager.Interior"/>.
/// </summary>
public sealed class WmoInterior(Node3D node, WmoPortals portals, bool[] visible)
{
    public Node3D Node { get; } = node;

    /// <summary>Whether a point in the scene is in one of the groups in view.</summary>
    public bool Shows(Vector3 global)
    {
        // Portals are in the file's axes, Z up.
        var local = Node.ToLocal(global);
        return portals.InVisibleInterior(new System.Numerics.Vector3(local.X, -local.Z, local.Y), visible);
    }
}
