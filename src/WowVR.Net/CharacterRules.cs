namespace WowVR.Net;

public sealed record RaceInfo(byte Id, string Name, bool Horde, byte[] Classes);

public sealed record ClassInfo(byte Id, string Name);

/// <summary>3.3.5a's playable races and classes and which go together. The server checks them again.</summary>
public static class CharacterRules
{
    public const byte DeathKnight = 6;

    public static readonly ClassInfo[] Classes =
    [
        new(1, "Warrior"), new(2, "Paladin"), new(3, "Hunter"), new(4, "Rogue"), new(5, "Priest"),
        new(6, "Death Knight"), new(7, "Shaman"), new(8, "Mage"), new(9, "Warlock"), new(11, "Druid"),
    ];

    public static readonly RaceInfo[] Races =
    [
        new(1, "Human", false, [1, 2, 4, 5, 6, 8, 9]),
        new(3, "Dwarf", false, [1, 2, 3, 4, 5, 6]),
        new(4, "Night Elf", false, [1, 3, 4, 5, 6, 11]),
        new(7, "Gnome", false, [1, 4, 6, 8, 9]),
        new(11, "Draenei", false, [1, 2, 3, 5, 6, 7, 8]),
        new(2, "Orc", true, [1, 3, 4, 6, 7, 9]),
        new(5, "Undead", true, [1, 4, 5, 6, 8, 9]),
        new(6, "Tauren", true, [1, 3, 6, 7, 11]),
        new(8, "Troll", true, [1, 3, 4, 5, 6, 7, 8]),
        new(10, "Blood Elf", true, [2, 3, 4, 5, 6, 8, 9]),
    ];

    public static RaceInfo? Race(byte id) => Races.FirstOrDefault(r => r.Id == id);

    public static string RaceName(byte id) => Race(id)?.Name ?? $"Race {id}";

    public static string ClassName(byte id) => Classes.FirstOrDefault(c => c.Id == id)?.Name ?? $"Class {id}";

    /// <summary>Letters only, 2 to 12 of them, capitalized as the server will store it.</summary>
    public static string? NameProblem(string name) =>
        name.Length < 2 ? "Names are at least 2 letters."
        : name.Length > 12 ? "Names are at most 12 letters."
        : !name.All(char.IsLetter) ? "Names use letters only."
        : null;

    public static string Capitalized(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
}
