using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.Models;
using WowVR.Formats.World;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Another player as their movement packets have them: a character beside the start jumps (the
/// jump clip, rising about as high as the game's jump, and kept on the ground until its landing's
/// packet though the arc goes on under it), lands, jumps and lands on the run, falls, swims, swims on the spot, walks
/// backwards and strafes, each with its clip, then stands.
/// </summary>
public partial class CreatureCheck
{
    private const long OtherPlayerGuid = 0x7000_0000_0000_0001;
    private const float JumpSpeed = 7.96f;

    /// <summary>The game's jump, 7.96 yards a second up against its gravity, rises 1.64 yards.</summary>
    private const float JumpRise = JumpSpeed * JumpSpeed / (2f * PlayerMove.Gravity);

    private int _otherStep;
    private double _otherAt;
    private System.Numerics.Vector3 _otherStart;
    private float _otherGroundY;
    private float _otherPeak;
    private float _otherLowest = float.MaxValue;
    private readonly List<string> _otherNotes = [];
    private string? _otherProblem;
    private readonly List<string> _otherSeen = [];

    private string OtherPlayerReport => _otherNotes.Count == 0 ? "not watched" : string.Join(", ", _otherNotes);

    private void WatchOtherPlayer()
    {
        void Seen(string id)
        {
            if (_otherSeen.Count == 0 || _otherSeen[^1] != id)
            {
                _otherSeen.Add(id);
            }
        }

        static string Id(string clip) => clip.Split('_')[0];
        var node = Creatures.LiveNode(OtherPlayerGuid);
        var since = Creatures.Clock - _otherAt;

        void Expect(string what, params string[] ids)
        {
            var clip = node!.Clip;
            _otherNotes.Add($"{what} {clip}{(node.MotionChosen ? "" : " (not chosen)")}");
            if (!ids.Contains(Id(clip)))
            {
                _otherProblem ??= $"another player {what} played {(clip.Length == 0 ? "nothing" : clip)}";
            }
        }

        void Move(MoveFlags flags, System.Numerics.Vector3? to = null, float up = 0f, float fallen = 0f)
        {
            var path = to is { } end ? new MovePath([_otherStart, end], 2, 0f, false, holdFacing: true) : MovePath.Standing(_otherStart, 0f);
            Creatures.SetLiveMotion(OtherPlayerGuid, new PlayerMotion(path, Creatures.Clock, new PlayerMove(flags, _otherStart.Z, up, fallen)));
            _otherAt = Creatures.Clock;
        }

        switch (_otherStep)
        {
            case 0 when _watched > 4:
            {
                // A character from the samples, standing three yards south of the start, facing north.
                // A character, which has the jump and swim clips; the wolf and kobold come in any order.
                var display = Named(SampleSpawns.StandingName)
                    .Where(n => n.ClipFor(M2SequenceIds.Jump) is not null && n.ClipFor(M2SequenceIds.Swim) is not null)
                    .Select(Creatures.SpawnOf).FirstOrDefault(s => s?.DisplayId is > 0)?.DisplayId;
                if (display is null)
                {
                    _otherProblem = "no sample character with jump and swim clips to copy for another player";
                    _otherStep = -1;
                    return;
                }

                var (_, x, y, z) = Tiles.PlayerLocation;
                _otherStart = new System.Numerics.Vector3((float)x - 3f, (float)y, z);
                Creatures.AddLive(
                    new CreatureSpawn { Guid = OtherPlayerGuid, DisplayId = display.Value, Name = "Sample player", Pos = [_otherStart.X, _otherStart.Y, _otherStart.Z] },
                    new PlayerMotion(MovePath.Standing(_otherStart, 0f), Creatures.Clock, new PlayerMove(MoveFlags.None, _otherStart.Z, 0f, 0f)));
                _otherStep++;
                break;
            }
            case 1 when node is not null && _watched > 6:
            {
                // Its height comes from its packets, so it stands where they say; the ground under it is what the jump must come back to.
                _otherGroundY = RayDown(node.GlobalPosition + (Vector3.Up * 3f)) ?? node.GlobalPosition.Y;
                _otherStart.Z = node.GlobalPosition.Y;
                _otherNotes.Add($"model {node.Asset.Skeleton.Clips.Count} extra clips, jump {node.ClipFor(M2SequenceIds.Jump) ?? "none"}, swim {node.ClipFor(M2SequenceIds.Swim) ?? "none"}");
                Move(MoveFlags.Falling, up: JumpSpeed);
                _otherStep++;
                break;
            }
            case 2:
                _otherPeak = Mathf.Max(_otherPeak, node!.GlobalPosition.Y - _otherStart.Z);
                Seen(Id(node.Clip));

                // The arc would be 4 yards under the ground by now; no landing's packet has come.
                if (since > 1.2)
                {
                    _otherLowest = node.GlobalPosition.Y - _otherGroundY;
                    _otherNotes.Add($"rose {_otherPeak:F2} yd (the game's jump {JumpRise:F2}), {_otherLowest:F2} yd over the ground before landing");
                    if (Mathf.Abs(_otherPeak - JumpRise) > 0.2f)
                    {
                        _otherProblem ??= $"another player's jump rose {_otherPeak:F2} yards";
                    }

                    if (_otherLowest < -0.3f)
                    {
                        _otherProblem ??= $"another player falling sank {-_otherLowest:F2} yards into the ground";
                    }

                    // The start plays once, then the jump's loop.
                    _otherNotes.Add($"in the air played {string.Join(" then ", _otherSeen)}");
                    if (!_otherSeen.Contains("37") || !_otherSeen.Contains("38"))
                    {
                        _otherProblem ??= $"another player's jump played {string.Join(", ", _otherSeen)}, not its start and loop";
                    }

                    _otherSeen.Clear();
                    Move(MoveFlags.None);
                    _otherStep++;
                }

                break;
            case 3:
                // Landing standing plays the landing once, then the idles.
                Seen(Id(node!.Clip));
                if (since > 1.5)
                {
                    _otherNotes.Add($"landing played {string.Join(" then ", _otherSeen)}");
                    if (!_otherSeen.Contains("39"))
                    {
                        _otherProblem ??= $"another player landing played {string.Join(", ", _otherSeen)}, not the landing";
                    }

                    Expect("landed", "0", "1", "2", "3", "60", "61", "62", "63", "64", "65", "66");
                    Move(MoveFlags.Falling | MoveFlags.Forward, _otherStart + new System.Numerics.Vector3(14f, 0f, 0f), up: JumpSpeed);
                    _otherStep = 20;
                }

                break;
            case 20 when since > 0.6:
                // Landing on the run goes straight back to running.
                Move(MoveFlags.Forward, _otherStart + new System.Numerics.Vector3(18f, 0f, 0f));
                _otherStep = 21;
                break;
            case 21 when since > 0.15:
                Expect("landing on the run", "4", "5");
                Move(MoveFlags.Falling, fallen: 0.6f);
                _otherStep = 4;
                break;
            case 4 when since > 0.1:
                Expect("falling", "40");
                Move(MoveFlags.Swimming | MoveFlags.Forward, _otherStart + new System.Numerics.Vector3(8f, 0f, 0f));
                _otherStep++;
                break;
            case 5 when since > 0.5:
                Expect("swimming", "42");
                Move(MoveFlags.Swimming);
                _otherStep++;
                break;
            case 6 when since > 0.5:
                Expect("swimming still", "41");
                Move(MoveFlags.Backward, _otherStart - new System.Numerics.Vector3(4.5f, 0f, 0f));
                _otherStep++;
                break;
            case 7 when since > 0.5:
                Expect("walking backwards", "13");
                Move(MoveFlags.Forward | MoveFlags.StrafeLeft, _otherStart + new System.Numerics.Vector3(5f, 5f, 0f));
                _otherStep++;
                break;
            case 8 when since > 0.7:
            {
                // Facing north and going north-west: the body turns 45 degrees toward it.
                var turned = Mathf.AngleDifference(0f, node!.Facing);
                _otherNotes.Add($"strafing forward and left turned {Mathf.RadToDeg(turned):F0} degrees, playing {node.Clip}");
                if (Mathf.Abs(turned - (Mathf.Pi / 4)) > 0.1f)
                {
                    _otherProblem ??= $"another player strafing turned {Mathf.RadToDeg(turned):F0} degrees";
                }

                Move(MoveFlags.None);
                _otherStep++;
                break;
            }
            case 9 when since > 0.6:
                Expect("stopped", "0", "1", "2", "3", "60", "61", "62", "63", "64", "65", "66");
                _otherStep++;
                break;
        }
    }
}
