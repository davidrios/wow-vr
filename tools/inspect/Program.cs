using AzerothVR.Formats.Assets;
using AzerothVR.Formats.Database;
using AzerothVR.Formats.Textures;

const string Usage = """
    Usage: inspect <command> [arguments]

    Reads from the client at AZVR_CLIENT_PATH.

      list [text]                 List files, optionally only those containing text.
      blp <file> <out.png> [mip]  Export a BLP texture to PNG. Don't commit the output.
      dbc <file> [max rows]       Print rows of a DBC file. Map and AreaTable are typed.
    """;

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

var clientPath = Environment.GetEnvironmentVariable("AZVR_CLIENT_PATH");
if (string.IsNullOrWhiteSpace(clientPath))
{
    Console.Error.WriteLine("AZVR_CLIENT_PATH is not set.");
    return 1;
}

try
{
    using var source = MpqAssetSource.Open(clientPath);

    switch (args)
    {
        case ["list"]:
            List(source, "");
            return 0;
        case ["list", var text]:
            List(source, text);
            return 0;
        case ["blp", var file, var output]:
            return ExportBlp(source, file, output, 0);
        case ["blp", var file, var output, var mip]:
            return ExportBlp(source, file, output, int.Parse(mip));
        case ["dbc", var file]:
            return PrintDbc(source, file, 50);
        case ["dbc", var file, var maxRows]:
            return PrintDbc(source, file, int.Parse(maxRows));
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception e) when (e is IOException or AzerothVR.Formats.WowAlchemyException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static void List(MpqAssetSource source, string text)
{
    foreach (var file in source.List().Where(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(file);
    }
}

static int ExportBlp(MpqAssetSource source, string file, string output, int mip)
{
    var data = source.Read(file);
    if (data is null)
    {
        Console.Error.WriteLine($"Not in the client: {file}");
        return 1;
    }

    var texture = BlpDecoder.Decode(data, mip);
    PngWriter.WriteRgba8(output, texture.Rgba8, texture.Width, texture.Height);
    Console.WriteLine($"{output}: {texture.Width}x{texture.Height}, mip {mip} of {texture.MipCount}");
    return 0;
}

static int PrintDbc(MpqAssetSource source, string file, int maxRows)
{
    var data = source.Read(file);
    if (data is null)
    {
        Console.Error.WriteLine($"Not in the client: {file}");
        return 1;
    }

    using var dbc = DbcFile.Open(data);
    Console.WriteLine($"{file}: {dbc.RecordCount} records, {dbc.FieldCount} fields");

    if (file.EndsWith("Map.dbc", StringComparison.OrdinalIgnoreCase))
    {
        Print(MapRecord.ReadAll(dbc, source.Locale), maxRows);
    }
    else if (file.EndsWith("AreaTable.dbc", StringComparison.OrdinalIgnoreCase))
    {
        Print(AreaTableRecord.ReadAll(dbc, source.Locale), maxRows);
    }
    else
    {
        for (var row = 0; row < Math.Min(dbc.RecordCount, maxRows); row++)
        {
            var fields = Enumerable.Range(0, dbc.FieldCount).Select(field => dbc.GetUInt32(row, field));
            Console.WriteLine(string.Join(' ', fields));
        }
    }

    return 0;
}

static void Print<T>(IEnumerable<T> records, int maxRows)
{
    foreach (var record in records.Take(maxRows))
    {
        Console.WriteLine(record);
    }
}
