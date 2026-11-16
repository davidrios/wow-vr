using WowVR.Formats.Assets;
using WowVR.Formats.Database;

namespace WowVR.Formats.World;

/// <summary>How a weapon is held, which chooses the attack, parry and ready clips.</summary>
public enum WeaponGrip
{
    Unarmed,
    OneHand,
    TwoHand,

    /// <summary>Polearms, staves and spears, held loosely in both hands.</summary>
    TwoHandLoose,
}

/// <summary>How heavy a swing sounds, WeaponSwingSounds2's swing type.</summary>
public enum SwingWeight
{
    Light = 0,
    Medium = 1,
    Heavy = 2,
}

/// <summary>What a blow landed on, WeaponImpactSounds' column. Its numbers are the table's order.</summary>
public enum ImpactSurface
{
    Flesh = 0,
    Chain = 1,
    Plate = 2,
    ShieldMetal = 3,
    ShieldWood = 4,

    /// <summary>Parried by a metal weapon, and by a wooden one.</summary>
    ParryMetal = 5,
    ParryWood = 6,
    Wood = 7,
    Stone = 8,
    Ethereal = 9,
}

/// <summary>What became of a swing, from the server's hit flags and the victim's state.</summary>
public enum MeleeOutcome
{
    Hit,
    Critical,
    Miss,
    Dodge,
    Parry,
    Block,
    Evade,
    Immune,

    /// <summary>All of it absorbed, as by a shield spell.</summary>
    Absorb,
}

/// <summary>An item's row in Item.dbc: what it is and what it's made of.</summary>
public sealed record ItemKind(int Class, int Subclass, int SoundSubclass, int Material, int InventoryType)
{
    public const int WeaponClass = 2;
    public const int ArmorClass = 4;
    private const int ShieldSubclass = 6;

    /// <summary>Item.dbc's Material: 1 is metal; 2 is wood.</summary>
    private const int MetalMaterial = 1;

    public bool IsWeapon => Class == WeaponClass;

    public bool IsShield => Class == ArmorClass && Subclass == ShieldSubclass;

    public bool IsMetal => Material == MetalMaterial;

    /// <summary>Item subclasses of weapons: 1 two-handed axe, 5 mace, 8 sword; 6 polearm, 10 staff, 17 spear, 20 fishing pole; 13 fist weapon.</summary>
    public WeaponGrip Grip => !IsWeapon ? WeaponGrip.Unarmed : Subclass switch
    {
        1 or 5 or 8 => WeaponGrip.TwoHand,
        6 or 10 or 17 or 20 => WeaponGrip.TwoHandLoose,
        13 => WeaponGrip.Unarmed,
        _ => WeaponGrip.OneHand,
    };

    /// <summary>The subclass whose impact sounds it makes: its own unless the item overrides it.</summary>
    public int ImpactSubclass => SoundSubclass >= 0 ? SoundSubclass : Subclass;

    /// <summary>Daggers and fist weapons swing light, two-handers heavy, the rest medium.</summary>
    public SwingWeight Weight => Grip switch
    {
        WeaponGrip.TwoHand or WeaponGrip.TwoHandLoose => SwingWeight.Heavy,
        _ when Subclass is 13 or 15 => SwingWeight.Light,
        _ => SwingWeight.Medium,
    };

    /// <summary>A worn chest's armor: mail rings as chain, plate as plate, the rest as flesh.</summary>
    public ImpactSurface ArmorSurface => Class != ArmorClass ? ImpactSurface.Flesh : Subclass switch
    {
        3 => ImpactSurface.Chain,
        4 => ImpactSurface.Plate,
        _ => ImpactSurface.Flesh,
    };
}

/// <summary>What a unit fights with: its main hand and off hand, either of which may be empty.</summary>
public sealed record MeleeWeapons(ItemKind? MainHand, ItemKind? OffHand)
{
    public static readonly MeleeWeapons None = new(null, null);

    public WeaponGrip Grip => MainHand?.Grip ?? WeaponGrip.Unarmed;

    public bool HasShield => OffHand?.IsShield == true;

    /// <summary>The weapon a swing is made with: the off hand's for an off-hand swing.</summary>
    public ItemKind? Swinging(bool offHand) => offHand ? OffHand is { IsWeapon: true } off ? off : null : MainHand is { IsWeapon: true } main ? main : null;
}

/// <summary>Which clips melee plays, in AnimationData's numbering, with what to try when a model lacks one.</summary>
public static class MeleeAnimations
{
    public const int StandWound = 8;
    public const int CombatWound = 9;
    public const int CombatCritical = 10;
    public const int AttackUnarmed = 16;
    public const int Attack1H = 17;
    public const int Attack2H = 18;
    public const int Attack2HL = 19;
    public const int ParryUnarmed = 20;
    public const int Parry1H = 21;
    public const int Parry2H = 22;
    public const int Parry2HL = 23;
    public const int ShieldBlock = 24;
    public const int ReadyUnarmed = 25;
    public const int Ready1H = 26;
    public const int Ready2H = 27;
    public const int Ready2HL = 28;
    public const int Dodge = 30;
    public const int Block = 36;
    public const int AttackOff = 87;
    public const int AttackUnarmedOff = 117;

    /// <summary>All of them, for the clips a model keeps.</summary>
    public static readonly int[] All =
    [
        StandWound, CombatWound, CombatCritical, AttackUnarmed, Attack1H, Attack2H, Attack2HL, ParryUnarmed, Parry1H, Parry2H, Parry2HL,
        ShieldBlock, ReadyUnarmed, Ready1H, Ready2H, Ready2HL, Dodge, Block, AttackOff, AttackUnarmedOff,
    ];

    public static readonly int[] ReadyStances = [ReadyUnarmed, Ready1H, Ready2H, Ready2HL];

    /// <summary>A swing, the off hand's with <paramref name="offHand"/>, then what stands in for it.</summary>
    public static int[] Attack(WeaponGrip grip, bool offHand) => offHand
        ? grip == WeaponGrip.Unarmed ? [AttackUnarmedOff, AttackOff, AttackUnarmed, Attack1H] : [AttackOff, Attack1H, AttackUnarmed]
        : grip switch
        {
            WeaponGrip.OneHand => [Attack1H, AttackUnarmed, Attack2H],
            WeaponGrip.TwoHand => [Attack2H, Attack1H, AttackUnarmed],
            WeaponGrip.TwoHandLoose => [Attack2HL, Attack2H, Attack1H, AttackUnarmed],
            _ => [AttackUnarmed, Attack1H, Attack2H],
        };

    /// <summary>Standing ready between swings.</summary>
    public static int[] Ready(WeaponGrip grip) => grip switch
    {
        WeaponGrip.OneHand => [Ready1H, ReadyUnarmed],
        WeaponGrip.TwoHand => [Ready2H, Ready1H, ReadyUnarmed],
        WeaponGrip.TwoHandLoose => [Ready2HL, Ready2H, Ready1H, ReadyUnarmed],
        _ => [ReadyUnarmed, Ready1H],
    };

    /// <summary>How the victim takes a swing; empty when it doesn't show it.</summary>
    public static int[] Reaction(MeleeOutcome outcome, MeleeWeapons victim) => outcome switch
    {
        MeleeOutcome.Hit => [CombatWound, StandWound],
        MeleeOutcome.Critical => [CombatCritical, CombatWound, StandWound],
        MeleeOutcome.Dodge => [Dodge],
        MeleeOutcome.Parry => victim.Grip switch
        {
            WeaponGrip.OneHand => [Parry1H, ParryUnarmed],
            WeaponGrip.TwoHand => [Parry2H, Parry1H],
            WeaponGrip.TwoHandLoose => [Parry2HL, Parry2H, Parry1H],
            _ => [ParryUnarmed, Parry1H],
        },
        MeleeOutcome.Block => victim.HasShield ? [ShieldBlock, Block, Parry1H] : [Block, ShieldBlock, Parry1H],
        _ => [],
    };

    /// <summary>The swing clip's moments when it's not marked: the sound a quarter through, the blow at four tenths.</summary>
    public const float DefaultSwingFraction = 0.25f;
    public const float DefaultHitFraction = 0.4f;

    /// <summary>
    /// Whether an M2 event marks a swing's sound (<c>$CSS</c>) or the blow landing (<c>$CST</c> on
    /// characters, <c>$CAH</c> on many creatures). Null for any other event.
    /// </summary>
    public static bool? IsSwingOrHit(string id) => id switch
    {
        "$CSS" => true,
        "$CST" or "$CAH" => false,
        _ => null,
    };
}

/// <summary>
/// The sounds of melee: WeaponImpactSounds gives a blow's by the weapon's subclass, whether it's
/// metal, what it lands on and whether it's critical; WeaponSwingSounds2 a swing's by its weight.
/// </summary>
public sealed class CombatSoundTable
{
    private const int Surfaces = 10;

    /// <summary>Fist weapons' row, which also stands for a bare hand: "Unarmed_Generic".</summary>
    private const int UnarmedSubclass = 13;

    private readonly Dictionary<(int Subclass, bool Metal), (int[] Normal, int[] Critical)> _impacts = [];
    private readonly Dictionary<(SwingWeight Weight, bool Critical), int> _swings = [];

    private CombatSoundTable()
    {
    }

    public static CombatSoundTable Read(IAssetSource source)
    {
        var table = new CombatSoundTable();
        DbcTables.Read<object?>(source, @"DBFilesClient\WeaponImpactSounds.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                var normal = new int[Surfaces];
                var critical = new int[Surfaces];
                for (var s = 0; s < Surfaces; s++)
                {
                    normal[s] = dbc.GetInt32(i, 3 + s);
                    critical[s] = dbc.GetInt32(i, 3 + Surfaces + s);
                }

                table._impacts[(dbc.GetInt32(i, 1), dbc.GetInt32(i, 2) != 0)] = (normal, critical);
            }

            return null;
        });
        DbcTables.Read<object?>(source, @"DBFilesClient\WeaponSwingSounds2.dbc", dbc =>
        {
            for (var i = 0; i < dbc.RecordCount; i++)
            {
                table._swings[((SwingWeight)dbc.GetInt32(i, 1), dbc.GetInt32(i, 2) != 0)] = dbc.GetInt32(i, 3);
            }

            return null;
        });
        return table;
    }

    /// <summary>The SoundEntries row for a blow with this weapon, or a bare hand for null, on a surface; 0 for none.</summary>
    public int Impact(ItemKind? weapon, ImpactSurface surface, bool critical)
    {
        var subclass = weapon?.ImpactSubclass ?? UnarmedSubclass;
        var metal = weapon?.IsMetal ?? false;
        if (!_impacts.TryGetValue((subclass, metal), out var row)
            && !_impacts.TryGetValue((subclass, !metal), out row)
            && !_impacts.TryGetValue((UnarmedSubclass, false), out row))
        {
            return 0;
        }

        var sound = (critical ? row.Critical : row.Normal)[(int)surface];
        return sound != 0 ? sound : row.Normal[(int)surface];
    }

    /// <summary>The whoosh of a swing with this weapon, a bare hand's for null.</summary>
    public int Swing(ItemKind? weapon, bool critical) =>
        _swings.GetValueOrDefault((weapon?.Weight ?? SwingWeight.Light, critical));
}

/// <summary>Reads the server's melee report into an outcome.</summary>
public static class MeleeHits
{
    public const uint OffHandFlag = 0x4;
    public const uint MissFlag = 0x10;
    public const uint FullAbsorbFlag = 0x20;
    public const uint CriticalFlag = 0x200;
    public const uint BlockFlag = 0x2000;
    public const uint CrushingFlag = 0x20000;
    public const uint NoAnimationFlag = 0x40000;

    /// <summary>From SMSG_ATTACKERSTATEUPDATE's hit flags and victim state (2 dodge, 3 parry, 5 blocks, 6 evades, 7 immune).</summary>
    public static MeleeOutcome Outcome(uint hitInfo, byte victimState, uint damage) => victimState switch
    {
        2 => MeleeOutcome.Dodge,
        3 => MeleeOutcome.Parry,
        5 => MeleeOutcome.Block,
        6 => MeleeOutcome.Evade,
        7 => MeleeOutcome.Immune,
        _ when (hitInfo & MissFlag) != 0 || victimState == 0 => MeleeOutcome.Miss,
        _ when (hitInfo & BlockFlag) != 0 && damage == 0 => MeleeOutcome.Block,
        _ when (hitInfo & FullAbsorbFlag) != 0 && damage == 0 => MeleeOutcome.Absorb,
        _ when (hitInfo & CriticalFlag) != 0 => MeleeOutcome.Critical,
        _ => MeleeOutcome.Hit,
    };
}
