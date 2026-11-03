using System.IO.Compression;
using System.Numerics;

namespace WowVR.Net;

/// <summary>One object the server has told us about.</summary>
public sealed class TrackedObject(ulong guid, ObjectType type)
{
    public ulong Guid { get; } = guid;

    public ObjectType Type { get; } = type;

    public Dictionary<int, uint> Fields { get; } = [];

    /// <summary>WoW coordinates.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Radians turning left from north.</summary>
    public float Orientation { get; set; }

    public float WalkSpeed { get; set; } = 2.5f;

    public float RunSpeed { get; set; } = 7f;

    /// <summary>What it's doing now. Standing where it is, until the server says it's moving.</summary>
    public MovePath? Path { get; set; }

    /// <summary>How far along <see cref="Path"/> it already was when the server said, in seconds.</summary>
    public double PathElapsed { get; set; }

    public uint Field(int index) => Fields.GetValueOrDefault(index);

    public uint Entry => Field(UpdateFields.ObjectEntry);

    public float Scale => Fields.TryGetValue(UpdateFields.ObjectScale, out var bits) ? BitConverter.UInt32BitsToSingle(bits) : 1f;

    public uint DisplayId => Type == ObjectType.GameObject ? Field(UpdateFields.GameObjectDisplayId) : Field(UpdateFields.UnitDisplayId);
}

/// <summary>
/// Reads the object update and movement packets into what's around the player, and says
/// what changed. It keeps no clock: paths carry how far along they already are.
/// </summary>
public sealed class ObjectTracker
{
    private const uint SplineFinalPoint = 0x8000;
    private const uint SplineFinalTarget = 0x10000;
    private const uint SplineFinalAngle = 0x20000;
    private const uint SplineCatmullRom = 0x40000;
    private const uint SplineFlying = 0x2000;
    private const uint SplineCyclic = 0x80000;
    private const uint SplineAnimation = 0x200000;
    private const uint SplineParabolic = 0x800;

    public Dictionary<ulong, TrackedObject> Objects { get; } = [];

    public event Action<TrackedObject>? Created;

    public event Action<TrackedObject>? Moved;

    public event Action<TrackedObject>? Removed;

    /// <summary>Blocks that couldn't be read, which stop the rest of their packet being read.</summary>
    public int Errors { get; private set; }

    public string? LastError { get; private set; }

    public void ReadUpdate(byte[] data)
    {
        var r = new PacketReader(data);
        var blocks = r.U32();
        for (uint b = 0; b < blocks; b++)
        {
            try
            {
                ReadBlock(r);
            }
            catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
            {
                Errors++;
                LastError = $"update block {b + 1} of {blocks}: {e.Message}";
                return;
            }
        }
    }

    public void ReadCompressedUpdate(byte[] data) => ReadUpdate(Inflate(data));

    public void ReadDestroy(byte[] data)
    {
        var guid = new PacketReader(data).U64();
        if (Objects.Remove(guid, out var gone))
        {
            Removed?.Invoke(gone);
        }
    }

    public void ReadMonsterMove(byte[] data) => ReadMonsterMove(new PacketReader(data));

    /// <summary>Several monster moves in one zlib stream, each with a byte of size and two of opcode.</summary>
    public void ReadCompressedMoves(byte[] data)
    {
        var inner = Inflate(data);
        var pos = 0;
        while (pos + 3 <= inner.Length)
        {
            int size = inner[pos];
            var opcode = (ushort)(inner[pos + 1] | (inner[pos + 2] << 8));
            if (opcode == Opcodes.SMSG_MONSTER_MOVE && size >= 2)
            {
                ReadMonsterMove(new PacketReader(inner, pos + 3, size - 2));
            }

            pos += 1 + size;
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        var size = (int)new PacketReader(data).U32();
        using var z = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
        var output = new byte[size];
        z.ReadExactly(output);
        return output;
    }

    private void ReadBlock(PacketReader r)
    {
        var updateType = r.U8();
        switch (updateType)
        {
            case 0: // values
            {
                var obj = Objects.GetValueOrDefault(r.PackedGuid());
                ReadValues(r, obj);
                break;
            }
            case 1: // movement
            {
                var guid = r.PackedGuid();
                var obj = Objects.GetValueOrDefault(guid) ?? new TrackedObject(guid, ObjectType.Object);
                ReadMovement(r, obj);
                if (Objects.ContainsKey(guid))
                {
                    Moved?.Invoke(obj);
                }

                break;
            }
            case 2: // create
            case 3: // create, for one that was there already
            {
                var guid = r.PackedGuid();
                var obj = new TrackedObject(guid, (ObjectType)r.U8());
                ReadMovement(r, obj);
                ReadValues(r, obj);
                if (Objects.Remove(guid, out var old))
                {
                    Removed?.Invoke(old);
                }

                Objects[guid] = obj;
                Created?.Invoke(obj);
                break;
            }
            case 4: // out of range
            case 5: // near objects
            {
                var count = r.U32();
                for (uint i = 0; i < count; i++)
                {
                    var guid = r.PackedGuid();
                    if (updateType == 4 && Objects.Remove(guid, out var gone))
                    {
                        Removed?.Invoke(gone);
                    }
                }

                break;
            }
            default:
                throw new InvalidDataException($"unknown update type {updateType}");
        }
    }

    private static void ReadValues(PacketReader r, TrackedObject? obj)
    {
        int maskBlocks = r.U8();
        Span<uint> mask = stackalloc uint[maskBlocks];
        for (var i = 0; i < maskBlocks; i++)
        {
            mask[i] = r.U32();
        }

        for (var i = 0; i < maskBlocks * 32; i++)
        {
            if ((mask[i / 32] & (1u << (i % 32))) != 0)
            {
                var value = r.U32();
                obj?.Fields[i] = value;
            }
        }
    }

    private static void ReadMovement(PacketReader r, TrackedObject obj)
    {
        var flags = r.U16();
        if ((flags & 0x20) != 0) // living
        {
            var moveFlags = r.U32();
            var moveFlags2 = r.U16();
            r.U32(); // time
            obj.Position = Vec(r);
            obj.Orientation = r.F32();
            if ((moveFlags & 0x200) != 0) // on a transport
            {
                r.PackedGuid();
                r.Skip(16);
                r.U32();
                r.U8();
                if ((moveFlags2 & 0x400) != 0)
                {
                    r.U32();
                }
            }

            if ((moveFlags & (0x200000 | 0x2000000)) != 0 || (moveFlags2 & 0x20) != 0)
            {
                r.F32(); // pitch
            }

            r.U32(); // fall time
            if ((moveFlags & 0x1000) != 0) // falling
            {
                r.Skip(16);
            }

            if ((moveFlags & 0x4000000) != 0) // spline elevation
            {
                r.F32();
            }

            obj.WalkSpeed = r.F32();
            obj.RunSpeed = r.F32();
            r.Skip(7 * 4); // back, swim, swim back, flight, flight back, turn and pitch rates
            obj.Path = MovePath.Standing(obj.Position, obj.Orientation);
            obj.PathElapsed = 0;
            if ((moveFlags & 0x8000000) != 0) // spline enabled
            {
                ReadCreateSpline(r, obj);
            }
        }
        else if ((flags & 0x100) != 0) // position
        {
            r.PackedGuid();
            obj.Position = Vec(r);
            r.Skip(12);
            obj.Orientation = r.F32();
            r.F32();
        }
        else if ((flags & 0x40) != 0) // stationary position
        {
            obj.Position = Vec(r);
            obj.Orientation = r.F32();
        }

        if ((flags & 0x08) != 0)
        {
            r.U32();
        }

        if ((flags & 0x10) != 0)
        {
            r.U32();
        }

        if ((flags & 0x04) != 0)
        {
            r.PackedGuid();
        }

        if ((flags & 0x02) != 0)
        {
            r.U32();
        }

        if ((flags & 0x80) != 0)
        {
            r.U32();
            r.F32();
        }

        if ((flags & 0x200) != 0)
        {
            r.U64();
        }
    }

    /// <summary>A spline already under way when the unit comes into view.</summary>
    private static void ReadCreateSpline(PacketReader r, TrackedObject obj)
    {
        var splineFlags = r.U32();
        float? facing = null;
        if ((splineFlags & SplineFinalAngle) != 0)
        {
            facing = r.F32();
        }
        else if ((splineFlags & SplineFinalTarget) != 0)
        {
            r.U64();
        }
        else if ((splineFlags & SplineFinalPoint) != 0)
        {
            r.Skip(12);
        }

        var passed = r.U32();
        var duration = r.U32();
        r.U32(); // id
        r.Skip(12); // duration mods and vertical acceleration
        r.U32(); // effect start time
        var count = r.U32();
        var points = new List<Vector3>((int)count);
        for (uint i = 0; i < count; i++)
        {
            points.Add(Vec(r));
        }

        r.U8(); // interpolation mode
        r.Skip(12); // final destination

        // A cyclic path's last point is a stand-in.
        var cyclic = (splineFlags & SplineCyclic) != 0;
        if (cyclic && points.Count > 2 && (splineFlags & SplineFlying) == 0)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count > 0)
        {
            obj.Path = new MovePath(points, duration / 1000.0, facing, cyclic);
            obj.PathElapsed = passed / 1000.0;
        }
    }

    private void ReadMonsterMove(PacketReader r)
    {
        var guid = r.PackedGuid();
        r.U8();
        var start = Vec(r);
        r.U32(); // spline id
        var type = r.U8();
        if (!Objects.TryGetValue(guid, out var obj))
        {
            return;
        }

        obj.Position = start;
        float? facing = null;
        switch (type)
        {
            case 1: // stop
                obj.Path = MovePath.Standing(start, obj.Orientation);
                obj.PathElapsed = 0;
                Moved?.Invoke(obj);
                return;
            case 2: // facing a spot
                var spot = Vec(r);
                facing = MathF.Atan2(spot.Y - start.Y, spot.X - start.X);
                break;
            case 3: // facing a target
                r.U64();
                break;
            case 4: // facing an angle
                facing = r.F32();
                break;
        }

        var splineFlags = r.U32();
        if ((splineFlags & SplineAnimation) != 0)
        {
            r.U8();
            r.U32();
        }

        var duration = r.U32();
        if ((splineFlags & SplineParabolic) != 0)
        {
            r.F32();
            r.U32();
        }

        var count = r.U32();
        var points = new List<Vector3>((int)count + 1) { start };
        var cyclic = (splineFlags & SplineCyclic) != 0;
        if ((splineFlags & (SplineFlying | SplineCatmullRom)) != 0)
        {
            for (uint i = 0; i < count; i++)
            {
                points.Add(Vec(r));
            }

            // Ground cycles end on a stand-in point of zeros; flying ones start on the unit's own position.
            if (cyclic && (splineFlags & SplineFlying) == 0 && points.Count > 2)
            {
                points.RemoveAt(points.Count - 1);
            }
        }
        else if (count > 0)
        {
            var end = Vec(r);
            var middle = (start + end) / 2f;
            for (uint i = 1; i < count; i++)
            {
                points.Add(middle - Unpack(r.U32()));
            }

            points.Add(end);
        }

        obj.Path = new MovePath(points, duration / 1000.0, facing, cyclic);
        obj.PathElapsed = 0;
        if (facing is { } f && points.Count == 1)
        {
            obj.Orientation = f;
        }

        Moved?.Invoke(obj);
    }

    /// <summary>A point packed in 32 bits as quarter yards: 11 for X, 11 for Y and 10 for Z, each signed.</summary>
    internal static Vector3 Unpack(uint packed)
    {
        var x = (int)(packed << 21) >> 21;
        var y = (int)(packed << 10) >> 21;
        var z = (int)packed >> 22;
        return new Vector3(x, y, z) * 0.25f;
    }

    private static Vector3 Vec(PacketReader r) => new(r.F32(), r.F32(), r.F32());
}
