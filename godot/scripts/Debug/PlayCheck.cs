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

            if (line.Kind is ChatKind.Error or ChatKind.System)
            {
                GD.Print($"  [{line.Kind}] {line.Text}");
            }

            if (line.Kind == ChatKind.Say && line.Text.Contains("play check"))
            {
                _said = line;
            }
        };
        Server.Dialog += e => _dialog ??= e is GossipClosed ? null : e;
        Server.ChatLine += line =>
        {
            _questDone |= line.Text == "Quest completed.";
            _alreadyOn |= line.Text == "You are already on that quest.";
        };
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
        Step("mouse: right-drag turns, left-drag orbits, clicks select and use", Mouse);
        Step("buy and sell at a vendor", Vendor);
        Step("equip and remove", Equip);
        Step("strafe and back clips", Clips);
        GoTo("go to Brill", new System.Numerics.Vector3(2269, 244, 34));
        Step("accept and hand in a quest", Quest);
        GoTo("go hunting on Tirisfal's coast", new System.Numerics.Vector3(2520, 1470, 2));
        Step("kill and loot", KillAndLoot);
        GoTo("go into the sea", new System.Numerics.Vector3(2680, 1428, 0));
        Step("swim clips", Swim);
        Step("die, release and return at the spirit healer", SpiritHealer);
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

    private float _yawBefore;
    private Basis _cameraBefore;
    private ulong _clicked;

    /// <summary>WoW's mouse, as real events through the viewport, so they reach the player as the mouse's would.</summary>
    private bool? Mouse(double age)
    {
        if (Player is not DesktopPlayer desktop)
        {
            return true;
        }

        var camera = GetViewport().GetCamera3D();
        var center = GetViewport().GetVisibleRect().Size / 2;
        void Button(MouseButton button, bool pressed, Vector2 at) =>
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = pressed, Position = at, GlobalPosition = at });
        void Drag(float x) => Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(x, 0), Position = center, GlobalPosition = center });

        if (First)
        {
            (_phase, _phaseAt, _dialog) = (0, 0, null);
        }

        switch (_phase)
        {
            case 0:
                _yawBefore = desktop.Rotation.Y;
                Button(MouseButton.Right, true, center);
                Drag(120);
                Button(MouseButton.Right, false, center);
                NextPhase(age);
                break;
            case 1 when age - _phaseAt > 0.3:
                var turned = Mathf.AngleDifference(_yawBefore, desktop.Rotation.Y);
                Note($"  right-drag turned the character {Mathf.RadToDeg(turned):F0} degrees");
                if (Mathf.Abs(turned) < 0.1f)
                {
                    return false;
                }

                _yawBefore = desktop.Rotation.Y;
                _cameraBefore = camera!.GlobalBasis;
                Button(MouseButton.Left, true, center);
                Drag(120);
                Button(MouseButton.Left, false, center);
                NextPhase(age);
                break;
            case 2 when age - _phaseAt > 0.3:
                var bodyTurn = Mathf.AngleDifference(_yawBefore, desktop.Rotation.Y);
                var cameraTurn = Mathf.AngleDifference(_cameraBefore.GetEuler().Y, camera!.GlobalBasis.GetEuler().Y);
                Note($"  left-drag turned the camera {Mathf.RadToDeg(cameraTurn):F0} degrees and the character {Mathf.RadToDeg(bodyTurn):F0}");
                if (Mathf.Abs(cameraTurn) < 0.1f || Mathf.Abs(bodyTurn) > 0.01f)
                {
                    return false;
                }

                // Face the nearest friendly NPC, so a click can find it on screen.
                if (Nearest(u => u.IsNpc && !u.IsDead && Server.ReactionTo(u) != Reaction.Hostile) is not { } npc)
                {
                    Note("  no NPC in view to click");
                    return false;
                }

                _clicked = (ulong)npc.Guid;
                var away = npc.Node.GlobalPosition - desktop.GlobalPosition;
                desktop.Rotation = new Vector3(0, Mathf.Atan2(-away.X, -away.Z), 0);
                desktop.PitchDegrees = -10;
                Server.SetTarget(0);
                NextPhase(age);
                break;
            case 3 when age - _phaseAt > 0.5:
                var node = Creatures.LiveNode((long)_clicked)!;
                var at = camera!.UnprojectPosition(node.GlobalPosition + (Vector3.Up * node.Height * 0.5f));
                Note($"  left-clicking {Server.Units[_clicked].Name} at ({at.X:F0}, {at.Y:F0}) on screen");
                Button(MouseButton.Left, true, at);
                Button(MouseButton.Left, false, at);
                NextPhase(age);
                break;
            case 4 when age - _phaseAt > 0.3:
                Note($"  target: {(Server.TargetStats is { } t ? t.Name : "none")}");
                if (Server.Target != _clicked)
                {
                    return false;
                }

                var again = Creatures.LiveNode((long)_clicked)!;
                var spot = camera!.UnprojectPosition(again.GlobalPosition + (Vector3.Up * again.Height * 0.5f));
                Button(MouseButton.Right, true, spot);
                Button(MouseButton.Right, false, spot);
                NextPhase(age);
                break;
            case 5 when _dialog is { } opened:
                Note($"  right-click opened {opened.GetType().Name}");
                Shot("right-click");
                Ui.GameHud? hud = GetParent().GetNodeOrNull<Ui.GameHud>("HudLayer/Hud");
                hud?.CloseTopWindow();
                return true;
        }

        return age - _phaseAt > 8 ? false : null;
    }

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
            // The command acts on the selected player, so the character selects itself.
            Server.Session.Select(Server.Session.SelfGuid);
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

        // Each clip for a second and a half, then a picture of the body and the clip's name.
        (string Name, Vector2 Intent)[] moves = [("strafe-left", new Vector2(1, 0)), ("back", new Vector2(0, -1)), ("back-right", new Vector2(-1, -1)), ("forward-left", new Vector2(1, 1))];
        var index = (int)(age / 1.5);
        if (index < moves.Length)
        {
            desktop.ScriptedIntent = moves[index].Intent;
            if (age - (index * 1.5) > 1.2 && _shotClip < index + 1)
            {
                _shotClip = index + 1;
                Shot(moves[index].Name);
                var clip = Server.Avatar?.BodyClip ?? "";
                _clips.Add(clip);
                Note($"  {moves[index].Name}: clip {clip}");
            }

            return null;
        }

        desktop.ScriptedIntent = null;

        // Strafing runs (5) or walks (4) turned; going back plays walking backwards (13).
        return _clips.Count == moves.Length && _clips[1].StartsWith("13_") && _clips[2].StartsWith("13_") && _clips[0] is ['4' or '5', '_', ..];
    }

    private readonly List<string> _clips = [];

    /// <summary>In the sea: forward plays swimming (42), back swimming backwards (45), still the swimming idle (41).</summary>
    private bool? Swim(double age)
    {
        if (Player is not DesktopPlayer desktop)
        {
            return true;
        }

        if (First)
        {
            _swimClips.Clear();
        }

        (string Name, Vector2 Intent, string Clip)[] moves = [("swim-still", Vector2.Zero, "41_"), ("swim", new Vector2(0, 1), "42_"), ("swim-back", new Vector2(0, -1), "45_")];
        var index = (int)(age / 2);
        if (index < moves.Length)
        {
            desktop.ScriptedIntent = moves[index].Intent;
            if (age - (index * 2) > 1.6 && _swimClips.Count == index)
            {
                var clip = Server.Avatar?.BodyClip ?? "";
                _swimClips.Add(clip);
                Shot(moves[index].Name);
                Note($"  {moves[index].Name}: swimming {desktop.Swimming}, clip {clip}");
            }

            return null;
        }

        desktop.ScriptedIntent = null;
        return _swimClips.Count == moves.Length && Enumerable.Range(0, moves.Length).All(i => _swimClips[i].StartsWith(moves[i].Clip));
    }

    private readonly List<string> _swimClips = [];

    private readonly Queue<ulong> _questGivers = new();
    private readonly Queue<ulong> _enders = new();
    private readonly HashSet<uint> _failedQuests = [];
    private ulong _questNpc;
    private uint _quest;

    /// <summary>
    /// Takes a quest from the nearest quest giver that has one, completes it with a GM command,
    /// and hands it in to the same giver, choosing the first reward.
    /// </summary>
    private bool? Quest(double age)
    {
        if (First)
        {
            (_phase, _phaseAt, _dialog, _quest) = (0, 0, null, 0);
            _questGivers.Clear();
            var feet = ((IPlayer)Player).Feet;
            foreach (var (guid, _) in Creatures.LiveNodes
                .Where(n => Server.Units.TryGetValue((ulong)n.Guid, out var u) && u.IsQuestGiver && !u.IsDead && Server.ReactionTo(u) != Reaction.Hostile)
                .OrderBy(n => n.Node.GlobalPosition.DistanceTo(feet)))
            {
                _questGivers.Enqueue((ulong)guid);
            }

            Note($"  {_questGivers.Count} quest givers in view");
        }

        switch (_phase)
        {
            // Next giver: go to it, then ask what it has.
            case 0:
                if (!_questGivers.TryDequeue(out _questNpc))
                {
                    return false;
                }

                if (Creatures.LiveNode((long)_questNpc) is { } node)
                {
                    GoNextTo(node);
                }

                _dialog = null;
                NextPhase(age);
                break;
            case 1 when age - _phaseAt > 3:
                Server.Session.Interact(Server.Units[_questNpc] with { NpcFlags = NpcFlagMask.QuestGiver });
                NextPhase(age);
                break;
            case 2 when _dialog is QuestShown { Step: QuestStep.RequestItems or QuestStep.OfferReward } open && !_failedQuests.Contains(open.QuestId):
                // A giver with one quest to take back opens on it.
                Note($"  {Server.Units[_questNpc].Name} takes back \"{open.Title}\" ({open.QuestId})");
                _quest = open.QuestId;
                _dialog = null;
                (_phase, _phaseAt) = (4, age);
                break;
            case 2 when _dialog is GossipShown { Quests.Count: > 0 } list && list.Quests.All(q => q.Icon == 4) && list.Quests.LastOrDefault(q => q.Icon == 4 && !_failedQuests.Contains(q.Id)) is { } takes:
                // A quest the character is on, which this one takes back: complete it and hand it in.
                Note($"  {Server.Units[_questNpc].Name} takes back \"{takes.Title}\" ({takes.Id})");
                _quest = takes.Id;
                _dialog = null;
                (_phase, _phaseAt) = (4, age);
                break;
            case 2 when _dialog is GossipShown { Quests.Count: > 0 } list:
                // A new quest first: take it, complete it, and hand it in.
                var offered = list.Quests.First(q => q.Icon != 4);
                Note($"  {Server.Units[_questNpc].Name} offers {string.Join(", ", list.Quests.Where(q => q.Icon != 4).Select(q => q.Title))}");
                _dialog = null;
                _asked = offered.Id;
                _alreadyOn = false;
                Server.Session.QueryQuest(_questNpc, offered.Id);
                NextPhase(age);
                break;
            case 2 when _dialog is QuestShown { Step: QuestStep.Details }:
                // A giver with one quest shows it at once.
                NextPhase(age);
                break;
            case 2 when _dialog is not null || age - _phaseAt > 4:
                Note($"  {Server.Units[_questNpc].Name}: {_dialog switch { null => "no answer", GossipShown g => $"gossip, no quests ({g.Options.Count} options)", QuestShown q => $"quest {q.Step}", _ => _dialog.GetType().Name }}");
                (_phase, _phaseAt) = (0, age);
                break;
            case 3 when _alreadyOn:
                // Taken before: it goes straight to being completed and handed in.
                _quest = _asked;
                Note($"  already on quest {_quest}");
                (_phase, _phaseAt) = (4, age);
                break;
            case 3 when _dialog is QuestShown { Step: QuestStep.Details } details:
                _quest = details.QuestId;
                Note($"  accepting \"{details.Title}\" ({details.QuestId})");
                _dialog = null;
                Server.Session.AcceptQuest(_questNpc, _quest);
                (_phase, _phaseAt) = (4, age);
                break;
            case 4 when age - _phaseAt > 2:
                Server.Session.Select(Server.Session.SelfGuid);
                Server.Say($".quest complete {_quest}");
                NextPhase(age);
                break;
            case 5 when age - _phaseAt > 2:
                // Whoever takes it back: each giver in view, nearest first, until one lists it.
                _enders.Clear();
                var feet = ((IPlayer)Player).Feet;
                foreach (var (guid, _) in Creatures.LiveNodes
                    .Where(n => Server.Units.TryGetValue((ulong)n.Guid, out var u) && u.IsQuestGiver && !u.IsDead)
                    .OrderBy(n => n.Node.GlobalPosition.DistanceTo(feet)))
                {
                    _enders.Enqueue((ulong)guid);
                }

                (_phase, _phaseAt) = (8, age);
                break;
            case 8:
                if (!_enders.TryDequeue(out _questNpc))
                {
                    Note($"  no giver in view takes back quest {_quest}");
                    _failedQuests.Add(_quest);
                    (_phase, _phaseAt) = (0, age);
                    break;
                }

                if (Creatures.LiveNode((long)_questNpc) is { } ender)
                {
                    GoNextTo(ender);
                }

                NextPhase(age);
                break;
            case 9 when age - _phaseAt > 3:
                _dialog = null;
                Server.Session.Interact(Server.Units[_questNpc] with { NpcFlags = NpcFlagMask.QuestGiver });
                NextPhase(age);
                break;
            case 10 when _dialog is GossipShown shown && shown.Quests.FirstOrDefault(q => q.Id == _quest) is { } listed:
                _dialog = null;
                Server.Session.ChooseGossipQuest(_questNpc, listed);
                break;
            case 10 when _dialog is QuestShown { Step: QuestStep.RequestItems } request && request.QuestId == _quest:
                _dialog = null;
                Server.Session.RequestQuestReward(_questNpc, request.QuestId);
                break;
            case 10 when _dialog is QuestShown { Step: QuestStep.OfferReward } reward && reward.QuestId == _quest:
                Note($"  handing in to {Server.Units[_questNpc].Name}, {reward.Choices.Count} rewards to choose from");
                _dialog = null;
                _questDone = false;
                Server.Session.ChooseQuestReward(_questNpc, reward.QuestId, 0);
                NextPhase(age);
                break;
            case 10 when _dialog is not null || age - _phaseAt > 4:
                (_phase, _phaseAt) = (8, age);
                break;
            case 11 when _questDone:
                Shot("quest");
                return true;
            case 11 when age - _phaseAt > 8:
                Note($"  quest {_quest} wasn't handed in");
                _failedQuests.Add(_quest);
                (_phase, _phaseAt) = (0, age);
                break;
        }

        return age - _phaseAt > 12 ? false : null;
    }

    private bool _questDone;
    private bool _alreadyOn;
    private uint _asked;

    private ulong _healer;

    /// <summary>Dies, releases to the graveyard as a ghost, and takes the spirit healer's offer.</summary>
    private bool? SpiritHealer(double age)
    {
        if (First)
        {
            (_phase, _phaseAt) = (0, 0);
            Server.Say(".gm off");
            Server.Session.Select(Server.Session.SelfGuid);
            Server.Say(".die");
        }

        var self = Server.Self;
        switch (_phase)
        {
            case 0 when self is { IsCorpse: true }:
                Note("  dead");
                Shot("corpse");
                Server.Session.ReleaseSpirit();
                NextPhase(age);
                break;
            case 1 when self is { IsGhost: true } && age - _phaseAt > 6 && Loaded:
                Note($"  a ghost, corpse {(Server.Corpse is { } c ? $"at ({c.X:F0}, {c.Y:F0})" : "unknown")}");
                Shot("ghost");
                if (Nearest(u => u.IsSpiritHealer) is not { } healer)
                {
                    Note("  no spirit healer in view");
                    return false;
                }

                _healer = (ulong)healer.Guid;
                GoNextTo(healer.Node);
                _dialog = null;
                NextPhase(age);
                break;
            case 2 when age - _phaseAt > 3:
                Server.Session.Interact(Server.Units[_healer]);
                NextPhase(age);
                break;
            case 3 when _dialog is GossipShown { Options.Count: > 0 } gossip:
                Note($"  {Server.Units[_healer].Name}: {gossip.Options[0].Text}");
                _dialog = null;
                Server.Session.ChooseGossip(_healer, gossip.MenuId, gossip.Options[0].Index);
                break;
            case 3 when _dialog is SpiritHealerOffer:
                Server.Session.AcceptSpiritHealer(_healer);
                NextPhase(age);
                break;
            case 4 when self is { IsGhost: false, Unit.IsDead: false }:
                Note($"  alive, health {self.Unit.Health}/{self.Unit.MaxHealth}");
                Server.Say(".gm on");
                return true;
        }

        return age - _phaseAt > 20 ? false : null;
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

        if (_phase == 0 && age > 1 && !_leveled)
        {
            // This portal is for level 58 and up; the character goes back to its own level after.
            _leveled = true;
            _level = Server.Self?.Unit.Level ?? 0;
            Server.Session.Select(Server.Session.SelfGuid);
            Server.Say(".character level 58");
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
            if (_level > 0)
            {
                Server.Session.Select(Server.Session.SelfGuid);
                Server.Say($".character level {_level}");
            }
            _afterMapChange = null;
            Shot("portal");
            return true;
        }

        return _phase == 1 && age - _phaseAt > 30 ? false : null;
    }

    private ulong _portal;
    private bool _leveled;
    private uint _level;
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
