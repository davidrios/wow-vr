using System.Numerics;

namespace AzerothVR.Formats.Terrain;

/// <summary>
/// One tile as a single mesh in Godot axes, relative to the tile's north-west
/// corner at height 0. The texture array and alpha atlas let the whole tile be
/// one draw call, however many textures its chunks use.
/// </summary>
public sealed class TerrainMeshData
{
    private const int CellsPerSide = 8;
    private const int RowsPerChunk = CellsPerSide * 2 + 1;
    private const int VerticesPerRowPair = CellsPerSide * 2 + 1;
    private const float CellSize = (float)(WowCoords.ChunkSize / CellsPerSide);
    private const int AtlasCell = TerrainChunk.AlphaMapSize;
    private const float NormalScale = 127f;
    private const uint ReflectionLayerFlag = 0x400;

    /// <summary>Width and height of <see cref="AlphaAtlas"/>.</summary>
    public const int AtlasSize = AdtTerrain.ChunksPerSide * AtlasCell;

    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }

    /// <summary>Position within the chunk, 0 to 1.</summary>
    public required Vector2[] ChunkUvs { get; init; }

    /// <summary>Position in <see cref="AlphaAtlas"/>, 0 to 1.</summary>
    public required Vector2[] AtlasUvs { get; init; }

    /// <summary>The textures the tile draws, which <see cref="TextureIndexes"/> index.</summary>
    public required IReadOnlyList<string> Textures { get; init; }

    /// <summary>4 bytes per vertex: the chunk's 4 texture indexes.</summary>
    public required byte[] TextureIndexes { get; init; }

    /// <summary>4 bytes per vertex: red, green, blue, alpha, with 127 neutral.</summary>
    public required byte[] VertexColors { get; init; }

    public required int[] Indices { get; init; }

    /// <summary>RGB bytes: the alpha of layers 1, 2 and 3 for every chunk.</summary>
    public required byte[] AlphaAtlas { get; init; }

    public static TerrainMeshData Build(AdtTerrain terrain)
    {
        var chunkCount = terrain.Chunks.Count;
        var vertexCount = chunkCount * TerrainChunk.VertexCount;

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var chunkUvs = new Vector2[vertexCount];
        var atlasUvs = new Vector2[vertexCount];
        var textureIndexes = new byte[vertexCount * 4];
        var vertexColors = new byte[vertexCount * 4];
        var indices = new List<int>(chunkCount * CellsPerSide * CellsPerSide * 12);
        var atlas = new byte[AtlasSize * AtlasSize * 3];
        var textures = new List<string>();
        var textureSlots = new Dictionary<int, byte>();

        foreach (var chunk in terrain.Chunks)
        {
            var firstVertex = (chunk.Iy * AdtTerrain.ChunksPerSide + chunk.Ix) * TerrainChunk.VertexCount;
            var chunkEast = (float)(chunk.Ix * WowCoords.ChunkSize);
            var chunkSouth = (float)(chunk.Iy * WowCoords.ChunkSize);

            var vertex = firstVertex;
            for (var row = 0; row < RowsPerChunk; row++)
            {
                var isInnerRow = row % 2 == 1;
                var columns = isInnerRow ? CellsPerSide : CellsPerSide + 1;

                for (var column = 0; column < columns; column++, vertex++)
                {
                    var east = (column + (isInnerRow ? 0.5f : 0f)) * CellSize;
                    var south = row * 0.5f * CellSize;
                    var height = chunk.WorldPosition.Z + chunk.Heights[vertex - firstVertex];

                    positions[vertex] = WowCoords.ToGodot(new Vector3(-(chunkSouth + south), -(chunkEast + east), height));
                    normals[vertex] = ReadNormal(chunk.Normals, vertex - firstVertex);

                    var u = east / (float)WowCoords.ChunkSize;
                    var v = south / (float)WowCoords.ChunkSize;
                    chunkUvs[vertex] = new Vector2(u, v);
                    atlasUvs[vertex] = new Vector2(AtlasCoordinate(chunk.Ix, u), AtlasCoordinate(chunk.Iy, v));

                    var color = chunk.VertexColors.AsSpan((vertex - firstVertex) * 4, 4);
                    var outputColor = vertexColors.AsSpan(vertex * 4, 4);
                    outputColor[0] = color[2];
                    outputColor[1] = color[1];
                    outputColor[2] = color[0];
                    outputColor[3] = color[3];

                    var indexes = textureIndexes.AsSpan(vertex * 4, 4);
                    for (var layer = 0; layer < 4; layer++)
                    {
                        var texture = SurfaceTexture(chunk, layer);
                        if (!textureSlots.TryGetValue(texture, out var slot))
                        {
                            slot = checked((byte)textures.Count);
                            textureSlots[texture] = slot;
                            textures.Add(terrain.TextureNames[texture]);
                        }

                        indexes[layer] = slot;
                    }
                }
            }

            AddCells(indices, firstVertex, chunk.Holes);
            CopyAlpha(chunk, atlas);
        }

        return new TerrainMeshData
        {
            Positions = positions,
            Normals = normals,
            ChunkUvs = chunkUvs,
            AtlasUvs = atlasUvs,
            Textures = textures,
            TextureIndexes = textureIndexes,
            VertexColors = vertexColors,
            Indices = indices.ToArray(),
            AlphaAtlas = atlas,
        };
    }

    /// <summary>
    /// The texture a layer draws. Reflection layers name an environment map rather than
    /// a surface, so they draw the next surface layer, which is what the chunk's
    /// neighbours show. The reflection itself isn't rendered yet.
    /// </summary>
    public static int SurfaceTexture(TerrainChunk chunk, int layer)
    {
        // Unused slots have zero alpha, so they only need not to add a texture.
        layer = Math.Min(layer, chunk.LayerCount - 1);

        for (var i = layer; i < chunk.LayerCount; i++)
        {
            if ((chunk.LayerFlags[i] & ReflectionLayerFlag) == 0)
            {
                return chunk.TextureIds[i];
            }
        }

        for (var i = layer - 1; i >= 0; i--)
        {
            if ((chunk.LayerFlags[i] & ReflectionLayerFlag) == 0)
            {
                return chunk.TextureIds[i];
            }
        }

        return chunk.TextureIds[layer];
    }

    /// <summary>Stored as WoW X, Y, Z, which a test checks against the slope of the heights.</summary>
    private static Vector3 ReadNormal(sbyte[] normals, int vertex)
    {
        var wow = new Vector3(normals[vertex * 3], normals[vertex * 3 + 1], normals[vertex * 3 + 2]) / NormalScale;
        return Vector3.Normalize(WowCoords.ToGodot(wow));
    }

    /// <summary>Stays half a texel inside the chunk's cell, because neighbouring cells can mean different textures.</summary>
    private static float AtlasCoordinate(int cell, float position) =>
        (cell + (0.5f + position * (AtlasCell - 1)) / AtlasCell) / AdtTerrain.ChunksPerSide;

    /// <summary>Four triangles around each inner vertex, clockwise from above, which is Godot's front face.</summary>
    private static void AddCells(List<int> indices, int firstVertex, ushort holes)
    {
        for (var y = 0; y < CellsPerSide; y++)
        {
            for (var x = 0; x < CellsPerSide; x++)
            {
                if (IsHole(holes, x, y))
                {
                    continue;
                }

                var topLeft = firstVertex + y * VerticesPerRowPair + x;
                var topRight = topLeft + 1;
                var center = topLeft + CellsPerSide + 1;
                var bottomLeft = topLeft + VerticesPerRowPair;
                var bottomRight = bottomLeft + 1;

                indices.AddRange([center, topLeft, topRight]);
                indices.AddRange([center, topRight, bottomRight]);
                indices.AddRange([center, bottomRight, bottomLeft]);
                indices.AddRange([center, bottomLeft, topLeft]);
            }
        }
    }

    /// <summary>The mask is 4x4, so each bit covers 2x2 cells.</summary>
    public static bool IsHole(ushort holes, int cellX, int cellY) =>
        (holes & (1 << (cellX / 2 + cellY / 2 * 4))) != 0;

    private static void CopyAlpha(TerrainChunk chunk, byte[] atlas)
    {
        const int mapBytes = AtlasCell * AtlasCell;

        for (var row = 0; row < AtlasCell; row++)
        {
            var output = ((chunk.Iy * AtlasCell + row) * AtlasSize + chunk.Ix * AtlasCell) * 3;
            for (var column = 0; column < AtlasCell; column++)
            {
                var pixel = row * AtlasCell + column;
                atlas[output++] = chunk.Alpha[pixel];
                atlas[output++] = chunk.Alpha[mapBytes + pixel];
                atlas[output++] = chunk.Alpha[mapBytes * 2 + pixel];
            }
        }
    }
}
