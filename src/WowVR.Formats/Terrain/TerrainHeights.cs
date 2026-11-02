namespace WowVR.Formats.Terrain;

/// <summary>A tile's terrain height anywhere on it, without physics, for things far from the player.</summary>
public sealed class TerrainHeights
{
    private const int CellsPerChunk = 8;
    private const int RowStride = 17;
    private const int InnerRowOffset = 9;

    private readonly int _tileX;
    private readonly int _tileY;

    /// <summary>145 absolute heights per chunk.</summary>
    private readonly float[] _heights = new float[AdtTerrain.ChunksPerSide * AdtTerrain.ChunksPerSide * TerrainChunk.VertexCount];

    /// <summary>The ground effect showing most in each cell of each chunk.</summary>
    private readonly uint[] _effects = new uint[AdtTerrain.ChunksPerSide * AdtTerrain.ChunksPerSide * CellsPerChunk * CellsPerChunk];

    public TerrainHeights(AdtTerrain terrain, int tileX, int tileY)
    {
        _tileX = tileX;
        _tileY = tileY;
        foreach (var chunk in terrain.Chunks)
        {
            FillEffects(chunk);
            var start = (chunk.Iy * AdtTerrain.ChunksPerSide + chunk.Ix) * TerrainChunk.VertexCount;
            for (var i = 0; i < TerrainChunk.VertexCount; i++)
            {
                _heights[start + i] = chunk.WorldPosition.Z + chunk.Heights[i];
            }
        }
    }

    private void FillEffects(TerrainChunk chunk)
    {
        const int AlphaLayers = 3;
        const int TexelsPerCell = TerrainChunk.AlphaMapSize / CellsPerChunk;
        var start = (chunk.Iy * AdtTerrain.ChunksPerSide + chunk.Ix) * CellsPerChunk * CellsPerChunk;
        for (var row = 0; row < CellsPerChunk; row++)
        {
            for (var column = 0; column < CellsPerChunk; column++)
            {
                var texel = (row * TexelsPerCell + TexelsPerCell / 2) * TerrainChunk.AlphaMapSize + column * TexelsPerCell + TexelsPerCell / 2;
                var effect = chunk.EffectIds[0];
                var best = 0;
                var total = 0;
                for (var layer = 1; layer < chunk.LayerCount && layer <= AlphaLayers; layer++)
                {
                    int alpha = chunk.Alpha[(layer - 1) * TerrainChunk.AlphaMapSize * TerrainChunk.AlphaMapSize + texel];
                    total += alpha;
                    if (alpha > best)
                    {
                        (best, effect) = (alpha, chunk.EffectIds[layer]);
                    }
                }

                // The base layer shows wherever the others leave room.
                _effects[start + row * CellsPerChunk + column] = byte.MaxValue - total > best ? chunk.EffectIds[0] : effect;
            }
        }
    }

    /// <summary>The ground effect, a GroundEffectTexture row, most visible at a WoW position.</summary>
    public uint EffectAt(double wowX, double wowY)
    {
        var east = (WowCoords.MapOrigin - wowY - _tileX * WowCoords.TileSize) / WowCoords.ChunkSize;
        var south = (WowCoords.MapOrigin - wowX - _tileY * WowCoords.TileSize) / WowCoords.ChunkSize;
        var ix = Math.Clamp((int)Math.Floor(east), 0, AdtTerrain.ChunksPerSide - 1);
        var iy = Math.Clamp((int)Math.Floor(south), 0, AdtTerrain.ChunksPerSide - 1);
        var column = Math.Clamp((int)((east - ix) * CellsPerChunk), 0, CellsPerChunk - 1);
        var row = Math.Clamp((int)((south - iy) * CellsPerChunk), 0, CellsPerChunk - 1);
        return _effects[(iy * AdtTerrain.ChunksPerSide + ix) * CellsPerChunk * CellsPerChunk + row * CellsPerChunk + column];
    }

    /// <summary>The surface at a WoW position, drawn the way the mesh is: 4 triangles per cell. Holes are ignored.</summary>
    public float HeightAt(double wowX, double wowY)
    {
        var east = (WowCoords.MapOrigin - wowY - _tileX * WowCoords.TileSize) / WowCoords.ChunkSize;
        var south = (WowCoords.MapOrigin - wowX - _tileY * WowCoords.TileSize) / WowCoords.ChunkSize;
        var ix = Math.Clamp((int)Math.Floor(east), 0, AdtTerrain.ChunksPerSide - 1);
        var iy = Math.Clamp((int)Math.Floor(south), 0, AdtTerrain.ChunksPerSide - 1);

        var cellX = Math.Clamp((east - ix) * CellsPerChunk, 0, CellsPerChunk);
        var cellY = Math.Clamp((south - iy) * CellsPerChunk, 0, CellsPerChunk);
        var column = Math.Min((int)cellX, CellsPerChunk - 1);
        var row = Math.Min((int)cellY, CellsPerChunk - 1);
        var u = (float)(cellX - column);
        var v = (float)(cellY - row);

        var start = (iy * AdtTerrain.ChunksPerSide + ix) * TerrainChunk.VertexCount;
        var northWest = _heights[start + row * RowStride + column];
        var northEast = _heights[start + row * RowStride + column + 1];
        var southWest = _heights[start + (row + 1) * RowStride + column];
        var southEast = _heights[start + (row + 1) * RowStride + column + 1];
        var center = _heights[start + row * RowStride + InnerRowOffset + column];

        // Each triangle has the center and one edge. Across it, a point's weight on the
        // center grows from the edge, and the rest splits along the edge.
        float along, fromEdge, a, b;
        if (v <= u && v <= 1 - u)
        {
            (a, b, along, fromEdge) = (northWest, northEast, u, v);
        }
        else if (v >= u && v >= 1 - u)
        {
            (a, b, along, fromEdge) = (southWest, southEast, u, 1 - v);
        }
        else if (u < v)
        {
            (a, b, along, fromEdge) = (northWest, southWest, v, u);
        }
        else
        {
            (a, b, along, fromEdge) = (northEast, southEast, v, 1 - u);
        }

        var centerWeight = 2 * fromEdge;
        var bWeight = along - fromEdge;
        return center * centerWeight + b * bWeight + a * (1 - centerWeight - bWeight);
    }
}
