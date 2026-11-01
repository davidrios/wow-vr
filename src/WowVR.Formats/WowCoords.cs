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

    private static int TileIndex(double wow) =>
        Math.Clamp((int)Math.Floor((MapOrigin - wow) / TileSize), 0, TilesPerSide - 1);
}
