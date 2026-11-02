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
public readonly record struct ModelMaterial(string Texture, ModelBlend Blend, bool Unlit, bool TwoSided, bool Repeat);

/// <summary>One material's triangles with only the vertices they use, in Godot axes.</summary>
public sealed class ModelSurface
{
    public required ModelMaterial Material { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }

    /// <summary>4 bytes per vertex: red, green, blue, alpha. Null when there are none.</summary>
    public byte[]? Colors { get; init; }

    /// <summary>Wound the way Godot expects.</summary>
    public required int[] Indices { get; init; }
}

internal static unsafe class MeshReader
{
    public static string Text(byte* text, nuint length) => Encoding.UTF8.GetString(text, checked((int)length));

    /// <summary>Splits a mesh by material, merging batches that share one.</summary>
    public static List<ModelSurface> Surfaces(in WaMesh mesh, Func<uint, ModelMaterial?> materialOf)
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
            for (var i = 0; i < used.Count; i++)
            {
                var v = used[i];
                outPositions[i] = WowCoords.ModelToGodot(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
                outNormals[i] = WowCoords.ModelToGodot(new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]));
                outTexCoords[i] = new Vector2(texCoords[v * 2], texCoords[v * 2 + 1]);
                if (outColors is not null)
                {
                    colors.Slice(v * 4, 4).CopyTo(outColors.AsSpan(i * 4));
                }
            }

            surfaces.Add(new ModelSurface
            {
                Material = material,
                Positions = outPositions,
                Normals = outNormals,
                TexCoords = outTexCoords,
                Colors = outColors,
                Indices = surfaceIndices.ToArray(),
            });
        }

        return surfaces;
    }
}

/// <summary>An M2 model in its rest pose.</summary>
public sealed class M2Model
{
    private const uint FlagUnlit = 0x1;
    private const uint FlagTwoSided = 0x4;
    private const uint TextureWrapX = 0x1;
    private const uint TextureWrapY = 0x2;

    public required IReadOnlyList<ModelSurface> Surfaces { get; init; }

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

            ModelMaterial? MaterialOf(uint index)
            {
                if (ModelNativeMethods.M2Material(model, index, out var material) != WaStatus.Ok
                    || material.Texture == ModelNativeMethods.NoTexture
                    || ModelNativeMethods.M2TextureName(model, material.Texture, out var text, out var length) != WaStatus.Ok)
                {
                    return null;
                }

                return new ModelMaterial(
                    MeshReader.Text(text, length),
                    BlendOf(material.BlendMode),
                    (material.Flags & FlagUnlit) != 0,
                    (material.Flags & FlagTwoSided) != 0,
                    (material.TextureFlags & (TextureWrapX | TextureWrapY)) != 0);
            }

            return new M2Model
            {
                Surfaces = MeshReader.Surfaces(mesh, MaterialOf),
                Radius = ModelNativeMethods.M2BoundingRadius(model),
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
public readonly record struct WmoDoodad(string Model, PlacementTransform Transform);

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
                    Repeat: true);
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
                        doodad.Scale));
            }

            return new WmoRoot
            {
                GroupCount = (int)ModelNativeMethods.WmoGroupCount(wmo),
                Flags = ModelNativeMethods.WmoFlags(wmo),
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
    private const uint FlagIndoor = 0x2000;

    public required bool Indoor { get; init; }
    public required IReadOnlyList<ModelSurface> Surfaces { get; init; }

    /// <summary>Three corners per triangle in Godot axes, wound like the surfaces.</summary>
    public required Vector3[] CollisionFaces { get; init; }

    /// <summary>Null when the group has no liquid.</summary>
    public Terrain.LiquidSurface? Liquid { get; init; }

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
                Indoor = (ModelNativeMethods.WmoGroupFlags(group) & FlagIndoor) != 0,
                Surfaces = MeshReader.Surfaces(mesh, m => m < root.Materials.Count ? root.Materials[(int)m] : null),
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
