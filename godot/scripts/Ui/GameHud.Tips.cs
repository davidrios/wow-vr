using System.Collections.Generic;
using System.Linq;
using WowVR.Net;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The HUD's tooltip, shown for whatever the pointer rests on that has a <see cref="Tooltips.Meta"/>
/// key, or a link in chat; clicking a link keeps its tooltip up until it's clicked again. Also
/// puts links into what's being typed: shift-clicking an item or a spell adds it as
/// <c>[Name]</c>, which goes out as the game's link.
/// </summary>
public partial class GameHud
{
    /// <summary>The pointer rests this long before a tooltip shows, and the text is rebuilt this often, for times left.</summary>
    private const double TipDelay = 0.25;
    private const double TipRefresh = 0.5;

    private PanelContainer _tip = null!;
    private RichTextLabel _tipText = null!;
    private string? _tipKey;
    private double _tipAge;
    private double _tipBuilt;
    private bool _tipChanges;
    private string? _hoveredLink;
    private Control? _hoveredControl;
    private string? _hoveredKey;
    private int _hoveredVersion = -1;
    private bool _tipHasText;
    private string? _pinnedLink;
    private readonly Dictionary<string, string> _typedLinks = [];

    /// <summary>The tooltip's text while one shows, for checks.</summary>
    public string? TooltipShown => _tip.Visible ? _tipText.Text : null;

    /// <summary>The chat log's BBCode, for checks.</summary>
    public string ChatLogText => _lastChatBbcode;

    private string _lastChatBbcode = "";

    /// <summary>How many buffs and debuffs the player's frame shows.</summary>
    public int PlayerAuraIcons => _playerFrame.Auras.IconCount;

    private void BuildTooltip()
    {
        _tip = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        _tip.AddThemeStyleboxOverride("panel", GlueTheme.Box(new Color(0.02f, 0.02f, 0.06f, 0.94f), 6, 12, GlueTheme.Edge));
        _tipText = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(340, 0),
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _tipText.AddThemeFontSizeOverride("normal_font_size", 16);
        _tip.AddChild(_tipText);
        AddChild(_tip);

        _chatLog.MetaUnderlined = false;
        _chatLog.MetaHoverStarted += meta => _hoveredLink = meta.AsString();
        _chatLog.MetaHoverEnded += _ => _hoveredLink = null;
        _chatLog.MetaClicked += meta =>
        {
            var link = meta.AsString();
            _pinnedLink = _pinnedLink == link ? null : link;
        };
    }

    /// <summary>The key of what the pointer is on: a chat link, else the nearest control up the tree with one.</summary>
    private string? HoveredTip()
    {
        if (_hoveredLink is not null)
        {
            return _hoveredLink;
        }

        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered != _hoveredControl || Tooltips.Version != _hoveredVersion)
        {
            (_hoveredControl, _hoveredVersion) = (hovered, Tooltips.Version);
            _hoveredKey = null;
            for (var control = hovered; control is not null && control != this; control = control.GetParentControl())
            {
                if (control.HasMeta(Tooltips.MetaName))
                {
                    _hoveredKey = control.GetMeta(Tooltips.MetaName).AsString();
                    break;
                }
            }
        }

        return _hoveredKey ?? _pinnedLink;
    }

    private void UpdateTooltip(double delta)
    {
        var key = _world is null ? null : HoveredTip();
        if (key != _tipKey)
        {
            (_tipKey, _tipAge, _tipBuilt) = (key, 0, double.NegativeInfinity);
            _tip.Visible = false;
        }

        if (key is null)
        {
            return;
        }

        _tipAge += delta;
        var pinned = key == _pinnedLink && _hoveredLink is null;
        if (_tipAge < TipDelay && !pinned)
        {
            return;
        }

        if (double.IsNegativeInfinity(_tipBuilt) || (_tipChanges && _tipAge - _tipBuilt >= TipRefresh))
        {
            _tipBuilt = _tipAge;
            string? text;
            try
            {
                text = Tooltips.Text(_world!, key);
            }
            catch (System.Exception e)
            {
                // A bad key, or data, mustn't stop the HUD updating.
                GD.PushWarning($"Tooltip for {key}: {e.Message}");
                text = null;
            }

            if (string.IsNullOrEmpty(text))
            {
                _tipText.Text = "";
                _tipHasText = false;
                _tipChanges = true;
                _tip.Visible = false;
                return;
            }

            _tipChanges = key.StartsWith("aura:", System.StringComparison.Ordinal) || text.Contains("Retrieving", System.StringComparison.Ordinal) || _world!.Spells is null;
            _tipHasText = true;
            if (text != _tipText.Text)
            {
                _tipText.Text = text;
                _tip.ResetSize();
            }
        }

        if (!_tipHasText)
        {
            return;
        }

        _tip.Visible = true;

        // Beside the pointer, kept on screen; a pinned one sits above the chat.
        var area = GetViewportRect().Size;
        var size = _tip.Size;
        var at = pinned ? _chatLog.GlobalPosition - new Vector2(0, size.Y + 8) : GetLocalMousePosition() + new Vector2(24, 24);
        if (at.X + size.X > area.X)
        {
            at.X = GetLocalMousePosition().X - size.X - 16;
        }

        if (at.Y + size.Y > area.Y)
        {
            at.Y = area.Y - size.Y - 8;
        }

        _tip.Position = new Vector2(Mathf.Max(at.X, 0), Mathf.Max(at.Y, 0));
    }

    /// <summary>Shift-click: puts an item or spell into the chat line as [Name], opening it if it's closed.</summary>
    public bool InsertLink(ActionButton action)
    {
        if (_world is null || !Input.IsKeyPressed(Key.Shift))
        {
            return false;
        }

        string? name = null, link = null;
        if (action.Kind == ActionKind.Item && _world.Session.ItemInfoOf(action.Id) is { } item)
        {
            (name, link) = (item.Name, Tooltips.ItemLink(item, _world.Self?.Unit.Level ?? 1));
        }
        else if (action.Kind == ActionKind.Spell && _world.Spells?.Get((int)action.Id) is { } spell)
        {
            (name, link) = (spell.Name, Tooltips.SpellLink(spell));
        }

        if (name is null || link is null)
        {
            return false;
        }

        var shown = $"[{name}]";
        _typedLinks[shown] = link;
        if (!_chatInput.Visible)
        {
            OpenChat();
        }

        _chatInput.InsertTextAtCaret(shown);
        return true;
    }

    /// <summary>Typed [Name]s that came from shift-clicks go out as the links they stand for.</summary>
    private string WithLinks(string text)
    {
        foreach (var (shown, link) in _typedLinks)
        {
            text = text.Replace(shown, link);
        }

        _typedLinks.Clear();
        return text;
    }

    /// <summary>A chat line: its prefix as plain text, the message with its colors and links.</summary>
    private string ChatBbcode(string prefix, ChatLine line) =>
        _lastChatBbcode = Escape(prefix) + (line.Raw.Length > 0 ? ChatMarkup.ToBbcode(line.Raw) : Escape(line.Text));
}
