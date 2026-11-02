using System;
using System.Collections.Concurrent;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Converters;

/// <summary>Turns a tile into a mesh, material and collision data. Runs off the main thread.</summary>
public sealed class TerrainBuilder(IAssetSource source, WdtMap map)
{
    /// <summary>A 1024 chain takes about 0.7 MB of video memory per layer.</summary>
    private const int MaxTextureSize = 1024;
    private const int FallbackTextureSize = 256;
    private const int MaxCachedTextures = 128;

    private static readonly Shader TerrainShader = GD.Load<Shader>("res://shaders/terrain.gdshader");

    private readonly ConcurrentDictionary<string, int> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Name, int Size), Image> _textures = new();

    public static void LoadShared() => _ = TerrainShader;

    public (ArrayMesh Mesh, TerrainCollisionData Collision, AdtTerrain Terrain) Build(int tileX, int tileY)
    {
        var path = map.TilePath(tileX, tileY);
        var data = source.Read(path) ?? throw new System.IO.FileNotFoundException("Not in the client.", path);
        var terrain = AdtTerrain.Parse(data, map.BigAlpha);
        var mesh = TerrainMeshData.Build(terrain);

        var result = BuildMesh(mesh);
        result.SurfaceSetMaterial(0, BuildMaterial(mesh));
        return (result, mesh.Collision, terrain);
    }

    private static ArrayMesh BuildMesh(TerrainMeshData data)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Positions.Select(ToGodot).ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals.Select(ToGodot).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = data.ChunkUvs.Select(ToGodot).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = data.AtlasUvs.Select(ToGodot).ToArray();
        arrays[(int)Mesh.ArrayType.Custom0] = data.TextureIndexes;
        arrays[(int)Mesh.ArrayType.Color] = ToColors(data.VertexColors);
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;

        var customFormat = (long)Mesh.ArrayCustomFormat.Rgba8Unorm << (int)Mesh.ArrayFormat.FormatCustom0Shift;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: (Mesh.ArrayFormat)customFormat);
        return mesh;
    }

    private ShaderMaterial BuildMaterial(TerrainMeshData mesh)
    {
        var size = mesh.Textures.Max(TextureSizeOf);
        size = size > 0 ? size : FallbackTextureSize;

        // Each chain is small, but a long trip would otherwise collect them all.
        if (_textures.Count > MaxCachedTextures)
        {
            _textures.Clear();
        }

        var images = new Godot.Collections.Array<Image>(mesh.Textures.Select(name => LoadTexture(name, size)));
        var layers = new Texture2DArray();
        layers.CreateFromImages(images);

        var atlas = Image.CreateFromData(
            TerrainMeshData.AtlasSize, TerrainMeshData.AtlasSize, false, Image.Format.Rgb8, mesh.AlphaAtlas);

        var material = new ShaderMaterial { Shader = TerrainShader };
        material.SetShaderParameter("layers", layers);
        material.SetShaderParameter("alpha_atlas", ImageTexture.CreateFromImage(atlas));
        material.SetShaderParameter("alpha_is_weights", map.BigAlpha);
        return material;
    }

    /// <summary>The largest power of two the texture covers, up to the limit, or 0 if it's missing.</summary>
    private int TextureSizeOf(string name) => _sizes.GetOrAdd(name, file =>
    {
        var data = source.Read(file);
        if (data is null)
        {
            return 0;
        }

        var (width, height) = BlpDecoder.ReadSize(data);
        var size = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(width, height) / 2 + 1);
        return Math.Min((int)size, MaxTextureSize);
    });

    /// <summary>Texture arrays need one size and format, so every layer becomes a BC1 chain of the tile's size.</summary>
    private Image LoadTexture(string name, int size) => _textures.GetOrAdd((name, size), key =>
    {
        var data = source.Read(key.Name);
        if (data is null)
        {
            GD.PushWarning($"Terrain texture missing: {key.Name}");
        }

        var mipmaps = data is null ? SolidMagentaBc1(key.Size) : BlpDecoder.ConvertToBc1(data, key.Size);
        return Image.CreateFromData(key.Size, key.Size, true, Image.Format.Dxt1, mipmaps);
    });

    private static byte[] SolidMagentaBc1(int size)
    {
        var bytes = 0;
        for (var level = size; level >= 1; level /= 2)
        {
            var blocks = (level + 3) / 4;
            bytes += blocks * blocks * 8;
        }

        var mipmaps = new byte[bytes];
        for (var i = 0; i < bytes; i += 8)
        {
            mipmaps[i] = 0x1F;
            mipmaps[i + 1] = 0xF8;
            mipmaps[i + 2] = 0x1F;
            mipmaps[i + 3] = 0xF8;
        }

        return mipmaps;
    }

    private static Color[] ToColors(byte[] bytes)
    {
        var colors = new Color[bytes.Length / 4];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = Color.Color8(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
        }

        return colors;
    }

    /// <summary>Formats already gives these in Godot axes, so this only changes the type.</summary>
    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector2 ToGodot(System.Numerics.Vector2 v) => new(v.X, v.Y);
}
