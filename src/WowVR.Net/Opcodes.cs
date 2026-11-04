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
    public const ushort SMSG_CHARACTER_LOGIN_FAILED = 0x041;
    public const ushort SMSG_LOGIN_SETTIMESPEED = 0x042;
    public const ushort CMSG_LOGOUT_REQUEST = 0x04B;
    public const ushort SMSG_LOGOUT_RESPONSE = 0x04C;
    public const ushort SMSG_LOGOUT_COMPLETE = 0x04D;
    public const ushort CMSG_LOGOUT_CANCEL = 0x04E;
    public const ushort CMSG_NAME_QUERY = 0x050;
    public const ushort SMSG_NAME_QUERY_RESPONSE = 0x051;
    public const ushort CMSG_GAMEOBJECT_QUERY = 0x05E;
    public const ushort SMSG_GAMEOBJECT_QUERY_RESPONSE = 0x05F;
    public const ushort CMSG_CREATURE_QUERY = 0x060;
    public const ushort SMSG_CREATURE_QUERY_RESPONSE = 0x061;
    public const ushort SMSG_UPDATE_OBJECT = 0x0A9;
    public const ushort SMSG_DESTROY_OBJECT = 0x0AA;
    public const ushort MSG_MOVE_TELEPORT_ACK = 0x0C7;
    public const ushort SMSG_MONSTER_MOVE = 0x0DD;
    public const ushort MSG_MOVE_HEARTBEAT = 0x0EE;
    public const ushort CMSG_PING = 0x1DC;
    public const ushort SMSG_AUTH_CHALLENGE = 0x1EC;
    public const ushort CMSG_AUTH_SESSION = 0x1ED;
    public const ushort SMSG_AUTH_RESPONSE = 0x1EE;
    public const ushort SMSG_COMPRESSED_UPDATE_OBJECT = 0x1F6;
    public const ushort SMSG_LOGIN_VERIFY_WORLD = 0x236;
    public const ushort SMSG_COMPRESSED_MOVES = 0x2FB;
    public const ushort SMSG_TIME_SYNC_REQ = 0x390;
    public const ushort CMSG_TIME_SYNC_RESP = 0x391;
}

/// <summary>Indexes into an object's update fields, in 32-bit words.</summary>
public static class UpdateFields
{
    public const int ObjectEntry = 0x03;
    public const int ObjectScale = 0x04;
    public const int ObjectEnd = 0x06;
    public const int UnitLevel = ObjectEnd + 0x30;
    public const int UnitVirtualItems = ObjectEnd + 0x32;
    public const int UnitFlags = ObjectEnd + 0x35;
    public const int UnitDisplayId = ObjectEnd + 0x3D;
    public const int UnitMountDisplayId = ObjectEnd + 0x3F;
    public const int UnitNpcEmoteState = ObjectEnd + 0x4D;
    public const int GameObjectDisplayId = ObjectEnd + 0x02;
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
