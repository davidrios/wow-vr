using System;
using System.Collections.Concurrent;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Converters;

/// <summary>An M2 ready to draw.</summary>
public sealed record M2Asset(ArrayMesh Mesh, float Radius, Vector3[] CollisionFaces)
{
    private const float ScaleKeyPrecision = 100f;

    private readonly ConcurrentDictionary<int, ConcavePolygonShape3D> _shapes = new();

    /// <summary>
    /// A collision shape at one scale, shared by every copy at that scale. Scale goes in the
    /// shape, because physics engines don't reliably scale bodies.
    /// </summary>
    public ConcavePolygonShape3D ShapeAt(float scale) => _shapes.GetOrAdd(
        Mathf.RoundToInt(scale * ScaleKeyPrecision),
        _ => new ConcavePolygonShape3D { Data = CollisionFaces.Select(f => f * scale).ToArray() });
}

/// <summary>One WMO group ready to draw and collide with, in the WMO's own space.</summary>
public sealed class WmoGroupAsset
{
    public ArrayMesh? Mesh { get; init; }
    public bool Indoor { get; init; }

    /// <summary>Null when the group has nothing to collide with.</summary>
    public ConcavePolygonShape3D? Shape { get; init; }

    /// <summary>Around the collision faces.</summary>
    public Aabb Bounds { get; init; }

    /// <summary>The middle of what's drawn.</summary>
    public Vector3 Center { get; init; }
}

/// <summary>A WMO ready to place.</summary>
public sealed record WmoAsset(WmoRoot Root, WmoGroupAsset[] Groups);

/// <summary>
/// Loads models once and shares them between tiles. Runs off the main thread. Files that
/// can't be read are remembered as missing, so they're only reported once.
/// </summary>
public sealed class ModelLibrary(IAssetSource source)
{
    private const int MaxCachedModels = 1500;
    private const int MaxCachedTextures = 1500;

    private readonly ConcurrentDictionary<string, M2Asset?> _m2s = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WmoAsset?> _wmos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(ModelMaterial Material, bool Indoor), StandardMaterial3D?> _materials = new();

    /// <summary>Models and textures stay alive in the nodes using them, so this only stops a long trip collecting them all.</summary>
    public void TrimCaches()
    {
        if (_m2s.Count + _wmos.Count > MaxCachedModels)
        {
            _m2s.Clear();
            _wmos.Clear();
        }

        if (_textures.Count > MaxCachedTextures)
        {
            _textures.Clear();
            _materials.Clear();
        }
    }

    public M2Asset? GetM2(string path) => _m2s.GetOrAdd(path, LoadM2);

    public WmoAsset? GetWmo(string path) => _wmos.GetOrAdd(path, LoadWmo);

    private M2Asset? LoadM2(string path)
    {
        try
        {
            var m2 = source.Read(path);
            var skin = source.Read(M2Model.SkinPath(path));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                GD.PushWarning($"Model missing or empty: {path}");
                return null;
            }

            var model = M2Model.Parse(m2, skin);
            return BuildMesh(model.Surfaces, indoor: false) is { } mesh ? new M2Asset(mesh, model.Radius, model.CollisionFaces.Select(ToGodot).ToArray()) : null;
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private WmoAsset? LoadWmo(string path)
    {
        try
        {
            var data = source.Read(path);
            if (data is not { Length: > 0 })
            {
                GD.PushWarning($"WMO missing or empty: {path}");
                return null;
            }

            var root = WmoRoot.Parse(data);
            var groups = new WmoGroupAsset[root.GroupCount];
            for (var i = 0; i < groups.Length; i++)
            {
                groups[i] = LoadGroup(path, i, root);
            }

            return new WmoAsset(root, groups);
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private WmoGroupAsset LoadGroup(string wmoPath, int index, WmoRoot root)
    {
        var data = source.Read(WmoRoot.GroupPath(wmoPath, index));
        if (data is not { Length: > 0 })
        {
            GD.PushWarning($"WMO group missing: {WmoRoot.GroupPath(wmoPath, index)}");
            return new WmoGroupAsset();
        }

        var group = WmoGroup.Parse(data, root);
        var faces = group.CollisionFaces.Select(ToGodot).ToArray();
        var mesh = BuildMesh(group.Surfaces, group.Indoor);

        return new WmoGroupAsset
        {
            Mesh = mesh,
            Center = mesh?.GetAabb().GetCenter() ?? Vector3.Zero,
            Indoor = group.Indoor,
            Shape = faces.Length > 0 ? new ConcavePolygonShape3D { Data = faces } : null,
            Bounds = BoundsOf(faces),
        };
    }

    private static Aabb BoundsOf(Vector3[] points)
    {
        if (points.Length == 0)
        {
            return default;
        }

        var bounds = new Aabb(points[0], Vector3.Zero);
        foreach (var point in points)
        {
            bounds = bounds.Expand(point);
        }

        return bounds;
    }

    private ArrayMesh? BuildMesh(System.Collections.Generic.IReadOnlyList<ModelSurface> surfaces, bool indoor)
    {
        ArrayMesh? mesh = null;
        foreach (var surface in surfaces)
        {
            if (surface.Indices.Length == 0 || GetMaterial(surface.Material, indoor) is not { } material)
            {
                continue;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = surface.Positions.Select(ToGodot).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = surface.Normals.Select(ToGodot).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = surface.TexCoords.Select(v => new Vector2(v.X, v.Y)).ToArray();
            arrays[(int)Mesh.ArrayType.Index] = surface.Indices;

            // Only indoor groups use their colors, as WoWModelViewer does.
            if (indoor && surface.Colors is { } colors)
            {
                var vertexColors = new Color[colors.Length / 4];
                for (var i = 0; i < vertexColors.Length; i++)
                {
                    vertexColors[i] = Color.Color8(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2]);
                }

                arrays[(int)Mesh.ArrayType.Color] = vertexColors;
            }

            mesh ??= new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }

        return mesh;
    }

    private StandardMaterial3D? GetMaterial(ModelMaterial material, bool indoor) =>
        _materials.GetOrAdd((material, indoor), key => BuildMaterial(key.Material, key.Indoor));

    private StandardMaterial3D? BuildMaterial(ModelMaterial material, bool indoor)
    {
        if (GetTexture(material.Texture) is not { } texture)
        {
            return null;
        }

        var result = new StandardMaterial3D
        {
            AlbedoTexture = texture,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
            TextureRepeat = material.Repeat,
            CullMode = material.TwoSided ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
            ShadingMode = material.Unlit || indoor
                ? BaseMaterial3D.ShadingModeEnum.Unshaded
                : BaseMaterial3D.ShadingModeEnum.PerPixel,
            MetallicSpecular = 0f,
            Roughness = 1f,
            VertexColorUseAsAlbedo = indoor,
            VertexColorIsSrgb = indoor,
        };

        switch (material.Blend)
        {
            case ModelBlend.AlphaKey:
                result.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                result.AlphaScissorThreshold = 0.5f;
                break;
            case ModelBlend.Alpha:
                result.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                break;
            case ModelBlend.Add:
                result.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                result.BlendMode = BaseMaterial3D.BlendModeEnum.Add;
                result.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                break;
            case ModelBlend.Modulate:
                result.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                result.BlendMode = BaseMaterial3D.BlendModeEnum.Mul;
                result.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                break;
        }

        return result;
    }

    private ImageTexture? GetTexture(string path) => _textures.GetOrAdd(path, LoadTexture);

    private ImageTexture? LoadTexture(string path)
    {
        try
        {
            var data = source.Read(path);
            if (data is not { Length: > 0 })
            {
                GD.PushWarning($"Texture missing: {path}");
                return null;
            }

            var texture = BlpDecoder.ConvertToGpu(data);
            Image image;
            if (texture.Format == GpuTextureFormat.Rgba8)
            {
                image = Image.CreateFromData(texture.Width, texture.Height, false, Image.Format.Rgba8, texture.Pixels);
                image.GenerateMipmaps();
            }
            else
            {
                var format = texture.Format switch
                {
                    GpuTextureFormat.Dxt1 => Image.Format.Dxt1,
                    GpuTextureFormat.Dxt3 => Image.Format.Dxt3,
                    _ => Image.Format.Dxt5,
                };
                image = Image.CreateFromData(texture.Width, texture.Height, true, format, texture.Pixels);
            }

            return ImageTexture.CreateFromImage(image);
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>Formats already gives these in Godot axes, so this only changes the type.</summary>
    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
