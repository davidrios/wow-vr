using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AzerothVR.Formats.Native;

internal enum WaStatus
{
    Ok = 0,
    NotFound = 1,
    Error = -1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaBuffer
{
    public nint Pointer;
    public nuint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaImage
{
    public WaBuffer Pixels;
    public uint Width;
    public uint Height;
    public uint MipCount;
}

/// <summary>Bindings for wow-alchemy-ffi. The contract is in its include/wow_alchemy.h.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Library = "wow_alchemy_ffi";

    // A module initializer guarantees this runs before the first native call, on any host.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);

    /// <summary>Finds the library next to this assembly, wherever the host loads it from.</summary>
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Library)
        {
            return 0;
        }

        var directory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        var fileName = OperatingSystem.IsWindows() ? $"{Library}.dll"
            : OperatingSystem.IsMacOS() ? $"lib{Library}.dylib"
            : $"lib{Library}.so";

        return NativeLibrary.TryLoad(Path.Combine(directory, fileName), out var handle) ? handle : 0;
    }

    [LibraryImport(Library, EntryPoint = "wa_last_error")]
    private static partial nint LastError();

    internal static string LastErrorMessage() =>
        Marshal.PtrToStringUTF8(LastError()) ?? "unknown wow-alchemy error";

    [LibraryImport(Library, EntryPoint = "wa_buffer_free")]
    internal static partial void BufferFree(WaBuffer buffer);

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_new")]
    internal static partial nint MpqChainNew();

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_free")]
    internal static partial void MpqChainFree(nint chain);

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_add", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial WaStatus MpqChainAdd(MpqChainHandle chain, string path, int priority);

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_contains", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MpqChainContains(MpqChainHandle chain, string fileName);

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_read", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial WaStatus MpqChainRead(MpqChainHandle chain, string fileName, out WaBuffer buffer);

    [LibraryImport(Library, EntryPoint = "wa_mpq_chain_list")]
    internal static partial WaStatus MpqChainList(MpqChainHandle chain, out WaBuffer buffer);

    [LibraryImport(Library, EntryPoint = "wa_blp_decode")]
    internal static partial WaStatus BlpDecode(byte* data, nuint length, uint mipLevel, out WaImage image);

    [LibraryImport(Library, EntryPoint = "wa_dbc_open")]
    internal static partial nint DbcOpen(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "wa_dbc_free")]
    internal static partial void DbcFree(nint dbc);

    [LibraryImport(Library, EntryPoint = "wa_dbc_info")]
    internal static partial void DbcInfo(DbcHandle dbc, out uint recordCount, out uint fieldCount, out uint recordSize);

    [LibraryImport(Library, EntryPoint = "wa_dbc_get_u32")]
    internal static partial WaStatus DbcGetU32(DbcHandle dbc, uint record, uint field, out uint value);

    [LibraryImport(Library, EntryPoint = "wa_dbc_get_string")]
    internal static partial WaStatus DbcGetString(DbcHandle dbc, uint record, uint field, out byte* text, out nuint length);

    /// <summary>Copies a native buffer into managed memory and frees it.</summary>
    internal static byte[] TakeBuffer(WaBuffer buffer)
    {
        try
        {
            if (buffer.Length == 0)
            {
                return [];
            }

            return new ReadOnlySpan<byte>((void*)buffer.Pointer, checked((int)buffer.Length)).ToArray();
        }
        finally
        {
            BufferFree(buffer);
        }
    }
}

internal sealed class MpqChainHandle() : SafeHandle(0, ownsHandle: true)
{
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.MpqChainFree(handle);
        return true;
    }

    internal static MpqChainHandle Create()
    {
        var result = new MpqChainHandle();
        Marshal.InitHandle(result, NativeMethods.MpqChainNew());
        return result;
    }
}

internal sealed class DbcHandle() : SafeHandle(0, ownsHandle: true)
{
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.DbcFree(handle);
        return true;
    }

    internal static DbcHandle From(nint pointer)
    {
        var result = new DbcHandle();
        Marshal.InitHandle(result, pointer);
        return result;
    }
}
