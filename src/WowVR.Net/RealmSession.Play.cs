using System.Numerics;

namespace WowVR.Net;

/// <summary>
/// Playing in the world: the player's movement, the server moving them, chat, talking to NPCs,
/// fighting, items and game objects. What the server says arrives in <see cref="RealmSession.Events"/>.
/// </summary>
public sealed partial class RealmSession
{
    private const ushort MsgCorpseQuery = 0x216;

    /// <summary>While moving, the server hears where the player is this often.</summary>
    private static readonly TimeSpan MovingHeartbeat = TimeSpan.FromMilliseconds(500);

    /// <summary>Turning on the spot is sent at most this often.</summary>
    private static readonly TimeSpan FacingInterval = TimeSpan.FromMilliseconds(200);

    private readonly Dictionary<ulong, string> _playerNames = [];
    private readonly HashSet<ulong> _askedPlayerNames = [];
    private readonly Dictionary<uint, ItemInfo> _items = [];
    private readonly HashSet<uint> _askedItems = [];
    private readonly Dictionary<uint, (string Name, uint Type, uint Display, uint[] Data)> _gameObjectTemplates = [];
    private readonly HashSet<uint> _askedGameObjects = [];
    private readonly Dictionary<ulong, TrackedObject> _waitingGameObjects = [];
    private readonly Dictionary<uint, string> _npcTexts = [];
    private readonly Dictionary<uint, List<Action<string>>> _waitingNpcTexts = [];
    private readonly object _moveLock = new();
    private MovementInfo _lastMove;
    private TimeSpan _lastMoveSent;
    private bool _hasMoved;
    private bool _inventoryDirty;
    private PlayerStats? _self;

    /// <summary>The player's own unit, once the server has sent it.</summary>
    public PlayerStats? Self => _self;

    public ulong SelfGuid => Character.Guid;

    /// <summary>The language the character speaks: Orcish for the Horde and Common for the Alliance.</summary>
    private uint Language => Character.Race is 2 or 5 or 6 or 8 or 10 ? 1u : 7u;

    /// <summary>Milliseconds on this client's clock, which the server matches to its own through time sync.</summary>
    private uint Now => (uint)_clock.ElapsedMilliseconds;

    /// <summary>
    /// Says how the player is moving, every frame. The server hears when they start, stop or
    /// change how they move, and where they are twice a second while they do.
    /// </summary>
    public void UpdateMovement(MovementInfo move)
    {
        if (!IsOpen || !_inWorld || _worldPortPending)
        {
            if (LogMoves && _worldPortPending)
            {
                _log("move held: waiting for the world port");
            }

            return;
        }

        lock (_moveLock)
        {
            var now = _clock.Elapsed;
            var previous = _lastMove;
            var opcode = (ushort)0;
            if (!_hasMoved)
            {
                opcode = Opcodes.MSG_MOVE_HEARTBEAT;
            }
            else if (previous.Flags != move.Flags)
            {
                opcode = ChangeOpcode(previous.Flags, move.Flags);
            }
            else if ((move.Flags & (MoveFlags.Moving | MoveFlags.Falling | MoveFlags.Ascending | MoveFlags.Descending)) != 0
                || Vector3.DistanceSquared(previous.Position, move.Position) > 0.25f)
            {
                if (now - _lastMoveSent >= MovingHeartbeat)
                {
                    opcode = Opcodes.MSG_MOVE_HEARTBEAT;
                }
            }
            else if (MathF.Abs(previous.Orientation - move.Orientation) > 0.02f && now - _lastMoveSent >= FacingInterval)
            {
                opcode = Opcodes.MSG_MOVE_SET_FACING;
            }

            if (opcode == 0)
            {
                return;
            }

            SendMove(opcode, move);
        }
    }

    /// <summary>The player jumped: what the server needs to show it and to know they're in the air.</summary>
    public void Jump(MovementInfo move)
    {
        if (!IsOpen || !_inWorld || _worldPortPending)
        {
            return;
        }

        lock (_moveLock)
        {
            SendMove(Opcodes.MSG_MOVE_JUMP, move);
        }
    }

    /// <summary>Landed from a jump or fall.</summary>
    public void Land(MovementInfo move)
    {
        if (!IsOpen || !_inWorld || _worldPortPending)
        {
            return;
        }

        lock (_moveLock)
        {
            SendMove(Opcodes.MSG_MOVE_FALL_LAND, move);
        }
    }

    private static readonly bool LogMoves = Environment.GetEnvironmentVariable("WOWVR_NET_LOG_MOVES") == "1";

    private void SendMove(ushort opcode, MovementInfo move)
    {
        move.Time = Now;
        if (LogMoves)
        {
            _log($"move 0x{opcode:X3} flags {move.Flags} at ({move.Position.X:F1}, {move.Position.Y:F1}, {move.Position.Z:F1}) facing {move.Orientation:F2}");
        }

        var w = new PacketWriter().Packed(Character.Guid);
        move.Write(w);
        _ = SendQuietly(opcode, w.ToArray());
        _lastMove = move;
        _lastMoveSent = _clock.Elapsed;
        _hasMoved = true;
    }

    /// <summary>The opcode a real client sends for a change of movement flags.</summary>
    internal static ushort ChangeOpcode(MoveFlags before, MoveFlags after)
    {
        var started = after & ~before;
        var stopped = before & ~after;
        if ((started & MoveFlags.Swimming) != 0)
        {
            return Opcodes.MSG_MOVE_START_SWIM;
        }

        if ((stopped & MoveFlags.Swimming) != 0)
        {
            return Opcodes.MSG_MOVE_STOP_SWIM;
        }

        if ((started & MoveFlags.Forward) != 0)
        {
            return Opcodes.MSG_MOVE_START_FORWARD;
        }

        if ((started & MoveFlags.Backward) != 0)
        {
            return Opcodes.MSG_MOVE_START_BACKWARD;
        }

        if ((started & MoveFlags.StrafeLeft) != 0)
        {
            return Opcodes.MSG_MOVE_START_STRAFE_LEFT;
        }

        if ((started & MoveFlags.StrafeRight) != 0)
        {
            return Opcodes.MSG_MOVE_START_STRAFE_RIGHT;
        }

        if ((stopped & (MoveFlags.Forward | MoveFlags.Backward)) != 0)
        {
            return Opcodes.MSG_MOVE_STOP;
        }

        if ((stopped & (MoveFlags.StrafeLeft | MoveFlags.StrafeRight)) != 0)
        {
            return (after & (MoveFlags.Forward | MoveFlags.Backward)) != 0 ? Opcodes.MSG_MOVE_STOP_STRAFE : Opcodes.MSG_MOVE_STOP;
        }

        if ((stopped & MoveFlags.Falling) != 0)
        {
            return Opcodes.MSG_MOVE_FALL_LAND;
        }

        return Opcodes.MSG_MOVE_HEARTBEAT;
    }

    private bool _worldPortPending;

    /// <summary>The game has the new map loading: the server can put the player in it.</summary>
    public void AckWorldPort()
    {
        if (!_worldPortPending)
        {
            return;
        }

        _worldPortPending = false;
        _hasMoved = false;
        _ = SendQuietly(Opcodes.MSG_MOVE_WORLDPORT_ACK, []);
    }

    /// <summary>Says something, or runs a command: <c>/s</c>, <c>/y</c>, <c>/w Name</c>, <c>/e</c>, <c>/p</c>, <c>/g</c>, or a server command starting with a dot.</summary>
    public void Say(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || !_inWorld)
        {
            return;
        }

        var kind = ChatKind.Say;
        string? target = null;
        if (text.StartsWith('/'))
        {
            var space = text.IndexOf(' ');
            var command = (space < 0 ? text : text[..space]).ToLowerInvariant();
            var rest = space < 0 ? "" : text[(space + 1)..].Trim();
            switch (command)
            {
                case "/s" or "/say":
                    kind = ChatKind.Say;
                    break;
                case "/y" or "/yell":
                    kind = ChatKind.Yell;
                    break;
                case "/e" or "/em" or "/me" or "/emote":
                    kind = ChatKind.Emote;
                    break;
                case "/p" or "/party":
                    kind = ChatKind.Party;
                    break;
                case "/g" or "/guild":
                    kind = ChatKind.Guild;
                    break;
                case "/w" or "/whisper" or "/t" or "/tell":
                {
                    var split = rest.IndexOf(' ');
                    if (split < 0)
                    {
                        Notice("Whisper to whom? /w Name message", true);
                        return;
                    }

                    kind = ChatKind.Whisper;
                    target = rest[..split];
                    rest = rest[(split + 1)..];
                    break;
                }
                default:
                    Notice($"Unknown command: {command}", true);
                    return;
            }

            text = rest;
            if (text.Length == 0)
            {
                return;
            }
        }

        var w = new PacketWriter().U32((uint)kind).U32(Language);
        if (target is not null)
        {
            w.CString(target);
        }

        w.CString(text);
        _ = SendQuietly(Opcodes.CMSG_MESSAGECHAT, w.ToArray());
    }

    /// <summary>A line only this client shows, as the game does for errors.</summary>
    private void Notice(string text, bool error) =>
        Events.Enqueue(new ChatReceived(new ChatLine(error ? ChatKind.Error : ChatKind.Notice, "", StripCodes(text))));

    private bool _askedCorpse;

    private void ReadCorpse(byte[] data)
    {
        var r = new PacketReader(data);
        if (r.U8() == 0)
        {
            return;
        }

        var map = r.U32();
        var position = new Vector3(r.F32(), r.F32(), r.F32());
        Events.Enqueue(new CorpseAt(position, map));
    }

    /// <summary>Tells the server what the player has selected, which spells and some commands act on.</summary>
    public void Select(ulong guid) => _ = SendQuietly(Opcodes.CMSG_SET_SELECTION, new PacketWriter().U64(guid).ToArray());

    public void Attack(ulong guid) => _ = SendQuietly(Opcodes.CMSG_ATTACKSWING, new PacketWriter().U64(guid).ToArray());

    public void StopAttack() => _ = SendQuietly(Opcodes.CMSG_ATTACKSTOP, []);

    /// <summary>Right-clicking an NPC: its talk window, its quests or its wares, whichever it has.</summary>
    public void Interact(UnitStats unit)
    {
        var guid = new PacketWriter().U64(unit.Guid).ToArray();
        if (unit.IsSpiritHealer && _self?.IsGhost == true)
        {
            _ = SendQuietly(Opcodes.CMSG_GOSSIP_HELLO, guid);
        }
        else if (unit.HasGossip)
        {
            _ = SendQuietly(Opcodes.CMSG_GOSSIP_HELLO, guid);
        }
        else if (unit.IsQuestGiver)
        {
            _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_HELLO, guid);
        }
        else if (unit.IsVendor)
        {
            _ = SendQuietly(Opcodes.CMSG_LIST_INVENTORY, guid);
        }
        else if (unit.Lootable)
        {
            _ = SendQuietly(Opcodes.CMSG_LOOT, guid);
        }
        else
        {
            _ = SendQuietly(Opcodes.CMSG_GOSSIP_HELLO, guid);
        }
    }

    public void Loot(ulong guid) => _ = SendQuietly(Opcodes.CMSG_LOOT, new PacketWriter().U64(guid).ToArray());

    public void ChooseGossip(ulong npc, uint menuId, uint index) =>
        _ = SendQuietly(Opcodes.CMSG_GOSSIP_SELECT_OPTION, new PacketWriter().U64(npc).U32(menuId).U32(index).ToArray());

    /// <summary>
    /// Picking a quest in an NPC's list: one it takes back (icon 4) is handed in, which shows what it
    /// still needs or the rewards; any other is asked about, to show it to accept.
    /// </summary>
    public void ChooseGossipQuest(ulong npc, GossipQuest quest)
    {
        if (quest.Icon == 4)
        {
            CompleteQuest(npc, quest.Id);
        }
        else
        {
            QueryQuest(npc, quest.Id);
        }
    }

    public void QueryQuest(ulong npc, uint quest) =>
        _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_QUERY_QUEST, new PacketWriter().U64(npc).U32(quest).U8(1).ToArray());

    public void AcceptQuest(ulong npc, uint quest) =>
        _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_ACCEPT_QUEST, new PacketWriter().U64(npc).U32(quest).U32(0).ToArray());

    public void CompleteQuest(ulong npc, uint quest) =>
        _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_COMPLETE_QUEST, new PacketWriter().U64(npc).U32(quest).ToArray());

    public void RequestQuestReward(ulong npc, uint quest) =>
        _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_REQUEST_REWARD, new PacketWriter().U64(npc).U32(quest).ToArray());

    public void ChooseQuestReward(ulong npc, uint quest, uint choice) =>
        _ = SendQuietly(Opcodes.CMSG_QUESTGIVER_CHOOSE_REWARD, new PacketWriter().U64(npc).U32(quest).U32(choice).ToArray());

    public void Buy(ulong vendor, VendorItem item, uint count = 1) =>
        _ = SendQuietly(Opcodes.CMSG_BUY_ITEM, new PacketWriter().U64(vendor).U32(item.ItemId).U32(item.Slot).U32(count).U8(0).ToArray());

    public void Sell(ulong vendor, OwnedItem item) =>
        _ = SendQuietly(Opcodes.CMSG_SELL_ITEM, new PacketWriter().U64(vendor).U64(item.Guid).U32(item.Count).ToArray());

    /// <summary>Puts on an item from the bags, or takes one off into them when it's worn.</summary>
    public void Equip(OwnedItem item)
    {
        if (item.Bag == 255 && item.Slot < 19)
        {
            _ = SendQuietly(0x10B, new PacketWriter().U8(item.Bag).U8(item.Slot).U8(255).ToArray()); // CMSG_AUTOSTORE_BAG_ITEM
        }
        else
        {
            _ = SendQuietly(Opcodes.CMSG_AUTOEQUIP_ITEM, new PacketWriter().U8(item.Bag).U8(item.Slot).ToArray());
        }
    }

    /// <summary>Uses an item, as eating or opening something. Items whose spell needs a target aren't handled.</summary>
    public void Use(OwnedItem item) =>
        _ = SendQuietly(Opcodes.CMSG_USE_ITEM, new PacketWriter()
            .U8(item.Bag).U8(item.Slot).U8(0) // cast count
            .U32(0) // spell, which the server finds from the item
            .U64(item.Guid).U32(0).U8(0) // glyph index, cast flags
            .U32(0) // target flags: self
            .ToArray());

    public void UseGameObject(ulong guid) => _ = SendQuietly(Opcodes.CMSG_GAMEOBJ_USE, new PacketWriter().U64(guid).ToArray());

    /// <summary>The player walked into an area trigger, as an instance's entrance or exit.</summary>
    public void EnterAreaTrigger(uint id) => _ = SendQuietly(Opcodes.CMSG_AREATRIGGER, new PacketWriter().U32(id).ToArray());

    /// <summary>Leaves the body as a spirit, at the graveyard.</summary>
    public void ReleaseSpirit() => _ = SendQuietly(Opcodes.CMSG_REPOP_REQUEST, new PacketWriter().U8(0).ToArray());

    /// <summary>Back into the body, standing at the corpse.</summary>
    public void ReclaimCorpse() => _ = SendQuietly(Opcodes.CMSG_RECLAIM_CORPSE, new PacketWriter().U64(Character.Guid).ToArray());

    public void AcceptSpiritHealer(ulong npc) => _ = SendQuietly(Opcodes.CMSG_SPIRIT_HEALER_ACTIVATE, new PacketWriter().U64(npc).ToArray());

    public void AnswerResurrect(ulong from, bool accept) =>
        _ = SendQuietly(Opcodes.CMSG_RESURRECT_RESPONSE, new PacketWriter().U64(from).U8(accept ? (byte)1 : (byte)0).ToArray());

    /// <summary>What's known about an item, or null while the server is asked.</summary>
    public ItemInfo? ItemInfoOf(uint entry)
    {
        lock (_items)
        {
            if (_items.TryGetValue(entry, out var info))
            {
                return info;
            }
        }

        AskItem(entry);
        return null;
    }

    private void AskItem(uint entry)
    {
        if (entry == 0)
        {
            return;
        }

        lock (_items)
        {
            if (!_askedItems.Add(entry))
            {
                return;
            }
        }

        _ = SendQuietly(Opcodes.CMSG_ITEM_QUERY_SINGLE, new PacketWriter().U32(entry).ToArray());
    }

    /// <summary>The name the server gave for a player or creature, or empty.</summary>
    public string NameOf(TrackedObject obj) => obj.Type == ObjectType.Player
        ? _playerNames.GetValueOrDefault(obj.Guid, "")
        : _creatureNames.GetValueOrDefault(obj.Entry, "");

    /// <summary>Packets of the world beyond creatures' comings and goings. Runs on the reading thread.</summary>
    private void HandlePlay(WorldPacket p)
    {
        if (LogMoves)
        {
            _log($"got 0x{p.Opcode:X3}, {p.Data.Length} bytes");
        }

        switch (p.Opcode)
        {
            case Opcodes.MSG_MOVE_TELEPORT_ACK:
                ReadTeleport(p.Data);
                break;
            case Opcodes.SMSG_TRANSFER_PENDING:
                _worldPortPending = true;
                break;
            case Opcodes.SMSG_NEW_WORLD:
                ReadNewWorld(p.Data);
                break;
            case Opcodes.SMSG_FORCE_WALK_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_RUN_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_RUN_BACK_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_SWIM_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_FLIGHT_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE:
            case Opcodes.SMSG_FORCE_TURN_RATE_CHANGE:
                ReadSpeedChange(p.Opcode, p.Data);
                break;
            case Opcodes.SMSG_MOVE_SET_CAN_FLY:
            case Opcodes.SMSG_MOVE_UNSET_CAN_FLY:
            case Opcodes.SMSG_FORCE_MOVE_ROOT:
            case Opcodes.SMSG_FORCE_MOVE_UNROOT:
                ReadMoveToggle(p.Opcode, p.Data);
                break;
            case >= Opcodes.MSG_MOVE_START_FORWARD and <= Opcodes.MSG_MOVE_STOP_TURN:
            case Opcodes.MSG_MOVE_FALL_LAND:
            case Opcodes.MSG_MOVE_START_SWIM:
            case Opcodes.MSG_MOVE_STOP_SWIM:
            case Opcodes.MSG_MOVE_SET_FACING:
            case Opcodes.MSG_MOVE_HEARTBEAT:
            case Opcodes.MSG_MOVE_TELEPORT:
                ReadOtherMovement(p.Data);
                break;
            case Opcodes.SMSG_MESSAGECHAT:
            case Opcodes.SMSG_GM_MESSAGECHAT:
                ReadChat(p.Data, p.Opcode == Opcodes.SMSG_GM_MESSAGECHAT);
                break;
            case Opcodes.SMSG_NOTIFICATION:
                Events.Enqueue(new ScreenMessage(new PacketReader(p.Data).CString(), false));
                break;
            case Opcodes.SMSG_AREA_TRIGGER_MESSAGE:
            {
                var r = new PacketReader(p.Data);
                r.U32();
                Events.Enqueue(new ScreenMessage(r.CString(), true));
                break;
            }
            case Opcodes.SMSG_SERVER_MESSAGE:
            {
                var r = new PacketReader(p.Data);
                r.U32();
                Notice(r.CString(), false);
                break;
            }
            case Opcodes.SMSG_MOTD:
            {
                var r = new PacketReader(p.Data);
                var lines = r.U32();
                for (uint i = 0; i < lines && r.Remaining > 0; i++)
                {
                    Notice(r.CString(), false);
                }

                break;
            }
            case Opcodes.SMSG_CHAT_PLAYER_NOT_FOUND:
                Notice($"No player named '{new PacketReader(p.Data).CString()}' is playing.", true);
                break;
            case Opcodes.SMSG_NAME_QUERY_RESPONSE:
                ReadPlayerName(p.Data);
                break;
            case Opcodes.SMSG_ITEM_QUERY_SINGLE_RESPONSE:
                ReadItem(p.Data);
                break;
            case Opcodes.SMSG_GAMEOBJECT_QUERY_RESPONSE:
                ReadGameObjectTemplate(p.Data);
                break;
            case Opcodes.SMSG_GOSSIP_MESSAGE:
                ReadGossip(p.Data);
                break;
            case Opcodes.SMSG_GOSSIP_COMPLETE:
                Events.Enqueue(new GossipClosed());
                break;
            case Opcodes.SMSG_NPC_TEXT_UPDATE:
                ReadNpcText(p.Data);
                break;
            case Opcodes.SMSG_QUESTGIVER_QUEST_LIST:
                ReadQuestList(p.Data);
                break;
            case Opcodes.SMSG_QUESTGIVER_QUEST_DETAILS:
                ReadQuestDetails(p.Data);
                break;
            case Opcodes.SMSG_QUESTGIVER_REQUEST_ITEMS:
                ReadQuestRequestItems(p.Data);
                break;
            case Opcodes.SMSG_QUESTGIVER_OFFER_REWARD:
                ReadQuestOfferReward(p.Data);
                break;
            case Opcodes.SMSG_QUESTGIVER_QUEST_COMPLETE:
                Events.Enqueue(new GossipClosed());
                Notice("Quest completed.", false);
                break;
            case Opcodes.SMSG_LIST_INVENTORY:
                ReadVendor(p.Data);
                break;
            case Opcodes.SMSG_LOOT_RESPONSE:
                TakeLoot(p.Data);
                break;
            case Opcodes.SMSG_ATTACKSTART:
            {
                var r = new PacketReader(p.Data);
                var attacker = r.U64();
                var victim = r.U64();
                if (attacker == Character.Guid)
                {
                    Events.Enqueue(new AttackChanged(victim, true));
                }

                break;
            }
            case Opcodes.SMSG_ATTACKSTOP:
            {
                var r = new PacketReader(p.Data);
                var attacker = r.PackedGuid();
                var victim = r.PackedGuid();
                if (attacker == Character.Guid)
                {
                    Events.Enqueue(new AttackChanged(victim, false));
                }

                break;
            }
            case Opcodes.SMSG_ATTACKSWING_NOTINRANGE:
                Events.Enqueue(new ScreenMessage("You are too far away!", true));
                break;
            case Opcodes.SMSG_ATTACKSWING_BADFACING:
                Events.Enqueue(new ScreenMessage("You are facing the wrong way!", true));
                break;
            case Opcodes.SMSG_ATTACKSWING_DEADTARGET:
                Events.Enqueue(new ScreenMessage("Your target is dead.", true));
                break;
            case Opcodes.SMSG_ATTACKSWING_CANT_ATTACK:
                Events.Enqueue(new ScreenMessage("You can't attack that.", true));
                break;
            case Opcodes.SMSG_ATTACKERSTATEUPDATE:
                ReadSwing(p.Data);
                break;
            case Opcodes.SMSG_INVENTORY_CHANGE_FAILURE:
                if (p.Data.Length > 0 && p.Data[0] != 0)
                {
                    Events.Enqueue(new ScreenMessage(InventoryError(p.Data[0]), true));
                }

                break;
            case Opcodes.SMSG_BUY_FAILED:
                Events.Enqueue(new ScreenMessage("You can't buy that.", true));
                break;
            case Opcodes.SMSG_LOG_XPGAIN:
            {
                var r = new PacketReader(p.Data);
                r.U64();
                Notice($"You gain {r.U32()} experience.", false);
                break;
            }
            case Opcodes.SMSG_LEVELUP_INFO:
                Notice($"Congratulations, you have reached level {new PacketReader(p.Data).U32()}!", false);
                break;
            case Opcodes.SMSG_ITEM_PUSH_RESULT:
            {
                var r = new PacketReader(p.Data);
                r.U64(); // player
                var received = r.U32();
                r.U32(); // created
                r.U32(); // shown
                r.U8(); // bag
                r.U32(); // slot
                var entry = r.U32();
                r.U32(); // suffix
                r.U32(); // random property
                var count = r.U32();
                var name = ItemInfoOf(entry)?.Name ?? $"item {entry}";
                Notice($"You {(received == 0 ? "loot" : "receive")}: {name}{(count > 1 ? $" x{count}" : "")}", false);
                break;
            }
            case MsgCorpseQuery:
                ReadCorpse(p.Data);
                break;
            case 0x18F: // SMSG_QUESTGIVER_QUEST_INVALID
                Events.Enqueue(new ScreenMessage(QuestInvalid(new PacketReader(p.Data).U32()), true));
                break;
            case Opcodes.SMSG_SPIRIT_HEALER_CONFIRM:
                Events.Enqueue(new SpiritHealerOffer(new PacketReader(p.Data).U64()));
                break;
            case Opcodes.SMSG_RESURRECT_REQUEST:
            {
                var r = new PacketReader(p.Data);
                var from = r.U64();
                r.U32();
                Events.Enqueue(new ResurrectOffer(from, r.Remaining > 0 ? r.CString() : ""));
                break;
            }
            default:
                HandleSpells(p);
                break;
        }
    }

    private void ReadTeleport(byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.PackedGuid();
        var counter = r.U32();
        var info = MovementInfo.Read(r);
        if (guid != Character.Guid)
        {
            return;
        }

        lock (_moveLock)
        {
            _lastMove = info;
            _hasMoved = false;
        }

        _ = SendQuietly(Opcodes.MSG_MOVE_TELEPORT_ACK, new PacketWriter().Packed(guid).U32(counter).U32(Now).ToArray());
        Events.Enqueue(new Teleported(info.Position, info.Orientation));
    }

    private void ReadNewWorld(byte[] data)
    {
        var r = new PacketReader(data);
        var map = r.U32();
        var position = new Vector3(r.F32(), r.F32(), r.F32());
        var orientation = r.F32();
        _worldPortPending = true;
        MapId = map;
        StartPosition = position;
        StartOrientation = orientation;

        // A new map starts with nothing known: the server sends it all again once the client says it's there.
        foreach (var obj in _objects.Objects.Values.ToList())
        {
            OnRemoved(obj);
        }

        _objects.Objects.Clear();
        _waitingGameObjects.Clear();
        while (Events.TryDequeue(out _))
        {
        }

        Events.Enqueue(new WorldChanged(map, position, orientation));
    }

    private void ReadSpeedChange(ushort opcode, byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.PackedGuid();
        var counter = r.U32();
        if (opcode == Opcodes.SMSG_FORCE_RUN_SPEED_CHANGE)
        {
            r.U8();
        }

        var speed = r.F32();
        if (guid != Character.Guid || !_objects.Objects.TryGetValue(guid, out var self))
        {
            return;
        }

        switch (opcode)
        {
            case Opcodes.SMSG_FORCE_WALK_SPEED_CHANGE: self.WalkSpeed = speed; break;
            case Opcodes.SMSG_FORCE_RUN_SPEED_CHANGE: self.RunSpeed = speed; break;
            case Opcodes.SMSG_FORCE_RUN_BACK_SPEED_CHANGE: self.RunBackSpeed = speed; break;
            case Opcodes.SMSG_FORCE_SWIM_SPEED_CHANGE: self.SwimSpeed = speed; break;
            case Opcodes.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE: self.SwimBackSpeed = speed; break;
            case Opcodes.SMSG_FORCE_FLIGHT_SPEED_CHANGE: self.FlightSpeed = speed; break;
            case Opcodes.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE: self.FlightBackSpeed = speed; break;
        }

        var w = new PacketWriter().Packed(guid).U32(counter);
        CurrentMove().Write(w);
        w.F32(speed);
        _ = SendQuietly((ushort)(opcode + 1), w.ToArray());
        Events.Enqueue(new SpeedChanged(SpeedsOf(self)));
    }

    private static PlayerSpeeds SpeedsOf(TrackedObject self) =>
        new(self.WalkSpeed, self.RunSpeed, self.RunBackSpeed, self.SwimSpeed, self.SwimBackSpeed, self.FlightSpeed, self.FlightBackSpeed);

    private void ReadMoveToggle(ushort opcode, byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.PackedGuid();
        var counter = r.U32();
        if (guid != Character.Guid)
        {
            return;
        }

        var (ack, flag, on) = opcode switch
        {
            Opcodes.SMSG_MOVE_SET_CAN_FLY => (Opcodes.CMSG_MOVE_SET_CAN_FLY_ACK, MoveFlags.CanFly, true),
            Opcodes.SMSG_MOVE_UNSET_CAN_FLY => (Opcodes.CMSG_MOVE_SET_CAN_FLY_ACK, MoveFlags.CanFly, false),
            Opcodes.SMSG_FORCE_MOVE_ROOT => (Opcodes.CMSG_FORCE_MOVE_ROOT_ACK, MoveFlags.Root, true),
            _ => (Opcodes.CMSG_FORCE_MOVE_UNROOT_ACK, MoveFlags.Root, false),
        };

        MovementInfo move;
        lock (_moveLock)
        {
            move = CurrentMove();
            move.Flags = on ? move.Flags | flag : move.Flags & ~flag;
            if (flag == MoveFlags.Root && on)
            {
                move.Flags &= ~MoveFlags.Moving;
            }

            _lastMove = move;
        }

        var w = new PacketWriter().Packed(guid).U32(counter);
        move.Write(w);
        if (ack == Opcodes.CMSG_MOVE_SET_CAN_FLY_ACK)
        {
            w.F32(on ? 1f : 0f);
        }

        _ = SendQuietly(ack, w.ToArray());
        Events.Enqueue(flag == MoveFlags.CanFly ? new CanFlyChanged(on) : new RootChanged(on));
    }

    /// <summary>The player's movement as last sent, at this moment.</summary>
    private MovementInfo CurrentMove()
    {
        var move = _lastMove;
        if (!_hasMoved && _objects.Objects.TryGetValue(Character.Guid, out var self))
        {
            move.Position = self.Position;
            move.Orientation = self.Orientation;
        }

        move.Time = Now;
        return move;
    }

    private void ReadOtherMovement(byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.PackedGuid();
        if (guid == Character.Guid)
        {
            return;
        }

        _objects.ApplyMovement(guid, MovementInfo.Read(r));
    }

    private void ReadChat(byte[] data, bool gm)
    {
        var r = new PacketReader(data);
        var kind = (ChatKind)r.U8();
        r.U32(); // language
        var sender = r.U64();
        r.U32();
        var name = "";
        var channel = "";
        switch (kind)
        {
            case ChatKind.MonsterSay or ChatKind.MonsterParty or ChatKind.MonsterYell or ChatKind.MonsterWhisper or ChatKind.MonsterEmote or ChatKind.RaidBossEmote or ChatKind.RaidBossWhisper:
            {
                r.U32();
                name = r.CString();
                var receiver = r.U64();
                if (receiver != 0 && (receiver >> 48) != 0 && (receiver >> 48) != 0xF140)
                {
                    r.U32();
                    r.CString();
                }

                break;
            }
            case ChatKind.WhisperForeign:
                r.U32();
                name = r.CString();
                r.U64();
                break;
            default:
                if (gm)
                {
                    r.U32();
                    name = r.CString();
                }

                if (kind == ChatKind.Channel)
                {
                    channel = r.CString();
                }

                r.U64();
                break;
        }

        r.U32(); // length
        var text = StripCodes(r.CString());
        if (name.Length == 0 && sender != 0)
        {
            name = _playerNames.GetValueOrDefault(sender, "");
            if (name.Length == 0 && _objects.Objects.TryGetValue(sender, out var obj))
            {
                name = NameOf(obj);
            }

            if (name.Length == 0 && (sender >> 48) == 0)
            {
                // A player we haven't seen: the line waits for the name.
                AskPlayerName(sender, n => Events.Enqueue(new ChatReceived(new ChatLine(kind, n, text, channel))));
                return;
            }
        }

        Events.Enqueue(new ChatReceived(new ChatLine(kind, name, text, channel)));
    }

    /// <summary>WoW's markup: colors as |cAARRGGBB ... |r, and links as |H...|h[text]|h, shown as their text.</summary>
    internal static string StripCodes(string text)
    {
        var b = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '|' && i + 1 < text.Length)
            {
                var c = text[i + 1];
                if (c == 'c' && i + 10 <= text.Length)
                {
                    i += 9;
                    continue;
                }

                if (c == 'r')
                {
                    i++;
                    continue;
                }

                if (c == 'H')
                {
                    var end = text.IndexOf("|h", i + 2, StringComparison.Ordinal);
                    if (end > 0)
                    {
                        i = end + 1;
                        continue;
                    }
                }

                if (c == 'h')
                {
                    i++;
                    continue;
                }

                if (c == '|')
                {
                    b.Append('|');
                    i++;
                    continue;
                }
            }

            b.Append(text[i]);
        }

        return b.ToString();
    }

    private readonly Dictionary<ulong, List<Action<string>>> _waitingPlayerNames = [];

    private void AskPlayerName(ulong guid, Action<string>? then = null)
    {
        if (then is not null)
        {
            if (!_waitingPlayerNames.TryGetValue(guid, out var list))
            {
                _waitingPlayerNames[guid] = list = [];
            }

            list.Add(then);
        }

        if (_askedPlayerNames.Add(guid))
        {
            _ = SendQuietly(Opcodes.CMSG_NAME_QUERY, new PacketWriter().U64(guid).ToArray());
        }
    }

    private void ReadPlayerName(byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.PackedGuid();
        if (r.U8() != 0)
        {
            return;
        }

        var name = r.CString();
        _playerNames[guid] = name;
        if (_waitingPlayerNames.Remove(guid, out var waiting))
        {
            foreach (var then in waiting)
            {
                then(name);
            }
        }

        if (_objects.Objects.TryGetValue(guid, out var obj))
        {
            Events.Enqueue(new UnitChanged(UnitStats.From(obj, name)));
        }
    }

    private void ReadItem(byte[] data)
    {
        var r = new PacketReader(data);
        var entry = r.U32();
        if ((entry & 0x80000000) != 0)
        {
            return;
        }

        r.U32(); // class
        r.U32(); // subclass
        r.U32(); // sound override
        var name = r.CString();
        r.CString();
        r.CString();
        r.CString();
        var display = r.U32();
        var quality = r.U32();
        r.U32(); // flags
        r.U32(); // flags 2
        var buy = r.U32();
        var sell = r.U32();
        var inventoryType = r.U32();
        r.Skip(4 * 12); // classes, races, level, required level, skill, rank, spell, honor, city, faction, rank, max count
        r.U32(); // stackable
        var slots = r.U32();
        var info = new ItemInfo(entry, name, quality, inventoryType, display, sell, buy, slots);
        lock (_items)
        {
            _items[entry] = info;
        }

        Events.Enqueue(new ItemInfoKnown(info));
    }

    private void ReadGameObjectTemplate(byte[] data)
    {
        var r = new PacketReader(data);
        var entry = r.U32();
        if ((entry & 0x80000000) != 0)
        {
            entry &= 0x7FFFFFFF;
            _gameObjectTemplates[entry] = ("", 0, 0, new uint[24]);
        }
        else
        {
            var type = r.U32();
            var display = r.U32();
            var name = r.CString();
            r.CString();
            r.CString();
            r.CString();
            r.CString(); // icon
            r.CString(); // cast bar caption
            r.CString();
            var fields = new uint[24];
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i] = r.U32();
            }

            _gameObjectTemplates[entry] = (name, type, display, fields);
        }

        foreach (var (guid, obj) in _waitingGameObjects.Where(w => w.Value.Entry == entry).ToList())
        {
            _waitingGameObjects.Remove(guid);
            AnnounceGameObject(obj);
        }
    }

    private void OnGameObjectCreated(TrackedObject obj)
    {
        if (_gameObjectTemplates.ContainsKey(obj.Entry))
        {
            AnnounceGameObject(obj);
            return;
        }

        _waitingGameObjects[obj.Guid] = obj;
        if (_askedGameObjects.Add(obj.Entry))
        {
            _ = SendQuietly(Opcodes.CMSG_GAMEOBJECT_QUERY, new PacketWriter().U32(obj.Entry).U64(obj.Guid).ToArray());
        }
    }

    private void AnnounceGameObject(TrackedObject obj)
    {
        if (!_announced.Add(obj.Guid))
        {
            return;
        }

        Events.Enqueue(new GameObjectAppeared(GameObjectInfoOf(obj)));
    }

    /// <summary>What the game needs to draw and use a game object, from what the server sent and its template.</summary>
    private GameObjectInfo GameObjectInfoOf(TrackedObject obj)
    {
        if (!_gameObjectTemplates.TryGetValue(obj.Entry, out var template))
        {
            template = ("", 0u, 0u, new uint[24]);
        }

        var rotation = obj.Rotation ?? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, obj.Orientation);
        var bytes1 = obj.Field(UpdateFields.GameObjectBytes1);
        return new GameObjectInfo(
            obj.Guid, obj.Entry, template.Item1, obj.DisplayId, (bytes1 >> 8) & 0xFF, obj.Position, rotation, obj.Scale,
            obj.Field(UpdateFields.GameObjectFlags), bytes1 & 0xFF, obj.PathProgress, template.Item4);
    }

    private void ReadGossip(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        var menu = r.U32();
        var textId = r.U32();
        var count = r.U32();
        var options = new List<GossipOption>();
        for (uint i = 0; i < count; i++)
        {
            var index = r.U32();
            var icon = r.U8();
            r.U8(); // coded
            r.U32(); // money
            var text = r.CString();
            r.CString();
            options.Add(new GossipOption(index, icon, StripCodes(text)));
        }

        var quests = ReadQuestItems(r, r.U32());
        WithNpcText(textId, text => Events.Enqueue(new GossipShown(npc, menu, text, options, quests)));
    }

    private static List<GossipQuest> ReadQuestItems(PacketReader r, uint count)
    {
        var quests = new List<GossipQuest>();
        for (uint i = 0; i < count; i++)
        {
            var id = r.U32();
            var icon = r.U32();
            var level = (int)r.U32();
            r.U32(); // flags
            r.U8(); // repeatable
            quests.Add(new GossipQuest(id, icon, level, StripCodes(r.CString())));
        }

        return quests;
    }

    /// <summary>Calls back with an NPC's greeting, asking the server for it the first time.</summary>
    private void WithNpcText(uint id, Action<string> then)
    {
        if (_npcTexts.TryGetValue(id, out var known))
        {
            then(known);
            return;
        }

        if (!_waitingNpcTexts.TryGetValue(id, out var waiting))
        {
            _waitingNpcTexts[id] = waiting = [];
            _ = SendQuietly(Opcodes.CMSG_NPC_TEXT_QUERY, new PacketWriter().U32(id).U64(0).ToArray());
        }

        waiting.Add(then);
    }

    private void ReadNpcText(byte[] data)
    {
        var r = new PacketReader(data);
        var id = r.U32();
        var text = "";
        for (var i = 0; i < 8 && r.Remaining > 0; i++)
        {
            r.F32(); // probability
            var a = r.CString();
            var b = r.CString();
            r.U32(); // language
            r.Skip(6 * 4); // emotes
            if (text.Length == 0)
            {
                text = a.Length > 0 ? a : b;
            }
        }

        text = FillIn(StripCodes(text));
        _npcTexts[id] = text;
        if (_waitingNpcTexts.Remove(id, out var waiting))
        {
            foreach (var then in waiting)
            {
                then(text);
            }
        }
    }

    /// <summary>The game's text codes: $N for the character's name, $B for a line break, $C and $R for class and race, $g for he/she choices.</summary>
    private string FillIn(string text)
    {
        var b = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '$' && i + 1 < text.Length)
            {
                var code = char.ToUpperInvariant(text[i + 1]);
                switch (code)
                {
                    case 'N':
                        b.Append(Character.Name);
                        i++;
                        continue;
                    case 'B':
                        b.Append('\n');
                        i++;
                        continue;
                    case 'C':
                        b.Append(CharacterRules.ClassName(Character.Class));
                        i++;
                        continue;
                    case 'R':
                        b.Append(CharacterRules.RaceName(Character.Race));
                        i++;
                        continue;
                    case 'G':
                    {
                        var end = text.IndexOf(';', i);
                        var colon = text.IndexOf(':', i);
                        if (end > 0 && colon > 0 && colon < end)
                        {
                            var male = text[(i + 2)..colon].Trim();
                            var female = text[(colon + 1)..end].Trim();
                            b.Append(Character.Sex == 0 ? male : female);
                            i = end;
                            continue;
                        }

                        break;
                    }
                }
            }

            b.Append(text[i]);
        }

        return b.ToString();
    }

    private void ReadQuestList(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        var title = FillIn(StripCodes(r.CString()));
        r.U32();
        r.U32();
        var count = r.U8();
        var quests = ReadQuestItems(r, count);
        Events.Enqueue(new GossipShown(npc, 0, title, [], quests));
    }

    private void ReadQuestDetails(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        r.U64(); // shared by
        var quest = r.U32();
        var title = r.CString();
        var details = r.CString();
        var objectives = r.CString();
        r.U8(); // auto finish
        var flags = r.U32();
        r.U32(); // suggested players
        r.U8();
        var choices = new List<QuestRewardItem>();
        var rewards = new List<QuestRewardItem>();
        if ((flags & 0x200) == 0) // rewards not hidden
        {
            choices = ReadRewardItems(r);
            rewards = ReadRewardItems(r);
        }

        Events.Enqueue(new QuestShown(npc, quest, QuestStep.Details, StripCodes(title), FillIn(StripCodes(details)), FillIn(StripCodes(objectives)), true, choices, rewards));
    }

    private List<QuestRewardItem> ReadRewardItems(PacketReader r)
    {
        var list = new List<QuestRewardItem>();
        var count = r.U32();
        for (uint i = 0; i < count; i++)
        {
            var item = r.U32();
            var amount = r.U32();
            r.U32(); // display
            list.Add(new QuestRewardItem(item, amount));
            AskItem(item);
        }

        return list;
    }

    private void ReadQuestRequestItems(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        var quest = r.U32();
        var title = r.CString();
        var text = r.CString();
        r.U32();
        r.U32(); // emote
        r.U32(); // close on cancel
        r.U32(); // flags
        r.U32(); // players
        r.U32(); // money
        var count = r.U32();
        var needed = new List<QuestRewardItem>();
        for (uint i = 0; i < count; i++)
        {
            var item = r.U32();
            var amount = r.U32();
            r.U32();
            needed.Add(new QuestRewardItem(item, amount));
            AskItem(item);
        }

        var completable = r.U32() == 3;
        Events.Enqueue(new QuestShown(npc, quest, QuestStep.RequestItems, StripCodes(title), FillIn(StripCodes(text)), "", completable, [], needed));
    }

    private void ReadQuestOfferReward(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        var quest = r.U32();
        var title = r.CString();
        var text = r.CString();
        r.U8(); // auto finish
        r.U32(); // flags
        r.U32(); // players
        var emotes = r.U32();
        r.Skip((int)emotes * 8);
        var choices = ReadRewardItems(r);
        var rewards = ReadRewardItems(r);
        Events.Enqueue(new QuestShown(npc, quest, QuestStep.OfferReward, StripCodes(title), FillIn(StripCodes(text)), "", true, choices, rewards));
    }

    private void ReadVendor(byte[] data)
    {
        var r = new PacketReader(data);
        var npc = r.U64();
        var count = r.U8();
        var items = new List<VendorItem>();
        for (var i = 0; i < count; i++)
        {
            var slot = r.U32();
            var item = r.U32();
            r.U32(); // display
            var stock = (int)r.U32();
            var price = r.U32();
            r.U32(); // durability
            var buyCount = r.U32();
            r.U32(); // extended cost
            items.Add(new VendorItem(slot, item, stock, price, buyCount));
            AskItem(item);
        }

        if (count == 0)
        {
            Events.Enqueue(new ScreenMessage("This vendor has nothing to sell.", true));
            return;
        }

        Events.Enqueue(new VendorShown(npc, items));
    }

    /// <summary>Takes everything in a corpse or chest at once, and closes it.</summary>
    private void TakeLoot(byte[] data)
    {
        var r = new PacketReader(data);
        var guid = r.U64();
        var type = r.U8();
        if (type == 0)
        {
            var error = r.Remaining > 0 ? r.U8() : (byte)0;
            Events.Enqueue(new ScreenMessage(error == 4 ? "You are too far away!" : "You can't loot that.", true));
            return;
        }

        var gold = r.U32();
        var count = r.U8();
        if (gold > 0)
        {
            _ = SendQuietly(Opcodes.CMSG_LOOT_MONEY, []);
            Notice($"You loot {Money(gold)}.", false);
        }

        for (var i = 0; i < count; i++)
        {
            var slot = r.U8();
            var item = r.U32();
            r.U32(); // count
            r.U32(); // display
            r.U32();
            r.U32();
            var slotType = r.U8();
            AskItem(item);
            if (slotType is 0 or 3) // allowed, or owner
            {
                _ = SendQuietly(Opcodes.CMSG_AUTOSTORE_LOOT_ITEM, new PacketWriter().U8(slot).ToArray());
            }
        }

        _ = SendQuietly(Opcodes.CMSG_LOOT_RELEASE, new PacketWriter().U64(guid).ToArray());
    }

    /// <summary>Copper as gold, silver and copper.</summary>
    public static string Money(uint copper)
    {
        var parts = new List<string>();
        if (copper >= 10000)
        {
            parts.Add($"{copper / 10000}g");
        }

        if (copper % 10000 >= 100)
        {
            parts.Add($"{copper % 10000 / 100}s");
        }

        if (copper % 100 > 0 || parts.Count == 0)
        {
            parts.Add($"{copper % 100}c");
        }

        return string.Join(" ", parts);
    }

    private void ReadSwing(byte[] data)
    {
        var r = new PacketReader(data);
        var hitInfo = r.U32();
        var attacker = r.PackedGuid();
        var target = r.PackedGuid();
        var damage = r.U32();
        var missed = (hitInfo & 0x10) != 0 || damage == 0; // miss
        Events.Enqueue(new SwingHappened(attacker, target, damage, missed));
        string NameFor(ulong guid) => guid == Character.Guid ? "You"
            : _objects.Objects.TryGetValue(guid, out var o) && NameOf(o) is { Length: > 0 } n ? n : "Something";
        if (attacker == Character.Guid || target == Character.Guid)
        {
            var who = NameFor(attacker);
            var whom = target == Character.Guid ? "you" : NameFor(target);
            var line = missed ? $"{who} miss{(attacker == Character.Guid ? "" : "es")} {whom}." : $"{who} hit{(attacker == Character.Guid ? "" : "s")} {whom} for {damage}.";
            Events.Enqueue(new ChatReceived(new ChatLine(ChatKind.Combat, "", line)));
        }
    }

    /// <summary>Why a quest giver won't give a quest, by AzerothCore's <c>QuestFailedReason</c>.</summary>
    internal static string QuestInvalid(uint code) => code switch
    {
        0 => "You don't meet the requirements for that quest.",
        1 => "You are not high enough level for that quest.",
        6 => "That quest is for another race.",
        7 => "You have completed that quest.",
        12 => "You can only be on one timed quest at a time.",
        13 or 18 => "You are already on that quest.",
        16 => "That quest needs another expansion.",
        21 => "You don't have the items that quest needs.",
        23 => "You don't have enough money for that quest.",
        26 => "You have done all the daily quests you can today.",
        29 => "You have completed that daily quest today.",
        _ => $"You can't take that quest (reason {code}).",
    };

    private static string InventoryError(byte code) => code switch
    {
        1 => "You can't equip that yet.",
        2 => "Your skill isn't high enough.",
        3 => "That item doesn't go in that slot.",
        4 => "That bag is full.",
        11 => "You can't equip that with what you have.",
        21 => "You can't carry any more of those.",
        23 => "Your inventory is full.",
        26 => "You can't do that while dead.",
        28 => "You can't do that right now.",
        48 => "You don't have enough money.",
        _ => $"You can't do that (error {code}).",
    };

    /// <summary>A unit, ours or another, changed: the HUD hears its health and the like.</summary>
    private void OnValuesChanged(TrackedObject obj)
    {
        if (obj.Guid == Character.Guid)
        {
            AnnounceSelf(obj);
            return;
        }

        if (obj.IsUnit && (_announced.Contains(obj.Guid) || obj.Type == ObjectType.Player))
        {
            Events.Enqueue(new UnitChanged(UnitStats.From(obj, NameOf(obj))));
        }

        if (obj.Type == ObjectType.Player && _announced.Contains(obj.Guid)
            && PlayerLook.From(obj) is var look && !look.SameAs(_playerLooks.GetValueOrDefault(obj.Guid)))
        {
            _playerLooks[obj.Guid] = look;
            Events.Enqueue(new PlayerLookChanged(obj.Guid, look));
        }

        if (obj.Type is ObjectType.Item or ObjectType.Container)
        {
            _inventoryDirty = true;
        }

        // A door opening or a chest's state: the game draws it again.
        if (obj.Type == ObjectType.GameObject && _announced.Contains(obj.Guid))
        {
            Events.Enqueue(new GameObjectAppeared(GameObjectInfoOf(obj)));
        }
    }

    private void AnnounceSelf(TrackedObject obj)
    {
        var stats = new PlayerStats(
            UnitStats.From(obj, Character.Name),
            obj.Field(UpdateFields.PlayerXp),
            obj.Field(UpdateFields.PlayerNextLevelXp),
            obj.Field(UpdateFields.PlayerCoinage),
            obj.Field(UpdateFields.PlayerFlags))
        {
            Sheet = CharacterSheet.From(obj),
            Look = PlayerLook.From(obj),
        };

        // Putting something on or taking it off changes the body, as another player's does.
        if (_self?.Look is { } before && !stats.Look.SameAs(before))
        {
            Events.Enqueue(new PlayerLookChanged(obj.Guid, stats.Look));
        }

        _self = stats;
        Events.Enqueue(new SelfChanged(stats));

        // A ghost needs to know where its body lies, which may be too far for the server to show.
        if (stats.IsGhost && !_askedCorpse)
        {
            _askedCorpse = true;
            _ = SendQuietly(MsgCorpseQuery, []);
        }
        else if (!stats.IsGhost)
        {
            _askedCorpse = false;
        }
        _inventoryDirty = true;
    }

    /// <summary>What the player carries, read from their own fields and their bags', when anything changed.</summary>
    private void SendInventoryIfChanged()
    {
        if (!_inventoryDirty || !_objects.Objects.TryGetValue(Character.Guid, out var self))
        {
            return;
        }

        _inventoryDirty = false;
        var items = new List<OwnedItem>();
        var bags = new List<(byte, int)>();
        for (byte slot = 0; slot < 39; slot++)
        {
            var guid = self.Field64(UpdateFields.PlayerInvSlotHead + (slot * 2));
            if (guid == 0 || !_objects.Objects.TryGetValue(guid, out var item))
            {
                continue;
            }

            items.Add(new OwnedItem(guid, item.Entry, Math.Max(item.Field(UpdateFields.ItemStackCount), 1), 255, slot));
            AskItem(item.Entry);
            if (slot is >= 19 and < 23 && item.Type == ObjectType.Container)
            {
                var slots = (int)item.Field(UpdateFields.ContainerNumSlots);
                bags.Add((slot, slots));
                for (var i = 0; i < slots; i++)
                {
                    var inside = item.Field64(UpdateFields.ContainerSlot1 + (i * 2));
                    if (inside != 0 && _objects.Objects.TryGetValue(inside, out var content))
                    {
                        items.Add(new OwnedItem(inside, content.Entry, Math.Max(content.Field(UpdateFields.ItemStackCount), 1), slot, (byte)i));
                        AskItem(content.Entry);
                    }
                }
            }
        }

        Events.Enqueue(new InventoryChanged(items, bags));
    }
}
