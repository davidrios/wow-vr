using System.Text;
using WowVR.Formats.Native;

namespace WowVR.Formats.Terrain;

/// <summary>Terrain data of one map chunk.</summary>
public sealed class TerrainChunk
{
    public const int VertexCount = 145;
    public const int AlphaMapSize = 64;

    public required int Ix { get; init; }
    public required int Iy { get; init; }
    public required int AreaId { get; init; }
    public required ushort Holes { get; init; }

    /// <summary>The chunk's north-west corner: north, west, base height.</summary>
    public required System.Numerics.Vector3 WorldPosition { get; init; }

    /// <summary>145 heights relative to the base height, in alternating rows of 9 and 8.</summary>
    public required float[] Heights { get; init; }

    /// <summary>145 normals of 3 signed bytes, as stored in the file.</summary>
    public required sbyte[] Normals { get; init; }

    public required int LayerCount { get; init; }

    /// <summary>Indexes into <see cref="AdtTerrain.TextureNames"/>, 4 slots.</summary>
    public required int[] TextureIds { get; init; }

    /// <summary>MCLY flags of each layer, 4 slots.</summary>
    public required uint[] LayerFlags { get; init; }

    /// <summary>Alpha of layers 1 to 3: 3 maps of 64x64 bytes, row by row.</summary>
    public required byte[] Alpha { get; init; }

    /// <summary>145 colors of 4 bytes as stored: blue, green, red, alpha, with 127 neutral.</summary>
    public required byte[] VertexColors { get; init; }
}

/// <summary>
/// A model placed on a tile. Position and bounds run east, up and south from the map's
/// north-west corner. Rotation is in degrees. Tiles a model overlaps all list it.
/// </summary>
public sealed record ModelPlacement(
    string Model,
    uint UniqueId,
    System.Numerics.Vector3 Position,
    System.Numerics.Vector3 Rotation,
    float Scale,
    int DoodadSet,
    System.Numerics.Vector3 BoundsMin,
    System.Numerics.Vector3 BoundsMax)
{
    /// <summary>The tile whose grid square holds the model's origin.</summary>
    public (int X, int Y) OriginTile => (
        Math.Clamp((int)Math.Floor(Position.X / WowCoords.TileSize), 0, WowCoords.TilesPerSide - 1),
        Math.Clamp((int)Math.Floor(Position.Z / WowCoords.TileSize), 0, WowCoords.TilesPerSide - 1));
}

/// <summary>The terrain of one ADT tile.</summary>
public sealed class AdtTerrain
{
    public const int ChunksPerSide = 16;

    /// <summary>The tile's M2 placements.</summary>
    public IReadOnlyList<ModelPlacement> Doodads { get; private init; } = [];

    /// <summary>The tile's WMO placements.</summary>
    public IReadOnlyList<ModelPlacement> Wmos { get; private init; } = [];

    /// <summary>The tile's liquid, chunk by chunk.</summary>
    public IReadOnlyList<LiquidLayer> Liquids { get; private init; } = [];

    public IReadOnlyList<string> TextureNames { get; }

    /// <summary>256 chunks, ordered by <see cref="TerrainChunk.Iy"/> then <see cref="TerrainChunk.Ix"/>.</summary>
    public IReadOnlyList<TerrainChunk> Chunks { get; }

    private AdtTerrain(IReadOnlyList<string> textureNames, IReadOnlyList<TerrainChunk> chunks)
    {
        TextureNames = textureNames;
        Chunks = chunks;
    }

    public static unsafe AdtTerrain Parse(ReadOnlySpan<byte> data, bool bigAlpha)
    {
        nint pointer;
        fixed (byte* input = data)
        {
            pointer = TerrainNativeMethods.AdtOpen(input, (nuint)data.Length, bigAlpha ? 1 : 0);
        }

        if (pointer == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        using var handle = AdtHandle.From(pointer);

        var names = new string[TerrainNativeMethods.AdtTextureCount(handle)];
        for (var i = 0; i < names.Length; i++)
        {
            TerrainNativeMethods.AdtTextureName(handle, (uint)i, out var text, out var length);
            names[i] = Encoding.UTF8.GetString(text, checked((int)length));
        }

        var chunks = new TerrainChunk[ChunksPerSide * ChunksPerSide];
        for (var i = 0; i < chunks.Length; i++)
        {
            if (TerrainNativeMethods.AdtChunk(handle, (uint)i, out var chunk) != WaStatus.Ok)
            {
                throw new WowAlchemyException($"Tile is missing chunk {i}.");
            }

            chunks[i] = Copy(chunk);
        }

        return new AdtTerrain(names, chunks)
        {
            Doodads = ReadPlacements(handle, wmo: false),
            Wmos = ReadPlacements(handle, wmo: true),
            Liquids = ReadLiquids(handle),
        };
    }

    private static unsafe LiquidLayer[] ReadLiquids(AdtHandle handle)
    {
        var layers = new LiquidLayer[TerrainNativeMethods.AdtLiquidCount(handle)];
        for (var i = 0; i < layers.Length; i++)
        {
            if (TerrainNativeMethods.AdtLiquid(handle, (uint)i, out var layer) != WaStatus.Ok)
            {
                throw new WowAlchemyException($"Tile is missing liquid layer {i}.");
            }

            layers[i] = new LiquidLayer
            {
                ChunkIx = (int)layer.Chunk % ChunksPerSide,
                ChunkIy = (int)layer.Chunk / ChunksPerSide,
                LiquidType = (int)layer.LiquidType,
                Cells = layer.Cells,
                Heights = new ReadOnlySpan<float>(layer.Heights, LiquidLayer.VertexCount).ToArray(),
                Depths = new ReadOnlySpan<byte>(layer.Depths, LiquidLayer.VertexCount).ToArray(),
            };
        }

        return layers;
    }

    private static unsafe ModelPlacement[] ReadPlacements(AdtHandle handle, bool wmo)
    {
        WaPlacement* placements;
        nuint count;
        if (wmo)
        {
            ModelNativeMethods.AdtWmos(handle, out placements, out count);
        }
        else
        {
            ModelNativeMethods.AdtDoodads(handle, out placements, out count);
        }

        var names = new Dictionary<uint, string>();
        var result = new ModelPlacement[(int)count];
        for (var i = 0; i < result.Length; i++)
        {
            ref readonly var p = ref placements[i];
            if (!names.TryGetValue(p.Name, out var name))
            {
                var status = wmo
                    ? ModelNativeMethods.AdtWmoName(handle, p.Name, out var text, out var length)
                    : ModelNativeMethods.AdtModelName(handle, p.Name, out text, out length);
                if (status != WaStatus.Ok)
                {
                    throw new WowAlchemyException($"A placement names missing model {p.Name}.");
                }

                names[p.Name] = name = Encoding.UTF8.GetString(text, checked((int)length));
            }

            result[i] = new ModelPlacement(
                wmo ? name : Models.M2Model.NormalizePath(name),
                p.UniqueId,
                new System.Numerics.Vector3(p.Position[0], p.Position[1], p.Position[2]),
                new System.Numerics.Vector3(p.Rotation[0], p.Rotation[1], p.Rotation[2]),
                p.Scale,
                (int)p.DoodadSet,
                new System.Numerics.Vector3(p.BoundsMin[0], p.BoundsMin[1], p.BoundsMin[2]),
                new System.Numerics.Vector3(p.BoundsMax[0], p.BoundsMax[1], p.BoundsMax[2]));
        }

        return result;
    }

    private static unsafe TerrainChunk Copy(in WaTerrainChunk chunk)
    {
        return new TerrainChunk
        {
            Ix = (int)chunk.Ix,
            Iy = (int)chunk.Iy,
            AreaId = (int)chunk.AreaId,
            Holes = (ushort)chunk.Holes,
            WorldPosition = new System.Numerics.Vector3(chunk.Position[0], chunk.Position[1], chunk.Position[2]),
            Heights = new ReadOnlySpan<float>(chunk.Heights, TerrainChunk.VertexCount).ToArray(),
            Normals = new ReadOnlySpan<sbyte>(chunk.Normals, TerrainChunk.VertexCount * 3).ToArray(),
            LayerCount = (int)chunk.LayerCount,
            TextureIds =
            [
                (int)chunk.TextureIds[0],
                (int)chunk.TextureIds[1],
                (int)chunk.TextureIds[2],
                (int)chunk.TextureIds[3],
            ],
            LayerFlags = [chunk.LayerFlags[0], chunk.LayerFlags[1], chunk.LayerFlags[2], chunk.LayerFlags[3]],
            Alpha = new ReadOnlySpan<byte>(chunk.Alpha, 3 * TerrainChunk.AlphaMapSize * TerrainChunk.AlphaMapSize).ToArray(),
            VertexColors = new ReadOnlySpan<byte>(chunk.VertexColors, TerrainChunk.VertexCount * 4).ToArray(),
        };
    }
}
