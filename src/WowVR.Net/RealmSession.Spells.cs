using System.Numerics;

namespace WowVR.Net;

/// <summary>
/// The player's spells and action buttons, and casting: what the server says the player knows
/// and has on their bars, and the start, end and failure of their casts.
/// </summary>
public sealed partial class RealmSession
{
    public const int ActionButtonCount = 144;

    private const ushort CMSG_SET_ACTION_BUTTON = 0x128;
    private const ushort SMSG_ACTION_BUTTONS = 0x129;
    private const ushort SMSG_INITIAL_SPELLS = 0x12A;
    private const ushort SMSG_LEARNED_SPELL = 0x12B;
    private const ushort SMSG_SUPERCEDED_SPELL = 0x12C;
    private const ushort CMSG_CAST_SPELL = 0x12E;
    private const ushort CMSG_CANCEL_CAST = 0x12F;
    private const ushort CMSG_CANCEL_CHANNELLING = 0x13B;
    private const ushort SMSG_CAST_FAILED = 0x130;
    private const ushort SMSG_SPELL_START = 0x131;
    private const ushort SMSG_SPELL_GO = 0x132;
    private const ushort SMSG_SPELL_FAILURE = 0x133;
    private const ushort SMSG_SPELL_COOLDOWN = 0x134;
    private const ushort SMSG_COOLDOWN_EVENT = 0x135;
    private const ushort MSG_CHANNEL_START = 0x139;
    private const ushort MSG_CHANNEL_UPDATE = 0x13A;
    private const ushort SMSG_CLEAR_COOLDOWN = 0x1DE;
    private const ushort SMSG_REMOVED_SPELL = 0x203;
    private const ushort SMSG_SPELL_FAILED_OTHER = 0x2A6;

    /// <summary>SpellMissInfo's reflect, which carries one more byte.</summary>
    private const byte MissReflect = 11;

    /// <summary>SpellCastTargets' flags: a unit, and a place on the ground.</summary>
    private const uint TargetUnit = 0x2;
    private const uint TargetDestination = 0x40;

    /// <summary>SMSG_SPELL_START and _GO's flag for a cast the server started on its own.</summary>
    private const uint CastPending = 0x1;

    private readonly object _spellLock = new();
    private List<uint> _spells = [];
    private ActionButton[] _buttons = new ActionButton[ActionButtonCount];
    private byte _castCount;

    /// <summary>The spells the player knows, as last sent. Read from any thread.</summary>
    public IReadOnlyList<uint> KnownSpells
    {
        get
        {
            lock (_spellLock)
            {
                return [.. _spells];
            }
        }
    }

    /// <summary>The player's 144 action buttons, as last sent. Read from any thread.</summary>
    public IReadOnlyList<ActionButton> ActionButtons
    {
        get
        {
            lock (_spellLock)
            {
                return [.. _buttons];
            }
        }
    }

    /// <summary>
    /// Casts a spell: at a unit, on the ground at a place, or with neither, which the server
    /// takes as the caster or what the spell finds itself.
    /// </summary>
    public void CastSpell(uint spell, ulong unit = 0, Vector3? ground = null)
    {
        var w = new PacketWriter().U8(++_castCount).U32(spell).U8(0);
        var mask = (unit != 0 ? TargetUnit : 0) | (ground is not null ? TargetDestination : 0);
        w.U32(mask);
        if (unit != 0)
        {
            w.Packed(unit);
        }

        if (ground is { } place)
        {
            w.Packed(0).F32(place.X).F32(place.Y).F32(place.Z);
        }

        _ = SendQuietly(CMSG_CAST_SPELL, w.ToArray());
    }

    public void CancelCast(uint spell) =>
        _ = SendQuietly(CMSG_CANCEL_CAST, new PacketWriter().U8(0).U32(spell).ToArray());

    /// <summary>Stops a channel, which a cast's cancel doesn't.</summary>
    public void CancelChannel(uint spell) =>
        _ = SendQuietly(CMSG_CANCEL_CHANNELLING, new PacketWriter().U32(spell).ToArray());

    /// <summary>Puts a spell or item on a button, or clears it with an empty one. The server saves it.</summary>
    public void SetActionButton(int slot, ActionButton button)
    {
        if (slot is < 0 or >= ActionButtonCount)
        {
            return;
        }

        lock (_spellLock)
        {
            _buttons[slot] = button;
        }

        _ = SendQuietly(CMSG_SET_ACTION_BUTTON, new PacketWriter().U8((byte)slot).U32(button.Packed).ToArray());
        Events.Enqueue(new ActionButtonsChanged(ActionButtons));
    }

    /// <summary>Spell packets. Runs on the reading thread; false for any other packet.</summary>
    private bool HandleSpells(WorldPacket p)
    {
        switch (p.Opcode)
        {
            case SMSG_INITIAL_SPELLS:
                ReadInitialSpells(p.Data);
                return true;
            case SMSG_LEARNED_SPELL:
                ChangeSpells(add: new PacketReader(p.Data).U32());
                return true;
            case SMSG_REMOVED_SPELL:
                ChangeSpells(remove: new PacketReader(p.Data).U32());
                return true;
            case SMSG_SUPERCEDED_SPELL:
            {
                var r = new PacketReader(p.Data);
                var old = r.U32();
                var replacement = r.U32();
                ChangeSpells(add: replacement, remove: old);
                ReplaceOnButtons(old, replacement);
                return true;
            }
            case SMSG_ACTION_BUTTONS:
                ReadActionButtons(p.Data);
                return true;
            case SMSG_CAST_FAILED:
            {
                var r = new PacketReader(p.Data);
                r.U8();
                var spell = r.U32();
                var reason = r.U8();
                Events.Enqueue(new CastEnded(spell, false));
                if (CastResults.Describe(reason) is { } text)
                {
                    Events.Enqueue(new ScreenMessage(text, true));
                }

                return true;
            }
            case SMSG_SPELL_START:
                ReadSpellStart(p.Data, went: false);
                return true;
            case SMSG_SPELL_GO:
                ReadSpellStart(p.Data, went: true);
                return true;
            case SMSG_SPELL_FAILED_OTHER:
            {
                var r = new PacketReader(p.Data);
                var caster = r.PackedGuid();
                r.U8();
                if (caster != Character.Guid)
                {
                    Events.Enqueue(new UnitCastStopped(caster, r.U32()));
                }

                return true;
            }
            case SMSG_SPELL_FAILURE:
            {
                var r = new PacketReader(p.Data);
                var caster = r.PackedGuid();
                r.U8();
                var spell = r.U32();
                var reason = r.U8();
                Events.Enqueue(new UnitCastStopped(caster, spell));
                if (caster == Character.Guid)
                {
                    Events.Enqueue(new CastEnded(spell, false));
                    if (CastResults.Describe(reason) is { } text)
                    {
                        Events.Enqueue(new ScreenMessage(text, true));
                    }
                }

                return true;
            }
            case MSG_CHANNEL_START:
            {
                var r = new PacketReader(p.Data);
                var caster = r.PackedGuid();
                var spell = r.U32();
                var duration = r.U32();
                Events.Enqueue(new UnitCastBegan(caster, spell, duration, 0, Channel: true));
                if (caster == Character.Guid)
                {
                    Events.Enqueue(new CastStarted(spell, duration, Channel: true));
                }

                return true;
            }
            case MSG_CHANNEL_UPDATE:
            {
                var r = new PacketReader(p.Data);
                var caster = r.PackedGuid();
                if (r.U32() == 0)
                {
                    Events.Enqueue(new UnitCastStopped(caster, 0));
                    if (caster == Character.Guid)
                    {
                        Events.Enqueue(new CastEnded(0, true));
                    }
                }

                return true;
            }
            case SMSG_SPELL_COOLDOWN:
            {
                var r = new PacketReader(p.Data);
                if (r.U64() != Character.Guid)
                {
                    return true;
                }

                r.U8();
                while (r.Remaining >= 8)
                {
                    var spell = r.U32();
                    Events.Enqueue(new CooldownSet(spell, r.U32()));
                }

                return true;
            }
            case SMSG_COOLDOWN_EVENT:
                Events.Enqueue(new SpellWent(new PacketReader(p.Data).U32()));
                return true;
            case SMSG_CLEAR_COOLDOWN:
                Events.Enqueue(new CooldownSet(new PacketReader(p.Data).U32(), 0));
                return true;
            default:
                return false;
        }
    }

    /// <summary>The count of spells, each a u32 and a u16, then cooldowns, which the game works out itself.</summary>
    private void ReadInitialSpells(byte[] data)
    {
        var r = new PacketReader(data);
        r.U8();
        var count = r.U16();
        var spells = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            spells.Add(r.U32());
            r.U16();
        }

        lock (_spellLock)
        {
            _spells = spells;
        }

        Events.Enqueue(new SpellsKnown(KnownSpells));
    }

    private void ChangeSpells(uint add = 0, uint remove = 0)
    {
        lock (_spellLock)
        {
            _spells.Remove(remove);
            if (add != 0 && !_spells.Contains(add))
            {
                _spells.Add(add);
            }
        }

        Events.Enqueue(new SpellsKnown(KnownSpells));
    }

    /// <summary>A higher rank takes its old rank's buttons, as the client moves them itself.</summary>
    private void ReplaceOnButtons(uint old, uint replacement)
    {
        var changed = new List<int>();
        lock (_spellLock)
        {
            for (var i = 0; i < _buttons.Length; i++)
            {
                if (_buttons[i] is { Kind: ActionKind.Spell } b && b.Id == old)
                {
                    _buttons[i] = new ActionButton(replacement, ActionKind.Spell);
                    changed.Add(i);
                }
            }
        }

        foreach (var slot in changed)
        {
            _ = SendQuietly(CMSG_SET_ACTION_BUTTON, new PacketWriter().U8((byte)slot).U32(new ActionButton(replacement, ActionKind.Spell).Packed).ToArray());
        }

        if (changed.Count > 0)
        {
            Events.Enqueue(new ActionButtonsChanged(ActionButtons));
        }
    }

    /// <summary>A state byte, then all 144 buttons, unless the state is 2, which clears them.</summary>
    private void ReadActionButtons(byte[] data)
    {
        var r = new PacketReader(data);
        var state = r.U8();
        var buttons = new ActionButton[ActionButtonCount];
        if (state != 2)
        {
            for (var i = 0; i < buttons.Length && r.Remaining >= 4; i++)
            {
                buttons[i] = ActionButton.Unpack(r.U32());
            }
        }

        lock (_spellLock)
        {
            _buttons = buttons;
        }

        Events.Enqueue(new ActionButtonsChanged(ActionButtons));
    }

    /// <summary>A go's hit units, as full guids, then its misses, each a guid, a reason, and one more byte for a reflect.</summary>
    internal static List<ulong> ReadHits(PacketReader r)
    {
        var hits = new List<ulong>();
        var count = r.U8();
        for (var i = 0; i < count; i++)
        {
            hits.Add(r.U64());
        }

        var misses = r.U8();
        for (var i = 0; i < misses; i++)
        {
            r.U64();
            if (r.U8() == MissReflect)
            {
                r.U8();
            }
        }

        return hits;
    }

    /// <summary>A cast's targets: the flags, then the unit or object, an item, and where it starts and lands.</summary>
    internal static (ulong Unit, Vector3? Place) ReadTargets(PacketReader r)
    {
        var mask = r.U32();
        ulong unit = 0;
        Vector3? place = null;
        if ((mask & (0x2 | 0x800 | 0x8000 | 0x200 | 0x10000)) != 0)
        {
            unit = r.PackedGuid();
        }

        // An item or a trade item, written as a packed guid even when there's none.
        if ((mask & (0x10 | 0x1000)) != 0)
        {
            r.PackedGuid();
        }

        if ((mask & 0x20) != 0)
        {
            r.PackedGuid();
            r.Skip(12);
        }

        if ((mask & TargetDestination) != 0)
        {
            r.PackedGuid();
            place = new Vector3(r.F32(), r.F32(), r.F32());
        }

        return (unit, place);
    }

    /// <summary>
    /// Who cast what: the item or caster, the caster, the cast count, the spell, its flags and, for
    /// a start, how long it takes. Only the player's own casts matter here.
    /// </summary>
    private void ReadSpellStart(byte[] data, bool went)
    {
        var r = new PacketReader(data);
        r.PackedGuid();
        var caster = r.PackedGuid();
        r.U8();
        var spell = r.U32();
        var flags = r.U32();
        var time = r.U32();
        try
        {
            var hits = went ? ReadHits(r) : [];
            var (target, place) = ReadTargets(r);
            Events.Enqueue(went
                ? new UnitSpellWent(caster, spell, hits, place)
                : new UnitCastBegan(caster, spell, time, target, Channel: false));
        }
        catch (EndOfStreamException)
        {
            // Without targets it's still worth showing the caster's side.
            Events.Enqueue(went ? new UnitSpellWent(caster, spell, [], null) : new UnitCastBegan(caster, spell, time, 0, Channel: false));
        }

        if (caster != Character.Guid)
        {
            return;
        }

        if (LogMoves)
        {
            _log($"spell {spell} {(went ? "went" : "started")}, flags 0x{flags:X}, time {time} ms");
        }

        if (!went)
        {
            if (time > 0)
            {
                Events.Enqueue(new CastStarted(spell, time, Channel: false));
            }

            return;
        }

        Events.Enqueue(new CastEnded(spell, true));
        if ((flags & CastPending) == 0)
        {
            Events.Enqueue(new SpellWent(spell));
        }
    }
}
