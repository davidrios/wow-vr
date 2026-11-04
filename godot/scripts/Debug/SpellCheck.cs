using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--spell-check</c> on a server: waits for the spells and buttons the server sends at login,
/// opens the spellbook and bags, puts a harmless spell the player can cast on themselves on the
/// bar's last button, presses it as its key does, and prints PASS once the server cast it and the
/// button shows its cooldown, then puts the button back and quits. <c>--shots=folder</c> saves
/// the spellbook and the bar.
/// </summary>
public partial class SpellCheck : Node
{
    private const int Slot = 11;
    private const double GiveUpSeconds = 120;

    public required ServerWorld Server { get; init; }

    private readonly List<string> _failures = [];
    private Ui.GameHud? _hud;
    private double _age;
    private double _stepAge;
    private int _step;
    private SpellRecord? _spell;
    private ActionButton _was;
    private bool _went;
    private string? _error;

    public override void _Ready()
    {
        Server.ScreenText += (text, error) =>
        {
            if (error)
            {
                _error = text;
                GD.Print($"  [error] {text}");
            }
        };
        Server.Session.Events.Enqueue(new ChatReceived(new ChatLine(ChatKind.Notice, "", "Spell check starting")));
        Server.UnitCast += e =>
        {
            if (e is AreaEffectAppeared area)
            {
                _areas.Add($"{area.Spell} ({area.Radius} yards)");
            }
        };
    }

    public override void _Process(double delta)
    {
        _age += delta;
        _stepAge += delta;
        _hud ??= GetParent().GetNodeOrNull<Ui.GameHud>("HudLayer/Hud");

        // What the body plays, as it changes, to see casts and channels hold their animations.
        if (Server.Avatar?.Body?.Clip is { } bodyClip && bodyClip != _lastClip)
        {
            GD.Print($"Spell check: at {_age:F1} s the body plays {(bodyClip.Length == 0 ? "nothing" : bodyClip)}{(Server.Casting is { } c ? $" while {(c.Channel ? "channeling" : "casting")} {c.Spell}" : "")}");
            _lastClip = bodyClip;
        }

        if (_groundFrame == 4 && _rainShots < 3 && _stepAge - _groundAt > 2.5 + _rainShots * 0.3)
        {
            Shot($"rain{_rainShots}");
            GD.Print($"Spell check: rain at {_stepAge - _groundAt:F1} s: {GetParent().GetNodeOrNull<SpellEffects>("SpellEffects")?.RainReport}");
            _rainShots++;
        }
        if (_age > GiveUpSeconds)
        {
            Fail($"gave up at step {_step}");
            Finish();
            return;
        }

        switch (_step)
        {
            case 0 when Server.Spells is not null && Server.KnownSpells.Count > 0 && _hud is not null && Loaded:
                var spells = Server.Spells;
                var shown = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>().Where(s => !s.IsHidden && spells.InSpellbook(s.Id)).ToList();
                GD.Print($"Spell check: {Server.KnownSpells.Count} spells known, {shown.Count} in the spellbook, tabs: {string.Join(", ", shown.Select(s => spells.TabOf(s.Id)?.Name ?? "General").Distinct())}");
                GD.Print($"Spell check: bar: {string.Join(" | ", Server.ActionButtons.Take(12).Select(b => b.IsEmpty ? "-" : b.Kind == ActionKind.Spell ? spells.Get((int)b.Id)?.Name ?? $"spell {b.Id}" : $"{b.Kind} {b.Id}"))}");
                if (shown.Count == 0)
                {
                    Fail("no spells to show");
                }

                _hud.ToggleCharacter();
                _hud.ToggleBags();

                // Life Tap and the like cost health, which the checks spend run after run: a GM tops it up.
                if (Server.Self is { } self && self.Unit.Health < self.Unit.MaxHealth)
                {
                    Server.Say($".modify hp {self.Unit.MaxHealth}");
                }

                Next();
                break;
            case 1 when _stepAge > 2 && !NpcPhase():
                break;
            case 1 when _stepAge > 2 && !DragFrame():
                break;
            case 1 when !_characterShot:
                // The character's model builds off the main thread; a few seconds is plenty.
                if (_stepAge > 6)
                {
                    Shot("character");
                    _characterShot = true;
                    _hud!.ToggleSpellbook();
                    _stepAge = 2;
                }

                break;
            case 1 when _stepAge < 3:
                break;
            case 1 when _stepAge > 2 && !DragToBar():
                break;
            case 1 when _stepAge > 2:
                _hud!.CloseTopWindow();
                _hud.CloseTopWindow();
                _was = Server.ActionButtons[Slot];
                _spell = ChooseSpell();
                if (_spell is null)
                {
                    GD.Print("Spell check: no instant spell to cast on oneself");
                    _step = 4;
                    _stepAge = 10;
                    break;
                }

                GD.Print($"Spell check: casting {_spell.FullName} ({_spell.Id}), cast {_spell.CastTimeMs} ms, cooldown {_spell.RecoveryMs} ms, global {_spell.GlobalCooldownMs} ms");
                _was = Server.ActionButtons[Slot];
                Server.SetActionButton(Slot, new ActionButton((uint)_spell.Id, ActionKind.Spell));
                Next();
                break;
            case 2 when _stepAge > 1:
                if (Server.ActionButtons[Slot].Id != (uint)_spell!.Id)
                {
                    Fail("the button didn't take the spell");
                }

                Server.CastChanged += () => _went |= Server.Casting is not null;
                Server.Session.Events.Enqueue(new ChatReceived(new ChatLine(ChatKind.Notice, "", $"Pressing = for {_spell.Name}")));
                _error = null;
                _hud!.PressAction(Slot);
                Next();
                break;
            case 3:
                var (left, total) = Server.CooldownOf((uint)_spell!.Id);
                if (!_printed && _stepAge > 0.2)
                {
                    _printed = true;
                    GD.Print($"Spell check: {_spell.Name}'s button has {left:F2} of {total:F2} s to wait");
                    Shot("bar");
                }

                _went |= left > 0;

                if (_error is { } error && _stepAge < 3)
                {
                    Fail($"the server refused it: {error}");
                    Next();
                }
                else if (_stepAge > 1 && _went)
                {
                    GD.Print($"Spell check: PASS {_spell.Name} cast, and its button showed its cooldown");
                    Next();
                }
                else if (_stepAge > 6 + _spell.CastTimeMs / 1000.0)
                {
                    Fail("nothing came back from casting it");
                    Next();
                }

                break;
            case 4 when _stepAge > 1 + (_spell?.CastTimeMs ?? 0) / 1000.0:
                // Then one that takes a while, to see the cast bar, if the class has one.
                _spell = ChooseSpell(castTime: true);
                if (_spell is null)
                {
                    GD.Print("Spell check: no spell with a cast time to try");
                    if (_tried.Count == 0)
                    {
                        Fail("no harmless spell to cast on oneself");
                    }

                    _step = 7;
                    _stepAge = 0;
                    break;
                }

                GD.Print($"Spell check: casting {_spell.FullName} ({_spell.Id}), cast {_spell.CastTimeMs} ms");
                Server.SetActionButton(Slot, new ActionButton((uint)_spell.Id, ActionKind.Spell));
                _castSeen = false;
                Next();
                break;
            case 5 when _stepAge > 2:
                _error = null;
                _hud!.PressAction(Slot);
                Next();
                break;
            case 6:
                if (Server.Casting is not null && !_castSeen)
                {
                    _castSeen = true;
                    _castShownAt = _stepAge;
                }

                if (_castSeen && !_castShot && _stepAge - _castShownAt > 0.5)
                {
                    _castShot = true;
                    Shot("cast");
                    var body = Server.Avatar?.Body;
                    var effects = body is null ? 0 : Descendants(body).Count(n => n.Name == "SpellEffect");
                    var clip = body?.Clip ?? "";
                    GD.Print($"Spell check: while casting the body plays {clip} with {effects} effects on it");
                    if (clip.Length == 0 || clip.StartsWith("0_") || clip.StartsWith("4_") || clip.StartsWith("5_"))
                    {
                        Fail($"the body plays {clip} while casting, not a spell's");
                    }
                }

                if (_error is { } refused && _tried.Count < 6)
                {
                    GD.Print($"Spell check: the server refused {_spell!.Name} ({refused}); trying another");
                    _step = 4;
                    _stepAge = 10;
                }
                else if (_error is { } failed)
                {
                    Fail($"the server refused {_spell!.Name}: {failed}");
                    Next();
                }
                else if (_castSeen && Server.Casting is null)
                {
                    GD.Print($"Spell check: PASS {_spell!.Name} showed its cast bar for {_stepAge - _castShownAt:F1} s and finished");
                    Next();
                }
                else if (!_castSeen && _stepAge > 2 && _tried.Count < 6)
                {
                    // A talent can make a spell instant: the server says it went at once. Try another.
                    GD.Print($"Spell check: the server cast {_spell!.Name} at once; trying another");
                    _step = 4;
                    _stepAge = 10;
                }
                else if (_stepAge > 3 + _spell!.CastTimeMs / 1000.0)
                {
                    Fail(_castSeen ? "the cast bar never ended" : "no cast bar came");
                    Next();
                }

                break;
            case 7 when _stepAge > 1 && !GroundPhase():
                break;
            case 7 when _stepAge > 1:
                Server.SetActionButton(Slot, _was);
                Next();
                break;
            case 8 when _stepAge > 1:
                Finish();
                break;
        }
    }

    /// <summary>
    /// A spell of the class's own, cast on oneself, quick, and harmless: no summons, portals or
    /// teleports. One with a cooldown of its own is best, to see it on the button.
    /// </summary>
    private SpellRecord? ChooseSpell(bool castTime = false)
    {
        var spells = Server.Spells!;
        string[] avoid = ["Summon", "Portal", "Teleport", "Ritual", "Hearth", "Shapeshift", "Form", "Stealth", "Vanish", "Feign", "Eye of", "Banish", "Enslave"];
        var candidates = Server.KnownSpells
            .Select(id => spells.Get((int)id))
            .OfType<SpellRecord>()
            .Where(s => !s.IsPassive && !s.IsHidden && !s.NeedsForm && spells.TabOf(s.Id) is not null && spells.InSpellbook(s.Id))
            .Where(s => s.Aim is SpellAim.Self or SpellAim.Friend)
            .Where(s => castTime ? s.CastTimeMs is >= 1000 and <= 3500 : s.CastTimeMs == 0)
            .Where(s => !avoid.Any(a => s.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s.Id != _spell?.Id && !_tried.Contains(s.Id) && !_triedNames.Contains(s.Name) && Server.CooldownOf((uint)s.Id).Left <= 0)
            .Where(Server.HasPowerFor)
            .ToList();
        var chosen = candidates.OrderByDescending(s => s.RecoveryMs is > 0 and < 120_000).ThenBy(s => s.CastTimeMs).FirstOrDefault();
        if (chosen is not null)
        {
            _tried.Add(chosen.Id);
            _triedNames.Add(chosen.Name);
        }

        return chosen;
    }

    private readonly HashSet<int> _tried = [];
    private readonly HashSet<string> _triedNames = [];

    private string? _lastClip;
    private int _rainShots;
    private readonly List<string> _areas = [];
    private int _groundFrame;
    private double _groundAt;
    private SpellRecord? _groundSpell;
    private Vector2 _aimAt;

    /// <summary>
    /// A ground spell, if the class has one: pressing its button aims it, the circle lies where the
    /// mouse points on the ground ahead, a real left click there casts it, and pressing the button
    /// again or Escape puts it away. True when done.
    /// </summary>
    private bool GroundPhase()
    {
        var ground = GetParent().GetNodeOrNull<GroundTargeter>("GroundTargeter");
        var age = _stepAge - _groundAt;
        switch (_groundFrame)
        {
            case 0:
                var spells = Server.Spells!;
                _groundSpell = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>()
                    .Where(s => s.Aim == SpellAim.Ground && !s.IsPassive && !s.NeedsForm && spells.InSpellbook(s.Id) && Server.HasPowerFor(s) && Server.CooldownOf((uint)s.Id).Left <= 0)
                    .OrderBy(s => s.CastTimeMs)
                    .FirstOrDefault();
                if (_groundSpell is null || ground is null)
                {
                    GD.Print("Spell check: no ground spell to aim");
                    return true;
                }

                GD.Print($"Spell check: aiming {_groundSpell.FullName} ({_groundSpell.Id}), {_groundSpell.Radius} yards wide, {_groundSpell.Range} yards away at most");
                Server.SetActionButton(Slot, new ActionButton((uint)_groundSpell.Id, ActionKind.Spell));
                (_groundFrame, _groundAt) = (1, _stepAge);
                return false;
            case 1 when age > 1.5:
                _error = null;
                _hud!.PressAction(Slot);
                if (Server.Aiming?.Id != _groundSpell!.Id)
                {
                    Fail($"pressing {_groundSpell.Name} didn't aim it");
                    return true;
                }

                // The floor a few yards ahead of the character, below the middle of the view.
                var size = GetViewport().GetVisibleRect().Size;
                _aimAt = new Vector2(size.X * 0.5f, size.Y * 0.62f);
                Input.WarpMouse(_aimAt);
                Input.ParseInputEvent(new InputEventMouseMotion { Position = _aimAt, GlobalPosition = _aimAt });
                (_groundFrame, _groundAt) = (2, _stepAge);
                return false;
            case 2 when age > 0.5:
                if (ground!.Point is not { } point)
                {
                    Fail("no circle on the ground under the mouse");
                    Server.CancelAiming();
                    return true;
                }

                GD.Print($"Spell check: PASS the circle lies {point.DistanceTo(ground.Feet()):F1} yards away");
                Shot("aim");
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, Position = _aimAt, GlobalPosition = _aimAt });
                (_groundFrame, _groundAt) = (3, _stepAge);
                return false;
            case 3:
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = _aimAt, GlobalPosition = _aimAt });
                (_groundFrame, _groundAt) = (4, _stepAge);
                return false;
            case 4 when age > 4:
                if (Server.Aiming is not null)
                {
                    Fail("clicking the ground didn't cast it");
                    Server.CancelAiming();
                }
                else if (_error is { } error)
                {
                    Fail($"the server refused {_groundSpell!.Name} on the ground: {error}");
                }
                else
                {
                    Shot("ground");
                    GD.Print($"Spell check: PASS {_groundSpell!.Name} cast where the mouse pointed{(Server.Casting is { Channel: true } ? ", channeling" : "")}");
                    var spellEffects = GetParent().GetNodeOrNull<SpellEffects>("SpellEffects");
                    GD.Print($"Spell check: rain now: {spellEffects?.RainReport}");
                    var shards = spellEffects?.ShowingShards ?? 0;
                    var rains = Server.Spells?.VisualOf(_groundSpell.Id)?.PersistentArea is { } area && Server.Spells.AreaRainOf(area) is not null;
                    if (rains)
                    {
                        GD.Print(shards > 0 ? $"Spell check: PASS {shards} shards of its rain are falling" : "Spell check: FAIL its rain shows no shards");
                        if (shards == 0)
                        {
                            Fail("no rain");
                        }
                    }
                }

                Server.Session.CancelChannel((uint)_groundSpell!.Id);
                (_groundFrame, _groundAt) = (5, _stepAge);
                return false;
            case 5 when age > 1:
                // Put away by pressing again, and by Escape.
                _hud!.PressAction(Slot);
                var aimed = Server.Aiming is not null;
                _hud.PressAction(Slot);
                var pressedAway = Server.Aiming is null;
                _hud.PressAction(Slot);
                _hud.CloseTopWindow();
                if (aimed && pressedAway && Server.Aiming is null)
                {
                    GD.Print("Spell check: PASS pressing the button again and Escape both put it away");
                }
                else
                {
                    Fail($"putting it away: aimed {aimed}, again {pressedAway}, Escape {Server.Aiming is null}");
                    Server.CancelAiming();
                }

                // A lasting area the client draws: Consecration's glow, cast by GM command.
                Server.Say(".cast 26573 triggered");
                (_groundFrame, _groundAt) = (6, _stepAge);
                return false;
            case 6 when age > 2.5:
                var effects = GetParent().GetNodeOrNull<SpellEffects>("SpellEffects");
                var drawn = effects is null ? 0 : Descendants(effects).Count(n => n.Name == "AreaEffect");
                GD.Print($"Spell check: areas that appeared: {string.Join(", ", _areas)}; {drawn} area models drawn now");
                if (_areas.Any(a => a.StartsWith("26573")) && drawn > 0)
                {
                    GD.Print("Spell check: PASS Consecration's area showed on the ground");
                }
                else
                {
                    Fail("Consecration's area didn't show");
                }

                Shot("area");
                _groundFrame = 7;
                return true;
            case >= 7:
                return true;
        }

        return false;
    }

    private int _npcStep;
    private double _npcAt;
    private ulong _npc;
    private int _soundsBefore;

    /// <summary>
    /// The nearest standing NPC: selecting it shows its ring at once and clearing the selection
    /// hides it; talking to it turns it to the player and plays its greeting. True when done.
    /// </summary>
    private bool NpcPhase()
    {
        var creatures = GetParent().GetNode<CreatureManager>("Creatures");
        var sounds = GetParent().GetNode<CreatureSoundPlayer>("CreatureSounds");
        var here = Server.PlayerPosition;
        var age = _stepAge - _npcAt;
        switch (_npcStep)
        {
            case 0:
                var nearest = Server.Units.Values
                    .Where(u => u.IsNpc && !u.IsDead && u.HasGossip && creatures.LiveNode((long)u.Guid) is not null)
                    .Select(u => (Unit: u, At: creatures.LivePosition((long)u.Guid)))
                    .Where(u => u.At is not null)
                    .OrderBy(u => System.Numerics.Vector3.Distance(u.At!.Value, here))
                    .FirstOrDefault();
                if (nearest.Unit is null || System.Numerics.Vector3.Distance(nearest.At!.Value, here) > 40)
                {
                    GD.Print("Spell check: no NPC near to talk to");
                    _npcStep = 99;
                    return true;
                }

                _npc = nearest.Unit.Guid;
                GD.Print($"Spell check: the nearest NPC is {nearest.Unit.Name}, {System.Numerics.Vector3.Distance(nearest.At!.Value, here):F1} yards away");
                Server.SetTarget(_npc);
                (_npcStep, _npcAt) = (1, _stepAge);
                return false;
            case 1 when age > 0.2:
                var shown = creatures.LiveNode((long)_npc)?.RingShown == true;
                Server.SetTarget(0);
                (_npcStep, _npcAt) = (2, _stepAge);
                _ringWasShown = shown;
                return false;
            case 2 when age > 0.2:
                var gone = creatures.LiveNode((long)_npc)?.RingShown != true;
                if (_ringWasShown && gone)
                {
                    GD.Print("Spell check: PASS the selection ring showed at once and went when cleared");
                }
                else
                {
                    Fail($"selection ring: shown when selected {_ringWasShown}, gone when cleared {gone}");
                }

                // Close enough to talk, as the server wants.
                var at = creatures.LivePosition((long)_npc)!.Value;
                var away = here - at;
                var spot = at + (away.Length() > 0.1f ? System.Numerics.Vector3.Normalize(away) * 3f : new System.Numerics.Vector3(3, 0, 0));
                Server.Say(FormattableString.Invariant($".go xyz {spot.X:F2} {spot.Y:F2} {spot.Z + 0.5f:F2}"));
                (_npcStep, _npcAt) = (3, _stepAge);
                return false;
            case 3 when age > 3:
                _soundsBefore = sounds.PlayedCount;
                var npc = Server.Units[_npc];
                GD.Print($"Spell check: talking to {npc.Name}: npc flags 0x{npc.NpcFlags:X}, {Server.ReactionTo(npc)}, facing {creatures.LiveNode((long)_npc)?.Facing:F2}");
                Server.Use(_npc);
                (_npcStep, _npcAt) = (4, _stepAge);
                return false;
            case 4 when age > 2.5:
                var node = creatures.LiveNode((long)_npc);
                var npcAt = creatures.LivePosition((long)_npc);
                var me = Server.PlayerPosition;
                if (node is null || npcAt is null)
                {
                    Fail("the NPC went away");
                    _npcStep = 99;
                    return true;
                }

                var toward = MathF.Atan2(me.Y - npcAt.Value.Y, me.X - npcAt.Value.X);
                var off = Mathf.Abs(Mathf.AngleDifference(node.Facing, toward));
                GD.Print($"Spell check: the NPC faces {node.Facing:F2}, the player is toward {toward:F2}; a window is open: {_hud!.HasOpenWindow}");
                if (off < 0.35f)
                {
                    GD.Print($"Spell check: PASS the NPC turned to face the player ({Mathf.RadToDeg(off):F0} degrees off)");
                }
                else
                {
                    Fail($"the NPC faces {Mathf.RadToDeg(off):F0} degrees away from the player");
                }

                GD.Print(sounds.PlayedCount > _soundsBefore
                    ? $"Spell check: PASS the NPC's greeting played ({sounds.PlayedCount - _soundsBefore} sounds)"
                    : "Spell check: the NPC played no greeting (it may have none)");
                Shot("talk");
                _hud!.CloseTopWindow();
                Server.SetTarget(0);
                _npcStep = 99;
                return true;
            case 99:
                return true;
        }

        return false;
    }

    private bool _ringWasShown;
    private bool _characterShot;
    private int _frameStep;
    private Vector2 _frameFrom;
    private Vector2 _frameWas;
    private Ui.UnitFrame? _frame;
    private static readonly Vector2 FrameMove = new(300, 200);

    /// <summary>Drags the player's frame 300 by 200 pixels with real mouse events, checks it moved and was saved, and puts it back. True when done.</summary>
    private bool DragFrame()
    {
        void Mouse(InputEvent e) => Input.ParseInputEvent(e);
        switch (_frameStep)
        {
            case 0:
                _frame = Descendants(_hud!).OfType<Ui.UnitFrame>().FirstOrDefault(f => f.PlaceKey == "player_frame");
                if (_frame is null)
                {
                    Fail("no player frame");
                    _frameStep = 99;
                    return true;
                }

                _frameWas = _frame.Position;
                _frameFrom = _frame.GetGlobalRect().GetCenter();
                Mouse(new InputEventMouseMotion { Position = _frameFrom, GlobalPosition = _frameFrom });
                Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, Position = _frameFrom, GlobalPosition = _frameFrom });
                _frameStep++;
                return false;
            case <= 10:
                var at = _frameFrom + FrameMove * (_frameStep / 10f);
                Mouse(new InputEventMouseMotion { Position = at, GlobalPosition = at, Relative = FrameMove / 10f, ButtonMask = MouseButtonMask.Left });
                _frameStep++;
                return false;
            case 11:
                var to = _frameFrom + FrameMove;
                Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = to, GlobalPosition = to });
                _frameStep++;
                return false;
            case 12:
                var moved = _frame!.Position - _frameWas;
                var saved = AppSettings.HudPosition("player_frame");
                if (moved.DistanceTo(FrameMove) < 2 && saved is { } place && place.DistanceTo(_frame.Position) < 1)
                {
                    GD.Print($"Spell check: PASS the player frame dragged by {moved} and saved at {place}");
                }
                else
                {
                    Fail($"dragging the player frame moved it by {moved}, saved {saved}");
                }

                Shot("frame");
                _frame.Position = _frameWas;
                AppSettings.SetHudPosition("player_frame", _frameWas);
                _frameStep++;
                return true;
            default:
                return true;
        }
    }

    private const int DragSlot = 10;
    private int _dragFrame;
    private double _dragAt;
    private Vector2 _dragFrom;
    private Vector2 _dragTo;
    private ActionButton _dragAction;
    private ActionButton _dragWas;

    /// <summary>
    /// Drags the spellbook's first castable icon onto button "-" with real mouse events, a frame at
    /// a time, checks the server's buttons took it, and puts the old one back. True when done.
    /// </summary>
    private bool DragToBar()
    {
        void Mouse(InputEvent e) => Input.ParseInputEvent(e);
        switch (_dragFrame)
        {
            case 0:
                Shot("spellbook");
                var icon = Descendants(_hud!).OfType<Ui.ActionIcon>().FirstOrDefault(i => i.IsVisibleInTree() && i.Action.Kind == ActionKind.Spell && !i.Action.IsEmpty);
                var slot = Descendants(_hud!).OfType<Ui.ActionSlot>().ElementAtOrDefault(DragSlot);
                if (icon is null || slot is null)
                {
                    Fail("no spellbook icon or bar button to drag between");
                    return true;
                }

                _dragAction = icon.Action;
                _dragWas = Server.ActionButtons[DragSlot];
                _dragFrom = icon.GetGlobalRect().GetCenter();
                _dragTo = slot.GetGlobalRect().GetCenter();
                Mouse(new InputEventMouseMotion { Position = _dragFrom, GlobalPosition = _dragFrom });
                Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left, Position = _dragFrom, GlobalPosition = _dragFrom });
                _dragFrame++;
                return false;
            case <= 12:
                var at = _dragFrom.Lerp(_dragTo, _dragFrame / 12f);
                Mouse(new InputEventMouseMotion { Position = at, GlobalPosition = at, Relative = (_dragTo - _dragFrom) / 12f, ButtonMask = MouseButtonMask.Left });
                _dragFrame++;
                return false;
            case 13:
                Mouse(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = _dragTo, GlobalPosition = _dragTo });
                _dragAt = _stepAge;
                _dragFrame++;
                return false;
            case 14 when _stepAge - _dragAt > 1.5:
                var name = Server.Spells?.Get((int)_dragAction.Id)?.Name;
                if (Server.ActionButtons[DragSlot] == _dragAction)
                {
                    GD.Print($"Spell check: PASS dragging {name} from the spellbook put it on button -");
                }
                else
                {
                    Fail($"dragging {name} to button - left {Server.ActionButtons[DragSlot]}");
                }

                Server.SetActionButton(DragSlot, _dragWas);
                _dragFrame++;
                return true;
            case 14:
                return false;
            default:
                return true;
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    private bool _printed;
    private bool _castShot;
    private bool _castSeen;
    private double _castShownAt;

    private double _loadedFor;

    /// <summary>The loading screen has been gone a few seconds, so the world and the HUD show.</summary>
    private bool Loaded
    {
        get
        {
            var loading = GetParent().GetNodeOrNull<Ui.LoadingScreen>("LoadingScreen");
            _loadedFor = loading is { Showing: true } ? 0 : _loadedFor + GetProcessDeltaTime();
            return _loadedFor > 4;
        }
    }

    private void Next()
    {
        _step++;
        _stepAge = 0;
    }

    private void Fail(string why)
    {
        _failures.Add(why);
        GD.Print($"Spell check: FAIL {why}");
    }

    private void Shot(string name)
    {
        var folder = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots="))?["--shots=".Length..];
        if (folder is not null)
        {
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"spell-{name}.png"));
        }
    }

    private void Finish()
    {
        GD.Print(_failures.Count == 0 ? "Spell check: PASS" : $"Spell check: FAIL ({string.Join("; ", _failures)})");
        SetProcess(false);
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }
}
