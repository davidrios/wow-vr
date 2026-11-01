using AzerothVR.Formats.Assets;
using AzerothVR.Formats.Database;
using AzerothVR.Formats.Textures;
using Xunit;

namespace AzerothVR.Formats.Tests;

/// <summary>Opens the client once for all tests. Null when AZVR_CLIENT_PATH is unset.</summary>
public sealed class ClientFixture : IDisposable
{
    public MpqAssetSource? Source { get; }

    public ClientFixture()
    {
        var path = Environment.GetEnvironmentVariable("AZVR_CLIENT_PATH");
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
                Assert.Skip("AZVR_CLIENT_PATH is not set.");
            }

            return client.Source;
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
