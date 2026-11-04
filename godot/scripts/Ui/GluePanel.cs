using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The login, realm, character and new character pages. It only shows and asks: <see cref="GlueScreens"/>
/// does the talking to the server. Desktop shows it on screen and VR on a floating panel, so it
/// uses no popups, which don't open on a panel in VR.
/// </summary>
public partial class GluePanel : PanelContainer
{
    public const int Width = 620;
    public const int Height = 1000;

    private readonly Dictionary<string, Control> _pages = [];
    private Label _status = null!;

    // Login
    private LineEdit _host = null!;
    private LineEdit _account = null!;
    private LineEdit _password = null!;
    private CheckBox _remember = null!;
    private Label _rememberWhere = null!;
    private Button _login = null!;

    // Realms and characters
    private VBoxContainer _realmList = null!;
    private VBoxContainer _characterList = null!;
    private Label _realmTitle = null!;
    private Button _enter = null!;
    private Button _delete = null!;
    private Button _create = null!;
    private CharacterInfo? _selected;
    private bool _deleteArmed;

    // New character
    private readonly Dictionary<byte, Button> _raceButtons = [];
    private readonly Dictionary<byte, Button> _classButtons = [];
    private readonly Dictionary<string, Label> _choiceLabels = [];
    private Button _male = null!;
    private Button _female = null!;
    private LineEdit _name = null!;
    private CharacterCreation _creation = new("", 1, 1, 0, 0, 0, 0, 0, 0);
    private Func<byte, byte, bool, CharacterChoices>? _choices;

    public event Action<string, string, string, bool>? LoginRequested;
    public event Action? OfflineRequested;
    public event Action<Realm>? RealmChosen;
    public event Action? LogoutRequested;
    public event Action<CharacterInfo>? CharacterSelected;
    public event Action<CharacterInfo>? EnterRequested;
    public event Action<CharacterInfo>? DeleteRequested;
    public event Action? ChangeRealmRequested;
    public event Action? CreatePageOpened;
    public event Action<CharacterCreation>? CreationChanged;
    public event Action<CharacterCreation>? CreateRequested;
    public event Action? CreateCancelled;

    /// <summary>A text field took focus, or none has it, for showing VR's keyboard.</summary>
    public event Action<bool>? TextFocusChanged;

    public string Page { get; private set; } = "";

    public override void _Ready()
    {
        Theme = GlueTheme.Theme;
        CustomMinimumSize = new Vector2(Width, Height);
        Size = new Vector2(Width, Height);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 18);
        AddChild(column);

        var pages = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(pages);
        AddPage(pages, "login", BuildLogin());
        AddPage(pages, "busy", BuildBusy());
        AddPage(pages, "realms", BuildRealms());
        AddPage(pages, "characters", BuildCharacters());
        AddPage(pages, "create", Scroll(BuildCreate()));

        _status = GlueTheme.Note("");
        _status.CustomMinimumSize = new Vector2(0, 56);
        column.AddChild(_status);

        GetViewport().GuiFocusChanged += OnFocusChanged;
        Show("busy");
    }

    public override void _ExitTree() => GetViewport().GuiFocusChanged -= OnFocusChanged;

    private void OnFocusChanged(Control control) => TextFocusChanged?.Invoke(control is LineEdit);

    private void AddPage(Control parent, string name, Control page)
    {
        page.SetAnchorsPreset(LayoutPreset.FullRect);
        page.Visible = false;
        parent.AddChild(page);
        _pages[name] = page;
    }

    private void Show(string page)
    {
        // A hidden field would keep focus, and VR's keyboard with it.
        GetViewport().GuiReleaseFocus();
        TextFocusChanged?.Invoke(false);
        Page = page;
        foreach (var (name, control) in _pages)
        {
            control.Visible = name == page;
        }

        _status.Text = "";
    }

    public void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        _status.LabelSettings.FontColor = error ? GlueTheme.Error : GlueTheme.Muted;
    }

    // ---- Busy

    private Label _busyText = null!;

    private Control BuildBusy()
    {
        var page = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        _busyText = GlueTheme.Title("");
        _busyText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        page.AddChild(_busyText);
        return page;
    }

    public void ShowBusy(string text)
    {
        Show("busy");
        _busyText.Text = text;
    }

    // ---- Login

    private Control BuildLogin()
    {
        var page = Column();
        page.AddChild(GlueTheme.Title("Log in"));
        page.AddChild(GlueTheme.Note("To an AzerothCore server, with an account on it. Exploring offline needs neither."));
        page.AddChild(Spacer(10));

        _host = Field(page, "Server", "address or name");
        _account = Field(page, "Account", "account name");
        _password = Field(page, "Password", "");
        _password.Secret = true;

        _remember = new CheckBox { Text = "Remember password" };
        page.AddChild(_remember);
        _rememberWhere = GlueTheme.Note("");
        _rememberWhere.HorizontalAlignment = HorizontalAlignment.Left;
        page.AddChild(_rememberWhere);

        page.AddChild(Spacer(10));
        _login = GlueTheme.Primary("Log in");
        _login.Pressed += SubmitLogin;
        page.AddChild(_login);
        foreach (var field in new[] { _host, _account, _password })
        {
            field.TextSubmitted += _ => SubmitLogin();
        }

        var offline = GlueTheme.Plain("Explore offline");
        offline.Pressed += () => OfflineRequested?.Invoke();
        page.AddChild(offline);
        return page;
    }

    private void SubmitLogin()
    {
        var (host, account) = (_host.Text.Trim(), _account.Text.Trim());
        if (host.Length == 0 || account.Length == 0 || _password.Text.Length == 0)
        {
            SetStatus("Enter the server, account and password.", true);
            return;
        }

        LoginRequested?.Invoke(host, account, _password.Text, _remember.ButtonPressed);
    }

    /// <param name="rememberWhere">Where a remembered password goes, or null when this system has nowhere safe.</param>
    public void ShowLogin(string host, string account, string? password, bool remember, string? rememberWhere, string? error = null)
    {
        Show("login");
        _host.Text = host;
        _account.Text = account;
        _password.Text = password ?? "";
        _remember.Disabled = rememberWhere is null;
        _remember.ButtonPressed = remember && rememberWhere is not null;
        _rememberWhere.Text = rememberWhere is null
            ? "This system has no keyring to keep it in, so it's asked for each time."
            : $"Kept in {rememberWhere}, which only your user can read.";
        if (error is not null)
        {
            SetStatus(error, true);
        }

        (host.Length == 0 ? _host : account.Length == 0 ? _account : _password).GrabFocus();
    }

    // ---- Realms

    private Control BuildRealms()
    {
        var page = Column();
        page.AddChild(GlueTheme.Title("Realms"));
        _realmList = new VBoxContainer();
        _realmList.AddThemeConstantOverride("separation", 12);
        page.AddChild(Scroll(_realmList));
        var back = GlueTheme.Plain("Log out");
        back.Pressed += () => LogoutRequested?.Invoke();
        page.AddChild(back);
        return page;
    }

    public void ShowRealms(IReadOnlyList<Realm> realms)
    {
        Show("realms");
        Clear(_realmList);
        foreach (var realm in realms)
        {
            var population = realm.Population switch { < 0.5f => "Low", < 1.5f => "Medium", _ => "High" };
            var button = GlueTheme.Plain($"{realm.Name}\n{realm.TypeName} · {population} · {realm.Characters} characters", 96);
            button.Alignment = HorizontalAlignment.Left;
            button.Disabled = realm.Offline || realm.Locked;
            if (realm.Offline)
            {
                button.Text += " · offline";
            }

            button.Pressed += () => RealmChosen?.Invoke(realm);
            _realmList.AddChild(button);
        }
    }

    // ---- Characters

    private Control BuildCharacters()
    {
        var page = Column();
        _realmTitle = GlueTheme.Title("");
        page.AddChild(_realmTitle);
        _characterList = new VBoxContainer();
        _characterList.AddThemeConstantOverride("separation", 10);
        page.AddChild(Scroll(_characterList));

        _enter = GlueTheme.Primary("Enter world");
        _enter.Pressed += () =>
        {
            if (_selected is not null)
            {
                EnterRequested?.Invoke(_selected);
            }
        };
        page.AddChild(_enter);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        _create = GlueTheme.Plain("New character");
        _create.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _create.Pressed += OpenCreate;
        _delete = GlueTheme.Plain("Delete");
        _delete.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _delete.Pressed += PressDelete;
        row.AddChild(_create);
        row.AddChild(_delete);
        page.AddChild(row);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 12);
        var realm = GlueTheme.Plain("Change realm");
        realm.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        realm.Pressed += () => ChangeRealmRequested?.Invoke();
        var logout = GlueTheme.Plain("Log out");
        logout.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        logout.Pressed += () => LogoutRequested?.Invoke();
        bottom.AddChild(realm);
        bottom.AddChild(logout);
        page.AddChild(bottom);
        return page;
    }

    public void ShowCharacters(string realm, IReadOnlyList<CharacterInfo> characters, string? select)
    {
        Show("characters");
        _realmTitle.Text = realm;
        Clear(_characterList);
        var group = new ButtonGroup();
        Button? chosen = null;
        foreach (var c in characters)
        {
            var button = GlueTheme.Plain($"{c.Name}\nLevel {c.Level} {CharacterRules.RaceName(c.Race)} {CharacterRules.ClassName(c.Class)}{(c.IsGhost ? " · ghost" : "")}", 92);
            button.Alignment = HorizontalAlignment.Left;
            button.ToggleMode = true;
            button.ButtonGroup = group;
            button.Pressed += () => Select(c);
            _characterList.AddChild(button);
            if (chosen is null || c.Name.Equals(select, StringComparison.OrdinalIgnoreCase))
            {
                chosen = button;
                _selected = c;
            }
        }

        if (characters.Count == 0)
        {
            _selected = null;
            _characterList.AddChild(GlueTheme.Note("No characters on this realm yet."));
        }

        _enter.Disabled = _delete.Disabled = _selected is null;
        _create.Disabled = characters.Count >= RealmSession.MaxCharacters;
        if (chosen is not null)
        {
            chosen.ButtonPressed = true;
            Select(_selected!);
        }
    }

    private void Select(CharacterInfo c)
    {
        _selected = c;
        _deleteArmed = false;
        _delete.Text = "Delete";
        _status.Text = "";
        CharacterSelected?.Invoke(c);
    }

    /// <summary>A second press deletes, since a confirmation popup wouldn't open in VR.</summary>
    private void PressDelete()
    {
        if (_selected is null)
        {
            return;
        }

        if (!_deleteArmed)
        {
            _deleteArmed = true;
            _delete.Text = "Press again to delete";
            SetStatus($"Deleting {_selected.Name} can't be undone.", true);
            return;
        }

        _deleteArmed = false;
        _delete.Text = "Delete";
        DeleteRequested?.Invoke(_selected);
    }

    // ---- New character

    private Control BuildCreate()
    {
        var page = Column();
        page.AddThemeConstantOverride("separation", 10);
        page.AddChild(GlueTheme.Title("New character"));

        var sides = new HBoxContainer();
        sides.AddThemeConstantOverride("separation", 10);
        foreach (var (side, color) in new[] { ("Alliance", GlueTheme.Alliance), ("Horde", GlueTheme.Horde) })
        {
            sides.AddChild(new Label
            {
                Text = side,
                HorizontalAlignment = HorizontalAlignment.Center,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                LabelSettings = new LabelSettings { FontSize = GlueTheme.SmallSize, FontColor = color.Lightened(0.25f), OutlineSize = 3, OutlineColor = Colors.Black },
            });
        }

        page.AddChild(sides);
        var races = new GridContainer { Columns = 2 };
        races.AddThemeConstantOverride("h_separation", 10);
        races.AddThemeConstantOverride("v_separation", 8);
        var raceGroup = new ButtonGroup();

        // Alliance on the left and Horde on the right, as the game lists them.
        var alliance = CharacterRules.Races.Where(r => !r.Horde).ToArray();
        var horde = CharacterRules.Races.Where(r => r.Horde).ToArray();
        for (var i = 0; i < Math.Max(alliance.Length, horde.Length); i++)
        {
            foreach (var race in new[] { alliance.ElementAtOrDefault(i), horde.ElementAtOrDefault(i) })
            {
                if (race is null)
                {
                    races.AddChild(new Control());
                    continue;
                }

                var button = GlueTheme.Plain(race.Name, 52);
                button.ToggleMode = true;
                button.ButtonGroup = raceGroup;
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                button.Pressed += () => SetCreation(_creation with { Race = race.Id, Class = race.Classes.Contains(_creation.Class) ? _creation.Class : race.Classes[0] }, resetLook: true);
                _raceButtons[race.Id] = button;
                races.AddChild(button);
            }
        }

        page.AddChild(races);

        var classes = new GridContainer { Columns = 3 };
        classes.AddThemeConstantOverride("h_separation", 8);
        classes.AddThemeConstantOverride("v_separation", 8);
        var classGroup = new ButtonGroup();
        foreach (var cls in CharacterRules.Classes)
        {
            var button = GlueTheme.Plain(cls.Name, 48);
            button.ToggleMode = true;
            button.ButtonGroup = classGroup;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", GlueTheme.SmallSize);
            button.Pressed += () => SetCreation(_creation with { Class = cls.Id }, resetLook: cls.Id == CharacterRules.DeathKnight || _creation.Class == CharacterRules.DeathKnight);
            _classButtons[cls.Id] = button;
            classes.AddChild(button);
        }

        page.AddChild(classes);

        var sexes = new HBoxContainer();
        sexes.AddThemeConstantOverride("separation", 10);
        var sexGroup = new ButtonGroup();
        _male = GlueTheme.Plain("Male", 48);
        _female = GlueTheme.Plain("Female", 48);
        foreach (var (button, sex) in new[] { (_male, (byte)0), (_female, (byte)1) })
        {
            button.ToggleMode = true;
            button.ButtonGroup = sexGroup;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Pressed += () => SetCreation(_creation with { Sex = sex }, resetLook: true);
            sexes.AddChild(button);
        }

        page.AddChild(sexes);

        foreach (var choice in new[] { "Skin", "Face", "Hair style", "Hair color", "Facial hair" })
        {
            page.AddChild(Stepper(choice));
        }

        var randomize = GlueTheme.Plain("Randomize", 48);
        randomize.Pressed += Randomize;
        page.AddChild(randomize);

        _name = new LineEdit { PlaceholderText = "Name", MaxLength = 12, CustomMinimumSize = new Vector2(0, 60) };
        _name.TextChanged += text => _creation = _creation with { Name = text };
        _name.TextSubmitted += _ => SubmitCreate();
        page.AddChild(_name);

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 12);
        var back = GlueTheme.Plain("Back");
        back.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        back.Pressed += () => CreateCancelled?.Invoke();
        var create = GlueTheme.Primary("Create");
        create.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        create.Pressed += SubmitCreate;
        buttons.AddChild(back);
        buttons.AddChild(create);
        page.AddChild(buttons);
        return page;
    }

    private Control Stepper(string label)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var name = new Label { Text = label, SizeFlagsHorizontal = SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        var previous = GlueTheme.Plain("‹", 44);
        previous.CustomMinimumSize = new Vector2(64, 44);
        var value = new Label { CustomMinimumSize = new Vector2(80, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var next = GlueTheme.Plain("›", 44);
        next.CustomMinimumSize = new Vector2(64, 44);
        previous.Pressed += () => Step(label, -1);
        next.Pressed += () => Step(label, 1);
        _choiceLabels[label] = value;
        row.AddChild(name);
        row.AddChild(previous);
        row.AddChild(value);
        row.AddChild(next);
        return row;
    }

    private CharacterChoices Choices() =>
        _choices?.Invoke(_creation.Race, _creation.Sex, _creation.Class == CharacterRules.DeathKnight) ?? new CharacterChoices(1, 1, 1, 1, 1);

    private void Step(string label, int by)
    {
        var choices = Choices();
        static byte Wrap(int value, int count) => (byte)(((value % count) + count) % count);
        SetCreation(label switch
        {
            "Skin" => _creation with { Skin = Wrap(_creation.Skin + by, choices.Skins) },
            "Face" => _creation with { Face = Wrap(_creation.Face + by, choices.Faces) },
            "Hair style" => _creation with { HairStyle = Wrap(_creation.HairStyle + by, choices.HairStyles) },
            "Hair color" => _creation with { HairColor = Wrap(_creation.HairColor + by, choices.HairColors) },
            _ => _creation with { FacialHair = Wrap(_creation.FacialHair + by, choices.FacialHairs) },
        });
    }

    private void Randomize()
    {
        var choices = Choices();
        var random = Random.Shared;
        SetCreation(_creation with
        {
            Skin = (byte)random.Next(choices.Skins),
            Face = (byte)random.Next(choices.Faces),
            HairStyle = (byte)random.Next(choices.HairStyles),
            HairColor = (byte)random.Next(choices.HairColors),
            FacialHair = (byte)random.Next(choices.FacialHairs),
        });
    }

    private void OpenCreate() => CreatePageOpened?.Invoke();

    /// <summary>Opens the page with a random race and look. <paramref name="choices"/> says how many of each a race and sex have.</summary>
    public void ShowCreate(Func<byte, byte, bool, CharacterChoices> choices)
    {
        _choices = choices;
        Show("create");
        var race = CharacterRules.Races[Random.Shared.Next(CharacterRules.Races.Length)];
        _name.Text = "";
        SetCreation(new CharacterCreation("", race.Id, race.Classes[0], (byte)Random.Shared.Next(2), 0, 0, 0, 0, 0), resetLook: true);
        Randomize();
    }

    private void SetCreation(CharacterCreation creation, bool resetLook = false)
    {
        if (resetLook)
        {
            creation = creation with { Skin = 0, Face = 0, HairStyle = 0, HairColor = 0, FacialHair = 0 };
        }

        _creation = creation with { Name = _name.Text };
        var race = CharacterRules.Race(_creation.Race)!;
        foreach (var (id, button) in _raceButtons)
        {
            button.SetPressedNoSignal(id == _creation.Race);
        }

        foreach (var (id, button) in _classButtons)
        {
            button.Disabled = !race.Classes.Contains(id);
            button.SetPressedNoSignal(id == _creation.Class);
        }

        _male.SetPressedNoSignal(_creation.Sex == 0);
        _female.SetPressedNoSignal(_creation.Sex == 1);
        var choices = Choices();
        _choiceLabels["Skin"].Text = $"{_creation.Skin + 1}/{choices.Skins}";
        _choiceLabels["Face"].Text = $"{_creation.Face + 1}/{choices.Faces}";
        _choiceLabels["Hair style"].Text = $"{_creation.HairStyle + 1}/{choices.HairStyles}";
        _choiceLabels["Hair color"].Text = $"{_creation.HairColor + 1}/{choices.HairColors}";
        _choiceLabels["Facial hair"].Text = $"{_creation.FacialHair + 1}/{choices.FacialHairs}";
        CreationChanged?.Invoke(_creation);
    }

    private void SubmitCreate()
    {
        var name = CharacterRules.Capitalized(_name.Text.Trim());
        if (CharacterRules.NameProblem(name) is { } problem)
        {
            SetStatus(problem, true);
            return;
        }

        CreateRequested?.Invoke(_creation with { Name = name });
    }

    // ---- For --login-check, which presses what a player would.

    public void FillLogin(string host, string account, string password)
    {
        (_host.Text, _account.Text, _password.Text) = (host, account, password);
        SubmitLogin();
    }

    public bool ChooseCharacter(string name)
    {
        foreach (var child in _characterList.GetChildren())
        {
            if (child is Button button && button.Text.StartsWith(name + '\n', StringComparison.OrdinalIgnoreCase))
            {
                button.ButtonPressed = true;
                button.EmitSignal(BaseButton.SignalName.Pressed);
                return true;
            }
        }

        return false;
    }

    public void PressEnter() => _enter.EmitSignal(BaseButton.SignalName.Pressed);

    public void OpenCreatePage() => _create.EmitSignal(BaseButton.SignalName.Pressed);

    public void LeaveCreatePage() => CreateCancelled?.Invoke();

    public string Status => _status.Text;

    // ---- Helpers

    private static VBoxContainer Column()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        return column;
    }

    private static Control Spacer(float height) => new() { CustomMinimumSize = new Vector2(0, height) };

    private static LineEdit Field(Container parent, string label, string placeholder)
    {
        parent.AddChild(new Label { Text = label, LabelSettings = new LabelSettings { FontSize = GlueTheme.SmallSize, FontColor = GlueTheme.Muted } });
        var field = new LineEdit { PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 64) };
        parent.AddChild(field);
        return field;
    }

    private static ScrollContainer Scroll(Control content)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(content);
        return scroll;
    }

    private static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            child.QueueFree();
        }
    }
}
