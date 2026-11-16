using System.Collections.Generic;
using System.Linq;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--creature-check</c> places sample creatures, then prints PASS or FAIL for whether they
/// appear, animate, move, stay on the ground and keep to the frame budget, turn in place, and
/// fight as the server's melee has them: stand ready, swing and stagger at a critical, and quits.
/// </summary>
public partial class CreatureCheck : Node
{
    private const double GiveUpSeconds = 120;
    private const double WatchSeconds = 20;
    private const double SettleSeconds = 2;
    private const float MaxFrameMilliseconds = 50f;
    private const float MinBoneMovement = 0.001f;
    private const float MinWalk = 1f;
    private const float MaxHeightAboveGround = 1.5f;

    public required TileManager Tiles { get; init; }
    public required CreatureManager Creatures { get; init; }

    private double _age;
    private double _watched = -1;
    private double _settled;
    private float _worstFrame;
    private bool _sawWalkClip;
    private float _worstGroundGap;
    private readonly Dictionary<CreatureNode, Vector3> _startPositions = [];
    private readonly Dictionary<CreatureNode, float> _moved = [];
    private readonly Dictionary<CreatureNode, Transform3D[]> _startPoses = [];
    private readonly HashSet<CreatureNode> _posed = [];

    public override void _Process(double delta)
    {
        _age += delta;
        if (_watched < 0)
        {
            var ready = Creatures.Nodes.Any() && !Creatures.IsBusy && !Tiles.IsBusy && !Tiles.Covered;
            _settled = ready ? _settled + delta : 0;
            if (_settled >= SettleSeconds)
            {
                _watched = 0;
                foreach (var node in Creatures.Nodes)
                {
                    _startPositions[node] = node.GlobalPosition;
                    _startPoses[node] = Pose(node);
                }
            }
            else if (_age > GiveUpSeconds)
            {
                Finish(false, "no creatures appeared");
            }

            return;
        }

        _watched += delta;
        // A picture read back from the GPU stalls the frames around it, which aren't the creatures' cost.
        if (_ignoreFrames > 0)
        {
            _ignoreFrames--;
        }
        else
        {
            _worstFrame = Mathf.Max(_worstFrame, (float)delta * 1000f);
        }
        WatchTurning(delta);
        WatchMelee();
        foreach (var (node, start) in _startPositions)
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                continue;
            }

            _moved[node] = Mathf.Max(_moved.GetValueOrDefault(node), node.GlobalPosition.DistanceTo(start));
            _sawWalkClip |= node.Clip.StartsWith("4_") || node.Clip.StartsWith("5_");
            if (!_posed.Contains(node) && Pose(node).Zip(_startPoses[node]).Any(p => p.First.Origin.DistanceTo(p.Second.Origin) > MinBoneMovement || !p.First.Basis.IsEqualApprox(p.Second.Basis)))
            {
                _posed.Add(node);
            }

            var ground = RayDown(node.GlobalPosition + Vector3.Up * 0.5f);
            if (ground is { } y)
            {
                _worstGroundGap = Mathf.Max(_worstGroundGap, Mathf.Abs(node.GlobalPosition.Y - y));
            }
        }

        if (_watched < WatchSeconds)
        {
            return;
        }

        var nodes = _startPositions.Count;
        var standing = Named(SampleSpawns.StandingName);
        var wandering = Named(SampleSpawns.WanderingName);
        var patrolling = Named(SampleSpawns.PatrollingName);
        GD.Print($"Creatures: {nodes} nodes of {Creatures.SpawnCount} spawns, {_posed.Count} changed pose, {Engine.GetFramesPerSecond():F0} FPS, worst frame {_worstFrame:F1} ms, worst ground gap {_worstGroundGap:F2}, {Creatures.Sounds.PlayedCount} sounds, {Creatures.Nodes.Count(n => n.FloorLightAmount > 0.5f)} lit by a floor");

        var problems = new List<string>();
        if (_posed.Count < nodes)
        {
            problems.Add($"only {_posed.Count} of {nodes} animate");
        }

        if (standing.Any(n => _moved.GetValueOrDefault(n) > 0.01f))
        {
            problems.Add("a standing creature moved");
        }

        if (wandering.Any() && wandering.All(n => _moved.GetValueOrDefault(n) < MinWalk))
        {
            problems.Add("no wandering creature moved");
        }

        if (patrolling.Any() && patrolling.All(n => _moved.GetValueOrDefault(n) < MinWalk))
        {
            problems.Add("no patrolling creature moved");
        }

        if (!_sawWalkClip)
        {
            problems.Add("nothing played a walk");
        }

        if (_worstGroundGap > MaxHeightAboveGround)
        {
            problems.Add($"a creature was {_worstGroundGap:F2} yards from the ground");
        }

        if (Creatures.Sounds.PlayedCount == 0)
        {
            problems.Add("no creature made a sound");
        }

        if (_turner is null)
        {
            problems.Add("no standing creature to turn");
        }
        else
        {
            GD.Print($"Turning in place: shuffled {(_sawShuffle ? "yes" : "no")} (model has {(_turner.ClipFor(Formats.Models.M2SequenceIds.ShuffleLeft) is null ? "no" : "a")} shuffle), fastest {_fastestTurn:F2} rad/s, back {Mathf.Abs(Mathf.AngleDifference(_turner.Facing, _turnFrom)):F2} rad from where it faced, now playing {_turner.Clip}");
            if (_turner.ClipFor(Formats.Models.M2SequenceIds.ShuffleLeft) is not null && !_sawShuffle)
            {
                problems.Add("turning in place didn't play the shuffle");
            }

            if (_fastestTurn > 2.7f)
            {
                problems.Add($"turning in place was {_fastestTurn:F2} rad/s");
            }

            if (Mathf.Abs(Mathf.AngleDifference(_turner.Facing, _turnFrom)) > 0.05f || _turner.Clip.StartsWith("11_") || _turner.Clip.StartsWith("12_"))
            {
                problems.Add("it didn't turn back and stand");
            }
        }

        GD.Print($"Melee: {_melee}");
        if (_meleeProblem is { } melee)
        {
            problems.Add(melee);
        }

        if (_worstFrame > MaxFrameMilliseconds)
        {
            problems.Add($"a frame took {_worstFrame:F1} ms");
        }

        Finish(problems.Count == 0, string.Join(", ", problems));
    }

    private CreatureNode? _turner;
    private float _turnFrom;
    private float _lastFacing;
    private bool _sawShuffle;
    private float _fastestTurn;

    /// <summary>A standing creature turns most of the way round, as to someone talking to it, then back.</summary>
    private void WatchTurning(double delta)
    {
        if (_turner is null && _watched > 3)
        {
            _turner = Named(SampleSpawns.StandingName).FirstOrDefault();
            if (_turner is not null)
            {
                _turnFrom = _lastFacing = _turner.Facing;
                Creatures.TurnTo(_turner, _turnFrom + 2f, 3);
            }

            return;
        }

        if (_turner is null || !GodotObject.IsInstanceValid(_turner) || delta <= 0)
        {
            return;
        }

        _sawShuffle |= _turner.Clip.StartsWith("11_") || _turner.Clip.StartsWith("12_");
        _fastestTurn = Mathf.Max(_fastestTurn, Mathf.Abs(Mathf.AngleDifference(_lastFacing, _turner.Facing)) / (float)delta);
        _lastFacing = _turner.Facing;
    }

    private int _meleeStep;
    private CreatureNode? _fighter;
    private CreatureNode? _walker;
    private bool _shot;
    private int _ignoreFrames;
    private string _walking = "nothing walked";
    private string _melee = "not watched";
    private string? _meleeProblem;

    /// <summary>
    /// Once the turner has turned back: it stands ready, swings with its weapon's clip, staggers at a
    /// critical, then stands down to its idles. A model without one of these clips skips it.
    /// </summary>
    private void WatchMelee()
    {
        // A standing creature with a ready stance, a character rather than the wolf, else any.
        _fighter ??= _watched > 10 ? Named(SampleSpawns.StandingName).OrderBy(n => n.ClipFor(Formats.World.MeleeAnimations.ReadyUnarmed) is null ? 1 : 0).FirstOrDefault() : null;
        if (_fighter is null || !GodotObject.IsInstanceValid(_fighter))
        {
            return;
        }

        static string Id(string clip) => clip.Split('_')[0];
        var node = _fighter;

        // From 12 seconds on, the first creature seen walking that has a spine swings, and keeps walking.
        if (_walker is null && _watched > 12 && _watched < 19
            && Creatures.Nodes.FirstOrDefault(n => n != node && n.CanLayer && Id(n.Clip) is "4" or "5") is { } walker)
        {
            _walker = walker;
            walker.PlaySwing(offHand: false);
            _walking = $"{walker.Name} swung walking: legs {walker.Clip}, arms {(walker.UpperBodyPlaying ? "swinging" : "not")}";
            if (Id(walker.Clip) is not ("4" or "5") || !walker.UpperBodyPlaying)
            {
                _meleeProblem ??= "a swing on the move stopped the walk or didn't play on the arms";
            }
        }

        switch (_meleeStep)
        {
            case 0 when _watched > 11:
                node.CombatReady = true;
                _meleeStep++;
                break;
            case 1 when _watched > 12:
                var readyHas = Formats.World.MeleeAnimations.Ready(node.Weapons.Grip).Any(s => node.ClipFor(s) is not null);
                _melee = $"{node.Name}: ready {node.Clip}";
                if (readyHas && Id(node.Clip) is not ("25" or "26" or "27" or "28"))
                {
                    _meleeProblem = $"standing ready played {node.Clip}";
                }

                var timing = node.PlaySwing(offHand: false);
                _melee += $", swing {node.Clip} (whoosh at {timing?.Swing:F2} s, blow at {timing?.Hit:F2} s)";
                if (timing is null || Id(node.Clip) is not ("16" or "17" or "18" or "19"))
                {
                    _meleeProblem ??= $"the swing played {node.Clip}";
                }

                _meleeStep++;
                break;
            case 2 when _watched > 14:
                _melee += $", after it {node.Clip}";
                node.PlayReaction(Formats.World.MeleeOutcome.Critical);
                _melee += $", critical {node.Clip}";
                if (Formats.World.MeleeAnimations.Reaction(Formats.World.MeleeOutcome.Critical, node.Weapons).Any(s => node.ClipFor(s) is not null)
                    && Id(node.Clip) is not ("8" or "9" or "10"))
                {
                    _meleeProblem ??= $"the critical played {node.Clip}";
                }

                _meleeStep++;
                break;
            case 3 when _watched > 16:
                node.CombatReady = false;
                _meleeStep++;
                break;
            case 4 when _watched > 17:
                _melee += $", stood down {node.Clip}";
                if (Id(node.Clip) is "25" or "26" or "27" or "28")
                {
                    _meleeProblem ??= "it stayed ready";
                }

                // Swinging standing, then walking: the rest of the swing goes on in the upper body.
                node.PlaySwing(offHand: false);
                _meleeStep++;
                break;
            case 5 when _watched > 17.3:
                node.PlayMotion(Formats.Models.M2SequenceIds.Walk, 2f);
                _melee += $", walked off mid-swing: legs {node.Clip}, arms {(node.UpperBodyPlaying ? "swinging" : "not")}";
                if (Id(node.Clip) != "4" || !node.UpperBodyPlaying)
                {
                    _meleeProblem ??= "walking off mid-swing didn't carry the swing on in the arms";
                }

                _meleeStep++;
                break;
            case 6 when _watched > 17.6 && !_shot:
                // With --shots=folder, a picture of the swing carried on over the walk, to judge the blend by eye.
                _shot = true;
                if (OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith("--shots="))?["--shots=".Length..] is { } folder)
                {
                    var camera = GetViewport().GetCamera3D();
                    camera.LookAt(node.GlobalPosition + (Vector3.Up * node.Height * 0.6f));
                    GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, "melee-walk-swing.png"));
                    _ignoreFrames = 3;
                }

                break;
            case 6 when _watched > 19:
                _melee += $", a second later arms {(node.UpperBodyPlaying ? "still swinging" : "done")}; {_walking}";
                if (node.UpperBodyPlaying)
                {
                    _meleeProblem ??= "the upper body's swing didn't end";
                }

                if (_walker is null)
                {
                    _meleeProblem ??= "no creature with a spine walked to swing on the move";
                }

                _meleeStep++;
                break;
        }
    }

    private IEnumerable<CreatureNode> Named(string name) =>
        Creatures.NodesNamed(name).Where(_startPositions.ContainsKey);

    private static Transform3D[] Pose(CreatureNode node)
    {
        var bones = node.Skeleton.GetBoneCount();
        var pose = new Transform3D[bones];
        for (var i = 0; i < bones; i++)
        {
            pose[i] = node.Skeleton.GetBoneGlobalPose(i);
        }

        return pose;
    }

    private float? RayDown(Vector3 from)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Down * 8f, TileManager.TerrainLayer);
        var result = GetViewport().FindWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count > 0 ? result["position"].AsVector3().Y : null;
    }

    private void Finish(bool passed, string problems)
    {
        GD.Print(passed ? "Creature check: PASS" : $"Creature check: FAIL: {problems}");
        GetTree().Quit(passed ? 0 : 1);
    }
}
