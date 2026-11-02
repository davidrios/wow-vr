using WowVR.Formats.Assets;
using WowVR.Formats.Database;

namespace WowVR.Formats.World;

/// <summary>A creature's sounds, from CreatureSoundData. Sound ids are SoundEntries rows, and 0 means none.</summary>
public sealed record CreatureSoundSet(int Footstep, int WingFlap, int Loop, int[] Fidgets, float FidgetDelayMin, float FidgetDelayMax);

public enum CreatureSoundKind
{
    Footstep,
    Fidget,
    WingFlap,
}

/// <summary>Reads what an M2 event means for a creature's sound. Others, such as $HIT, need combat.</summary>
public static class CreatureSoundEvents
{
    /// <summary>The sound an event asks for, with the fidget's index for a fidget. Null for any other event.</summary>
    public static (CreatureSoundKind Kind, int Index)? Parse(string id)
    {
        if (id.Length != 4 || id[0] != '$')
        {
            return null;
        }

        var name = id[1..3];
        if (name is "FL" or "FR" or "RL" or "RR" or "BL" or "BR" && char.IsDigit(id[3]))
        {
            return (CreatureSoundKind.Footstep, 0);
        }

        if (name == "FD" && id[3] is >= '1' and <= '5')
        {
            return (CreatureSoundKind.Fidget, id[3] - '1');
        }

        return id == "$WNG" ? (CreatureSoundKind.WingFlap, 0) : null;
    }
}

/// <summary>
/// Which sound a footstep makes on which ground, from a terrain layer's effect through
/// GroundEffectTexture, TerrainType and FootstepTerrainLookup.
/// </summary>
public sealed class FootstepTable
{
    private const int DefaultTerrainSound = 1;

    private readonly Dictionary<uint, int> _terrainTypeOfEffect = [];
    private readonly Dictionary<int, int> _soundOfTerrainType = [];
    private readonly Dictionary<(int Creature, int TerrainSound), int> _sounds = [];

    private FootstepTable()
    {
    }

    public static FootstepTable Read(IAssetSource source)
    {
        var table = new FootstepTable();
        DbcTables.Read<object?>(source, @"DBFilesClient\GroundEffectTexture.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._terrainTypeOfEffect[dbc.GetUInt32(i, 0)] = dbc.GetInt32(i, 10);
            }

            return null;
        });
        DbcTables.Read<object?>(source, @"DBFilesClient\TerrainType.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._soundOfTerrainType[dbc.GetInt32(i, 0)] = dbc.GetInt32(i, 4);
            }

            return null;
        });
        DbcTables.Read<object?>(source, @"DBFilesClient\FootstepTerrainLookup.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._sounds[(dbc.GetInt32(i, 1), dbc.GetInt32(i, 2))] = dbc.GetInt32(i, 3);
            }

            return null;
        });
        return table;
    }

    /// <summary>The SoundEntries row for a creature's footstep on a layer's effect, or 0. Unknown ground sounds like the first type.</summary>
    public int SoundFor(int creatureFootstep, uint effect)
    {
        var terrainSound = _terrainTypeOfEffect.TryGetValue(effect, out var type) && _soundOfTerrainType.TryGetValue(type, out var sound) && sound != 0
            ? sound
            : DefaultTerrainSound;
        return _sounds.GetValueOrDefault((creatureFootstep, terrainSound));
    }
}
