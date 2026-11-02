using WowVR.Formats.Models;
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
internal unsafe struct WaM2Material
{
    public uint Texture;
    public uint BlendMode;
    public uint Flags;
    public uint TextureFlags;
    public uint TextureType;
    public uint Geoset;
    public uint ShaderId;
    public uint TextureCount;
    public uint MaterialIndex;
    public uint MaterialLayer;
    public uint BatchFlags;
    public fixed uint Textures[2];
    public fixed int TexCoords[2];
    public fixed int TextureTransforms[2];
    public int Color;
    public int TextureWeight;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Bone
{
    public int Parent;
    public int KeyBone;
    public uint Flags;
    public fixed float Pivot[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Sequence
{
    public uint Id;
    public uint Variation;
    public uint DurationMs;
    public float MovementSpeed;
    public uint Flags;
    public int Frequency;
    public int NextVariation;
    public uint AliasNext;
    public uint BlendTimeMs;
    public fixed float BoundsMin[3];
    public fixed float BoundsMax[3];
    public float BoundsRadius;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Attachment
{
    public uint Id;
    public uint Bone;
    public fixed float Position[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Event
{
    public fixed byte Identifier[4];
    public uint Data;
    public uint Bone;
    public fixed float Position[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaM2Track
{
    public uint Interpolation;
    public int GlobalSequence;
    public uint KeyCount;
    public uint* Times;
    public float* Values;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaWmoMaterial
{
    public uint Flags;
    public uint BlendMode;
    public uint Shader;
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
    public uint Color;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WaWmoPortal
{
    public uint Start;
    public uint Count;
    public fixed float Normal[3];
    public float Offset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaWmoPortalRef
{
    public uint Portal;
    public uint Group;
    public int Side;
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

    [LibraryImport(Library, EntryPoint = "wa_m2_vertex_bones")]
    internal static partial void M2VertexBones(nint model, out byte* weights, out byte* indices);

    [LibraryImport(Library, EntryPoint = "wa_m2_bone_count")]
    internal static partial uint M2BoneCount(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_bone")]
    internal static partial WaStatus M2Bone(nint model, uint index, out WaM2Bone bone);

    [LibraryImport(Library, EntryPoint = "wa_m2_sequence_count")]
    internal static partial uint M2SequenceCount(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_sequence")]
    internal static partial WaStatus M2Sequence(nint model, uint index, out WaM2Sequence sequence);

    [LibraryImport(Library, EntryPoint = "wa_m2_global_sequences")]
    internal static partial void M2GlobalSequences(nint model, out uint* lengths, out uint count);

    [LibraryImport(Library, EntryPoint = "wa_m2_emitter_count")]
    internal static partial uint M2EmitterCount(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_emitter")]
    internal static partial WaStatus M2Emitter(nint model, uint index, out WaM2Emitter emitter);

    [LibraryImport(Library, EntryPoint = "wa_m2_attachment_count")]
    internal static partial uint M2AttachmentCount(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_attachment")]
    internal static partial WaStatus M2Attachment(nint model, uint index, out WaM2Attachment attachment);

    [LibraryImport(Library, EntryPoint = "wa_m2_clip_open")]
    internal static partial nint M2ClipOpen(nint model, uint sequence, byte* anim, nuint animLength);

    [LibraryImport(Library, EntryPoint = "wa_m2_clip_free")]
    internal static partial void M2ClipFree(nint clip);

    [LibraryImport(Library, EntryPoint = "wa_m2_clip_track")]
    internal static partial WaStatus M2ClipTrack(nint clip, uint bone, uint kind, out WaM2Track track);

    [LibraryImport(Library, EntryPoint = "wa_m2_tex_coords2")]
    internal static partial float* M2TexCoords2(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_texture_type")]
    internal static partial WaStatus M2TextureType(nint model, uint index, out uint textureType, out uint flags);

    [LibraryImport(Library, EntryPoint = "wa_m2_combiner_overrides")]
    internal static partial uint M2CombinerOverrides(nint model, out ushort* values, out uint count);

    [LibraryImport(Library, EntryPoint = "wa_m2_first_sequence_ms")]
    internal static partial uint M2FirstSequenceMs(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_effects_open")]
    internal static partial nint M2EffectsOpen(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_effects_free")]
    internal static partial void M2EffectsFree(nint effects);

    [LibraryImport(Library, EntryPoint = "wa_m2_effects_count")]
    internal static partial uint M2EffectsCount(nint effects, uint family);

    [LibraryImport(Library, EntryPoint = "wa_m2_effects_track")]
    internal static partial WaStatus M2EffectsTrack(nint effects, uint family, uint index, uint kind, out WaM2Track track);

    [LibraryImport(Library, EntryPoint = "wa_m2_event_count")]
    internal static partial uint M2EventCount(nint model);

    [LibraryImport(Library, EntryPoint = "wa_m2_event")]
    internal static partial WaStatus M2Event(nint model, uint index, out WaM2Event m2Event);

    [LibraryImport(Library, EntryPoint = "wa_m2_clip_event_times")]
    internal static partial WaStatus M2ClipEventTimes(nint clip, uint m2Event, out uint* times, out uint count);

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

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_doodad_refs")]
    internal static partial void WmoGroupDoodadRefs(nint group, out ushort* refs, out uint count);

    [LibraryImport(Library, EntryPoint = "wa_wmo_portal_vertices")]
    internal static partial void WmoPortalVertices(nint wmo, out float* vertices, out uint count);

    [LibraryImport(Library, EntryPoint = "wa_wmo_portal_count")]
    internal static partial uint WmoPortalCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_portal")]
    internal static partial WaStatus WmoPortal(nint wmo, uint index, out WaWmoPortal portal);

    [LibraryImport(Library, EntryPoint = "wa_wmo_portal_ref_count")]
    internal static partial uint WmoPortalRefCount(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_portal_ref")]
    internal static partial WaStatus WmoPortalRef(nint wmo, uint index, out WaWmoPortalRef portalRef);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_bounds")]
    internal static partial void WmoGroupBounds(nint group, float* bounds);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_portal_refs")]
    internal static partial void WmoGroupPortalRefs(nint group, out uint start, out uint count);

    [LibraryImport(Library, EntryPoint = "wa_wmo_ambient_color")]
    internal static partial uint WmoAmbientColor(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_flags")]
    internal static partial uint WmoGroupFlags(nint group);

    [LibraryImport(Library, EntryPoint = "wa_wmo_flags")]
    internal static partial uint WmoFlags(nint wmo);

    [LibraryImport(Library, EntryPoint = "wa_wmo_group_liquid")]
    internal static partial WaStatus WmoGroupLiquid(nint group, uint rootFlags, out WaWmoLiquid liquid);

    [LibraryImport(Library, EntryPoint = "wa_blp_to_gpu")]
    internal static partial WaStatus BlpToGpu(byte* data, nuint length, out WaGpuTexture texture);
}
