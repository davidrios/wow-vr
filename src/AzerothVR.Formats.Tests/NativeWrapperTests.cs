using System.Text;
using AzerothVR.Formats.Assets;
using AzerothVR.Formats.Database;
using AzerothVR.Formats.Textures;
using Xunit;

namespace AzerothVR.Formats.Tests;

public class NativeWrapperTests
{
    private static byte[] SampleDbc()
    {
        var strings = Encoding.UTF8.GetBytes("\0Azeroth\0");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("WDBC"u8);
        foreach (var value in new[] { 1, 3, 12, strings.Length, 7, 1, -1 })
        {
            writer.Write(value);
        }

        writer.Write(strings);
        return stream.ToArray();
    }

    [Fact]
    public void DbcFileReadsFieldsAndStrings()
    {
        using var dbc = DbcFile.Open(SampleDbc());

        Assert.Equal(1, dbc.RecordCount);
        Assert.Equal(3, dbc.FieldCount);
        Assert.Equal(7, dbc.GetInt32(0, 0));
        Assert.Equal("Azeroth", dbc.GetString(0, 1));
        Assert.Equal(-1, dbc.GetInt32(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => dbc.GetInt32(1, 0));
    }

    [Fact]
    public void InvalidFilesRaiseNativeErrors()
    {
        var error = Assert.Throws<WowAlchemyException>(() => DbcFile.Open("nope"u8));
        Assert.NotEmpty(error.Message);

        error = Assert.Throws<WowAlchemyException>(() => BlpDecoder.Decode("nope"u8));
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void ClientArchivesLoadInPatchOrder()
    {
        var client = Directory.CreateTempSubdirectory("azvr-test");
        try
        {
            var data = client.CreateSubdirectory("Data");
            var locale = data.CreateSubdirectory("enUS");
            foreach (var name in new[] { "patch-3.MPQ", "PATCH.mpq", "lichking.MPQ", "common.MPQ", "patch-2.MPQ", "enUS.zip" })
            {
                File.WriteAllBytes(Path.Combine(data.FullName, name), []);
            }

            foreach (var name in new[] { "patch-enUS-2.MPQ", "patch-enUS.MPQ", "locale-enUS.MPQ", "backup-enUS.MPQ" })
            {
                File.WriteAllBytes(Path.Combine(locale.FullName, name), []);
            }

            var archives = ClientArchives.Find(client.FullName);

            Assert.Equal("enUS", archives.Locale);
            Assert.Equal(
                ["common.MPQ", "lichking.MPQ", "locale-enUS.MPQ", "PATCH.mpq", "patch-2.MPQ", "patch-3.MPQ", "patch-enUS.MPQ", "patch-enUS-2.MPQ"],
                archives.Paths.Select(Path.GetFileName));
        }
        finally
        {
            client.Delete(recursive: true);
        }
    }

    [Fact]
    public void MissingDataFolderIsReported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ClientArchives.Find(Path.Combine(Path.GetTempPath(), "azvr-no-such-client")));
    }
}
