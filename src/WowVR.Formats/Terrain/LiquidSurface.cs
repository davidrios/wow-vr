using System.Numerics;

namespace WowVR.Formats.Terrain;

/// <summary>One layer of liquid in one map chunk, on the chunk's 9x9 grid.</summary>
public sealed class LiquidLayer
{
    public const int CellsPerSide = 8;
    public const int VerticesPerSide = CellsPerSide + 1;
    public const int VertexCount = VerticesPerSide * VerticesPerSide;

    public required int ChunkIx { get; init; }
    public required int ChunkIy { get; init; }

    /// <summary>The row in LiquidType.dbc.</summary>
    public required int LiquidType { get; init; }

    /// <summary>Bit <c>row * 8 + column</c> is set when that cell has liquid.</summary>
    public required ulong Cells { get; init; }

    /// <summary>81 world heights. Columns run east and rows run south.</summary>
    public required float[] Heights { get; init; }

    /// <summary>81 depths from 0 to 255.</summary>
    public required byte[] Depths { get; init; }

    public bool HasCell(int column, int row) => (Cells >> (row * CellsPerSide + column) & 1) != 0;
}

/// <summary>All of one kind of liquid in a tile or WMO group, ready for a mesh in Godot axes.</summary>
public sealed class LiquidSurface
{
    /// <summary>A liquid cell's size in yards, the same for tiles and WMOs.</summary>
    public const float CellSize = (float)(WowCoords.ChunkSize / LiquidLayer.CellsPerSide);

    /// <summary>The liquid's texture repeats once per this many cells, as in Noggit.</summary>
    private const int CellsPerTextureRepeat = 4;

    /// <summary>WMO liquids have no depth data, so this makes them read as fairly deep.</summary>
    private const float WmoDepth = 0.75f;

    public required int LiquidType { get; init; }
    public required Vector3[] Positions { get; init; }

    /// <summary>Texture coordinates, continuous across a tile and whole numbers at its edges.</summary>
    public required Vector2[] TexCoords { get; init; }

    /// <summary>How deep each vertex looks, from 0 to 1.</summary>
    public required float[] Depths { get; init; }

    public required int[] Indices { get; init; }

    /// <summary>A tile's liquid relative to its north-west corner, like its terrain, one surface per liquid type.</summary>
    public static List<LiquidSurface> FromTile(IEnumerable<LiquidLayer> layers)
    {
        var surfaces = new List<LiquidSurface>();
        foreach (var group in layers.GroupBy(l => l.LiquidType))
        {
            var builder = new Builder();
            foreach (var layer in group)
            {
                var chunkEast = (float)(layer.ChunkIx * WowCoords.ChunkSize);
                var chunkSouth = (float)(layer.ChunkIy * WowCoords.ChunkSize);
                var firstColumn = layer.ChunkIx * LiquidLayer.CellsPerSide;
                var firstRow = layer.ChunkIy * LiquidLayer.CellsPerSide;

                for (var row = 0; row < LiquidLayer.CellsPerSide; row++)
                {
                    for (var column = 0; column < LiquidLayer.CellsPerSide; column++)
                    {
                        if (!layer.HasCell(column, row))
                        {
                            continue;
                        }

                        builder.AddCell(column + firstColumn, row + firstRow, (cellColumn, cellRow) =>
                        {
                            var localColumn = cellColumn - firstColumn;
                            var localRow = cellRow - firstRow;
                            var vertex = localRow * LiquidLayer.VerticesPerSide + localColumn;
                            var position = WowCoords.ToGodot(new Vector3(
                                -(chunkSouth + localRow * CellSize),
                                -(chunkEast + localColumn * CellSize),
                                layer.Heights[vertex]));
                            return (position, layer.Depths[vertex] / 255f);
                        });
                    }
                }
            }

            surfaces.Add(builder.Finish(group.Key));
        }

        return surfaces;
    }

    /// <summary>A WMO group's liquid in the WMO's own space, once in Godot axes.</summary>
    public static LiquidSurface FromGrid(int liquidType, int verticesX, int verticesY, Vector3 corner, ReadOnlySpan<float> heights, ReadOnlySpan<byte> cells)
    {
        var builder = new Builder();
        var heightArray = heights.ToArray();
        var cellsX = verticesX - 1;
        for (var row = 0; row < verticesY - 1; row++)
        {
            for (var column = 0; column < cellsX; column++)
            {
                if (cells[row * cellsX + column] == 0)
                {
                    continue;
                }

                builder.AddCell(column, row, (cellColumn, cellRow) =>
                {
                    var position = WowCoords.ModelToGodot(new Vector3(
                        corner.X + cellColumn * CellSize,
                        corner.Y + cellRow * CellSize,
                        heightArray[cellRow * verticesX + cellColumn]));
                    return (position, WmoDepth);
                });
            }
        }

        return builder.Finish(liquidType);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> _positions = [];
        private readonly List<Vector2> _texCoords = [];
        private readonly List<float> _depths = [];
        private readonly List<int> _indices = [];

        public void AddCell(int column, int row, Func<int, int, (Vector3 Position, float Depth)> vertexAt)
        {
            var first = _positions.Count;
            foreach (var (cornerColumn, cornerRow) in new[] { (column, row), (column + 1, row), (column, row + 1), (column + 1, row + 1) })
            {
                var (position, depth) = vertexAt(cornerColumn, cornerRow);
                _positions.Add(position);
                _depths.Add(depth);
                _texCoords.Add(new Vector2(cornerColumn, cornerRow) / CellsPerTextureRepeat);
            }

            _indices.AddRange([first, first + 1, first + 2, first + 2, first + 1, first + 3]);
        }

        public LiquidSurface Finish(int liquidType) => new()
        {
            LiquidType = liquidType,
            Positions = _positions.ToArray(),
            TexCoords = _texCoords.ToArray(),
            Depths = _depths.ToArray(),
            Indices = _indices.ToArray(),
        };
    }
}
