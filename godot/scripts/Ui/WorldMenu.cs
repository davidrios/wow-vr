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
    private CheckButton _fly = null!;
    private HSlider _time = null!;
    private CheckButton _timePasses = null!;
    private SkyController? _sky;

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
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        margin.AddChild(rows);

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
        smoothTurn.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // Shares a row, because the wrist menu has no room for another.
        var shadows = new CheckButton { Text = "Shadows", ButtonPressed = AppSettings.Shadows, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        shadows.Toggled += on => AppSettings.Shadows = on;
        var graphics = new HBoxContainer();
        graphics.AddChild(smoothTurn);
        graphics.AddChild(shadows);
        var groundCover = new CheckButton { Text = "Grass", ButtonPressed = AppSettings.GroundCover, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        groundCover.Toggled += on => AppSettings.GroundCover = on;
        graphics.AddChild(groundCover);
        rows.AddChild(graphics);

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

        rows.AddChild(new Label { Text = "Time of day" });
        _time = new HSlider { MinValue = 0, MaxValue = LightTable.TicksPerDay - 1, Step = 10 };
        _time.ValueChanged += value =>
        {
            if (_sky is not null)
            {
                _sky.Ticks = value;
                _sky.Refresh();
            }
        };
        rows.AddChild(_time);

        _timePasses = new CheckButton { Text = "Time passes", ButtonPressed = true };
        _timePasses.Toggled += on =>
        {
            if (_sky is not null)
            {
                _sky.TimePasses = on;
            }
        };
        rows.AddChild(_timePasses);

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
