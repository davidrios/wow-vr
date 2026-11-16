using System;
using System.Collections.Generic;
using WowVR.Formats.Assets;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// Melee as the client shows it: units that attack stand ready between swings, each swing plays
/// its clip with what the attacker holds, its whoosh where the clip marks it, and where the blow
/// lands the victim flinches, staggers, dodges, parries or blocks, the blow's impact sounds for the
/// weapon and what it hit, and both cry out.
/// </summary>
public partial class ServerWorld
{
    /// <summary>A swing's moments when the attacker isn't drawn or has no swing clip, in seconds.</summary>
    private const float UndrawnSwingAt = 0.1f;
    private const float UndrawnHitAt = 0.3f;

    /// <summary>How often a plain swing or hit draws a grunt or cry; a critical always does. By ear.</summary>
    private const double GruntChance = 0.5;

    /// <summary>The Attack spell every character knows, whose button turns melee on and off.</summary>
    public const uint AttackSpell = 6603;

    /// <summary>The ranged auto attack going on, as Auto Shot, or 0.</summary>
    public uint AutoRepeat => AutoRepeating;

    private uint AutoRepeating { get; set; }

    /// <summary>Whether a spell's button shows as going on: Attack while attacking, a ranged auto attack while it repeats.</summary>
    public bool IsActive(uint spell) => spell == AttackSpell ? Attacking : spell != 0 && spell == AutoRepeating;

    /// <summary>The Attack button: attacks the target, or stops attacking.</summary>
    public void ToggleAttack()
    {
        if (Attacking)
        {
            StopAttack();
            ButtonsChanged?.Invoke();
            return;
        }

        if (TargetStats is not { } target || target.IsDead || !CanAttack(target))
        {
            ScreenText?.Invoke(TargetStats is null ? "You have no target." : "You can't attack that.", true);
            return;
        }

        StartAttack(target.Guid);
    }

    public void StopAutoRepeat()
    {
        if (AutoRepeating == 0)
        {
            return;
        }

        _session.CancelAutoRepeat();
        AutoRepeating = 0;
        ButtonsChanged?.Invoke();
    }

    /// <summary>Whether the player can swing at a unit: one that isn't friendly and the server lets be attacked.</summary>
    private bool CanAttack(UnitStats unit) => unit.Type == ObjectType.Unit && !unit.NotAttackable && ReactionTo(unit) != Reaction.Friendly;

    /// <summary>
    /// The target changed while attacking: the swings follow a new enemy, as in the game, and stop
    /// with no target.
    /// </summary>
    private void FollowTarget(ulong guid)
    {
        if (!Attacking)
        {
            return;
        }

        if (guid == 0)
        {
            StopAttack();
        }
        else if (Units.TryGetValue(guid, out var unit) && !unit.IsDead && CanAttack(unit))
        {
            _session.Attack(guid);
        }
    }

    private CombatSoundTable? _combatSounds;
    private readonly Dictionary<ulong, MeleeWeapons> _weapons = [];
    private readonly Dictionary<ulong, ImpactSurface> _armor = [];
    private readonly Dictionary<ulong, ulong> _attacking = [];
    private readonly List<(double At, Action Do)> _strikes = [];

    private void StartMelee(IAssetSource source)
    {
        try
        {
            _combatSounds = CombatSoundTable.Read(source);
        }
        catch (Exception e)
        {
            GD.PushWarning($"Could not read the combat sounds: {e.Message}");
        }
    }

    /// <summary>What a unit holds and wears: an NPC's virtual items, a player's visible ones.</summary>
    private void NoteWeapons(ulong guid, uint[] npcItems, PlayerLook? look)
    {
        if (look is not null)
        {
            NoteLook(guid, look);
            return;
        }

        _weapons[guid] = new MeleeWeapons(ItemKind(npcItems.Length > 0 ? npcItems[0] : 0), ItemKind(npcItems.Length > 1 ? npcItems[1] : 0));
        _armor.Remove(guid);
        UpdateReady(guid);
    }

    private void NoteLook(ulong guid, PlayerLook look)
    {
        uint Item(EquipmentSlot slot) => (int)slot < look.Items.Length ? look.Items[(int)slot] : 0;
        _weapons[guid] = new MeleeWeapons(ItemKind(Item(EquipmentSlot.MainHand)), ItemKind(Item(EquipmentSlot.OffHand)));
        _armor[guid] = ItemKind(Item(EquipmentSlot.Chest))?.ArmorSurface ?? ImpactSurface.Flesh;
        UpdateReady(guid);
    }

    private ItemKind? ItemKind(uint item) => item == 0 ? null : _displays.ItemKindOf((int)item);

    private MeleeWeapons WeaponsOf(ulong guid) => _weapons.GetValueOrDefault(guid) ?? MeleeWeapons.None;

    private void ForgetMelee(ulong guid)
    {
        _weapons.Remove(guid);
        _armor.Remove(guid);
        _attacking.Remove(guid);
    }

    /// <summary>The server says who's attacking whom; attackers stand ready, and stop when they stop.</summary>
    private void OnUnitAttack(UnitAttackChanged attack)
    {
        if (attack.Attacking)
        {
            _attacking[attack.Attacker] = attack.Victim;
        }
        else
        {
            _attacking.Remove(attack.Attacker);
        }

        UpdateReady(attack.Attacker);
    }

    private void UpdateReady(ulong guid)
    {
        if (guid != _session.SelfGuid)
        {
            _creatures.SetLiveCombat((long)guid, WeaponsOf(guid), _attacking.ContainsKey(guid));
        }
    }

    /// <summary>The player's own body: what they hold, and standing ready while they attack. Not in VR, where the hands are the player's.</summary>
    private void UpdateSelfMelee()
    {
        if (Avatar?.Body is not { } body)
        {
            return;
        }

        if (Self?.Look is { } look && !_weapons.ContainsKey(_session.SelfGuid))
        {
            NoteLook(_session.SelfGuid, look);
        }

        body.Weapons = WeaponsOf(_session.SelfGuid);
        body.CombatReady = Attacking && !Avatar.InVr;
    }

    private CreatureNode? NodeOf(ulong guid) => guid == _session.SelfGuid ? Avatar?.Body : _creatures.LiveNode((long)guid);

    private CreatureSoundSet? SoundsOf(ulong guid) => guid == _session.SelfGuid ? Avatar?.Body?.Asset.Sounds : _creatures.LiveSounds((long)guid);

    /// <summary>A swing the server reports: its clip now, its whoosh and its blow when the clip marks them.</summary>
    /// <summary>Every melee swing the server reports, for checks.</summary>
    public event Action<SwingHappened>? Swung;

    private void OnSwing(SwingHappened swing)
    {
        Swung?.Invoke(swing);
        var outcome = MeleeHits.Outcome(swing.HitInfo, swing.VictimState, swing.Damage);
        var weapons = WeaponsOf(swing.Attacker);
        var weapon = weapons.Swinging(swing.OffHand);
        var critical = outcome == MeleeOutcome.Critical;
        var attacker = NodeOf(swing.Attacker);

        // The VR player's arms are their own, so only the sounds play for their swings.
        var animate = (swing.HitInfo & MeleeHits.NoAnimationFlag) == 0 && !(swing.Attacker == _session.SelfGuid && Avatar?.InVr == true);
        var (swingAt, hitAt) = (animate ? attacker?.PlaySwing(swing.OffHand) : null) ?? (UndrawnSwingAt, UndrawnHitAt);
        var now = Now;

        _strikes.Add((now + swingAt, () =>
        {
            if (NodeOf(swing.Attacker) is not { } node)
            {
                return;
            }

            var sounds = SoundsOf(swing.Attacker);
            var at = Chest(node);

            // A beast with its own attack sounds bites or claws; anything else whooshes its weapon or fist.
            var whoosh = weapon is null && sounds?.CustomAttacks is { Length: > 0 } custom
                ? custom[Random.Shared.Next(custom.Length)]
                : _combatSounds?.Swing(weapon, critical) ?? 0;
            _creatures.SoundPlayer.Play(whoosh, at);
            if (sounds is not null && (critical || Random.Shared.NextDouble() < GruntChance))
            {
                _creatures.SoundPlayer.Play(critical && sounds.AttackCritical != 0 ? sounds.AttackCritical : sounds.Attack, at);
            }
        }));

        _strikes.Add((now + hitAt, () =>
        {
            if (NodeOf(swing.Target) is not { } victim)
            {
                return;
            }

            if (animate)
            {
                victim.PlayReaction(outcome);
            }

            var at = Chest(victim);
            var victimWeapons = WeaponsOf(swing.Target);
            ImpactSurface? surface = outcome switch
            {
                MeleeOutcome.Hit or MeleeOutcome.Critical => _armor.GetValueOrDefault(swing.Target, ImpactSurface.Flesh),
                MeleeOutcome.Block => victimWeapons.OffHand is { IsShield: true, IsMetal: false } ? ImpactSurface.ShieldWood : ImpactSurface.ShieldMetal,
                MeleeOutcome.Parry => victimWeapons.MainHand is { IsWeapon: true, IsMetal: false } ? ImpactSurface.ParryWood : ImpactSurface.ParryMetal,
                _ => null,
            };
            if (surface is { } s && _combatSounds is { } table)
            {
                _creatures.SoundPlayer.Play(table.Impact(weapon, s, critical), at);
            }

            if (outcome is MeleeOutcome.Hit or MeleeOutcome.Critical && SoundsOf(swing.Target) is { } sounds)
            {
                var crushing = (swing.HitInfo & MeleeHits.CrushingFlag) != 0;
                var cry = crushing && sounds.WoundCrushing != 0 ? sounds.WoundCrushing
                    : critical && sounds.WoundCritical != 0 ? sounds.WoundCritical
                    : critical || crushing || Random.Shared.NextDouble() < GruntChance ? sounds.Wound
                    : 0;
                _creatures.SoundPlayer.Play(cry, at);
            }
        }));
    }

    private static Vector3 Chest(CreatureNode node) => node.GlobalPosition + (Vector3.Up * node.Height * 0.6f);

    /// <summary>Plays each swing's moments as they come.</summary>
    private void ProcessMelee()
    {
        UpdateSelfMelee();
        if (_strikes.Count == 0)
        {
            return;
        }

        var now = Now;
        for (var i = 0; i < _strikes.Count;)
        {
            if (_strikes[i].At <= now)
            {
                var strike = _strikes[i].Do;
                _strikes.RemoveAt(i);
                strike();
            }
            else
            {
                i++;
            }
        }
    }
}
