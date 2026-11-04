using System.Numerics;
using Xunit;

namespace WowVR.Net.Tests;

public class PlayTests
{
    [Fact]
    public void MovementInfoReadsBackWhatItWrote()
    {
        var info = new MovementInfo
        {
            Flags = MoveFlags.Forward | MoveFlags.Swimming | MoveFlags.Falling,
            Time = 1234,
            Position = new Vector3(1, 2, 3),
            Orientation = 1.5f,
            Pitch = -0.25f,
            FallTime = 300,
            JumpVerticalSpeed = 7.9f,
            JumpCos = 1,
            JumpHorizontalSpeed = 7,
        };
        var w = new PacketWriter();
        info.Write(w);

        var read = MovementInfo.Read(new PacketReader(w.ToArray()));

        Assert.Equal(info, read);
    }

    [Fact]
    public void MovementInfoCarriesATransport()
    {
        var info = new MovementInfo
        {
            Flags = MoveFlags.OnTransport,
            Position = new Vector3(10, 20, 30),
            TransportGuid = 0x1FC0000000000123,
            TransportOffset = new Vector3(1, -2, 0.5f),
            TransportOrientation = 0.3f,
            TransportTime = 99,
        };
        var w = new PacketWriter();
        info.Write(w);

        var read = MovementInfo.Read(new PacketReader(w.ToArray()));

        Assert.Equal(info.TransportGuid, read.TransportGuid);
        Assert.Equal(info.TransportOffset, read.TransportOffset);
        Assert.Equal(info.TransportTime, read.TransportTime);
    }

    /// <summary>A button packs its id in 24 bits and its kind above: Fireball, then a Hearthstone.</summary>
    [Fact]
    public void ActionButtonsPackAsTheServerSavesThem()
    {
        Assert.Equal(new ActionButton(133, ActionKind.Spell), ActionButton.Unpack(133));
        Assert.Equal(new ActionButton(6948, ActionKind.Item), ActionButton.Unpack(0x80000000 | 6948));
        Assert.Equal(0x80000000u | 6948, new ActionButton(6948, ActionKind.Item).Packed);
        Assert.True(ActionButton.Unpack(0).IsEmpty);
    }

    [Theory]
    [InlineData(97, "Out of range")]
    [InlineData(67, "Spell is not ready yet")]
    [InlineData(60, "Not here")]
    [InlineData(187, "Unknown")]
    public void CastFailuresReadAsSentences(byte reason, string text) => Assert.Equal(text, CastResults.Describe(reason));

    [Fact]
    public void QuietFailuresSayNothing() => Assert.Null(CastResults.Describe(CastResults.DontReport));

    /// <summary>An undead woman with her helm hidden, a sword and a shirt, as her update fields say.</summary>
    [Fact]
    public void PlayerLookReadsTheUpdateFields()
    {
        var obj = new TrackedObject(0x42, ObjectType.Player);
        obj.Fields[UpdateFields.UnitBytes0] = 5 | (1 << 8) | (1 << 16) | (1u << 24);
        obj.Fields[UpdateFields.PlayerBytes] = 3 | (4 << 8) | (5 << 16) | (6u << 24);
        obj.Fields[UpdateFields.PlayerBytes2] = 2 | (1 << 24);
        obj.Fields[UpdateFields.PlayerFlags] = 0x400;
        obj.Fields[UpdateFields.PlayerVisibleItem1 + (2 * 3)] = 45;
        obj.Fields[UpdateFields.PlayerVisibleItem1 + (2 * 3) + 1] = 999; // its enchantment
        obj.Fields[UpdateFields.PlayerVisibleItem1 + (2 * 15)] = 1899;

        var look = PlayerLook.From(obj);

        Assert.Equal((5, 1, 3, 4, 5, 6, 2), ((int)look.Race, (int)look.Sex, (int)look.Skin, (int)look.Face, (int)look.HairStyle, (int)look.HairColor, (int)look.FacialHair));
        Assert.True(look.HidesHelm);
        Assert.False(look.HidesCloak);
        Assert.Equal(45u, look.Items[3]);
        Assert.Equal(1899u, look.Items[15]);
        Assert.Equal(17, look.Items.Count(i => i == 0));
        Assert.True(look.SameAs(PlayerLook.From(obj)));

        obj.Fields[UpdateFields.PlayerVisibleItem1 + (2 * 15)] = 0;
        Assert.False(look.SameAs(PlayerLook.From(obj)));
    }

    [Theory]
    [InlineData(MoveFlags.None, MoveFlags.Forward, Opcodes.MSG_MOVE_START_FORWARD)]
    [InlineData(MoveFlags.Forward, MoveFlags.None, Opcodes.MSG_MOVE_STOP)]
    [InlineData(MoveFlags.None, MoveFlags.Backward, Opcodes.MSG_MOVE_START_BACKWARD)]
    [InlineData(MoveFlags.Forward, MoveFlags.Forward | MoveFlags.StrafeLeft, Opcodes.MSG_MOVE_START_STRAFE_LEFT)]
    [InlineData(MoveFlags.Forward | MoveFlags.StrafeLeft, MoveFlags.Forward, Opcodes.MSG_MOVE_STOP_STRAFE)]
    [InlineData(MoveFlags.StrafeRight, MoveFlags.None, Opcodes.MSG_MOVE_STOP)]
    [InlineData(MoveFlags.Forward, MoveFlags.Forward | MoveFlags.Swimming, Opcodes.MSG_MOVE_START_SWIM)]
    [InlineData(MoveFlags.Falling, MoveFlags.None, Opcodes.MSG_MOVE_FALL_LAND)]
    public void MovementChangesUseTheClientsOpcodes(MoveFlags before, MoveFlags after, ushort opcode) =>
        Assert.Equal(opcode, RealmSession.ChangeOpcode(before, after));

    [Theory]
    [InlineData("|cffff0000Red|r text", "Red text")]
    [InlineData("Loot: |cff9d9d9d|Hitem:2589:0:0:0:0:0:0:0|h[Linen Cloth]|h|r", "Loot: [Linen Cloth]")]
    [InlineData("a || b", "a | b")]
    [InlineData("plain", "plain")]
    public void ChatMarkupIsStripped(string text, string expected) =>
        Assert.Equal(expected, RealmSession.StripCodes(text));

    [Fact]
    public void RotationUnpacksToAUnitQuaternion()
    {
        // A quarter turn about Z, packed as the server packs it.
        var z = MathF.Sin(MathF.PI / 4);
        var packed = (long)(z * (1 << 20)) & 0x1FFFFF;

        var q = ObjectTracker.UnpackRotation(packed);

        Assert.Equal(0f, q.X, 3);
        Assert.Equal(0f, q.Y, 3);
        Assert.Equal(z, q.Z, 3);
        Assert.Equal(MathF.Cos(MathF.PI / 4), q.W, 3);
    }

    [Theory]
    [InlineData(0u, "0c")]
    [InlineData(178u, "1s 78c")]
    [InlineData(10000u, "1g")]
    [InlineData(123456u, "12g 34s 56c")]
    public void MoneyReadsAsGoldSilverAndCopper(uint copper, string expected) =>
        Assert.Equal(expected, RealmSession.Money(copper));

    [Theory]
    [InlineData(13u, "You are already on that quest.")]
    [InlineData(1u, "You are not high enough level for that quest.")]
    [InlineData(21u, "You don't have the items that quest needs.")]
    public void QuestRefusalsReadAsTheGameSays(uint code, string text) =>
        Assert.Equal(text, RealmSession.QuestInvalid(code));
}
