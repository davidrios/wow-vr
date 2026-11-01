using AzerothVR.Formats.Assets;

namespace AzerothVR.Formats.Database;

/// <summary>
/// The slot of a client locale in 3.3.5a localized strings. A client only fills in
/// its own slot, and enGB uses the enUS slot.
/// </summary>
public static class DbcLocale
{
    private static readonly string[] Slots =
        ["enUS", "koKR", "frFR", "deDE", "zhCN", "zhTW", "esES", "esMX", "ruRU"];

    public static int SlotOf(string locale)
    {
        var slot = Array.IndexOf(Slots, locale == "enGB" ? "enUS" : locale);
        return slot >= 0 ? slot : throw new ArgumentException($"Unknown locale: {locale}", nameof(locale));
    }
}

/// <summary>A row of DBFilesClient\Map.dbc.</summary>
public sealed record MapRecord(int Id, string Directory, string Name)
{
    public const string FilePath = @"DBFilesClient\Map.dbc";

    private const int IdField = 0;
    private const int DirectoryField = 1;
    private const int NameField = 5;

    public static IReadOnlyList<MapRecord> ReadAll(DbcFile dbc, string locale)
    {
        var slot = DbcLocale.SlotOf(locale);
        return Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new MapRecord(
                dbc.GetInt32(i, IdField),
                dbc.GetString(i, DirectoryField),
                dbc.GetString(i, NameField + slot)))
            .ToList();
    }

    public static IReadOnlyList<MapRecord> ReadAll(MpqAssetSource source) =>
        DbcTables.Read(source, FilePath, dbc => ReadAll(dbc, source.Locale));
}

/// <summary>A row of DBFilesClient\AreaTable.dbc.</summary>
public sealed record AreaTableRecord(int Id, int MapId, int ParentAreaId, string Name)
{
    public const string FilePath = @"DBFilesClient\AreaTable.dbc";

    private const int IdField = 0;
    private const int MapIdField = 1;
    private const int ParentAreaIdField = 2;
    private const int NameField = 11;

    public static IReadOnlyList<AreaTableRecord> ReadAll(DbcFile dbc, string locale)
    {
        var slot = DbcLocale.SlotOf(locale);
        return Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new AreaTableRecord(
                dbc.GetInt32(i, IdField),
                dbc.GetInt32(i, MapIdField),
                dbc.GetInt32(i, ParentAreaIdField),
                dbc.GetString(i, NameField + slot)))
            .ToList();
    }

    public static IReadOnlyList<AreaTableRecord> ReadAll(MpqAssetSource source) =>
        DbcTables.Read(source, FilePath, dbc => ReadAll(dbc, source.Locale));
}

internal static class DbcTables
{
    internal static T Read<T>(IAssetSource source, string path, Func<DbcFile, T> read)
    {
        var data = source.Read(path) ?? throw new FileNotFoundException($"Not in the client: {path}", path);
        using var dbc = DbcFile.Open(data);
        return read(dbc);
    }
}
