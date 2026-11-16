using System.Collections.Generic;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// VR bodies on a server that speaks WowVR's protocol (<c>docs/vr-multiplayer-protocol.md</c>):
/// in VR, the player's pose goes out at the rate the server asks for; and other VR players'
/// characters are posed from theirs, drawn a little behind so poses arriving unevenly still move
/// smoothly, with the same body solver as the player's own. A character whose poses stop, or
/// who leaves VR, goes back to its animation. On a server without it, nothing happens.
/// </summary>
public partial class VrMultiplayer : Node
{
    /// <summary>Received poses are drawn this many send intervals late, to have one on each side.</summary>
    private const double DrawBehindIntervals = 1.5;

    public required ServerWorld Server { get; init; }
    public required CreatureManager Creatures { get; init; }
    public required Node3D Player { get; init; }

    private readonly Dictionary<ulong, Remote> _remotes = [];
    private double _sinceSent;
    private ushort _sequence;
    private bool _sentVr;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>For checks: poses sent, and remote bodies being posed now.</summary>
    public int PosesSent { get; private set; }

    public int PosedRemotes { get; private set; }

    /// <summary>Another player's VR body: its last two poses, when they came, and its solver.</summary>
    private sealed class Remote
    {
        public VrPoseMessage? Previous;
        public VrPoseMessage? Latest;
        public double PreviousAt;
        public double LatestAt;
        public VrState State;
        public CreatureNode? Node;
        public VrBodyIk? Ik;
        public bool Posing;
    }

    public override void _Ready()
    {
        // After the creatures have moved and animated this frame, so the poses go on top.
        ProcessPriority = 100;
        Server.VrMessage += OnMessage;
    }

    public override void _ExitTree()
    {
        Server.VrMessage -= OnMessage;
        if (_sentVr)
        {
            Server.Session.SendVrState(VrState.None);
        }
    }

    private void OnMessage(SessionEvent message)
    {
        var now = _clock.Elapsed.TotalSeconds;
        switch (message)
        {
            case VrPoseReceived { Guid: var guid, Pose: var pose }:
                var remote = _remotes.TryGetValue(guid, out var known) ? known : _remotes[guid] = new Remote();

                // Older than the last drawn: it came late, and is dropped.
                if (remote.Latest is { } latest && (short)(pose.Sequence - latest.Sequence) <= 0)
                {
                    break;
                }

                (remote.Previous, remote.PreviousAt) = (remote.Latest, remote.LatestAt);
                (remote.Latest, remote.LatestAt) = (pose, now);
                remote.State = pose.State;
                break;
            case VrStateReceived { Guid: var guid, State: var state }:
                if (_remotes.TryGetValue(guid, out var changed))
                {
                    changed.State = state;
                }

                break;
        }
    }

    public override void _Process(double delta)
    {
        if (Server.Session.Vr is not { } vr)
        {
            return;
        }

        SendOwnPose(vr, delta);
        PoseRemotes(vr);
    }

    private void SendOwnPose(VrReady vr, double delta)
    {
        if (Player is not Xr.XrPlayer || Server.Avatar is not { } avatar)
        {
            return;
        }

        _sinceSent += delta;
        if (_sinceSent < 1.0 / System.Math.Max((int)vr.SendRate, 1))
        {
            return;
        }

        if (avatar.CharacterPose(_sequence, (ushort)_clock.ElapsedMilliseconds) is not { } pose)
        {
            // Dead, or the body isn't built: others see the character as anyone else.
            if (_sentVr)
            {
                Server.Session.SendVrState(VrState.None);
                _sentVr = false;
            }

            return;
        }

        _sinceSent = 0;
        _sequence++;
        Server.Session.SendVrPose(pose);
        _sentVr = true;
        PosesSent++;
    }

    private void PoseRemotes(VrReady vr)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var interval = 1.0 / System.Math.Max((int)vr.SendRate, 1);
        var timeout = vr.PoseTimeoutMs / 1000.0;
        var posed = 0;
        List<ulong>? gone = null;
        foreach (var (guid, remote) in _remotes)
        {
            var node = Creatures.LiveNode((long)guid);
            var fresh = remote.Latest is not null && now - remote.LatestAt < timeout && (remote.State & VrState.InVr) != 0;
            if (node != remote.Node)
            {
                // A new node, built again: the old one took its solver with it.
                remote.Ik = null;
                remote.Posing = false;
                remote.Node = node;
            }
            else if (!fresh)
            {
                Stop(remote);
            }

            if (node is null || !fresh)
            {
                if (!fresh && now - remote.LatestAt > timeout * 10)
                {
                    (gone ??= []).Add(guid);
                }

                continue;
            }

            remote.Ik ??= VrBodyIk.Create(node.Skeleton, node.Asset);
            if (remote.Ik is null)
            {
                continue;
            }

            // Drawn behind by a little more than one interval, between the two poses around then.
            var pose = remote.Latest!;
            if (remote.Previous is { } previous && remote.LatestAt > remote.PreviousAt)
            {
                var drawAt = now - (interval * DrawBehindIntervals);
                var t = (float)Mathf.Clamp((drawAt - remote.PreviousAt) / (remote.LatestAt - remote.PreviousAt), 0, 1);
                pose = VrPoseSpace.Between(previous, remote.Latest!, t);
            }

            // The server places the character; the pose goes on it, where it stands and as it faces.
            var feet = node.GlobalPosition;
            if (VrPoseSpace.Decode(pose, feet, node.Yaw) is not { } decoded)
            {
                continue;
            }

            node.GlobalPosition = decoded.Stand;
            // A VR player doesn't fidget: an idle's sway would move the hips, and so where the arms reach.
            node.NoIdle = true;
            remote.Ik.Solve(decoded.Pose);
            remote.Posing = true;
            posed++;
        }

        foreach (var guid in gone ?? [])
        {
            _remotes.Remove(guid);
        }

        PosedRemotes = posed;
    }

    /// <summary>Gives a body back to its animation, keeping its solver for when the poses come again.</summary>
    private static void Stop(Remote remote)
    {
        if (remote.Posing && remote.Node is not null && GodotObject.IsInstanceValid(remote.Node))
        {
            remote.Ik?.Release();
            remote.Node.NoIdle = false;
        }

        remote.Posing = false;
    }

    /// <summary>For checks: poses another player's character as if the server had relayed this.</summary>
    public void Receive(ulong guid, VrPoseMessage pose) => OnMessage(new VrPoseReceived(guid, pose));
}
