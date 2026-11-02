using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Models;

namespace WowVR.Formats.World;

/// <summary>A placed object, such as a mailbox or signpost: one row of tools/spawn-exporter's object files.</summary>
public sealed record GameObjectSpawn
{
    [JsonPropertyName("guid")]
    public required long Guid { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("displayId")]
    public required int DisplayId { get; init; }

    /// <summary>X, Y and Z.</summary>
    [JsonPropertyName("pos")]
    public required float[] Pos { get; init; }

    /// <summary>Radians, turning left from north. Only used when there's no <see cref="Rotation"/>.</summary>
    [JsonPropertyName("orientation")]
    public float Orientation { get; init; }

    /// <summary>A quaternion in WoW's axes, as X, Y, Z and W. All zeros when it isn't given.</summary>
    [JsonPropertyName("rotation")]
    public float[] Rotation { get; init; } = [];

    [JsonPropertyName("scale")]
    public float Scale { get; init; } = 1f;

    public static IReadOnlyList<GameObjectSpawn> ParseFile(string json) =>
        JsonSerializer.Deserialize<List<GameObjectSpawn>>(json)?.Where(s => s.Pos is { Length: 3 }).ToList() ?? [];

    /// <summary>Where a tile's object file is, inside the spawn folder.</summary>
    public static string FilePath(string folder, int mapId, int tileX, int tileY) =>
        Path.Combine(folder, mapId.ToString(System.Globalization.CultureInfo.InvariantCulture), "objects", $"{tileX}_{tileY}.json");

    /// <summary>Where it goes on its tile, relative to the tile's corner, in Godot axes.</summary>
    public PlacementTransform Placement(int tileX, int tileY)
    {
        var rotation = Rotation is { Length: 4 } r && (r[0] != 0f || r[1] != 0f || r[2] != 0f || r[3] != 0f)
            ? Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3]))
            : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Orientation);

        // A model's axes in Godot are (x, z, -y) of its file's, and the world's are (-y, z, -x).
        static Vector3 ToWorld(Vector3 wow) => new(-wow.Y, wow.Z, -wow.X);
        var scale = Scale > 0f ? Scale : 1f;
        return new PlacementTransform(
            ToWorld(Vector3.Transform(Vector3.UnitX, rotation)) * scale,
            ToWorld(Vector3.Transform(Vector3.UnitZ, rotation)) * scale,
            ToWorld(Vector3.Transform(-Vector3.UnitY, rotation)) * scale,
            WowCoords.TileLocal(Pos[0], Pos[1], Pos[2], tileX, tileY));
    }
}

/// <summary>Where a tile's objects come from.</summary>
public interface IGameObjectSource
{
    IReadOnlyList<GameObjectSpawn> Read(int tileX, int tileY);
}

/// <summary>The files tools/spawn-exporter writes. A tile without one simply has no objects.</summary>
public sealed class GameObjectFiles(string folder, int mapId) : IGameObjectSource
{
    public IReadOnlyList<GameObjectSpawn> Read(int tileX, int tileY)
    {
        var path = GameObjectSpawn.FilePath(folder, mapId, tileX, tileY);
        return File.Exists(path) ? GameObjectSpawn.ParseFile(File.ReadAllText(path)) : [];
    }
}

/// <summary>For when no objects are wanted.</summary>
public sealed class NoGameObjects : IGameObjectSource
{
    public IReadOnlyList<GameObjectSpawn> Read(int tileX, int tileY) => [];
}

/// <summary>Each display's model, from GameObjectDisplayInfo.</summary>
public sealed class GameObjectDisplayTable
{
    private const int ModelField = 1;

    private readonly Dictionary<int, string> _models = [];

    private GameObjectDisplayTable()
    {
    }

    public static GameObjectDisplayTable Read(IAssetSource source)
    {
        var table = new GameObjectDisplayTable();
        DbcTables.Read<object?>(source, @"DBFilesClient\GameObjectDisplayInfo.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var model = dbc.GetString(i, ModelField);
                if (model.Length > 0)
                {
                    table._models[dbc.GetInt32(i, 0)] = model;
                }
            }

            return null;
        });
        return table;
    }

    /// <summary>The file's path, as an M2 path unless it's a WMO. Null for an unknown display.</summary>
    public string? ModelOf(int displayId) => _models.TryGetValue(displayId, out var model)
        ? model.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase) ? model : M2Model.NormalizePath(model)
        : null;
}
