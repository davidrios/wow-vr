using System.Numerics;

namespace WowVR.Net;

/// <summary>What the HUD and the world need to know about a unit: health, power, level and what it does.</summary>
public sealed record UnitStats(
    ulong Guid,
    ObjectType Type,
    uint Entry,
    string Name,
    uint Health,
    uint MaxHealth,
    uint Power,
    uint MaxPower,
    PowerType PowerType,
    uint Level,
    uint Faction,
    uint Flags,
    uint DynamicFlags,
    uint NpcFlags,
    ulong Target,
    byte Race,
    byte Class,
    byte Sex)
{
    public bool IsDead => Health == 0 && MaxHealth > 0;

    /// <summary>Its corpse has loot for this player.</summary>
    public bool Lootable => (DynamicFlags & 0x1) != 0;

    /// <summary>Someone else hit it first.</summary>
    public bool TappedByOther => (DynamicFlags & 0x4) != 0 && (DynamicFlags & 0x8) == 0;

    public bool InCombat => (Flags & 0x80000) != 0;

    /// <summary>The server says it can't be attacked or selected.</summary>
    public bool NotAttackable => (Flags & (0x2 | 0x100 | 0x200 | 0x2000000)) != 0;

    public bool NotSelectable => (Flags & 0x2000000) != 0;

    public bool HasGossip => (NpcFlags & NpcFlagMask.Gossip) != 0;

    public bool IsQuestGiver => (NpcFlags & NpcFlagMask.QuestGiver) != 0;

    public bool IsVendor => (NpcFlags & NpcFlagMask.Vendor) != 0;

    public bool IsSpiritHealer => (NpcFlags & (NpcFlagMask.SpiritHealer | NpcFlagMask.SpiritGuide)) != 0;

    public bool IsNpc => NpcFlags != 0;

    public static UnitStats From(TrackedObject obj, string name)
    {
        var bytes0 = obj.Field(UpdateFields.UnitBytes0);
        var powerType = (PowerType)((bytes0 >> 24) & 0xFF);
        var powerIndex = Math.Min((int)powerType, 6);
        return new UnitStats(
            obj.Guid,
            obj.Type,
            obj.Entry,
            name,
            obj.Field(UpdateFields.UnitHealth),
            obj.Field(UpdateFields.UnitMaxHealth),
            obj.Field(UpdateFields.UnitPower1 + powerIndex),
            obj.Field(UpdateFields.UnitMaxPower1 + powerIndex),
            powerType,
            obj.Field(UpdateFields.UnitLevel),
            obj.Field(UpdateFields.UnitFaction),
            obj.Field(UpdateFields.UnitFlags),
            obj.Field(UpdateFields.UnitDynamicFlags),
            obj.Field(UpdateFields.UnitNpcFlags),
            obj.Field64(UpdateFields.UnitTarget),
            (byte)(bytes0 & 0xFF),
            (byte)((bytes0 >> 8) & 0xFF),
            (byte)((bytes0 >> 16) & 0xFF));
    }
}

public static class NpcFlagMask
{
    public const uint Gossip = 0x1;
    public const uint QuestGiver = 0x2;
    public const uint Trainer = 0x10;
    public const uint Vendor = 0x80;
    public const uint Repair = 0x1000;
    public const uint FlightMaster = 0x2000;
    public const uint SpiritHealer = 0x4000;
    public const uint SpiritGuide = 0x8000;
    public const uint Innkeeper = 0x10000;
    public const uint Banker = 0x20000;
}

public enum PowerType : byte
{
    Mana,
    Rage,
    Focus,
    Energy,
    Happiness,
    Runes,
    RunicPower,
}

/// <summary>
/// The player's character sheet, from their own update fields, as the game's character window
/// shows it. The five stats are strength, agility, stamina, intellect and spirit, with what gear
/// and auras add and take away; resistances start with armor.
/// </summary>
public sealed record CharacterSheet(
    int[] Stats,
    int[] StatBonus,
    int[] StatMalus,
    int[] Resistances,
    float MinDamage,
    float MaxDamage,
    uint AttackSpeedMs,
    int AttackPower,
    int RangedAttackPower,
    float MinRangedDamage,
    float MaxRangedDamage,
    float Crit,
    float RangedCrit,
    float SpellCrit,
    float Dodge,
    float Parry,
    float Block,
    int SpellPower,
    int Healing)
{
    public static CharacterSheet From(TrackedObject obj)
    {
        float F(int index) => BitConverter.UInt32BitsToSingle(obj.Field(index));
        int I(int index) => (int)obj.Field(index);
        int[] Run(int first, int count) => [.. Enumerable.Range(first, count).Select(I)];

        // Attack power's mods are two shorts: what adds to it, then what takes from it.
        int Power(int baseIndex)
        {
            var mods = obj.Field(baseIndex + 1);
            return I(baseIndex) + (short)(mods & 0xFFFF) - (short)(mods >> 16);
        }

        // Spells by school 1 to 6, holy to arcane; the sheet shows the best.
        var spellCrit = Enumerable.Range(1, 6).Max(s => F(UpdateFields.PlayerSpellCrit + s));
        var spellPower = Enumerable.Range(1, 6).Max(s => I(UpdateFields.PlayerDamageDonePos + s) - I(UpdateFields.PlayerDamageDoneNeg + s));
        return new CharacterSheet(
            Run(UpdateFields.UnitStat0, 5),
            Run(UpdateFields.UnitPosStat0, 5),
            Run(UpdateFields.UnitNegStat0, 5),
            Run(UpdateFields.UnitResistances, 7),
            F(UpdateFields.UnitMinDamage),
            F(UpdateFields.UnitMaxDamage),
            obj.Field(UpdateFields.UnitBaseAttackTime),
            Power(UpdateFields.UnitAttackPower),
            Power(UpdateFields.UnitRangedAttackPower),
            F(UpdateFields.UnitMinRangedDamage),
            F(UpdateFields.UnitMaxRangedDamage),
            F(UpdateFields.PlayerCrit),
            F(UpdateFields.PlayerRangedCrit),
            spellCrit,
            F(UpdateFields.PlayerDodge),
            F(UpdateFields.PlayerParry),
            F(UpdateFields.PlayerBlock),
            spellPower,
            I(UpdateFields.PlayerHealingDonePos));
    }
}

/// <summary>The player's own state, beyond what any unit has.</summary>
public sealed record PlayerStats(UnitStats Unit, uint Xp, uint NextLevelXp, uint Money, uint PlayerFlags)
{
    /// <summary>The character window's numbers.</summary>
    public CharacterSheet? Sheet { get; init; }

    /// <summary>How the character looks now, with what they wear.</summary>
    public PlayerLook? Look { get; init; }

    /// <summary>Released and walking back as a spirit.</summary>
    public bool IsGhost => (PlayerFlags & 0x10) != 0;

    /// <summary>Dead, still in the body.</summary>
    public bool IsCorpse => Unit.Health == 0 && !IsGhost;
}

/// <summary>Who's speaking where, as the chat frame shows it.</summary>
public enum ChatKind : byte
{
    System = 0x00,
    Say = 0x01,
    Party = 0x02,
    Raid = 0x03,
    Guild = 0x04,
    Officer = 0x05,
    Yell = 0x06,
    Whisper = 0x07,
    WhisperForeign = 0x08,
    WhisperInform = 0x09,
    Emote = 0x0A,
    TextEmote = 0x0B,
    MonsterSay = 0x0C,
    MonsterParty = 0x0D,
    MonsterYell = 0x0E,
    MonsterWhisper = 0x0F,
    MonsterEmote = 0x10,
    Channel = 0x11,
    RaidBossEmote = 0x29,
    RaidBossWhisper = 0x2A,

    /// <summary>Not the server's: messages this client adds, such as errors and combat.</summary>
    Notice = 0xF0,
    Combat = 0xF1,
    Error = 0xF2,
}

public sealed record ChatLine(ChatKind Kind, string Sender, string Text, string Channel = "");

public sealed record ChatReceived(ChatLine Line) : SessionEvent;

/// <summary>Something the game shows in the middle of the screen for a moment, as WoW's red error text.</summary>
public sealed record ScreenMessage(string Text, bool Error) : SessionEvent;

public sealed record SelfChanged(PlayerStats Stats) : SessionEvent;

public sealed record UnitChanged(UnitStats Stats) : SessionEvent;

/// <summary>The server moved the player on the same map.</summary>
public sealed record Teleported(Vector3 Position, float Orientation) : SessionEvent;

/// <summary>The server moved the player to another map. The game loads it, then calls <see cref="RealmSession.AckWorldPort"/>.</summary>
public sealed record WorldChanged(uint Map, Vector3 Position, float Orientation) : SessionEvent;

/// <summary>The server set how fast the player runs, swims or flies, in yards a second.</summary>
public sealed record SpeedChanged(PlayerSpeeds Speeds) : SessionEvent;

public sealed record PlayerSpeeds(float Walk, float Run, float RunBack, float Swim, float SwimBack, float Flight, float FlightBack);

/// <summary>The server let the player fly, or stopped them, as <c>.gm fly on</c> does.</summary>
public sealed record CanFlyChanged(bool CanFly) : SessionEvent;

/// <summary>Held in place, as the dead and the stunned are.</summary>
public sealed record RootChanged(bool Rooted) : SessionEvent;

public sealed record GossipOption(uint Index, byte Icon, string Text);

public sealed record GossipQuest(uint Id, uint Icon, int Level, string Title);

/// <summary>An NPC's talk window: what it says, what can be chosen, and the quests it has.</summary>
public sealed record GossipShown(ulong Npc, uint MenuId, string Text, IReadOnlyList<GossipOption> Options, IReadOnlyList<GossipQuest> Quests) : SessionEvent;

public sealed record GossipClosed : SessionEvent;

public enum QuestStep
{
    Details,
    RequestItems,
    OfferReward,
}

public sealed record QuestRewardItem(uint ItemId, uint Count);

/// <summary>A quest giver showing a quest: to accept, to hand in, or to choose a reward for.</summary>
public sealed record QuestShown(ulong Npc, uint QuestId, QuestStep Step, string Title, string Text, string Objectives, bool Completable, IReadOnlyList<QuestRewardItem> Choices, IReadOnlyList<QuestRewardItem> Rewards) : SessionEvent;

public sealed record VendorItem(uint Slot, uint ItemId, int Stock, uint Price, uint BuyCount);

public sealed record VendorShown(ulong Npc, IReadOnlyList<VendorItem> Items) : SessionEvent;

/// <summary>What the server says about an item, once asked.</summary>
public sealed record ItemInfo(uint Entry, string Name, uint Quality, uint InventoryType, uint DisplayId, uint SellPrice, uint BuyPrice, uint ContainerSlots);

public sealed record ItemInfoKnown(ItemInfo Item) : SessionEvent;

/// <summary>One item the player has. <see cref="Bag"/> is 255 for what's worn and the backpack, whose slots 23 to 38 hold it, or 19 to 22 for a bag.</summary>
public sealed record OwnedItem(ulong Guid, uint Entry, uint Count, byte Bag, byte Slot);

public sealed record InventoryChanged(IReadOnlyList<OwnedItem> Items, IReadOnlyList<(byte Bag, int Slots)> Bags) : SessionEvent;

/// <summary>The player started or stopped hitting something.</summary>
public sealed record AttackChanged(ulong Target, bool Attacking) : SessionEvent;

/// <summary>A melee swing landed, or missed, so units can play their attack and the HUD can show it.</summary>
public sealed record SwingHappened(ulong Attacker, ulong Target, uint Damage, bool Missed) : SessionEvent;

/// <summary>A game object the server shows: its model, where it is, and how it's turned, in WoW coordinates.</summary>
public sealed record GameObjectInfo(ulong Guid, uint Entry, string Name, uint DisplayId, uint Type, Vector3 Position, Quaternion Rotation, float Scale, uint Flags, uint State, uint PathProgress, uint[] Data);

public sealed record GameObjectAppeared(GameObjectInfo Object) : SessionEvent;

public sealed record GameObjectGone(ulong Guid) : SessionEvent;

/// <summary>The server has the player aboard a transport, at this offset on it in its own axes, as after crossing to another map on it.</summary>
public sealed record BoardedTransport(ulong Transport, Vector3 Offset) : SessionEvent;

/// <summary>Where the player's corpse lies, while they're a ghost.</summary>
public sealed record CorpseAt(Vector3 Position, uint Map) : SessionEvent;

/// <summary>A spirit healer offers to bring the player back at the graveyard.</summary>
public sealed record SpiritHealerOffer(ulong Npc) : SessionEvent;

/// <summary>Someone offers to resurrect the player.</summary>
public sealed record ResurrectOffer(ulong From, string Name) : SessionEvent;

/// <summary>Every spell the player knows now, as the server lists them: the highest rank of each, and passives.</summary>
public sealed record SpellsKnown(IReadOnlyList<uint> Spells) : SessionEvent;

/// <summary>What's on a button: nothing, a spell, an item or a macro.</summary>
public enum ActionKind : byte
{
    Spell = 0x00,
    Macro = 0x40,
    Item = 0x80,
}

/// <summary>One of the player's 144 action buttons, saved on the server. Empty when <see cref="Id"/> is 0.</summary>
public readonly record struct ActionButton(uint Id, ActionKind Kind)
{
    public bool IsEmpty => Id == 0;

    /// <summary>As the server packs it: the id in the low 24 bits, the kind above.</summary>
    public uint Packed => (Id & 0xFFFFFF) | ((uint)Kind << 24);

    public static ActionButton Unpack(uint packed) => new(packed & 0xFFFFFF, (ActionKind)(packed >> 24));
}

/// <summary>The player's action buttons, the first 12 being the main bar.</summary>
public sealed record ActionButtonsChanged(IReadOnlyList<ActionButton> Buttons) : SessionEvent;

/// <summary>The player began a cast or a channel that takes this long, in milliseconds.</summary>
public sealed record CastStarted(uint Spell, uint DurationMs, bool Channel) : SessionEvent;

/// <summary>The player's cast or channel ended: it went off, or it failed or was interrupted.</summary>
public sealed record CastEnded(uint Spell, bool Succeeded) : SessionEvent;

/// <summary>A spell of the player's went off, which starts its cooldown and the global one.</summary>
public sealed record SpellWent(uint Spell) : SessionEvent;

/// <summary>Any unit, the player too, began casting or channeling at a target (0 for none), for its animations and sounds.</summary>
public sealed record UnitCastBegan(ulong Caster, uint Spell, uint DurationMs, ulong Target, bool Channel) : SessionEvent;

/// <summary>Any unit's spell went off, hitting these units, or landing at a place.</summary>
public sealed record UnitSpellWent(ulong Caster, uint Spell, IReadOnlyList<ulong> Hits, Vector3? Place) : SessionEvent;

/// <summary>Any unit stopped casting or channeling without its spell going off, or its channel ran out.</summary>
public sealed record UnitCastStopped(ulong Caster, uint Spell) : SessionEvent;

/// <summary>A spell's lasting area on the ground, as Consecration or Blizzard, at a place in WoW coordinates, until it goes.</summary>
public sealed record AreaEffectAppeared(ulong Guid, ulong Caster, uint Spell, float Radius, Vector3 Position) : SessionEvent;

public sealed record AreaEffectGone(ulong Guid) : SessionEvent;

/// <summary>The server set a spell's cooldown, in milliseconds from now, or cleared it with 0.</summary>
public sealed record CooldownSet(uint Spell, uint Milliseconds) : SessionEvent;
