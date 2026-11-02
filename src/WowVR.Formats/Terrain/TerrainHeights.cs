namespace WowVR.Formats.Terrain;

/// <summary>A tile's terrain height anywhere on it, without physics, for things far from the player.</summary>
public sealed class TerrainHeights
{
    public const int CellsPerChunk = 8;
    private const int CellsPerHole = 2;
    private const int HolesPerSide = 4;
    private const int RowStride = 17;
    private const int InnerRowOffset = 9;

    private readonly int _tileX;
    private readonly int _tileY;

    /// <summary>145 absolute heights per chunk.</summary>
    private readonly float[] _heights = new float[AdtTerrain.ChunksPerSide * AdtTerrain.ChunksPerSide * TerrainChunk.VertexCount];

    /// <summary>The ground effect showing most in each cell of each chunk.</summary>
    private readonly uint[] _effects = new uint[AdtTerrain.ChunksPerSide * AdtTerrain.ChunksPerSide * CellsPerChunk * CellsPerChunk];

    /// <summary>Per chunk, the cells that get no ground cover: as the map says, or with a hole.</summary>
    private readonly ulong[] _noCover = new ulong[AdtTerrain.ChunksPerSide * AdtTerrain.ChunksPerSide];

    /// <summary>The liquid layers of the chunks that have any.</summary>
    private readonly Dictionary<int, List<LiquidLayer>> _liquids = [];

    public TerrainHeights(AdtTerrain terrain, int tileX, int tileY)
    {
        _tileX = tileX;
        _tileY = tileY;
        foreach (var layer in terrain.Liquids)
        {
            var index = layer.ChunkIy * AdtTerrain.ChunksPerSide + layer.ChunkIx;
            if (!_liquids.TryGetValue(index, out var layers))
            {
                _liquids[index] = layers = [];
            }

            layers.Add(layer);
        }

        foreach (var chunk in terrain.Chunks)
        {
            FillEffects(chunk);
            _noCover[chunk.Iy * AdtTerrain.ChunksPerSide + chunk.Ix] = chunk.NoEffectDoodad | HoleCells(chunk.Holes);
            var start = (chunk.Iy * AdtTerrain.ChunksPerSide + chunk.Ix) * TerrainChunk.VertexCount;
            for (var i = 0; i < TerrainChunk.VertexCount; i++)
            {
                _heights[start + i] = chunk.WorldPosition.Z + chunk.Heights[i];
            }
        }
    }

    /// <summary>The highest liquid over a WoW position, as its height and LiquidType row. Null where there's none.</summary>
    public (float Surface, int Type)? LiquidAt(double wowX, double wowY)
    {
        var east = (WowCoords.MapOrigin - wowY - _tileX * WowCoords.TileSize) / WowCoords.ChunkSize;
        var south = (WowCoords.MapOrigin - wowX - _tileY * WowCoords.TileSize) / WowCoords.ChunkSize;
        var ix = Math.Clamp((int)Math.Floor(east), 0, AdtTerrain.ChunksPerSide - 1);
        var iy = Math.Clamp((int)Math.Floor(south), 0, AdtTerrain.ChunksPerSide - 1);
        if (!_liquids.TryGetValue(iy * AdtTerrain.ChunksPerSide + ix, out var layers))
        {
            return null;
        }

        var cellX = Math.Clamp((east - ix) * LiquidLayer.CellsPerSide, 0, LiquidLayer.CellsPerSide);
        var cellY = Math.Clamp((south - iy) * LiquidLayer.CellsPerSide, 0, LiquidLayer.CellsPerSide);
        var column = Math.Min((int)cellX, LiquidLayer.CellsPerSide - 1);
        var row = Math.Min((int)cellY, LiquidLayer.CellsPerSide - 1);
        var u = (float)(cellX - column);
        var v = (float)(cellY - row);
        (float Surface, int Type)? best = null;
        foreach (var layer in layers)
        {
            if (!layer.HasCell(column, row))
            {
                continue;
            }

            var top = row * LiquidLayer.VerticesPerSide + column;
            var bottom = top + LiquidLayer.VerticesPerSide;
            var surface = float.Lerp(
                float.Lerp(layer.Heights[top], layer.Heights[top + 1], u),
                float.Lerp(layer.Heights[bottom], layer.Heights[bottom + 1], u),
                v);
            if (best is null || surface > best.Value.Surface)
            {
                best = (surface, layer.LiquidType);
            }
        }

        return best;
    }

    /// <summary>Whether a point at a WoW position and height is below the surface of the tile's liquid.</summary>
    public bool UnderLiquid(double wowX, double wowY, float wowZ) => LiquidAt(wowX, wowY) is { } liquid && wowZ < liquid.Surface;

    private static ulong HoleCells(ushort holes)
    {
        var cells = 0UL;
        for (var row = 0; row < CellsPerChunk; row++)
        {
            for (var column = 0; column < CellsPerChunk; column++)
            {
                if ((holes & (1 << (row / CellsPerHole * HolesPerSide + column / CellsPerHole))) != 0)
                {
                    cells |= 1UL << (row * CellsPerChunk + column);
                }
            }
        }

        return cells;
    }

    /// <summary>Whether a cell of a chunk should stay clear of ground cover.</summary>
    public bool CoverBlocked(int ix, int iy, int row, int column) =>
        (_noCover[iy * AdtTerrain.ChunksPerSide + ix] & (1UL << (row * CellsPerChunk + column))) != 0;

    /// <summary>The ground effect showing most in a cell of a chunk.</summary>
    public uint EffectOfCell(int ix, int iy, int row, int column) =>
        _effects[(iy * AdtTerrain.ChunksPerSide + ix) * CellsPerChunk * CellsPerChunk + row * CellsPerChunk + column];

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
