using System.Buffers.Binary;
using System.Text;

namespace WowVR.Net;

/// <summary>Little-endian reader over one packet's payload.</summary>
public sealed class PacketReader(byte[] data, int start = 0, int? length = null)
{
    readonly int _end = start + (length ?? data.Length - start);

    public int Position { get; set; } = start;
    public int Remaining => _end - Position;

    void Need(int n)
    {
        if (Position + n > _end)
            throw new EndOfStreamException($"needed {n} bytes at {Position}, packet ends at {_end}");
    }

    public byte U8() { Need(1); return data[Position++]; }
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position)); Position += 2; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position)); Position += 4; return v; }
    public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(Position)); Position += 8; return v; }
    public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(Position)); Position += 4; return v; }
    public (float X, float Y, float Z) Vec3() => (F32(), F32(), F32());

    public byte[] Bytes(int n)
    {
        Need(n);
        var b = data.AsSpan(Position, n).ToArray();
        Position += n;
        return b;
    }

    public void Skip(int n) { Need(n); Position += n; }

    public string CString()
    {
        int zero = Array.IndexOf(data, (byte)0, Position, _end - Position);
        if (zero < 0)
            throw new EndOfStreamException("unterminated string");
        var s = Encoding.UTF8.GetString(data, Position, zero - Position);
        Position = zero + 1;
        return s;
    }

    /// <summary>A GUID sent as a mask byte followed by its non-zero bytes.</summary>
    public ulong PackedGuid()
    {
        byte mask = U8();
        ulong guid = 0;
        for (int i = 0; i < 8; i++)
            if ((mask & (1 << i)) != 0)
                guid |= (ulong)U8() << (i * 8);
        return guid;
    }
}

/// <summary>Little-endian writer for packet payloads.</summary>
public sealed class PacketWriter
{
    readonly MemoryStream _ms = new();

    public PacketWriter U8(byte v) { _ms.WriteByte(v); return this; }
    public PacketWriter U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); return this; }
    public PacketWriter U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); return this; }
    public PacketWriter U64(ulong v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); _ms.Write(b); return this; }
    public PacketWriter F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); _ms.Write(b); return this; }
    public PacketWriter Bytes(ReadOnlySpan<byte> v) { _ms.Write(v); return this; }
    public PacketWriter CString(string s) { _ms.Write(Encoding.UTF8.GetBytes(s)); _ms.WriteByte(0); return this; }

    public byte[] ToArray() => _ms.ToArray();

    /// <summary>A GUID as a mask byte followed by its non-zero bytes.</summary>
    public static void PackedGuid(PacketWriter w, ulong guid)
    {
        byte mask = 0;
        Span<byte> bytes = stackalloc byte[8];
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = (byte)(guid >> (i * 8));
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                bytes[count++] = b;
            }
        }

        w.U8(mask).Bytes(bytes[..count]);
    }

    public PacketWriter Packed(ulong guid)
    {
        PackedGuid(this, guid);
        return this;
    }
    public int Length => (int)_ms.Length;
}
