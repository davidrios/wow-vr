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

    /// <summary>A ride waits for its transport to come in, then crosses: a few minutes each.</summary>
    private const double RideGiveUpSeconds = 1200;

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
        var ride = arguments.FirstOrDefault(a => a.StartsWith("--ride-check="))?["--ride-check=".Length..];
        if (_riding is { } crossed)
        {
            _riding = null;
            Step($"aboard {crossed} on the new map", age => Transports?.Riding is not null ? true : age > 60 ? false : null);
            AddSync($"position aboard {crossed} on the new map");
            return;
        }

        if (ride is not null)
        {
            AddRideSteps(ride);
            return;
        }

        if (_afterMapChange is { } changed)
        {
            AddSync($"position after .tele {changed}");
            _afterMapChange = null;
            AddPlaySteps();
            if (changed == "the portal")
            {
                Note("Play check: PASS the portal took the player to another map");
            }
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
            if (arguments.Contains("--full"))
            {
                AddFullSteps();
            }
        }

        _stepAge = 0;
    }

    /// <summary>Waits for the named transport to dock on this map, steps aboard, and stays on until the server takes it to its other map.</summary>
    private void AddRideSteps(string name)
    {
        Step("gm on", _ =>
        {
            if (First)
            {
                Server.Say(".gm on");
            }

            return true;
        });
        ulong guid = 0;
        Step($"{name} docked here", age =>
        {
            var docked = Transports?.All.FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && t.Here && t.Docked);
            if (docked is not { Guid: not 0 } found)
            {
                return age > 600 ? false : null;
            }

            guid = found.Guid;
            Note($"  {found.Name} docked at ({found.Position.X:F1}, {found.Position.Y:F1}, {found.Position.Z:F1})");
            return true;
        });
        Step($"aboard {name}", age =>
        {
            if (First)
            {
                var at = Transports!.All.First(t => t.Guid == guid).Position;
                Server.Say(FormattableString.Invariant($".go xyz {at.X:F1} {at.Y:F1} {at.Z + 12:F1}"));
            }

            if (Transports!.Riding == guid)
            {
                Note($"  standing on it after {age:F0} s");
                return true;
            }

            return age > 30 ? false : null;
        });
        Step("server takes the player to the other map", age =>
        {
            if (First)
            {
                _riding = name;
            }

            if (Transports!.Riding != guid && age > 5 && age % 10 < GetProcessDeltaTime())
            {
                Note($"  fell off at {age:F0} s");
            }

            return age > 900 ? false : null;
        });
    }

    /// <summary>
    /// With <c>--full</c>: buying and selling at the nearest vendor, putting an item on and taking
    /// it off, killing and looting a creature, the body's strafing, backwards and swimming clips
    /// (saved with <c>--shots</c>), and last, the nearest portal, which leaves the map.
    /// </summary>
    private void AddFullSteps()
    {
        Step("buy and sell at a vendor", Vendor);
        Step("equip and remove", Equip);
        Step("strafe, back and swim clips", Clips);
        GoTo("go hunting on Tirisfal's coast", new System.Numerics.Vector3(2520, 1470, 2));
        Step("kill and loot", KillAndLoot);
        GoTo("go to Undercity's Portal to Blasted Lands", new System.Numerics.Vector3(1771, 57, -46));
        Step("click a portal", Portal);
    }

    private void GoTo(string name, System.Numerics.Vector3 at) => Step(name, age =>
    {
        if (First)
        {
            Server.Say(FormattableString.Invariant($".go xyz {at.X:F1} {at.Y:F1} {at.Z:F1} 0"));
        }

        return age > 8 && Loaded ? true : null;
    });

    private ulong _vendorNpc;
    private VendorItem? _bought;
    private int _phase;
    private double _phaseAt;
    private int _carriedBefore;

    private void NextPhase(double age)
    {
        _phase++;
        _phaseAt = age;
    }

    private int Carried(uint entry) => Server.Inventory?.Items.Where(i => i.Entry == entry && !(i.Bag == 255 && i.Slot < 19)).Sum(i => (int)i.Count) ?? 0;

    private (long Guid, Node3D Node)? Nearest(Func<UnitStats, bool> which)
    {
        var feet = ((IPlayer)Player).Feet;
        var found = Creatures.LiveNodes
            .Where(n => Server.Units.TryGetValue((ulong)n.Guid, out var u) && which(u))
            .OrderBy(n => n.Node.GlobalPosition.DistanceTo(feet))
            .FirstOrDefault();
        return found.Node is null ? null : (found.Guid, found.Node);
    }

    private void GoNextTo(Node3D node)
    {
        var at = Tiles.LocalToWow(node.GlobalPosition);
        Server.Say(FormattableString.Invariant($".go xyz {at.X + 1.5f:F1} {at.Y:F1} {at.Z + 0.5f:F1}"));
    }

    private bool? Vendor(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _dialog) = (0, 0, null);
            if (Nearest(u => u.IsVendor && !u.IsDead && Server.ReactionTo(u) != Reaction.Hostile) is not { } vendor)
            {
                Note("  no vendor in view");
                return false;
            }

            _vendorNpc = (ulong)vendor.Guid;
            // The command acts on the selected unit, so nothing must be.
            Server.SetTarget(0);
            Server.Session.Select(0);
            Server.Say(".modify money 100000");
            Note($"  going to {Server.Units[_vendorNpc].Name}");
            GoNextTo(vendor.Node);
        }

        switch (_phase)
        {
            case 0 when age > 4:
                Server.Session.Interact(Server.Units[_vendorNpc] with { NpcFlags = NpcFlagMask.Vendor });
                NextPhase(age);
                break;
            case 1 when _dialog is VendorShown shown:
                _bought = shown.Items.FirstOrDefault(i => i.Price <= (Server.Self?.Money ?? 0) && i.Stock != 0);
                if (_bought is null)
                {
                    Note("  nothing affordable");
                    return false;
                }

                _carriedBefore = Carried(_bought.ItemId);
                Note($"  {shown.Items.Count} wares; buying {Server.Session.ItemInfoOf(_bought.ItemId)?.Name ?? _bought.ItemId.ToString()} for {RealmSession.Money(_bought.Price)}, carrying {_carriedBefore}");
                Server.Session.Buy(_vendorNpc, _bought);
                NextPhase(age);
                break;
            case 2 when Carried(_bought!.ItemId) > _carriedBefore:
                Note($"  bought: now carrying {Carried(_bought.ItemId)}");
                Shot("vendor");
                var item = Server.Inventory!.Items.Last(i => i.Entry == _bought.ItemId && !(i.Bag == 255 && i.Slot < 19));
                Server.Session.Sell(_vendorNpc, item);
                NextPhase(age);
                break;
            case 3 when Carried(_bought!.ItemId) < _carriedBefore + (int)Math.Max(_bought.BuyCount, 1):
                Note($"  sold: now carrying {Carried(_bought.ItemId)}");
                return true;
        }

        return age - _phaseAt > 10 ? false : null;
    }

    private OwnedItem? _equipping;

    private bool? Equip(double age)
    {
        if (First)
        {
            (_phase, _phaseAt) = (0, 0);
            // A one-handed weapon or armor first: not every class can use what's carried.
            _equipping = Server.Inventory?.Items
                .Where(i => !(i.Bag == 255 && i.Slot < 23) && Server.Session.ItemInfoOf(i.Entry) is { InventoryType: > 0 and not 18 and not 17 })
                .OrderBy(i => Server.Session.ItemInfoOf(i.Entry)!.InventoryType is 13 or 21 ? 0 : 1)
                .FirstOrDefault();
            if (_equipping is null)
            {
                Note("  nothing to put on");
                return false;
            }

            Note($"  putting on {Server.Session.ItemInfoOf(_equipping.Entry)?.Name}");
            Server.Session.Equip(_equipping);
        }

        bool Worn() => Server.Inventory?.Items.Any(i => i.Entry == _equipping!.Entry && i.Bag == 255 && i.Slot < 19) == true;
        switch (_phase)
        {
            case 0 when Worn():
                var worn = Server.Inventory!.Items.First(i => i.Entry == _equipping!.Entry && i.Bag == 255 && i.Slot < 19);
                Note($"  worn in slot {worn.Slot}; taking it off");
                Server.Session.Equip(worn);
                NextPhase(age);
                break;
            case 1 when !Worn():
                Note("  back in the bags");
                return true;
        }

        return age - _phaseAt > 10 ? false : null;
    }

    private ulong _prey;
    private bool _looted;

    private bool? KillAndLoot(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _looted) = (0, 0, false);
            Server.ChatLine += line => _looted |= line.Text.StartsWith("You loot") || line.Text.StartsWith("You receive");
            if (Nearest(u => !u.IsDead && u.Type == ObjectType.Unit && !u.IsNpc && !u.NotAttackable && Server.ReactionTo(u) != Reaction.Friendly) is not { } prey)
            {
                Note("  nothing to fight in view");
                return false;
            }

            _prey = (ulong)prey.Guid;
            Note($"  fighting {Server.Units[_prey].Name}");
            GoNextTo(prey.Node);
        }

        switch (_phase)
        {
            case 0 when age > 4:
                Server.StartAttack(_prey);
                Server.Say(".damage 100000");
                NextPhase(age);
                break;
            case 1 when Server.Units.TryGetValue(_prey, out var dead) && dead.IsDead:
                Note($"  dead{(dead.Lootable ? ", lootable" : ", nothing to loot")}");
                Shot("dead");
                if (!dead.Lootable)
                {
                    return true;
                }

                Server.Use(_prey);
                NextPhase(age);
                break;
            case 2 when _looted || (Server.Units.TryGetValue(_prey, out var looted) && !looted.Lootable):
                Note("  looted");
                return true;
        }

        return age - _phaseAt > 15 ? false : null;
    }

    private bool? Clips(double age)
    {
        if (Player is not DesktopPlayer desktop)
        {
            return true;
        }

        // Each clip for a second and a half, then a picture of the body.
        (string Name, Vector2 Intent)[] moves = [("strafe-left", new Vector2(1, 0)), ("back", new Vector2(0, -1)), ("back-right", new Vector2(-1, -1)), ("forward-left", new Vector2(1, 1))];
        var index = (int)(age / 1.5);
        if (index < moves.Length)
        {
            desktop.ScriptedIntent = moves[index].Intent;
            if (age - (index * 1.5) > 1.2 && _shotClip < index + 1)
            {
                _shotClip = index + 1;
                Shot(moves[index].Name);
            }

            return null;
        }

        desktop.ScriptedIntent = null;
        if (index == moves.Length && _shotClip == moves.Length)
        {
            // Swimming needs water: the clip is chosen from the player's state, so pretend it.
            desktop.SetSwimming(true, desktop.Feet.Y + 10);
            desktop.ScriptedIntent = new Vector2(0, 1);
            _shotClip++;
        }

        if (age > (moves.Length + 1.2) * 1.5 && _shotClip == moves.Length + 1)
        {
            Shot("swim");
            desktop.ScriptedIntent = null;
            desktop.SetSwimming(false, 0);
            _shotClip++;
            return true;
        }

        return null;
    }

    private int _shotClip;

    private bool? Portal(double age)
    {
        if (First)
        {
            var feet = ((IPlayer)Player).Feet;
            var portal = Server.GameObjects.Values
                .Where(o => o.Type == 22 || o.Name.Contains("Portal", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Orb of Translocation", StringComparison.OrdinalIgnoreCase))
                .OrderBy(o => System.Numerics.Vector3.Distance(o.Position, Tiles.LocalToWow(feet)))
                .FirstOrDefault();
            if (portal is null)
            {
                Note("  no portal in view");
                return false;
            }

            _portal = portal.Guid;
            Note($"  going to {portal.Name} (type {portal.Type}) at ({portal.Position.X:F1}, {portal.Position.Y:F1}, {portal.Position.Z:F1})");
            Server.Say(FormattableString.Invariant($".go xyz {portal.Position.X + 1f:F1} {portal.Position.Y:F1} {portal.Position.Z + 0.5f:F1}"));
            _afterMapChange = "the portal";
            (_phase, _phaseAt) = (0, 0);
            _portalFrom = Tiles.LocalToWow(feet);
        }

        if (_phase == 0 && age > 5)
        {
            // As right-clicking it: picked along a ray from above, onto the object.
            var godot = Formats.WowCoords.ToGodot(Server.GameObjects[_portal].Position);
            var target = Tiles.WorldToLocal(godot.X, godot.Y, godot.Z) + (Vector3.Up * 0.5f);
            var from = target + new Vector3(0, 3, 3);
            var picked = new Interaction(Server, Creatures, Tiles).Use(from, target - from);
            Note($"  clicked: {(picked ? "it" : "nothing")}");
            NextPhase(age);
        }

        // A portal on this map moves the player far; one to another map reloads the scene, which carries on from there.
        var (_, x, y, z) = Tiles.PlayerLocation;
        if (_phase == 1 && System.Numerics.Vector3.Distance(new System.Numerics.Vector3((float)x, (float)y, z), _portalFrom) > 500 && Loaded)
        {
            Note($"  taken to ({x:F0}, {y:F0}, {z:F0})");
            _afterMapChange = null;
            Shot("portal");
            return true;
        }

        return _phase == 1 && age - _phaseAt > 30 ? false : null;
    }

    private ulong _portal;
    private System.Numerics.Vector3 _portalFrom;

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

    public Transports? Transports { get; init; }

    /// <summary>Carries a ride check across the reload onto the transport's other map.</summary>
    private static string? _riding;

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

        if (_dialog is { } shown && _shownAt < 0)
        {
            Note($"  {shown.GetType().Name}");
            _shownAt = age;
        }

        // A moment for the window to draw, for --shots, and the bags beside it.
        if (_shownAt >= 0 && age > _shownAt + 1)
        {
            Shot("npc");
            GetParent().GetNodeOrNull<Ui.GameHud>("HudLayer/Hud")?.ToggleBags();
            _shownAt = double.MaxValue;
        }

        if (_shownAt == double.MaxValue && age > 0)
        {
            if (_bagsShot++ == 3)
            {
                Shot("bags");
                return true;
            }

            return null;
        }

        return age > 10 ? false : null;
    }

    private bool _talked;
    private double _shownAt = -1;
    private int _bagsShot;

    /// <summary>With <c>--shots=folder</c>, saves the view, which shows game assets, so don't commit it.</summary>
    private void Shot(string name)
    {
        var folder = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots="))?["--shots=".Length..];
        if (folder is not null)
        {
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"play-{name}.png"));
        }
    }

    public override void _Process(double delta)
    {
        _age += delta;
        _stepAge += delta;
        var giveUp = OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--ride-check")) ? RideGiveUpSeconds : GiveUpSeconds;
        if (_age > giveUp)
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
