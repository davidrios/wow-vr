namespace WowVR.Net;

/// <summary>The 3.3.5a opcodes this client sends or reads, as AzerothCore numbers them.</summary>
public static class Opcodes
{
    public const ushort CMSG_CHAR_CREATE = 0x036;
    public const ushort CMSG_CHAR_ENUM = 0x037;
    public const ushort CMSG_CHAR_DELETE = 0x038;
    public const ushort SMSG_CHAR_CREATE = 0x03A;
    public const ushort SMSG_CHAR_DELETE = 0x03C;
    public const ushort SMSG_CHAR_ENUM = 0x03B;
    public const ushort CMSG_PLAYER_LOGIN = 0x03D;
    public const ushort SMSG_NEW_WORLD = 0x03E;
    public const ushort SMSG_TRANSFER_PENDING = 0x03F;
    public const ushort SMSG_CHARACTER_LOGIN_FAILED = 0x041;
    public const ushort SMSG_LOGIN_SETTIMESPEED = 0x042;
    public const ushort CMSG_LOGOUT_REQUEST = 0x04B;
    public const ushort SMSG_LOGOUT_RESPONSE = 0x04C;
    public const ushort SMSG_LOGOUT_COMPLETE = 0x04D;
    public const ushort CMSG_LOGOUT_CANCEL = 0x04E;
    public const ushort CMSG_NAME_QUERY = 0x050;
    public const ushort SMSG_NAME_QUERY_RESPONSE = 0x051;
    public const ushort CMSG_ITEM_QUERY_SINGLE = 0x056;
    public const ushort SMSG_ITEM_QUERY_SINGLE_RESPONSE = 0x058;
    public const ushort CMSG_GAMEOBJECT_QUERY = 0x05E;
    public const ushort SMSG_GAMEOBJECT_QUERY_RESPONSE = 0x05F;
    public const ushort CMSG_CREATURE_QUERY = 0x060;
    public const ushort SMSG_CREATURE_QUERY_RESPONSE = 0x061;
    public const ushort CMSG_MESSAGECHAT = 0x095;
    public const ushort SMSG_MESSAGECHAT = 0x096;
    public const ushort SMSG_UPDATE_OBJECT = 0x0A9;
    public const ushort SMSG_DESTROY_OBJECT = 0x0AA;
    public const ushort CMSG_GAMEOBJ_USE = 0x0B1;
    public const ushort CMSG_AREATRIGGER = 0x0B4;
    public const ushort MSG_MOVE_START_FORWARD = 0x0B5;
    public const ushort MSG_MOVE_START_BACKWARD = 0x0B6;
    public const ushort MSG_MOVE_STOP = 0x0B7;
    public const ushort MSG_MOVE_START_STRAFE_LEFT = 0x0B8;
    public const ushort MSG_MOVE_START_STRAFE_RIGHT = 0x0B9;
    public const ushort MSG_MOVE_STOP_STRAFE = 0x0BA;
    public const ushort MSG_MOVE_JUMP = 0x0BB;
    public const ushort MSG_MOVE_START_TURN_LEFT = 0x0BC;
    public const ushort MSG_MOVE_START_TURN_RIGHT = 0x0BD;
    public const ushort MSG_MOVE_STOP_TURN = 0x0BE;
    public const ushort MSG_MOVE_TELEPORT = 0x0C5;
    public const ushort MSG_MOVE_TELEPORT_ACK = 0x0C7;
    public const ushort MSG_MOVE_FALL_LAND = 0x0C9;
    public const ushort MSG_MOVE_START_SWIM = 0x0CA;
    public const ushort MSG_MOVE_STOP_SWIM = 0x0CB;
    public const ushort MSG_MOVE_SET_FACING = 0x0DA;
    public const ushort MSG_MOVE_WORLDPORT_ACK = 0x0DC;
    public const ushort SMSG_MONSTER_MOVE = 0x0DD;
    public const ushort SMSG_FORCE_RUN_SPEED_CHANGE = 0x0E2;
    public const ushort CMSG_FORCE_RUN_SPEED_CHANGE_ACK = 0x0E3;
    public const ushort SMSG_FORCE_RUN_BACK_SPEED_CHANGE = 0x0E4;
    public const ushort CMSG_FORCE_RUN_BACK_SPEED_CHANGE_ACK = 0x0E5;
    public const ushort SMSG_FORCE_SWIM_SPEED_CHANGE = 0x0E6;
    public const ushort CMSG_FORCE_SWIM_SPEED_CHANGE_ACK = 0x0E7;
    public const ushort SMSG_FORCE_MOVE_ROOT = 0x0E8;
    public const ushort CMSG_FORCE_MOVE_ROOT_ACK = 0x0E9;
    public const ushort SMSG_FORCE_MOVE_UNROOT = 0x0EA;
    public const ushort CMSG_FORCE_MOVE_UNROOT_ACK = 0x0EB;
    public const ushort MSG_MOVE_HEARTBEAT = 0x0EE;
    public const ushort CMSG_AUTOEQUIP_ITEM = 0x10A;
    public const ushort CMSG_SWAP_INV_ITEM = 0x10D;
    public const ushort SMSG_INVENTORY_CHANGE_FAILURE = 0x112;
    public const ushort CMSG_USE_ITEM = 0x0AB;
    public const ushort CMSG_SET_SELECTION = 0x13D;
    public const ushort CMSG_ATTACKSWING = 0x141;
    public const ushort CMSG_ATTACKSTOP = 0x142;
    public const ushort SMSG_ATTACKSTART = 0x143;
    public const ushort SMSG_ATTACKSTOP = 0x144;
    public const ushort SMSG_ATTACKSWING_NOTINRANGE = 0x145;
    public const ushort SMSG_ATTACKSWING_BADFACING = 0x146;
    public const ushort SMSG_ATTACKSWING_DEADTARGET = 0x148;
    public const ushort SMSG_ATTACKSWING_CANT_ATTACK = 0x149;
    public const ushort SMSG_ATTACKERSTATEUPDATE = 0x14A;
    public const ushort CMSG_REPOP_REQUEST = 0x15A;
    public const ushort SMSG_RESURRECT_REQUEST = 0x15B;
    public const ushort CMSG_RESURRECT_RESPONSE = 0x15C;
    public const ushort CMSG_LOOT = 0x15D;
    public const ushort CMSG_LOOT_MONEY = 0x15E;
    public const ushort CMSG_LOOT_RELEASE = 0x15F;
    public const ushort SMSG_LOOT_RESPONSE = 0x160;
    public const ushort SMSG_LOOT_RELEASE_RESPONSE = 0x161;
    public const ushort SMSG_LOOT_REMOVED = 0x162;
    public const ushort SMSG_ITEM_PUSH_RESULT = 0x166;
    public const ushort CMSG_AUTOSTORE_LOOT_ITEM = 0x108;
    public const ushort CMSG_GOSSIP_HELLO = 0x17B;
    public const ushort CMSG_GOSSIP_SELECT_OPTION = 0x17C;
    public const ushort SMSG_GOSSIP_MESSAGE = 0x17D;
    public const ushort SMSG_GOSSIP_COMPLETE = 0x17E;
    public const ushort CMSG_NPC_TEXT_QUERY = 0x17F;
    public const ushort SMSG_NPC_TEXT_UPDATE = 0x180;
    public const ushort CMSG_QUESTGIVER_HELLO = 0x184;
    public const ushort SMSG_QUESTGIVER_QUEST_LIST = 0x185;
    public const ushort CMSG_QUESTGIVER_QUERY_QUEST = 0x186;
    public const ushort SMSG_QUESTGIVER_QUEST_DETAILS = 0x188;
    public const ushort CMSG_QUESTGIVER_ACCEPT_QUEST = 0x189;
    public const ushort CMSG_QUESTGIVER_COMPLETE_QUEST = 0x18A;
    public const ushort SMSG_QUESTGIVER_REQUEST_ITEMS = 0x18B;
    public const ushort CMSG_QUESTGIVER_REQUEST_REWARD = 0x18C;
    public const ushort SMSG_QUESTGIVER_OFFER_REWARD = 0x18D;
    public const ushort CMSG_QUESTGIVER_CHOOSE_REWARD = 0x18E;
    public const ushort SMSG_QUESTGIVER_QUEST_COMPLETE = 0x191;
    public const ushort CMSG_LIST_INVENTORY = 0x19E;
    public const ushort SMSG_LIST_INVENTORY = 0x19F;
    public const ushort CMSG_SELL_ITEM = 0x1A0;
    public const ushort CMSG_BUY_ITEM = 0x1A2;
    public const ushort SMSG_BUY_FAILED = 0x1A5;
    public const ushort SMSG_NOTIFICATION = 0x1CB;
    public const ushort SMSG_LOG_XPGAIN = 0x1D0;
    public const ushort CMSG_RECLAIM_CORPSE = 0x1D2;
    public const ushort SMSG_LEVELUP_INFO = 0x1D4;
    public const ushort CMSG_PING = 0x1DC;
    public const ushort SMSG_AUTH_CHALLENGE = 0x1EC;
    public const ushort CMSG_AUTH_SESSION = 0x1ED;
    public const ushort SMSG_AUTH_RESPONSE = 0x1EE;
    public const ushort SMSG_COMPRESSED_UPDATE_OBJECT = 0x1F6;
    public const ushort CMSG_SPIRIT_HEALER_ACTIVATE = 0x21C;
    public const ushort SMSG_SPIRIT_HEALER_CONFIRM = 0x222;
    public const ushort SMSG_LOGIN_VERIFY_WORLD = 0x236;
    public const ushort CMSG_SET_ACTIVE_MOVER = 0x26A;
    public const ushort SMSG_SERVER_MESSAGE = 0x291;
    public const ushort SMSG_CHAT_PLAYER_NOT_FOUND = 0x2A9;
    public const ushort SMSG_AREA_TRIGGER_MESSAGE = 0x2B8;
    public const ushort SMSG_FORCE_WALK_SPEED_CHANGE = 0x2DA;
    public const ushort CMSG_FORCE_WALK_SPEED_CHANGE_ACK = 0x2DB;
    public const ushort SMSG_FORCE_SWIM_BACK_SPEED_CHANGE = 0x2DC;
    public const ushort CMSG_FORCE_SWIM_BACK_SPEED_CHANGE_ACK = 0x2DD;
    public const ushort SMSG_FORCE_TURN_RATE_CHANGE = 0x2DE;
    public const ushort CMSG_FORCE_TURN_RATE_CHANGE_ACK = 0x2DF;
    public const ushort SMSG_COMPRESSED_MOVES = 0x2FB;
    public const ushort SMSG_MOTD = 0x33D;
    public const ushort SMSG_MOVE_SET_CAN_FLY = 0x343;
    public const ushort SMSG_MOVE_UNSET_CAN_FLY = 0x344;
    public const ushort CMSG_MOVE_SET_CAN_FLY_ACK = 0x345;
    public const ushort SMSG_FORCE_FLIGHT_SPEED_CHANGE = 0x381;
    public const ushort CMSG_FORCE_FLIGHT_SPEED_CHANGE_ACK = 0x382;
    public const ushort SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE = 0x383;
    public const ushort CMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK = 0x384;
    public const ushort SMSG_TIME_SYNC_REQ = 0x390;
    public const ushort CMSG_TIME_SYNC_RESP = 0x391;
    public const ushort SMSG_GM_MESSAGECHAT = 0x3B3;
}

/// <summary>Indexes into an object's update fields, in 32-bit words, for 3.3.5a.</summary>
public static class UpdateFields
{
    public const int ObjectEntry = 0x03;
    public const int ObjectScale = 0x04;
    public const int ObjectEnd = 0x06;

    public const int ItemStackCount = ObjectEnd + 0x08;
    public const int ItemEnd = ObjectEnd + 0x3A;
    public const int ContainerNumSlots = ItemEnd;
    public const int ContainerSlot1 = ItemEnd + 0x02;

    public const int UnitTarget = ObjectEnd + 0x0C;
    public const int UnitBytes0 = ObjectEnd + 0x11;
    public const int UnitHealth = ObjectEnd + 0x12;
    public const int UnitPower1 = ObjectEnd + 0x13;
    public const int UnitMaxHealth = ObjectEnd + 0x1A;
    public const int UnitMaxPower1 = ObjectEnd + 0x1B;
    public const int UnitLevel = ObjectEnd + 0x30;
    public const int UnitFaction = ObjectEnd + 0x31;
    public const int UnitVirtualItems = ObjectEnd + 0x32;
    public const int UnitFlags = ObjectEnd + 0x35;
    public const int UnitDisplayId = ObjectEnd + 0x3D;
    public const int UnitMountDisplayId = ObjectEnd + 0x3F;
    public const int UnitBytes1 = ObjectEnd + 0x44;
    public const int UnitDynamicFlags = ObjectEnd + 0x49;
    public const int UnitNpcFlags = ObjectEnd + 0x4C;
    public const int UnitNpcEmoteState = ObjectEnd + 0x4D;
    public const int UnitEnd = ObjectEnd + 0x8E;

    public const int PlayerFlags = UnitEnd + 0x02;
    public const int PlayerBytes = UnitEnd + 0x05;
    public const int PlayerBytes2 = UnitEnd + 0x06;
    public const int PlayerBytes3 = UnitEnd + 0x07;
    public const int PlayerVisibleItem1 = UnitEnd + 0x87;
    public const int PlayerInvSlotHead = UnitEnd + 0xB0;
    public const int PlayerPackSlot1 = UnitEnd + 0xDE;
    public const int PlayerXp = UnitEnd + 0x1E6;
    public const int PlayerNextLevelXp = UnitEnd + 0x1E7;
    public const int PlayerCoinage = UnitEnd + 0x3FE;

    public const int GameObjectDisplayId = ObjectEnd + 0x02;
    public const int GameObjectFlags = ObjectEnd + 0x03;
    public const int GameObjectRotation = ObjectEnd + 0x04;
    public const int GameObjectDynamic = ObjectEnd + 0x08;
    public const int GameObjectFaction = ObjectEnd + 0x09;
    public const int GameObjectBytes1 = ObjectEnd + 0x0B;

    public const int CorpseOwner = ObjectEnd;
}

public enum ObjectType : byte
{
    Object,
    Item,
    Container,
    Unit,
    Player,
    GameObject,
    DynamicObject,
    Corpse,
}

/// <summary>Movement flags in a movement block.</summary>
[Flags]
public enum MoveFlags : uint
{
    None = 0,
    Forward = 0x1,
    Backward = 0x2,
    StrafeLeft = 0x4,
    StrafeRight = 0x8,
    TurnLeft = 0x10,
    TurnRight = 0x20,
    Walking = 0x100,
    OnTransport = 0x200,
    DisableGravity = 0x400,
    Root = 0x800,
    Falling = 0x1000,
    FallingFar = 0x2000,
    Swimming = 0x200000,
    Ascending = 0x400000,
    Descending = 0x800000,
    CanFly = 0x1000000,
    Flying = 0x2000000,
    SplineEnabled = 0x8000000,
    WaterWalking = 0x10000000,
    Hover = 0x40000000,
    Moving = Forward | Backward | StrafeLeft | StrafeRight,
}
