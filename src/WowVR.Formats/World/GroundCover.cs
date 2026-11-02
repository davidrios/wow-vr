using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;

namespace WowVR.Formats.World;

/// <summary>What a terrain layer scatters over the ground: a GroundEffectTexture row.</summary>
public sealed record GroundEffect(string[] Models, int[] Weights, int Amount);

/// <summary>Grass, flowers and small stones per ground effect, from GroundEffectTexture and GroundEffectDoodad.</summary>
public sealed class GroundEffectTable
{
    private const int DoodadSlots = 4;
    private const int FirstDoodadField = 1;
    private const int FirstWeightField = 5;
    private const int AmountField = 9;
    private const string Folder = @"World\NoDXT\Detail\";

    private readonly Dictionary<uint, GroundEffect> _effects = [];

    private GroundEffectTable()
    {
    }

    public static GroundEffectTable Read(IAssetSource source)
    {
        var table = new GroundEffectTable();
        var names = new Dictionary<int, string>();
        DbcTables.Read<object?>(source, @"DBFilesClient\GroundEffectDoodad.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                names[dbc.GetInt32(i, 0)] = Folder + M2Model.NormalizePath(dbc.GetString(i, 1));
            }

            return null;
        });
        DbcTables.Read<object?>(source, @"DBFilesClient\GroundEffectTexture.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var models = new List<string>();
                var weights = new List<int>();
                for (var slot = 0; slot < DoodadSlots; slot++)
                {
                    if (names.TryGetValue(dbc.GetInt32(i, FirstDoodadField + slot), out var name))
                    {
                        models.Add(name);
                        weights.Add(Math.Max(dbc.GetInt32(i, FirstWeightField + slot), 1));
                    }
                }

                if (models.Count > 0)
                {
                    table._effects[dbc.GetUInt32(i, 0)] = new GroundEffect(models.ToArray(), weights.ToArray(), dbc.GetInt32(i, AmountField));
                }
            }

            return null;
        });
        return table;
    }

    public GroundEffect? Find(uint effect) => _effects.GetValueOrDefault(effect);
}

/// <summary>One scattered model, in WoW coordinates.</summary>
public readonly record struct GroundCoverItem(string Model, double WowX, double WowY, float WowZ, float Yaw);

/// <summary>
/// Scatters a chunk's ground cover. The same chunk always gets the same result, so it can be
/// dropped and remade. The amount as a count per cell, and the random placement, are guesses:
/// no reference says how the client uses them.
/// </summary>
public static class GroundCover
{
    /// <summary>A few rows ask for over 100 per cell, which would be thousands in one chunk.</summary>
    private const int MaxPerCell = 16;

    public static List<GroundCoverItem> PlaceChunk(TerrainHeights heights, GroundEffectTable table, int tileX, int tileY, int ix, int iy)
    {
        var items = new List<GroundCoverItem>();
        var cellSize = WowCoords.ChunkSize / TerrainHeights.CellsPerChunk;
        var north = WowCoords.MapOrigin - tileY * WowCoords.TileSize - iy * WowCoords.ChunkSize;
        var west = WowCoords.MapOrigin - tileX * WowCoords.TileSize - ix * WowCoords.ChunkSize;
        for (var row = 0; row < TerrainHeights.CellsPerChunk; row++)
        {
            for (var column = 0; column < TerrainHeights.CellsPerChunk; column++)
            {
                if (heights.CoverBlocked(ix, iy, row, column) || table.Find(heights.EffectOfCell(ix, iy, row, column)) is not { } effect)
                {
                    continue;
                }

                var random = new Random(StableSeed(tileX, tileY, ix, iy, row, column));
                var total = effect.Weights.Sum();
                var count = Math.Min(effect.Amount, MaxPerCell);
                for (var i = 0; i < count; i++)
                {
                    var wowX = north - (row + random.NextDouble()) * cellSize;
                    var wowY = west - (column + random.NextDouble()) * cellSize;
                    var choice = random.Next(total);
                    var slot = 0;
                    while (choice >= effect.Weights[slot])
                    {
                        choice -= effect.Weights[slot++];
                    }

                    var wowZ = heights.HeightAt(wowX, wowY);
                    var yaw = (float)(random.NextDouble() * Math.Tau);
                    if (!heights.UnderLiquid(wowX, wowY, wowZ))
                    {
                        items.Add(new GroundCoverItem(effect.Models[slot], wowX, wowY, wowZ, yaw));
                    }
                }
            }
        }

        return items;
    }

    /// <summary>Not <see cref="HashCode"/>, which differs each run. M7's players should see the same grass.</summary>
    private static int StableSeed(int tileX, int tileY, int ix, int iy, int row, int column)
    {
        var seed = 17;
        foreach (var part in (ReadOnlySpan<int>)[tileX, tileY, ix, iy, row, column])
        {
            seed = unchecked(seed * 31 + part);
        }

        return seed;
    }
}
