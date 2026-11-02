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

public class GroundCoverDataTests
{
    [Fact]
    public void ElwynnCoverIsRepeatableAndLeavesSomeCellsClear()
    {
        var client = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        using var source = WowVR.Formats.Assets.MpqAssetSource.Open(client);
        var table = WowVR.Formats.World.GroundEffectTable.Read(source);
        var terrain = AdtTerrain.Parse(source.Read(@"World\Maps\Azeroth\Azeroth_31_49.adt")!, false);
        var heights = new TerrainHeights(terrain, 31, 49);

        var blocked = terrain.Chunks.Sum(c => System.Numerics.BitOperations.PopCount(c.NoEffectDoodad));
        var items = Enumerable.Range(0, AdtTerrain.ChunksPerSide)
            .SelectMany(iy => Enumerable.Range(0, AdtTerrain.ChunksPerSide)
                .SelectMany(ix => WowVR.Formats.World.GroundCover.PlaceChunk(heights, table, 31, 49, ix, iy)))
            .ToList();

        Assert.InRange(blocked, 1, 256 * 64 - 1);
        Assert.NotEmpty(items);
        Assert.All(items.Select(i => i.Model).Distinct(), model => Assert.NotNull(source.Read(model)));
        Assert.All(items.Take(500), i => Assert.Equal(heights.HeightAt(i.WowX, i.WowY), i.WowZ));
        Assert.Equal(
            WowVR.Formats.World.GroundCover.PlaceChunk(heights, table, 31, 49, 8, 8),
            WowVR.Formats.World.GroundCover.PlaceChunk(heights, table, 31, 49, 8, 8));
    }
}

public class ContinentTests
{
    [Fact]
    public void EveryContinentHasSomewhereToArrive()
    {
        Assert.All(WowVR.Formats.World.Landmarks.Continents, c => Assert.NotEmpty(WowVR.Formats.World.Landmarks.On(c.Map)));
    }
}

public class GroundCoverWaterTests
{
    [Fact]
    public void NoCoverGrowsUnderwater()
    {
        var client = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        using var source = WowVR.Formats.Assets.MpqAssetSource.Open(client);
        var table = WowVR.Formats.World.GroundEffectTable.Read(source);
        var underwaterGround = 0;
        var items = 0;
        foreach (var (x, y) in new[] { (31, 49), (32, 49), (32, 50), (31, 50), (33, 49) })
        {
            var terrain = AdtTerrain.Parse(source.Read($@"World\Maps\Azeroth\Azeroth_{x}_{y}.adt")!, false);
            var heights = new TerrainHeights(terrain, x, y);
            foreach (var layer in terrain.Liquids)
            {
                var wowX = WowCoords.MapOrigin - y * WowCoords.TileSize - (layer.ChunkIy + 0.5) * WowCoords.ChunkSize;
                var wowY = WowCoords.MapOrigin - x * WowCoords.TileSize - (layer.ChunkIx + 0.5) * WowCoords.ChunkSize;
                underwaterGround += heights.UnderLiquid(wowX, wowY, heights.HeightAt(wowX, wowY)) ? 1 : 0;

                var placed = WowVR.Formats.World.GroundCover.PlaceChunk(heights, table, x, y, layer.ChunkIx, layer.ChunkIy);
                items += placed.Count;
                Assert.DoesNotContain(placed, i => heights.UnderLiquid(i.WowX, i.WowY, i.WowZ));
            }
        }

        // Without these, the check above proves nothing.
        Assert.True(underwaterGround > 0);
        Assert.True(items > 0);
    }
}

public class WmoFloorLightTests
{
    private static ModelSurface Surface(Vector3[] positions, byte[]? colors, int[] indices) => new()
    {
        Material = new ModelMaterial("t", ModelBlend.Opaque, false, false, true),
        Positions = positions,
        Normals = new Vector3[positions.Length],
        TexCoords = new Vector2[positions.Length],
        Colors = colors,
        Indices = indices,
    };

    /// <summary>A floor triangle at this height, with a corner of each color: red, green, blue.</summary>
    private static ModelSurface Floor(float y) => Surface(
        [new Vector3(0, y, 0), new Vector3(10, y, 0), new Vector3(0, y, 10)],
        [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255],
        [0, 1, 2]);

    [Fact]
    public void BlendsTheCornersOfTheTriangleUnderThePoint()
    {
        var light = WmoFloorLight.Build([Floor(0)])!;

        Assert.Equal(new Vector3(1, 0, 0), light.Sample(new Vector3(0, 0, 0))!.Value);

        var middle = light.Sample(new Vector3(5, 0, 0))!.Value;
        Assert.Equal(0.5f, middle.X, 3);
        Assert.Equal(0.5f, middle.Y, 3);
        Assert.Equal(0f, middle.Z, 3);
    }

    [Fact]
    public void FindsATriangleCrossingManyCells()
    {
        var light = WmoFloorLight.Build([Floor(0)])!;

        Assert.NotNull(light.Sample(new Vector3(1, 0, 8.5f)));
    }

    [Fact]
    public void PicksTheStoreyNearestThePoint()
    {
        var upstairs = Surface(
            [new Vector3(0, 5, 0), new Vector3(10, 5, 0), new Vector3(0, 5, 10)],
            [9, 9, 9, 255, 9, 9, 9, 255, 9, 9, 9, 255],
            [0, 1, 2]);
        var light = WmoFloorLight.Build([Floor(0), upstairs])!;

        Assert.Equal(9f / 255f, light.Sample(new Vector3(1, 4.5f, 1))!.Value.X, 3);
        Assert.True(light.Sample(new Vector3(1, 0.5f, 1))!.Value.X > 0.5f);
    }

    [Fact]
    public void ACreatureKeepsOnlyAHintOfAStrongColor()
    {
        var floor = new Vector3(1f, 0.5f, 0.15f);

        var light = WmoFloorLight.ForCreature(floor);

        Assert.True(light.X > light.Z);
        Assert.True(light.X / light.Z < floor.X / floor.Z / 2f);
    }

    [Fact]
    public void ACreatureOnADarkFloorStaysVisible()
    {
        var light = WmoFloorLight.ForCreature(Vector3.Zero);

        Assert.Equal(0.85f, light.X, 3);
        Assert.Equal(0.85f, light.Y, 3);
        Assert.Equal(0.85f, light.Z, 3);
    }

    [Fact]
    public void ADoodadIsNeverBlackOrPastWhite()
    {
        Assert.Equal(0.35f, WmoFloorLight.ForDoodad(Vector3.Zero).X, 3);

        var bright = WmoFloorLight.ForDoodad(new Vector3(1.13f));
        Assert.Equal(1f, bright.X, 3);
        Assert.Equal(1f, bright.Z, 3);
    }

    [Fact]
    public void ADoodadKeepsMoreColorThanACreature()
    {
        var blue = new Vector3(0.28f, 0.31f, 0.71f);

        var doodad = WmoFloorLight.ForDoodad(blue);
        var creature = WmoFloorLight.ForCreature(blue);

        Assert.True(doodad.Z / doodad.X < blue.Z / blue.X);
        Assert.True(doodad.Z / doodad.X > creature.Z / creature.X);
    }

    [Fact]
    public void HasNoAnswerAwayFromAnyFloor()
    {
        var light = WmoFloorLight.Build([Floor(0)])!;

        Assert.Null(light.Sample(new Vector3(9, 0, 9)));
        Assert.Null(light.Sample(new Vector3(1, 3, 1)));
        Assert.Null(light.Sample(new Vector3(-50, 0, 1)));
    }

    [Fact]
    public void WallsAndSurfacesWithoutColorsAreLeftOut()
    {
        var wall = Surface(
            [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 10, 0)],
            [1, 1, 1, 255, 1, 1, 1, 255, 1, 1, 1, 255],
            [0, 1, 2]);
        var plain = Surface([new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10)], null, [0, 1, 2]);

        Assert.Null(WmoFloorLight.Build([wall, plain]));
    }
}

public class WmoPortalTests
{
    private const uint Exterior = 0x8;
    private const uint Interior = 0x2000;

    /// <summary>Outside, then room 1 through a door at X = 10 facing the camera, then room 2 behind room 1 through a door at X = 20.</summary>
    private static WmoPortals House() => new(
        [Exterior, Interior, Interior],
        [(new Vector3(-100, -100, -100), new Vector3(100, 100, 100)), (new Vector3(10, -5, -5), new Vector3(20, 5, 5)), (new Vector3(20, -5, -5), new Vector3(30, 5, 5))],
        [new(10, -1, -1), new(10, 1, -1), new(10, 1, 1), new(10, -1, 1), new(20, -1, -1), new(20, 1, -1), new(20, 1, 1), new(20, -1, 1)],
        [(0, 4, Vector3.UnitX, -10f), (4, 4, Vector3.UnitX, -20f)],
        [(0, 0, 1, -1), (1, 0, 0, 1), (1, 1, 2, -1), (2, 1, 1, 1)]);

    /// <summary>A camera at the origin-relative position, looking along +X, with a 90 degree view.</summary>
    private static Func<Vector3, (float X, float Y, float W)> LookingAlongX(Vector3 camera) =>
        p => (p.Y - camera.Y, p.Z - camera.Z, p.X - camera.X);

    private static bool[] Compute(WmoPortals portals, Vector3 camera, Func<Vector3, (float X, float Y, float W)> toClip)
    {
        var visible = new bool[portals.GroupCount];
        portals.Compute(camera, toClip, visible);
        return visible;
    }

    [Fact]
    public void LookingAtTheDoorShowsWhatsBehindIt()
    {
        var camera = new Vector3(0, 0, 0);

        Assert.Equal([true, true, true], Compute(House(), camera, LookingAlongX(camera)));
    }

    [Fact]
    public void LookingAwayFromTheDoorHidesTheRooms()
    {
        var camera = new Vector3(0, 0, 0);

        var lookingBack = Compute(House(), camera, p => (p.Y, p.Z, camera.X - p.X));

        Assert.Equal([true, false, false], lookingBack);
    }

    [Fact]
    public void ADoorOffToTheSideOfTheViewIsntFollowed()
    {
        var camera = new Vector3(0, 0, 0);

        var aside = Compute(House(), camera, p => (p.Y + 50f * p.X, p.Z, p.X));

        Assert.Equal([true, false, false], aside);
    }

    [Fact]
    public void FromBehindTheDoorsPlaneItLeadsNowhere()
    {
        var camera = new Vector3(40, 0, 0);

        var visible = Compute(House(), camera, p => (p.Y, p.Z, camera.X - p.X));

        Assert.Equal([true, false, false], visible);
    }

    [Fact]
    public void InsideARoomOnlyPortalsLeadOut()
    {
        var camera = new Vector3(15, 0, 0);

        var visible = Compute(House(), camera, LookingAlongX(camera));

        Assert.True(visible[1]);
        Assert.True(visible[2]);
        Assert.False(visible[0]);
    }

    [Fact]
    public void JustAboveARoomsBoxShowsBothTheRoomAndTheOutside()
    {
        var camera = new Vector3(15, 0, 6.5f);

        var visible = Compute(House(), camera, p => (p.Y - camera.Y, p.Z - camera.Z, camera.X - p.X));

        Assert.Equal([true, true, false], visible);
    }

    [Fact]
    public void AWmoWithNoOutsideShowsEverythingFromOutside()
    {
        var portals = new WmoPortals([Interior], [(Vector3.Zero, Vector3.One)], [], [], []);

        Assert.Equal([true], Compute(portals, new Vector3(50, 0, 0), p => (0, 0, 1)));
    }

    [Fact]
    public void ABoxsCornerOutsideTheRoomStillSeesTheOutside()
    {
        const uint Exterior = 0x8;

        // A diamond-shaped room, whose box has four empty corners.
        var (min, max) = (new Vector3(-10, -10, 0), new Vector3(10, 10, 10));
        var (north, east, south, west) = (new Vector2(0, 10), new Vector2(10, 0), new Vector2(0, -10), new Vector2(-10, 0));
        var room = GroupFootprint.Build([(north, east, south), (north, south, west)], new Vector2(min.X, min.Y), new Vector2(max.X, max.Y));
        var portals = new WmoPortals(
            [Interior, Exterior],
            [(min, max), (min * 2, max * 2)],
            [],
            [],
            [],
            [room, null]);

        Assert.Equal([false, true], Compute(portals, new Vector3(9, 9, 5), p => (p.X, p.Y, 1)));
        Assert.Equal([true, false], Compute(portals, new Vector3(1, 1, 5), p => (p.X, p.Y, 1)));
    }
}

public class ClientModelTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    [Theory]
    [InlineData(@"World\wmo\Lorderon\Undercity\Undercity.wmo")]
    [InlineData(@"World\wmo\Azeroth\Buildings\Stormwind\Stormwind.wmo")]
    public void PortalPlanesAndSidesReadTheWayTheCullingExpects(string path)
    {
        SkipWithoutClient();
        var root = WmoRoot.Parse(client.Source!.Read(path));
        var groups = Enumerable.Range(0, root.GroupCount).Select(i => WmoGroup.Parse(client.Source.Read(WmoRoot.GroupPath(path, i)), root)).ToArray();

        foreach (var (start, count, normal, offset) in root.Portals)
        {
            for (var i = start; i < start + count; i++)
            {
                Assert.True(Math.Abs(Vector3.Dot(normal, root.PortalVertices[i]) + offset) < 0.1f);
            }
        }

        // The group listing a portal mostly lies on the side the listing states.
        var (agree, total) = (0, 0);
        foreach (var group in groups)
        {
            var center = (group.Bounds.Min + group.Bounds.Max) / 2;
            for (var i = group.PortalRange.Start; i < group.PortalRange.Start + group.PortalRange.Count; i++)
            {
                var (portal, _, side) = root.PortalRefs[i];
                total++;
                agree += Math.Sign(Vector3.Dot(root.Portals[portal].Normal, center) + root.Portals[portal].Offset) == side ? 1 : 0;
            }
        }

        Assert.True(agree >= total * 0.9, $"{agree} of {total}");
    }

    [Fact]
    public void UndercitysInteriorShowsFromInside()
    {
        SkipWithoutClient();
        const string Path = @"World\wmo\Lorderon\Undercity\Undercity.wmo";
        var root = WmoRoot.Parse(client.Source!.Read(Path));
        var groups = Enumerable.Range(0, root.GroupCount).Select(i => WmoGroup.Parse(client.Source.Read(WmoRoot.GroupPath(Path, i)), root)).ToArray();
        var references = new List<(int, int, int, int)>();
        for (var g = 0; g < groups.Length; g++)
        {
            for (var i = groups[g].PortalRange.Start; i < groups[g].PortalRange.Start + groups[g].PortalRange.Count; i++)
            {
                var (portal, other, side) = root.PortalRefs[i];
                references.Add((g, portal, other, side));
            }
        }

        var portals = new WmoPortals(groups.Select(g => g.Flags).ToArray(), groups.Select(g => g.Bounds).ToArray(), root.PortalVertices, root.Portals, references.ToArray());
        var biggest = Array.IndexOf(groups, groups.Where(g => g.Indoor).MaxBy(g => (g.Bounds.Max - g.Bounds.Min).LengthSquared()));
        var camera = (groups[biggest].Bounds.Min + groups[biggest].Bounds.Max) / 2;
        var visible = new bool[groups.Length];

        portals.Compute(camera, p => (p.Y - camera.Y, p.Z - camera.Z, p.X - camera.X), visible);

        Assert.True(visible[biggest]);
        Assert.True(visible.Count(v => v) > 1, "nothing shows through its portals");
    }

    [Fact]
    public void UndercitysInteriorHidesFromTheSurface()
    {
        SkipWithoutClient();
        const string Path = @"World\wmo\Lorderon\Undercity\Undercity.wmo";
        const uint Exterior = 0x8;
        var root = WmoRoot.Parse(client.Source!.Read(Path));
        var groups = Enumerable.Range(0, root.GroupCount).Select(i => WmoGroup.Parse(client.Source.Read(WmoRoot.GroupPath(Path, i)), root)).ToArray();
        var references = new List<(int, int, int, int)>();
        for (var g = 0; g < groups.Length; g++)
        {
            for (var i = groups[g].PortalRange.Start; i < groups[g].PortalRange.Start + groups[g].PortalRange.Count; i++)
            {
                var (portal, other, side) = root.PortalRefs[i];
                references.Add((g, portal, other, side));
            }
        }

        Assert.NotEmpty(references);
        var portals = new WmoPortals(groups.Select(g => g.Flags).ToArray(), groups.Select(g => g.Bounds).ToArray(), root.PortalVertices, root.Portals, references.ToArray());

        // High above, looking straight along +X, past the top of the whole thing.
        var height = root.Bounds.Max.Z + 500f;
        var camera = new Vector3(root.Bounds.Min.X - 2000f, 0, height);
        var visible = new bool[groups.Length];
        portals.Compute(camera, p => (p.Y - camera.Y, p.Z - camera.Z, p.X - camera.X), visible);

        Assert.All(groups.Select((g, i) => (g, i)).Where(x => (x.g.Flags & Exterior) != 0), x => Assert.True(visible[x.i]));
        Assert.True(visible.Count(v => !v) > groups.Length / 2, $"{visible.Count(v => !v)} of {groups.Length} are hidden");
    }

    [Theory]
    [InlineData(@"World\wmo\Azeroth\Buildings\Stormwind\Stormwind.wmo")]
    [InlineData(@"World\wmo\Lorderon\Undercity\Undercity.wmo")]
    public void GroupsLitFromOutsideArentIndoor(string path)
    {
        SkipWithoutClient();
        const uint Interior = 0x2000;
        const uint ExteriorLit = 0x40;
        var root = WmoRoot.Parse(client.Source!.Read(path));
        var groups = Enumerable.Range(0, root.GroupCount)
            .Select(i => client.Source.Read(WmoRoot.GroupPath(path, i)))
            .Where(g => g is { Length: > 0 })
            .Select(g => WmoGroup.Parse(g!, root))
            .ToList();

        Assert.Contains(groups, g => (g.Flags & (Interior | ExteriorLit)) == (Interior | ExteriorLit));
        Assert.All(groups, g => Assert.Equal((g.Flags & Interior) != 0 && (g.Flags & ExteriorLit) == 0, g.Indoor));
        Assert.Contains(groups, g => g.Indoor && g.DoodadRefs.Length > 0);
        Assert.All(groups, g => Assert.All(g.DoodadRefs, r => Assert.True(r < root.Doodads.Count)));
    }

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
