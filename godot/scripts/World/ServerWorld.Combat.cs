using System;
using WowVR.Formats.Database;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// Auras for the frames, floating combat text for what the player does and what's done to them,
/// and range: whether the target is within reach of a spell, which the bar shows and casting
/// checks first.
/// </summary>
public partial class ServerWorld
{
    /// <summary>
    /// A unit's combat reach when it isn't known, and the player's: AzerothCore's default for
    /// players, and what most creatures' models have.
    /// </summary>
    private const float DefaultCombatReach = 1.5f;

    /// <summary>Melee reaches the two units' combat reaches and this much more, but never less than 5 yards.</summary>
    private const float MeleeExtra = 4f / 3f;
    private const float NominalMeleeRange = 5f;

    private CombatText? _combatText;

    /// <summary>How many floating texts have shown, for checks.</summary>
    public int CombatTextShown => _combatText?.ShownTotal ?? 0;

    /// <summary>A unit's body in the scene, the player's own included; null when it isn't drawn.</summary>
    public CreatureNode? UnitNode(ulong guid) => NodeOf(guid);

    /// <summary>Where the player stands in the scene.</summary>
    public Vector3 PlayerFeet => _iplayer.Feet;

    /// <summary>A unit's auras changed: the frames show them again.</summary>
    public event Action<ulong>? AurasUpdated;

    private void StartCombat()
    {
        _combatText = new CombatText { Name = "CombatText" };
        AddChild(_combatText);
        _session.SpellName = id => Spells?.Get((int)id)?.Name ?? "";
    }

    /// <summary>Events for this part: true when one was handled here.</summary>
    private bool ApplyCombat(SessionEvent e)
    {
        switch (e)
        {
            case AurasChanged auras:
                AurasUpdated?.Invoke(auras.Unit);
                return true;
            case SpellEffectLogged effect:
                ShowEffect(effect);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Where floating text about a unit starts: over its head, or the player's.</summary>
    private Vector3? TextPlace(ulong guid)
    {
        if (NodeOf(guid) is { } node)
        {
            return node.GlobalPosition + (Vector3.Up * (node.Height + 0.3f));
        }

        return guid == _session.SelfGuid ? _iplayer.Feet + (Vector3.Up * 2.2f) : null;
    }

    private bool Involves(ulong a, ulong b) => a == _session.SelfGuid || b == _session.SelfGuid;

    /// <summary>A melee swing's text, when its blow lands: over the victim, red when it's the player.</summary>
    private void ShowSwingText(SwingHappened swing, MeleeOutcome outcome)
    {
        if (!Involves(swing.Attacker, swing.Target) || TextPlace(swing.Target) is not { } at)
        {
            return;
        }

        var mine = swing.Target == _session.SelfGuid;
        var word = OutcomeWord(outcome);
        if (word is not null)
        {
            _combatText?.Show(at, word, CombatText.Miss);
            return;
        }

        var amount = mine ? $"-{swing.Damage}" : $"{swing.Damage}";
        _combatText?.Show(at, amount, mine ? CombatText.Incoming : CombatText.Melee, outcome == MeleeOutcome.Critical);
    }

    private static string? OutcomeWord(MeleeOutcome outcome) => outcome switch
    {
        MeleeOutcome.Miss => "Miss",
        MeleeOutcome.Dodge => "Dodge",
        MeleeOutcome.Parry => "Parry",
        MeleeOutcome.Block => "Block",
        MeleeOutcome.Evade => "Evade",
        MeleeOutcome.Immune => "Immune",
        MeleeOutcome.Absorb => "Absorb",
        _ => null,
    };

    private void ShowEffect(SpellEffectLogged effect)
    {
        if (!Involves(effect.Caster, effect.Target) || effect.Kind == SpellEffectKind.Energize || TextPlace(effect.Target) is not { } at)
        {
            return;
        }

        var mine = effect.Target == _session.SelfGuid;
        if (effect.Amount == 0)
        {
            var word = effect.Absorbed > 0 ? "Absorb" : effect.Resisted > 0 ? "Resist" : effect.Blocked > 0 ? "Block" : null;
            if (word is not null)
            {
                _combatText?.Show(at, word, CombatText.Miss);
            }

            return;
        }

        if (effect.Kind == SpellEffectKind.Heal)
        {
            _combatText?.Show(at, $"+{effect.Amount}", CombatText.Heal, effect.Critical);
            return;
        }

        _combatText?.Show(at, mine ? $"-{effect.Amount}" : $"{effect.Amount}", mine ? CombatText.Incoming : CombatText.Spell, effect.Critical);
    }

    /// <summary>A spell that missed the player or that the player cast and missed: why, over who it missed.</summary>
    private void ShowSpellMisses(UnitSpellWent went)
    {
        foreach (var (unit, reason) in went.Misses)
        {
            if (!Involves(went.Caster, unit) || TextPlace(unit) is not { } at)
            {
                continue;
            }

            var word = reason switch
            {
                1 => "Miss",
                2 => "Resist",
                3 => "Dodge",
                4 => "Parry",
                5 => "Block",
                6 => "Evade",
                7 => "Immune",
                9 => "Deflect",
                10 => "Absorb",
                11 => "Reflect",
                _ => null,
            };
            if (word is not null)
            {
                _combatText?.Show(at, word, CombatText.Miss);
            }
        }
    }

    /// <summary>
    /// Whether the selected unit is within reach of a spell, or of melee for Attack: null when
    /// there's nothing to measure (no target, a spell on the player or the ground, a target that
    /// isn't drawn). Distances count from the units' middles, plus their combat reaches, as the
    /// server's spell range does.
    /// </summary>
    public bool? InRange(uint spellId)
    {
        if (TargetMeasure() is not { } measure)
        {
            return null;
        }

        var (distance, reach, friendly) = measure;
        if (spellId == AttackSpell)
        {
            return friendly ? null : distance <= MeleeReach(reach);
        }

        if (Spells?.Get((int)spellId) is not { IsPassive: false } spell)
        {
            return null;
        }

        float max;
        switch (spell.Aim)
        {
            case SpellAim.Enemy when !friendly:
                max = spell.Range;
                break;
            case SpellAim.Friend when friendly:
                max = spell.FriendRange;
                break;
            case SpellAim.Any:
                max = friendly ? spell.FriendRange : spell.Range;
                break;
            default:
                return null;
        }

        if (spell.MeleeRange)
        {
            return distance <= MeleeReach(reach);
        }

        if (max <= 0)
        {
            return null;
        }

        var extra = DefaultCombatReach + reach;
        return distance <= max + extra && (spell.MinRange <= 0 || distance >= spell.MinRange);
    }

    private ulong _measuredFrame = ulong.MaxValue;
    private (float Distance, float Reach, bool Friendly)? _measure;

    /// <summary>How far the target is, its reach and whether it's friendly, measured once a frame for all the bar's buttons.</summary>
    private (float Distance, float Reach, bool Friendly)? TargetMeasure()
    {
        var frame = Engine.GetProcessFrames();
        if (frame == _measuredFrame)
        {
            return _measure;
        }

        _measuredFrame = frame;
        _measure = TargetStats is { } target && target.Guid != _session.SelfGuid && NodeOf(target.Guid) is { } node
            ? ((node.GlobalPosition - _iplayer.Feet).Length(), Mathf.Max(DefaultCombatReach, node.Radius), ReactionTo(target) == Reaction.Friendly)
            : null;
        return _measure;
    }

    private static float MeleeReach(float targetReach) => Mathf.Max(NominalMeleeRange, DefaultCombatReach + targetReach + MeleeExtra);
}
