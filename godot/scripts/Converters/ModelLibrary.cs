using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.Textures;
using Godot;

namespace WowVR.Converters;

/// <summary>An M2 ready to draw.</summary>
public sealed record M2Asset(ArrayMesh Mesh, float Radius, Vector3[] CollisionFaces, ModelMaterial[] Materials)
{
    /// <summary>Its particle emitters.</summary>
    public ParticleAsset[] Particles { get; init; } = [];

    /// <summary>The same mesh for copies inside buildings, made when one first needs it.</summary>
    public ArrayMesh? InteriorMesh { get; set; }

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

    /// <summary>Null when the group has no liquid.</summary>
    public ArrayMesh? LiquidMesh { get; init; }

    /// <summary>The same liquid's height at any point, and its LiquidType row.</summary>
    public LiquidHeightField? LiquidHeights { get; init; }
    public int LiquidType { get; init; }

    public bool Indoor { get; init; }

    /// <summary>The light baked into an indoor group's floor. Null for an outdoor group, or one with no vertex colors.</summary>
    public WmoFloorLight? FloorLight { get; init; }

    public ushort[] DoodadRefs { get; init; } = [];

    public uint Flags { get; init; }

    public (System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) FileBounds { get; init; }

    /// <summary>What an indoor group covers from above, to tell its room from its box.</summary>
    public GroupFootprint? Footprint { get; init; }

    public (int Start, int Count) PortalRange { get; init; }

    /// <summary>Null when the group has nothing to collide with.</summary>
    public ConcavePolygonShape3D? Shape { get; init; }

    /// <summary>Around the collision faces.</summary>
    public Aabb Bounds { get; init; }

    /// <summary>The middle of what's drawn.</summary>
    public Vector3 Center { get; init; }
}

/// <summary>A WMO ready to place.</summary>
public sealed record WmoAsset(WmoRoot Root, WmoGroupAsset[] Groups)
{
    private readonly HashSet<int> _interiorDoodads = Groups
        .Where(g => g.Indoor)
        .SelectMany(g => g.DoodadRefs)
        .Select(d => (int)d)
        .ToHashSet();

    /// <summary>Null for a WMO with nothing to hide, because it has no interior behind a portal.</summary>
    public WmoPortals? Portals { get; } = BuildPortals(Root, Groups);

    /// <summary>Which group a doodad shows and hides with: the first indoor one listing it. -1 for one that always shows.</summary>
    public int GroupOf(WmoDoodad doodad)
    {
        if (Portals is null)
        {
            return -1;
        }

        for (var i = 0; i < Groups.Length; i++)
        {
            if (Groups[i].Indoor && System.Array.IndexOf(Groups[i].DoodadRefs, (ushort)doodad.Index) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// An indoor doodad's light. With no color of its own it takes the floor's where it stands,
    /// as the ambient alone is black in WMOs such as Duskwood's crypts. See docs/formats.md.
    /// </summary>
    public System.Numerics.Vector3 LightOf(WmoDoodad doodad)
    {
        var light = Root.Ambient + doodad.Color;
        if (doodad.Color == System.Numerics.Vector3.Zero)
        {
            foreach (var group in Groups)
            {
                if (group.Indoor
                    && System.Array.IndexOf(group.DoodadRefs, (ushort)doodad.Index) >= 0
                    && group.FloorLight?.Sample(doodad.Transform.Origin) is { } floor)
                {
                    light = floor;
                    break;
                }
            }
        }

        return WmoFloorLight.ForDoodad(light);
    }

    /// <summary>In the file's axes, as the portals are. The node turns the camera into them.</summary>
    private static WmoPortals? BuildPortals(WmoRoot root, WmoGroupAsset[] groups)
    {
        if (root.Portals.Length == 0 || !groups.Any(g => g.Indoor))
        {
            return null;
        }

        var references = new List<(int, int, int, int)>();
        for (var group = 0; group < groups.Length; group++)
        {
            var (start, count) = groups[group].PortalRange;
            for (var i = start; i < start + count && i < root.PortalRefs.Length; i++)
            {
                var (portal, other, side) = root.PortalRefs[i];
                references.Add((group, portal, other, side));
            }
        }

        return new WmoPortals(
            groups.Select(g => g.Flags).ToArray(),
            groups.Select(g => g.FileBounds).ToArray(),
            root.PortalVertices,
            root.Portals,
            references.ToArray(),
            groups.Select(g => g.Footprint).ToArray());
    }

    /// <summary>A doodad listed by an indoor group gets its light from there, as the client decides it. See docs/formats.md.</summary>
    public bool IsInterior(WmoDoodad doodad) => _interiorDoodads.Contains(doodad.Index);
}

/// <summary>
/// Loads models once and shares them between tiles. Runs off the main thread. Files that
/// can't be read are remembered as missing, so they're only reported once.
/// </summary>
public sealed class ModelLibrary(IAssetSource source, LiquidLibrary liquids)
{
    private const int MaxCachedModels = 1500;
    private const float PlainAlphaKey = 0.5f;
    private const int MaxCachedTextures = 1500;

    private readonly ConcurrentDictionary<string, M2Asset?> _m2s = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WmoAsset?> _wmos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(ModelMaterial Material, bool Indoor, bool Creature), Material?> _materials = new();

    /// <summary>What a creature's plain batches use, kept so that equal batches share a material.</summary>
    private static readonly M2Effect PlainOpaque = new() { Combiner = M2Combiners.Opaque, Uv0 = M2UvSource.First, Uv1 = M2UvSource.First };
    private static readonly M2Effect PlainMod = new() { Combiner = M2Combiners.Mod, Uv0 = M2UvSource.First, Uv1 = M2UvSource.First };
    private readonly ConcurrentDictionary<string, Shader> _effectShaders = new();
    private readonly ConcurrentDictionary<M2BoneBake, ImageTexture> _boneTextures = new();

    /// <summary>Materials whose look changes over time, with the cache generation they were made in.</summary>
    private readonly List<(ShaderMaterial Material, M2Effect Effect, int Generation)> _animated = [];
    private readonly ConcurrentQueue<(ShaderMaterial Material, M2Effect Effect, int Generation)> _newAnimated = new();
    private int _generation;
    private ParticleLibrary? _particles;
    private int _prunedGeneration;

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
            _boneTextures.Clear();
            _textures.Clear();
            _materials.Clear();
            Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>
    /// Moves every animated material to this moment, all at once because models share them.
    /// Materials from before a cache trim go when nothing's left using them.
    /// </summary>
    public void AnimateMaterials(double clock)
    {
        while (_newAnimated.TryDequeue(out var added))
        {
            _animated.Add(added);
        }

        var generation = Volatile.Read(ref _generation);
        if (generation != _prunedGeneration)
        {
            _prunedGeneration = generation;
            _animated.RemoveAll(a => a.Generation < generation && a.Material.GetReferenceCount() <= 1);
        }

        foreach (var (material, effect, _) in _animated)
        {
            ApplyEffect(material, effect, clock);
        }
    }

    public int AnimatedMaterialCount => _animated.Count;

    /// <summary>A material shared with everything else that looks the same. Null when its texture can't be read.</summary>
    public Material? MaterialFor(ModelMaterial material) => GetMaterial(material, indoor: false);

    /// <summary>
    /// A material for a creature, shared in the same way. Each creature tells it how much indoor
    /// light it stands in, through an instance uniform, so even a plain batch needs the shader.
    /// </summary>
    public Material? CreatureMaterialFor(ModelMaterial material)
    {
        var key = material.Effect is not null
            ? material
            : material with
            {
                Effect = material.Blend == ModelBlend.Opaque ? PlainOpaque : PlainMod,
                Unlit = material.Unlit || material.Blend == ModelBlend.Add,
            };
        return _materials.GetOrAdd((key, false, true), k => BuildEffectMaterial(k.Material, k.Material.Effect!, indoor: false, creature: true));
    }

    public ParticleLibrary Particles => _particles ??= new ParticleLibrary(GetTexture);

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

            if (LoadAnimatedM2(path, m2, skin) is { } animated)
            {
                return animated;
            }

            var model = M2Model.Parse(m2, skin);
            var used = new List<ModelMaterial>();
            return BuildMesh(model.Surfaces, indoor: false, used) is { } mesh
                ? new M2Asset(mesh, model.Radius, model.CollisionFaces.Select(ToGodot).ToArray(), used.ToArray()) { Particles = model.Emitters.Count > 0 ? DoodadParticles(ParseStand(path, m2, skin)) : [] }
                : null;
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private M2SkinnedModel ParseStand(string path, byte[] m2, byte[] skin) => M2SkinnedModel.Parse(
        m2,
        skin,
        s => s.Id == M2SequenceIds.Stand && s.Variation == 0,
        s => source.Read(s.AnimPath(path)));

    private ParticleAsset[] DoodadParticles(M2SkinnedModel data) =>
        Particles.Build(data.Emitters, e => data.Clips.Count > 0 ? M2BoneBake.EmitterPath(data, data.Clips[0], e) : null);

    /// <summary>A doodad with a stand animation that moves, baked for its vertex shader. Null for any other.</summary>
    private M2Asset? LoadAnimatedM2(string path, byte[] m2, byte[] skin)
    {
        var data = ParseStand(path, m2, skin);
        if (data.Clips.Count == 0 || M2BoneBake.TryCreate(data, data.Clips[0]) is not { } bake)
        {
            return null;
        }

        var byMaterial = new System.Collections.Generic.Dictionary<ModelMaterial, List<int[]>>();
        foreach (var batch in data.Batches)
        {
            if (batch.Texture is null)
            {
                continue;
            }

            // A plain batch needs this shader too, and looks as it would without one.
            var effect = batch.Effect ?? new M2Effect
            {
                Combiner = batch.Blend == ModelBlend.Opaque ? M2Combiners.Opaque : M2Combiners.Mod,
                Uv0 = M2UvSource.First,
                Uv1 = M2UvSource.First,
            };
            var unlit = batch.Unlit || (batch.Effect is null && batch.Blend == ModelBlend.Add);
            var billboard = batch.Indices.Any(v => VertexBillboard(data, v).Kind != M2BillboardKind.None);
            var key = new ModelMaterial(batch.Texture, batch.Blend, unlit, batch.TwoSided, batch.Repeat, effect, effect.Texture2Name, bake, billboard);
            if (!byMaterial.TryGetValue(key, out var parts))
            {
                byMaterial[key] = parts = [];
            }

            parts.Add(batch.Indices);
        }

        var bounds = BoundsOf(data.Positions.Select(ToGodot).ToArray());
        var sequence = data.Clips[0].Sequence;
        bounds = bounds.Merge(new Aabb(ToGodot(sequence.BoundsMin), ToGodot(sequence.BoundsMax - sequence.BoundsMin)));
        ArrayMesh? mesh = null;
        var used = new List<ModelMaterial>();
        foreach (var (key, parts) in byMaterial)
        {
            if (GetMaterial(key, indoor: false) is { } material)
            {
                used.Add(key);
                mesh ??= new ArrayMesh { CustomAabb = bounds };
                AddBakedSurface(mesh, data, bake, parts, material, key.Effect!.UsesSecondUv, key.Billboard);
            }
        }

        if (mesh is null)
        {
            return null;
        }

        var faces = M2Model.Parse(m2, skin).CollisionFaces.Select(ToGodot).ToArray();
        return new M2Asset(mesh, Mathf.Max(bounds.Size.Length() / 2f, 0.1f), faces, used.ToArray()) { Particles = DoodadParticles(data) };
    }

    public static void AddBakedSurface(ArrayMesh mesh, M2SkinnedModel data, M2BoneBake bake, List<int[]> parts, Material material, bool secondUv, bool billboard)
    {
        var remap = new int[data.Positions.Length];
        Array.Fill(remap, -1);
        var used = new List<int>();
        var indices = new int[parts.Sum(p => p.Length)];
        var next = 0;
        foreach (var part in parts)
        {
            foreach (var vertex in part)
            {
                if (remap[vertex] < 0)
                {
                    remap[vertex] = used.Count;
                    used.Add(vertex);
                }

                indices[next++] = remap[vertex];
            }
        }

        var bones = new byte[used.Count * 4];
        var weights = new byte[used.Count * 4];
        for (var i = 0; i < used.Count; i++)
        {
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = data.BoneWeights[used[i] * 4 + slot];
                var bone = bake.BoneRemap[data.BoneIndices[used[i] * 4 + slot]];
                bones[i * 4 + slot] = weight > 0 && bone >= 0 ? (byte)bone : (byte)0;
                weights[i * 4 + slot] = bone >= 0 ? weight : (byte)0;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = used.Select(v => ToGodot(data.Positions[v])).ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = used.Select(v => ToGodot(data.Normals[v])).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = used.Select(v => new Vector2(data.TexCoords[v].X, data.TexCoords[v].Y)).ToArray();
        if (secondUv)
        {
            arrays[(int)Mesh.ArrayType.TexUV2] = used.Select(v => new Vector2(data.TexCoords2[v].X, data.TexCoords2[v].Y)).ToArray();
        }

        arrays[(int)Mesh.ArrayType.Custom0] = bones;
        arrays[(int)Mesh.ArrayType.Custom1] = weights;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var customFormat = ((long)Mesh.ArrayCustomFormat.Rgba8Unorm << (int)Mesh.ArrayFormat.FormatCustom0Shift)
            | ((long)Mesh.ArrayCustomFormat.Rgba8Unorm << (int)Mesh.ArrayFormat.FormatCustom1Shift);
        if (billboard)
        {
            var turning = new float[used.Count * 4];
            for (var i = 0; i < used.Count; i++)
            {
                var (kind, pivot) = VertexBillboard(data, used[i]);
                (turning[i * 4], turning[i * 4 + 1], turning[i * 4 + 2], turning[i * 4 + 3]) = (pivot.X, pivot.Y, pivot.Z, (float)kind);
            }

            arrays[(int)Mesh.ArrayType.Custom2] = turning;
            customFormat |= (long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift;
        }

        var format = (Mesh.ArrayFormat)customFormat;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: format);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>What turns a vertex to the viewer: its heaviest bone decides.</summary>
    public static (M2BillboardKind Kind, System.Numerics.Vector3 Pivot) VertexBillboard(M2SkinnedModel data, int vertex)
    {
        var heaviest = 0;
        for (var slot = 1; slot < 4; slot++)
        {
            if (data.BoneWeights[vertex * 4 + slot] > data.BoneWeights[vertex * 4 + heaviest])
            {
                heaviest = slot;
            }
        }

        return M2BoneBake.BillboardOf(data, data.BoneIndices[vertex * 4 + heaviest]);
    }

    private ImageTexture BoneTexture(M2BoneBake bake) => _boneTextures.GetOrAdd(bake, b =>
    {
        var bytes = new byte[b.Texels.Length * sizeof(float)];
        Buffer.BlockCopy(b.Texels, 0, bytes, 0, bytes.Length);
        return ImageTexture.CreateFromImage(Image.CreateFromData(b.BoneCount * M2BoneBake.RowsPerBone, b.Frames, false, Image.Format.Rgbaf, bytes));
    });

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

            // A group's vertex colors depend on what its neighbors are, so all are read first.
            var parsed = new WmoGroup?[root.GroupCount];
            for (var i = 0; i < parsed.Length; i++)
            {
                var groupData = source.Read(WmoRoot.GroupPath(path, i));
                if (groupData is { Length: > 0 })
                {
                    parsed[i] = WmoGroup.Parse(groupData, root);
                }
                else
                {
                    GD.PushWarning($"WMO group missing: {WmoRoot.GroupPath(path, i)}");
                }
            }

            var groups = new WmoGroupAsset[root.GroupCount];
            for (var i = 0; i < groups.Length; i++)
            {
                WmoVertexLight.Apply(root, parsed, i);
                groups[i] = parsed[i] is { } group ? LoadGroup(group) : new WmoGroupAsset();
            }

            return new WmoAsset(root, groups);
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private WmoGroupAsset LoadGroup(WmoGroup group)
    {
        var faces = group.CollisionFaces.Select(ToGodot).ToArray();
        var mesh = BuildMesh(group.Surfaces, group.Indoor, null);

        return new WmoGroupAsset
        {
            Mesh = mesh,
            LiquidMesh = group.Liquid is { } liquid ? liquids.BuildMesh([liquid]) : null,
            LiquidHeights = group.Liquid is { } surface ? new LiquidHeightField(surface) : null,
            LiquidType = group.Liquid?.LiquidType ?? 0,
            Center = mesh?.GetAabb().GetCenter() ?? Vector3.Zero,
            Indoor = group.Indoor,
            FloorLight = group.Indoor ? WmoFloorLight.Build(group.Surfaces) : null,
            DoodadRefs = group.DoodadRefs,
            Flags = group.Flags,
            FileBounds = group.Bounds,
            Footprint = group.Indoor ? GroupFootprint.Build(group.Surfaces, group.Bounds) : null,
            PortalRange = group.PortalRange,
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

    /// <summary>A model's mesh for copies inside buildings: its surfaces, with unlit materials that read each copy's color.</summary>
    public ArrayMesh InteriorMeshOf(M2Asset model)
    {
        lock (model)
        {
            if (model.InteriorMesh is { } existing)
            {
                return existing;
            }

            var mesh = (ArrayMesh)model.Mesh.Duplicate();
            for (var i = 0; i < model.Materials.Length && i < mesh.GetSurfaceCount(); i++)
            {
                // Unlit parts, such as a lantern's glass, glow the same anywhere, and keep their material.
                if (!model.Materials[i].Unlit && GetMaterial(model.Materials[i], indoor: true) is { } material)
                {
                    mesh.SurfaceSetMaterial(i, material);
                }
            }

            model.InteriorMesh = mesh;
            return mesh;
        }
    }

    /// <summary>A model's mesh as a skybox, with its own materials, since the weight is per sky.</summary>
    public (ArrayMesh Mesh, (ShaderMaterial Material, M2Effect Effect)[] Materials) SkyboxMeshOf(M2Asset model, bool behindClouds = false)
    {
        var mesh = (ArrayMesh)model.Mesh.Duplicate();
        var materials = new List<(ShaderMaterial, M2Effect)>();
        for (var i = 0; i < model.Materials.Length && i < mesh.GetSurfaceCount(); i++)
        {
            var source = model.Materials[i];
            var effect = source.Effect ?? (source.Blend == ModelBlend.Opaque ? PlainOpaque : PlainMod);
            if (BuildEffectMaterial(source with { Unlit = true }, effect, indoor: false, creature: false, skybox: true, behindClouds) is { } material)
            {
                mesh.SurfaceSetMaterial(i, material);
                materials.Add((material, effect));
            }
        }

        mesh.CustomAabb = new Aabb(-Vector3.One * 1e6f, Vector3.One * 2e6f);
        return (mesh, materials.ToArray());
    }

    private ArrayMesh? BuildMesh(IReadOnlyList<ModelSurface> surfaces, bool indoor, List<ModelMaterial>? used)
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
            if (surface.TexCoords2 is { } texCoords2)
            {
                arrays[(int)Mesh.ArrayType.TexUV2] = texCoords2.Select(v => new Vector2(v.X, v.Y)).ToArray();
            }

            // Only indoor groups use their colors, as WoWModelViewer does.
            if (indoor && surface.Colors is { } colors)
            {
                var vertexColors = new Color[colors.Length / 4];
                for (var i = 0; i < vertexColors.Length; i++)
                {
                    vertexColors[i] = Color.Color8(colors[i * 4], colors[i * 4 + 1], colors[i * 4 + 2], colors[i * 4 + 3]);
                }

                arrays[(int)Mesh.ArrayType.Color] = vertexColors;
            }

            used?.Add(surface.Material);
            mesh ??= new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
        }

        return mesh;
    }

    private Material? GetMaterial(ModelMaterial material, bool indoor) =>
        _materials.GetOrAdd((material, indoor, false), key => key.Material.Effect is { } effect
            ? BuildEffectMaterial(key.Material, effect, key.Indoor, creature: false)
            : key.Material.Blend is ModelBlend.Alpha or ModelBlend.Add or ModelBlend.Modulate

                // Blending needs the shader, to match the client's and to fade in fog. See its GAMMA_ADD and FOG_TO_BLACK.
                ? BuildEffectMaterial(key.Material with { Unlit = key.Material.Unlit || key.Material.Blend == ModelBlend.Add }, PlainMod, key.Indoor, creature: false)
                : key.Indoor

                    // So does ambient occlusion indoors. See its INDOOR_OCCLUSION.
                    ? BuildEffectMaterial(key.Material, key.Material.Blend == ModelBlend.Opaque ? PlainOpaque : PlainMod, indoor: true, creature: false)
                    : key.Material.Shader is WmoShader.Specular or WmoShader.Metal or WmoShader.EnvironmentMetal && !key.Material.Unlit
                        ? BuildSpecularMaterial(key.Material)
                        : BuildMaterial(key.Material, key.Indoor));

    /// <summary>The outside of a WMO, lit by the sun, whose shader has a highlight.</summary>
    private ShaderMaterial? BuildSpecularMaterial(ModelMaterial material)
    {
        if (GetTexture(material.Texture) is not { } texture)
        {
            return null;
        }

        var alphaKey = material.Blend == ModelBlend.AlphaKey;
        var cull = material.TwoSided ? "cull_disabled" : "cull_back";
        var shader = _effectShaders.GetOrAdd($"wmo specular {cull} {alphaKey}", _ => new Shader
        {
            Code = $"shader_type spatial;\nrender_mode {cull};\n{(alphaKey ? "#define ALPHA_KEY\n" : "")}#include \"res://shaders/wmo_specular.gdshaderinc\"\n",
        });

        var result = new ShaderMaterial { Shader = shader };
        result.SetShaderParameter("albedo_texture", texture);
        result.SetShaderParameter("mode", (int)material.Shader);
        return result;
    }

    private ShaderMaterial? BuildEffectMaterial(ModelMaterial material, M2Effect effect, bool indoor, bool creature, bool skybox = false, bool behindClouds = false)
    {
        if (GetTexture(material.Texture) is not { } texture)
        {
            return null;
        }

        var result = new ShaderMaterial { Shader = EffectShader(material, effect, indoor, creature, skybox, behindClouds) };
        if (skybox)
        {
            // Before anything else that's transparent, since it's behind everything.
            result.RenderPriority = (int)RenderingServer.MaterialRenderPriorityMin;
        }

        if (ReferenceEquals(effect, PlainMod))
        {
            result.SetShaderParameter("alpha_key", PlainAlphaKey);
        }

        result.SetShaderParameter("tex0", texture);
        if (effect.HasTwoTextures && material.Texture2 is { } second && GetTexture(second) is { } texture2)
        {
            result.SetShaderParameter("tex1", texture2);
        }

        result.SetShaderParameter("combiner", effect.Combiner);
        result.SetShaderParameter("uv_source0", (int)effect.Uv0);
        result.SetShaderParameter("uv_source1", (int)effect.Uv1);
        ApplyEffect(result, effect, 0);

        if (material.Bake is { } bake)
        {
            result.SetShaderParameter("bone_texture", BoneTexture(bake));
            result.SetShaderParameter("bake_seconds", bake.Seconds);
            result.SetShaderParameter("bake_frames", bake.Frames);

            // A batch with no effect of its own keeps the threshold still doodads use.
            if (effect is { Combiner: M2Combiners.Mod, Color: null, Weight: null, ColorAlpha: null, Uv0Animation: null })
            {
                result.SetShaderParameter("alpha_key", PlainAlphaKey);
            }
        }

        // A skybox's owner runs its effects, because some follow the time of day.
        if (effect.Animated && !skybox)
        {
            _newAnimated.Enqueue((result, effect, Volatile.Read(ref _generation)));
        }

        return result;
    }

    // Made once: passing a string makes a new StringName on every call, which is garbage, and these are set every frame.
    private static readonly StringName UvLinear0 = "uv_linear0";
    private static readonly StringName UvOffset0 = "uv_offset0";
    private static readonly StringName UvLinear1 = "uv_linear1";
    private static readonly StringName UvOffset1 = "uv_offset1";
    private static readonly StringName Tint = "tint";
    private static readonly StringName ElementAlpha = "element_alpha";

    public static void ApplyEffect(ShaderMaterial material, M2Effect effect, double clock)
    {
        // Vectors rather than a Basis: a Basis goes through a Variant with a finalizer, and this is every frame.
        if (effect.Uv0Animation is { } uv0)
        {
            var m = uv0.At(clock);
            material.SetShaderParameter(UvLinear0, new Vector4(m.A, m.C, m.B, m.D));
            material.SetShaderParameter(UvOffset0, new Vector2(m.X, m.Y));
        }

        if (effect.Uv1Animation is { } uv1)
        {
            var m = uv1.At(clock);
            material.SetShaderParameter(UvLinear1, new Vector4(m.A, m.C, m.B, m.D));
            material.SetShaderParameter(UvOffset1, new Vector2(m.X, m.Y));
        }

        if (effect.Color is not null)
        {
            var tint = effect.TintAt(clock);
            material.SetShaderParameter(Tint, new Vector3(tint.X, tint.Y, tint.Z));
        }

        if (effect.ColorAlpha is not null || effect.Weight is not null)
        {
            material.SetShaderParameter(ElementAlpha, effect.AlphaAt(clock));
        }
    }

    /// <summary>Render modes and wrap modes are fixed in a shader's code, so each mix gets a shader.</summary>
    private Shader EffectShader(ModelMaterial material, M2Effect effect, bool indoor, bool creature, bool skybox = false, bool behindClouds = false)
    {
        var modulates = material.Blend == ModelBlend.Modulate;

        // An opaque part of a creature, such as an eyelid, is tinted to match a body that's lit.
        var tintReplacesLight = effect.Tinted && !(creature && material.Blend == ModelBlend.Opaque);
        var unshaded = material.Unlit || modulates || tintReplacesLight || indoor;
        var blended = skybox || material.Blend is ModelBlend.Alpha or ModelBlend.Add || effect.Fades;
        var baked = material.Bake is not null;
        var floorLit = creature && !unshaded;

        // Blended surfaces don't write depth or take occlusion, and glows shouldn't be dimmed.
        var indoorOccluded = indoor && !material.Unlit && !modulates && !effect.Tinted && !blended;
        // Glows and glass have no shape to show. The shader leaves a WMO's own walls alone, which get no direction.
        var shaped = indoor && !material.Unlit && !effect.Tinted && material.Blend is ModelBlend.Opaque or ModelBlend.AlphaKey;
        var key = $"{material.Blend}_{material.TwoSided}_{material.Repeat}_{effect.Repeat2}_{unshaded}_{blended}_{baked}_{material.Billboard}_{indoor}_{floorLit}_{indoorOccluded}_{shaped}_{skybox}_{behindClouds}";
        return _effectShaders.GetOrAdd(key, _ =>
        {
            var modes = new List<string> { material.TwoSided ? "cull_disabled" : "cull_back" };
            if (unshaded && !indoorOccluded)
            {
                modes.Add("unshaded");
            }

            if (material.Blend == ModelBlend.Add)
            {
                modes.Add("blend_add");
                modes.Add("fog_disabled");
            }
            else if (modulates)
            {
                modes.Add("blend_mul");
                modes.Add("fog_disabled");
            }

            var defines = new List<string>
            {
                $"#define TEX0_WRAP {(material.Repeat ? "repeat_enable" : "repeat_disable")}",
                $"#define TEX1_WRAP {(effect.Repeat2 ? "repeat_enable" : "repeat_disable")}",
            };
            if (skybox)
            {
                defines.Add("#define SKYBOX");
                if (behindClouds)
                {
                    defines.Add("#define BEHIND_CLOUDS");
                }

                modes.Add("skip_vertex_transform");
                modes.Add("depth_draw_never");
                modes.Add("shadows_disabled");
                if (material.Blend != ModelBlend.Add && !modulates)
                {
                    modes.Add("fog_disabled");
                }
            }

            if (unshaded)
            {
                defines.Add("#define UNSHADED");
            }

            if (blended || modulates)
            {
                defines.Add("#define BLENDED");
            }

            if (material.Blend == ModelBlend.AlphaKey)
            {
                defines.Add("#define ALPHA_KEY");
            }

            if (material.Blend == ModelBlend.Add)
            {
                defines.Add("#define FOG_TO_BLACK");
            }
            else if (modulates)
            {
                defines.Add("#define FOG_TO_WHITE");
            }

            if (baked)
            {
                defines.Add("#define BAKED_BONES");
            }

            if (indoor)
            {
                defines.Add("#define INSTANCE_LIGHT");
            }

            if (floorLit)
            {
                defines.Add("#define FLOOR_LIGHT");
            }

            if (shaped)
            {
                defines.Add("#define SHAPED_LIGHT");
            }

            if (indoorOccluded)
            {
                defines.Add("#define INDOOR_OCCLUSION");
            }

            if (material.Blend == ModelBlend.Add && unshaded)
            {
                defines.Add("#define GAMMA_ADD");
            }
            else if (material.Blend == ModelBlend.Alpha)
            {
                defines.Add("#define GAMMA_ALPHA");
            }

            if (material.Billboard)
            {
                defines.Add("#define BILLBOARD");
                modes.Add("skip_vertex_transform");
            }

            return new Shader
            {
                Code = $"shader_type spatial;\nrender_mode {string.Join(", ", modes.Distinct())};\n{string.Join("\n", defines)}\n#include \"res://shaders/m2_combiners.gdshaderinc\"\n",
            };
        });
    }

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
        }

        return result;
    }

    public ImageTexture? GetTexture(string path) => _textures.GetOrAdd(path, LoadTexture);

    /// <summary>A texture made here rather than read, such as a character's painted body, under a name materials can ask for.</summary>
    public void AddTexture(string name, ImageTexture texture) => _textures[name] = texture;

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
