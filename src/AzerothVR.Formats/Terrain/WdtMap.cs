using AzerothVR.Formats.Assets;
using AzerothVR.Formats.Native;

namespace AzerothVR.Formats.Terrain;

/// <summary>Which tiles of a map exist, and how its ADT files store alpha maps.</summary>
public sealed class WdtMap
{
    private const int TileCount = WowCoords.TilesPerSide * WowCoords.TilesPerSide;
    private const uint BigAlphaFlags = 0x4 | 0x80;

    private readonly byte[] _tiles;

    public string Directory { get; }
    public bool BigAlpha { get; }

    private WdtMap(string directory, byte[] tiles, bool bigAlpha)
    {
        Directory = directory;
        _tiles = tiles;
        BigAlpha = bigAlpha;
    }

    public static WdtMap Read(IAssetSource source, string directory)
    {
        var path = $@"World\Maps\{directory}\{directory}.wdt";
        var data = source.Read(path) ?? throw new FileNotFoundException($"Not in the client: {path}", path);
        return Parse(data, directory);
    }

    public static unsafe WdtMap Parse(ReadOnlySpan<byte> data, string directory)
    {
        var tiles = new byte[TileCount];
        uint flags;

        fixed (byte* input = data, output = tiles)
        {
            if (TerrainNativeMethods.WdtRead(input, (nuint)data.Length, output, out flags) != WaStatus.Ok)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }
        }

        return new WdtMap(directory, tiles, (flags & BigAlphaFlags) != 0);
    }

    public int TileCountWithData => _tiles.Count(t => t != 0);

    public bool HasTile(int x, int y) =>
        x is >= 0 and < WowCoords.TilesPerSide
        && y is >= 0 and < WowCoords.TilesPerSide
        && _tiles[y * WowCoords.TilesPerSide + x] != 0;

    public string TilePath(int x, int y) => $@"World\Maps\{Directory}\{Directory}_{x}_{y}.adt";
}
