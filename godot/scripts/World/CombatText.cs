using System;
using System.Collections.Generic;
using Godot;

namespace WowVR.World;

/// <summary>
/// Floating combat text, as the game's: what the player does shows over its target, white for
/// melee and yellow for spells, and what's done to the player shows over them in red; heals are
/// green with a plus, misses say why, and criticals start large and settle. Each rises and fades
/// out. Labels are kept and reused.
/// </summary>
public partial class CombatText : Node3D
{
    private const float RiseYards = 1.6f;
    private const double LifeSeconds = 1.5;
    private const double FadeSeconds = 0.5;
    private const double PopSeconds = 0.15;
    private const float PopScale = 1.7f;
    private const int FontSize = 64;
    private const int MaxShown = 40;

    public static readonly Color Melee = Colors.White;
    public static readonly Color Spell = new(1f, 1f, 0.2f);
    public static readonly Color Incoming = new(1f, 0.15f, 0.15f);
    public static readonly Color Heal = new(0.2f, 1f, 0.2f);
    public static readonly Color Miss = new(0.9f, 0.9f, 0.9f);

    private sealed class Floater
    {
        public required Label3D Label { get; init; }
        public Vector3 From { get; set; }
        public double Age { get; set; }
        public bool Critical { get; set; }
        public Color Color { get; set; }
    }

    private readonly List<Floater> _shown = [];

    /// <summary>How many have been shown, for checks.</summary>
    public int ShownTotal { get; private set; }
    private readonly Stack<Label3D> _spare = [];

    /// <summary>How big the text is, as a share of the screen's height per font pixel: Label3D's fixed-size pixel size.</summary>
    public float PixelSize { get; set; } = 0.0009f;

    /// <summary>Shows text rising from a point in the scene, as over a unit's head.</summary>
    public void Show(Vector3 at, string text, Color color, bool critical = false)
    {
        ShownTotal++;
        if (_shown.Count >= MaxShown)
        {
            Recycle(0);
        }

        var label = _spare.Count > 0 ? _spare.Pop() : NewLabel();
        label.Text = text;
        label.Modulate = color;
        label.Transparency = 0;
        label.Scale = Vector3.One;
        label.Visible = true;

        // A little to one side, so several at once don't sit on each other.
        var jitter = new Vector3((float)(Random.Shared.NextDouble() - 0.5) * 0.8f, (float)Random.Shared.NextDouble() * 0.3f, (float)(Random.Shared.NextDouble() - 0.5) * 0.8f);
        var floater = new Floater { Label = label, From = at + jitter, Critical = critical, Color = color };
        _shown.Add(floater);
        Place(floater);
    }

    private Label3D NewLabel()
    {
        var label = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            NoDepthTest = true,
            Shaded = false,
            DoubleSided = true,
            FontSize = FontSize,
            PixelSize = PixelSize,
            OutlineSize = 12,
            OutlineModulate = Colors.Black,
            RenderPriority = 10,
            OutlineRenderPriority = 9,
        };
        AddChild(label);
        return label;
    }

    public override void _Process(double delta)
    {
        for (var i = _shown.Count - 1; i >= 0; i--)
        {
            var floater = _shown[i];
            floater.Age += delta;
            if (floater.Age >= LifeSeconds)
            {
                Recycle(i);
                continue;
            }

            Place(floater);
        }
    }

    private void Place(Floater floater)
    {
        var t = (float)(floater.Age / LifeSeconds);
        floater.Label.GlobalPosition = floater.From + (Vector3.Up * RiseYards * t);
        // Setting a Label3D's colors or pixel size rebuilds its mesh; its scale and transparency don't.
        if (floater.Critical)
        {
            var pop = floater.Age < PopSeconds ? Mathf.Lerp(PopScale, 1.25f, (float)(floater.Age / PopSeconds)) : 1.25f;
            floater.Label.Scale = Vector3.One * pop;
        }

        var alpha = (float)Math.Clamp((LifeSeconds - floater.Age) / FadeSeconds, 0, 1);
        if (alpha < 1)
        {
            floater.Label.Transparency = 1 - alpha;
        }
    }

    private void Recycle(int index)
    {
        var label = _shown[index].Label;
        _shown.RemoveAt(index);
        label.Visible = false;
        _spare.Push(label);
    }
}
