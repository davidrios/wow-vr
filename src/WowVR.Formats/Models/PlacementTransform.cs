using System.Numerics;

namespace WowVR.Formats;

/// <summary>
/// Rotation, uniform scale and translation in Godot axes, as columns, so the Godot side
/// can build a Transform3D without this project depending on Godot.
/// </summary>
public readonly record struct PlacementTransform(Vector3 AxisX, Vector3 AxisY, Vector3 AxisZ, Vector3 Origin)
{
    public static readonly PlacementTransform Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

    public Vector3 Apply(Vector3 point) =>
        AxisX * point.X + AxisY * point.Y + AxisZ * point.Z + Origin;

    /// <summary>This transform after <paramref name="inner"/>: the point goes through that one first.</summary>
    public PlacementTransform Compose(in PlacementTransform inner) => new(
        AxisX * inner.AxisX.X + AxisY * inner.AxisX.Y + AxisZ * inner.AxisX.Z,
        AxisX * inner.AxisY.X + AxisY * inner.AxisY.Y + AxisZ * inner.AxisY.Z,
        AxisX * inner.AxisZ.X + AxisY * inner.AxisZ.Y + AxisZ * inner.AxisZ.Z,
        Apply(inner.Origin));

    /// <summary>System.Numerics matrices multiply row vectors, so their rows are the axes.</summary>
    internal static PlacementTransform FromRotation(Matrix4x4 m, float scale, Vector3 origin) => new(
        new Vector3(m.M11, m.M12, m.M13) * scale,
        new Vector3(m.M21, m.M22, m.M23) * scale,
        new Vector3(m.M31, m.M32, m.M33) * scale,
        origin);
}
