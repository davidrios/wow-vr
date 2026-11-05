using System;
using System.Collections.Generic;
using WowVR.Formats.World;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The game menu, then landmarks and settings behind its Options. Desktop shows it on screen and
/// VR on the wrist, so it talks to the player and world without knowing which. What the game menu
/// offers, such as going back to the character list, comes from whoever started the world.
/// </summary>
public partial class WorldMenu : PanelContainer
{
    public const string VignetteSliderName = "VignetteSize";

    private GridContainer _landmarks = null!;
    private HBoxContainer _continents = null!;
    private CheckButton _fly = null!;
    private HSlider _time = null!;
    private CheckButton _timePasses = null!;
    private SkyController? _sky;
    private VBoxContainer _gamePage = null!;
    private VBoxContainer _gameActions = null!;
    private Label _gameStatus = null!;
    private VBoxContainer _mainPage = null!;
    private VBoxContainer _videoPage = null!;
    private readonly List<Action> _refreshVideo = [];

    public IPlayer? Player { get; set; }

    public event Action? CloseRequested;

    public override void _Ready()
    {
        Theme = GlueTheme.Compact;
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride($"margin_{side}", 12);
        }

        AddChild(margin);
        // Only one shows, and a container gives the hidden one no room.
        var pages = new VBoxContainer();
        margin.AddChild(pages);
        var rows = Page();
        _mainPage = rows;
        _mainPage.Visible = false;
        _videoPage = Page();
        _videoPage.Visible = false;
        _gamePage = Page();
        pages.AddChild(_gamePage);
        pages.AddChild(rows);
        pages.AddChild(_videoPage);
        BuildGamePage(_gamePage);
        BuildVideoPage(_videoPage);
        AppSettings.Changed += RefreshVideoPage;

        _continents = new HBoxContainer();
        rows.AddChild(_continents);

        rows.AddChild(new Label { Text = "Teleport" });
        _landmarks = new GridContainer { Columns = 2, SizeFlagsVertical = SizeFlags.ExpandFill };
        rows.AddChild(_landmarks);

        rows.AddChild(new HSeparator());

        _fly = new CheckButton { Text = "Fly" };
        _fly.Toggled += on =>
        {
            if (Player is not null)
            {
                Player.Flying = on;
            }
        };

        // Shares a row, because the wrist menu has no room for another.
        var namePlates = new CheckButton
        {
            Text = "Creature names",
            ButtonPressed = AppSettings.NamePlates,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        namePlates.Toggled += on => AppSettings.NamePlates = on;
        _fly.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var toggles = new HBoxContainer();
        toggles.AddChild(_fly);
        toggles.AddChild(namePlates);
        var video = new Button { Text = "Video settings", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        video.Pressed += () => ShowVideoPage(true);
        toggles.AddChild(video);
        rows.AddChild(toggles);

        rows.AddChild(new Label { Text = "Fly speed" });
        var speed = new HSlider
        {
            MinValue = AppSettings.MinFlySpeed,
            MaxValue = AppSettings.MaxFlySpeed,
            Step = 5,
            Value = AppSettings.FlySpeed,
        };
        speed.ValueChanged += value => AppSettings.FlySpeed = (float)value;
        rows.AddChild(speed);

        rows.AddChild(new Label { Text = "Scale" });

        // Buttons rather than an OptionButton, whose popup doesn't open on the wrist menu.
        var scale = new HBoxContainer();
        var scaleGroup = new ButtonGroup();
        scale.AddChild(ScaleButton("Player-sized", PlayerScale.PlayerSized, scaleGroup));
        scale.AddChild(ScaleButton("True scale", PlayerScale.TrueScale, scaleGroup));
        rows.AddChild(scale);

        var smoothTurn = new CheckButton { Text = "Smooth turning (VR)", ButtonPressed = AppSettings.SmoothTurn };
        smoothTurn.Toggled += on => AppSettings.SmoothTurn = on;
        rows.AddChild(smoothTurn);

        rows.AddChild(new Label { Text = "Comfort vignette size, left is off (VR)" });
        var vignette = new HSlider
        {
            Name = VignetteSliderName,
            MinValue = 0,
            MaxValue = 1,
            Step = 0.05,
            Value = AppSettings.VignetteSize,
        };
        vignette.ValueChanged += value => AppSettings.VignetteSize = (float)value;
        rows.AddChild(vignette);

        rows.AddChild(new HSeparator());

        // The label shares its row, because the wrist menu has no room for another.
        var timeRow = new HBoxContainer();
        timeRow.AddChild(new Label { Text = "Time of day", SizeFlagsHorizontal = SizeFlags.ExpandFill });
        rows.AddChild(timeRow);
        _time = new HSlider { MinValue = 0, MaxValue = LightTable.TicksPerDay - 1, Step = 10 };
        _time.ValueChanged += value =>
        {
            if (_sky is not null)
            {
                _sky.Ticks = value;
                _sky.Refresh();
            }
        };

        _timePasses = new CheckButton { Text = "Time passes", ButtonPressed = true };
        _timePasses.Toggled += on =>
        {
            if (_sky is not null)
            {
                _sky.TimePasses = on;
            }
        };
        timeRow.AddChild(_timePasses);
        rows.AddChild(_time);

        rows.AddChild(new Label { Text = "Music volume" });
        var music = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = AppSettings.MusicVolume };
        music.ValueChanged += value => AppSettings.MusicVolume = (float)value;
        rows.AddChild(music);

        rows.AddChild(new Label { Text = "Ambient and creature sound volume" });
        var ambience = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = AppSettings.AmbienceVolume };
        ambience.ValueChanged += value => AppSettings.AmbienceVolume = (float)value;
        rows.AddChild(ambience);

        var bottom = new HBoxContainer();
        var back = new Button { Text = "Game menu", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        back.Pressed += ShowGamePage;
        var close = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        close.Pressed += () => CloseRequested?.Invoke();
        bottom.AddChild(back);
        bottom.AddChild(close);
        rows.AddChild(bottom);
    }

    /// <summary>As the game's own Esc menu: back to the game, the actions the world was given, options and video.</summary>
    private void BuildGamePage(VBoxContainer page)
    {
        page.AddThemeConstantOverride("separation", 12);
        page.AddChild(new Label
        {
            Text = "Game Menu",
            HorizontalAlignment = HorizontalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 30, FontColor = GlueTheme.Text, OutlineSize = 4, OutlineColor = Colors.Black },
        });

        var resume = Big("Return to game");
        resume.Pressed += () => CloseRequested?.Invoke();
        page.AddChild(resume);

        _gameActions = new VBoxContainer();
        _gameActions.AddThemeConstantOverride("separation", 12);
        page.AddChild(_gameActions);

        var options = Big("Options");
        options.Pressed += () => ShowPage(_mainPage);
        page.AddChild(options);
        var video = Big("Video");
        video.Pressed += () => ShowPage(_videoPage);
        page.AddChild(video);

        _gameStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Center,
            LabelSettings = new LabelSettings { FontSize = 17, FontColor = GlueTheme.Muted },
        };
        page.AddChild(_gameStatus);
    }

    private static Button Big(string text) => new() { Text = text, CustomMinimumSize = new Vector2(0, 56) };

    /// <summary>The game menu's own actions, between Return to game and Options, in order.</summary>
    public void SetGameActions(IEnumerable<(string Text, Action Pressed)> actions)
    {
        foreach (var child in _gameActions.GetChildren())
        {
            child.QueueFree();
        }

        foreach (var (text, pressed) in actions)
        {
            var button = Big(text);
            button.Pressed += pressed;
            _gameActions.AddChild(button);
        }
    }

    /// <summary>A line under the game menu's buttons, such as a logout's countdown or why it was refused.</summary>
    public void SetGameStatus(string text, bool error = false)
    {
        _gameStatus.Text = text;
        _gameStatus.LabelSettings.FontColor = error ? GlueTheme.Error : GlueTheme.Muted;
    }

    /// <summary>Opening the menu starts at the game menu.</summary>
    public void ShowGamePage() => ShowPage(_gamePage);

    private void ShowPage(VBoxContainer page)
    {
        _gamePage.Visible = page == _gamePage;
        _mainPage.Visible = page == _mainPage;
        _videoPage.Visible = page == _videoPage;
    }

    public override void _ExitTree() => AppSettings.Changed -= RefreshVideoPage;

    private static VBoxContainer Page()
    {
        var page = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 8);
        return page;
    }

    /// <summary>The video page, or the options with landmarks and settings.</summary>
    public void ShowVideoPage(bool show) => ShowPage(show ? _videoPage : _mainPage);

    /// <summary>
    /// What costs frame time, cheapest to lose first. The numbers are from <c>--bench</c> on a fast
    /// PC; on a slower GPU, render scale and occlusion matter more.
    /// </summary>
    private void BuildVideoPage(VBoxContainer rows)
    {
        rows.AddChild(new Label { Text = "Video settings. Presets set everything below." });

        // Buttons rather than an OptionButton, whose popup doesn't open on the wrist menu.
        var presets = new HBoxContainer();
        foreach (var preset in new[] { VideoPreset.Low, VideoPreset.Medium, VideoPreset.High })
        {
            var button = new Button { Text = preset.ToString(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.Pressed += () => AppSettings.ApplyPreset(preset);
            presets.AddChild(button);
        }

        rows.AddChild(presets);

        var scaleAndDetail = new HBoxContainer();
        scaleAndDetail.AddChild(VideoSlider("Render scale", AppSettings.MinRenderScale, 1f, 0.05, "0%", () => AppSettings.RenderScale, v => AppSettings.RenderScale = v));
        scaleAndDetail.AddChild(VideoSlider("Detail distance", AppSettings.MinDetailDistance, 1f, 0.05, "0%", () => AppSettings.DetailDistance, v => AppSettings.DetailDistance = v));
        rows.AddChild(scaleAndDetail);

        var creatures = new HBoxContainer();
        creatures.AddChild(VideoSlider("Creature distance", AppSettings.MinCreatureRange, AppSettings.MaxCreatureRange, 10, "0' yd'", () => AppSettings.CreatureRange, v => AppSettings.CreatureRange = v));
        creatures.AddChild(VideoSlider("Desktop FOV", AppSettings.MinFieldOfView, AppSettings.MaxFieldOfView, 1, "0", () => AppSettings.FieldOfView, v => AppSettings.FieldOfView = v));
        rows.AddChild(creatures);

        var effects = new HBoxContainer();
        effects.AddChild(VideoToggle("Shadows", () => AppSettings.Shadows, on => AppSettings.Shadows = on));
        effects.AddChild(VideoToggle("Grass", () => AppSettings.GroundCover, on => AppSettings.GroundCover = on));
        effects.AddChild(VideoToggle("Particles", () => AppSettings.Particles, on => AppSettings.Particles = on));
        rows.AddChild(effects);

        var toggles = new HBoxContainer();
        toggles.AddChild(VideoToggle("Ambient occlusion", () => AppSettings.AmbientOcclusion, on => AppSettings.AmbientOcclusion = on));
        toggles.AddChild(VideoToggle("Full-rate far animation", () => AppSettings.FullRateAnimation, on => AppSettings.FullRateAnimation = on));
        rows.AddChild(toggles);
        var occlusionSliders = new HBoxContainer();
        occlusionSliders.AddChild(VideoSlider("Occlusion strength", 0f, AppSettings.MaxOcclusionStrength, 0.25, "0.00", () => AppSettings.OcclusionStrength, v => AppSettings.OcclusionStrength = v));
        occlusionSliders.AddChild(VideoSlider("Occlusion size", AppSettings.MinOcclusionSize, AppSettings.MaxOcclusionSize, 0.05, "0.00", () => AppSettings.OcclusionSize, v => AppSettings.OcclusionSize = v));
        rows.AddChild(occlusionSliders);

        var interfaceScale = new HBoxContainer();
        interfaceScale.AddChild(VideoSlider("Interface scale (desktop)", AppSettings.MinInterfaceScale, AppSettings.MaxInterfaceScale, 0.25, "0.00'x'", () => AppSettings.InterfaceScale, v => AppSettings.InterfaceScale = v, whenReleased: true));
        rows.AddChild(interfaceScale);

        var frames = new HBoxContainer();
        frames.AddChild(VideoToggle("Unlock FPS (desktop)", () => AppSettings.UnlockFps, on => AppSettings.UnlockFps = on));
        frames.AddChild(VideoToggle("FPS display", () => AppSettings.FpsDisplay, on => AppSettings.FpsDisplay = on));
        rows.AddChild(frames);

        var back = new Button { Text = "Back" };
        back.Pressed += () => ShowVideoPage(false);
        rows.AddChild(back);
    }

    private CheckButton VideoToggle(string text, Func<bool> read, Action<bool> write)
    {
        var toggle = new CheckButton { Text = text, ButtonPressed = read(), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        toggle.Toggled += on => write(on);
        _refreshVideo.Add(() => toggle.SetPressedNoSignal(read()));
        return toggle;
    }

    /// <summary>A slider that shows its value, and follows presets.</summary>
    /// <param name="whenReleased">Changes the setting only when the slider is let go, for one that resizes the slider itself.</param>
    private VBoxContainer VideoSlider(string name, float min, float max, double step, string format, Func<float> read, Action<float> write, bool whenReleased = false)
    {
        string Text(double v) => $"{name}: {v.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}";
        var label = new Label { Text = Text(read()) };
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = read() };
        var dragging = false;
        slider.DragStarted += () => dragging = true;
        slider.DragEnded += changed =>
        {
            dragging = false;
            if (whenReleased && changed)
            {
                write((float)slider.Value);
            }
        };
        slider.ValueChanged += v =>
        {
            label.Text = Text(v);
            if (!whenReleased || !dragging)
            {
                write((float)v);
            }
        };
        _refreshVideo.Add(() =>
        {
            slider.SetValueNoSignal(read());
            label.Text = Text(read());
        });

        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddChild(label);
        box.AddChild(slider);
        return box;
    }

    private void RefreshVideoPage()
    {
        foreach (var refresh in _refreshVideo)
        {
            refresh();
        }
    }

    private static Button ScaleButton(string text, PlayerScale value, ButtonGroup group)
    {
        var button = new Button
        {
            Text = text,
            ToggleMode = true,
            ButtonGroup = group,
            ButtonPressed = AppSettings.Scale == value,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        button.Pressed += () => AppSettings.Scale = value;
        return button;
    }

    public void SetSky(SkyController sky)
    {
        _sky = sky;
        _timePasses.SetPressedNoSignal(sky.TimePasses);
    }

    /// <summary>A button per continent. The one the player's on can't be pressed.</summary>
    public void SetContinents(string current, Action<Continent> travel)
    {
        foreach (var continent in Landmarks.Continents)
        {
            var button = new Button
            {
                Text = continent.Name,
                Disabled = continent.Map.Equals(current, StringComparison.OrdinalIgnoreCase),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            button.Pressed += () => travel(continent);
            _continents.AddChild(button);
        }
    }

    public void SetLandmarks(IEnumerable<Landmark> landmarks, Action<Landmark> teleport)
    {
        foreach (var child in _landmarks.GetChildren())
        {
            child.QueueFree();
        }

        foreach (var landmark in landmarks)
        {
            var button = new Button { Text = landmark.Name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            button.Pressed += () =>
            {
                teleport(landmark);
                CloseRequested?.Invoke();
            };
            _landmarks.AddChild(button);
        }
    }

    public override void _Process(double delta)
    {
        // The player can also change this with a key or button.
        if (Player is not null)
        {
            _fly.SetPressedNoSignal(Player.Flying);
        }

        if (_sky is not null)
        {
            _time.SetValueNoSignal(_sky.Ticks);
        }
    }
}
