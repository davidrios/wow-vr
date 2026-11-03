using WowVR.Formats.Assets;

namespace WowVR.Formats.Database;

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
public sealed record MapRecord(int Id, string Directory, string Name, int LoadingScreenId = 0)
{
    public const string FilePath = @"DBFilesClient\Map.dbc";

    private const int IdField = 0;
    private const int DirectoryField = 1;
    private const int NameField = 5;
    private const int LoadingScreenField = 57;

    public static IReadOnlyList<MapRecord> ReadAll(DbcFile dbc, string locale)
    {
        var slot = DbcLocale.SlotOf(locale);
        return Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new MapRecord(
                dbc.GetInt32(i, IdField),
                dbc.GetString(i, DirectoryField),
                dbc.GetString(i, NameField + slot),
                dbc.GetInt32(i, LoadingScreenField)))
            .ToList();
    }

    public static IReadOnlyList<MapRecord> ReadAll(MpqAssetSource source) =>
        DbcTables.Read(source, FilePath, dbc => ReadAll(dbc, source.Locale));
}

/// <summary>A row of DBFilesClient\LoadingScreens.dbc: the picture a map shows while it loads.</summary>
public sealed record LoadingScreenRecord(int Id, string Path)
{
    public const string FilePath = @"DBFilesClient\LoadingScreens.dbc";

    private const int IdField = 0;
    private const int PathField = 2;

    public static IReadOnlyList<LoadingScreenRecord> ReadAll(DbcFile dbc) =>
        Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new LoadingScreenRecord(dbc.GetInt32(i, IdField), dbc.GetString(i, PathField)))
            .ToList();

    public static IReadOnlyList<LoadingScreenRecord> ReadAll(MpqAssetSource source) =>
        DbcTables.Read(source, FilePath, ReadAll);
}

/// <summary>A row of DBFilesClient\AreaTable.dbc.</summary>
public sealed record AreaTableRecord(int Id, int MapId, int ParentAreaId, string Name, int AmbienceId = 0, int ZoneMusicId = 0)
{
    public const string FilePath = @"DBFilesClient\AreaTable.dbc";

    private const int IdField = 0;
    private const int MapIdField = 1;
    private const int ParentAreaIdField = 2;
    private const int AmbienceIdField = 7;
    private const int ZoneMusicIdField = 8;
    private const int NameField = 11;

    public static IReadOnlyList<AreaTableRecord> ReadAll(DbcFile dbc, string locale)
    {
        var slot = DbcLocale.SlotOf(locale);
        return Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new AreaTableRecord(
                dbc.GetInt32(i, IdField),
                dbc.GetInt32(i, MapIdField),
                dbc.GetInt32(i, ParentAreaIdField),
                dbc.GetString(i, NameField + slot),
                dbc.GetInt32(i, AmbienceIdField),
                dbc.GetInt32(i, ZoneMusicIdField)))
            .ToList();
    }

    public static IReadOnlyList<AreaTableRecord> ReadAll(MpqAssetSource source) =>
        DbcTables.Read(source, FilePath, dbc => ReadAll(dbc, source.Locale));
}

/// <summary>The basic kinds of liquid, from LiquidType's type field.</summary>
public enum LiquidKind
{
    Water = 0,
    Ocean = 1,
    Magma = 2,
    Slime = 3,
}

/// <summary>A row of DBFilesClient\LiquidType.dbc.</summary>
public sealed record LiquidTypeRecord(int Id, string Name, LiquidKind Kind, string TexturePattern)
{
    public const string FilePath = @"DBFilesClient\LiquidType.dbc";

    private const int NameField = 1;
    private const int KindField = 3;
    private const int ShaderField = 14;
    private const int TextureField = 15;

    /// <summary>This shader names procedural textures rather than frames, so Noggit draws lake water instead.</summary>
    private const int ProceduralShader = 3;
    private const string ProceduralFallback = @"XTextures\river\lake_a.%d.blp";

    public static IReadOnlyList<LiquidTypeRecord> ReadAll(DbcFile dbc) =>
        Enumerable.Range(0, dbc.RecordCount)
            .Select(i => new LiquidTypeRecord(
                dbc.GetInt32(i, 0),
                dbc.GetString(i, NameField),
                (LiquidKind)dbc.GetInt32(i, KindField),
                dbc.GetInt32(i, ShaderField) == ProceduralShader ? ProceduralFallback : dbc.GetString(i, TextureField)))
            .ToList();

    public static IReadOnlyList<LiquidTypeRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read(source, FilePath, ReadAll);
}

/// <summary>A row of DBFilesClient\ZoneMusic.dbc.</summary>
public sealed record ZoneMusicRecord(int Id, int SilenceMinMs, int SilenceMaxMs, int DaySoundId, int NightSoundId)
{
    public static IReadOnlyList<ZoneMusicRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read<IReadOnlyList<ZoneMusicRecord>>(source, @"DBFilesClient\ZoneMusic.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount)
                .Select(i => new ZoneMusicRecord(
                    dbc.GetInt32(i, 0),
                    dbc.GetInt32(i, 2),
                    dbc.GetInt32(i, 4),
                    dbc.GetInt32(i, 6),
                    dbc.GetInt32(i, 7)))
                .ToList());
}

/// <summary>A row of DBFilesClient\SoundAmbience.dbc.</summary>
public sealed record SoundAmbienceRecord(int Id, int DaySoundId, int NightSoundId)
{
    public static IReadOnlyList<SoundAmbienceRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read<IReadOnlyList<SoundAmbienceRecord>>(source, @"DBFilesClient\SoundAmbience.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount)
                .Select(i => new SoundAmbienceRecord(dbc.GetInt32(i, 0), dbc.GetInt32(i, 1), dbc.GetInt32(i, 2)))
                .ToList());
}

/// <summary>A row of DBFilesClient\SoundEntries.dbc. A sound picks one of its files by weight.</summary>
/// <param name="MinDistance">Full volume this close, in yards.</param>
/// <param name="Cutoff">Silent past this, in yards.</param>
public sealed record SoundEntryRecord(int Id, IReadOnlyList<(string Name, int Weight)> Files, string Directory, float Volume, float MinDistance, float Cutoff)
{
    private const int FileField = 3;
    private const int WeightField = 13;
    private const int MaxFiles = 10;
    private const int DirectoryField = 23;
    private const int VolumeField = 24;
    private const int MinDistanceField = 26;
    private const int CutoffField = 27;

    public static IReadOnlyList<SoundEntryRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read<IReadOnlyList<SoundEntryRecord>>(source, @"DBFilesClient\SoundEntries.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount)
                .Select(i => new SoundEntryRecord(
                    dbc.GetInt32(i, 0),
                    Enumerable.Range(0, MaxFiles)
                        .Select(f => (Name: dbc.GetString(i, FileField + f), Weight: dbc.GetInt32(i, WeightField + f)))
                        .Where(f => f.Name.Length > 0)
                        .ToList(),
                    // A few rows start or end the folder with a backslash, which would double up when joined to a file name.
                    dbc.GetString(i, DirectoryField).Trim('\\', '/'),
                    dbc.GetSingle(i, VolumeField),
                    dbc.GetSingle(i, MinDistanceField),
                    dbc.GetSingle(i, CutoffField)))
                .ToList());
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
