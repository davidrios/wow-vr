using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WowVR.Desktop;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--play-check</c> plays on a server with a GM account (<c>--server=</c>, <c>--character=</c>,
/// <c>WOWVR_AC_USER</c> and <c>WOWVR_AC_PASS</c>) and prints PASS or FAIL for each step, then quits:
/// the server's position matching the client's after standing and walking, following a teleport,
/// talking to the nearest NPC, and chat. It turns GM mode on and revives the character first, so
/// nothing attacks it. <c>--play-check=Name</c> then also sends <c>.tele Name</c>, or the command
/// itself when it starts with a dot, as <c>.go xyz 54 0 -18 34</c> into an instance, and checks the
/// position there, after the world loads again when the map changed.
/// </summary>
public partial class PlayCheck : Node
{
    private const double GiveUpSeconds = 240;

    /// <summary>The server and the client agree on where the player is within this, in yards.</summary>
    private const float SyncYards = 3f;

    public required ServerWorld Server { get; init; }
    public required TileManager Tiles { get; init; }
    public required Node3D Player { get; init; }

    /// <summary>Carries the map change's name across the reload.</summary>
    private static string? _afterMapChange;

    private static readonly Regex GpsLine = new(@"X:\s*(-?[\d.]+)\s+Y:\s*(-?[\d.]+)\s+Z:\s*(-?[\d.]+)", RegexOptions.Compiled);

    private readonly List<string> _results = [];
    private readonly Queue<(string Name, Func<double, bool?> Step)> _steps = new();
    private System.Numerics.Vector3? _gps;
    private ChatLine? _said;
    private SessionEvent? _dialog;
    private double _age;
    private double _stepAge;
    private bool _failed;

    public override void _Ready()
    {
        Server.ChatLine += line =>
        {
            if (line.Kind == ChatKind.System && GpsLine.Match(line.Text) is { Success: true } m)
            {
                _gps = new System.Numerics.Vector3(Parse(m.Groups[1].Value), Parse(m.Groups[2].Value), Parse(m.Groups[3].Value));
            }

            if (line.Kind == ChatKind.Say && line.Text.Contains("play check"))
            {
                _said = line;
            }
        };
        Server.Dialog += e => _dialog ??= e is GossipClosed ? null : e;
        Server.Session.Events.Enqueue(new ChatReceived(new ChatLine(ChatKind.Notice, "", "Play check starting")));

        var arguments = OS.GetCmdlineUserArgs();
        var map = arguments.FirstOrDefault(a => a.StartsWith("--play-check="))?["--play-check=".Length..];
        Step("world loaded", _ => Loaded ? true : null);
        if (_afterMapChange is { } changed)
        {
            AddSync($"position after .tele {changed}");
            _afterMapChange = null;
            AddPlaySteps();
        }
        else
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
            AddSync("position standing");
            Step("walk 4 seconds", age =>
            {
                if (Player is DesktopPlayer desktop)
                {
                    desktop.ScriptedWalk = age < 4 ? -Player.GlobalBasis.Z : null;
                }
                else if (age < 4)
                {
                    // The VR player has no scripted walk: it slides along, as the stick would move it.
                    var player = (IPlayer)Player;
                    player.PlaceAt(player.Feet + (Vector3.Forward * AppSettings.WalkSpeed * (float)GetProcessDeltaTime()), keepVelocity: true);
                }

                return age > 5 ? true : null;
            });
            AddSync("position after walking");
            Step("near teleport", age =>
            {
                if (First)
                {
                    var (_, x, y, z) = Tiles.PlayerLocation;
                    Server.Say(FormattableString.Invariant($".go xyz {x + 25:F1} {y:F1} {z + 3:F1}"));
                }

                return age > 4 && Loaded ? true : null;
            });
            AddSync("position after teleport");
            if (map is not null)
            {
                // Another map reloads the scene, and the check carries on there; on this map, it carries on here.
                Step($".tele {map}", age =>
                {
                    if (First)
                    {
                        _afterMapChange = map;
                        Server.Say(map.StartsWith('.') ? map : $".tele {map}");
                    }

                    return age > 6 && Loaded ? true : age > 60 ? false : null;
                });
                AddSync($"position after .tele {map}");
                Step("same map", _ =>
                {
                    _afterMapChange = null;
                    return true;
                });
            }

            AddPlaySteps();
        }

        _stepAge = 0;
    }

    private void AddPlaySteps()
    {
        Step("talk to the nearest NPC", TalkToNearestNpc);
        Step("say", age =>
        {
            if (First)
            {
                Server.Say("play check");
            }

            return _said is not null ? true : age > 5 ? false : null;
        });
    }

    /// <summary>The step's first frame, to act once.</summary>
    private bool First { get; set; }

    private int _calls;

    private bool Loaded => Tiles.HasStarted && !Tiles.PlacingPlayer && Server.Self is not null;

    private void Step(string name, Func<double, bool?> step) => _steps.Enqueue((name, step));

    /// <summary>Asks the server where it has the player, and compares with where the client has them.</summary>
    private void AddSync(string name) => Step(name, age =>
    {
        if (First)
        {
            // The command reports on the selected unit, which the server can remember from before.
            _gps = null;
            Server.SetTarget(0);
            Server.Session.Select(0);
            Server.Say(".gps");
        }

        if (_gps is not { } server)
        {
            return age > 6 ? false : null;
        }

        var (_, x, y, z) = Tiles.PlayerLocation;
        var client = new System.Numerics.Vector3((float)x, (float)y, z);
        var off = System.Numerics.Vector3.Distance(new System.Numerics.Vector3(server.X, server.Y, 0), new System.Numerics.Vector3(client.X, client.Y, 0));
        Note($"  server ({server.X:F1}, {server.Y:F1}, {server.Z:F1}), client ({client.X:F1}, {client.Y:F1}, {client.Z:F1}), {off:F1} yd apart");
        return off <= SyncYards;
    });

    public required CreatureManager Creatures { get; init; }

    private UnitStats? _npc;

    /// <summary>Goes next to the nearest friendly NPC the server shows, then talks to it.</summary>
    private bool? TalkToNearestNpc(double age)
    {
        if (First)
        {
            var feet = ((IPlayer)Player).Feet;
            var nearest = Creatures.LiveNodes
                .Where(n => Server.Units.TryGetValue((ulong)n.Guid, out var u) && u.IsNpc && !u.IsDead && u.Type == ObjectType.Unit && Server.ReactionTo(u) != Reaction.Hostile)
                .OrderBy(n => n.Node.GlobalPosition.DistanceTo(feet))
                .FirstOrDefault();
            if (nearest.Node is null)
            {
                Note("  no NPC in view; skipped");
                _npc = null;
                return true;
            }

            _npc = Server.Units[(ulong)nearest.Guid];
            _dialog = null;
            var at = Tiles.LocalToWow(nearest.Node.GlobalPosition);
            Note($"  going to {_npc.Name} (npc flags 0x{_npc.NpcFlags:X}) at ({at.X:F1}, {at.Y:F1}, {at.Z:F1})");
            Server.Say(FormattableString.Invariant($".go xyz {at.X + 1.5f:F1} {at.Y:F1} {at.Z + 0.5f:F1}"));
        }

        if (_npc is null)
        {
            return true;
        }

        if (age > 4 && !_talked)
        {
            _talked = true;
            Server.Use(_npc.Guid);
        }

        if (_dialog is { } shown)
        {
            Note($"  {shown.GetType().Name}");
            return true;
        }

        return age > 10 ? false : null;
    }

    private bool _talked;

    public override void _Process(double delta)
    {
        _age += delta;
        _stepAge += delta;
        if (_age > GiveUpSeconds)
        {
            Finish($"gave up at {(_steps.Count > 0 ? _steps.Peek().Name : "the end")}");
            return;
        }

        if (_steps.Count == 0)
        {
            Finish(null);
            return;
        }

        var (name, step) = _steps.Peek();
        First = _calls++ == 0;
        var result = step(_stepAge);
        if (result is null)
        {
            return;
        }

        Note($"Play check: {(result.Value ? "PASS" : "FAIL")} {name}");
        _failed |= !result.Value;
        _steps.Dequeue();
        _stepAge = 0;
        _calls = 0;
    }

    private void Note(string line)
    {
        GD.Print(line);
        _results.Add(line);
    }

    private void Finish(string? problem)
    {
        if (problem is not null)
        {
            Note($"Play check: FAIL {problem}");
            _failed = true;
        }

        GD.Print($"Play check: {(_failed ? "FAIL" : "PASS")}");
        SetProcess(false);
        GetTree().Quit(_failed ? 1 : 0);
    }

    private static float Parse(string text) => float.Parse(text, CultureInfo.InvariantCulture);
}
