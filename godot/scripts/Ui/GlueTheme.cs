using Godot;

namespace WowVR.Ui;

/// <summary>
/// The login and character screens' look: laid out like VRChat's menus, with large type and
/// buttons big enough to hit with a laser pointer, in the game's own colors: near-black panels
/// with gold edges, the dark red glue buttons, gold text, and the client's own font once it's open.
/// </summary>
public static class GlueTheme
{
    public static readonly Color Panel = new(0.04f, 0.03f, 0.02f, 0.9f);
    public static readonly Color Edge = new(0.72f, 0.57f, 0.27f);
    public static readonly Color Card = new(0.42f, 0.05f, 0.03f, 0.95f);
    public static readonly Color CardHover = new(0.58f, 0.09f, 0.05f, 1f);
    public static readonly Color Selected = new(0.22f, 0.16f, 0.05f, 0.95f);
    public static readonly Color Accent = new(1f, 0.82f, 0f);
    public static readonly Color Text = new(1f, 0.82f, 0f);
    public static readonly Color Highlight = new(1f, 1f, 1f);
    public static readonly Color Muted = new(0.8f, 0.74f, 0.62f);
    public static readonly Color Error = new(1f, 0.25f, 0.2f);
    public static readonly Color Alliance = new(0.35f, 0.55f, 1f);
    public static readonly Color Horde = new(0.95f, 0.25f, 0.2f);

    public const int FontSize = 26;
    public const int TitleSize = 40;
    public const int SmallSize = 20;
    public const int Radius = 6;

    private static Theme? _theme;

    public static Theme Theme => _theme ??= Build(FontSize, 16);

    private static Theme? _compact;

    /// <summary>The same look in smaller type and padding, for the in-game menu, whose wrist panel in VR is small.</summary>
    public static Theme Compact => _compact ??= Build(17, 8);

    public static StyleBoxFlat Box(Color color, int radius = Radius, int padding = 16, Color? border = null)
    {
        var box = new StyleBoxFlat
        {
            BgColor = color,
            ContentMarginLeft = padding,
            ContentMarginRight = padding,
            ContentMarginTop = padding * 0.6f,
            ContentMarginBottom = padding * 0.6f,
            AntiAliasing = true,
        };
        box.SetCornerRadiusAll(radius);
        if (border is { } b)
        {
            box.BorderColor = b;
            box.SetBorderWidthAll(3);
        }

        return box;
    }

    private static Theme Build(int fontSize, int padding)
    {
        var theme = new Theme { DefaultFontSize = fontSize };
        StyleBoxFlat Box(Color color, int radius = Radius, int pad = -1, Color? border = null) => GlueTheme.Box(color, radius, pad < 0 ? padding : pad, border);

        theme.SetStylebox("panel", "PanelContainer", Box(Panel, 10, padding * 7 / 4, Edge));
        theme.SetColor("font_color", "Label", Highlight);

        foreach (var type in new[] { "Button", "CheckBox", "CheckButton" })
        {
            theme.SetStylebox("normal", type, Box(Card, border: Edge.Darkened(0.35f)));
            theme.SetStylebox("hover", type, Box(CardHover, border: Edge));
            theme.SetStylebox("pressed", type, Box(Selected, border: Accent));
            theme.SetStylebox("hover_pressed", type, Box(Selected, border: Accent));
            theme.SetStylebox("disabled", type, Box(new Color(0.15f, 0.12f, 0.1f, 0.6f), border: new Color(Edge, 0.25f)));
            theme.SetStylebox("focus", type, new StyleBoxEmpty());
            theme.SetColor("font_color", type, Text);
            theme.SetColor("font_hover_color", type, Highlight);
            theme.SetColor("font_pressed_color", type, Highlight);
            theme.SetColor("font_hover_pressed_color", type, Highlight);
            theme.SetColor("font_disabled_color", type, new Color(0.5f, 0.5f, 0.5f));
        }

        // A check box is plain text beside its box, as in the game's options.
        theme.SetColor("font_color", "CheckBox", Highlight);
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed" })
        {
            theme.SetStylebox(state, "CheckBox", new StyleBoxEmpty());
        }

        theme.SetStylebox("normal", "LineEdit", Box(new Color(0f, 0f, 0f, 0.85f), 4, padding, new Color(0.5f, 0.5f, 0.5f)));
        theme.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), 4, padding, Accent));
        theme.SetColor("font_color", "LineEdit", Highlight);
        theme.SetColor("caret_color", "LineEdit", Accent);
        theme.SetColor("font_placeholder_color", "LineEdit", new Color(Muted, 0.7f));

        theme.SetStylebox("panel", "ScrollContainer", new StyleBoxEmpty());
        return theme;
    }

    public static Label Title(string text) => new()
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Center,
        LabelSettings = new LabelSettings { FontSize = TitleSize, FontColor = Text, OutlineSize = 4, OutlineColor = Colors.Black },
    };

    public static Label Note(string text, Color? color = null) => new()
    {
        Text = text,
        AutowrapMode = TextServer.AutowrapMode.WordSmart,
        HorizontalAlignment = HorizontalAlignment.Center,
        LabelSettings = new LabelSettings { FontSize = SmallSize, FontColor = color ?? Muted },
    };

    /// <summary>The page's main button, as the game's Enter World is: bigger, with a bright gold edge.</summary>
    public static Button Primary(string text)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 76) };
        button.AddThemeStyleboxOverride("normal", Box(Card, border: Accent));
        button.AddThemeStyleboxOverride("hover", Box(CardHover, border: Highlight));
        button.AddThemeFontSizeOverride("font_size", FontSize + 4);
        return button;
    }

    /// <summary>The client's own font, once its files are open. Labels without a font of their own take it from here.</summary>
    public static void UseFont(Font font)
    {
        Theme.DefaultFont = font;
        Compact.DefaultFont = font;
    }

    public static bool HasFont => Theme.DefaultFont is not null;

    public static Button Plain(string text, float height = 60) => new() { Text = text, CustomMinimumSize = new Vector2(0, height) };
}
