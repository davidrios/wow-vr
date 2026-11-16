namespace WowVR.Net;

/// <summary>
/// WowVR's VR protocol, for seeing each other's VR bodies on a server with mod-wowvr
/// (<c>docs/vr-multiplayer-protocol.md</c>). Nothing of it is sent until the server announces
/// itself, since a stock server disconnects a client for an opcode it doesn't know.
/// </summary>
public sealed partial class RealmSession
{
    private const uint AddonLanguage = 0xFFFFFFFF;
    private const string VrAnnouncement = "WOWVR\t";
    private const uint SendsPoses = 1;
    private const uint WantsPoses = 2;

    private bool _vrAnnounced;

    /// <summary>What the server agreed to, once it has; null on a server without mod-wowvr.</summary>
    public VrReady? Vr { get; private set; }

    /// <summary>A new world: the server announces itself again on each login.</summary>
    private void ResetVr()
    {
        _vrAnnounced = false;
        Vr = null;
    }

    /// <summary>
    /// The server's announcement: an addon whisper from the player themselves, <c>WOWVR\t</c> and its
    /// version. Answered with a hello, asking to send poses and to get other players'. True when it
    /// was the announcement, which isn't chat.
    /// </summary>
    private bool ReadVrAnnouncement(ChatKind kind, uint language, ulong sender, string text)
    {
        if (kind != ChatKind.Whisper || language != AddonLanguage || sender != Character.Guid || !text.StartsWith(VrAnnouncement, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_vrAnnounced && ushort.TryParse(text.AsSpan(VrAnnouncement.Length), out var version) && version >= 1)
        {
            _vrAnnounced = true;
            var w = new PacketWriter().U16((ushort)Math.Min((int)version, VrPoseMessage.Version)).U32(SendsPoses | WantsPoses);
            _ = SendQuietly(Opcodes.CMSG_WOWVR_HELLO, w.ToArray());
        }

        return true;
    }

    /// <summary>Sends a pose, once the server has agreed; otherwise nothing.</summary>
    public void SendVrPose(VrPoseMessage pose)
    {
        if (Vr is not null)
        {
            _ = SendQuietly(Opcodes.CMSG_WOWVR_POSE, pose.Write());
        }
    }

    /// <summary>Says the player went into VR or out of it, once the server has agreed.</summary>
    public void SendVrState(VrState state)
    {
        if (Vr is not null)
        {
            _ = SendQuietly(Opcodes.CMSG_WOWVR_STATE, [(byte)state]);
        }
    }

    private void ReadVr(WorldPacket p)
    {
        var r = new PacketReader(p.Data);
        switch (p.Opcode)
        {
            case Opcodes.SMSG_WOWVR_HELLO:
                var ready = new VrReady(r.U16(), r.U32(), r.U16(), r.U16(), r.U16());
                Vr = ready;
                Events.Enqueue(ready);
                break;
            case Opcodes.SMSG_WOWVR_STATE:
                Events.Enqueue(new VrStateReceived(r.PackedGuid(), (VrState)r.U8()));
                break;
            case Opcodes.SMSG_WOWVR_POSE:
                var guid = r.PackedGuid();
                if (VrPoseMessage.Read(p.Data.AsSpan(r.Position)) is { } pose)
                {
                    Events.Enqueue(new VrPoseReceived(guid, pose));
                }

                break;
        }
    }
}
