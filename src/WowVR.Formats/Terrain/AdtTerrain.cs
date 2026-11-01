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

/// <summary>The terrain of one ADT tile.</summary>
public sealed class AdtTerrain
{
    public const int ChunksPerSide = 16;

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

        return new AdtTerrain(names, chunks);
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
