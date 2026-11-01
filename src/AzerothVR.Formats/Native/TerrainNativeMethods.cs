using System.Runtime.InteropServices;

namespace AzerothVR.Formats.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaTerrainChunk
{
    public uint Ix;
    public uint Iy;
    public uint Flags;
    public uint AreaId;
    public uint Holes;
    public fixed float Position[3];
    public float* Heights;
    public sbyte* Normals;
    public uint LayerCount;
    public fixed uint TextureIds[4];
    public fixed uint LayerFlags[4];
    public byte* Alpha;
    public byte* VertexColors;
}

internal static unsafe partial class TerrainNativeMethods
{
    private const string Library = "wow_alchemy_ffi";

    [LibraryImport(Library, EntryPoint = "wa_wdt_read")]
    internal static partial WaStatus WdtRead(byte* data, nuint length, byte* tiles, out uint flags);

    [LibraryImport(Library, EntryPoint = "wa_adt_open")]
    internal static partial nint AdtOpen(byte* data, nuint length, int bigAlpha);

    [LibraryImport(Library, EntryPoint = "wa_adt_free")]
    internal static partial void AdtFree(nint adt);

    [LibraryImport(Library, EntryPoint = "wa_adt_texture_count")]
    internal static partial uint AdtTextureCount(AdtHandle adt);

    [LibraryImport(Library, EntryPoint = "wa_adt_texture_name")]
    internal static partial WaStatus AdtTextureName(AdtHandle adt, uint index, out byte* text, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_adt_chunk")]
    internal static partial WaStatus AdtChunk(AdtHandle adt, uint index, out WaTerrainChunk chunk);
}

internal sealed class AdtHandle() : SafeHandle(0, ownsHandle: true)
{
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        TerrainNativeMethods.AdtFree(handle);
        return true;
    }

    internal static AdtHandle From(nint pointer)
    {
        var result = new AdtHandle();
        Marshal.InitHandle(result, pointer);
        return result;
    }
}
