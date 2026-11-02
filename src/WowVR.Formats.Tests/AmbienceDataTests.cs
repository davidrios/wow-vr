using System.Numerics;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

/// <summary>Checks liquids, sky data and zone sounds against the client's own files.</summary>
public class AmbienceDataTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private const int Azeroth = 0;

    private static readonly (float X, float Y) CrystalLake = (-9409f, -328f);
    private static readonly (float X, float Y) Goldshire = (-9464f, 62f);

    private MpqAssetSource Source
    {
        get
        {
            if (client.Source is null)
            {
                Assert.Skip("WOWVR_CLIENT_PATH is not set.");
            }

            return client.Source;
        }
    }

    private AdtTerrain ReadTile(string map, float wowX, float wowY)
    {
        var wdt = WdtMap.Read(Source, map);
        var tile = WowCoords.TileAt(wowX, wowY);
        return AdtTerrain.Parse(Source.Read(wdt.TilePath(tile.X, tile.Y))!, wdt.BigAlpha);
    }

    /// <summary>The terrain height at a liquid vertex, reading the grid as its columns and rows or swapped.</summary>
    private static float TerrainUnder(AdtTerrain terrain, LiquidLayer layer, int column, int row, bool swapped)
    {
        var chunk = terrain.Chunks[swapped
            ? layer.ChunkIx * AdtTerrain.ChunksPerSide + layer.ChunkIy
            : layer.ChunkIy * AdtTerrain.ChunksPerSide + layer.ChunkIx];
        var (x, y) = swapped ? (row, column) : (column, row);

        // Outer vertices sit in rows of 9 with rows of 8 between them.
        return chunk.WorldPosition.Z + chunk.Heights[y * 17 + x];
    }

    [Fact]
    public void CrystalLakeWaterLiesAboveItsLakebedOnlyWithColumnsEastAndRowsSouth()
    {
        var terrain = ReadTile("Azeroth", CrystalLake.X, CrystalLake.Y);
        Assert.NotEmpty(terrain.Liquids);

        double correct = 0, swapped = 0, total = 0;
        foreach (var layer in terrain.Liquids)
        {
            for (var row = 0; row < LiquidLayer.VerticesPerSide; row++)
            {
                for (var column = 0; column < LiquidLayer.VerticesPerSide; column++)
                {
                    // A vertex touches liquid if any of its cells has it, and only cells in the middle count.
                    if (row == 8 || column == 8 || !layer.HasCell(column, row) || layer.Depths[row * 9 + column] < 64)
                    {
                        continue;
                    }

                    var surface = layer.Heights[row * 9 + column];
                    total++;
                    correct += TerrainUnder(terrain, layer, column, row, swapped: false) < surface ? 1 : 0;
                    swapped += TerrainUnder(terrain, layer, column, row, swapped: true) < surface ? 1 : 0;
                }
            }
        }

        Assert.True(total > 100, $"Only {total} deep vertices.");
        Assert.True(correct / total > 0.95, $"Correct order: {correct / total:P1}");
        Assert.True(correct / total > swapped / total + 0.1, $"Correct {correct / total:P1}, swapped {swapped / total:P1}");
    }

    [Fact]
    public void LiquidsNameRowsOfLiquidTypeOnAllContinents()
    {
        var types = LiquidTypeRecord.ReadAll(Source).ToDictionary(t => t.Id);
        var layers = 0;

        foreach (var map in new[] { "Azeroth", "Kalimdor", "Expansion01", "Northrend" })
        {
            var wdt = WdtMap.Read(Source, map);
            for (var y = 0; y < WowCoords.TilesPerSide; y++)
            {
                for (var x = 0; x < WowCoords.TilesPerSide; x++)
                {
                    if (!wdt.HasTile(x, y))
                    {
                        continue;
                    }

                    var terrain = AdtTerrain.Parse(Source.Read(wdt.TilePath(x, y))!, wdt.BigAlpha);
                    foreach (var layer in terrain.Liquids)
                    {
                        layers++;
                        Assert.True(types.ContainsKey(layer.LiquidType), $"{map} {x}_{y}: liquid type {layer.LiquidType}");
                        Assert.All(layer.Heights, h => Assert.True(float.IsFinite(h) && Math.Abs(h) < 20000, $"{map} {x}_{y}: height {h}"));
                    }
                }
            }
        }

        Assert.True(layers > 100000, $"Only {layers} layers.");
    }

    [Fact]
    public void LiquidTexturesExistForEveryLiquidType()
    {
        foreach (var type in LiquidTypeRecord.ReadAll(Source))
        {
            var first = type.TexturePattern.Replace("%d", "1");
            Assert.True(Source.Read(first) is { Length: > 0 }, $"{type.Name}: {first}");
        }
    }

    [Fact]
    public void WmoLiquidsCoverTheirGroupsAndNameRowsOfLiquidType()
    {
        var types = LiquidTypeRecord.ReadAll(Source).ToDictionary(t => t.Id);

        // Stormwind's canals run through its WMOs.
        var path = @"World\wmo\Azeroth\Buildings\Stormwind\Stormwind.wmo";
        var data = Source.Read(path);
        Assert.NotNull(data);
        var root = WmoRoot.Parse(data);

        var liquids = 0;
        for (var i = 0; i < root.GroupCount; i++)
        {
            var group = WmoGroup.Parse(Source.Read(WmoRoot.GroupPath(path, i))!, root);
            if (group.Liquid is not { } liquid)
            {
                continue;
            }

            liquids++;
            Assert.True(types.ContainsKey(liquid.LiquidType), $"Group {i}: liquid type {liquid.LiquidType}");

            // In Godot axes the root's bounds run X, Z, -Y. Heights must lie within them.
            var (min, max) = root.Bounds;
            Assert.All(liquid.Positions, p => Assert.InRange(p.Y, min.Z - 1, max.Z + 1));
        }

        Assert.True(liquids > 0, "Stormwind has no liquid groups.");
    }

    private static Vector3 MapPosition(float wowX, float wowY, float wowZ) => new(
        (float)WowCoords.MapOrigin - wowY,
        wowZ,
        (float)WowCoords.MapOrigin - wowX);

    private SkyState SkyAt(float wowX, float wowY, float hour)
    {
        var state = new SkyState();
        LightTable.Read(Source).Sample(Azeroth, MapPosition(wowX, wowY, 60), hour * 120f, state);
        return state;
    }

    [Fact]
    public void GoldshireIsBrightAtNoonAndDarkAtMidnight()
    {
        var noon = SkyAt(Goldshire.X, Goldshire.Y, 12);
        var midnight = SkyAt(Goldshire.X, Goldshire.Y, 0);

        // Diffuse stays strong all night, as blue moonlight, so ambient and the sky show the change.
        Assert.True(noon[SkyBand.Ambient].Length() > midnight[SkyBand.Ambient].Length() * 1.5f);
        Assert.True(noon[SkyBand.SkyTop].Length() > midnight[SkyBand.SkyTop].Length() * 1.5f);

        // A clear day's sky is blue at the top.
        Assert.True(noon[SkyBand.SkyTop].Z > noon[SkyBand.SkyTop].X);
    }

    [Fact]
    public void FogEndsAtAPlausibleDistanceInElwynn()
    {
        var noon = SkyAt(Goldshire.X, Goldshire.Y, 12);
        Assert.InRange(noon.FogEnd, 200f, 3000f);
        Assert.InRange(noon.FogStart, 0f, 1f);
    }

    [Fact]
    public void WaterIsSeeThroughInElwynn()
    {
        var noon = SkyAt(CrystalLake.X, CrystalLake.Y, 12);
        Assert.InRange(noon.RiverShallowAlpha, 0.01f, 1f);
        Assert.InRange(noon.RiverDeepAlpha, noon.RiverShallowAlpha, 1f);
    }

    [Fact]
    public void DuskwoodHasCloserFogThanElwynn()
    {
        var elwynn = SkyAt(Goldshire.X, Goldshire.Y, 12);
        var duskwood = SkyAt(-10634f, -1192f, 12);
        Assert.True(duskwood.FogEnd < elwynn.FogEnd / 2f, $"Duskwood {duskwood.FogEnd}, Elwynn {elwynn.FogEnd}");
    }

    [Fact]
    public void TimeCurvesWrapSmoothlyAroundMidnight()
    {
        var table = LightTable.Read(Source);
        var (before, after) = (new SkyState(), new SkyState());
        var position = MapPosition(Goldshire.X, Goldshire.Y, 60);
        table.Sample(Azeroth, position, LightTable.TicksPerDay - 0.5f, before);
        table.Sample(Azeroth, position, 0.5f, after);

        for (var band = 0; band < LightTable.BandsPerParams; band++)
        {
            Assert.True(Vector3.Distance(before.Colors[band], after.Colors[band]) < 0.02f, $"Band {band} jumps.");
        }
    }

    [Fact]
    public void SunRisesInTheEastAndSetsInTheWest()
    {
        Assert.InRange(SunPath.Direction(SunPath.SunriseTicks).Y, -0.01f, 0.01f);
        Assert.True(SunPath.Direction(SunPath.SunriseTicks).X > 0.9f);
        Assert.True(SunPath.Direction((SunPath.SunriseTicks + SunPath.SunsetTicks) / 2f).Y > 0.9f);
        Assert.InRange(SunPath.Direction(SunPath.SunsetTicks).Y, -0.01f, 0.01f);
        Assert.True(SunPath.Direction(SunPath.SunsetTicks).X < -0.9f);
        Assert.True(SunPath.Direction(0).Y < 0f);
    }

    [Fact]
    public void CrystalLakeUsesElwynnSoundsThatExistInTheClient()
    {
        var terrain = ReadTile("Azeroth", CrystalLake.X, CrystalLake.Y);
        var tile = WowCoords.TileAt(CrystalLake.X, CrystalLake.Y);
        var chunkX = (int)((WowCoords.MapOrigin - CrystalLake.Y - tile.X * WowCoords.TileSize) / WowCoords.ChunkSize);
        var chunkY = (int)((WowCoords.MapOrigin - CrystalLake.X - tile.Y * WowCoords.TileSize) / WowCoords.ChunkSize);
        var chunk = terrain.Chunks[chunkY * AdtTerrain.ChunksPerSide + chunkX];

        var zone = ZoneTable.Read(Source).Find(chunk.AreaId);

        Assert.NotNull(zone);
        Assert.Equal("Crystal Lake", zone.Name);
        Assert.NotNull(zone.Music?.Day);
        Assert.NotNull(zone.AmbienceDay);
        foreach (var (path, _) in zone.Music!.Day!.Files.Concat(zone.Music.Night!.Files).Concat(zone.AmbienceDay!.Files))
        {
            Assert.True(Source.Read(path) is { Length: > 0 }, $"Missing {path}");
        }
    }

    [Fact]
    public void SubzonesSharingMusicGiveTheSameChoice()
    {
        // Crystal Lake takes its music from Elwynn Forest, so walking between them mustn't restart it.
        var zones = ZoneTable.Read(Source);
        var elwynn = zones.Find(12)!;
        var lake = zones.Find(18)!;

        Assert.NotNull(elwynn.Music?.Day);
        Assert.Same(elwynn.Music.Day, lake.Music?.Day);
    }

    [Fact]
    public void ZoneSoundPickFollowsWeights()
    {
        var choice = new SoundChoice([("a", 1), ("b", 3)], 1f);
        Assert.Equal("a", choice.Pick(0.1));
        Assert.Equal("b", choice.Pick(0.5));
        Assert.Equal("b", choice.Pick(0.999));
    }
}
