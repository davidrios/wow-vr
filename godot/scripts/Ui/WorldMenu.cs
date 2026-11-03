using System;
using System.Collections.Generic;
using WowVR.Formats.World;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// Landmarks and movement settings. Desktop shows it on screen and VR on the wrist,
/// so it talks to the player and world without knowing which.
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
    private VBoxContainer _mainPage = null!;
    private VBoxContainer _videoPage = null!;
    private readonly List<Action> _refreshVideo = [];

    public IPlayer? Player { get; set; }

    public event Action? CloseRequested;

    public override void _Ready()
    {
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
        _videoPage = Page();
        _videoPage.Visible = false;
        pages.AddChild(rows);
        pages.AddChild(_videoPage);
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

        var close = new Button { Text = "Close" };
        close.Pressed += () => CloseRequested?.Invoke();
        rows.AddChild(close);
    }

    public override void _ExitTree() => AppSettings.Changed -= RefreshVideoPage;

    private static VBoxContainer Page()
    {
        var page = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        page.AddThemeConstantOverride("separation", 8);
        return page;
    }

    public void ShowVideoPage(bool show)
    {
        _mainPage.Visible = !show;
        _videoPage.Visible = show;
    }

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
        creatures.AddChild(VideoSlider("Animated creatures", AppSettings.MinAnimatedCreatures, AppSettings.MaxAnimatedCreatures, 5, "0", () => AppSettings.AnimatedCreatures, v => AppSettings.AnimatedCreatures = (int)v));
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
    private VBoxContainer VideoSlider(string name, float min, float max, double step, string format, Func<float> read, Action<float> write)
    {
        string Text(double v) => $"{name}: {v.ToString(format, System.Globalization.CultureInfo.InvariantCulture)}";
        var label = new Label { Text = Text(read()) };
        var slider = new HSlider { MinValue = min, MaxValue = max, Step = step, Value = read() };
        slider.ValueChanged += v =>
        {
            label.Text = Text(v);
            write((float)v);
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
