using System.Numerics;

namespace AzerothVR.Formats;

/// <summary>
/// The only place that converts between WoW and Godot coordinates. WoW: X north,
/// Y west, Z up. Godot: X east, Y up, Z south. Both in yards.
/// </summary>
public static class WowCoords
{
    public const int TilesPerSide = 64;
    public const double TileSize = 1600.0 / 3.0;
    public const double ChunkSize = TileSize / 16.0;

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

    private static int TileIndex(double wow) =>
        Math.Clamp((int)Math.Floor((MapOrigin - wow) / TileSize), 0, TilesPerSide - 1);
}
