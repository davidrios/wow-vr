using System.Numerics;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;
using Xunit;

namespace WowVR.Formats.Tests;

public class WowCoordsTests
{
    [Fact]
    public void ConvertsAxesBothWays()
    {
        var wow = new Vector3(10, 20, 30);

        Assert.Equal(new Vector3(-20, 30, -10), WowCoords.ToGodot(wow));
        Assert.Equal(wow, WowCoords.ToWow(WowCoords.ToGodot(wow)));
    }

    [Theory]
    [InlineData(-9464, 62, 31, 49)] // Goldshire.
    [InlineData(0, 0, 32, 32)]
    [InlineData(17066, 17066, 0, 0)]
    [InlineData(-17066, -17066, 63, 63)]
    [InlineData(99999, -99999, 63, 0)]
    public void FindsTileAtWorldPosition(double wowX, double wowY, int tileX, int tileY)
    {
        Assert.Equal((tileX, tileY), WowCoords.TileAt(wowX, wowY));
    }

    [Fact]
    public void TileOriginIsItsNorthWestCorner()
    {
        var (x, z) = WowCoords.TileOriginGodot(31, 49);
        var wow = WowCoords.ToWow(new Vector3((float)x + 1, 0, (float)z + 1));

        Assert.Equal((31, 49), WowCoords.TileAt(wow.X, wow.Y));
    }
}

public class TerrainMeshDataTests
{
    [Theory]
    [InlineData(0x0001, 0, 0, true)]
    [InlineData(0x0001, 1, 1, true)]
    [InlineData(0x0001, 2, 0, false)]
    [InlineData(0x0002, 2, 1, true)]
    [InlineData(0x0010, 0, 2, true)]
    [InlineData(0x8000, 7, 7, true)]
    [InlineData(0x8000, 5, 7, false)]
    public void HoleBitsCoverTwoByTwoCells(int holes, int x, int y, bool expected)
    {
        Assert.Equal(expected, TerrainMeshData.IsHole((ushort)holes, x, y));
    }
}

public class ClientTerrainTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    [Fact]
    public void TerrainTexturesConvertToBc1Chains()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, "Northrend");
        var terrain = AdtTerrain.Parse(client.Source.Read(map.TilePath(30, 21))!, map.BigAlpha);
        var mesh = TerrainMeshData.Build(terrain);

        Assert.InRange(mesh.Textures.Count, 1, terrain.TextureNames.Count);
        Assert.DoesNotContain(mesh.Textures, name => name.Contains("Cube Maps"));
        Assert.All(mesh.TextureIndexes, index => Assert.InRange(index, 0, mesh.Textures.Count - 1));

        var data = client.Source.Read(mesh.Textures[0])!;
        var (width, height) = BlpDecoder.ReadSize(data);
        Assert.Equal(width, height);

        var chain = BlpDecoder.ConvertToBc1(data, 16);

        // 16, 8, 4, 2 and 1 pixels are 16, 4, 1, 1 and 1 blocks of 8 bytes.
        Assert.Equal(23 * 8, chain.Length);
    }

    /// <summary>Northrend's alpha maps are weights: the order of a chunk's layers doesn't matter.</summary>
    [Fact]
    public void BigAlphaMapsAreWeightsThatMatchAcrossChunkEdges()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, "Northrend");
        var terrain = AdtTerrain.Parse(client.Source.Read(map.TilePath(30, 20))!, map.BigAlpha);

        var worstSum = 0;
        foreach (var chunk in terrain.Chunks)
        {
            for (var pixel = 0; pixel < 4096; pixel++)
            {
                worstSum = Math.Max(
                    worstSum,
                    chunk.Alpha[pixel] + chunk.Alpha[4096 + pixel] + chunk.Alpha[8192 + pixel]);
            }
        }

        Assert.InRange(worstSum, 200, 280);

        var left = terrain.Chunks[11 * 16 + 11];
        var right = terrain.Chunks[11 * 16 + 12];
        Assert.NotEqual(left.TextureIds[1], right.TextureIds[1]);

        for (var layer = 1; layer < left.LayerCount; layer++)
        {
            var match = Array.FindIndex(right.TextureIds, 1, id => id == left.TextureIds[layer]);
            Assert.True(match > 0);

            var difference = 0;
            for (var row = 0; row < 64; row++)
            {
                var a = left.Alpha[(layer - 1) * 4096 + row * 64 + 63];
                var b = right.Alpha[(match - 1) * 4096 + row * 64];
                difference += Math.Abs(a - b);
            }

            // The two sides are neighbouring pixels, so single rows can differ a little.
            Assert.InRange(difference / 64, 0, 16);
        }
    }

    [Fact]
    public void ReflectionLayersDrawASurfaceTexture()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, "Northrend");
        var terrain = AdtTerrain.Parse(client.Source.Read(map.TilePath(30, 21))!, map.BigAlpha);

        var chunk = terrain.Chunks[11 * 16 + 7];
        Assert.Contains("Cube Maps", terrain.TextureNames[chunk.TextureIds[0]]);
        Assert.Equal(chunk.TextureIds[1], TerrainMeshData.SurfaceTexture(chunk, 0));

        var neighbour = terrain.Chunks[11 * 16 + 8];
        Assert.Equal(neighbour.TextureIds[0], TerrainMeshData.SurfaceTexture(chunk, 0));

        Assert.All(terrain.Chunks, c =>
        {
            for (var layer = 0; layer < c.LayerCount; layer++)
            {
                Assert.DoesNotContain("Cube Maps", terrain.TextureNames[TerrainMeshData.SurfaceTexture(c, layer)]);
            }
        });
    }

    /// <summary>Northrend's big alpha tiles only parse with the MCNK header's MCAL size.</summary>
    [Theory]
    [InlineData("Azeroth", false)]
    [InlineData("Kalimdor", false)]
    [InlineData("Expansion01", false)]
    [InlineData("Northrend", true)]
    public void EveryTileOfEveryContinentParses(string directory, bool bigAlpha)
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, directory);
        Assert.Equal(bigAlpha, map.BigAlpha);

        for (var y = 0; y < WowCoords.TilesPerSide; y++)
        {
            for (var x = 0; x < WowCoords.TilesPerSide; x++)
            {
                if (map.HasTile(x, y))
                {
                    var data = client.Source.Read(map.TilePath(x, y));
                    Assert.NotNull(data);
                    Assert.Equal(256, AdtTerrain.Parse(data, map.BigAlpha).Chunks.Count);
                }
            }
        }
    }

    private const int GoldshireX = 31;
    private const int GoldshireY = 49;

    private (WdtMap Map, AdtTerrain Terrain) LoadGoldshire()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, "Azeroth");
        var data = client.Source.Read(map.TilePath(GoldshireX, GoldshireY));
        Assert.NotNull(data);
        return (map, AdtTerrain.Parse(data, map.BigAlpha));
    }

    [Fact]
    public void AzerothHasGoldshireButNotTheOcean()
    {
        var (map, _) = LoadGoldshire();

        Assert.True(map.HasTile(GoldshireX, GoldshireY));
        Assert.False(map.HasTile(0, 0));
        Assert.InRange(map.TileCountWithData, 100, 4096);
    }

    [Fact]
    public void ChunkPositionsMatchTheirTileIndexes()
    {
        var (_, terrain) = LoadGoldshire();

        Assert.Equal(256, terrain.Chunks.Count);
        foreach (var chunk in terrain.Chunks)
        {
            var north = WowCoords.MapOrigin - (GoldshireY * WowCoords.TileSize + chunk.Iy * WowCoords.ChunkSize);
            var west = WowCoords.MapOrigin - (GoldshireX * WowCoords.TileSize + chunk.Ix * WowCoords.ChunkSize);

            Assert.Equal(north, chunk.WorldPosition.X, 0.01);
            Assert.Equal(west, chunk.WorldPosition.Y, 0.01);
        }
    }

    [Fact]
    public void GoldshireIsAtItsKnownHeight()
    {
        var (_, terrain) = LoadGoldshire();

        // Goldshire's inn stands at about Z 57, near (-9460, 45).
        var (ix, iy) = (
            (int)((WowCoords.MapOrigin - 45 - GoldshireX * WowCoords.TileSize) / WowCoords.ChunkSize),
            (int)((WowCoords.MapOrigin + 9460 - GoldshireY * WowCoords.TileSize) / WowCoords.ChunkSize));
        var chunk = terrain.Chunks[iy * 16 + ix];

        Assert.Equal((ix, iy), (chunk.Ix, chunk.Iy));
        Assert.InRange(chunk.WorldPosition.Z + chunk.Heights[0], 40f, 75f);
    }

    [Fact]
    public void TexturesAndAlphaMapsAreUsable()
    {
        var (map, terrain) = LoadGoldshire();

        Assert.False(map.BigAlpha);
        Assert.Contains(terrain.TextureNames, n => n.Contains("Elwynn", StringComparison.OrdinalIgnoreCase));
        Assert.All(terrain.Chunks, chunk =>
        {
            Assert.InRange(chunk.LayerCount, 1, 4);
            Assert.All(chunk.TextureIds[..chunk.LayerCount], id => Assert.InRange(id, 0, terrain.TextureNames.Count - 1));
        });

        // Layers blend, so maps shouldn't all be fully off or fully on.
        var withLayers = terrain.Chunks.Where(c => c.LayerCount > 1).ToList();
        Assert.NotEmpty(withLayers);
        Assert.Contains(withLayers, c => c.Alpha.Take(4096).Any(a => a is > 0 and < 255));
    }

    [Fact]
    public void NormalsAgreeWithTheSlopeOfTheHeights()
    {
        var (_, terrain) = LoadGoldshire();
        var mesh = TerrainMeshData.Build(terrain);

        var total = 0f;
        var count = 0;
        for (var triangle = 0; triangle < mesh.Indices.Length; triangle += 3)
        {
            var (a, b, c) = (mesh.Indices[triangle], mesh.Indices[triangle + 1], mesh.Indices[triangle + 2]);
            var edge1 = mesh.Positions[b] - mesh.Positions[a];
            var edge2 = mesh.Positions[c] - mesh.Positions[a];

            // Clockwise from above: this cross product points up for Godot front faces.
            var face = Vector3.Normalize(Vector3.Cross(edge2, edge1));
            Assert.True(face.Y > 0, "Triangles must face up.");

            // Compare only sloped ground, because flat ground can't tell the axes apart.
            if (face.Y < 0.97f)
            {
                total += Vector3.Dot(face, mesh.Normals[a]);
                count++;
            }
        }

        Assert.True(count > 1000, $"Only {count} sloped triangles.");
        Assert.InRange(total / count, 0.9f, 1f);
    }

    [Fact]
    public void MeshHasEveryVertexWithinTheTile()
    {
        var (_, terrain) = LoadGoldshire();
        var mesh = TerrainMeshData.Build(terrain);

        Assert.Equal(256 * 145, mesh.Positions.Length);
        Assert.All(mesh.Indices, i => Assert.InRange(i, 0, mesh.Positions.Length - 1));
        Assert.Equal(TerrainMeshData.AtlasSize * TerrainMeshData.AtlasSize * 3, mesh.AlphaAtlas.Length);

        var extent = (float)WowCoords.TileSize;
        Assert.All(mesh.Positions, p =>
        {
            Assert.InRange(p.X, -0.01f, extent + 0.01f);
            Assert.InRange(p.Z, -0.01f, extent + 0.01f);
        });
    }
}
