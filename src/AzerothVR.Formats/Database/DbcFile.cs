using System.Text;
using AzerothVR.Formats.Native;

namespace AzerothVR.Formats.Database;

/// <summary>A DBC table with untyped access to its 32-bit fields.</summary>
public sealed class DbcFile : IDisposable
{
    private readonly DbcHandle _handle;

    public int RecordCount { get; }
    public int FieldCount { get; }

    private DbcFile(DbcHandle handle)
    {
        _handle = handle;
        NativeMethods.DbcInfo(handle, out var recordCount, out var fieldCount, out _);
        RecordCount = checked((int)recordCount);
        FieldCount = checked((int)fieldCount);
    }

    public static unsafe DbcFile Open(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            var dbc = NativeMethods.DbcOpen(pointer, (nuint)data.Length);
            if (dbc == 0)
            {
                throw new WowAlchemyException(NativeMethods.LastErrorMessage());
            }

            return new DbcFile(DbcHandle.From(dbc));
        }
    }

    public uint GetUInt32(int record, int field)
    {
        if (NativeMethods.DbcGetU32(_handle, (uint)record, (uint)field, out var value) != WaStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(nameof(record), $"No field {field} in record {record}.");
        }

        return value;
    }

    public int GetInt32(int record, int field) => unchecked((int)GetUInt32(record, field));

    public float GetSingle(int record, int field) => BitConverter.UInt32BitsToSingle(GetUInt32(record, field));

    public unsafe string GetString(int record, int field)
    {
        if (NativeMethods.DbcGetString(_handle, (uint)record, (uint)field, out var text, out var length) != WaStatus.Ok)
        {
            throw new ArgumentOutOfRangeException(nameof(record), $"No string at field {field} of record {record}.");
        }

        return Encoding.UTF8.GetString(text, checked((int)length));
    }

    public void Dispose() => _handle.Dispose();
}
