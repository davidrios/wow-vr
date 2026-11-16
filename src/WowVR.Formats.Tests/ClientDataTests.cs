using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Textures;
using Xunit;

namespace WowVR.Formats.Tests;

/// <summary>Opens the client once for all tests. Null when WOWVR_CLIENT_PATH is unset.</summary>
public sealed class ClientFixture : IDisposable
{
    public MpqAssetSource? Source { get; }

    public ClientFixture()
    {
        var path = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            Source = MpqAssetSource.Open(path);
        }
    }

    public void Dispose() => Source?.Dispose();
}

public class ClientDataTests(ClientFixture client) : IClassFixture<ClientFixture>
{
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

    [Theory]
    [InlineData(@"textures\SunCenter.blp")]
    [InlineData(@"textures\SunGlare.blp")]
    [InlineData(@"textures\moon.blp")]
    [InlineData(@"textures\moonglare.blp")]
    public void ReadsTheSunAndMoon(string path)
    {
        var texture = BlpDecoder.ConvertToGpu(Source.Read(path)!);

        Assert.True(texture.Width > 0 && texture.Height > 0);
    }

    [Theory]
    [InlineData(@"TILESET\Elwynn\ElwynnGrassBase.blp")]
    [InlineData(@"textures\SunCenter.blp")]
    [InlineData(@"Character\Human\Male\HumanMaleSkin00_00.blp")]
    [InlineData(@"Interface\Icons\INV_Misc_QuestionMark.blp")]
    public void InfoSaysHowATextureIsStored(string path)
    {
        var data = Source.Read(path)!;

        var info = BlpDecoder.ReadInfo(data);
        var gpu = BlpDecoder.ConvertToGpu(data);

        Assert.Equal(2, info.Version);
        Assert.Equal(BlpDecoder.ReadSize(data), (info.Width, info.Height));
        Assert.Equal(info.Encoding == BlpEncoding.Dxt, info.Dxt != 0);

        // The GPU path keeps a file's DXT blocks only with a whole chain of mipmaps; the icon has none.
        if (gpu.Format != GpuTextureFormat.Rgba8)
        {
            Assert.Equal((BlpEncoding.Dxt, (int)gpu.Format), (info.Encoding, info.Dxt));
        }

        if (path.StartsWith("Character", StringComparison.Ordinal))
        {
            Assert.Equal(BlpEncoding.Palette, info.Encoding);
        }
    }

    [Fact]
    public void MapTableHasTheContinents()
    {
        var maps = MapRecord.ReadAll(Source).ToDictionary(m => m.Id);

        Assert.Equal("Azeroth", maps[0].Directory);
        Assert.Equal("Eastern Kingdoms", maps[0].Name);
        Assert.Equal("Kalimdor", maps[1].Directory);
        Assert.Equal("Expansion01", maps[530].Directory);
        Assert.Equal("Northrend", maps[571].Directory);
    }

    [Theory]
    [InlineData(0, "EASTERNKINGDOM")]
    [InlineData(1, "KALIMDOR")]
    [InlineData(530, "OUTLAND")]
    [InlineData(571, "NORTHREND")]
    public void ContinentsNameTheirLoadingScreens(int map, string picture)
    {
        var id = MapRecord.ReadAll(Source).First(m => m.Id == map).LoadingScreenId;
        var path = LoadingScreenRecord.ReadAll(Source).First(s => s.Id == id).Path;

        Assert.Contains(picture, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(BlpDecoder.Decode(Source.Read(path)!).Width > 0);
    }

    [Fact]
    public void ATerrainTexturesSpecularFileKeepsItsColorsAndHasShineInAlpha()
    {
        const string Path = @"TILESET\Elwynn\ElwynnCobbleStoneBase.blp";

        var plain = BlpDecoder.Decode(Source.Read(Path)!);
        var specular = BlpDecoder.Decode(Source.Read(WowVR.Formats.Terrain.TerrainTextures.SpecularPath(Path))!);

        Assert.Equal((plain.Width, plain.Height), (specular.Width, specular.Height));
        Assert.Equal(plain.Rgba8[0..3], specular.Rgba8[0..3]);
        var alphas = specular.Rgba8.Where((_, i) => i % 4 == 3).ToArray();
        Assert.InRange(alphas.Average(a => (double)a), 20, 100);
        Assert.True(alphas.Min() < alphas.Max());
    }

    [Fact]
    public void AreaTableHasElwynnForestAndGoldshire()
    {
        var areas = AreaTableRecord.ReadAll(Source).ToDictionary(a => a.Id);

        Assert.Equal("Elwynn Forest", areas[12].Name);
        Assert.Equal(0, areas[12].MapId);
        Assert.Equal("Goldshire", areas[87].Name);
        Assert.Equal(12, areas[87].ParentAreaId);
    }

    [Fact]
    public void IconDecodesToItsKnownSize()
    {
        var blp = Source.Read(@"Interface\Icons\INV_Misc_QuestionMark.blp");
        Assert.NotNull(blp);

        var texture = BlpDecoder.Decode(blp);

        Assert.Equal(64, texture.Width);
        Assert.Equal(64, texture.Height);
        Assert.Equal(64 * 64 * 4, texture.Rgba8.Length);
        Assert.Contains(texture.Rgba8, b => b != 0);
    }

    [Fact]
    public void PathsAreCaseAndSeparatorInsensitive()
    {
        Assert.True(Source.Exists(@"DBFilesClient\Map.dbc"));
        Assert.True(Source.Exists("dbfilesclient/map.dbc"));
    }

    [Fact]
    public void MissingFileReadsAsNull()
    {
        Assert.False(Source.Exists(@"No\Such\File.blp"));
        Assert.Null(Source.Read(@"No\Such\File.blp"));
    }

    [Fact]
    public void ListIncludesKnownFiles()
    {
        Assert.Contains(Source.List(), f => f.Equals(@"DBFilesClient\Map.dbc", StringComparison.OrdinalIgnoreCase));
    }
}
