using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Desktop;
using WowVR.Formats.World;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--melee-check</c>: on Tirisfal's coast, turns melee on and off with the Attack button, swings
/// at a creature and lets another fight back, and checks the clips both play, the sounds, the
/// button lighting and the swings following a new target. GM mode goes off for it, so creatures
/// fight back, and back on after.
/// </summary>
public partial class PlayCheck
{
    private ulong _foe;
    private int _soundsBefore;
    private readonly HashSet<string> _selfClips = [];
    private readonly HashSet<string> _foeClips = [];
    private readonly List<SwingHappened> _swings = [];
    private bool _listening;

    private void AddMeleeSteps()
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
        Step("attack button turns melee on and off", AttackButton);
        Step("swing: clips, reaction and sounds", Swing);

        // Elites with three times the health, so a GM's character doesn't kill one with its first swing.
        GoTo("go to the cursed elites in the Alterac foothills", new System.Numerics.Vector3(-1233, 561, 34));
        Step("a creature fights back", FightBack);
        Step("gm on again", age =>
        {
            if (First)
            {
                Server.StopAttack();
                Server.Say(".gm on");
                Server.Say(".revive");
            }

            return age > 2 ? true : null;
        });
    }

    private void Listen()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        Server.Swung += swing => _swings.Add(swing);
    }

    /// <summary>Faces a unit and stands next to it.</summary>
    private void Approach(Node3D node)
    {
        GoNextTo(node);
        Face(node);
    }

    private void Face(Node3D node)
    {
        if (Player is DesktopPlayer desktop)
        {
            var away = node.GlobalPosition - desktop.GlobalPosition;
            desktop.Rotation = new Vector3(0, Mathf.Atan2(-away.X, -away.Z), 0);
        }
    }

    /// <summary>
    /// The nearest creature to fight. With GM mode on the character is friendly to everything, so
    /// any that isn't friendly will do; <paramref name="hostile"/> wants one that fights back.
    /// </summary>
    private (long Guid, Node3D Node)? NearestFoe(ulong except = 0, bool hostile = false) =>
        Nearest(u => u.Guid != except && !u.IsDead && u.Type == ObjectType.Unit && !u.IsNpc && !u.NotAttackable
            && (hostile ? Server.ReactionTo(u) == Reaction.Hostile : Server.ReactionTo(u) != Reaction.Friendly));

    /// <summary>The Attack button with a target turns melee on, lights, and again turns it off; changing target while on follows it.</summary>
    private bool? AttackButton(double age)
    {
        if (First)
        {
            (_phase, _phaseAt) = (0, 0);
            Listen();
            if (NearestFoe() is not { } foe)
            {
                Note("  nothing hostile in view");
                return false;
            }

            _foe = (ulong)foe.Guid;
            Server.SetTarget(_foe);
            Approach(foe.Node);
            Note($"  target {Server.Units[_foe].Name}");
        }

        switch (_phase)
        {
            case 0 when age > 3:
                Server.Cast(ServerWorld.AttackSpell);
                NextPhase(age);
                break;
            case 1 when Server.Attacking:
                Note($"  attacking after {age - _phaseAt:F1} s; button lit: {Server.IsActive(ServerWorld.AttackSpell)}");
                if (!Server.IsActive(ServerWorld.AttackSpell))
                {
                    return false;
                }

                // Another enemy selected while attacking: the swings follow it.
                if (NearestFoe(_foe) is { } other)
                {
                    Server.SetTarget((ulong)other.Guid);
                    Note($"  switched target to {Server.Units[(ulong)other.Guid].Name}");
                }

                NextPhase(age);
                break;
            case 2 when age - _phaseAt > 1:
                Note($"  still attacking after the switch: {Server.Attacking}");
                Server.Cast(ServerWorld.AttackSpell);
                NextPhase(age);
                break;
            case 3 when !Server.Attacking:
                Note($"  stopped; button lit: {Server.IsActive(ServerWorld.AttackSpell)}");
                return !Server.IsActive(ServerWorld.AttackSpell);
        }

        return age - _phaseAt > 6 ? false : null;
    }

    /// <summary>Swings at a creature: the body stands ready and swings, the creature takes the blow, and sounds play.</summary>
    private bool _closedIn;
    private bool _faced;

    private bool? Swing(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _closedIn, _faced) = (0, 0, false, false);
            _swings.Clear();
            _selfClips.Clear();
            _foeClips.Clear();
            if (NearestFoe() is not { } foe)
            {
                Note("  nothing hostile in view");
                return false;
            }

            _foe = (ulong)foe.Guid;
            Server.SetTarget(_foe);
            Approach(foe.Node);
            Note($"  swinging at {Server.Units[_foe].Name}");
        }

        Sample();
        switch (_phase)
        {
            // The teleport lands after the facing was set, and the creature may have wandered:
            // next to it again, then facing it once there.
            case 0 when age > 2 && !_closedIn:
                _closedIn = true;
                if (Creatures.LiveNode((long)_foe) is { } again)
                {
                    GoNextTo(again);
                }

                break;
            case 0 when age > 3.5 && !_faced:
                _faced = true;
                if (Creatures.LiveNode((long)_foe) is { } there)
                {
                    Face(there);
                }

                break;
            case 0 when age > 4:
                _soundsBefore = Creatures.SoundPlayer.PlayedCount;
                Server.Cast(ServerWorld.AttackSpell);
                NextPhase(age);
                break;
            case 1 when _swings.Any(s => s.Attacker == Server.Session.SelfGuid) && age - _phaseAt > 2.5:
                var mine = _swings.Where(s => s.Attacker == Server.Session.SelfGuid).ToList();
                var outcomes = string.Join(", ", mine.Select(s => MeleeHits.Outcome(s.HitInfo, s.VictimState, s.Damage)));
                var sounds = Creatures.SoundPlayer.PlayedCount - _soundsBefore;
                Note($"  {mine.Count} swings ({outcomes}); body clips {string.Join(' ', _selfClips)}; its clips {string.Join(' ', _foeClips)}; {sounds} sounds");
                Shot("melee-swing");
                var swung = _selfClips.Any(c => c.Split('_')[0] is "16" or "17" or "18" or "19" or "87" or "117");
                var reacted = _foeClips.Any(c => c.Split('_')[0] is "8" or "9" or "10" or "1" or "6" or "30" or "20" or "21" or "22" or "23" or "24")
                    || mine.All(s => s.Missed);
                Server.StopAttack();
                return swung && reacted && sounds > 0;
        }

        return age - _phaseAt > 12 ? false : null;
    }

    /// <summary>
    /// GM mode off, a creature attacked and left to fight back: it stands ready and swings, the
    /// player's body takes the blows, and the swings sound.
    /// </summary>
    private bool? FightBack(double age)
    {
        if (First)
        {
            (_phase, _phaseAt) = (0, 0);
            _swings.Clear();
            _selfClips.Clear();
            _foeClips.Clear();
            Server.Say(".gm off");
            _foe = 0;
        }

        // GM mode going off takes a moment to change the character's faction, which says who's hostile.
        if (_foe == 0)
        {
            if (age < 2)
            {
                return null;
            }

            if (NearestFoe(hostile: true) is not { } foe)
            {
                Note("  nothing hostile in view");
                return false;
            }

            _foe = (ulong)foe.Guid;
            Server.SetTarget(_foe);
            Approach(foe.Node);
            Note($"  provoking {Server.Units[_foe].Name}");
            _phaseAt = age;
        }

        Sample();
        switch (_phase)
        {
            case 0 when age - _phaseAt > 3:
                // One swing starts the fight; then the player stops and lets it hit back.
                Server.Cast(ServerWorld.AttackSpell);
                NextPhase(age);
                break;
            case 1 when age - _phaseAt > 1.5:
                Server.Cast(ServerWorld.AttackSpell);
                _soundsBefore = Creatures.SoundPlayer.PlayedCount;
                NextPhase(age);
                break;
            case 2 when _swings.Count(s => s.Target == Server.Session.SelfGuid) >= 2 && age - _phaseAt > 2:
                var theirs = _swings.Where(s => s.Target == Server.Session.SelfGuid).ToList();
                var outcomes = string.Join(", ", theirs.Select(s => MeleeHits.Outcome(s.HitInfo, s.VictimState, s.Damage)));
                var sounds = Creatures.SoundPlayer.PlayedCount - _soundsBefore;
                Note($"  it swung {theirs.Count} times ({outcomes}); its clips {string.Join(' ', _foeClips)}; body clips {string.Join(' ', _selfClips)}; {sounds} sounds");
                Shot("melee-taken");
                var ready = _foeClips.Any(c => c.Split('_')[0] is "25" or "26" or "27" or "28");
                var swung = _foeClips.Any(c => c.Split('_')[0] is "16" or "17" or "18" or "19" or "87" or "117");
                return swung && sounds > 0 && (ready || theirs.Count > 0);
            case 2 when Server.Units.TryGetValue(_foe, out var dead) && dead.IsDead:
                Note("  it died before swinging back");
                return false;
        }

        return age - _phaseAt > 20 ? false : null;
    }

    /// <summary>Notes the clips the body and the foe play.</summary>
    private void Sample()
    {
        if (Server.Avatar?.BodyClip is { Length: > 0 } mine)
        {
            _selfClips.Add(mine);
        }

        if (Creatures.LiveNode((long)_foe)?.Clip is { Length: > 0 } theirs)
        {
            _foeClips.Add(theirs);
        }
    }
}
