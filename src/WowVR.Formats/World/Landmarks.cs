namespace WowVR.Formats.World;

/// <summary>A place to teleport to. Height comes from the terrain on arrival.</summary>
public sealed record Landmark(string Map, string Name, string Area, float WowX, float WowY);

/// <summary>A map to travel to, by its directory in the client and a short name for a button.</summary>
public sealed record Continent(string Map, string Name);

public static class Landmarks
{
    public static readonly IReadOnlyList<Continent> Continents =
    [
        new("Azeroth", "E. Kingdoms"),
        new("Kalimdor", "Kalimdor"),
        new("Expansion01", "Outland"),
        new("Northrend", "Northrend"),
    ];

    /// <summary>Positions are the centers of each area's terrain, from <c>inspect area</c>.</summary>
    public static readonly IReadOnlyList<Landmark> All =
    [
        new("Azeroth", "Goldshire", "Goldshire", -9464, 62),
        new("Azeroth", "Northshire", "Northshire Valley", -8914, -133),
        new("Azeroth", "Crystal Lake", "Crystal Lake", -9409, -328),
        new("Azeroth", "Stormwind", "Stormwind City", -8651, 734),
        new("Azeroth", "Lakeshire", "Lakeshire", -9221, -2181),
        new("Azeroth", "Darkshire", "Darkshire", -10634, -1192),
        new("Azeroth", "Kharanos", "Kharanos", -5578, -502),
        new("Azeroth", "Booty Bay", "Booty Bay", -14366, 507),
        new("Azeroth", "Brill", "Brill", 2326, 354),
        new("Kalimdor", "Orgrimmar", "Orgrimmar", 1841, -4433),
        new("Kalimdor", "Razor Hill", "Razor Hill", 324, -4774),
        new("Kalimdor", "Thunder Bluff", "Thunder Bluff", -1180, 28),
        new("Kalimdor", "Bloodhoof Village", "Bloodhoof Village", -2303, -409),
        new("Kalimdor", "Ratchet", "Ratchet", -956, -3655),
        new("Kalimdor", "Darnassus", "Darnassus", 9947, 2325),
        new("Expansion01", "Shattrath", "Shattrath City", -1827, 5399),
        new("Expansion01", "Honor Hold", "Honor Hold", -719, 2680),
        new("Expansion01", "Thrallmar", "Thrallmar", 172, 2721),
        new("Northrend", "Valiance Keep", "Valiance Keep", 2242, 5262),
        new("Northrend", "Warsong Hold", "Warsong Hold", 2820, 6164),
        new("Northrend", "Sholazar Basin", "Sholazar Basin", 5631, 4964),
    ];

    public static IEnumerable<Landmark> On(string map) =>
        All.Where(l => l.Map.Equals(map, StringComparison.OrdinalIgnoreCase));
}
