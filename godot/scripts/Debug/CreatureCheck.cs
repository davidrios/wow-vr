using System.Collections.Generic;
using System.Linq;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--creature-check</c> places sample creatures, then prints PASS or FAIL for whether they
/// appear, animate, move, stay on the ground and keep to the frame budget, and quits.
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
        _worstFrame = Mathf.Max(_worstFrame, (float)delta * 1000f);
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
        if (_posed.Count < System.Math.Min(nodes, CreatureManager.MaxAnimated))
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

        if (_worstFrame > MaxFrameMilliseconds)
        {
            problems.Add($"a frame took {_worstFrame:F1} ms");
        }

        Finish(problems.Count == 0, string.Join(", ", problems));
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
