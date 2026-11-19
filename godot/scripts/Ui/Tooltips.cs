using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// What a tooltip says about an item, a spell or an aura, as BBCode for a RichTextLabel, laid out
/// as the game's: an item's name in its quality's color, binding, slot and type, damage and speed,
/// armor, stats, requirements, its spells' effects and its flavor text; a spell's cost, range,
/// cast time, cooldown and description; an aura's text and time left. Controls ask for one by
/// their <c>tip</c> meta, as <c>item:25</c>, <c>spell:133</c> or <c>aura:guid:slot</c>.
/// </summary>
public static class Tooltips
{
    public const string Meta = "tip";

    /// <summary>The meta's name, made once: a string where Godot wants a StringName makes a new one each call.</summary>
    public static readonly StringName MetaName = Meta;

    /// <summary>Counts every change of a control's tooltip, so the HUD knows to look again at the one under the pointer.</summary>
    public static int Version { get; private set; }

    /// <summary>Gives a control a tooltip; null takes it away.</summary>
    public static void Set(Control control, string? key)
    {
        Version++;
        if (key is null)
        {
            control.RemoveMeta(MetaName);
        }
        else
        {
            control.SetMeta(MetaName, key);
        }

        control.TooltipText = "";
    }

    public static string ItemKey(uint entry) => $"item:{entry}";

    public static string SpellKey(uint spell) => $"spell:{spell}";

    public static string AuraKey(ulong unit, byte slot) => $"aura:{unit}:{slot}";

    /// <summary>The text for a key, or null for one it doesn't know.</summary>
    public static string? Text(ServerWorld world, string key)
    {
        var parts = key.Split(':');
        return parts[0] switch
        {
            "item" when parts.Length > 1 && uint.TryParse(parts[1], out var item) => Item(world, item),
            "spell" when parts.Length > 1 && uint.TryParse(parts[1], out var spell) => Spell(world, spell),
            "aura" when parts.Length > 2 && ulong.TryParse(parts[1], out var unit) && byte.TryParse(parts[2], out var slot) => Aura(world, unit, slot),
            "text" when key.Length > 5 => key[5..],
            _ => null,
        };
    }

    private static readonly string[] QualityColors = ["9d9d9d", "ffffff", "1eff00", "0070dd", "a335ee", "ff8000", "e6cc80", "e6cc80"];

    public static string QualityHex(uint quality) => QualityColors[Math.Min(quality, (uint)QualityColors.Length - 1)];

    private const string Green = "1eff00";
    private const string Gold = "ffd100";
    private const string Grey = "9d9d9d";

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private static string Colored(string hex, string text) => $"[color=#{hex}]{Escape(text)}[/color]";

    /// <summary>Two columns, the second on the right, as "Main Hand ... Sword".</summary>
    private static string Row(string left, string right) =>
        right.Length == 0 ? Escape(left) : $"[table=2][cell expand=1]{Escape(left)}[/cell][cell][right]{Escape(right)}[/right][/cell][/table]";

    public static string Item(ServerWorld world, uint entry)
    {
        if (world.Session.ItemInfoOf(entry) is not { } item)
        {
            return Colored(Grey, "Retrieving item information...");
        }

        var lines = new List<string> { $"[font_size=20]{Colored(QualityHex(item.Quality), item.Name)}[/font_size]" };
        if (item.Bonding is > 0 and < 5)
        {
            lines.Add(item.Bonding switch { 1 => "Binds when picked up", 2 => "Binds when equipped", 3 => "Binds when used", _ => "Quest Item" });
        }

        if (item.MaxCount == 1)
        {
            lines.Add("Unique");
        }

        var slot = SlotName(item.InventoryType);
        var kind = TypeName(item.Class, item.Subclass);
        if (slot.Length > 0 || kind.Length > 0)
        {
            lines.Add(Row(slot, kind));
        }

        if (item.ContainerSlots > 0)
        {
            lines.Add($"{item.ContainerSlots} Slot {(item.Class == 1 ? "Bag" : "Container")}");
        }

        if (item.Damage.Count > 0)
        {
            var (min, max, school) = item.Damage[0];
            var speed = item.DelayMs / 1000.0;
            lines.Add(Row($"{Math.Floor(min)} - {Math.Floor(max)} {SchoolName(school)}Damage", speed > 0 ? $"Speed {speed.ToString("0.00", CultureInfo.InvariantCulture)}" : ""));
            foreach (var (extraMin, extraMax, extraSchool) in item.Damage.Skip(1))
            {
                lines.Add($"+ {Math.Floor(extraMin)} - {Math.Floor(extraMax)} {SchoolName(extraSchool)}Damage");
            }

            if (speed > 0)
            {
                var perSecond = item.Damage.Sum(d => (d.Min + d.Max) / 2) / speed;
                lines.Add($"({perSecond.ToString("0.0", CultureInfo.InvariantCulture)} damage per second)");
            }
        }

        if (item.Armor > 0)
        {
            lines.Add($"{item.Armor} Armor");
        }

        if (item.Block > 0)
        {
            lines.Add($"{item.Block} Block");
        }

        var equipLines = new List<string>();
        foreach (var (type, value) in item.Stats)
        {
            if (value == 0)
            {
                continue;
            }

            if (PrimaryStat(type) is { } primary)
            {
                lines.Add($"{(value > 0 ? "+" : "")}{value} {primary}");
            }
            else if (RatingText(type) is { } rating)
            {
                equipLines.Add(Colored(Green, $"Equip: {string.Format(CultureInfo.InvariantCulture, rating, value)}"));
            }
        }

        string[] resistances = ["Holy", "Fire", "Nature", "Frost", "Shadow", "Arcane"];
        for (var i = 0; i < item.Resistances.Length && i < resistances.Length; i++)
        {
            if (item.Resistances[i] > 0)
            {
                lines.Add($"+{item.Resistances[i]} {resistances[i]} Resistance");
            }
        }

        foreach (var socket in item.Sockets.Where(s => s != 0))
        {
            lines.Add(Colored(Grey, $"{SocketName(socket)} Socket"));
        }

        if (item.MaxDurability > 0)
        {
            lines.Add($"Durability {item.MaxDurability} / {item.MaxDurability}");
        }

        if (item.RequiredLevel > 1)
        {
            var level = world.Self?.Unit.Level ?? 0;
            lines.Add(level >= item.RequiredLevel ? $"Requires Level {item.RequiredLevel}" : Colored("ff2020", $"Requires Level {item.RequiredLevel}"));
        }

        if (item.ItemLevel > 0 && item.Class is 2 or 4)
        {
            lines.Add(Colored(Gold, $"Item Level {item.ItemLevel}"));
        }

        lines.AddRange(equipLines);
        foreach (var (spellId, trigger) in item.Spells)
        {
            if (world.Spells?.Get((int)spellId) is not { } spell)
            {
                continue;
            }

            var text = SpellText.Format(spell.Description, spell, world.Spells.Get);
            if (text.Length == 0)
            {
                continue;
            }

            var prefix = trigger switch { 0 => "Use: ", 1 => "Equip: ", 2 => "Chance on hit: ", _ => "" };
            lines.Add(Colored(Green, prefix + text));
        }

        if (item.StartQuest > 0)
        {
            lines.Add(Colored(Gold, "This Item Begins a Quest"));
        }

        if (item.Description.Length > 0)
        {
            lines.Add(Colored(Gold, $"\"{item.Description}\""));
        }

        if (item.SellPrice > 0)
        {
            lines.Add($"Sell Price: {RealmSession.Money(item.SellPrice)}");
        }

        return string.Join("\n", lines);
    }

    public static string Spell(ServerWorld world, uint id)
    {
        if (world.Spells?.Get((int)id) is not { } spell)
        {
            return Colored(Grey, $"Spell {id}");
        }

        var lines = new List<string> { $"[font_size=20]{Row(spell.Name, "")}[/font_size]" };
        if (spell.Rank.Length > 0)
        {
            lines[0] = $"[font_size=20]{Escape(spell.Name)}[/font_size]  {Colored(Grey, spell.Rank)}";
        }

        if (spell.IsPassive)
        {
            lines.Add("Passive");
        }
        else
        {
            var cost = Cost(spell);
            var range = spell.MeleeRange ? "Melee Range" : spell.Range > 0 ? $"{Number(spell.Range)} yd range" : "";
            if (cost.Length > 0 || range.Length > 0)
            {
                lines.Add(Row(cost, range));
            }

            var cast = spell.IsChanneled ? "Channeled" : spell.CastTimeMs > 0 ? $"{Number(spell.CastTimeMs / 1000.0)} sec cast" : "Instant";
            var cooldown = Math.Max(spell.RecoveryMs, spell.CategoryRecoveryMs) is var ms and > 1500 ? $"{CooldownText(ms)} cooldown" : "";
            lines.Add(Row(cast, cooldown));
        }

        var description = SpellText.Format(spell.Description, spell, world.Spells.Get);
        if (description.Length > 0)
        {
            lines.Add(Colored(Gold, description));
        }

        return string.Join("\n", lines);
    }

    public static string Aura(ServerWorld world, ulong unit, byte slot)
    {
        if (world.Session.AuraIn(unit, slot) is not { } aura || world.Spells?.Get((int)aura.Spell) is not { } spell)
        {
            return "";
        }

        var lines = new List<string> { $"[font_size=20]{Colored(aura.Harmful ? "ff4040" : "ffffff", spell.Name)}[/font_size]" };
        var text = SpellText.Format(spell.AuraText, spell, world.Spells.Get);
        if (text.Length > 0)
        {
            lines.Add(text);
        }

        if (aura.LeftAt(RealmSession.AuraClock()) is { } left)
        {
            lines.Add(Colored(Gold, $"{SpellText.Duration((int)(left * 1000))} remaining"));
        }

        if (unit == world.Session.SelfGuid && !aura.Harmful)
        {
            lines.Add(Colored(Grey, "Right-click to cancel"));
        }

        return string.Join("\n", lines);
    }

    private static string Cost(SpellRecord spell)
    {
        if (spell.PowerCostPercent > 0)
        {
            return $"{spell.PowerCostPercent}% of base mana";
        }

        if (spell.PowerCost <= 0)
        {
            return "";
        }

        // Rage and runic power are kept in tenths.
        var amount = spell.PowerType is 1 or 6 ? spell.PowerCost / 10 : spell.PowerCost;
        var name = spell.PowerType switch { 1 => "Rage", 2 => "Focus", 3 => "Energy", 5 => "Runes", 6 => "Runic Power", _ => "Mana" };
        return spell.PowerPerSecond > 0 ? $"{amount} {name}, plus {spell.PowerPerSecond} per sec" : $"{amount} {name}";
    }

    private static string CooldownText(int ms) => ms >= 60000 ? $"{Number(ms / 60000.0)} min" : $"{Number(ms / 1000.0)} sec";

    private static string Number(double value) => value.ToString(value == Math.Floor(value) ? "0" : "0.#", CultureInfo.InvariantCulture);

    private static string SchoolName(uint school) => school switch
    {
        1 => "Holy ",
        2 => "Fire ",
        3 => "Nature ",
        4 => "Frost ",
        5 => "Shadow ",
        6 => "Arcane ",
        _ => "",
    };

    private static string SocketName(uint color) => color switch
    {
        1 => "Meta",
        2 => "Red",
        4 => "Yellow",
        8 => "Blue",
        _ => "Prismatic",
    };

    /// <summary>Item.dbc's InventoryType, as the tooltip names the slot.</summary>
    public static string SlotName(uint inventoryType) => inventoryType switch
    {
        1 => "Head",
        2 => "Neck",
        3 => "Shoulder",
        4 => "Shirt",
        5 or 20 => "Chest",
        6 => "Waist",
        7 => "Legs",
        8 => "Feet",
        9 => "Wrist",
        10 => "Hands",
        11 => "Finger",
        12 => "Trinket",
        13 => "One-Hand",
        14 => "Off Hand",
        15 or 26 => "Ranged",
        16 => "Back",
        17 => "Two-Hand",
        19 => "Tabard",
        21 => "Main Hand",
        22 => "Off Hand",
        23 => "Held In Off-hand",
        24 => "Projectile",
        25 => "Thrown",
        28 => "Relic",
        _ => "",
    };

    private static string TypeName(uint itemClass, uint subclass) => (itemClass, subclass) switch
    {
        (2, 0 or 1) => "Axe",
        (2, 2) => "Bow",
        (2, 3) => "Gun",
        (2, 4 or 5) => "Mace",
        (2, 6) => "Polearm",
        (2, 7 or 8) => "Sword",
        (2, 10) => "Staff",
        (2, 13) => "Fist Weapon",
        (2, 15) => "Dagger",
        (2, 16) => "Thrown",
        (2, 17) => "Spear",
        (2, 18) => "Crossbow",
        (2, 19) => "Wand",
        (2, 20) => "Fishing Pole",
        (4, 1) => "Cloth",
        (4, 2) => "Leather",
        (4, 3) => "Mail",
        (4, 4) => "Plate",
        (4, 6) => "Shield",
        (4, 7) => "Libram",
        (4, 8) => "Idol",
        (4, 9) => "Totem",
        (4, 10) => "Sigil",
        _ => "",
    };

    /// <summary>ItemModType's stats shown as "+N Stamina".</summary>
    private static string? PrimaryStat(uint type) => type switch
    {
        0 => "Mana",
        1 => "Health",
        3 => "Agility",
        4 => "Strength",
        5 => "Intellect",
        6 => "Spirit",
        7 => "Stamina",
        _ => null,
    };

    /// <summary>ItemModType's others, shown as green "Equip:" lines.</summary>
    private static string? RatingText(uint type) => type switch
    {
        12 => "Increases defense rating by {0}.",
        13 => "Increases your dodge rating by {0}.",
        14 => "Increases your parry rating by {0}.",
        15 => "Increases your shield block rating by {0}.",
        16 or 17 or 18 or 31 => "Improves hit rating by {0}.",
        19 or 20 or 21 or 32 => "Improves critical strike rating by {0}.",
        28 or 29 or 30 or 36 => "Improves haste rating by {0}.",
        35 => "Improves your resilience rating by {0}.",
        37 => "Increases your expertise rating by {0}.",
        38 => "Increases attack power by {0}.",
        39 => "Increases ranged attack power by {0}.",
        43 => "Restores {0} mana per 5 sec.",
        44 => "Increases your armor penetration rating by {0}.",
        41 or 42 or 45 => "Increases spell power by {0}.",
        46 => "Restores {0} health per 5 sec.",
        47 => "Increases spell penetration by {0}.",
        48 => "Increases the block value of your shield by {0}.",
        _ => null,
    };

    /// <summary>A chat link for an item, colored by quality, as the game sends it; the server checks the name and color.</summary>
    public static string ItemLink(ItemInfo item, uint level) =>
        $"|cff{QualityHex(item.Quality)}|Hitem:{item.Entry}:0:0:0:0:0:0:0:{level}|h[{item.Name}]|h|r";

    /// <summary>A chat link for a spell.</summary>
    public static string SpellLink(SpellRecord spell) => $"|cff71d5ff|Hspell:{spell.Id}|h[{spell.Name}]|h|r";
}
