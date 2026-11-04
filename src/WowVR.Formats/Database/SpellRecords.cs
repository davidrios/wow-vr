using WowVR.Formats.Assets;

namespace WowVR.Formats.Database;

/// <summary>Who a spell's first effects aim at, as far as casting it from a button needs to know.</summary>
public enum SpellAim
{
    /// <summary>The caster, or what's around them: nothing to send.</summary>
    Self,

    /// <summary>An enemy, which must be selected.</summary>
    Enemy,

    /// <summary>A friend, the caster when nothing friendly is selected.</summary>
    Friend,

    /// <summary>Anyone selected, or the caster.</summary>
    Any,

    /// <summary>A place on the ground.</summary>
    Ground,
}

/// <summary>
/// One row of Spell.dbc, with what the spellbook, the action bar and casting need: its name and
/// rank, its icon, how long it takes and how long until it can be cast again, and what it aims at.
/// </summary>
public sealed record SpellRecord(
    int Id,
    string Name,
    string Rank,
    string Description,
    string? IconPath,
    int CastTimeMs,
    int RecoveryMs,
    int Category,
    int CategoryRecoveryMs,
    int GlobalCooldownMs,
    int GlobalCooldownCategory,
    uint Attributes,
    int PowerType,
    int PowerCost,
    SpellAim Aim)
{
    /// <summary>Always on, never cast: the spellbook shows it, but it can't go on a button.</summary>
    public bool IsPassive => (Attributes & 0x40) != 0;

    /// <summary>The client never shows it in the spellbook.</summary>
    public bool IsHidden => (Attributes & 0x80) != 0;

    /// <summary>The shapeshift forms it can be cast in, as a mask of 1 &lt;&lt; (form - 1).</summary>
    public uint Forms { get; init; }

    /// <summary>The second set of attribute flags, of which 0x80000 lets a spell with forms be cast without one.</summary>
    public uint Attributes2 { get; init; }

    /// <summary>How far its area reaches from where it lands, in yards: its first effect's with one, or 0.</summary>
    public float Radius { get; init; }

    /// <summary>How far away it can be cast, in yards, at an enemy; 0 for the caster only.</summary>
    public float Range { get; init; }

    /// <summary>Only castable in one of its forms, as AzerothCore's CheckShapeshift has it for the unshifted.</summary>
    public bool NeedsForm => Forms != 0 && (Attributes2 & 0x80000) == 0;

    /// <summary>"Fireball" or "Fireball (Rank 3)".</summary>
    public string FullName => Rank.Length > 0 ? $"{Name} ({Rank})" : Name;
}

/// <summary>A spellbook tab, as a skill line: a class's trees each get one, and everything else is General.</summary>
public sealed record SpellTab(int SkillLine, string Name, string? IconPath);

/// <summary>Spell.dbc and the tables around it, read once, for the spellbook and casting.</summary>
public sealed class SpellTable
{
    /// <summary>Spell.dbc's columns in 3.3.5a, where each text has 16 locale slots and a flags field.</summary>
    private const int CategoryField = 1;
    private const int AttributesField = 4;
    private const int CastTimeField = 28;
    private const int RecoveryField = 29;
    private const int CategoryRecoveryField = 30;
    private const int PowerTypeField = 41;
    private const int PowerCostField = 42;
    private const int EffectField = 71;
    private const int TargetAField = 86;
    private const int IconField = 133;
    private const int NameField = 136;
    private const int RankField = 153;
    private const int DescriptionField = 170;
    private const int GlobalCategoryField = 205;
    private const int GlobalCooldownField = 206;
    private const int TargetsField = 16;
    private const int FormsField = 12;
    private const int Attributes2Field = 6;
    private const int RangeField = 46;
    private const int RadiusField = 92;

    /// <summary>SkillLine.dbc's columns: its category, then its name.</summary>
    private const int SkillCategoryField = 1;
    private const int SkillNameField = 3;
    private const int SkillIconField = 37;

    /// <summary>SkillLine categories: class talent trees and their spells.</summary>
    private const int ClassSkillCategory = 7;

    /// <summary>Spell.dbc's target flag for a place on the ground.</summary>
    private const uint DestinationTarget = 0x40;

    private readonly Dictionary<int, SpellRecord> _spells = [];
    private readonly Dictionary<int, SpellTab> _classTabs = [];
    private readonly Dictionary<int, int> _skillOfSpell = [];
    private readonly HashSet<int> _bookSpells = [];

    private SpellTable()
    {
    }

    public int Count => _spells.Count;

    public SpellRecord? Get(int id) => _spells.GetValueOrDefault(id);

    /// <summary>
    /// Whether the player's spellbook lists it: it's taught by a skill line, as the client's book
    /// is built, and not only a pet's. Auras and effects the player also "knows" aren't.
    /// </summary>
    public bool InSpellbook(int spell) => _bookSpells.Contains(spell);

    /// <summary>The class tab a spell belongs on, or null for General.</summary>
    public SpellTab? TabOf(int spell) =>
        _skillOfSpell.TryGetValue(spell, out var skill) && _classTabs.TryGetValue(skill, out var tab) ? tab : null;

    public static SpellTable Read(MpqAssetSource source) => Read(source, source.Locale);

    public static SpellTable Read(IAssetSource source, string locale)
    {
        var slot = DbcLocale.SlotOf(locale);
        var table = new SpellTable();

        var icons = DbcTables.Read(source, @"DBFilesClient\SpellIcon.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => dbc.GetInt32(i, 0), i => dbc.GetString(i, 1)));
        string? IconPath(int id) => icons.TryGetValue(id, out var path) && path.Length > 0 ? path + ".blp" : null;

        // SpellRadius: an id, then the radius in yards. SpellRange: an id, the least ranges, then the most at an enemy.
        var radii = DbcTables.Read(source, @"DBFilesClient\SpellRadius.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => dbc.GetInt32(i, 0), i => dbc.GetSingle(i, 1)));
        var ranges = DbcTables.Read(source, @"DBFilesClient\SpellRange.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => dbc.GetInt32(i, 0), i => dbc.GetSingle(i, 3)));

        var castTimes = DbcTables.Read(source, @"DBFilesClient\SpellCastTimes.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => dbc.GetInt32(i, 0), i => dbc.GetInt32(i, 1)));

        DbcTables.Read<object?>(source, @"DBFilesClient\Spell.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var id = dbc.GetInt32(i, 0);
                table._spells[id] = new SpellRecord(
                    id,
                    dbc.GetString(i, NameField + slot),
                    dbc.GetString(i, RankField + slot),
                    dbc.GetString(i, DescriptionField + slot),
                    IconPath(dbc.GetInt32(i, IconField)),
                    castTimes.GetValueOrDefault(dbc.GetInt32(i, CastTimeField)),
                    dbc.GetInt32(i, RecoveryField),
                    dbc.GetInt32(i, CategoryField),
                    dbc.GetInt32(i, CategoryRecoveryField),
                    dbc.GetInt32(i, GlobalCooldownField),
                    dbc.GetInt32(i, GlobalCategoryField),
                    dbc.GetUInt32(i, AttributesField),
                    dbc.GetInt32(i, PowerTypeField),
                    dbc.GetInt32(i, PowerCostField),
                    AimOf(dbc.GetUInt32(i, TargetsField), [.. Enumerable.Range(0, 3).Select(e => (dbc.GetInt32(i, EffectField + e), dbc.GetInt32(i, TargetAField + e)))]))
                {
                    Forms = dbc.GetUInt32(i, FormsField),
                    Attributes2 = dbc.GetUInt32(i, Attributes2Field),
                    Radius = Enumerable.Range(0, 3).Select(e => radii.GetValueOrDefault(dbc.GetInt32(i, RadiusField + e))).FirstOrDefault(r => r > 0),
                    Range = ranges.GetValueOrDefault(dbc.GetInt32(i, RangeField)),
                };
            }

            return null;
        });

        // Pets' abilities are in the class category too, but belong in the pet's own book.
        var petSkills = DbcTables.Read(source, @"DBFilesClient\CreatureFamily.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).SelectMany(i => new[] { dbc.GetInt32(i, 5), dbc.GetInt32(i, 6) }).Where(s => s > 0).ToHashSet());

        DbcTables.Read<object?>(source, @"DBFilesClient\SkillLine.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var id = dbc.GetInt32(i, 0);
                if (dbc.GetInt32(i, SkillCategoryField) == ClassSkillCategory && !petSkills.Contains(id))
                {
                    table._classTabs[id] = new SpellTab(id, dbc.GetString(i, SkillNameField + slot), IconPath(dbc.GetInt32(i, SkillIconField)));
                }
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\SkillLineAbility.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var skill = dbc.GetInt32(i, 1);
                var spell = dbc.GetInt32(i, 2);
                if (petSkills.Contains(skill))
                {
                    continue;
                }

                table._bookSpells.Add(spell);
                if (table._classTabs.ContainsKey(skill) || !table._skillOfSpell.ContainsKey(spell))
                {
                    table._skillOfSpell[spell] = skill;
                }
            }

            return null;
        });

        return table;
    }

    /// <summary>
    /// What a spell aims at, from its effects' first implicit targets: the ones AzerothCore's
    /// SharedDefines names TARGET_UNIT_TARGET_ENEMY (6), _ALLY (21), _PARTY (57), _RAID (61) and
    /// _ANY (25), and the places of TARGET_DEST_DYNOBJ (28 to 30) and the ground target flag.
    /// </summary>
    public static SpellAim AimOf(uint targets, (int Effect, int Target)[] effects)
    {
        if ((targets & DestinationTarget) != 0)
        {
            return SpellAim.Ground;
        }

        var aims = effects.Where(e => e.Effect != 0).Select(e => e.Target).ToList();
        if (aims.Contains(6))
        {
            return SpellAim.Enemy;
        }

        if (aims.Any(t => t is 21 or 57 or 61))
        {
            return SpellAim.Friend;
        }

        if (aims.Contains(25))
        {
            return SpellAim.Any;
        }

        return aims.Any(t => t is 28 or 29 or 30 or 87) ? SpellAim.Ground : SpellAim.Self;
    }
}
