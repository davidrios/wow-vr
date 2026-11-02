using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Models;
using WowVR.Formats.World;
using WowVR.World;
using Godot;

namespace WowVR.Converters;

/// <summary>A worn item's mesh and where it goes, in its bone's space.</summary>
public sealed record CreatureItemAsset(string BoneName, Vector3 Offset, ArrayMesh Mesh, ParticleAsset[] Particles);

/// <summary>An emitter on a bone, at a position in that bone's space.</summary>
public sealed record CreatureParticle(string BoneName, Vector3 Offset, ParticleAsset Particle);

/// <summary>A moment in a clip that makes a sound.</summary>
public readonly record struct ClipSoundEvent(float Time, CreatureSoundKind Kind, int Index);

/// <summary>A clip the idle state can choose, by its weight.</summary>
public readonly record struct IdleClip(string Name, int Weight);

/// <summary>What's shared by every creature using a model.</summary>
public sealed class CreatureSkeletonAsset
{
    public required (string Name, int Parent, Vector3 Offset)[] Bones { get; init; }
    public required Skin Skin { get; init; }
    public required AnimationLibrary Animations { get; init; }
    public required IdleClip[] IdleClips { get; init; }
    public string? WalkClip { get; init; }
    public float WalkSpeed { get; init; }
    public required Aabb Bounds { get; init; }

    /// <summary>By clip name, sorted by time. Clips with no sounds aren't listed.</summary>
    public required Dictionary<string, ClipSoundEvent[]> ClipSounds { get; init; }

    /// <summary>Bones that turn to the viewer, in an order safe to apply.</summary>
    public required (int Bone, M2BillboardKind Kind)[] Billboards { get; init; }
}

/// <summary>A display ready to place. Each placed creature gets its own skeleton.</summary>
public sealed record CreatureAsset(CreatureSkeletonAsset Skeleton, ArrayMesh Mesh, float Scale, IReadOnlyList<CreatureItemAsset> Items, CreatureSoundSet? Sounds, IReadOnlyList<CreatureParticle> Particles);

/// <summary>Builds creatures off the main thread, once per display and skeleton per model.</summary>
public sealed class CreatureLibrary(IAssetSource source, CreatureDisplayTable displays, ModelLibrary models)
{
    private const float MinBoundsSize = 0.5f;
    private const int ShieldAttachment = 0;

    /// <summary>The right hand, then the left.</summary>
    private static readonly int[] HandAttachments = [1, 2];

    /// <summary>Stand's variations play at random, so this avoids building rare ones many times over.</summary>
    private const int MaxIdleClips = 4;

    private readonly ConcurrentDictionary<string, (M2SkinnedModel Data, CreatureSkeletonAsset Skeleton)?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string Model, string? Texture), (ArrayMesh Mesh, ParticleAsset[] Particles)?> _items = new();

    /// <summary>Null for a display that's unknown or can't be read.</summary>
    public CreatureAsset? Build(int displayId, int[] weapons)
    {
        try
        {
            if (displays.Resolve(displayId) is not { } look)
            {
                GD.PushWarning($"Unknown creature display: {displayId}");
                return null;
            }

            if (_models.GetOrAdd(look.Model, LoadModel) is not { } model)
            {
                return null;
            }

            var mesh = BuildMesh(model.Data, look, skinned: true, model.Skeleton.Bounds);
            if (mesh is null)
            {
                GD.PushWarning($"Creature display {displayId} has nothing to draw: {look.Model}");
                return null;
            }

            var items = new List<CreatureItemAsset>();
            foreach (var item in look.Items.Concat(WeaponsOf(weapons)))
            {
                if (model.Data.Attachments.FirstOrDefault(a => a.Id == item.Attachment) is { Bone: >= 0 } attachment
                    && attachment.Bone < model.Data.Bones.Length
                    && model.Data.Attachments.Any(a => a.Id == item.Attachment)
                    && _items.GetOrAdd((item.Model, item.Texture), LoadItem) is { } itemMesh)
                {
                    var offset = attachment.Position - model.Data.Bones[attachment.Bone].Pivot;
                    items.Add(new CreatureItemAsset(BoneName(attachment.Bone), new Vector3(offset.X, offset.Y, offset.Z), itemMesh.Mesh, itemMesh.Particles));
                }
            }

            var particles = models.Particles.Build(model.Data.Emitters)
                .Where(p => p.Bone >= 0 && p.Bone < model.Data.Bones.Length)
                .Select(p => new CreatureParticle(BoneName(p.Bone), p.Position - ToGodot(model.Data.Bones[p.Bone].Pivot), p))
                .ToList();
            return new CreatureAsset(model.Skeleton, mesh, look.Scale, items, look.Sounds, particles);
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read creature display {displayId}: {e.Message}");
            return null;
        }
    }

    /// <summary>What's in each hand. A shield has its own folder and attachment, so its file says which it is.</summary>
    private IEnumerable<CreatureItem> WeaponsOf(int[] weapons)
    {
        for (var hand = 0; hand < weapons.Length && hand < HandAttachments.Length; hand++)
        {
            if (weapons[hand] == 0 || displays.ItemFiles(weapons[hand]) is not { } files)
            {
                continue;
            }

            var weapon = $@"Item\ObjectComponents\Weapon\{M2Model.NormalizePath(files.Model)}";
            var shield = $@"Item\ObjectComponents\Shield\{M2Model.NormalizePath(files.Model)}";
            if (source.Read(weapon) is { Length: > 0 })
            {
                yield return new CreatureItem(HandAttachments[hand], weapon, files.Texture.Length > 0 ? $@"Item\ObjectComponents\Weapon\{files.Texture}.blp" : null);
            }
            else if (source.Read(shield) is { Length: > 0 })
            {
                yield return new CreatureItem(ShieldAttachment, shield, files.Texture.Length > 0 ? $@"Item\ObjectComponents\Shield\{files.Texture}.blp" : null);
            }
        }
    }

    private static string BoneName(int bone) => $"b{bone}";

    private (M2SkinnedModel, CreatureSkeletonAsset)? LoadModel(string path)
    {
        try
        {
            var m2 = source.Read(path);
            var skin = source.Read(M2Model.SkinPath(path));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                GD.PushWarning($"Creature model missing or empty: {path}");
                return null;
            }

            var data = M2SkinnedModel.Parse(m2, skin, IsPlayed, s => source.Read(s.AnimPath(path)));
            return (data, BuildSkeleton(data));
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private static bool IsPlayed(M2Sequence sequence) =>
        (sequence.Id == M2SequenceIds.Stand && sequence.Variation < MaxIdleClips)
        || (sequence.Id is M2SequenceIds.Walk or M2SequenceIds.Run && sequence.Variation == 0);

    private static CreatureSkeletonAsset BuildSkeleton(M2SkinnedModel data)
    {
        var bones = new (string, int, Vector3)[data.Bones.Length];
        var skin = new Skin();
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = data.Bones[i];
            bones[i] = (BoneName(i), bone.Parent, RestOf(data.Bones, i));

            // A bone's space is around its pivot, and vertices are stored in the model's.
            skin.AddNamedBind(BoneName(i), new Transform3D(Basis.Identity, -ToGodot(bone.Pivot)));
        }

        var clipSounds = new Dictionary<string, ClipSoundEvent[]>();
        var library = new AnimationLibrary();
        var idle = new List<IdleClip>();
        string? walk = null;
        var walkSpeed = 0f;
        var bounds = new Aabb();
        var hasBounds = false;
        foreach (var clip in data.Clips)
        {
            var sequence = clip.Sequence;
            var name = $"{sequence.Id}_{sequence.Variation}";
            library.AddAnimation(name, BuildAnimation(clip, data));

            var sounds = new List<ClipSoundEvent>();
            for (var e = 0; e < data.Events.Count; e++)
            {
                if (CreatureSoundEvents.Parse(data.Events[e].Id) is { } sound)
                {
                    sounds.AddRange(clip.EventTimes[e].Select(time => new ClipSoundEvent(time, sound.Kind, sound.Index)));
                }
            }

            if (sounds.Count > 0)
            {
                clipSounds[name] = sounds.OrderBy(s => s.Time).ToArray();
            }

            var clipBounds = new Aabb(ToGodot(sequence.BoundsMin), ToGodot(sequence.BoundsMax - sequence.BoundsMin));
            bounds = hasBounds ? bounds.Merge(clipBounds) : clipBounds;
            hasBounds = true;

            if (sequence.Id == M2SequenceIds.Stand)
            {
                idle.Add(new IdleClip(name, Math.Max(sequence.Frequency, 1)));
            }
            else if (sequence.Id == M2SequenceIds.Walk || (sequence.Id == M2SequenceIds.Run && walk is null))
            {
                walk = name;
                walkSpeed = sequence.MovementSpeed;
            }
        }

        if (!hasBounds || bounds.Size.Y < MinBoundsSize)
        {
            bounds = BoundsOfVertices(data);
        }

        return new CreatureSkeletonAsset
        {
            Bones = bones,
            Skin = skin,
            Animations = library,
            IdleClips = idle.ToArray(),
            WalkClip = walk,
            WalkSpeed = walkSpeed,
            Bounds = bounds,
            ClipSounds = clipSounds,
            Billboards = data.Bones
                .Select((bone, index) => (Bone: index, Kind: bone.Billboard))
                .Where(b => b.Kind != M2BillboardKind.None)
                .OrderBy(b => Depth(data.Bones, b.Bone))
                .ToArray(),
        };
    }

    private static int Depth(M2Bone[] bones, int bone)
    {
        var depth = 0;
        for (var parent = bones[bone].Parent; parent >= 0 && depth < bones.Length; parent = bones[parent].Parent)
        {
            depth++;
        }

        return depth;
    }

    private static Vector3 RestOf(M2Bone[] bones, int index)
    {
        var bone = bones[index];
        var parentPivot = bone.Parent >= 0 ? bones[bone.Parent].Pivot : System.Numerics.Vector3.Zero;
        return ToGodot(bone.Pivot - parentPivot);
    }

    private static Aabb BoundsOfVertices(M2SkinnedModel data)
    {
        var bounds = new Aabb(ToGodot(data.Positions.FirstOrDefault()), Vector3.Zero);
        foreach (var position in data.Positions)
        {
            bounds = bounds.Expand(ToGodot(position));
        }

        return bounds;
    }

    /// <summary>
    /// A bone turns and scales around its pivot, moved by its translation keys, so in its
    /// parent's space it sits at its rest offset plus those keys. Bones with no keys stay at rest.
    /// </summary>
    private static Animation BuildAnimation(M2Clip clip, M2SkinnedModel data)
    {
        var bones = data.Bones;
        var animation = new Animation
        {
            Length = Math.Max(clip.Sequence.DurationSeconds, 0.001f),

            // Stand's variations choose what's next when they end. Everything else loops.
            LoopMode = clip.Sequence.Id == M2SequenceIds.Stand ? Animation.LoopModeEnum.None : Animation.LoopModeEnum.Linear,
        };

        for (var bone = 0; bone < bones.Length; bone++)
        {
            var path = new NodePath($"{CreatureNode.SkeletonName}:{BoneName(bone)}");
            var rest = RestOf(bones, bone);

            var translation = clip.Translations[bone];
            if (Usable(translation))
            {
                var track = AddTrack(animation, Animation.TrackType.Position3D, path, translation);
                ForEachKey(translation, animation.Length, data.GlobalSequences, (time, value) => animation.PositionTrackInsertKey(track, time, rest + ToGodot(value)));
            }

            var rotation = clip.Rotations[bone];
            if (Usable(rotation))
            {
                var track = AddTrack(animation, Animation.TrackType.Rotation3D, path, rotation);
                ForEachKey(rotation, animation.Length, data.GlobalSequences, (time, value) =>
                {
                    var length = value.Length();
                    var q = length > 0f ? System.Numerics.Quaternion.Normalize(value) : System.Numerics.Quaternion.Identity;
                    animation.RotationTrackInsertKey(track, time, new Quaternion(q.X, q.Y, q.Z, q.W));
                });
            }

            var scale = clip.Scales[bone];
            if (Usable(scale))
            {
                var track = AddTrack(animation, Animation.TrackType.Scale3D, path, scale);
                ForEachKey(scale, animation.Length, data.GlobalSequences, (time, value) => animation.ScaleTrackInsertKey(track, time, ToGodot(value)));
            }
        }

        return animation;
    }

    private static bool Usable<T>(M2Track<T> track) => !track.IsEmpty;

    private static int AddTrack<T>(Animation animation, Animation.TrackType type, NodePath path, M2Track<T> track)
    {
        var index = animation.AddTrack(type);
        animation.TrackSetPath(index, path);
        animation.TrackSetInterpolationType(index, track.Interpolation == M2Interpolation.Step
            ? Animation.InterpolationType.Nearest
            : Animation.InterpolationType.Linear);
        return index;
    }

    /// <summary>
    /// A global sequence runs on its own clock, which a clip can't, so it repeats through the
    /// clip instead. It can jump when the clip restarts, unless their lengths happen to fit.
    /// </summary>
    private static void ForEachKey<T>(M2Track<T> track, double clipLength, float[] globalSequences, Action<float, T> insert)
    {
        var period = track.GlobalSequence >= 0 && track.GlobalSequence < globalSequences.Length
            ? globalSequences[track.GlobalSequence]
            : 0f;
        if (period <= 0f)
        {
            for (var key = 0; key < track.Times.Length; key++)
            {
                insert(track.Times[key], track.Values[key]);
            }

            return;
        }

        for (var start = 0.0; start < clipLength; start += period)
        {
            for (var key = 0; key < track.Times.Length; key++)
            {
                var time = start + track.Times[key];
                if (time <= clipLength)
                {
                    insert((float)time, track.Values[key]);
                }
            }
        }
    }

    private (ArrayMesh Mesh, ParticleAsset[] Particles)? LoadItem((string Model, string? Texture) item)
    {
        try
        {
            var m2 = source.Read(item.Model);
            var skin = source.Read(M2Model.SkinPath(item.Model));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                GD.PushWarning($"Item model missing or empty: {item.Model}");
                return null;
            }

            // An item has no skeleton, so what moves in it, like a torch's glow, is baked for its vertex shader.
            var data = M2SkinnedModel.Parse(m2, skin, s => s.Id == M2SequenceIds.Stand && s.Variation == 0, s => source.Read(s.AnimPath(item.Model)));
            var bake = data.Clips.Count > 0 ? M2BoneBake.TryCreate(data, data.Clips[0]) : null;
            var textures = new Dictionary<int, string>();
            if (item.Texture is not null)
            {
                textures[M2TextureTypes.Item] = item.Texture;
            }

            return BuildMesh(data, new CreatureLook(item.Model, 1f, textures, null, []), skinned: false, BoundsOfVertices(data), bake) is { } mesh
                ? (mesh, models.Particles.Build(data.Emitters))
                : null;
        }
        catch (WowAlchemyException e)
        {
            GD.PushWarning($"Could not read {item.Model}: {e.Message}");
            return null;
        }
    }

    private ArrayMesh? BuildMesh(M2SkinnedModel data, CreatureLook look, bool skinned, Aabb bounds, M2BoneBake? bake = null)
    {
        var byMaterial = new Dictionary<ModelMaterial, List<int[]>>();
        foreach (var batch in data.Batches)
        {
            if (look.Geosets?.IsVisible(batch.Geoset) == false)
            {
                continue;
            }

            var texture = batch.TextureType == M2TextureTypes.Named ? batch.Texture : look.Textures.GetValueOrDefault(batch.TextureType);
            if (texture is null)
            {
                continue;
            }

            var texture2 = batch.Effect is { HasTwoTextures: true } effect
                ? effect.Texture2Name ?? look.Textures.GetValueOrDefault(effect.Texture2Type)
                : null;
            
            // A creature's skeleton turns its own billboards, and an item has no skeleton to do it.
            var billboard = !skinned && batch.Indices.Any(v => ModelLibrary.VertexBillboard(data, v).Kind != M2BillboardKind.None);
            var material = new ModelMaterial(texture, batch.Blend, batch.Unlit, batch.TwoSided, batch.Repeat, batch.Effect, texture2, bake, billboard);
            if (!byMaterial.TryGetValue(material, out var parts))
            {
                byMaterial[material] = parts = [];
            }

            parts.Add(batch.Indices);
        }

        ArrayMesh? mesh = null;
        var remap = new int[data.Positions.Length];
        foreach (var (key, parts) in byMaterial)
        {
            if (models.CreatureMaterialFor(key) is not { } material)
            {
                continue;
            }

            mesh ??= new ArrayMesh { CustomAabb = bounds };
            if (bake is not null)
            {
                ModelLibrary.AddBakedSurface(mesh, data, bake, parts, material, key.Effect?.UsesSecondUv == true, key.Billboard);
            }
            else
            {
                AddSurface(mesh, data, parts, material, remap, skinned, key.Effect?.UsesSecondUv == true, key.Billboard);
            }
        }

        return mesh;
    }

    private static void AddSurface(ArrayMesh mesh, M2SkinnedModel data, List<int[]> parts, Material material, int[] remap, bool skinned, bool secondUv, bool billboard)
    {
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

        var positions = new Vector3[used.Count];
        var normals = new Vector3[used.Count];
        var texCoords = new Vector2[used.Count];
        for (var i = 0; i < used.Count; i++)
        {
            positions[i] = ToGodot(data.Positions[used[i]]);
            normals[i] = ToGodot(data.Normals[used[i]]);
            var uv = data.TexCoords[used[i]];
            texCoords[i] = new Vector2(uv.X, uv.Y);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = texCoords;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        if (secondUv)
        {
            arrays[(int)Mesh.ArrayType.TexUV2] = used.Select(v => new Vector2(data.TexCoords2[v].X, data.TexCoords2[v].Y)).ToArray();
        }

        if (skinned)
        {
            var bones = new int[used.Count * 4];
            var weights = new float[used.Count * 4];
            for (var i = 0; i < used.Count; i++)
            {
                var total = 0;
                for (var slot = 0; slot < 4; slot++)
                {
                    total += data.BoneWeights[used[i] * 4 + slot];
                }

                for (var slot = 0; slot < 4; slot++)
                {
                    bones[i * 4 + slot] = data.BoneIndices[used[i] * 4 + slot];
                    weights[i * 4 + slot] = total > 0 ? data.BoneWeights[used[i] * 4 + slot] / (float)total : (slot == 0 ? 1f : 0f);
                }
            }

            arrays[(int)Mesh.ArrayType.Bones] = bones;
            arrays[(int)Mesh.ArrayType.Weights] = weights;
        }

        var format = (Mesh.ArrayFormat)0;
        if (billboard)
        {
            var turning = new float[used.Count * 4];
            for (var i = 0; i < used.Count; i++)
            {
                var (kind, pivot) = ModelLibrary.VertexBillboard(data, used[i]);
                (turning[i * 4], turning[i * 4 + 1], turning[i * 4 + 2], turning[i * 4 + 3]) = (pivot.X, pivot.Y, pivot.Z, (float)kind);
            }

            arrays[(int)Mesh.ArrayType.Custom2] = turning;
            format = (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift);
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: format);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>Formats already gives these in Godot axes, so these only change the type.</summary>
    private static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
}
