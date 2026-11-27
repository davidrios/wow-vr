using System;
using System.Linq;
using WowVR.Desktop;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--fight-check</c>: on Tirisfal's coast, with GM mode on so nothing fights back, casts a spell
/// with a missile at a creature and sees it fly and strike; has another creature cast Frostbolt at
/// the player (<c>.cast back</c>), which its target frame shows as a cast bar and whose missile
/// flies at the player; channels First Aid on the player with them selected and cancels it, which
/// the frame shows as interrupted; and teaches Blizzard for a moment (<c>.learn 10</c>), casts it on
/// a creature and sees its ice fall. <c>--shots=folder</c> saves each.
/// </summary>
public partial class PlayCheck
{
    /// <summary>Frostbolt, rank 1: a cast time and a missile, for a creature to cast at the player.</summary>
    private const uint FrostboltSpell = 116;

    /// <summary>First Aid with Linen Bandages: a channel with no cost, cast by GM command on the player.</summary>
    private const uint BandageSpell = 746;

    /// <summary>Blizzard, rank 1, the mage's: its ice falls in an area, by the kit's procedural effect.</summary>
    private const uint BlizzardSpell = 10;

    /// <summary>How far from a creature to stand to cast at it, so a missile has some way to fly.</summary>
    private const float CastingYards = 15f;

    private ulong _castAt;
    private (int Launched, int Arrived, int Impacts) _countsBefore;
    private double _seenAt = -1;
    private bool _fightShot;

    private SpellEffects? Effects => GetParent().GetNodeOrNull<SpellEffects>("SpellEffects");

    private void AddFightSteps()
    {
        Step("gm on and revive", age =>
        {
            if (First)
            {
                Server.Say(".gm on");
                Server.Say(".revive");
            }

            return age > 2 ? Server.Self is { } self && !self.IsGhost && !self.Unit.IsDead : null;
        });
        GoTo("go hunting on Tirisfal's coast", new System.Numerics.Vector3(2520, 1470, 2));
        Step("a missile flies to a creature and strikes it", MissileAtFoe);
        Step("a creature's cast shows on its frame and its missile flies at the player", FoeCasts);
        Step("a channel cut short shows as interrupted on the frame", InterruptedOnFrame);
        Step("Blizzard's ice falls on a creature", BlizzardOnFoe);
    }

    /// <summary>Stands <see cref="CastingYards"/> from a creature and, once there, faces it.</summary>
    private bool StandOffFrom(ulong guid, bool first, double age)
    {
        if (Creatures.LiveNode((long)guid) is not { } node)
        {
            return false;
        }

        if (first)
        {
            var at = Tiles.LocalToWow(node.GlobalPosition);
            Server.Say(FormattableString.Invariant($".go xyz {at.X + CastingYards:F1} {at.Y:F1} {at.Z + 1f:F1}"));
        }
        else if (age > 2.5 && Player is DesktopPlayer desktop)
        {
            var away = node.GlobalPosition - desktop.GlobalPosition;
            desktop.Rotation = new Vector3(0, Mathf.Atan2(-away.X, -away.Z), 0);
        }

        return true;
    }

    private bool? MissileAtFoe(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _seenAt, _fightShot) = (0, 0, -1, false);
            if (NearestFoe() is not { } foe)
            {
                Note("  nothing hostile in view");
                return false;
            }

            _castAt = (ulong)foe.Guid;
            Server.SetTarget(_castAt);
            Note($"  target {Server.Units[_castAt].Name}");
        }

        if (!StandOffFrom(_castAt, First, age) && _phase == 0)
        {
            Note("  the creature went");
            return false;
        }

        switch (_phase)
        {
            case 0 when age > 4:
            {
                var spells = Server.Spells!;
                var spell = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>()
                    .Where(s => s.Aim == SpellAim.Enemy && !s.IsPassive && s.Speed > 0 && s.Range >= CastingYards + 3
                        && spells.VisualOf(s.Id)?.Missile is not null && s.CastTimeMs <= 3500 && Server.HasPowerFor(s) && Server.CooldownOf((uint)s.Id).Left <= 0)
                    .OrderByDescending(s => s.CastTimeMs > 0)
                    .FirstOrDefault();
                if (spell is null)
                {
                    Note("  no spell with a missile to cast");
                    return false;
                }

                _countsBefore = Effects?.MissileCounts ?? default;
                Note($"  casting {spell.FullName} ({spell.Id}), cast {spell.CastTimeMs} ms, missile at {spell.Speed} yards a second");
                Server.Cast((uint)spell.Id);
                NextPhase(age);
                break;
            }
            case 1:
            {
                var counts = Effects?.MissileCounts ?? default;
                if (counts.Launched > _countsBefore.Launched && _seenAt < 0)
                {
                    _seenAt = age;
                }

                if (_seenAt >= 0 && !_fightShot && age - _seenAt > 0.25)
                {
                    _fightShot = true;
                    Shot("missile");
                }

                if (counts.Arrived > _countsBefore.Arrived)
                {
                    Note($"  launched {counts.Launched - _countsBefore.Launched}, arrived {counts.Arrived - _countsBefore.Arrived}, impacts played {counts.Impacts - _countsBefore.Impacts}, after {age - _phaseAt:F1} s");
                    Shot("impact");
                    return true;
                }

                if (age - _phaseAt > 8)
                {
                    Note($"  launched {counts.Launched - _countsBefore.Launched}, arrived {counts.Arrived - _countsBefore.Arrived}");
                    return false;
                }

                break;
            }
        }

        return null;
    }

    private bool? FoeCasts(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _seenAt, _fightShot) = (0, 0, -1, false);
            if (NearestFoe() is not { } foe)
            {
                Note("  nothing hostile left in view");
                return false;
            }

            _castAt = (ulong)foe.Guid;
            Server.SetTarget(_castAt);
            Note($"  target {Server.Units[_castAt].Name}");
        }

        StandOffFrom(_castAt, First, age);
        switch (_phase)
        {
            case 0 when age > 4:
                _countsBefore = Effects?.MissileCounts ?? default;
                Server.Say($".cast back {FrostboltSpell}");
                NextPhase(age);
                break;
            case 1:
                if (Server.CastOf(_castAt) is { } cast && _seenAt < 0)
                {
                    _seenAt = age;
                    Note($"  its frame shows {Server.Spells?.Get((int)cast.Spell)?.Name ?? cast.Spell.ToString()} for {cast.Duration:F1} s");
                }

                if (_seenAt >= 0 && !_fightShot && age - _seenAt > 0.6)
                {
                    _fightShot = true;
                    Shot("enemy-cast");
                }

                var counts = Effects?.MissileCounts ?? default;
                if (_seenAt >= 0 && Server.CastOf(_castAt) is null && counts.Arrived > _countsBefore.Arrived)
                {
                    Note($"  the bar went when it went off; its missile arrived ({counts.Arrived - _countsBefore.Arrived})");
                    return true;
                }

                if (age - _phaseAt > 8)
                {
                    Note($"  cast seen: {_seenAt >= 0}, missiles launched {counts.Launched - _countsBefore.Launched}, arrived {counts.Arrived - _countsBefore.Arrived}");
                    return false;
                }

                break;
        }

        return null;
    }

    private bool? InterruptedOnFrame(double age)
    {
        var self = Server.Session.SelfGuid;
        if (First)
        {
            (_phase, _phaseAt, _seenAt) = (0, 0, -1);
            Server.SetTarget(self);
        }

        switch (_phase)
        {
            case 0 when age > 1:
                Server.Say($".cast self {BandageSpell}");
                NextPhase(age);
                break;
            case 1 when Server.CastOf(self) is { Channel: true }:
                Note($"  channeling {Server.Spells?.Get((int)BandageSpell)?.Name}");
                NextPhase(age);
                break;
            case 1 when age - _phaseAt > 5:
                Note("  no channel came");
                return false;
            case 2 when age - _phaseAt > 1.5:
                Shot("channel");
                Server.Session.CancelChannel(BandageSpell);
                NextPhase(age);
                break;
            case 3 when Server.CastOf(self) is { InterruptedAt: not null }:
                Note($"  interrupted after {age - _phaseAt:F2} s");
                Shot("interrupted");
                NextPhase(age);
                break;
            case 3 when age - _phaseAt > 2:
                Note($"  after cancelling: {(Server.CastOf(self) is { } left ? $"still {left}" : "no bar")}");
                return false;
            case 4 when age - _phaseAt > InterruptedSecondsAndABit:
                var gone = Server.CastOf(self) is null;
                Note($"  the bar {(gone ? "went" : "stayed")} after a second");
                Server.SetTarget(0);
                return gone;
        }

        return null;
    }

    private const double InterruptedSecondsAndABit = ServerWorld.InterruptedSeconds + 0.3;

    private bool? BlizzardOnFoe(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _fightShot) = (0, 0, false);
            Server.Say($".learn {BlizzardSpell}");
            if (NearestFoe() is not { } foe)
            {
                Note("  nothing hostile left in view");
                Server.Say($".unlearn {BlizzardSpell}");
                return false;
            }

            _castAt = (ulong)foe.Guid;
            Server.SetTarget(_castAt);
            Note($"  target {Server.Units[_castAt].Name}");
        }

        StandOffFrom(_castAt, First, age);
        switch (_phase)
        {
            case 0 when age > 4:
                if (Creatures.LiveNode((long)_castAt) is not { } node)
                {
                    Note("  the creature went");
                    Server.Say($".unlearn {BlizzardSpell}");
                    return false;
                }

                Server.Cast(BlizzardSpell);
                if (Server.Aiming?.Id != (int)BlizzardSpell)
                {
                    Note("  Blizzard didn't aim");
                    Server.Say($".unlearn {BlizzardSpell}");
                    return false;
                }

                Server.CastAt(Tiles.LocalToWow(node.GlobalPosition));
                NextPhase(age);
                break;
            case 1 when age - _phaseAt > 2.5 && !_fightShot:
                _fightShot = true;
                Shot("blizzard");
                Note($"  {(Server.Casting is { Channel: true } ? "channeling" : "not channeling")}; {Effects?.RainReport}");
                break;
            case 1 when age - _phaseAt > 3:
                var shards = Effects?.ShowingShards ?? 0;
                Server.Session.CancelChannel(BlizzardSpell);
                Server.Say($".unlearn {BlizzardSpell}");
                Note($"  {shards} pieces of ice falling");
                return shards > 0;
        }

        return null;
    }
}
