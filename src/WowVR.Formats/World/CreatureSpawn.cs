using System.Text.Json;
using System.Text.Json.Serialization;

namespace WowVR.Formats.World;

public enum CreatureMovement
{
    Idle = 0,
    Wander = 1,
    Waypoints = 2,
}

/// <summary>A point on a creature's path. The delay is how long it waits there.</summary>
public sealed record CreatureWaypoint(
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("z")] float Z,
    [property: JsonPropertyName("delayMs")] int DelayMs = 0);

/// <summary>
/// One creature's spawn, in WoW coordinates. This is the layout of a spawn file's rows,
/// and what tools/spawn-exporter has to write: see <c>docs/decisions/0007-creatures.md</c>.
/// </summary>
public sealed record CreatureSpawn
{
    [JsonPropertyName("guid")]
    public required long Guid { get; init; }

    [JsonPropertyName("entry")]
    public int Entry { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("displayId")]
    public required int DisplayId { get; init; }

    /// <summary>X, Y and Z.</summary>
    [JsonPropertyName("pos")]
    public required float[] Pos { get; init; }

    /// <summary>Radians, turning left from north.</summary>
    [JsonPropertyName("orientation")]
    public float Orientation { get; init; }

    [JsonPropertyName("wanderRadius")]
    public float WanderRadius { get; init; }

    [JsonPropertyName("movementType")]
    public CreatureMovement MovementType { get; init; }

    [JsonPropertyName("waypoints")]
    public IReadOnlyList<CreatureWaypoint> Waypoints { get; init; } = [];

    /// <summary>The creature's own scale, on top of its display's.</summary>
    [JsonPropertyName("scale")]
    public float Scale { get; init; } = 1f;

    /// <summary>Yards per second.</summary>
    [JsonPropertyName("walkSpeed")]
    public float WalkSpeed { get; init; } = CreatureMotion.WalkSpeed;

    /// <summary>The ItemDisplayInfo rows of its right and left hands, with 0 for an empty hand.</summary>
    [JsonPropertyName("weapons")]
    public int[] Weapons { get; init; } = [];

    /// <summary>Another player's looks, which it's drawn from instead of <see cref="DisplayId"/>. Only a server gives one.</summary>
    [JsonIgnore]
    public CharacterAppearance? Character { get; init; }

    public float X => Pos[0];

    public float Y => Pos[1];

    public float Z => Pos[2];

    /// <summary>The rows of one tile's spawn file.</summary>
    public static IReadOnlyList<CreatureSpawn> ParseFile(string json) =>
        JsonSerializer.Deserialize<List<CreatureSpawn>>(json)?.Where(s => s.Pos is { Length: 3 }).ToList() ?? [];

    /// <summary>Where a tile's spawn file is, inside the spawn folder.</summary>
    public static string FilePath(string folder, int mapId, int tileX, int tileY) =>
        Path.Combine(folder, mapId.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{tileX}_{tileY}.json");
}
