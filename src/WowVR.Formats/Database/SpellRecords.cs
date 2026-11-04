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

    /// <summary>How fast its missile flies, in yards a second; 0 when it lands at once.</summary>
    public float Speed { get; init; }

    /// <summary>Its SpellVisual row, for what casting it looks and sounds like; 0 for none.</summary>
    public int Visual { get; init; }

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

/// <summary>Where on a unit a kit's effect model goes, in SpellVisualKit's order.</summary>
public enum KitPlace
{
    Head,
    Chest,
    Base,
    LeftHand,
    RightHand,
    Breath,
    LeftWeapon,
    RightWeapon,
    Special1,
    Special2,
    Special3,
    World,
}

/// <summary>
/// One SpellVisualKit row: what a unit does at one moment of a spell. An animation (after a start
/// one, or -1 for none), effect models by <see cref="KitPlace"/> as <c>.m2</c> paths or null, and a sound.
/// </summary>
public sealed record SpellKit(int StartAnimation, int Animation, string?[] Effects, int Sound)
{
    /// <summary>The client's own procedural effects, by kind (-1 for none). Kind 9 rains area models: see <see cref="SpellTable.AreaRainOf"/>.</summary>
    public int[] Procs { get; init; } = [];

    /// <summary>Each proc's first and second parameters, SpellVisualKit's 21 to 24 and 25 to 28.</summary>
    public float[] ProcFirst { get; init; } = [];

    public float[] ProcSecond { get; init; } = [];

    /// <summary>Each effect's size as SpellVisualEffectName gives it, which an area's radius scales from.</summary>
    public float[] EffectSizes { get; init; } = [];

    public bool IsEmpty => StartAnimation < 0 && Animation <= 0 && Sound == 0 && Effects.All(e => e is null) && Procs.All(p => p < 0);
}

/// <summary>
/// A spell's look, from SpellVisual.dbc: the kits for the caster while it casts, when it casts,
/// and while it channels, and for what it hits; and the missile flying between, if any.
/// </summary>
public sealed record SpellVisual(SpellKit? Precast, SpellKit? Cast, SpellKit? Impact, SpellKit? Channel, string? Missile, int MissileSound)
{
    /// <summary>On each unit hit, when the impact kit isn't for them.</summary>
    public SpellKit? TargetImpact { get; init; }

    /// <summary>Where it lands, once, as a ground spell's tick.</summary>
    public SpellKit? InstantArea { get; init; }

    /// <summary>Where it lands, once, when it's cast.</summary>
    public SpellKit? ImpactArea { get; init; }

    /// <summary>On the ground for as long as the area lasts, as Consecration's glow.</summary>
    public SpellKit? PersistentArea { get; init; }
}

/// <summary>
/// A kit's rain of area models, as Rain of Fire's and Blizzard's: so many a second, each dropped
/// at a random spot within the area onto the ground below, playing once.
/// </summary>
public sealed record AreaRain(string Model, float PerSecond);

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
    private const int VisualField = 131;
    private const int SpeedField = 47;
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
    private readonly Dictionary<int, SpellVisual> _visuals = [];
    private readonly Dictionary<int, int> _animationFallbacks = [];

    private readonly List<string> _areaModels = [];

    /// <summary>The procedural effect that rains area models.</summary>
    public const int AreaRainProc = 9;

    /// <summary>
    /// A kit's rain, if one of its procs is <see cref="AreaRainProc"/>: its first parameter, rounded,
    /// picks SpellVisualKitAreaModel's row by position (the first row when it's out of range), and its
    /// second is how many a second at full detail.
    /// </summary>
    public AreaRain? AreaRainOf(SpellKit kit)
    {
        var proc = Array.IndexOf(kit.Procs, AreaRainProc);
        if (proc < 0 || _areaModels.Count == 0)
        {
            return null;
        }

        var row = (int)MathF.Round(kit.ProcFirst.ElementAtOrDefault(proc));
        return new AreaRain(_areaModels[row >= 0 && row < _areaModels.Count ? row : 0], kit.ProcSecond.ElementAtOrDefault(proc));
    }

    /// <summary>What a spell looks like being cast, or null for one with no visual.</summary>
    public SpellVisual? VisualOf(int spell) =>
        _spells.TryGetValue(spell, out var record) && _visuals.TryGetValue(record.Visual, out var visual) ? visual : null;

    /// <summary>
    /// The animation a model without this one plays instead, from AnimationData's fallback column,
    /// as ReadySpellDirected (51) falls back to ReadySpellOmni (52) and that to SpellPrecast (31). 0 at the end.
    /// </summary>
    public int AnimationFallback(int animation) => _animationFallbacks.GetValueOrDefault(animation);

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
                    Visual = dbc.GetInt32(i, VisualField),
                    Speed = dbc.GetSingle(i, SpeedField),
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

        ReadVisuals(source, table);
        return table;
    }

    /// <summary>
    /// SpellVisualEffectName (an id, a name, then the model file), SpellVisualKit (an id, the start
    /// and main animations, twelve effects by <see cref="KitPlace"/>, then the sound), SpellVisual (an id,
    /// then the precast, cast, impact, state, state-done and channel kits, a missile flag, its model and
    /// its sound at 11), and AnimationData (an id, a name, flags, then the fallback at 5).
    /// </summary>
    private static void ReadVisuals(IAssetSource source, SpellTable table)
    {
        var models = DbcTables.Read(source, @"DBFilesClient\SpellVisualEffectName.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(i => dbc.GetInt32(i, 0), i => (Path: ModelPath(dbc.GetString(i, 2)), Size: dbc.GetSingle(i, 3))));
        string? Model(int id) => models.TryGetValue(id, out var m) ? m.Path : null;
        float Size(int id) => models.TryGetValue(id, out var m) && m.Size > 0 ? m.Size : 1f;

        // An id, the start and main animations, twelve effects, the sound, a shake, then four procs.
        var kits = DbcTables.Read(source, @"DBFilesClient\SpellVisualKit.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).ToDictionary(
                i => dbc.GetInt32(i, 0),
                i => new SpellKit(
                    dbc.GetInt32(i, 1),
                    dbc.GetInt32(i, 2),
                    [.. Enumerable.Range(0, 12).Select(e => Model(dbc.GetInt32(i, 3 + e)))],
                    dbc.GetInt32(i, 15))
                {
                    Procs = [.. Enumerable.Range(0, 4).Select(p => dbc.GetInt32(i, 17 + p))],
                    ProcFirst = [.. Enumerable.Range(0, 4).Select(p => dbc.GetSingle(i, 21 + p))],
                    ProcSecond = [.. Enumerable.Range(0, 4).Select(p => dbc.GetSingle(i, 25 + p))],
                    EffectSizes = [.. Enumerable.Range(0, 12).Select(e => Size(dbc.GetInt32(i, 3 + e)))],
                }));
        SpellKit? Kit(int id) => kits.TryGetValue(id, out var kit) && !kit.IsEmpty ? kit : null;

        DbcTables.Read<object?>(source, @"DBFilesClient\SpellVisual.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._visuals[dbc.GetInt32(i, 0)] = new SpellVisual(
                    Kit(dbc.GetInt32(i, 1)),
                    Kit(dbc.GetInt32(i, 2)),
                    Kit(dbc.GetInt32(i, 3)),
                    Kit(dbc.GetInt32(i, 6)),
                    dbc.GetInt32(i, 7) != 0 ? Model(dbc.GetInt32(i, 8)) : null,
                    dbc.GetInt32(i, 11))
                {
                    TargetImpact = Kit(dbc.GetInt32(i, 15)),
                    InstantArea = Kit(dbc.GetInt32(i, 23)),
                    ImpactArea = Kit(dbc.GetInt32(i, 24)),
                    PersistentArea = Kit(dbc.GetInt32(i, 25)),
                };
            }

            return null;
        });

        // SpellVisualKitAreaModel: an id, the model, and its place in the list; rows are picked by position.
        DbcTables.Read<object?>(source, @"DBFilesClient\SpellVisualKitAreaModel.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._areaModels.Add(ModelPath(dbc.GetString(i, 1)) ?? "");
            }

            return null;
        });

        DbcTables.Read<object?>(source, @"DBFilesClient\AnimationData.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._animationFallbacks[dbc.GetInt32(i, 0)] = dbc.GetInt32(i, 5);
            }

            return null;
        });
    }

    /// <summary>The client names models <c>.mdx</c>, as in older versions; the files are <c>.m2</c>.</summary>
    private static string? ModelPath(string name) =>
        name.Length == 0 ? null
        : name.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) ? name[..^4] + ".m2"
        : name;

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
