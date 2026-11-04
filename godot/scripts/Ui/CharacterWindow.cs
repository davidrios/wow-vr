using System;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Formats.World;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// The character window: the player as they look, turning slowly, with what they wear around
/// them in the game's paperdoll order, and their stats below. It opens with C or its button.
/// </summary>
public partial class CharacterWindow : PanelContainer
{
    private const float SlotSize = 44;
    private static readonly Vector2I PictureSize = new(240, 330);

    /// <summary>The paperdoll's columns, by equipment slot: the left side, the right, and the weapons below.</summary>
    private static readonly int[] LeftSlots = [0, 1, 2, 14, 4, 3, 18, 8];
    private static readonly int[] RightSlots = [9, 5, 6, 7, 10, 11, 12, 13];
    private static readonly int[] WeaponSlots = [15, 16, 17];

    private static readonly string[] SlotNames =
    [
        "Head", "Neck", "Shoulder", "Shirt", "Chest", "Waist", "Legs", "Feet", "Wrist", "Hands",
        "Finger", "Finger", "Trinket", "Trinket", "Back", "Main Hand", "Off Hand", "Ranged", "Tabard",
    ];

    private static readonly string[] StatNames = ["Strength", "Agility", "Stamina", "Intellect", "Spirit"];

    private readonly ServerWorld _world;
    private readonly ActionIcon[] _slots = new ActionIcon[19];
    private Label _title = null!;
    private GridContainer _stats = null!;
    private SubViewport _picture = null!;
    private Node3D _stage = null!;
    private Camera3D _camera = null!;
    private CreatureNode? _model;
    private string? _modelKey;
    private int _modelVersion;
    private float _turn;

    public CharacterWindow(ServerWorld world)
    {
        _world = world;
    }

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 14, GlueTheme.Edge));
        Position = new Vector2(16, 110);
        var outer = new VBoxContainer();
        AddChild(outer);

        var header = new HBoxContainer();
        outer.AddChild(header);
        _title = GlueTheme.Title("");
        _title.LabelSettings.FontSize = 22;
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(_title);
        var close = GlueTheme.Plain("X", 36);
        close.CustomMinimumSize = new Vector2(40, 36);
        close.Pressed += Hide;
        header.AddChild(close);

        var doll = new HBoxContainer();
        doll.AddThemeConstantOverride("separation", 10);
        outer.AddChild(doll);
        doll.AddChild(Column(LeftSlots));
        doll.AddChild(Picture());
        doll.AddChild(Column(RightSlots));

        var weapons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        weapons.AddThemeConstantOverride("separation", 10);
        outer.AddChild(weapons);
        foreach (var slot in WeaponSlots)
        {
            weapons.AddChild(SlotIcon(slot));
        }

        _stats = new GridContainer { Columns = 4 };
        _stats.AddThemeConstantOverride("h_separation", 14);
        _stats.AddThemeConstantOverride("v_separation", 2);
        outer.AddChild(_stats);

        _world.StatsChanged += () =>
        {
            if (Visible)
            {
                RefreshStats();
            }
        };
        _world.InventoryUpdated += () =>
        {
            if (Visible)
            {
                RefreshSlots();
            }
        };
        _world.SelfLookChanged += () =>
        {
            if (Visible)
            {
                RefreshModel();
            }
        };
        VisibilityChanged += () =>
        {
            _picture.RenderTargetUpdateMode = Visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (Visible)
            {
                RefreshSlots();
                RefreshStats();
                RefreshModel();
            }
        };
    }

    private VBoxContainer Column(int[] slots)
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        foreach (var slot in slots)
        {
            column.AddChild(SlotIcon(slot));
        }

        return column;
    }

    private ActionIcon SlotIcon(int slot)
    {
        // Not flat, so an empty slot still shows its box.
        var icon = new ActionIcon(null, default, SlotSize) { TooltipText = SlotNames[slot], Flat = false };
        _slots[slot] = icon;
        return icon;
    }

    /// <summary>A little stage of its own, lit from the front, where the character stands.</summary>
    private Control Picture()
    {
        var frame = new SubViewportContainer { Stretch = true, CustomMinimumSize = PictureSize };
        _picture = new SubViewport { OwnWorld3D = true, TransparentBg = true, Size = PictureSize, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        frame.AddChild(_picture);
        _stage = new Node3D();
        _picture.AddChild(_stage);
        _camera = new Camera3D
        {
            Fov = 30,
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.52f, 0.5f),
                AmbientLightEnergy = 1f,
            },
        };
        _stage.AddChild(_camera);
        var light = new DirectionalLight3D { LightEnergy = 1.1f, RotationDegrees = new Vector3(-30, 25, 0) };
        _stage.AddChild(light);
        return frame;
    }

    public override void _Process(double delta)
    {
        if (!Visible || _model is null)
        {
            return;
        }

        // Faces the camera down +Z, turning a little either way, as the character screen does.
        _turn += (float)delta * 0.3f;
        _model.Move(Vector3.Zero, -Mathf.Pi + Mathf.Sin(_turn) * 0.6f, false, 0, 0, delta);
        _model.Animate(delta, 0);
    }

    /// <summary>Builds the character as they look now, off the main thread, when that changed.</summary>
    private void RefreshModel()
    {
        if (_world.SelfAppearance is not { } appearance || _world.Library is not { } library || appearance.Key == _modelKey)
        {
            return;
        }

        _modelKey = appearance.Key;
        var version = ++_modelVersion;
        Task.Run(() => library.BuildCharacter(appearance)).ContinueWith(built => Callable.From(() =>
        {
            if (!IsInstanceValid(this) || version != _modelVersion || built.Result is not { } asset)
            {
                return;
            }

            _model?.QueueFree();
            _model = new CreatureNode { Name = "Character" };
            _model.Build(asset, "");
            _stage.AddChild(_model);
            _model.StartIdle();

            // The whole character in view, whatever its race's height.
            var height = Mathf.Max(asset.Skeleton.Bounds.End.Y * asset.Scale, 1f);
            _camera.Position = new Vector3(0, height * 0.52f, height * 2.3f);
            _camera.LookAt(new Vector3(0, height * 0.5f, 0), Vector3.Up);
        }).CallDeferred());
    }

    private void RefreshSlots()
    {
        var worn = _world.Inventory?.Items.Where(i => i.Bag == 255 && i.Slot < 19).ToDictionary(i => (int)i.Slot) ?? [];
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            var icon = _slots[slot];
            if (worn.TryGetValue(slot, out var item))
            {
                var info = _world.Session.ItemInfoOf(item.Entry);
                icon.Icon = _world.ItemIcon(item.Entry);
                icon.Action = new ActionButton(item.Entry, ActionKind.Item);
                icon.Modulate = Colors.White;
                icon.TooltipText = $"{info?.Name ?? $"Item {item.Entry}"}\n{SlotNames[slot]}";
                icon.AddThemeStyleboxOverride("normal", GlueTheme.Box(new Color(0, 0, 0, 0.5f), 3, 1, GameHud.QualityColor(info?.Quality ?? 1)));
            }
            else
            {
                icon.Icon = null;
                icon.Action = default;
                icon.TooltipText = SlotNames[slot];
                icon.AddThemeStyleboxOverride("normal", GlueTheme.Box(new Color(0, 0, 0, 0.5f), 3, 1, GlueTheme.Edge.Darkened(0.5f)));
            }
        }
    }

    private void RefreshStats()
    {
        if (_world.Self is not { } self)
        {
            return;
        }

        var unit = self.Unit;
        _title.Text = $"{unit.Name}  Level {unit.Level} {CharacterRules.RaceName(unit.Race)} {CharacterRules.ClassName(unit.Class)}";
        foreach (var child in _stats.GetChildren())
        {
            child.QueueFree();
        }

        if (self.Sheet is not { } sheet)
        {
            return;
        }

        void Row(string name, string value, Color? color = null)
        {
            var label = new Label { Text = name };
            label.AddThemeColorOverride("font_color", GlueTheme.Accent);
            label.AddThemeFontSizeOverride("font_size", 15);
            _stats.AddChild(label);
            var number = new Label { Text = value, HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(70, 0) };
            number.AddThemeColorOverride("font_color", color ?? GlueTheme.Highlight);
            number.AddThemeFontSizeOverride("font_size", 15);
            _stats.AddChild(number);
        }

        // Green when gear and auras raise it, red when they lower it, as the game colors them.
        var green = new Color(0.1f, 1f, 0.1f);
        var red = new Color(1f, 0.25f, 0.2f);
        for (var i = 0; i < StatNames.Length; i++)
        {
            var bonus = sheet.StatBonus[i] - sheet.StatMalus[i];
            Row(StatNames[i], sheet.Stats[i].ToString(), bonus > 0 ? green : bonus < 0 ? red : null);
        }

        Row("Armor", sheet.Resistances[0].ToString());
        Row("Health", $"{unit.Health} / {unit.MaxHealth}");
        if (unit.MaxPower > 0)
        {
            var shown = unit.PowerType is PowerType.Rage or PowerType.RunicPower ? 10u : 1u;
            Row(unit.PowerType.ToString(), $"{unit.Power / shown} / {unit.MaxPower / shown}");
        }

        Row("Damage", $"{sheet.MinDamage:F0} - {sheet.MaxDamage:F0}");
        Row("Speed", $"{sheet.AttackSpeedMs / 1000f:F2}");
        Row("Attack Power", sheet.AttackPower.ToString());
        Row("Ranged Power", sheet.RangedAttackPower.ToString());
        Row("Crit", $"{sheet.Crit:F2}%");
        Row("Spell Crit", $"{sheet.SpellCrit:F2}%");
        Row("Spell Power", sheet.SpellPower.ToString());
        Row("Healing", sheet.Healing.ToString());
        Row("Dodge", $"{sheet.Dodge:F2}%");
        Row("Parry", $"{sheet.Parry:F2}%");
        Row("Block", $"{sheet.Block:F2}%");
        Row("XP", self.NextLevelXp > 0 ? $"{self.Xp} / {self.NextLevelXp}" : "-");
    }
}
