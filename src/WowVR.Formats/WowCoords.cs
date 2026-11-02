using System.Numerics;

namespace WowVR.Formats;

/// <summary>
/// The only place that converts between WoW and Godot coordinates. WoW: X north,
/// Y west, Z up. Godot: X east, Y up, Z south. Both in yards.
/// </summary>
public static class WowCoords
{
    public const int TilesPerSide = 64;
    public const double TileSize = 1600.0 / 3.0;
    public const int ChunksPerTile = 16;
    public const double ChunkSize = TileSize / ChunksPerTile;

    /// <summary>World X and Y at the map's north-west corner, the origin of tile (0, 0).</summary>
    public const double MapOrigin = TilesPerSide / 2 * TileSize;

    public static Vector3 ToGodot(Vector3 wow) => new(-wow.Y, wow.Z, -wow.X);

    public static Vector3 ToWow(Vector3 godot) => new(-godot.Z, -godot.X, godot.Y);

    /// <summary>The tile at a world position. Tile X runs east and tile Y runs south.</summary>
    public static (int X, int Y) TileAt(double wowX, double wowY) =>
        (TileIndex(wowY), TileIndex(wowX));

    /// <summary>The Godot X and Z of a tile's north-west corner, the origin of its mesh.</summary>
    public static (double X, double Z) TileOriginGodot(int tileX, int tileY) =>
        (tileX * TileSize - MapOrigin, tileY * TileSize - MapOrigin);

    /// <summary>
    /// The chunk at a Godot position, counted across the whole map: divide by
    /// <see cref="ChunksPerTile"/> for the tile. Positions off the map give indexes off the map.
    /// </summary>
    public static (int X, int Z) ChunkAtGodot(double x, double z) =>
        ((int)Math.Floor((x + MapOrigin) / ChunkSize), (int)Math.Floor((z + MapOrigin) / ChunkSize));

    /// <summary>
    /// A vector in a model's or WMO's own space (X forward, Y left, Z up) in Godot axes.
    /// This differs from <see cref="ToGodot"/>, which is for map space:
    /// <see cref="PlacementTransform"/> carries the difference.
    /// </summary>
    public static Vector3 ModelToGodot(Vector3 model) => new(model.X, model.Z, -model.Y);

    /// <summary>
    /// A model placed on a tile. Files give the position as east, up and south from the
    /// map's north-west corner, and rotations in degrees, as WoWModelViewer applies them:
    /// around up by Y - 90, around south... Z by -X, then around X by Z.
    /// </summary>
    public static PlacementTransform TilePlacement(Vector3 position, Vector3 rotationDegrees, float scale, int tileX, int tileY)
    {
        var yaw = float.DegreesToRadians(rotationDegrees.Y - 90f);
        var roll = float.DegreesToRadians(-rotationDegrees.X);
        var pitch = float.DegreesToRadians(rotationDegrees.Z);

        var rotation = Matrix4x4.CreateRotationX(pitch)
            * Matrix4x4.CreateRotationZ(roll)
            * Matrix4x4.CreateRotationY(yaw);

        // Relative to the tile's corner, so it stays precise far from the map's center.
        var origin = new Vector3(
            (float)(position.X - tileX * TileSize),
            position.Y,
            (float)(position.Z - tileY * TileSize));

        return PlacementTransform.FromRotation(rotation, scale, origin);
    }

    /// <summary>A doodad placed in a WMO's own space, as stored in its file.</summary>
    public static PlacementTransform WmoDoodadTransform(Vector3 position, Quaternion rotation, float scale)
    {
        // A rotation around an axis is the same rotation around that axis in new axes.
        var axis = ModelToGodot(new Vector3(rotation.X, rotation.Y, rotation.Z));
        var godot = Quaternion.Normalize(new Quaternion(axis, rotation.W));

        return PlacementTransform.FromRotation(Matrix4x4.CreateFromQuaternion(godot), scale, ModelToGodot(position));
    }

    private static int TileIndex(double wow) =>
        Math.Clamp((int)Math.Floor((MapOrigin - wow) / TileSize), 0, TilesPerSide - 1);
}
