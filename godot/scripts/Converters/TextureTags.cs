using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WowVR.Formats.Assets;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Converters;

/// <summary>
/// A dev tool for trying out upscaled textures: tagged textures are saved as PNGs in a folder laid
/// out as the MPQs are (<c>World\Azeroth\x.blp</c> as <c>World/Azeroth/x.png</c>), so it can be
/// packed again, and whatever is in that folder is loaded instead of the client's texture and
/// counts as tagged. Beside each PNG, a <c>.json</c> says how the original was stored and how
/// much of the world one repeat of it covers where it's been tagged, to size its upscale by. While the texture brush is on, what
/// uses a tagged texture is tinted. Thread-safe: textures load off the main thread.
/// </summary>
public static class TextureTags
{
    private const string FolderArgument = "--textures=";

    /// <summary>Magenta, by its alpha: rare in WoW's textures, so it stands out.</summary>
    private static readonly Vector4 TintColor = new(1f, 0.25f, 1f, 0.55f);
    private static readonly Color StandardTint = new(1f, 0.55f, 1f);
    private static readonly StringName DevTint = "dev_tint";
    private static readonly StringName TaggedLayers = "tagged_layers";
    private const int MaxTerrainLayers = 64;

    /// <summary>Meta on a building's group or a doodad batch naming what it draws, for the notes of what's tagged in view.</summary>
    public static readonly StringName WhereMeta = "texture_where";

    private static readonly ConcurrentDictionary<string, byte> Tagged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What each material draws with, to tint it and to know what a surface under the brush shows.</summary>
    private static readonly ConditionalWeakTable<Material, string[]> MaterialTextures = new();
    private static readonly ConcurrentDictionary<string, ConcurrentBag<WeakReference<Material>>> MaterialsByTexture = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each terrain tile's material, with its layers' textures in order.</summary>
    private static readonly ConcurrentBag<(WeakReference<ShaderMaterial> Material, string[] Layers)> Terrains = [];

    private static readonly object NotesLock = new();

    /// <summary>The folder, from <c>--textures=</c>, else the settings' <c>[dev] texture_folder</c>.</summary>
    public static string Folder { get; } = ProjectSettings.GlobalizePath(
        Array.Find(OS.GetCmdlineUserArgs(), a => a.StartsWith(FolderArgument))?[FolderArgument.Length..] ?? AppSettings.TextureFolder);

    /// <summary>Whether tagged textures show tinted, which is while the brush is on.</summary>
    private static bool TintShown => AppSettings.TextureBrush;

    public static int TaggedCount => Tagged.Count;

    public static bool IsTagged(string texture) => Tagged.ContainsKey(texture);

    /// <summary>
    /// Where a texture's PNG goes: its MPQ path under the folder, in lower case with forward slashes.
    /// The client names one file in several cases, and MPQs ignore case, but not every file system does.
    /// </summary>
    public static string PngPath(string texture) => Path.Combine(Folder, Path.ChangeExtension(SafeRelative(texture), ".png"));

    /// <summary>
    /// A texture's name as a path under the folder: lower case, forward slashes, and nothing that
    /// leaves it, since names come from game files that a patch could fill with "..", a drive or a root.
    /// </summary>
    private static string SafeRelative(string texture) =>
        string.Join('/', texture.Replace('\\', '/').ToLowerInvariant().Split('/')
            .Where(part => part.Length > 0 && part != "." && part != ".." && part.IndexOf(':') < 0));

    public static string NotesPath(string texture) => Path.ChangeExtension(PngPath(texture), ".json");

    /// <summary>Where notes went before they were JSON, read once and replaced.</summary>
    private static string OldNotesPath(string texture) => Path.ChangeExtension(PngPath(texture), ".txt");

    /// <summary>The folder's PNG for a texture, as RGBA, which then counts as tagged; null without one.</summary>
    public static Image? LoadReplacement(string texture)
    {
        var png = PngPath(texture);
        if (!File.Exists(png))
        {
            return null;
        }

        var image = Image.LoadFromFile(png);
        if (image is null || image.IsEmpty())
        {
            GD.PushWarning($"Could not read {png}");
            return null;
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        Tagged[texture] = 0;
        return image;
    }

    /// <summary>Notes what a material draws with, and tints it if one is tagged.</summary>
    public static void Register(Material material, params string?[] textures)
    {
        var named = textures.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!).ToArray();
        if (named.Length == 0)
        {
            return;
        }

        MaterialTextures.AddOrUpdate(material, named);
        foreach (var texture in named)
        {
            MaterialsByTexture.GetOrAdd(texture, _ => []).Add(new WeakReference<Material>(material));
        }

        ApplyTint(material, named);
    }

    public static void RegisterTerrain(ShaderMaterial material, IReadOnlyList<string> layers)
    {
        var names = layers.ToArray();
        Terrains.Add((new WeakReference<ShaderMaterial>(material), names));
        ApplyTerrainTint(material, names);
    }

    /// <summary>The textures a material draws with, as registered; empty for one that wasn't.</summary>
    public static string[] TexturesOf(Material? material) =>
        material is not null && MaterialTextures.TryGetValue(material, out var textures) ? textures : [];

    /// <summary>
    /// Tags a texture: saves it as a PNG, unless the folder already has one, which may be an upscale,
    /// and notes how much one repeat of it covers where it's used. True when it's newly tagged.
    /// </summary>
    public static bool Tag(string texture, IAssetSource source, string where, double? areaPerRepeat)
    {
        var data = source.Read(texture);
        if (data is not { Length: > 0 })
        {
            return false;
        }

        var info = BlpDecoder.ReadInfo(data);
        var png = PngPath(texture);
        var added = false;
        if (!File.Exists(png))
        {
            var decoded = BlpDecoder.Decode(data);
            var image = Image.CreateFromData(decoded.Width, decoded.Height, false, Image.Format.Rgba8, decoded.Rgba8);
            Directory.CreateDirectory(Path.GetDirectoryName(png)!);
            if (image.SavePng(png) != Error.Ok)
            {
                GD.PushError($"Could not save {png}");
                return false;
            }

            added = true;
        }

        added |= Tagged.TryAdd(texture, 0);
        if (areaPerRepeat is { } area && area > 1e-6)
        {
            WriteNotes(texture, info, where, area);
        }

        return added;
    }

    /// <summary>Tints what uses a texture just tagged. Main thread.</summary>
    public static void Refresh(string texture)
    {
        if (MaterialsByTexture.TryGetValue(texture, out var materials))
        {
            foreach (var weak in materials)
            {
                if (weak.TryGetTarget(out var material))
                {
                    ApplyTint(material, TexturesOf(material));
                }
            }
        }

        foreach (var (weak, layers) in Terrains)
        {
            if (Array.Exists(layers, l => string.Equals(l, texture, StringComparison.OrdinalIgnoreCase)) && weak.TryGetTarget(out var material))
            {
                ApplyTerrainTint(material, layers);
            }
        }
    }

    /// <summary>Tints or clears everything, as the brush turns on or off. Main thread.</summary>
    public static void RefreshAll()
    {
        foreach (var texture in Tagged.Keys)
        {
            Refresh(texture);
        }
    }

    private static void ApplyTint(Material material, string[] textures)
    {
        var tinted = TintShown && Array.Exists(textures, IsTagged);
        switch (material)
        {
            case ShaderMaterial shader:
                shader.SetShaderParameter(DevTint, tinted ? TintColor : Vector4.Zero);
                break;
            case StandardMaterial3D standard:
                standard.AlbedoColor = tinted ? StandardTint : Colors.White;
                break;
        }
    }

    private static void ApplyTerrainTint(ShaderMaterial material, string[] layers)
    {
        var flags = new float[MaxTerrainLayers];
        var any = false;
        for (var i = 0; i < layers.Length && i < flags.Length; i++)
        {
            flags[i] = IsTagged(layers[i]) ? 1f : 0f;
            any |= flags[i] > 0;
        }

        material.SetShaderParameter(TaggedLayers, flags);
        material.SetShaderParameter(DevTint, TintShown && any ? TintColor : Vector4.Zero);
    }

    /// <summary>
    /// The notes beside a PNG: how the original was stored (its BLP version, and which DXT when it
    /// was compressed), and one entry per place it's been tagged, with how many square yards one
    /// repeat covers there and the texture's texels per square yard and per yard along an edge.
    /// The largest area comes first, as what an upscale should be sized for.
    /// </summary>
    private static void WriteNotes(string texture, BlpInfo info, string where, double area)
    {
        lock (NotesLock)
        {
            var path = NotesPath(texture);
            var uses = new Dictionary<string, double>();
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<Notes>(File.ReadAllText(path), NotesJson) is { Uses: { } known })
                {
                    foreach (var use in known)
                    {
                        uses[use.Where] = use.Area;
                    }
                }
                else if (File.Exists(OldNotesPath(texture)))
                {
                    ReadOldNotes(OldNotesPath(texture), uses);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                GD.PushWarning($"Could not read the notes in {path}, starting them again: {e.Message}");
            }

            uses[where] = area;
            var texels = (double)info.Width * info.Height;
            Use Row(string name, double a) => new(name, Math.Round(a, 3), Math.Round(texels / a, 1), Math.Round(Math.Sqrt(texels / a), 2));

            Use[] ordered = [.. uses.OrderByDescending(u => u.Value).Select(u => Row(u.Key, u.Value))];
            var notes = new Notes(
                texture,
                info.Width,
                info.Height,
                info.Version,
                info.Encoding == BlpEncoding.Dxt ? $"DXT{info.Dxt}" : null,
                info.AlphaBits,
                ordered[0],
                ordered);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(notes, NotesJson));
            File.Delete(OldNotesPath(texture));
        }
    }

    /// <summary>The tab-separated notes written before, by where and area.</summary>
    private static void ReadOldNotes(string path, Dictionary<string, double> uses)
    {
        foreach (var line in File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#'))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 2 && parts[0] is not ("largest" or "where")
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var known))
            {
                uses[parts[0]] = known;
            }
        }
    }

    private static readonly JsonSerializerOptions NotesJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// A texture's notes. <c>version</c> is the BLP's (2 in WoW); <c>compression</c> is DXT1, DXT3
    /// or DXT5 when the original was, and missing when it was palettized or plain BGRA.
    /// <c>largest</c> is the use where one repeat covers the most, to size an upscale for.
    /// <c>area</c> is the square yards one repeat covers there, <c>density</c> the texels per
    /// square yard, <c>per_yard</c> the texels per yard along an edge.
    /// </summary>
    private sealed record Notes(string Texture, int Width, int Height, int Version, string? Compression, int AlphaBits, Use Largest, Use[] Uses);

    private sealed record Use(string Where, double Area, double Density, double PerYard);

    /// <summary>
    /// How many square yards one repeat of a surface's texture covers: its triangles' area in the
    /// world over their area in texture coordinates. Null for a surface with no texture area.
    /// </summary>
    public static double? AreaPerRepeat(Vector3[] positions, Vector2[] uvs, int[] indices, Transform3D toWorld)
    {
        var (world, texture) = Areas(positions, uvs, indices, toWorld);
        return texture > 1e-9 ? world / texture : null;
    }

    /// <summary>A surface's triangles' area in the world, and in texture coordinates, to add up over many.</summary>
    public static (double World, double Texture) Areas(Vector3[] positions, Vector2[] uvs, int[] indices, Transform3D toWorld)
    {
        double world = 0, texture = 0;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = (indices[i], indices[i + 1], indices[i + 2]);
            if (a >= positions.Length || b >= positions.Length || c >= positions.Length || a >= uvs.Length || b >= uvs.Length || c >= uvs.Length)
            {
                continue;
            }

            var pa = toWorld * positions[a];
            world += (toWorld * positions[b] - pa).Cross(toWorld * positions[c] - pa).Length() / 2;
            var (ua, ub, uc) = (uvs[a], uvs[b], uvs[c]);
            texture += Math.Abs(((ub.X - ua.X) * (uc.Y - ua.Y)) - ((uc.X - ua.X) * (ub.Y - ua.Y))) / 2;
        }

        return (world, texture);
    }
}
