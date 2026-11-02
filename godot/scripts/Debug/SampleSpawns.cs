using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using WowVR.World;

namespace WowVR.Debug;

/// <summary>
/// Made-up creatures around a point, in rows north of it, for testing without a spawn export.
/// One of each kind stands, wanders and patrols, then they repeat. Displays are looked up
/// in the player's client by the kind of model.
/// </summary>
public sealed class SampleSpawns : ISpawnSource
{
    public const int DefaultCount = 12;

    public const string StandingName = "Sample standing";
    public const string WanderingName = "Sample wandering";
    public const string PatrollingName = "Sample patrolling";

    private const float RowSpacing = 3.5f;
    private const int PerRow = 4;
    private const float NorthOfStart = 5f;
    private const float WanderRadius = 8f;
    private const float PatrolSize = 10f;

    private readonly List<CreatureSpawn> _spawns = [];

    public SampleSpawns(CreatureDisplayTable displays, float startX, float startY, int count)
    {
        var kinds = new[]
        {
            displays.ArmoredCharacters(@"Human\Male").FirstOrDefault(),
            displays.DisplaysOfModel("Kobold").FirstOrDefault(),
            displays.DisplaysOfModel(@"Creature\Wolf").FirstOrDefault(),
            displays.ArmoredCharacters(@"Human\Female").FirstOrDefault(),
        }.Where(d => d != 0).ToArray();

        for (var i = 0; i < count && kinds.Length > 0; i++)
        {
            var row = i / PerRow;
            var x = startX + NorthOfStart + row * RowSpacing;
            var y = startY + (i % PerRow - (PerRow - 1) / 2f) * RowSpacing;
            var kind = (CreatureMovement)(i % 3);
            _spawns.Add(new CreatureSpawn
            {
                Guid = i + 1,
                DisplayId = kinds[i % kinds.Length],
                Name = kind switch
                {
                    CreatureMovement.Wander => WanderingName,
                    CreatureMovement.Waypoints => PatrollingName,
                    _ => StandingName,
                },
                Pos = [x, y, 0f],

                // Facing the start, to the south.
                Orientation = System.MathF.PI,
                MovementType = kind,
                WanderRadius = kind == CreatureMovement.Wander ? WanderRadius : 0f,
            });
        }
    }

    public IReadOnlyList<CreatureSpawn> Read(int tileX, int tileY, TerrainHeights heights)
    {
        var onTile = new List<CreatureSpawn>();
        foreach (var spawn in _spawns.Where(s => WowCoords.TileAt(s.X, s.Y) == (tileX, tileY)))
        {
            var placed = spawn with { Pos = [spawn.X, spawn.Y, heights.HeightAt(spawn.X, spawn.Y)] };
            if (spawn.MovementType == CreatureMovement.Waypoints)
            {
                placed = placed with { Waypoints = Patrol(spawn, heights) };
            }

            onTile.Add(placed);
        }

        return onTile;
    }

    private static List<CreatureWaypoint> Patrol(CreatureSpawn spawn, TerrainHeights heights)
    {
        var corners = new[] { (0f, 0f), (PatrolSize, 0f), (PatrolSize, PatrolSize), (0f, PatrolSize) };
        return corners
            .Select(c => (X: spawn.X + c.Item1, Y: spawn.Y + c.Item2))
            .Select(p => new CreatureWaypoint(p.X, p.Y, heights.HeightAt(p.X, p.Y), 1000))
            .ToList();
    }
}
