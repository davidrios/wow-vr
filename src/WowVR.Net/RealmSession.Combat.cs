namespace WowVR.Net;

/// <summary>
/// Auras on units, and what spells did: damage, heals, periodic ticks and misses, for the unit
/// frames' buffs, floating combat text and the combat log. Layouts as AzerothCore writes them.
/// </summary>
public sealed partial class RealmSession
{
    private const ushort CMSG_CANCEL_AURA = 0x136;
    private const ushort SMSG_SPELLHEALLOG = 0x150;
    private const ushort SMSG_SPELLENERGIZELOG = 0x151;
    private const ushort SMSG_PERIODICAURALOG = 0x24E;
    private const ushort SMSG_SPELLNONMELEEDAMAGELOG = 0x250;
    private const ushort SMSG_AURA_UPDATE_ALL = 0x495;
    private const ushort SMSG_AURA_UPDATE = 0x496;

    /// <summary>SpellAuraDefines' flags: the caster is the unit itself (no guid follows), and a duration follows.</summary>
    private const byte AuraFromSelf = 0x08;
    private const byte AuraHasDuration = 0x20;

    /// <summary>The periodic aura types SMSG_PERIODICAURALOG reports, as AzerothCore's <c>AuraType</c>.</summary>
    private const uint PeriodicDamage = 3;
    private const uint PeriodicHeal = 8;
    private const uint ObsModHealth = 20;
    private const uint ObsModPower = 21;
    private const uint PeriodicEnergize = 24;
    private const uint PeriodicManaLeech = 64;
    private const uint PeriodicDamagePercent = 89;

    /// <summary>SpellNonMeleeDamage's hit flag for a critical.</summary>
    private const uint SpellCriticalFlag = 0x2;

    private readonly Dictionary<ulong, SortedDictionary<byte, AuraInfo>> _auras = [];

    /// <summary>A unit's visible auras, by slot, as last sent.</summary>
    public IReadOnlyList<AuraInfo> AurasOf(ulong unit)
    {
        lock (_auras)
        {
            return _auras.TryGetValue(unit, out var auras) ? [.. auras.Values] : [];
        }
    }

    /// <summary>The aura in one of a unit's slots, or null, without copying the rest.</summary>
    public AuraInfo? AuraIn(ulong unit, byte slot)
    {
        lock (_auras)
        {
            return _auras.TryGetValue(unit, out var auras) ? auras.GetValueOrDefault(slot) : null;
        }
    }

    /// <summary>A unit that went out of view takes its auras; null forgets everyone's, leaving the world.</summary>
    private void ForgetAuras(ulong? unit)
    {
        lock (_auras)
        {
            if (unit is { } guid)
            {
                _auras.Remove(guid);
            }
            else
            {
                _auras.Clear();
            }
        }
    }

    /// <summary>Takes one of the player's own buffs off, as right-clicking it does.</summary>
    public void CancelAura(uint spell) => _ = SendQuietly(CMSG_CANCEL_AURA, new PacketWriter().U32(spell).ToArray());

    private bool HandleCombat(WorldPacket p)
    {
        switch (p.Opcode)
        {
            case SMSG_AURA_UPDATE_ALL:
            case SMSG_AURA_UPDATE:
                ReadAuras(p.Data, p.Opcode == SMSG_AURA_UPDATE_ALL);
                return true;
            case SMSG_SPELLNONMELEEDAMAGELOG:
                Report(ParseSpellDamage(p.Data));
                return true;
            case SMSG_SPELLHEALLOG:
                Report(ParseSpellHeal(p.Data));
                return true;
            case SMSG_PERIODICAURALOG:
                if (ParsePeriodic(p.Data) is { } tick)
                {
                    Report(tick);
                }

                return true;
            case SMSG_SPELLENERGIZELOG:
            {
                var r = new PacketReader(p.Data);
                var target = r.PackedGuid();
                var caster = r.PackedGuid();
                var spell = r.U32();
                var power = r.U32();
                Events.Enqueue(new SpellEffectLogged(target, caster, spell, SpellEffectKind.Energize, r.U32(), false) { PowerType = power });
                return true;
            }
            default:
                return false;
        }
    }

    private void ReadAuras(byte[] data, bool all)
    {
        var updates = ParseAuras(data, AuraClock(), out var unit);
        lock (_auras)
        {
            if (!_auras.TryGetValue(unit, out var auras) || all)
            {
                _auras[unit] = auras = [];
            }

            foreach (var (slot, aura) in updates)
            {
                if (aura is null)
                {
                    auras.Remove(slot);
                }
                else
                {
                    auras[slot] = aura;
                }
            }
        }

        Events.Enqueue(new AurasChanged(unit));
    }

    /// <summary>The clock aura times count from, in seconds: <c>Environment.TickCount64 / 1000</c>.</summary>
    public static double AuraClock() => Environment.TickCount64 / 1000.0;

    /// <summary>SMSG_AURA_UPDATE(_ALL): the unit, then slots, each with 0 for gone or a spell, flags, level, stacks, its caster and its time.</summary>
    internal static List<(byte Slot, AuraInfo? Aura)> ParseAuras(byte[] data, double now, out ulong unit)
    {
        var r = new PacketReader(data);
        unit = r.PackedGuid();
        var updates = new List<(byte, AuraInfo?)>();
        while (r.Remaining > 0)
        {
            var slot = r.U8();
            var spell = r.U32();
            if (spell == 0)
            {
                updates.Add((slot, null));
                continue;
            }

            var flags = r.U8();
            var level = r.U8();
            var stacks = r.U8();
            var caster = (flags & AuraFromSelf) != 0 ? unit : r.PackedGuid();
            int max = 0, left = 0;
            if ((flags & AuraHasDuration) != 0)
            {
                max = (int)r.U32();
                left = (int)r.U32();
            }

            updates.Add((slot, new AuraInfo(slot, spell, flags, level, stacks, caster, max, left, now)));
        }

        return updates;
    }

    internal static SpellEffectLogged ParseSpellDamage(byte[] data)
    {
        var r = new PacketReader(data);
        var target = r.PackedGuid();
        var caster = r.PackedGuid();
        var spell = r.U32();
        var damage = r.U32();
        r.U32(); // overkill
        var school = r.U8();
        var absorbed = r.U32();
        var resisted = r.U32();
        r.U8();
        r.U8();
        var blocked = r.U32();
        var hitInfo = r.U32();
        return new SpellEffectLogged(target, caster, spell, SpellEffectKind.Damage, damage, (hitInfo & SpellCriticalFlag) != 0)
        {
            School = school,
            Absorbed = absorbed,
            Resisted = resisted,
            Blocked = blocked,
        };
    }

    internal static SpellEffectLogged ParseSpellHeal(byte[] data)
    {
        var r = new PacketReader(data);
        var target = r.PackedGuid();
        var caster = r.PackedGuid();
        var spell = r.U32();
        var heal = r.U32();
        var overheal = r.U32();
        var absorbed = r.U32();
        return new SpellEffectLogged(target, caster, spell, SpellEffectKind.Heal, heal, r.U8() != 0) { Over = overheal, Absorbed = absorbed };
    }

    /// <summary>One tick of a periodic aura; null for a kind with nothing to show.</summary>
    internal static SpellEffectLogged? ParsePeriodic(byte[] data)
    {
        var r = new PacketReader(data);
        var target = r.PackedGuid();
        var caster = r.PackedGuid();
        var spell = r.U32();
        r.U32(); // count, always 1
        var aura = r.U32();
        switch (aura)
        {
            case PeriodicDamage or PeriodicDamagePercent:
            {
                var damage = r.U32();
                r.U32();
                var school = r.U32();
                var absorbed = r.U32();
                var resisted = r.U32();
                return new SpellEffectLogged(target, caster, spell, SpellEffectKind.Damage, damage, r.U8() != 0)
                {
                    Periodic = true,
                    School = (byte)school,
                    Absorbed = absorbed,
                    Resisted = resisted,
                };
            }
            case PeriodicHeal or ObsModHealth:
            {
                var heal = r.U32();
                var over = r.U32();
                var absorbed = r.U32();
                return new SpellEffectLogged(target, caster, spell, SpellEffectKind.Heal, heal, r.U8() != 0) { Periodic = true, Over = over, Absorbed = absorbed };
            }
            case ObsModPower or PeriodicEnergize or PeriodicManaLeech:
            {
                var power = r.U32();
                return new SpellEffectLogged(target, caster, spell, SpellEffectKind.Energize, r.U32(), false) { Periodic = true, PowerType = power };
            }
            default:
                return null;
        }
    }

    /// <summary>Sends a spell's effect on, with a line in the combat log when the player is in it.</summary>
    private void Report(SpellEffectLogged effect)
    {
        Events.Enqueue(effect);
        if (effect.Caster == Character.Guid || effect.Target == Character.Guid)
        {
            Events.Enqueue(new ChatReceived(new ChatLine(ChatKind.Combat, "", EffectLine(effect))));
        }
    }

    /// <summary>The spell's name, once the game has the spell table; the session doesn't.</summary>
    public Func<uint, string>? SpellName { get; set; }

    private string EffectLine(SpellEffectLogged e)
    {
        string NameFor(ulong guid, bool subject) => guid == Character.Guid ? subject ? "Your" : "you"
            : _objects.Objects.TryGetValue(guid, out var o) && NameOf(o) is { Length: > 0 } n ? subject ? $"{n}'s" : n : subject ? "Something's" : "something";
        var spell = SpellName?.Invoke(e.Spell) is { Length: > 0 } name ? name : $"spell {e.Spell}";
        var who = NameFor(e.Caster, subject: true);
        var whom = e.Target == e.Caster && e.Caster == Character.Guid ? "you" : NameFor(e.Target, subject: false);
        var crit = e.Critical ? " critically" : "";
        var notes = new List<string>();
        if (e.Over > 0)
        {
            notes.Add($"{e.Over} {(e.Kind == SpellEffectKind.Heal ? "overhealed" : "over")}");
        }

        if (e.Absorbed > 0)
        {
            notes.Add($"{e.Absorbed} absorbed");
        }

        if (e.Resisted > 0)
        {
            notes.Add($"{e.Resisted} resisted");
        }

        if (e.Blocked > 0)
        {
            notes.Add($"{e.Blocked} blocked");
        }

        var extra = notes.Count == 0 ? "" : $" ({string.Join(", ", notes)})";
        return e.Kind switch
        {
            SpellEffectKind.Heal => $"{who} {spell}{crit} heals {whom} for {e.Amount}{extra}.",
            SpellEffectKind.Energize => $"{who} {spell} gives {whom} {e.Amount} {PowerName(e.PowerType)}.",
            _ when e.Periodic => $"{Capital(whom)} suffer{(e.Target == Character.Guid ? "" : "s")} {e.Amount} damage from {(e.Caster == Character.Guid ? "your" : who)} {spell}{extra}.",
            _ => $"{who} {spell}{crit} hits {whom} for {e.Amount}{extra}.",
        };
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string PowerName(uint power) => power switch
    {
        0 => "Mana",
        1 => "Rage",
        2 => "Focus",
        3 => "Energy",
        6 => "Runic Power",
        _ => "power",
    };
}
