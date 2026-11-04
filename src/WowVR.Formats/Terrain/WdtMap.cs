using WowVR.Formats.Assets;
using WowVR.Formats.Native;

namespace WowVR.Formats.Terrain;

/// <summary>Which tiles of a map exist, and how its ADT files store alpha maps.</summary>
public sealed class WdtMap
{
    private const int TileCount = WowCoords.TilesPerSide * WowCoords.TilesPerSide;
    private const uint BigAlphaFlags = 0x4 | 0x80;

    private readonly byte[] _tiles;

    public string Directory { get; }
    public bool BigAlpha { get; }

    private WdtMap(string directory, byte[] tiles, bool bigAlpha, ModelPlacement? globalWmo)
    {
        Directory = directory;
        _tiles = tiles;
        BigAlpha = bigAlpha;
        GlobalWmo = globalWmo;
    }

    /// <summary>The one WMO a map without terrain is made of, as most instances are, or null.</summary>
    public ModelPlacement? GlobalWmo { get; }

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

        ModelPlacement? globalWmo = null;
        fixed (byte* input = data, output = tiles)
        {
            if (TerrainNativeMethods.WdtRead(input, (nuint)data.Length, output, out flags) != WaStatus.Ok)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            var status = TerrainNativeMethods.WdtGlobalWmo(input, (nuint)data.Length, out var name, out var p);
            if (status == WaStatus.Error)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            if (status == WaStatus.Ok)
            {
                var path = System.Text.Encoding.UTF8.GetString(NativeMethods.TakeBuffer(name));
                globalWmo = new ModelPlacement(
                    path,
                    p.UniqueId,
                    new System.Numerics.Vector3(p.Position[0], p.Position[1], p.Position[2]),
                    new System.Numerics.Vector3(p.Rotation[0], p.Rotation[1], p.Rotation[2]),
                    1f,
                    (int)p.DoodadSet,
                    new System.Numerics.Vector3(p.BoundsMin[0], p.BoundsMin[1], p.BoundsMin[2]),
                    new System.Numerics.Vector3(p.BoundsMax[0], p.BoundsMax[1], p.BoundsMax[2]));
            }
        }

        return new WdtMap(directory, tiles, (flags & BigAlphaFlags) != 0, globalWmo);
    }

    public int TileCountWithData => _tiles.Count(t => t != 0);

    public bool HasTile(int x, int y) =>
        x is >= 0 and < WowCoords.TilesPerSide
        && y is >= 0 and < WowCoords.TilesPerSide
        && _tiles[y * WowCoords.TilesPerSide + x] != 0;

    public string TilePath(int x, int y) => $@"World\Maps\{Directory}\{Directory}_{x}_{y}.adt";
}
