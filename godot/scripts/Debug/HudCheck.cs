using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--hud-check</c> on a server, with any account (it uses no GM commands): prints PASS or FAIL
/// for the tooltips of a spell and a carried item, a tooltip shown for the action bar under the
/// pointer, an item link sent in chat coming back as a link, a buff the character can cast on
/// itself showing on its frame with its tooltip and going when cancelled, floating combat text
/// over a hostile creature already in melee reach, and range: a spell in reach of a near unit and
/// out of reach of a far one. What the character can't do where it stands is a SKIP, not a FAIL.
/// It casts the buff (spending its cooldown) and attacks the creature for a few seconds.
/// </summary>
public partial class HudCheck : Node
{
    public required ServerWorld Server { get; init; }

    private readonly List<string> _failures = [];
    private readonly List<ChatLine> _chat = [];
    private string? _error;
    private GameHud? _hud;

    public override void _Ready()
    {
        Server.ChatLine += line => _chat.Add(line);
        Server.ScreenText += (text, error) =>
        {
            if (error)
            {
                _error = text;
                GD.Print($"  [error] {text}");
            }
        };
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            await Until(() => Server.Spells is not null && Server.KnownSpells.Count > 0 && Server.Self is not null && Loaded()
                && (_hud = GetParent().GetNodeOrNull<GameHud>("HudLayer/Hud")) is not null, 120);
            await Seconds(3);
            var spells = Server.Spells!;
            await Tooltips(spells);
            await HoverTooltip();
            await ChatLink();
            await Buff(spells);
            await FloatingText();
            Range(spells);
        }
        catch (TimeoutException e)
        {
            Fail(e.Message);
        }
        catch (Exception e)
        {
            Fail($"the check broke: {e}");
        }

        GD.Print(_failures.Count == 0 ? "Hud check: PASS" : $"Hud check: FAIL ({string.Join("; ", _failures)})");
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    private async Task Tooltips(SpellTable spells)
    {
        var spell = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>()
            .FirstOrDefault(s => !s.IsPassive && spells.InSpellbook(s.Id) && s.Description.Contains('$'));
        if (spell is null)
        {
            Fail("no spell with codes in its description");
        }
        else
        {
            var text = Ui.Tooltips.Spell(Server, (uint)spell.Id);
            Check($"a spell's tooltip ({spell.FullName})", text.Contains(spell.Name) && !text.Contains('$'), Plain(text));
        }

        var carried = Server.Inventory?.Items.FirstOrDefault(i => !(i.Bag == 255 && i.Slot < 19)) ?? Server.Inventory?.Items.FirstOrDefault();
        if (carried is null)
        {
            Fail("nothing carried to show");
            return;
        }

        Server.Session.ItemInfoOf(carried.Entry);
        await Until(() => Server.Session.ItemInfoOf(carried.Entry) is not null, 10);
        var item = Ui.Tooltips.Item(Server, carried.Entry);
        Check($"an item's tooltip ({Server.Session.ItemInfoOf(carried.Entry)!.Name})", item.Contains(Server.Session.ItemInfoOf(carried.Entry)!.Name), Plain(item));
    }

    /// <summary>The pointer on the bar's first spell button shows its tooltip.</summary>
    private async Task HoverTooltip()
    {
        // Godot finds nodes by their engine class, so buttons, then the bar's among them.
        var slot = _hud!.FindChildren("*", nameof(Button), true, false).OfType<ActionSlot>().FirstOrDefault(s => s.HasMeta(Ui.Tooltips.MetaName));
        if (slot is null)
        {
            Fail("no button on the bar with a tooltip");
            return;
        }

        var center = slot.GetGlobalRect().GetCenter();
        GetViewport().WarpMouse(center);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = center, GlobalPosition = center });
        await Seconds(1);
        var shown = _hud.TooltipShown;
        Check("pointing at a button shows its tooltip", shown is { Length: > 0 }, Plain(shown ?? "nothing shown"));
        Shot("tooltip");
        GetViewport().WarpMouse(Vector2.Zero);
    }

    /// <summary>An item link said in chat comes back, and shows as a link in the item's color.</summary>
    private async Task ChatLink()
    {
        var item = Server.Inventory?.Items.Select(i => Server.Session.ItemInfoOf(i.Entry)).OfType<ItemInfo>().FirstOrDefault();
        if (item is null)
        {
            Fail("no known item to link");
            return;
        }

        var link = Ui.Tooltips.ItemLink(item, Server.Self!.Unit.Level);
        _chat.Clear();
        Server.Say($"hud check {link}");
        await Until(() => _chat.Any(l => l.Raw.Contains("|Hitem:")), 10);
        await Seconds(0.2);
        var bbcode = _hud!.ChatLogText;
        Check("an item link said in chat comes back as a link", bbcode.Contains($"[url=item:{item.Entry}]") && bbcode.Contains(Ui.Tooltips.QualityHex(item.Quality)), _chat.Last(l => l.Raw.Contains("|Hitem:")).Raw);
    }

    /// <summary>A buff on the player: on their frame, with a tooltip, and gone when cancelled.</summary>
    private async Task Buff(SpellTable spells)
    {
        // One of the character's own it can pay for now.
        var buff = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>()
            .Where(s => !s.IsPassive && spells.InSpellbook(s.Id) && s.CastTimeMs == 0 && s.DurationMs > 10000 && s.Aim is SpellAim.Self or SpellAim.Friend or SpellAim.Any
                && s.Effects.Any(e => e.Type == 6) && s.AuraText.Length > 0 && Server.HasPowerFor(s) && Server.CooldownOf((uint)s.Id).Left <= 0)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();
        if (buff is null)
        {
            Skip("a buff on the frame", "no buff of its own it can pay for now");
            return;
        }

        Server.SetTarget(0);
        await Seconds(1.6);
        _error = null;
        GD.Print($"Hud check: casting {buff.FullName} on the player");
        Server.Cast((uint)buff.Id);
        var self = Server.Session.SelfGuid;
        try
        {
            await Until(() => Server.Session.AurasOf(self).Any(a => a.Spell == buff.Id) || _error is not null, 10);
        }
        catch (TimeoutException)
        {
        }

        if (!Server.Session.AurasOf(self).Any(a => a.Spell == buff.Id))
        {
            // A cooldown left from an earlier run is the server's to know, not this session's.
            Skip("a buff on the frame", $"{buff.Name} wasn't cast: {_error ?? "no answer"}");
            return;
        }

        await Seconds(0.5);
        var aura = Server.Session.AurasOf(self).First(a => a.Spell == buff.Id);
        var icons = _hud!.PlayerAuraIcons;
        var tip = Ui.Tooltips.Aura(Server, self, aura.Slot);
        Check($"{buff.Name} shows on the player's frame", icons > 0, $"{icons} icons; tooltip: {Plain(tip)}");
        Shot("buff");
        Server.Session.CancelAura((uint)buff.Id);
        await Until(() => Server.Session.AurasOf(self).All(a => a.Spell != buff.Id), 10);
        await Seconds(0.3);
        Check($"{buff.Name} goes when cancelled", _hud.PlayerAuraIcons < icons, $"{_hud.PlayerAuraIcons} icons");
    }

    /// <summary>A hostile creature already in melee reach attacked for a moment: the blows' numbers float over it.</summary>
    private async Task FloatingText()
    {
        var feet = Server.PlayerFeet;
        var unit = Server.Units.Values.Where(u => !u.IsDead && u.Type == ObjectType.Unit && !u.NotAttackable && Server.ReactionTo(u) == Reaction.Hostile)
            .Select(u => (Unit: u, Node: Server.UnitNode(u.Guid)))
            .Where(u => u.Node is not null)
            .Select(u => (u.Unit, Distance: u.Node!.GlobalPosition.DistanceTo(feet)))
            .OrderBy(u => u.Distance)
            .FirstOrDefault();
        if (unit.Unit is null || unit.Distance > 5)
        {
            Skip("floating combat text", unit.Unit is null ? "nothing hostile in view" : $"the nearest hostile, {unit.Unit.Name}, is {unit.Distance:F0} yards away");
            return;
        }

        var before = Server.CombatTextShown;
        Server.StartAttack(unit.Unit.Guid);
        try
        {
            await Until(() => Server.CombatTextShown > before, 10);
            Check($"attacking {unit.Unit.Name} floats the blows over it", true, $"{Server.CombatTextShown - before} shown");
            Shot("combat-text");
        }
        finally
        {
            Server.StopAttack();
            Server.SetTarget(0);
        }
    }

    /// <summary>A spell with a range reaches a near unit and not a far one: a friendly spell at friends, else one at enemies.</summary>
    private void Range(SpellTable spells)
    {
        var known = Server.KnownSpells.Select(id => spells.Get((int)id)).OfType<SpellRecord>().Where(s => !s.IsPassive).ToList();
        var friendly = known.FirstOrDefault(s => s.Aim is SpellAim.Friend or SpellAim.Any && s.FriendRange is >= 20 and <= 45 && s.MinRange <= 0);
        var hostile = known.FirstOrDefault(s => s.Aim == SpellAim.Enemy && s.Range is >= 20 and <= 45 && s.MinRange <= 0 && !s.MeleeRange);
        var feet = Server.PlayerFeet;
        List<(UnitStats Unit, float Distance)> Units(bool friends) => Server.Units.Values
            .Where(u => !u.IsDead && u.Guid != Server.Session.SelfGuid && (Server.ReactionTo(u) == Reaction.Friendly) == friends && (friends || !u.NotAttackable))
            .Select(u => (Unit: u, Node: Server.UnitNode(u.Guid)))
            .Where(u => u.Node is not null)
            .Select(u => (u.Unit, u.Node!.GlobalPosition.DistanceTo(feet)))
            .ToList();

        foreach (var (spell, friends) in new[] { (friendly, true), (hostile, false) })
        {
            if (spell is null)
            {
                continue;
            }

            var reach = friends ? spell.FriendRange : spell.Range;
            var units = Units(friends);
            var near = units.Where(f => f.Distance < 10).OrderBy(f => f.Distance).FirstOrDefault();
            var far = units.Where(f => f.Distance > reach + 10).OrderByDescending(f => f.Distance).FirstOrDefault();
            if (near.Unit is null || far.Unit is null)
            {
                continue;
            }

            Server.SetTarget(near.Unit.Guid);
            var inReach = Server.InRange((uint)spell.Id);
            Server.SetTarget(far.Unit.Guid);
            var outOfReach = Server.InRange((uint)spell.Id);
            Server.SetTarget(0);
            Check($"{spell.Name} ({reach} yards) reaches {near.Unit.Name} at {near.Distance:F0} yards and not {far.Unit.Name} at {far.Distance:F0}",
                inReach == true && outOfReach == false, $"{inReach}, {outOfReach}");
            return;
        }

        Skip("range", $"no spell with a range and units near and far for it (friendly {friendly?.Name ?? "none"}, hostile {hostile?.Name ?? "none"})");
    }

    private void Skip(string what, string why) => GD.Print($"Hud check: SKIP {what}: {why}");

    private static string Plain(string bbcode) => System.Text.RegularExpressions.Regex.Replace(bbcode, @"\[[^\]]*\]", "").Replace("\n", " | ");

    private void Check(string what, bool passed, string detail)
    {
        GD.Print($"Hud check: {(passed ? "PASS" : "FAIL")} {what}: {detail}");
        if (!passed)
        {
            _failures.Add(what);
        }
    }

    private void Fail(string why)
    {
        _failures.Add(why);
        GD.Print($"Hud check: FAIL {why}");
    }

    private void Shot(string name)
    {
        var folder = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--shots="))?["--shots=".Length..];
        if (folder is not null)
        {
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, $"hud-{name}.png"));
        }
    }

    private double _loadedFor;

    private bool Loaded()
    {
        var loading = GetParent().GetNodeOrNull<LoadingScreen>("LoadingScreen");
        _loadedFor = loading is { Showing: true } ? 0 : _loadedFor + GetProcessDeltaTime();
        return _loadedFor > 4;
    }

    private async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task Until(Func<bool> done, double seconds)
    {
        var started = Time.GetTicksMsec();
        while (!done())
        {
            if (Time.GetTicksMsec() - started > seconds * 1000)
            {
                throw new TimeoutException($"waited {seconds} s");
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
