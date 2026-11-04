using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.Database;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The spellbook: the spells the player knows, on a tab for each of the class's trees and General
/// for the rest, each with its icon, name and rank. Clicking an icon casts it; dragging it to the
/// bar, or clicking its name and then a button, puts it there.
/// </summary>
public partial class SpellBook : PanelContainer
{
    private const string General = "General";
    private const float IconSize = 44;

    private readonly ServerWorld _world;
    private readonly Action<ActionButton, string> _pickUp;
    private HFlowContainer _tabs = null!;
    private GridContainer _grid = null!;
    private Label _empty = null!;
    private string _tab = General;

    public SpellBook(ServerWorld world, Action<ActionButton, string> pickUp)
    {
        _world = world;
        _pickUp = pickUp;
    }

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 14, GlueTheme.Edge));
        // On the left, as in the game, beside the bags on the right.
        Position = new Vector2(16, 110);
        CustomMinimumSize = new Vector2(580, 0);
        var outer = new VBoxContainer();
        AddChild(outer);

        var header = new HBoxContainer();
        outer.AddChild(header);
        var title = GlueTheme.Title("Spellbook");
        title.AddThemeFontSizeOverride("font_size", 24);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        var close = GlueTheme.Plain("X", 36);
        close.CustomMinimumSize = new Vector2(40, 36);
        close.Pressed += () => Visible = false;
        header.AddChild(close);

        _tabs = new HFlowContainer { CustomMinimumSize = new Vector2(550, 0) };
        _tabs.AddThemeConstantOverride("h_separation", 6);
        _tabs.AddThemeConstantOverride("v_separation", 6);
        outer.AddChild(_tabs);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(550, 420), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(body);
        _empty = GlueTheme.Note("");
        body.AddChild(_empty);
        _grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 10);
        _grid.AddThemeConstantOverride("v_separation", 6);
        body.AddChild(_grid);

        var hint = GlueTheme.Note("Click an icon to cast. Drag it to the bar, or click the name and then a button.", GlueTheme.Muted);
        hint.AddThemeFontSizeOverride("font_size", 14);
        outer.AddChild(hint);

        _world.SpellsChanged += () =>
        {
            if (Visible)
            {
                Refresh();
            }
        };
        VisibilityChanged += () =>
        {
            if (Visible)
            {
                Refresh();
            }
        };
    }

    /// <summary>The spells the book shows: known and not hidden, each on its tab.</summary>
    private List<(SpellRecord Spell, string Tab)> Shown(SpellTable spells) =>
        [.. _world.KnownSpells
            .Select(id => spells.Get((int)id))
            .OfType<SpellRecord>()
            .Where(s => !s.IsHidden && s.Name.Length > 0 && spells.InSpellbook(s.Id))
            .Select(s => (s, spells.TabOf(s.Id)?.Name ?? General))
            .DistinctBy(s => s.s.Id)];

    private void Refresh()
    {
        foreach (var child in _tabs.GetChildren().Concat(_grid.GetChildren()))
        {
            child.QueueFree();
        }

        if (_world.Spells is not { } spells)
        {
            _empty.Text = "Reading the client's spells...";
            _empty.Visible = true;
            return;
        }

        var shown = Shown(spells);
        var tabs = shown.Select(s => s.Tab).Distinct().OrderBy(t => t == General ? 0 : 1).ThenBy(t => t).ToList();
        if (!tabs.Contains(_tab))
        {
            _tab = tabs.FirstOrDefault() ?? General;
        }

        foreach (var tab in tabs)
        {
            var name = tab;
            var button = GlueTheme.Plain(tab, 34);
            button.FocusMode = FocusModeEnum.None;
            button.CustomMinimumSize = new Vector2(90, 34);
            if (tab == _tab)
            {
                button.AddThemeColorOverride("font_color", GlueTheme.Accent);
                button.AddThemeStyleboxOverride("normal", GlueTheme.Box(GlueTheme.Selected, 6, 8, GlueTheme.Accent));
            }

            button.Pressed += () =>
            {
                _tab = name;
                Refresh();
            };
            _tabs.AddChild(button);
        }

        var page = shown.Where(s => s.Tab == _tab)
            .OrderBy(s => s.Spell.IsPassive)
            .ThenBy(s => s.Spell.Name, StringComparer.CurrentCulture)
            .ToList();
        _empty.Visible = page.Count == 0;
        _empty.Text = "You know no spells here.";
        foreach (var (spell, _) in page)
        {
            AddEntry(spell);
        }
    }

    private void AddEntry(SpellRecord spell)
    {
        var row = new HBoxContainer { CustomMinimumSize = new Vector2(265, IconSize), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        _grid.AddChild(row);

        var action = new ActionButton((uint)spell.Id, ActionKind.Spell);
        var icon = new ActionIcon(_world.Icons.Get(spell.IconPath), spell.IsPassive ? default : action, IconSize)
        {
            TooltipText = spell.FullName,
        };
        if (spell.IsPassive)
        {
            icon.Modulate = new Color(0.7f, 0.7f, 0.7f);
        }
        else
        {
            icon.Pressed += () => _world.Cast((uint)spell.Id);
        }

        row.AddChild(icon);

        var name = new Button
        {
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            ClipText = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            FocusMode = FocusModeEnum.None,
            Text = spell.Rank.Length > 0 || spell.IsPassive ? $"{spell.Name}\n{(spell.IsPassive ? "Passive" : spell.Rank)}" : spell.Name,
            TooltipText = spell.IsPassive ? "Passive: always on." : "Click, then click a button on the bar to put it there.",
            Disabled = spell.IsPassive,
        };
        name.AddThemeFontSizeOverride("font_size", 15);
        name.AddThemeColorOverride("font_color", GlueTheme.Accent);
        name.AddThemeColorOverride("font_disabled_color", GlueTheme.Muted);
        name.Pressed += () => _pickUp(action, spell.Name);
        row.AddChild(name);
    }
}
