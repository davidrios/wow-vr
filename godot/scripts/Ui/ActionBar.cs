using System;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>What's dragged onto a button: a spell from the spellbook, an item from the bags, or another button's action.</summary>
public static class ActionDrag
{
    public static Godot.Collections.Dictionary Make(ActionButton action, int fromSlot = -1) => new()
    {
        ["id"] = action.Id,
        ["kind"] = (int)action.Kind,
        ["from"] = fromSlot,
    };

    public static (ActionButton Action, int FromSlot)? Read(Variant data) =>
        data.VariantType == Variant.Type.Dictionary && data.AsGodotDictionary() is { } d && d.ContainsKey("id") && d.ContainsKey("kind")
            ? (new ActionButton(d["id"].AsUInt32(), (ActionKind)d["kind"].AsInt32()), d.TryGetValue("from", out var from) ? from.AsInt32() : -1)
            : null;

    /// <summary>A square of the icon that follows the pointer while dragging.</summary>
    public static Control Preview(Texture2D? icon, float size = 44)
    {
        var preview = new TextureRect
        {
            Texture = icon,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            Size = new Vector2(size, size),
            Position = new Vector2(-size / 2, -size / 2),
        };
        var holder = new Control();
        holder.AddChild(preview);
        return holder;
    }
}

/// <summary>
/// An icon that can be dragged to the action bar, as a spell in the spellbook or an item in the
/// bags. Pressing it does its own thing, as casting the spell.
/// </summary>
public partial class ActionIcon : Button
{
    public ActionButton Action { get; set; }

    public ActionIcon(Texture2D? icon, ActionButton action, float size)
    {
        Action = action;
        Icon = icon;
        ExpandIcon = true;
        IconAlignment = HorizontalAlignment.Center;
        CustomMinimumSize = new Vector2(size, size);
        FocusMode = FocusModeEnum.None;
        Flat = true;
        AddThemeConstantOverride("icon_max_width", (int)size);
        AddThemeStyleboxOverride("normal", GlueTheme.Box(new Color(0, 0, 0, 0.5f), 3, 1, GlueTheme.Edge.Darkened(0.4f)));
        AddThemeStyleboxOverride("hover", GlueTheme.Box(new Color(0.2f, 0.15f, 0.05f, 0.6f), 3, 1, GlueTheme.Accent));
        AddThemeStyleboxOverride("pressed", GlueTheme.Box(new Color(0.3f, 0.2f, 0.05f, 0.7f), 3, 1, GlueTheme.Accent));
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (Action.IsEmpty || Disabled)
        {
            return default;
        }

        SetDragPreview(ActionDrag.Preview(Icon));
        return ActionDrag.Make(Action);
    }
}

/// <summary>
/// One of the main bar's twelve buttons: its icon, its key, its cooldown darkening it from the
/// top with the seconds left, and an item's count. Dropping a spell or item on it puts it there,
/// and dragging it off clears it, as in the game.
/// </summary>
public partial class ActionSlot : Button
{
    public const float Size = 52;

    private readonly int _slot;
    private readonly ServerWorld _world;
    private readonly Func<ActionButton?> _held;
    private readonly Action _placed;
    private TextureRect _icon = null!;
    private ColorRect _cooldown = null!;
    private Label _seconds = null!;
    private Label _count = null!;
    private ActionButton _shown;
    private int _shownSeconds = -1;

    /// <summary>The slot being dragged off, cleared if the drag ends nowhere.</summary>
    private static int s_draggingFrom = -1;

    public ActionSlot(int slot, string key, ServerWorld world, Func<ActionButton?> held, Action placed)
    {
        _slot = slot;
        _world = world;
        _held = held;
        _placed = placed;
        CustomMinimumSize = new Vector2(Size, Size);
        FocusMode = FocusModeEnum.None;
        ClipContents = true;
        AddThemeStyleboxOverride("normal", GlueTheme.Box(new Color(0, 0, 0, 0.55f), 4, 0, GlueTheme.Edge.Darkened(0.3f)));
        AddThemeStyleboxOverride("hover", GlueTheme.Box(new Color(0, 0, 0, 0.55f), 4, 0, GlueTheme.Accent));
        AddThemeStyleboxOverride("pressed", GlueTheme.Box(new Color(0.3f, 0.2f, 0.05f, 0.7f), 4, 0, GlueTheme.Accent));

        _icon = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _icon.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 3);
        AddChild(_icon);

        _cooldown = new ColorRect { Color = new Color(0, 0, 0, 0.65f), MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        AddChild(_cooldown);

        _seconds = Text(22, HorizontalAlignment.Center, VerticalAlignment.Center, Colors.White);
        _seconds.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var keyLabel = Text(14, HorizontalAlignment.Left, VerticalAlignment.Top, new Color(0.85f, 0.85f, 0.85f));
        keyLabel.Text = key;
        keyLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 3);
        _count = Text(15, HorizontalAlignment.Right, VerticalAlignment.Bottom, Colors.White);
        _count.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 3);

        Pressed += OnPressed;
    }

    private Label Text(int size, HorizontalAlignment h, VerticalAlignment v, Color color)
    {
        var label = new Label { HorizontalAlignment = h, VerticalAlignment = v, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 4);
        AddChild(label);
        return label;
    }

    private ActionButton Action => _slot < _world.ActionButtons.Count ? _world.ActionButtons[_slot] : default;

    private void OnPressed()
    {
        if (_held() is { } held)
        {
            _world.SetActionButton(_slot, held);
            _placed();
            return;
        }

        _world.UseAction(_slot);
    }

    /// <summary>Looks up the icon and name again on the next refresh, as once Spell.dbc is read or an item becomes known.</summary>
    public void Invalidate() => _shown = new ActionButton(uint.MaxValue, ActionKind.Macro);

    /// <summary>Shows what's on the button now, its cooldown, whether there's power for it, and an item's count.</summary>
    public void Refresh()
    {
        var action = Action;
        if (action != _shown)
        {
            _shown = action;
            _icon.Texture = action.IsEmpty ? null
                : action.Kind == ActionKind.Item ? _world.ItemIcon(action.Id)
                : _world.SpellIcon(action.Id);
            var record = action.Kind == ActionKind.Spell ? _world.Spells?.Get((int)action.Id) : null;
            TooltipText = action.IsEmpty ? "" : action.Kind == ActionKind.Item
                ? _world.Session.ItemInfoOf(action.Id)?.Name ?? $"Item {action.Id}"
                : record?.FullName ?? $"Spell {action.Id}";
        }

        if (action.IsEmpty)
        {
            _cooldown.Visible = false;
            _seconds.Text = "";
            _count.Text = "";
            return;
        }

        if (action.Kind == ActionKind.Item)
        {
            var count = _world.CountOf(action.Id);
            _count.Text = count > 1 ? count.ToString() : "";
            _icon.Modulate = count > 0 ? Colors.White : new Color(0.4f, 0.4f, 0.4f);
            _cooldown.Visible = false;
            return;
        }

        var spell = _world.Spells?.Get((int)action.Id);
        _icon.Modulate = spell is not null && !_world.HasPowerFor(spell) ? new Color(0.45f, 0.45f, 1f) : Colors.White;
        var (left, total) = _world.CooldownOf(action.Id);
        _cooldown.Visible = left > 0 && total > 0;
        if (_cooldown.Visible)
        {
            var height = (float)(Size * Math.Clamp(left / total, 0, 1));
            _cooldown.Position = Vector2.Zero;
            _cooldown.Size = new Vector2(Size, height);
        }

        // The seconds show only for waits longer than the global cooldown, as in the game.
        var seconds = left > 1.6 ? (int)Math.Ceiling(left) : 0;
        if (seconds != _shownSeconds)
        {
            _shownSeconds = seconds;
            _seconds.Text = seconds == 0 ? "" : seconds >= 60 ? $"{(seconds + 59) / 60}m" : seconds.ToString();
        }
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        var action = Action;
        if (action.IsEmpty)
        {
            return default;
        }

        s_draggingFrom = _slot;
        SetDragPreview(ActionDrag.Preview(_icon.Texture));
        return ActionDrag.Make(action, _slot);
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => ActionDrag.Read(data) is not null;

    /// <summary>Dropped here: it takes the action, and a button dragged from swaps with this one.</summary>
    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (ActionDrag.Read(data) is not { } dropped)
        {
            return;
        }

        var here = Action;
        _world.SetActionButton(_slot, dropped.Action);
        if (dropped.FromSlot >= 0 && dropped.FromSlot != _slot)
        {
            _world.SetActionButton(dropped.FromSlot, here);
        }

        s_draggingFrom = -1;
    }

    public override void _Notification(int what)
    {
        // Dragged off the bar to nowhere: the button is cleared.
        if (what == NotificationDragEnd && s_draggingFrom == _slot)
        {
            s_draggingFrom = -1;
            if (!GetViewport().GuiIsDragSuccessful())
            {
                _world.SetActionButton(_slot, default);
            }
        }
    }
}
