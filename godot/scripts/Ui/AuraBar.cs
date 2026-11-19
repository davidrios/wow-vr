using System;
using System.Collections.Generic;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// A unit's buffs, then its debuffs, as icons under its frame: each with its stacks and time left,
/// debuffs edged in red, and the aura's tooltip. Right-clicking one of the player's own buffs
/// cancels it. Icons are made again only when the auras change, not each time the server
/// repeats them.
/// </summary>
public partial class AuraBar : HFlowContainer
{
    private const float IconSize = 30;

    private static readonly StringName FontSize = "font_size";
    private static readonly StringName FontOutlineColor = "font_outline_color";
    private static readonly StringName OutlineSize = "outline_size";
    private static readonly StringName PanelStyle = "panel";
    private static StyleBoxFlat? s_buffEdge;
    private static StyleBoxFlat? s_debuffEdge;

    /// <summary>Buffs before debuffs, then by slot, as the game orders them.</summary>
    private static readonly Comparison<AuraInfo> Order = (a, b) => a.Harmful != b.Harmful ? a.Harmful.CompareTo(b.Harmful) : a.Slot.CompareTo(b.Slot);

    private readonly List<AuraIcon> _icons = [];
    private readonly List<AuraInfo> _shown = [];
    private ulong _unit;

    /// <summary>The icons shown were made before Spell.dbc was read.</summary>
    private bool _withoutSpells;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Pass;
        AddThemeConstantOverride("h_separation", 3);
        AddThemeConstantOverride("v_separation", 3);
    }

    /// <summary>Shows a unit's auras as the session has them now; 0 shows none.</summary>
    public void Show(ServerWorld world, ulong unit)
    {
        var auras = unit == 0 ? [] : new List<AuraInfo>(world.Session.AurasOf(unit));
        auras.Sort(Order);

        // Spell.dbc takes a moment to read on the first login, and icons made before it are question marks.
        var spellsKnown = world.Spells is not null;
        if (unit == _unit && Same(auras) && !(spellsKnown && _withoutSpells))
        {
            return;
        }

        _withoutSpells = !spellsKnown;
        _unit = unit;
        _shown.Clear();
        _shown.AddRange(auras);
        foreach (var icon in _icons)
        {
            icon.QueueFree();
        }

        _icons.Clear();
        Visible = auras.Count > 0;
        foreach (var aura in auras)
        {
            var spell = world.Spells?.Get((int)aura.Spell);
            var icon = new AuraIcon(world, unit, aura, world.Icons.Get(spell?.IconPath));
            _icons.Add(icon);
            AddChild(icon);
        }
    }

    /// <summary>The same auras as shown, by spell, stacks and when they were last set.</summary>
    private bool Same(List<AuraInfo> auras)
    {
        if (auras.Count != _shown.Count)
        {
            return false;
        }

        for (var i = 0; i < auras.Count; i++)
        {
            if (auras[i] != _shown[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Counts the times down.</summary>
    public void Tick()
    {
        if (!Visible || _icons.Count == 0)
        {
            return;
        }

        var now = RealmSession.AuraClock();
        foreach (var icon in _icons)
        {
            icon.Tick(now);
        }
    }

    /// <summary>How many icons show, for checks.</summary>
    public int IconCount => _icons.Count;

    private static StyleBoxFlat Edge(bool harmful) => harmful
        ? s_debuffEdge ??= GlueTheme.Box(Colors.Black, 3, 0, new Color(0.9f, 0.1f, 0.1f))
        : s_buffEdge ??= GlueTheme.Box(Colors.Black, 3, 0, GlueTheme.Edge.Darkened(0.3f));

    private sealed partial class AuraIcon : Control
    {
        private readonly ServerWorld _world;
        private readonly ulong _unit;
        private readonly AuraInfo _aura;
        private readonly Label _time;
        private int _shownSeconds = -1;

        public AuraIcon(ServerWorld world, ulong unit, AuraInfo aura, Texture2D? icon)
        {
            (_world, _unit, _aura) = (world, unit, aura);
            CustomMinimumSize = new Vector2(IconSize, IconSize + 14);
            MouseFilter = MouseFilterEnum.Stop;
            Tooltips.Set(this, Tooltips.AuraKey(unit, aura.Slot));

            var edge = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(IconSize, IconSize) };
            edge.AddThemeStyleboxOverride(PanelStyle, Edge(aura.Harmful));
            AddChild(edge);
            AddChild(new TextureRect
            {
                Texture = icon,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                Position = new Vector2(2, 2),
                Size = new Vector2(IconSize - 4, IconSize - 4),
                MouseFilter = MouseFilterEnum.Ignore,
            });

            if (aura.Stacks > 1)
            {
                var stacks = Text($"{aura.Stacks}", 13);
                stacks.Position = new Vector2(IconSize - 14, IconSize - 18);
                AddChild(stacks);
            }

            _time = Text("", 11);
            _time.Position = new Vector2(0, IconSize - 1);
            _time.Size = new Vector2(IconSize, 14);
            _time.HorizontalAlignment = HorizontalAlignment.Center;
            AddChild(_time);
            Tick(RealmSession.AuraClock());
        }

        private static Label Text(string text, int size)
        {
            var label = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
            label.AddThemeFontSizeOverride(FontSize, size);
            label.AddThemeColorOverride(FontOutlineColor, Colors.Black);
            label.AddThemeConstantOverride(OutlineSize, 4);
            return label;
        }

        /// <summary>Seconds under a minute, then minutes, as the game's buff timers. The text changes once a second at most.</summary>
        public void Tick(double now)
        {
            var left = _aura.LeftAt(now);
            var seconds = left is { } l ? (int)Math.Ceiling(l) : -1;
            if (seconds == _shownSeconds)
            {
                return;
            }

            _shownSeconds = seconds;
            _time.Text = seconds < 0 ? "" : seconds < 60 ? $"{seconds}s" : seconds < 3600 ? $"{(seconds + 59) / 60}m" : $"{(seconds + 3599) / 3600}h";
        }

        public override void _GuiInput(InputEvent e)
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } && _unit == _world.Session.SelfGuid && !_aura.Harmful)
            {
                _world.Session.CancelAura(_aura.Spell);
                AcceptEvent();
            }
        }
    }
}
