using System.Runtime.InteropServices;

namespace WowVR.Formats.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaPlacement
{
    public uint Name;
    public uint UniqueId;
    public fixed float Position[3];
    public fixed float Rotation[3];
    public float Scale;
    public uint DoodadSet;
    public fixed float BoundsMin[3];
    public fixed float BoundsMax[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaBatch
{
    public uint IndexStart;
    public uint IndexCount;
    public uint Material;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaMesh
{
    public uint VertexCount;
    public float* Positions;
    public float* Normals;
    public float* TexCoords;
    public byte* Colors;
    public uint IndexCount;
    public uint* Indices;
    public uint BatchCount;
    public WaBatch* Batches;
    public uint CollisionIndexCount;
    public uint* CollisionIndices;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaM2Material
{
    public uint Texture;
    public uint BlendMode;
    public uint Flags;
    public uint TextureFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaWmoMaterial
{
    public uint Flags;
    public uint BlendMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaWmoDoodadSet
{
    public uint Start;
    public uint Count;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaWmoDoodad
{
    public uint Name;
    public fixed float Position[3];
    public fixed float Rotation[4];
    public float Scale;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaGpuTexture
{
    public WaBuffer Pixels;
    public uint Format;
    public uint Width;
    public uint Height;
    public uint MipCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaWmoLiquid
{
    public uint VerticesX;
    public uint VerticesY;
    public fixed float Corner[3];
    public float* Heights;
    public byte* Cells;
    public uint LiquidType;
}

internal static unsafe partial class ModelNativeMethods
{
    private const string Library = "wow_alchemy_ffi";

    internal const uint NoTexture = uint.MaxValue;

    [LibraryImport(Library, EntryPoint = "wa_adt_model_name")]
    internal static partial WaStatus AdtModelName(AdtHandle adt, uint index, out byte* text, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_adt_wmo_name")]
    internal static partial WaStatus AdtWmoName(AdtHandle adt, uint index, out byte* text, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_adt_doodads")]
    internal static partial void AdtDoodads(AdtHandle adt, out WaPlacement* placements, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_adt_wmos")]
    internal static partial void AdtWmos(AdtHandle adt, out WaPlacement* placements, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_m2_open")]
    internal static partial nint M2Open(byte* m2, nuint m2Length, byte* skin, nuint skinLength);

    [LibraryImport(Library, EntryPoint = "wa_m2_free")]
    internal static partial void M2Free(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_mesh")]
    internal static partial void M2Mesh(nint model, out WaMesh mesh);

    [LibraryImport(Library, EntryPoint = "wa_m2_bounding_radius")]
    internal static partial float M2BoundingRadius(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_collision")]
    internal static partial void M2Collision(nint model, out float* positions, out uint positionCount, out uint* indices, out uint indexCount);

    [LibraryImport(Library, EntryPoint = "wa_m2_material")]
    internal static partial WaStatus M2Material(nint model, uint index, out WaM2Material material);

    [LibraryImport(Library, EntryPoint = "wa_m2_texture_name")]
    internal static partial WaStatus M2TextureName(nint model, uint index, out byte* text, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_wmo_open")]
    internal static partial nint WmoOpen(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "wa_wmo_free")]
    internal static partial void WmoFree(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_count")]
    internal static partial uint WmoGroupCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_bounds")]
    internal static partial void WmoBounds(nint wmo, float* bounds);

    [LibraryImport(Library, EntryPoint = "wa_wmo_material_count")]
    internal static partial uint WmoMaterialCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_material")]
    internal static partial WaStatus WmoMaterial(nint wmo, uint index, out WaWmoMaterial material, out byte* texture, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_wmo_doodad_set_count")]
    internal static partial uint WmoDoodadSetCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_doodad_set")]
    internal static partial WaStatus WmoDoodadSet(nint wmo, uint index, out WaWmoDoodadSet set);

    [LibraryImport(Library, EntryPoint = "wa_wmo_doodad_count")]
    internal static partial uint WmoDoodadCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_doodad")]
    internal static partial WaStatus WmoDoodad(nint wmo, uint index, out WaWmoDoodad doodad);

    [LibraryImport(Library, EntryPoint = "wa_wmo_doodad_name")]
    internal static partial WaStatus WmoDoodadName(nint wmo, uint index, out byte* text, out nuint length);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_open")]
    internal static partial nint WmoGroupOpen(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_free")]
    internal static partial void WmoGroupFree(nint group);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_mesh")]
    internal static partial void WmoGroupMesh(nint group, out WaMesh mesh);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_flags")]
    internal static partial uint WmoGroupFlags(nint group);

    [LibraryImport(Library, EntryPoint = "wa_wmo_flags")]
    internal static partial uint WmoFlags(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_liquid")]
    internal static partial WaStatus WmoGroupLiquid(nint group, uint rootFlags, out WaWmoLiquid liquid);

    [LibraryImport(Library, EntryPoint = "wa_blp_to_gpu")]
    internal static partial WaStatus BlpToGpu(byte* data, nuint length, out WaGpuTexture texture);
}
