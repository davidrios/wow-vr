using System.Numerics;
using System.Text;
using WowVR.Formats.Native;

namespace WowVR.Formats.Models;

public enum ModelBlend
{
    Opaque,
    AlphaKey,
    Alpha,
    Add,
    Modulate,
}

/// <summary>How a surface draws. Equal materials can share a GPU material.</summary>
public readonly record struct ModelMaterial(string Texture, ModelBlend Blend, bool Unlit, bool TwoSided, bool Repeat, M2Effect? Effect = null, string? Texture2 = null, M2BoneBake? Bake = null, bool Billboard = false, WmoShader Shader = WmoShader.Diffuse);

/// <summary>A WMO material's shader in MOMT. Those not listed have no highlight.</summary>
public enum WmoShader
{
    Diffuse = 0,
    Specular = 1,
    Metal = 2,
    Environment = 3,
    Opaque = 4,
    EnvironmentMetal = 5,
}

/// <summary>One material's triangles with only the vertices they use, in Godot axes.</summary>
public sealed class ModelSurface
{
    public required ModelMaterial Material { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }

    /// <summary>The second UV set, when a material uses it.</summary>
    public Vector2[]? TexCoords2 { get; init; }

    /// <summary>4 bytes per vertex: red, green, blue, alpha. Null when there are none.</summary>
    public byte[]? Colors { get; init; }

    /// <summary>For a WMO group, which vertices belong to its transition batches. Null when none do.</summary>
    public bool[]? Transition { get; init; }

    /// <summary>Wound the way Godot expects.</summary>
    public required int[] Indices { get; init; }
}

internal static unsafe class MeshReader
{
    public static string Text(byte* text, nuint length) => Encoding.UTF8.GetString(text, checked((int)length));

    /// <summary>Splits a mesh by material, merging batches that share one.</summary>
    public static List<ModelSurface> Surfaces(in WaMesh mesh, Func<uint, ModelMaterial?> materialOf, float* texCoords2 = null, int transitionEnd = 0)
    {
        var positions = new ReadOnlySpan<float>(mesh.Positions, checked((int)mesh.VertexCount * 3));
        var normals = new ReadOnlySpan<float>(mesh.Normals, checked((int)mesh.VertexCount * 3));
        var texCoords = new ReadOnlySpan<float>(mesh.TexCoords, checked((int)mesh.VertexCount * 2));
        var colors = mesh.Colors is null ? default : new ReadOnlySpan<byte>(mesh.Colors, checked((int)mesh.VertexCount * 4));
        var indices = new ReadOnlySpan<uint>(mesh.Indices, checked((int)mesh.IndexCount));
        var batches = new ReadOnlySpan<WaBatch>(mesh.Batches, checked((int)mesh.BatchCount));

        var byMaterial = new Dictionary<ModelMaterial, List<(int Start, int Count)>>();
        foreach (var batch in batches)
        {
            var end = (long)batch.IndexStart + batch.IndexCount;
            if (batch.IndexCount == 0 || end > indices.Length || materialOf(batch.Material) is not { } material)
            {
                continue;
            }

            if (!byMaterial.TryGetValue(material, out var ranges))
            {
                byMaterial[material] = ranges = [];
            }

            ranges.Add(((int)batch.IndexStart, (int)batch.IndexCount));
        }

        var surfaces = new List<ModelSurface>(byMaterial.Count);
        var remap = new int[mesh.VertexCount];
        foreach (var (material, ranges) in byMaterial)
        {
            Array.Fill(remap, -1);
            var used = new List<int>();
            var surfaceIndices = new List<int>();
            foreach (var (start, count) in ranges)
            {
                // Reversed, because the files wind the other way from Godot.
                for (var triangle = 0; triangle + 2 < count; triangle += 3)
                {
                    for (var corner = 2; corner >= 0; corner--)
                    {
                        var vertex = (int)indices[start + triangle + corner];
                        if (remap[vertex] < 0)
                        {
                            remap[vertex] = used.Count;
                            used.Add(vertex);
                        }

                        surfaceIndices.Add(remap[vertex]);
                    }
                }
            }

            var outPositions = new Vector3[used.Count];
            var outNormals = new Vector3[used.Count];
            var outTexCoords = new Vector2[used.Count];
            var outColors = colors.IsEmpty ? null : new byte[used.Count * 4];
            var outTransition = transitionEnd > 0 ? new bool[used.Count] : null;
            var outTexCoords2 = texCoords2 is not null && material.Effect?.UsesSecondUv == true ? new Vector2[used.Count] : null;
            for (var i = 0; i < used.Count; i++)
            {
                var v = used[i];
                outPositions[i] = WowCoords.ModelToGodot(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
                outNormals[i] = WowCoords.ModelToGodot(new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]));
                outTexCoords[i] = new Vector2(texCoords[v * 2], texCoords[v * 2 + 1]);
                if (outTexCoords2 is not null)
                {
                    outTexCoords2[i] = new Vector2(texCoords2[v * 2], texCoords2[v * 2 + 1]);
                }

                if (outColors is not null)
                {
                    colors.Slice(v * 4, 4).CopyTo(outColors.AsSpan(i * 4));
                }

                if (outTransition is not null)
                {
                    outTransition[i] = v < transitionEnd;
                }
            }

            surfaces.Add(new ModelSurface
            {
                Material = material,
                Positions = outPositions,
                Normals = outNormals,
                TexCoords = outTexCoords,
                TexCoords2 = outTexCoords2,
                Colors = outColors,
                Transition = outTransition,
                Indices = surfaceIndices.ToArray(),
            });
        }

        return surfaces;
    }
}

/// <summary>An M2 model in its rest pose.</summary>
public sealed class M2Model
{
    public required IReadOnlyList<ModelSurface> Surfaces { get; init; }

    public IReadOnlyList<M2Emitter> Emitters { get; init; } = [];

    /// <summary>The distance of the furthest vertex from the model's origin.</summary>
    public required float Radius { get; init; }

    /// <summary>
    /// The file's simplified collision mesh: three corners per triangle in Godot axes, wound
    /// like the surfaces. Empty for models you can walk through, such as grass.
    /// </summary>
    public required Vector3[] CollisionFaces { get; init; }

    /// <summary>The path of a model's first skin file, for its M2 path.</summary>
    public static string SkinPath(string m2Path) => Path.ChangeExtension(m2Path, null) + "00.skin";

    /// <summary>Placements often say .mdx, from before the format was renamed.</summary>
    public static string NormalizePath(string path) => Path.ChangeExtension(path, ".m2");

    public static unsafe M2Model Parse(ReadOnlySpan<byte> m2, ReadOnlySpan<byte> skin)
    {
        nint model;
        fixed (byte* m2Data = m2, skinData = skin)
        {
            model = ModelNativeMethods.M2Open(m2Data, (nuint)m2.Length, skinData, (nuint)skin.Length);
        }

        if (model == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        try
        {
            ModelNativeMethods.M2Mesh(model, out var mesh);

            var looks = M2BatchLooks.Read(model, mesh.BatchCount, BlendOf);

            // Each batch's material says which, so batches with an effect keep their own surface.
            ModelMaterial? MaterialOf(uint index)
            {
                var look = looks[index];
                return look.Hidden || look.Texture is null
                    ? null
                    : new ModelMaterial(look.Texture, look.Blend, look.Unlit, look.TwoSided, look.Repeat, look.Effect, look.Effect?.Texture2Name);
            }

            return new M2Model
            {
                Surfaces = MeshReader.Surfaces(mesh, MaterialOf, ModelNativeMethods.M2TexCoords2(model)),
                Radius = ModelNativeMethods.M2BoundingRadius(model),
                Emitters = M2Emitter.ReadAll(model),
                CollisionFaces = ReadCollision(model),
            };
        }
        finally
        {
            ModelNativeMethods.M2Free(model);
        }
    }

    private static unsafe Vector3[] ReadCollision(nint model)
    {
        ModelNativeMethods.M2Collision(model, out var positions, out _, out var indices, out var indexCount);

        // Whole triangles only, and reversed, as for the surfaces.
        var faces = new Vector3[indexCount - indexCount % 3];
        for (var i = 0; i < faces.Length; i++)
        {
            var v = (int)indices[i - i % 3 + (2 - i % 3)];
            faces[i] = WowCoords.ModelToGodot(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
        }

        return faces;
    }

    private static ModelBlend BlendOf(uint mode) => mode switch
    {
        0 => ModelBlend.Opaque,
        1 => ModelBlend.AlphaKey,
        2 => ModelBlend.Alpha,
        3 or 4 => ModelBlend.Add,
        _ => ModelBlend.Modulate,
    };
}

/// <summary>A doodad placed in a WMO's own space, in Godot axes.</summary>
/// <param name="Index">Its place in <see cref="WmoRoot.Doodads"/>, which groups refer to it by.</param>
/// <param name="Color">Its baked light: red, green and blue from 0 to 1.</param>
public readonly record struct WmoDoodad(string Model, PlacementTransform Transform, int Index = 0, Vector3 Color = default);

/// <summary>A WMO root file.</summary>
public sealed class WmoRoot
{
    private const uint FlagUnlit = 0x1;
    private const uint FlagTwoSided = 0x4;

    public required int GroupCount { get; init; }

    /// <summary>MOHD flags, which decide how its groups' liquids read.</summary>
    public required uint Flags { get; init; }

    /// <summary>The corners of the WMO's bounding box in its own space, Z up, as stored.</summary>
    public required (Vector3 Min, Vector3 Max) Bounds { get; init; }

    /// <summary>MOPV, in the file's axes.</summary>
    public Vector3[] PortalVertices { get; init; } = [];

    /// <summary>MOPT: the first vertex, how many, and the plane, in the file's axes.</summary>
    public (int Start, int Count, Vector3 Normal, float Offset)[] Portals { get; init; } = [];

    /// <summary>MOPR: the portal, the group behind it, and the side it's seen from. Groups say which range is theirs.</summary>
    public (int Portal, int Group, int Side)[] PortalRefs { get; init; } = [];

    /// <summary>MOHD's ambient color: red, green and blue from 0 to 1.</summary>
    public Vector3 Ambient { get; init; }

    public required IReadOnlyList<ModelMaterial> Materials { get; init; }
    public required IReadOnlyList<(int Start, int Count)> DoodadSets { get; init; }
    public required IReadOnlyList<WmoDoodad> Doodads { get; init; }

    /// <summary>The path of a group file, for its WMO's path.</summary>
    public static string GroupPath(string wmoPath, int group) =>
        $"{Path.ChangeExtension(wmoPath, null)}_{group:D3}.wmo";

    /// <summary>A placement's doodads: the global first set, and its own if different.</summary>
    public IEnumerable<WmoDoodad> DoodadsInSet(int set)
    {
        if (DoodadSets.Count == 0)
        {
            yield break;
        }

        foreach (var index in set > 0 && set < DoodadSets.Count ? new[] { 0, set } : [0])
        {
            var (start, count) = DoodadSets[index];
            for (var i = start; i < start + count && i < Doodads.Count; i++)
            {
                yield return Doodads[i];
            }
        }
    }

    public static unsafe WmoRoot Parse(ReadOnlySpan<byte> data)
    {
        nint wmo;
        fixed (byte* input = data)
        {
            wmo = ModelNativeMethods.WmoOpen(input, (nuint)data.Length);
        }

        if (wmo == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        try
        {
            var bounds = stackalloc float[6];
            ModelNativeMethods.WmoBounds(wmo, bounds);

            var materials = new ModelMaterial[ModelNativeMethods.WmoMaterialCount(wmo)];
            for (var i = 0; i < materials.Length; i++)
            {
                ModelNativeMethods.WmoMaterial(wmo, (uint)i, out var material, out var text, out var length);
                materials[i] = new ModelMaterial(
                    MeshReader.Text(text, length),
                    BlendOf(material.BlendMode),
                    (material.Flags & FlagUnlit) != 0,
                    (material.Flags & FlagTwoSided) != 0,
                    Repeat: true,
                    Shader: (WmoShader)material.Shader);
            }

            var sets = new (int, int)[ModelNativeMethods.WmoDoodadSetCount(wmo)];
            for (var i = 0; i < sets.Length; i++)
            {
                ModelNativeMethods.WmoDoodadSet(wmo, (uint)i, out var set);
                sets[i] = ((int)set.Start, (int)set.Count);
            }

            var doodads = new WmoDoodad[ModelNativeMethods.WmoDoodadCount(wmo)];
            for (var i = 0; i < doodads.Length; i++)
            {
                ModelNativeMethods.WmoDoodad(wmo, (uint)i, out var doodad);
                ModelNativeMethods.WmoDoodadName(wmo, doodad.Name, out var text, out var length);
                doodads[i] = new WmoDoodad(
                    M2Model.NormalizePath(MeshReader.Text(text, length)),
                    WowCoords.WmoDoodadTransform(
                        new Vector3(doodad.Position[0], doodad.Position[1], doodad.Position[2]),
                        new Quaternion(doodad.Rotation[0], doodad.Rotation[1], doodad.Rotation[2], doodad.Rotation[3]),
                        doodad.Scale),
                    i,
                    ColorOf(doodad.Color));
            }

            ModelNativeMethods.WmoPortalVertices(wmo, out var portalCorners, out var cornerCount);
            var portalVertices = new Vector3[cornerCount];
            for (var i = 0; i < portalVertices.Length; i++)
            {
                portalVertices[i] = new Vector3(portalCorners[i * 3], portalCorners[i * 3 + 1], portalCorners[i * 3 + 2]);
            }

            var portals = new (int, int, Vector3, float)[ModelNativeMethods.WmoPortalCount(wmo)];
            for (var i = 0; i < portals.Length; i++)
            {
                ModelNativeMethods.WmoPortal(wmo, (uint)i, out var portal);
                portals[i] = ((int)portal.Start, (int)portal.Count, new Vector3(portal.Normal[0], portal.Normal[1], portal.Normal[2]), portal.Offset);
            }

            var portalRefs = new (int, int, int)[ModelNativeMethods.WmoPortalRefCount(wmo)];
            for (var i = 0; i < portalRefs.Length; i++)
            {
                ModelNativeMethods.WmoPortalRef(wmo, (uint)i, out var portalRef);
                portalRefs[i] = ((int)portalRef.Portal, (int)portalRef.Group, portalRef.Side);
            }

            return new WmoRoot
            {
                PortalVertices = portalVertices,
                Portals = portals,
                PortalRefs = portalRefs,
                GroupCount = (int)ModelNativeMethods.WmoGroupCount(wmo),
                Flags = ModelNativeMethods.WmoFlags(wmo),
                Ambient = ColorOf(ModelNativeMethods.WmoAmbientColor(wmo)),
                Bounds = (new Vector3(bounds[0], bounds[1], bounds[2]), new Vector3(bounds[3], bounds[4], bounds[5])),
                Materials = materials,
                DoodadSets = sets,
                Doodads = doodads,
            };
        }
        finally
        {
            ModelNativeMethods.WmoFree(wmo);
        }
    }

    /// <summary>A stored 0xAARRGGBB as red, green and blue from 0 to 1.</summary>
    private static Vector3 ColorOf(uint color) =>
        new(((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f);

    private static ModelBlend BlendOf(uint mode) => mode switch
    {
        0 => ModelBlend.Opaque,
        1 => ModelBlend.AlphaKey,
        2 => ModelBlend.Alpha,
        3 => ModelBlend.Add,
        _ => ModelBlend.Modulate,
    };
}

/// <summary>One group of a WMO.</summary>
public sealed class WmoGroup
{
    private const uint FlagInterior = 0x2000;
    private const uint FlagExteriorLit = 0x40;

    /// <summary>Lit by what's baked into it rather than the sky. A group can be flagged interior and still be lit from outside, as much of Stormwind's streets are.</summary>
    public required bool Indoor { get; init; }

    /// <summary>The doodads standing in this group, as indexes into <see cref="WmoRoot.Doodads"/>.</summary>
    public required ushort[] DoodadRefs { get; init; }

    /// <summary>The group's box in the file's axes.</summary>
    public required (Vector3 Min, Vector3 Max) Bounds { get; init; }

    /// <summary>Its range of <see cref="WmoRoot.PortalRefs"/>.</summary>
    public required (int Start, int Count) PortalRange { get; init; }


    /// <summary>MOGP flags.</summary>
    public required uint Flags { get; init; }
    public required IReadOnlyList<ModelSurface> Surfaces { get; init; }

    /// <summary>Three corners per triangle in Godot axes, wound like the surfaces.</summary>
    public required Vector3[] CollisionFaces { get; init; }

    /// <summary>Null when the group has no liquid.</summary>
    public Terrain.LiquidSurface? Liquid { get; init; }

    private static unsafe (Vector3 Min, Vector3 Max) ReadBounds(nint group)
    {
        var bounds = stackalloc float[6];
        ModelNativeMethods.WmoGroupBounds(group, bounds);
        return (new Vector3(bounds[0], bounds[1], bounds[2]), new Vector3(bounds[3], bounds[4], bounds[5]));
    }

    /// <summary>The client changes vertex colors as it loads, which needs the whole group's order of vertices.</summary>
    private static unsafe List<ModelSurface> ReadSurfaces(nint group, in WaMesh mesh, WmoRoot root)
    {
        var batches = new ReadOnlySpan<WaBatch>(mesh.Batches, checked((int)mesh.BatchCount));
        var indices = new ReadOnlySpan<uint>(mesh.Indices, checked((int)mesh.IndexCount));
        var transitionEnd = 0;
        var transitionBatches = (int)Math.Min(ModelNativeMethods.WmoGroupTransitionBatchCount(group), mesh.BatchCount);
        for (var b = 0; b < transitionBatches; b++)
        {
            foreach (var vertex in indices.Slice((int)batches[b].IndexStart, (int)batches[b].IndexCount))
            {
                transitionEnd = Math.Max(transitionEnd, (int)vertex + 1);
            }
        }

        ModelMaterial? MaterialOf(uint m) => m < root.Materials.Count ? root.Materials[(int)m] : null;
        if (mesh.Colors is null)
        {
            return MeshReader.Surfaces(mesh, MaterialOf);
        }

        var colors = new ReadOnlySpan<byte>(mesh.Colors, checked((int)mesh.VertexCount * 4)).ToArray();
        WmoVertexLight.Fix(colors, transitionEnd, root.Flags, ModelNativeMethods.WmoGroupFlags(group));
        fixed (byte* fixedColors = colors)
        {
            var copy = mesh;
            copy.Colors = fixedColors;
            return MeshReader.Surfaces(copy, MaterialOf, transitionEnd: transitionEnd);
        }
    }

    private static (int Start, int Count) ReadPortalRange(nint group)
    {
        ModelNativeMethods.WmoGroupPortalRefs(group, out var start, out var count);
        return ((int)start, (int)count);
    }

    private static unsafe ushort[] ReadDoodadRefs(nint group)
    {
        ModelNativeMethods.WmoGroupDoodadRefs(group, out var refs, out var count);
        return new ReadOnlySpan<ushort>(refs, checked((int)count)).ToArray();
    }

    private static unsafe Terrain.LiquidSurface? ReadLiquid(nint group, uint rootFlags)
    {
        if (ModelNativeMethods.WmoGroupLiquid(group, rootFlags, out var liquid) != WaStatus.Ok)
        {
            return null;
        }

        var (x, y) = (checked((int)liquid.VerticesX), checked((int)liquid.VerticesY));
        var surface = Terrain.LiquidSurface.FromGrid(
            (int)liquid.LiquidType,
            x,
            y,
            new Vector3(liquid.Corner[0], liquid.Corner[1], liquid.Corner[2]),
            new ReadOnlySpan<float>(liquid.Heights, x * y),
            new ReadOnlySpan<byte>(liquid.Cells, Math.Max(x - 1, 0) * Math.Max(y - 1, 0)));
        return surface.Indices.Length > 0 ? surface : null;
    }

    public static unsafe WmoGroup Parse(ReadOnlySpan<byte> data, WmoRoot root)
    {
        nint group;
        fixed (byte* input = data)
        {
            group = ModelNativeMethods.WmoGroupOpen(input, (nuint)data.Length);
        }

        if (group == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        try
        {
            ModelNativeMethods.WmoGroupMesh(group, out var mesh);

            var positions = new ReadOnlySpan<float>(mesh.Positions, checked((int)mesh.VertexCount * 3));
            var collision = new ReadOnlySpan<uint>(mesh.CollisionIndices, checked((int)mesh.CollisionIndexCount));
            var faces = new Vector3[collision.Length];
            for (var i = 0; i < faces.Length; i++)
            {
                // Each triangle's corners are reversed, as for the surfaces.
                var triangleCorner = i - i % 3 + (2 - i % 3);
                var v = (int)collision[triangleCorner];
                faces[i] = WowCoords.ModelToGodot(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
            }

            return new WmoGroup
            {
                Indoor = (ModelNativeMethods.WmoGroupFlags(group) & FlagInterior) != 0 && (ModelNativeMethods.WmoGroupFlags(group) & FlagExteriorLit) == 0,
                DoodadRefs = ReadDoodadRefs(group),
                Bounds = ReadBounds(group),
                PortalRange = ReadPortalRange(group),
                Flags = ModelNativeMethods.WmoGroupFlags(group),
                Surfaces = ReadSurfaces(group, mesh, root),
                CollisionFaces = faces,
                Liquid = ReadLiquid(group, root.Flags),
            };
        }
        finally
        {
            ModelNativeMethods.WmoGroupFree(group);
        }
    }
}
