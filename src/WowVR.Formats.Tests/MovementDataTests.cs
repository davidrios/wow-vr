using System.Numerics;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

public class ChunkLookupTests
{
    [Fact]
    public void ChunkDividesIntoItsTile()
    {
        var godot = WowCoords.ToGodot(new Vector3(-9464, 62, 0));
        var chunk = WowCoords.ChunkAtGodot(godot.X, godot.Z);

        Assert.Equal(WowCoords.TileAt(-9464, 62), (chunk.X / WowCoords.ChunksPerTile, chunk.Z / WowCoords.ChunksPerTile));
    }

    [Fact]
    public void NorthWestCornerIsTheFirstChunk()
    {
        var (x, z) = WowCoords.TileOriginGodot(0, 0);

        Assert.Equal((0, 0), WowCoords.ChunkAtGodot(x + 1, z + 1));
        Assert.Equal((-1, -1), WowCoords.ChunkAtGodot(x - 1, z - 1));
    }
}

public class ClientMovementTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    [Fact]
    public void CollisionMatchesTheMeshAndKeepsHolesOpen()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var map = WdtMap.Read(client.Source, "Azeroth");
        var terrain = AdtTerrain.Parse(client.Source.Read(map.TilePath(31, 49))!, map.BigAlpha);
        var mesh = TerrainMeshData.Build(terrain);

        var total = 0;
        foreach (var chunk in terrain.Chunks)
        {
            var faces = mesh.Collision.ChunkFaces(chunk.Ix, chunk.Iy);
            var holes = Enumerable.Range(0, 64).Count(c => TerrainMeshData.IsHole(chunk.Holes, c % 8, c / 8));

            Assert.Equal((64 - holes) * 12, faces.Length);
            total += faces.Length;

            // Every corner lies inside its own chunk.
            var west = chunk.Ix * WowCoords.ChunkSize;
            Assert.All(faces, f => Assert.InRange(f.X, west - 0.01, west + WowCoords.ChunkSize + 0.01));
        }

        Assert.Equal(mesh.Indices.Length, total);
    }

    [Fact]
    public void LandmarksAreInTheirAreas()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }

        var areas = AreaTableRecord.ReadAll(client.Source).ToDictionary(a => a.Id);

        Assert.All(Landmarks.All, landmark =>
        {
            var map = WdtMap.Read(client.Source, landmark.Map);
            var (tileX, tileY) = WowCoords.TileAt(landmark.WowX, landmark.WowY);
            Assert.True(map.HasTile(tileX, tileY), $"{landmark.Name} has no terrain.");

            var terrain = AdtTerrain.Parse(client.Source.Read(map.TilePath(tileX, tileY))!, map.BigAlpha);
            var godot = WowCoords.ToGodot(new Vector3(landmark.WowX, landmark.WowY, 0));
            var chunk = WowCoords.ChunkAtGodot(godot.X, godot.Z);
            var found = terrain.Chunks[chunk.Z % 16 * 16 + chunk.X % 16].AreaId;

            var names = new List<string>();
            while (areas.TryGetValue(found, out var area))
            {
                names.Add(area.Name);
                found = area.ParentAreaId;
            }

            Assert.True(names.Contains(landmark.Area), $"{landmark.Name} is in {string.Join(", ", names)}.");
        });
    }
}
