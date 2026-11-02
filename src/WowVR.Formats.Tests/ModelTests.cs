using System.Numerics;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;
using Xunit;


namespace WowVR.Formats.Tests;

public class PlacementMathTests
{
    [Fact]
    public void ModelAxesKeepHandedness()
    {
        var x = WowCoords.ModelToGodot(Vector3.UnitX);
        var y = WowCoords.ModelToGodot(Vector3.UnitY);
        var z = WowCoords.ModelToGodot(Vector3.UnitZ);

        Assert.Equal(z, Vector3.Cross(x, y));
    }

    [Fact]
    public void PlacementIsRelativeToTheTile()
    {
        var position = new Vector3((float)(31.5 * WowCoords.TileSize), 60, (float)(49.25 * WowCoords.TileSize));

        var transform = WowCoords.TilePlacement(position, Vector3.Zero, 2f, 31, 49);

        Assert.Equal((float)(0.5 * WowCoords.TileSize), transform.Origin.X, 2);
        Assert.Equal(60f, transform.Origin.Y);
        Assert.Equal((float)(0.25 * WowCoords.TileSize), transform.Origin.Z, 2);
        Assert.Equal(2f, transform.AxisX.Length(), 4);
    }

    [Fact]
    public void ComposeAppliesTheInnerTransformFirst()
    {
        var outer = new PlacementTransform(Vector3.UnitZ, Vector3.UnitY, -Vector3.UnitX, new Vector3(10, 0, 0));
        var inner = new PlacementTransform(Vector3.UnitX * 2, Vector3.UnitY * 2, Vector3.UnitZ * 2, new Vector3(1, 0, 0));
        var point = new Vector3(1, 2, 3);

        Assert.Equal(outer.Apply(inner.Apply(point)), outer.Compose(inner).Apply(point));
    }
}

public class ClientModelTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private const int GoldshireX = 31;
    private const int GoldshireY = 49;

    private AdtTerrain Goldshire()
    {
        var map = WdtMap.Read(client.Source!, "Azeroth");
        return AdtTerrain.Parse(client.Source!.Read(map.TilePath(GoldshireX, GoldshireY))!, map.BigAlpha);
    }

    private void SkipWithoutClient()
    {
        if (client.Source is null)
        {
            Assert.Skip("WOWVR_CLIENT_PATH is not set.");
        }
    }

    [Fact]
    public void PlacementsNameModelsInTheClient()
    {
        SkipWithoutClient();
        var terrain = Goldshire();

        Assert.NotEmpty(terrain.Doodads);
        Assert.NotEmpty(terrain.Wmos);
        Assert.All(terrain.Doodads, d =>
        {
            Assert.True(client.Source!.Read(d.Model) is not null, $"{d.Model} isn't in the client.");
            Assert.InRange(d.Scale, 0.01f, 20f);
        });
        Assert.All(terrain.Wmos, w => Assert.True(client.Source!.Read(w.Model) is not null, $"{w.Model} isn't in the client."));
    }

    /// <summary>The file stores each WMO's world bounds, which checks the rotation independently.</summary>
    [Theory]
    [InlineData("Azeroth")]
    [InlineData("Kalimdor")]
    [InlineData("Expansion01")]
    [InlineData("Northrend")]
    public void TransformedWmoBoundsMatchTheFile(string directory)
    {
        SkipWithoutClient();
        var map = WdtMap.Read(client.Source!, directory);
        var roots = new Dictionary<string, WmoRoot>(StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;
        var errors = new List<(float Error, string Model, Vector3 Rotation)>();
        var seen = new HashSet<uint>();

        for (var y = 0; y < WowCoords.TilesPerSide; y += 3)
        {
            for (var x = 0; x < WowCoords.TilesPerSide; x += 3)
            {
                if (!map.HasTile(x, y))
                {
                    continue;
                }

                foreach (var placement in AdtTerrain.Parse(client.Source!.Read(map.TilePath(x, y))!, map.BigAlpha).Wmos)
                {
                    if (!seen.Add(placement.UniqueId))
                    {
                        continue;
                    }

                    if (!roots.TryGetValue(placement.Model, out var root))
                    {
                        roots[placement.Model] = root = WmoRoot.Parse(client.Source.Read(placement.Model)!);
                    }

                    var transform = WowCoords.TilePlacement(placement.Position, placement.Rotation, 1f, 0, 0);
                    var min = new Vector3(float.MaxValue);
                    var max = new Vector3(float.MinValue);
                    for (var corner = 0; corner < 8; corner++)
                    {
                        var local = new Vector3(
                            (corner & 1) == 0 ? root.Bounds.Min.X : root.Bounds.Max.X,
                            (corner & 2) == 0 ? root.Bounds.Min.Y : root.Bounds.Max.Y,
                            (corner & 4) == 0 ? root.Bounds.Min.Z : root.Bounds.Max.Z);
                        var point = transform.Apply(WowCoords.ModelToGodot(local));
                        min = Vector3.Min(min, point);
                        max = Vector3.Max(max, point);
                    }

                    var error = Math.Max(
                        (min - placement.BoundsMin).Length(),
                        (max - placement.BoundsMax).Length());
                    errors.Add((error, placement.Model, placement.Rotation));
                    checkedCount++;
                }
            }
        }

        errors.Sort((p, q) => p.Error.CompareTo(q.Error));
        var median = errors[errors.Count / 2].Error;
        var within = errors.Count(e => e.Error < 1f) * 100 / errors.Count;
        TestContext.Current.SendDiagnosticMessage($"{directory}: {checkedCount} WMOs, median {median:F3}, {within}% within 1 yard");
        Console.WriteLine($"{directory}: {checkedCount} WMOs, median {median:F3}, {within}% within 1 yard");
        foreach (var e in errors.TakeLast(6))
        {
            Console.WriteLine($"  {e.Error:F1} {e.Model} rot {e.Rotation}");
        }

        Assert.True(checkedCount > 0);
        Assert.InRange(median, 0f, 0.1f);
        Assert.True(within >= 90, $"Only {within}% are within a yard.");
    }

    [Fact]
    public void GoldshireModelsParse()
    {
        SkipWithoutClient();
        var terrain = Goldshire();

        var surfaces = 0;
        var withCollision = 0;
        foreach (var name in terrain.Doodads.Select(d => d.Model).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            M2Model model;
            try
            {
                model = M2Model.Parse(client.Source!.Read(name)!, client.Source.Read(M2Model.SkinPath(name))!);
            }
            catch (WowAlchemyException e)
            {
                throw new InvalidOperationException($"{name}: {e.Message}", e);
            }

            surfaces += model.Surfaces.Count;
            withCollision += model.CollisionFaces.Length > 0 ? 1 : 0;

            // The collision mesh wraps the model, so it can't be far outside what's drawn.
            Assert.True(model.CollisionFaces.Length % 3 == 0, name);
            Assert.All(model.CollisionFaces, f => Assert.True(f.Length() <= model.Radius * 2f + 1f, $"{name} has a collision corner at {f}."));

            Assert.InRange(model.Radius, 0.001f, 500f);
            Assert.All(model.Surfaces, s =>
            {
                Assert.True(s.Indices.Length % 3 == 0 && s.Indices.All(i => i < s.Positions.Length), name);
                Assert.True(client.Source.Read(s.Material.Texture) is not null, $"{name} names missing {s.Material.Texture}.");
            });
        }

        Assert.True(surfaces > 0);
        Assert.True(withCollision > 0, "No model in Goldshire has a collision mesh.");
    }

    [Fact]
    public void GoldshireWmosParseWithGeometryInsideTheirBounds()
    {
        SkipWithoutClient();
        var terrain = Goldshire();

        foreach (var name in terrain.Wmos.Select(w => w.Model).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var root = WmoRoot.Parse(client.Source!.Read(name)!);
            Assert.True(root.GroupCount > 0, name);
            Assert.NotEmpty(root.Materials);

            var min = WowCoords.ModelToGodot(root.Bounds.Min);
            var max = WowCoords.ModelToGodot(root.Bounds.Max);
            var (low, high) = (Vector3.Min(min, max) - new Vector3(5), Vector3.Max(min, max) + new Vector3(5));

            var triangles = 0;
            for (var i = 0; i < root.GroupCount; i++)
            {
                var group = WmoGroup.Parse(client.Source.Read(WmoRoot.GroupPath(name, i))!, root);
                triangles += group.CollisionFaces.Length / 3;
                Assert.True(group.CollisionFaces.Length % 3 == 0, name);
                Assert.All(group.Surfaces.SelectMany(s => s.Positions), p =>
                    Assert.True(p.X >= low.X && p.X <= high.X && p.Y >= low.Y && p.Y <= high.Y && p.Z >= low.Z && p.Z <= high.Z, $"{name} has a vertex at {p}."));
            }

            Assert.True(triangles > 0, $"{name} has nothing to collide with.");

            Assert.All(root.DoodadsInSet(0), d =>
                Assert.True(client.Source.Read(d.Model) is not null, $"{name} names missing doodad {d.Model}."));
        }
    }

    /// <summary>Drawing a model only from its origin's tile relies on that tile listing it.</summary>
    [Fact]
    public void ModelsAreListedByTheTileHoldingTheirOrigin()
    {
        SkipWithoutClient();
        var map = WdtMap.Read(client.Source!, "Azeroth");
        var around = new[] { (30, 48), (31, 48), (32, 48), (30, 49), (31, 49), (32, 49), (30, 50), (31, 50), (32, 50) };

        var tiles = around.ToDictionary(t => t, t => AdtTerrain.Parse(client.Source!.Read(map.TilePath(t.Item1, t.Item2))!, map.BigAlpha));

        var (missing, total) = (0, 0);
        foreach (var placement in tiles[(31, 49)].Doodads.Concat(tiles[(31, 49)].Wmos))
        {
            total++;
            var origin = placement.OriginTile;
            if (!map.HasTile(origin.X, origin.Y) || !tiles.TryGetValue(origin, out var owner))
            {
                continue;
            }

            if (!owner.Doodads.Concat(owner.Wmos).Any(p => p.UniqueId == placement.UniqueId))
            {
                missing++;
            }
        }

        Console.WriteLine($"{missing} of {total} placements missing from their origin's tile");
        Assert.Equal(0, missing);
    }

    [Fact]
    public void TexturesConvertForUpload()
    {
        SkipWithoutClient();
        var terrain = Goldshire();
        var name = terrain.Doodads[0].Model;
        var model = M2Model.Parse(client.Source!.Read(name)!, client.Source.Read(M2Model.SkinPath(name))!);

        var texture = BlpDecoder.ConvertToGpu(client.Source.Read(model.Surfaces[0].Material.Texture)!);

        Assert.True(texture.Width > 0 && texture.Height > 0 && texture.Pixels.Length > 0);
        if (texture.Format == GpuTextureFormat.Rgba8)
        {
            Assert.Equal(texture.Width * texture.Height * 4, texture.Pixels.Length);
        }
    }
}
