using WowVR.Converters;
using Godot;

namespace WowVR.World;

/// <summary>A doodad's collision, which knows its model and where it's drawn, for the texture brush.</summary>
public partial class DoodadBody : StaticBody3D
{
    public M2Asset Model { get; init; } = null!;

    /// <summary>Where the model is drawn in the body's parent, with its scale, which the body leaves to its shape.</summary>
    public Transform3D Placement { get; init; }
}
