using System;
using System.Collections.Generic;
using System.Linq;
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
    private VBoxContainer _vrPage = null!;
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
        _vrPage = Page();
        _vrPage.Visible = false;
        _gamePage = Page();
        pages.AddChild(_gamePage);
        pages.AddChild(rows);
        pages.AddChild(_videoPage);
        pages.AddChild(_vrPage);
        BuildGamePage(_gamePage);
        BuildVideoPage(_videoPage);
        BuildVrPage(_vrPage);
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
        var vr = Big("VR alignment");
        vr.Pressed += () => ShowPage(_vrPage);
        page.AddChild(vr);

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

    /// <summary>The VR alignment page: the weapon's grip and weight, and the laser's aim.</summary>
    public void ShowVrPage() => ShowPage(_vrPage);

    private void ShowPage(VBoxContainer page)
    {
        _gamePage.Visible = page == _gamePage;
        _mainPage.Visible = page == _mainPage;
        _videoPage.Visible = page == _videoPage;
        _vrPage.Visible = page == _vrPage;
    }

    /// <summary>
    /// How a held weapon sits in the hand and where the laser points, for headsets and controllers
    /// whose poses differ: three turns each, with the weapon's weight, set by eye in the headset.
    /// </summary>
    private void BuildVrPage(VBoxContainer rows)
    {
        // The hands first: the weapon and the laser hang on them.
        rows.AddChild(Heading("Hands"));
        var hands = new HBoxContainer();
        var calibrate = new Button { Text = "Calibrate", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        calibrate.Pressed += () => (Player as Xr.XrPlayer)?.StartHandCalibration();
        hands.AddChild(calibrate);
        var resetHands = new Button { Text = "Reset the hands", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        resetHands.Pressed += Xr.XrPlayer.ResetHandCalibration;
        hands.AddChild(resetHands);
        rows.AddChild(hands);

        // Each hand's weight, for everything in it: the body's hand and what it holds, the laser on
        // the right, this menu and the HUD on the left. It doesn't move where the laser points.
        rows.AddChild(SliderRow("Left weight", 0, 1, 0.05, () => AppSettings.LeftHandWeight, v => AppSettings.LeftHandWeight = v, v => $"{v:F2}", []));
        rows.AddChild(SliderRow("Right weight", 0, 1, 0.05, () => AppSettings.RightHandWeight, v => AppSettings.RightHandWeight = v, v => $"{v:F2}", []));
        rows.AddChild(new HSeparator());

        rows.AddChild(Heading("Weapon in the hand"));
        var grip = new List<HSlider>();
        rows.AddChild(SliderRow("Tip forward", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.GripPitch, v => AppSettings.GripPitch = v, Degrees, grip));
        rows.AddChild(SliderRow("Roll inward", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.GripRoll, v => AppSettings.GripRoll = v, Degrees, grip));
        rows.AddChild(SliderRow("Turn inward", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.GripYaw, v => AppSettings.GripYaw = v, Degrees, grip));
        var resetGrip = new Button { Text = "Reset the weapon" };
        resetGrip.Pressed += () => Reset(grip, AppSettings.DefaultGripPitch, AppSettings.DefaultGripRoll, 0f);
        rows.AddChild(resetGrip);

        rows.AddChild(new HSeparator());
        rows.AddChild(Heading("Laser"));
        var laser = new List<HSlider>();

        // The laser drags these, so a second laser previews the new aim while one is being set,
        // and the real one takes it a moment after the slider is let go.
        var laserPending = new List<Func<bool>>();
        void PreviewLaser()
        {
            if (Player is Xr.XrPlayer xr && laser.Count == 3)
            {
                xr.PreviewLaser(laserPending.Any(p => p())
                    ? new Vector3((float)laser[0].Value, (float)laser[1].Value, (float)laser[2].Value)
                    : null);
            }
        }

        var deferred = new Deferred(LaserApplyDelay, laserPending, PreviewLaser);
        rows.AddChild(SliderRow("Up", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.PointerPitch, v => AppSettings.PointerPitch = v, Degrees, laser, deferred));
        rows.AddChild(SliderRow("Right", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.PointerYaw, v => AppSettings.PointerYaw = v, Degrees, laser, deferred));
        rows.AddChild(SliderRow("Roll", -AppSettings.MaxAlignment, AppSettings.MaxAlignment, 1, () => AppSettings.PointerRoll, v => AppSettings.PointerRoll = v, Degrees, laser, deferred));
        var resetLaser = new Button { Text = "Reset the laser" };
        resetLaser.Pressed += () => Reset(laser, 0f, 0f, 0f);
        rows.AddChild(resetLaser);

        var bottom = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkEnd | SizeFlags.Expand };
        var back = new Button { Text = "Game menu", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        back.Pressed += ShowGamePage;
        var close = new Button { Text = "Close", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        close.Pressed += () => CloseRequested?.Invoke();
        bottom.AddChild(back);
        bottom.AddChild(close);
        rows.AddChild(bottom);

        static string Degrees(float v) => $"{v:+0;-0;0}°";

        // Setting a slider's value saves it through its own handler, and its label follows.
        static void Reset(List<HSlider> sliders, params float[] values)
        {
            for (var i = 0; i < sliders.Count && i < values.Length; i++)
            {
                sliders[i].Value = values[i];
            }
        }
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        LabelSettings = new LabelSettings { FontSize = 22, FontColor = GlueTheme.Accent, OutlineSize = 3, OutlineColor = Colors.Black },
    };

    /// <summary>
    /// How long after letting go of a laser slider the real laser takes its value, in seconds: long
    /// enough for the trigger's release to land on the slider, aimed as it was.
    /// </summary>
    private const double LaserApplyDelay = 0.5;

    /// <summary>A slider whose value waits: whether it has a change not yet kept, and a call for each change of that.</summary>
    private sealed record Deferred(double After, List<Func<bool>> Pending, Action Changed);

    /// <summary>
    /// A label with the value, then a slider, on one row, since the wrist menu is short of room.
    /// <paramref name="deferred"/> keeps the value only a moment after the slider is let go, as for
    /// the laser, which can't drag its own slider while it turns; until then it's pending.
    /// </summary>
    private static HBoxContainer SliderRow(string name, double min, double max, double step, Func<float> read, Action<float> write, Func<float, string> show, List<HSlider> group, Deferred? deferred = null)
    {
        var row = new HBoxContainer();
        var label = new Label { Text = $"{name} {show(read())}", CustomMinimumSize = new Vector2(170, 0) };
        row.AddChild(label);
        if (deferred is { } wait)
        {
            var slider = NewSlider(name, min, max, step, read());
            var timer = new Timer { OneShot = true, WaitTime = wait.After };
            row.AddChild(timer);
            var dragging = false;
            var pending = read();
            bool Pending() => dragging || !timer.IsStopped();
            wait.Pending.Add(Pending);

            void Update()
            {
                label.Text = $"{name} {show(pending)}{(Pending() ? " (preview)" : "")}";
                wait.Changed();
            }

            slider.ValueChanged += value =>
            {
                pending = (float)value;
                if (!dragging)
                {
                    timer.Start();
                }

                Update();
            };
            slider.DragStarted += () =>
            {
                dragging = true;
                timer.Stop();
                Update();
            };
            slider.DragEnded += _ =>
            {
                dragging = false;
                timer.Start();
                Update();
            };
            timer.Timeout += () =>
            {
                write(pending);
                Update();
            };

            row.AddChild(slider);
            group.Add(slider);
            return row;
        }

        var immediate = NewSlider(name, min, max, step, read());
        immediate.ValueChanged += value =>
        {
            write((float)value);
            label.Text = $"{name} {show((float)value)}";
        };
        row.AddChild(immediate);
        group.Add(immediate);
        return row;
    }

    private static HSlider NewSlider(string name, double min, double max, double step, float value) => new()
    {
        Name = name.Replace(" ", ""),
        MinValue = min,
        MaxValue = max,
        Step = step,
        Value = value,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
        SizeFlagsVertical = SizeFlags.ShrinkCenter,
    };

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

        // Dev: the pointer tags the textures it paints over, saving them to the folder for upscaling.
        frames.AddChild(VideoToggle("Texture brush (dev)", () => AppSettings.TextureBrush, on =>
        {
            AppSettings.TextureBrush = on;
            GD.Print($"Texture brush {(on ? "on" : "off")}: {Converters.TextureTags.TaggedCount} tagged, in {Converters.TextureTags.Folder}");
        }));
        rows.AddChild(frames);

        // Dev: tags every texture in the camera's view at once, as F5 does with the brush on.
        var tagView = new Button { Text = "Tag everything in view (dev)" };
        tagView.Pressed += World.TextureBrush.RequestTagView;
        rows.AddChild(tagView);

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
