using WowVR.Formats.Assets;
using WowVR.Formats.Database;

namespace WowVR.Formats.World;

/// <summary>A sound with the files it picks between. Paths are ready to read from the client.</summary>
public sealed record SoundChoice(IReadOnlyList<(string Path, int Weight)> Files, float Volume)
{
    /// <summary>Picks a file by its weight. <paramref name="roll"/> runs from 0 to 1.</summary>
    public string Pick(double roll)
    {
        var total = Files.Sum(f => Math.Max(f.Weight, 1));
        var left = roll * total;
        foreach (var (path, weight) in Files)
        {
            left -= Math.Max(weight, 1);
            if (left < 0)
            {
                return path;
            }
        }

        return Files[^1].Path;
    }
}

/// <summary>What plays in a zone, for day or night.</summary>
public sealed record ZoneMusic(SoundChoice? Day, SoundChoice? Night, int SilenceMinMs, int SilenceMaxMs);

/// <summary>What an area shows and plays, with anything it lacks taken from its parents.</summary>
public sealed record ZoneInfo(int AreaId, string Name, ZoneMusic? Music, SoundChoice? AmbienceDay, SoundChoice? AmbienceNight);

/// <summary>Looks up areas from AreaTable, with their sounds from ZoneMusic, SoundAmbience and SoundEntries.</summary>
public sealed class ZoneTable
{
    private readonly Dictionary<int, AreaTableRecord> _areas;
    private readonly Dictionary<int, ZoneMusicRecord> _music;
    private readonly Dictionary<int, SoundAmbienceRecord> _ambience;
    private readonly Dictionary<int, SoundEntryRecord> _sounds;

    /// <summary>One object per sound, so areas sharing music can tell that it's the same music.</summary>
    private readonly Dictionary<int, SoundChoice?> _choices = [];

    private ZoneTable(
        IEnumerable<AreaTableRecord> areas,
        IEnumerable<ZoneMusicRecord> music,
        IEnumerable<SoundAmbienceRecord> ambience,
        IEnumerable<SoundEntryRecord> sounds)
    {
        _areas = areas.ToDictionary(a => a.Id);
        _music = music.ToDictionary(m => m.Id);
        _ambience = ambience.ToDictionary(a => a.Id);
        _sounds = sounds.ToDictionary(s => s.Id);
    }

    public static ZoneTable Read(MpqAssetSource source) => new(
        AreaTableRecord.ReadAll(source),
        ZoneMusicRecord.ReadAll(source),
        SoundAmbienceRecord.ReadAll(source),
        SoundEntryRecord.ReadAll(source));

    /// <summary>The area's name and sounds, or null if the table doesn't have it.</summary>
    public ZoneInfo? Find(int areaId)
    {
        if (!_areas.TryGetValue(areaId, out var area))
        {
            return null;
        }

        var musicId = Inherited(area, a => a.ZoneMusicId);
        var ambienceId = Inherited(area, a => a.AmbienceId);

        ZoneMusic? music = null;
        if (_music.TryGetValue(musicId, out var musicRow))
        {
            music = new ZoneMusic(Sound(musicRow.DaySoundId), Sound(musicRow.NightSoundId), musicRow.SilenceMinMs, musicRow.SilenceMaxMs);
        }

        _ambience.TryGetValue(ambienceId, out var ambienceRow);
        return new ZoneInfo(
            area.Id,
            area.Name,
            music,
            ambienceRow is null ? null : Sound(ambienceRow.DaySoundId),
            ambienceRow is null ? null : Sound(ambienceRow.NightSoundId));
    }

    /// <summary>Subzones often leave their sounds at 0, which means they use the parent zone's.</summary>
    private int Inherited(AreaTableRecord area, Func<AreaTableRecord, int> field)
    {
        var current = area;
        for (var depth = 0; depth < 8; depth++)
        {
            var value = field(current);
            if (value != 0 || !_areas.TryGetValue(current.ParentAreaId, out var parent))
            {
                return value;
            }

            current = parent;
        }

        return 0;
    }

    private SoundChoice? Sound(int soundId)
    {
        if (_choices.TryGetValue(soundId, out var known))
        {
            return known;
        }

        SoundChoice? choice = null;
        if (_sounds.TryGetValue(soundId, out var sound) && sound.Files.Count > 0)
        {
            choice = new SoundChoice(
                sound.Files.Select(f => ($@"{sound.Directory}\{f.Name}", f.Weight)).ToList(),
                sound.Volume);
        }

        _choices[soundId] = choice;
        return choice;
    }
}
