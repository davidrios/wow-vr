using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Net;
using WowVR.World;
using Godot;

namespace WowVR.Ui;

/// <summary>
/// What WoW shows over the world while playing on a server: the player's and the target's
/// frames, chat with a line to type in, red error text, what to do when dead, NPC windows
/// (talk, quests, vendors) and the bags. The same control fills the desktop window or a VR panel.
/// </summary>
public partial class GameHud : Control
{
    /// <summary>The size of the VR panel's canvas, which the layout is made for.</summary>
    public static readonly Vector2I PanelSize = new(1280, 800);

    private const int MaxChatLines = 200;
    private const double ScreenTextSeconds = 4;

    /// <summary>The game's own reach for taking a corpse back, in yards.</summary>
    private const float CorpseReach = 39f;

    private ServerWorld _world = null!;
    private Func<System.Numerics.Vector3> _playerPosition = null!;

    private UnitFrame _playerFrame = null!;
    private UnitFrame _targetFrame = null!;
    private RichTextLabel _chatLog = null!;
    private LineEdit _chatInput = null!;
    private Label _screenText = null!;
    private double _screenTextLeft;
    private PanelContainer _deathPanel = null!;
    private Label _deathText = null!;
    private Button _deathButton = null!;
    private PanelContainer _dialog = null!;
    private VBoxContainer _dialogBody = null!;
    private Label _dialogTitle = null!;
    private PanelContainer _bags = null!;
    private VBoxContainer _bagsBody = null!;
    private Label _money = null!;
    private HBoxContainer _bar = null!;
    private readonly List<ActionSlot> _slots = [];
    private SpellBook? _spellBook;
    private PanelContainer _castBar = null!;
    private ColorRect _castProgress = null!;
    private Label _castLabel = null!;
    private Label _heldNote = null!;
    private ActionButton? _held;
    private readonly List<string> _history = [];
    private int _historyIndex;
    private ulong _vendor;
    private SessionEvent? _shownDialog;

    /// <summary>Hides the HUD while true, as while the loading screen covers the world.</summary>
    public Func<bool>? Hidden { get; set; }

    /// <summary>The chat line has the keyboard: movement keys type instead.</summary>
    public bool Typing => _chatInput.HasFocus();

    /// <summary>A window is open that Escape should close before opening the menu.</summary>
    public bool HasOpenWindow => _dialog.Visible || _bags.Visible || _spellBook?.Visible == true || _held is not null || _world?.Aiming is not null;

    /// <summary>The keys of the main bar's twelve buttons, as the game binds them.</summary>
    public static readonly string[] SlotKeys = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="];

    /// <summary>The chat line gained or lost focus, for VR's keyboard.</summary>
    public event Action<bool>? TextFocusChanged;

    /// <summary>Pressed the menu button, which VR has no key for.</summary>
    public event Action? MenuRequested;

    public override void _Ready()
    {
        Theme = GlueTheme.Compact;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;

        _playerFrame = new UnitFrame { PlaceKey = "player_frame" };
        AddChild(_playerFrame);
        _playerFrame.Position = new Vector2(16, 16);

        _targetFrame = new UnitFrame { Visible = false, PlaceKey = "target_frame" };
        AddChild(_targetFrame);
        _targetFrame.Position = new Vector2(316, 16);

        // Once laid out, so they're kept on screen by their real size.
        Callable.From(() =>
        {
            _playerFrame.RestorePlace();
            _targetFrame.RestorePlace();
        }).CallDeferred();

        _screenText = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _screenText.AddThemeFontSizeOverride("font_size", 26);
        _screenText.AddThemeColorOverride("font_outline_color", Colors.Black);
        _screenText.AddThemeConstantOverride("outline_size", 6);
        _screenText.SetAnchorsPreset(LayoutPreset.CenterTop);
        _screenText.OffsetLeft = -400;
        _screenText.OffsetRight = 400;
        _screenText.OffsetTop = 110;
        AddChild(_screenText);

        BuildChat();
        BuildDeathPanel();
        BuildDialog();
        BuildBags();
        BuildCastBar();
    }

    public void Bind(ServerWorld world, Func<System.Numerics.Vector3> playerPosition)
    {
        _world = world;
        _playerPosition = playerPosition;
        world.ChatLine += AddChat;
        world.ScreenText += ShowScreenText;
        world.StatsChanged += RefreshFrames;
        world.TargetChanged += RefreshFrames;
        world.Dialog += ShowDialog;
        world.InventoryUpdated += () =>
        {
            if (_bags.Visible)
            {
                RefreshBags();
            }

            if (_shownDialog is VendorShown or QuestShown)
            {
                ShowDialog(_shownDialog);
            }

            InvalidateSlots();
        };
        world.SpellsChanged += InvalidateSlots;
        world.ButtonsChanged += InvalidateSlots;
        world.AimingChanged += () =>
        {
            _heldNote.Visible = world.Aiming is not null || _held is not null;
            if (world.Aiming is { } spell)
            {
                _heldNote.Text = XRServer.PrimaryInterface?.IsInitialized() == true
                    ? $"Point at the ground and pull the trigger to cast {spell.Name}. B cancels."
                    : $"Click the ground to cast {spell.Name}. Right-click or Esc cancels.";
            }
        };
        BuildActionBar();
        _spellBook = new SpellBook(world, PickUp) { Visible = false };
        AddChild(_spellBook);
        RefreshFrames();
    }

    private void InvalidateSlots()
    {
        foreach (var slot in _slots)
        {
            slot.Invalidate();
        }
    }

    /// <summary>A key of the main bar was pressed: its button's spell or item is used.</summary>
    public void PressAction(int slot) => _world?.UseAction(slot);

    public void ToggleSpellbook()
    {
        if (_spellBook is not null)
        {
            _spellBook.Visible = !_spellBook.Visible;
        }
    }

    /// <summary>Holds a spell to put on the next button clicked, for pointers that can't drag.</summary>
    private void PickUp(ActionButton action, string name)
    {
        _held = action;
        _heldNote.Text = $"Click a button to put {name} there.";
        _heldNote.Visible = true;
    }

    private void DropHeld()
    {
        _held = null;
        _heldNote.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (_screenTextLeft > 0)
        {
            _screenTextLeft -= delta;
            _screenText.Modulate = new Color(1, 1, 1, (float)Math.Clamp(_screenTextLeft, 0, 1));
            _screenText.Visible = _screenTextLeft > 0;
        }

        var hidden = Hidden?.Invoke() == true;
        if (Visible == hidden)
        {
            Visible = !hidden;
        }

        if (_world is not null && !hidden)
        {
            RefreshDeath();
            foreach (var slot in _slots)
            {
                slot.Refresh();
            }

            RefreshCastBar();
        }
    }

    /// <summary>Focuses the chat line, as Enter does, starting it with <paramref name="start"/>.</summary>
    public void OpenChat(string start = "")
    {
        _chatInput.Visible = true;
        _chatInput.Text = start;
        _chatInput.GrabFocus();
        _chatInput.CaretColumn = start.Length;
    }

    /// <summary>Escape: closes what's open, a window first, and says whether anything was.</summary>
    public bool CloseTopWindow()
    {
        if (Typing)
        {
            _chatInput.ReleaseFocus();
            return true;
        }

        if (_world?.Aiming is not null)
        {
            _world.CancelAiming();
            return true;
        }

        if (_held is not null)
        {
            DropHeld();
            return true;
        }

        if (_dialog.Visible)
        {
            CloseDialog();
            return true;
        }

        if (_spellBook?.Visible == true)
        {
            _spellBook.Visible = false;
            return true;
        }

        if (_bags.Visible)
        {
            _bags.Visible = false;
            return true;
        }

        return false;
    }

    public void ToggleBags()
    {
        _bags.Visible = !_bags.Visible;
        if (_bags.Visible)
        {
            RefreshBags();
        }
    }

    private void BuildChat()
    {
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.SetAnchorsPreset(LayoutPreset.BottomLeft);
        box.OffsetLeft = 16;
        box.OffsetRight = 576;
        box.OffsetTop = -330;
        box.OffsetBottom = -80;
        AddChild(box);

        _chatLog = new RichTextLabel
        {
            ScrollFollowing = true,
            BbcodeEnabled = true,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SelectionEnabled = false,
            MouseFilter = MouseFilterEnum.Pass,
        };
        _chatLog.AddThemeStyleboxOverride("normal", GlueTheme.Box(new Color(0, 0, 0, 0.35f), 6, 8));
        _chatLog.AddThemeFontSizeOverride("normal_font_size", 17);
        _chatLog.AddThemeColorOverride("font_outline_color", Colors.Black);
        _chatLog.AddThemeConstantOverride("outline_size", 4);
        box.AddChild(_chatLog);

        _chatInput = new LineEdit { PlaceholderText = "Say something, or /w Name, /y, .command", Visible = false };
        _chatInput.TextSubmitted += SubmitChat;
        _chatInput.FocusEntered += () => TextFocusChanged?.Invoke(true);
        _chatInput.FocusExited += () =>
        {
            TextFocusChanged?.Invoke(false);
            if (_chatInput.Text.Length == 0)
            {
                _chatInput.Visible = false;
            }
        };
        _chatInput.GuiInput += OnChatKey;
        box.AddChild(_chatInput);
    }

    private void SubmitChat(string text)
    {
        if (text.Trim().Length > 0)
        {
            _world.Say(text);
            _history.Add(text);
        }

        _historyIndex = _history.Count;
        _chatInput.Text = "";
        _chatInput.ReleaseFocus();
        _chatInput.Visible = false;
    }

    private void OnChatKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key)
        {
            return;
        }

        if (key.Keycode == Key.Escape)
        {
            _chatInput.Text = "";
            _chatInput.ReleaseFocus();
            AcceptEvent();
        }
        else if (key.Keycode is Key.Up or Key.Down && _history.Count > 0)
        {
            _historyIndex = Math.Clamp(_historyIndex + (key.Keycode == Key.Up ? -1 : 1), 0, _history.Count);
            _chatInput.Text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            _chatInput.CaretColumn = _chatInput.Text.Length;
            AcceptEvent();
        }
    }

    private void AddChat(ChatLine line)
    {
        var (color, prefix) = line.Kind switch
        {
            ChatKind.Say or ChatKind.MonsterSay => ("#ffffff", $"{line.Sender} says: "),
            ChatKind.Yell or ChatKind.MonsterYell => ("#ff4040", $"{line.Sender} yells: "),
            ChatKind.Whisper or ChatKind.MonsterWhisper or ChatKind.RaidBossWhisper => ("#ff80ff", $"{line.Sender} whispers: "),
            ChatKind.WhisperInform => ("#ff80ff", $"To {line.Sender}: "),
            ChatKind.Emote or ChatKind.MonsterEmote or ChatKind.RaidBossEmote => ("#ff8040", $"{line.Sender} "),
            ChatKind.TextEmote => ("#ff8040", ""),
            ChatKind.Party or ChatKind.MonsterParty => ("#aaaaff", $"[Party] {line.Sender}: "),
            ChatKind.Guild => ("#40ff40", $"[Guild] {line.Sender}: "),
            ChatKind.Officer => ("#40c040", $"[Officer] {line.Sender}: "),
            ChatKind.Channel => ("#ffc0c0", $"[{line.Channel}] {line.Sender}: "),
            ChatKind.Error => ("#ff3030", ""),
            ChatKind.Combat => ("#c0c0c0", ""),
            ChatKind.Notice => ("#ffff60", ""),
            _ => ("#ffff00", ""),
        };
        _chatLog.AppendText($"[color={color}]{Escape(prefix + line.Text)}[/color]\n");
        if (_chatLog.GetParagraphCount() > MaxChatLines)
        {
            _chatLog.RemoveParagraph(0);
        }
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private void ShowScreenText(string text, bool error)
    {
        _screenText.Text = text;
        _screenText.AddThemeColorOverride("font_color", error ? GlueTheme.Error : GlueTheme.Accent);
        _screenText.Visible = true;
        _screenTextLeft = ScreenTextSeconds;
    }

    private void BuildDeathPanel()
    {
        _deathPanel = new PanelContainer { Visible = false };
        _deathPanel.AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 14, GlueTheme.Edge));
        _deathPanel.SetAnchorsPreset(LayoutPreset.CenterTop);
        _deathPanel.OffsetLeft = -200;
        _deathPanel.OffsetRight = 200;
        _deathPanel.OffsetTop = 160;
        AddChild(_deathPanel);
        var box = new VBoxContainer();
        _deathPanel.AddChild(box);
        _deathText = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_deathText);
        _deathButton = GlueTheme.Primary("Release Spirit");
        _deathButton.Pressed += () =>
        {
            if (_world.Self is { IsGhost: true })
            {
                _world.Session.ReclaimCorpse();
            }
            else
            {
                _world.Session.ReleaseSpirit();
            }
        };
        box.AddChild(_deathButton);
    }

    private void RefreshDeath()
    {
        var self = _world.Self;
        var dead = self is { IsCorpse: true } || self is { IsGhost: true };
        _deathPanel.Visible = dead;
        if (!dead || self is null)
        {
            return;
        }

        if (!self.IsGhost)
        {
            _deathText.Text = "You have died.";
            _deathButton.Text = "Release Spirit";
            _deathButton.Disabled = false;
            return;
        }

        if (_world.Corpse is not { } corpse)
        {
            _deathText.Text = "You are a spirit. Find your corpse, or talk to the spirit healer.";
            _deathButton.Text = "Resurrect";
            _deathButton.Disabled = true;
            return;
        }

        var here = _playerPosition();
        var offset = corpse - here;
        var distance = new System.Numerics.Vector2(offset.X, offset.Y).Length();
        var direction = Compass(MathF.Atan2(offset.Y, offset.X));
        _deathText.Text = distance <= CorpseReach
            ? "You are near your corpse."
            : $"Your corpse is {distance:F0} yards {direction}.";
        _deathButton.Text = "Resurrect";
        _deathButton.Disabled = distance > CorpseReach;
    }

    /// <summary>A WoW angle, turning left from north, as a compass direction.</summary>
    private static string Compass(float angle)
    {
        string[] names = ["north", "north-west", "west", "south-west", "south", "south-east", "east", "north-east"];
        var index = (int)MathF.Round(angle / (MathF.PI / 4));
        return names[((index % 8) + 8) % 8];
    }

    private void RefreshFrames()
    {
        if (_world?.Self is { } self)
        {
            var unit = self.Unit;
            _playerFrame.Set(unit.Name, unit.Level, unit.Health, unit.MaxHealth, unit.Power, unit.MaxPower, unit.PowerType,
                GlueTheme.Accent, self.IsGhost ? "Ghost" : unit.IsDead ? "Dead" : _world.Attacking ? "Attacking" : "");
            _money.Text = RealmSession.Money(self.Money);
        }

        if (_world?.TargetStats is { } target)
        {
            var color = _world.ReactionTo(target) switch
            {
                Reaction.Hostile => new Color(1f, 0.25f, 0.2f),
                Reaction.Neutral => new Color(1f, 0.9f, 0.2f),
                _ => new Color(0.3f, 1f, 0.3f),
            };
            _targetFrame.Visible = true;
            _targetFrame.Set(target.Name.Length > 0 ? target.Name : "Unknown", target.Level, target.Health, target.MaxHealth, target.Power, target.MaxPower, target.PowerType,
                color, target.IsDead ? "Dead" : "");
        }
        else
        {
            _targetFrame.Visible = false;
        }
    }

    private void BuildDialog()
    {
        _dialog = new PanelContainer { Visible = false };
        _dialog.AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 14, GlueTheme.Edge));
        _dialog.Position = new Vector2(16, 110);
        _dialog.CustomMinimumSize = new Vector2(460, 0);
        AddChild(_dialog);
        var outer = new VBoxContainer();
        _dialog.AddChild(outer);
        var header = new HBoxContainer();
        outer.AddChild(header);
        _dialogTitle = GlueTheme.Title("");
        _dialogTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _dialogTitle.AddThemeFontSizeOverride("font_size", 24);
        header.AddChild(_dialogTitle);
        var close = GlueTheme.Plain("X", 36);
        close.CustomMinimumSize = new Vector2(40, 36);
        close.Pressed += CloseDialog;
        header.AddChild(close);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(440, 340), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        _dialogBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_dialogBody);
    }

    private void CloseDialog()
    {
        _dialog.Visible = false;
        _shownDialog = null;
        _vendor = 0;
    }

    private void ShowDialog(SessionEvent e)
    {
        if (e is GossipClosed)
        {
            CloseDialog();
            return;
        }

        _shownDialog = e;
        foreach (var child in _dialogBody.GetChildren())
        {
            child.QueueFree();
        }

        _vendor = 0;
        _dialog.Visible = true;
        _spellBook?.Hide();
        switch (e)
        {
            case GossipShown gossip:
                _dialogTitle.Text = NpcName(gossip.Npc);
                AddText(gossip.Text);
                foreach (var option in gossip.Options)
                {
                    AddButton(option.Text, () => _world.Session.ChooseGossip(gossip.Npc, gossip.MenuId, option.Index));
                }

                foreach (var quest in gossip.Quests)
                {
                    AddButton($"[{quest.Level}] {quest.Title}", () => _world.Session.ChooseGossipQuest(gossip.Npc, quest), GlueTheme.Accent);
                }

                break;
            case QuestShown quest:
                ShowQuest(quest);
                break;
            case VendorShown vendor:
                _vendor = vendor.Npc;
                _dialogTitle.Text = NpcName(vendor.Npc);
                AddText("Right side: your bags, to sell from.");
                foreach (var item in vendor.Items)
                {
                    var info = _world.Session.ItemInfoOf(item.ItemId);
                    var name = info?.Name ?? $"Item {item.ItemId}";
                    var count = item.BuyCount > 1 ? $" x{item.BuyCount}" : "";
                    var stock = item.Stock >= 0 ? $" ({item.Stock} left)" : "";
                    AddButton($"{name}{count} — {RealmSession.Money(item.Price)}{stock}", () => _world.Session.Buy(vendor.Npc, item), QualityColor(info?.Quality ?? 1), _world.ItemIcon(item.ItemId));
                }

                _bags.Visible = true;
                RefreshBags();
                break;
            case SpiritHealerOffer healer:
                _dialogTitle.Text = "Spirit Healer";
                AddText("Return to life now? You will be weakened by resurrection sickness.");
                AddButton("Accept", () =>
                {
                    _world.Session.AcceptSpiritHealer(healer.Npc);
                    CloseDialog();
                });
                break;
            case ResurrectOffer offer:
                _dialogTitle.Text = "Resurrection";
                AddText($"{(offer.Name.Length > 0 ? offer.Name : "Someone")} wants to resurrect you.");
                AddButton("Accept", () =>
                {
                    _world.Session.AnswerResurrect(offer.From, true);
                    CloseDialog();
                });
                AddButton("Decline", () =>
                {
                    _world.Session.AnswerResurrect(offer.From, false);
                    CloseDialog();
                });
                break;
        }
    }

    private void ShowQuest(QuestShown quest)
    {
        _dialogTitle.Text = quest.Title;
        AddText(quest.Text);
        if (quest.Objectives.Length > 0)
        {
            AddText("Objectives", GlueTheme.Accent);
            AddText(quest.Objectives);
        }

        switch (quest.Step)
        {
            case QuestStep.Details:
                AddItems("You will receive:", quest.Rewards);
                AddItems("Choose one of:", quest.Choices);
                AddButton("Accept", () =>
                {
                    _world.Session.AcceptQuest(quest.Npc, quest.QuestId);
                    CloseDialog();
                }, GlueTheme.Accent);
                AddButton("Decline", CloseDialog);
                break;
            case QuestStep.RequestItems:
                AddItems("Required items:", quest.Rewards);
                if (quest.Completable)
                {
                    AddButton("Continue", () => _world.Session.RequestQuestReward(quest.Npc, quest.QuestId), GlueTheme.Accent);
                }
                else
                {
                    AddText("You haven't finished this yet.", GlueTheme.Muted);
                }

                AddButton("Goodbye", CloseDialog);
                break;
            case QuestStep.OfferReward:
                AddItems("You will also receive:", quest.Rewards);
                if (quest.Choices.Count == 0)
                {
                    AddButton("Complete quest", () => _world.Session.ChooseQuestReward(quest.Npc, quest.QuestId, 0), GlueTheme.Accent);
                }
                else
                {
                    AddText("Choose your reward:", GlueTheme.Accent);
                    for (var i = 0; i < quest.Choices.Count; i++)
                    {
                        var choice = (uint)i;
                        var item = quest.Choices[i];
                        AddButton(ItemLabel(item.ItemId, item.Count), () => _world.Session.ChooseQuestReward(quest.Npc, quest.QuestId, choice),
                            QualityColor(_world.Session.ItemInfoOf(item.ItemId)?.Quality ?? 1), _world.ItemIcon(item.ItemId));
                    }
                }

                break;
        }
    }

    private void AddItems(string title, IReadOnlyList<QuestRewardItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        AddText(title, GlueTheme.Accent);
        foreach (var item in items)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            _dialogBody.AddChild(row);
            row.AddChild(new TextureRect
            {
                Texture = _world.ItemIcon(item.ItemId),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                CustomMinimumSize = new Vector2(32, 32),
            });
            var label = new Label { Text = ItemLabel(item.ItemId, item.Count), VerticalAlignment = VerticalAlignment.Center };
            label.AddThemeColorOverride("font_color", QualityColor(_world.Session.ItemInfoOf(item.ItemId)?.Quality ?? 1));
            row.AddChild(label);
        }
    }

    private string ItemLabel(uint entry, uint count) =>
        $"{_world.Session.ItemInfoOf(entry)?.Name ?? $"Item {entry}"}{(count > 1 ? $" x{count}" : "")}";

    private string NpcName(ulong guid) => _world.Units.TryGetValue(guid, out var unit) && unit.Name.Length > 0 ? unit.Name : "";

    private void AddText(string text, Color? color = null)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(420, 0) };
        label.AddThemeColorOverride("font_color", color ?? GlueTheme.Highlight);
        _dialogBody.AddChild(label);
    }

    private void AddButton(string text, Action pressed, Color? color = null, Texture2D? icon = null)
    {
        var button = GlueTheme.Plain(text, 40);
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        if (icon is not null)
        {
            button.Icon = icon;
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 32);
        }
        if (color is { } c)
        {
            button.AddThemeColorOverride("font_color", c);
        }

        button.Pressed += pressed;
        _dialogBody.AddChild(button);
    }

    /// <summary>Item quality colors, grey to orange, as the game shows names.</summary>
    private static Color QualityColor(uint quality) => quality switch
    {
        0 => new Color(0.62f, 0.62f, 0.62f),
        1 => Colors.White,
        2 => new Color(0.12f, 1f, 0f),
        3 => new Color(0f, 0.44f, 0.87f),
        4 => new Color(0.64f, 0.21f, 0.93f),
        5 => new Color(1f, 0.5f, 0f),
        _ => new Color(0.9f, 0.8f, 0.5f),
    };

    private void BuildBags()
    {
        _bags = new PanelContainer { Visible = false };
        _bags.AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 14, GlueTheme.Edge));
        _bags.SetAnchorsPreset(LayoutPreset.TopRight);
        _bags.OffsetLeft = -500;
        _bags.OffsetRight = -16;
        _bags.OffsetTop = 110;
        AddChild(_bags);
        var outer = new VBoxContainer();
        _bags.AddChild(outer);
        var header = new HBoxContainer();
        outer.AddChild(header);
        var title = GlueTheme.Title("Bags");
        title.AddThemeFontSizeOverride("font_size", 24);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        _money = new Label();
        header.AddChild(_money);
        var close = GlueTheme.Plain("X", 36);
        close.CustomMinimumSize = new Vector2(40, 36);
        close.Pressed += () => _bags.Visible = false;
        header.AddChild(close);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(470, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        _bagsBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(_bagsBody);
    }

    private static readonly string[] SlotNames =
    [
        "Head", "Neck", "Shoulders", "Shirt", "Chest", "Waist", "Legs", "Feet", "Wrists", "Hands",
        "Finger", "Finger", "Trinket", "Trinket", "Back", "Main hand", "Off hand", "Ranged", "Tabard",
    ];

    private void RefreshBags()
    {
        foreach (var child in _bagsBody.GetChildren())
        {
            child.QueueFree();
        }

        if (_world.Inventory is not { } inventory)
        {
            return;
        }

        var worn = inventory.Items.Where(i => i.Bag == 255 && i.Slot < 19).OrderBy(i => i.Slot).ToList();
        var carried = inventory.Items.Where(i => !(i.Bag == 255 && i.Slot < 23)).OrderBy(i => i.Bag).ThenBy(i => i.Slot).ToList();
        var bagCount = inventory.Bags.Count;
        var slots = 16 + inventory.Bags.Sum(b => b.Slots);
        AddBagHeader($"Carried ({carried.Count}/{slots}, {bagCount} bag{(bagCount == 1 ? "" : "s")})");
        foreach (var item in carried)
        {
            AddItemRow(item, null);
        }

        AddBagHeader("Worn");
        foreach (var item in worn)
        {
            AddItemRow(item, SlotNames[item.Slot]);
        }
    }

    private void AddBagHeader(string text)
    {
        var label = new Label { Text = text };
        label.AddThemeColorOverride("font_color", GlueTheme.Accent);
        _bagsBody.AddChild(label);
    }

    private void AddItemRow(OwnedItem item, string? slot)
    {
        var info = _world.Session.ItemInfoOf(item.Entry);
        var row = new HBoxContainer();
        _bagsBody.AddChild(row);
        var icon = new ActionIcon(_world.ItemIcon(item.Entry), new ActionButton(item.Entry, ActionKind.Item), 36)
        {
            TooltipText = "Drag to the action bar to use it from there.",
        };
        row.AddChild(icon);
        var label = new Label
        {
            Text = $"{(slot is null ? "" : slot + ": ")}{info?.Name ?? $"Item {item.Entry}"}{(item.Count > 1 ? $" x{item.Count}" : "")}",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            CustomMinimumSize = new Vector2(200, 0),
        };
        label.AddThemeColorOverride("font_color", QualityColor(info?.Quality ?? 1));
        row.AddChild(label);

        void Add(string text, Action pressed)
        {
            var button = GlueTheme.Plain(text, 34);
            button.CustomMinimumSize = new Vector2(70, 34);
            button.Pressed += pressed;
            row.AddChild(button);
        }

        if (_vendor != 0 && slot is null)
        {
            Add("Sell", () => _world.Session.Sell(_vendor, item));
        }

        var equippable = info is { InventoryType: > 0 and not 18 } || slot is not null;
        if (equippable)
        {
            Add(slot is null ? "Equip" : "Remove", () => _world.Session.Equip(item));
            icon.Pressed += () => _world.Session.Equip(item);
        }
        else if (slot is null)
        {
            Add("Use", () => _world.Session.Use(item));
            icon.Pressed += () => _world.Session.Use(item);
        }
    }

    /// <summary>The main bar's twelve buttons, then the spellbook, bags, chat and menu.</summary>
    private void BuildActionBar()
    {
        _bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _bar.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _bar.OffsetLeft = -560;
        _bar.OffsetRight = 560;
        _bar.OffsetTop = -12 - ActionSlot.Size;
        _bar.OffsetBottom = -12;
        _bar.Alignment = BoxContainer.AlignmentMode.Center;
        _bar.AddThemeConstantOverride("separation", 5);
        AddChild(_bar);

        for (var i = 0; i < SlotKeys.Length; i++)
        {
            var slot = new ActionSlot(i, SlotKeys[i], _world, () => _held, DropHeld);
            _slots.Add(slot);
            _bar.AddChild(slot);
        }

        _bar.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0), MouseFilter = MouseFilterEnum.Ignore });

        void Add(string text, Action pressed)
        {
            var button = GlueTheme.Plain(text, ActionSlot.Size);
            button.CustomMinimumSize = new Vector2(92, ActionSlot.Size);
            button.FocusMode = FocusModeEnum.None;
            button.AddThemeFontSizeOverride("font_size", 16);
            button.Pressed += pressed;
            _bar.AddChild(button);
        }

        Add("Spells", ToggleSpellbook);
        Add("Bags", ToggleBags);
        Add("Chat", () => OpenChat());
        Add("Menu", () => MenuRequested?.Invoke());
    }

    /// <summary>The bar over the action bar that fills while casting and empties while channeling, and the note for a held spell.</summary>
    private void BuildCastBar()
    {
        _castBar = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore };
        _castBar.AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 6, 4, GlueTheme.Edge));
        _castBar.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _castBar.OffsetLeft = -170;
        _castBar.OffsetRight = 170;
        _castBar.OffsetTop = -118;
        _castBar.OffsetBottom = -86;
        AddChild(_castBar);
        var track = new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(330, 24) };
        _castBar.AddChild(track);
        _castProgress = new ColorRect { Color = new Color(0.85f, 0.6f, 0f), MouseFilter = MouseFilterEnum.Ignore };
        track.AddChild(_castProgress);
        _castLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _castLabel.AddThemeFontSizeOverride("font_size", 16);
        _castLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _castLabel.AddThemeConstantOverride("outline_size", 4);
        _castBar.AddChild(_castLabel);

        _heldNote = new Label { Visible = false, HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        _heldNote.AddThemeColorOverride("font_color", GlueTheme.Accent);
        _heldNote.AddThemeColorOverride("font_outline_color", Colors.Black);
        _heldNote.AddThemeConstantOverride("outline_size", 4);
        _heldNote.SetAnchorsPreset(LayoutPreset.CenterBottom);
        _heldNote.OffsetLeft = -300;
        _heldNote.OffsetRight = 300;
        _heldNote.OffsetTop = -150;
        _heldNote.OffsetBottom = -124;
        AddChild(_heldNote);
    }

    private uint _castShown;

    private void RefreshCastBar()
    {
        // A cast whose end never came is let go a moment after it should have ended.
        if (_world.Casting is not { } cast || cast.Duration <= 0 || ServerWorld.Now > cast.Start + cast.Duration + 1)
        {
            _castBar.Visible = false;
            _castShown = 0;
            return;
        }

        var done = Math.Clamp((ServerWorld.Now - cast.Start) / cast.Duration, 0, 1);
        _castBar.Visible = true;
        var track = _castProgress.GetParent<Control>().Size;
        _castProgress.Size = new Vector2((float)(track.X * (cast.Channel ? 1 - done : done)), track.Y);
        if (_castShown != cast.Spell)
        {
            _castShown = cast.Spell;
            _castLabel.Text = _world.Spells?.Get((int)cast.Spell)?.Name ?? $"Spell {cast.Spell}";
        }
    }
}

/// <summary>A unit's name, level, health and power, as the game's portrait frames show them.</summary>
public partial class UnitFrame : PanelContainer
{
    private Label _name = null!;
    private Label _status = null!;
    private ProgressBar _health = null!;
    private ProgressBar _power = null!;
    private Label _healthText = null!;
    private Label _powerText = null!;

    /// <summary>The name its place is saved under once dragged; null for a frame that stays put.</summary>
    public string? PlaceKey { get; init; }

    private Vector2? _grab;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(280, 0);
        AddThemeStyleboxOverride("panel", GlueTheme.Box(GlueTheme.Panel, 8, 10, GlueTheme.Edge));
        MouseFilter = PlaceKey is null ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;
        if (PlaceKey is not null)
        {
            TooltipText = "Drag to move";
            MouseDefaultCursorShape = CursorShape.Move;
        }

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);
        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        box.AddChild(top);
        _name = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        top.AddChild(_name);
        _status = new Label();
        _status.AddThemeColorOverride("font_color", GlueTheme.Muted);
        top.AddChild(_status);
        (_health, _healthText) = Bar(box, new Color(0.1f, 0.75f, 0.1f), 20);
        (_power, _powerText) = Bar(box, new Color(0.15f, 0.35f, 0.95f), 14);
    }

    /// <summary>The key its place is saved under: the desktop window and the VR panel differ in size, so each has its own.</summary>
    private string? SavedKey => PlaceKey is null ? null : GetViewport() is SubViewport ? PlaceKey + "_vr" : PlaceKey;

    /// <summary>Puts it back where it was last left, kept on screen.</summary>
    public void RestorePlace()
    {
        if (SavedKey is { } key && AppSettings.HudPosition(key) is { } saved)
        {
            Position = Clamped(saved);
        }
    }

    private Vector2 Clamped(Vector2 at)
    {
        var area = GetParentAreaSize();
        return new Vector2(Mathf.Clamp(at.X, 0, Mathf.Max(area.X - Size.X, 0)), Mathf.Clamp(at.Y, 0, Mathf.Max(area.Y - Size.Y, 0)));
    }

    /// <summary>Dragged with the left button, by the mouse or the VR laser, and left where it's let go.</summary>
    public override void _GuiInput(InputEvent e)
    {
        if (PlaceKey is null)
        {
            return;
        }

        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                _grab = press.Position;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _grab is not null:
                _grab = null;
                AppSettings.SetHudPosition(SavedKey!, Position);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _grab is { } grab:
                Position = Clamped(Position + motion.Position - grab);
                AcceptEvent();
                break;
        }
    }

    private static (ProgressBar, Label) Bar(Container parent, Color color, int height)
    {
        var bar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, height), MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("background", GlueTheme.Box(new Color(0, 0, 0, 0.6f), 3, 0));
        bar.AddThemeStyleboxOverride("fill", GlueTheme.Box(color, 3, 0));
        parent.AddChild(bar);
        var text = new Label { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.AddThemeFontSizeOverride("font_size", Math.Max(height - 4, 10));
        text.AddThemeColorOverride("font_outline_color", Colors.Black);
        text.AddThemeConstantOverride("outline_size", 3);
        text.SetAnchorsPreset(LayoutPreset.FullRect);
        bar.AddChild(text);
        return (bar, text);
    }

    private (string, uint, uint, uint, uint, uint, PowerType, Color, string) _shown;

    public void Set(string name, uint level, uint health, uint maxHealth, uint power, uint maxPower, PowerType powerType, Color nameColor, string status)
    {
        var values = (name, level, health, maxHealth, power, maxPower, powerType, nameColor, status);
        if (values == _shown)
        {
            return;
        }

        _shown = values;
        _name.Text = $"{name}  {level}";
        _name.AddThemeColorOverride("font_color", nameColor);
        _status.Text = status;
        _health.MaxValue = Math.Max(maxHealth, 1);
        _health.Value = health;
        _healthText.Text = $"{health} / {maxHealth}";
        _power.Visible = maxPower > 0;
        _power.MaxValue = Math.Max(maxPower, 1);
        _power.Value = power;
        var shown = powerType is PowerType.Rage or PowerType.RunicPower ? power / 10 : power;
        var shownMax = powerType is PowerType.Rage or PowerType.RunicPower ? maxPower / 10 : maxPower;
        _powerText.Text = $"{shown} / {shownMax}";
        var color = powerType switch
        {
            PowerType.Rage => new Color(0.85f, 0.1f, 0.1f),
            PowerType.Energy => new Color(0.95f, 0.85f, 0.1f),
            PowerType.Focus => new Color(0.95f, 0.5f, 0.25f),
            PowerType.RunicPower => new Color(0f, 0.8f, 1f),
            _ => new Color(0.15f, 0.35f, 0.95f),
        };
        _power.AddThemeStyleboxOverride("fill", GlueTheme.Box(color, 3, 0));
    }
}
