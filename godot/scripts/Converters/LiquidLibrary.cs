using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Converters;

/// <summary>
/// Liquid materials, one per liquid type, shared by every tile and WMO so the sky can recolor them
/// all at once. Runs off the main thread.
/// </summary>
public sealed class LiquidLibrary
{
    private const int MaxFrames = 30;
    private const int MaxFrameSize = 256;

    /// <summary>Magma flows slowly. Slime drifts more slowly still.</summary>
    private static readonly Vector2 MagmaFlow = new(0.02f, 0.01f);
    private static readonly Vector2 SlimeFlow = new(0.01f, 0.005f);

    private static readonly Shader LiquidShader = GD.Load<Shader>("res://shaders/liquid.gdshader");

    /// <summary>
    /// The soft shapes the water paints with. It repeats seamlessly, and its mipmaps fade to flat
    /// grey, so distant water can't shimmer. Generated, so it's not a game asset.
    /// </summary>
    private static readonly NoiseTexture2D PaintNoise = new()
    {
        Width = 256,
        Height = 256,
        Seamless = true,
        GenerateMipmaps = true,
        Normalize = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.02f,
            FractalOctaves = 2,
        },
    };

    private readonly IAssetSource _source;
    private readonly Dictionary<int, LiquidTypeRecord> _types;
    private readonly ConcurrentDictionary<int, Lazy<ShaderMaterial?>> _materials = new();
    private readonly ConcurrentDictionary<string, Lazy<(Texture2DArray Frames, int Count)?>> _frames = new(StringComparer.OrdinalIgnoreCase);

    public LiquidLibrary(IAssetSource source)
    {
        _source = source;
        _types = LiquidTypeRecord.ReadAll(source).ToDictionary(t => t.Id);
    }

    /// <summary>Raised off the main thread when a new material appears, so the sky can color it before it shows.</summary>
    public event Action<ShaderMaterial, LiquidKind>? MaterialCreated;

    /// <summary>A mesh with one surface per liquid type, or null if there's nothing to draw.</summary>
    public ArrayMesh? BuildMesh(IEnumerable<LiquidSurface> surfaces)
    {
        ArrayMesh? mesh = null;
        foreach (var surface in surfaces)
        {
            if (surface.Indices.Length == 0 || GetMaterial(surface.LiquidType) is not { } material)
            {
                continue;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = surface.Positions.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = Enumerable.Repeat(Vector3.Up, surface.Positions.Length).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = surface.TexCoords.Select(t => new Vector2(t.X, t.Y)).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV2] = surface.Depths.Select(d => new Vector2(d, 0f)).ToArray();
            arrays[(int)Mesh.ArrayType.Index] = surface.Indices;

            mesh ??= new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }

        return mesh;
    }

    private ShaderMaterial? GetMaterial(int liquidType) =>
        _materials.GetOrAdd(liquidType, id => new Lazy<ShaderMaterial?>(() => BuildMaterial(id))).Value;

    private ShaderMaterial? BuildMaterial(int liquidType)
    {
        if (!_types.TryGetValue(liquidType, out var type))
        {
            GD.PushWarning($"Unknown liquid type: {liquidType}");
            return null;
        }

        var frames = _frames.GetOrAdd(type.TexturePattern, pattern => new Lazy<(Texture2DArray, int)?>(() => LoadFrames(pattern))).Value;
        if (frames is not { } loaded)
        {
            GD.PushWarning($"No frames for liquid {type.Name}: {type.TexturePattern}");
            return null;
        }

        var material = new ShaderMaterial { Shader = LiquidShader };
        material.SetShaderParameter("frames", loaded.Frames);
        material.SetShaderParameter("frame_count", loaded.Count);

        var textured = type.Kind is LiquidKind.Magma or LiquidKind.Slime;
        material.SetShaderParameter("textured", textured);
        material.SetShaderParameter("paint_noise", PaintNoise);

        // Oceans paint bigger shapes than rivers. Halving keeps them seamless, as a tile is 32 repeats.
        material.SetShaderParameter("paint_scale", type.Kind == LiquidKind.Ocean ? 0.5f : 1f);
        material.SetShaderParameter("flow", type.Kind switch
        {
            LiquidKind.Magma => MagmaFlow,
            LiquidKind.Slime => SlimeFlow,
            _ => Vector2.Zero,
        });

        MaterialCreated?.Invoke(material, type.Kind);
        return material;
    }

    private (Texture2DArray, int)? LoadFrames(string pattern)
    {
        var images = new Godot.Collections.Array<Image>();
        var size = 0;
        for (var frame = 1; frame <= MaxFrames; frame++)
        {
            if (_source.Read(pattern.Replace("%d", frame.ToString())) is not { Length: > 0 } data)
            {
                break;
            }

            if (size == 0)
            {
                size = Math.Min(Math.Max(BlpDecoder.ReadSize(data).Width, 1), MaxFrameSize);
            }

            images.Add(Image.CreateFromData(size, size, true, Image.Format.Dxt1, BlpDecoder.ConvertToBc1(data, size)));

            // A pattern with no %d is a single picture.
            if (!pattern.Contains("%d"))
            {
                break;
            }
        }

        if (images.Count == 0)
        {
            return null;
        }

        var frames = new Texture2DArray();
        frames.CreateFromImages(images);
        return (frames, images.Count);
    }
}
