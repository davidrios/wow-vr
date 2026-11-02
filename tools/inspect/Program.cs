using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;

const string Usage = """
    Usage: inspect <command> [arguments]

    Reads from the client at WOWVR_CLIENT_PATH.

      list [text]                 List files, optionally only those containing text.
      blp <file> <out.png> [mip]  Export a BLP texture to PNG. Don't commit the output.
      dbc <file> [max rows]       Print rows of a DBC file. Map and AreaTable are typed.
      area <map> <name>           Print the center of an area's terrain, for picking landmarks.
      wmos <map> <tile x> <tile y> Print a tile's WMO placements, with WoW positions.
      extract <file> <out>        Copy a file out of the client. Don't commit the output.
      check-models [text]         Parse every M2 and WMO, optionally only those containing text.
      emitters <m2 or display id>  Print a model's particle emitters.
      sky <map id> <x> <y> <hour> Print the sky colors and fog at a WoW position and time.
    """;

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

var clientPath = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
if (string.IsNullOrWhiteSpace(clientPath))
{
    Console.Error.WriteLine("WOWVR_CLIENT_PATH is not set.");
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
        case ["wmos", var map, var tileX, var tileY]:
            return PrintWmos(source, map, int.Parse(tileX), int.Parse(tileY));
        case ["sky", var mapId, var skyX, var skyY, var hour]:
            return PrintSky(source, int.Parse(mapId), float.Parse(skyX), float.Parse(skyY), float.Parse(hour));
        case ["extract", var file, var output]:
            File.WriteAllBytes(output, source.Read(file) ?? throw new IOException($"Not in the client: {file}"));
            return 0;
        case ["check-models"]:
            return CheckModels(source, "");
        case ["check-models", var text]:
            return CheckModels(source, text);
        case ["emitters", var file]:
            return PrintEmitters(source, file);
        case ["area", var map, var name]:
            return PrintArea(source, map, name);
        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (Exception e) when (e is IOException or WowVR.Formats.WowAlchemyException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int PrintWmos(MpqAssetSource source, string mapName, int tileX, int tileY)
{
    var map = WdtMap.Read(source, mapName);
    foreach (var wmo in AdtTerrain.Parse(source.Read(map.TilePath(tileX, tileY))!, map.BigAlpha).Wmos)
    {
        var (wowX, wowY) = (WowCoords.MapOrigin - wmo.Position.Z, WowCoords.MapOrigin - wmo.Position.X);
        Console.WriteLine($"{wowX:F0},{wowY:F0},{wmo.Position.Y:F0} yaw {wmo.Rotation.Y:F0} {wmo.Model}");
    }

    return 0;
}

static int CheckModels(MpqAssetSource source, string text)
{
    var files = source.List().Where(f => f.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    var (passed, failed) = (0, 0);

    void Check(string file, Action parse)
    {
        try
        {
            parse();
            passed++;
        }
        catch (Exception e) when (e is WowVR.Formats.WowAlchemyException or InvalidOperationException)
        {
            failed++;
            Console.WriteLine($"{file}: {e.Message}");
        }
    }

    foreach (var file in files.Where(f => f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)))
    {
        var skin = source.Read(M2Model.SkinPath(file));
        if (skin is not null)
        {
            Check(file, () => M2Model.Parse(source.Read(file)!, skin));
        }
    }

    foreach (var file in files.Where(f => f.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) && !IsWmoGroup(f)))
    {
        Check(file, () =>
        {
            var root = WmoRoot.Parse(source.Read(file)!);
            for (var i = 0; i < root.GroupCount; i++)
            {
                WmoGroup.Parse(source.Read(WmoRoot.GroupPath(file, i)) ?? throw new InvalidOperationException($"Group {i} is missing."), root);
            }
        });
    }

    Console.WriteLine($"{passed} parsed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

static bool IsWmoGroup(string file)
{
    var name = Path.GetFileNameWithoutExtension(file);
    return name.Length > 4 && name[^4] == '_' && name[^3..].All(char.IsAsciiDigit);
}

static int PrintArea(MpqAssetSource source, string mapName, string areaName)
{
    var areas = AreaTableRecord.ReadAll(source)
        .Where(a => a.Name.Equals(areaName, StringComparison.OrdinalIgnoreCase))
        .Select(a => a.Id)
        .ToHashSet();
    var map = WdtMap.Read(source, mapName);

    var (north, west, count) = (0.0, 0.0, 0);
    for (var y = 0; y < WowCoords.TilesPerSide; y++)
    {
        for (var x = 0; x < WowCoords.TilesPerSide; x++)
        {
            if (!map.HasTile(x, y))
            {
                continue;
            }

            foreach (var chunk in AdtTerrain.Parse(source.Read(map.TilePath(x, y))!, map.BigAlpha).Chunks)
            {
                if (areas.Contains(chunk.AreaId))
                {
                    north += chunk.WorldPosition.X - WowCoords.ChunkSize / 2;
                    west += chunk.WorldPosition.Y - WowCoords.ChunkSize / 2;
                    count++;
                }
            }
        }
    }

    if (count == 0)
    {
        Console.Error.WriteLine($"No terrain in {mapName} belongs to {areaName}.");
        return 1;
    }

    Console.WriteLine($"{areaName}: {north / count:F0}, {west / count:F0} ({count} chunks)");
    return 0;
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

static int PrintSky(MpqAssetSource source, int mapId, float x, float y, float hour)
{
    var sky = new WowVR.Formats.World.SkyState();
    var position = new System.Numerics.Vector3((float)WowCoords.MapOrigin - y, 0, (float)WowCoords.MapOrigin - x);
    WowVR.Formats.World.LightTable.Read(source).Sample(mapId, position, hour * 120f, sky);
    foreach (var band in Enum.GetValues<WowVR.Formats.World.SkyBand>())
    {
        var c = sky[band] * 255f;
        Console.WriteLine($"{band,-16} {c.X,4:F0} {c.Y,4:F0} {c.Z,4:F0}");
    }

    Console.WriteLine($"Fog {sky.FogStart:F2} to {sky.FogEnd:F0} yards");
    Console.WriteLine($"River alpha {sky.RiverShallowAlpha:F2} to {sky.RiverDeepAlpha:F2}, ocean {sky.OceanShallowAlpha:F2} to {sky.OceanDeepAlpha:F2}");
    return 0;
}

static int PrintEmitters(MpqAssetSource source, string file)
{
    if (int.TryParse(file, out var display))
    {
        file = WowVR.Formats.World.CreatureDisplayTable.Read(source).Resolve(display)?.Model ?? throw new IOException($"No display {display}");
        Console.WriteLine(file);
    }

    var model = M2Model.Parse(source.Read(file) ?? throw new IOException($"Not in the client: {file}"), source.Read(M2Model.SkinPath(file)));
    foreach (var e in model.Emitters)
    {
        Console.WriteLine($"bone {e.Bone} at {e.Position}: rate {e.Rate:F2}, life {e.Lifespan:F2} +-{e.LifespanVariation:F2}, speed {e.Speed:F2}, area {e.AreaLength:F2}x{e.AreaWidth:F2}, sizes {string.Join('/', e.Sizes.Select(v => v.ToString("F2")))}, blend {e.Blend}, {e.Texture}");
    }

    Console.WriteLine($"{model.Emitters.Count} emitters");
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
