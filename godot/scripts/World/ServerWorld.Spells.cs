using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.Ui;
using Godot;

namespace WowVR.World;

/// <summary>The player's cast or channel under way: what, since when on the game's clock, and for how long, in seconds.</summary>
public sealed record CastInfo(uint Spell, double Start, double Duration, bool Channel);

/// <summary>
/// Spells on a server: what the player knows and has on their buttons, casting from a button at
/// the right target, and cooldowns, which the client works out from Spell.dbc as the real one does.
/// </summary>
public partial class ServerWorld
{
    /// <summary>Spell.dbc takes about a second to read, so it's read once, off the main thread, and kept across maps.</summary>
    private static Task<SpellTable>? s_spells;

    private readonly Dictionary<uint, double> _spellReady = [];
    private readonly Dictionary<int, double> _categoryReady = [];
    private readonly Dictionary<int, double> _globalReady = [];
    private uint _lastCast;

    /// <summary>Spell.dbc, once read; null while it's still being read.</summary>
    public SpellTable? Spells => s_spells is { IsCompletedSuccessfully: true } task ? task.Result : null;

    public IconCache Icons { get; private set; } = null!;

    public IReadOnlyList<uint> KnownSpells { get; private set; } = [];

    /// <summary>The server's 144 buttons for the player; the first 12 are the main bar.</summary>
    public IReadOnlyList<ActionButton> ActionButtons { get; private set; } = new ActionButton[RealmSession.ActionButtonCount];

    public CastInfo? Casting { get; private set; }

    /// <summary>Seconds since the game started, the clock cooldowns and casts are measured on.</summary>
    public static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>The known spells, or Spell.dbc, changed.</summary>
    public event Action? SpellsChanged;

    public event Action? ButtonsChanged;

    /// <summary>A cast began or ended.</summary>
    public event Action? CastChanged;

    /// <summary>Any unit began, finished or stopped a cast, for what it looks and sounds like.</summary>
    public event Action<SessionEvent>? UnitCast;

    private void StartSpells(RealmSession session, MpqAssetSource source)
    {
        Icons = new IconCache(source);
        if (s_spells is null || s_spells.IsFaulted)
        {
            s_spells = Task.Run(() => SpellTable.Read(source));
        }

        s_spells.ContinueWith(_ => Callable.From(() => SpellsChanged?.Invoke()).CallDeferred());

        // A new map starts a new world with the same session, whose spells and buttons came at login.
        KnownSpells = session.KnownSpells;
        ActionButtons = session.ActionButtons;
    }

    private void ApplySpells(SessionEvent e)
    {
        switch (e)
        {
            case SpellsKnown known:
                KnownSpells = known.Spells;
                SpellsChanged?.Invoke();
                break;
            case ActionButtonsChanged buttons:
                ActionButtons = buttons.Buttons;
                ButtonsChanged?.Invoke();
                break;
            case CastStarted started:
                Casting = new CastInfo(started.Spell, Now, started.DurationMs / 1000.0, started.Channel);
                CastChanged?.Invoke();
                break;
            case CastEnded ended:
                if (Casting is { } casting && (ended.Spell == 0 || ended.Spell == casting.Spell))
                {
                    Casting = null;
                    CastChanged?.Invoke();
                }

                // A cast that never went off gives the global cooldown back, as the client does.
                if (!ended.Succeeded && ended.Spell == _lastCast && Spells?.Get((int)ended.Spell) is { } failed)
                {
                    _globalReady.Remove(failed.GlobalCooldownCategory);
                    ButtonsChanged?.Invoke();
                }

                break;
            case SpellWent went:
                StartCooldown(went.Spell);
                break;
            case UnitCastBegan or UnitSpellWent or UnitCastStopped or AreaEffectAppeared or AreaEffectGone:
                UnitCast?.Invoke(e);
                break;
            case CooldownSet cooldown:
                _spellReady[cooldown.Spell] = Now + cooldown.Milliseconds / 1000.0;
                ButtonsChanged?.Invoke();
                break;
        }
    }

    private void StartCooldown(uint spell)
    {
        if (Spells?.Get((int)spell) is not { } record)
        {
            return;
        }

        var now = Now;
        if (record.RecoveryMs > 0)
        {
            _spellReady[spell] = now + record.RecoveryMs / 1000.0;
        }

        if (record.Category > 0 && record.CategoryRecoveryMs > 0)
        {
            _categoryReady[record.Category] = now + record.CategoryRecoveryMs / 1000.0;
        }

        ButtonsChanged?.Invoke();
    }

    /// <summary>How long until a spell can be cast again, and how long that wait was in all, in seconds; zero when it's ready.</summary>
    public (double Left, double Total) CooldownOf(uint spell)
    {
        if (Spells?.Get((int)spell) is not { } record)
        {
            return (0, 0);
        }

        var now = Now;
        var (left, total) = (0.0, 0.0);
        void Consider(double ready, double length)
        {
            if (ready - now > left)
            {
                (left, total) = (ready - now, length);
            }
        }

        if (_spellReady.TryGetValue(spell, out var own))
        {
            Consider(own, Math.Max(record.RecoveryMs / 1000.0, own - now));
        }

        if (record.Category > 0 && _categoryReady.TryGetValue(record.Category, out var category))
        {
            Consider(category, record.CategoryRecoveryMs / 1000.0);
        }

        if (record.GlobalCooldownMs > 0 && _globalReady.TryGetValue(record.GlobalCooldownCategory, out var global))
        {
            Consider(global, record.GlobalCooldownMs / 1000.0);
        }

        return (left, total);
    }

    /// <summary>Whether the player has the power to cast it now: mana, rage, energy or the like.</summary>
    public bool HasPowerFor(SpellRecord spell) =>
        spell.PowerCost <= 0 || Self is not { } self || (int)self.Unit.PowerType != spell.PowerType || self.Unit.Power >= spell.PowerCost;

    /// <summary>
    /// Casts a spell as a button press does: at the selected enemy, at a friend or the player
    /// themselves, or on the ground at the target or the player's feet.
    /// </summary>
    public void Cast(uint spell)
    {
        var record = Spells?.Get((int)spell);
        if (record is { IsPassive: true })
        {
            return;
        }

        var (left, _) = CooldownOf(spell);
        if (left > 0.05)
        {
            ScreenText?.Invoke("Spell is not ready yet", true);
            return;
        }

        var target = TargetStats;
        switch (record?.Aim ?? SpellAim.Any)
        {
            case SpellAim.Enemy:
                if (target is null)
                {
                    ScreenText?.Invoke("You have no target.", true);
                    return;
                }

                _session.CastSpell(spell, target.Guid);
                break;
            case SpellAim.Friend:
                // Only a friend that can be helped: NPCs the server won't let players touch get the player instead, as the game's self-cast.
                var friendly = target is not null && !target.IsDead && ReactionTo(target) == Reaction.Friendly && !target.NotAttackable;
                _session.CastSpell(spell, friendly ? target!.Guid : _session.SelfGuid);
                break;
            case SpellAim.Any:
                _session.CastSpell(spell, target?.Guid ?? _session.SelfGuid);
                break;
            case SpellAim.Ground when record is not null:
                // Pressed again while aiming it: put away, as in the game.
                if (Aiming?.Id == record.Id)
                {
                    CancelAiming();
                }
                else
                {
                    Aiming = record;
                    AimingChanged?.Invoke();
                }

                return;
            default:
                _session.CastSpell(spell);
                break;
        }

        Sent(spell, record);
    }

    /// <summary>A cast went to the server: the global cooldown starts, as the client starts it.</summary>
    private void Sent(uint spell, SpellRecord? record)
    {
        _lastCast = spell;
        if (record is { GlobalCooldownMs: > 0 })
        {
            _globalReady[record.GlobalCooldownCategory] = Now + record.GlobalCooldownMs / 1000.0;
            ButtonsChanged?.Invoke();
        }
    }

    /// <summary>The ground spell waiting for a place, while the player points at the ground for it.</summary>
    public SpellRecord? Aiming { get; private set; }

    /// <summary>Aiming a ground spell began or ended.</summary>
    public event Action? AimingChanged;

    /// <summary>Casts the spell being aimed on the ground at a place, in WoW coordinates.</summary>
    public void CastAt(System.Numerics.Vector3 place)
    {
        if (Aiming is not { } spell)
        {
            return;
        }

        Aiming = null;
        AimingChanged?.Invoke();
        _session.CastSpell((uint)spell.Id, 0, place);
        Sent((uint)spell.Id, spell);
    }

    public void CancelAiming()
    {
        if (Aiming is not null)
        {
            Aiming = null;
            AimingChanged?.Invoke();
        }
    }

    /// <summary>Where the player stands, in WoW coordinates.</summary>
    public System.Numerics.Vector3 PlayerPosition
    {
        get
        {
            var (_, x, y, z) = _tiles.PlayerLocation;
            return new System.Numerics.Vector3((float)x, (float)y, z);
        }
    }

    /// <summary>Presses a button: casts its spell or uses its item. An empty one does nothing.</summary>
    public void UseAction(int slot)
    {
        if (slot < 0 || slot >= ActionButtons.Count || ActionButtons[slot] is not { IsEmpty: false } button)
        {
            return;
        }

        switch (button.Kind)
        {
            case ActionKind.Spell:
                Cast(button.Id);
                break;
            case ActionKind.Item:
                if (Inventory?.Items.FirstOrDefault(i => i.Entry == button.Id && !(i.Bag == 255 && i.Slot < 19)) is { } item)
                {
                    _session.Use(item);
                }
                else
                {
                    ScreenText?.Invoke("You don't have that item.", true);
                }

                break;
            default:
                ScreenText?.Invoke("Macros aren't supported.", true);
                break;
        }
    }

    /// <summary>Puts a spell or item on a button, or clears it; the server keeps it for the character.</summary>
    public void SetActionButton(int slot, ActionButton button) => _session.SetActionButton(slot, button);

    /// <summary>How many of an item the player carries, for an item's button.</summary>
    public uint CountOf(uint entry) =>
        (uint)(Inventory?.Items.Where(i => i.Entry == entry && !(i.Bag == 255 && i.Slot < 19)).Sum(i => (long)i.Count) ?? 0);

    /// <summary>An item's icon: the server's display for it if known, else the client's Item.dbc.</summary>
    public Texture2D? ItemIcon(uint entry)
    {
        var display = _session.ItemInfoOf(entry)?.DisplayId is { } known and > 0 ? (int)known : _displays.ItemDisplayOf((int)entry);
        return Icons.Get(_displays.ItemIconPath(display));
    }

    public Texture2D? SpellIcon(uint spell) => Icons.Get(Spells?.Get((int)spell)?.IconPath);
}
