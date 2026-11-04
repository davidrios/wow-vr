using System.Numerics;

namespace WowVR.Net;

/// <summary>
/// A unit's movement as movement packets carry it, in WoW coordinates: where it is, which way it
/// faces and how it's moving, and what it rides. Players send their own and get each other's.
/// </summary>
public record struct MovementInfo
{
    public MoveFlags Flags;
    public ushort Flags2;
    public uint Time;
    public Vector3 Position;
    public float Orientation;
    public ulong TransportGuid;
    public Vector3 TransportOffset;
    public float TransportOrientation;
    public uint TransportTime;
    public float Pitch;
    public uint FallTime;

    /// <summary>A jump's start: how fast up, and how fast and which way along the ground.</summary>
    public float JumpVerticalSpeed;
    public float JumpSin;
    public float JumpCos;
    public float JumpHorizontalSpeed;

    private const ushort InterpolatedMovement = 0x400;
    private const ushort AlwaysAllowPitching = 0x20;

    public static MovementInfo Read(PacketReader r)
    {
        var info = new MovementInfo
        {
            Flags = (MoveFlags)r.U32(),
            Flags2 = r.U16(),
            Time = r.U32(),
            Position = new Vector3(r.F32(), r.F32(), r.F32()),
            Orientation = r.F32(),
        };
        if ((info.Flags & MoveFlags.OnTransport) != 0)
        {
            info.TransportGuid = r.PackedGuid();
            info.TransportOffset = new Vector3(r.F32(), r.F32(), r.F32());
            info.TransportOrientation = r.F32();
            info.TransportTime = r.U32();
            r.U8(); // seat
            if ((info.Flags2 & InterpolatedMovement) != 0)
            {
                r.U32();
            }
        }

        if ((info.Flags & (MoveFlags.Swimming | MoveFlags.Flying)) != 0 || (info.Flags2 & AlwaysAllowPitching) != 0)
        {
            info.Pitch = r.F32();
        }

        info.FallTime = r.U32();
        if ((info.Flags & MoveFlags.Falling) != 0)
        {
            info.JumpVerticalSpeed = r.F32();
            info.JumpSin = r.F32();
            info.JumpCos = r.F32();
            info.JumpHorizontalSpeed = r.F32();
        }

        if ((info.Flags & (MoveFlags)0x4000000) != 0) // spline elevation
        {
            r.F32();
        }

        return info;
    }

    public readonly void Write(PacketWriter w)
    {
        w.U32((uint)Flags).U16(Flags2).U32(Time)
            .F32(Position.X).F32(Position.Y).F32(Position.Z).F32(Orientation);
        if ((Flags & MoveFlags.OnTransport) != 0)
        {
            PacketWriter.PackedGuid(w, TransportGuid);
            w.F32(TransportOffset.X).F32(TransportOffset.Y).F32(TransportOffset.Z).F32(TransportOrientation)
                .U32(TransportTime).U8(0xFF);
        }

        if ((Flags & (MoveFlags.Swimming | MoveFlags.Flying)) != 0 || (Flags2 & AlwaysAllowPitching) != 0)
        {
            w.F32(Pitch);
        }

        w.U32(FallTime);
        if ((Flags & MoveFlags.Falling) != 0)
        {
            w.F32(JumpVerticalSpeed).F32(JumpSin).F32(JumpCos).F32(JumpHorizontalSpeed);
        }
    }
}
