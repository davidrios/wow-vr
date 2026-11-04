using System.Numerics;
using WowVR.Formats.Native;

namespace WowVR.Formats.Models;

/// <summary>Sequence ids from AnimationData.dbc that this project plays.</summary>
public static class M2SequenceIds
{
    public const int Stand = 0;
    public const int Walk = 4;
    public const int Run = 5;
    public const int Death = 1;
    public const int Dead = 6;
    public const int WalkBackwards = 13;
    public const int AttackUnarmed = 16;
    public const int Attack1H = 17;
    public const int Attack2H = 18;
    public const int ReadyUnarmed = 25;
    public const int Ready1H = 26;
    public const int JumpStart = 37;
    public const int Jump = 38;
    public const int JumpEnd = 39;
    public const int Fall = 40;
    public const int SwimIdle = 41;
    public const int Swim = 42;
    public const int SwimLeft = 43;
    public const int SwimRight = 44;
    public const int SwimBackwards = 45;

    /// <summary>A door's, chest's or lever's: opening, standing open, closing and standing closed.</summary>
    public const int Open = 146;
    public const int Opened = 147;
    public const int Close = 148;
    public const int Closed = 149;

    /// <summary>Played once rather than looped.</summary>
    public static bool PlaysOnce(int id) => id is Stand or Death or AttackUnarmed or Attack1H or Attack2H or JumpStart or JumpEnd or Open or Close;

    /// <summary>The sequences this game plays beyond standing, walking and running.</summary>
    public static readonly int[] Extra = [Death, Dead, WalkBackwards, AttackUnarmed, Attack1H, Attack2H, ReadyUnarmed, Ready1H, JumpStart, Jump, JumpEnd, Fall, SwimIdle, Swim, SwimLeft, SwimRight, SwimBackwards, Open, Close];

    /// <summary>Not played as such: its first pose is what a hand's fingers hold while it has something in it.</summary>
    public const int HandsClosed = 15;
}

/// <summary>How a bone turns to the viewer. Spherical faces them fully, and the others turn around one of the model's axes.</summary>
public enum M2BillboardKind
{
    None = 0,
    Spherical = 1,
    LockModelX = 2,
    LockModelY = 3,
    LockModelZ = 4,
}

/// <summary>A bone in Godot axes. Its keys move it around <see cref="Pivot"/>.</summary>
public readonly record struct M2Bone(int Parent, uint Flags, Vector3 Pivot, int KeyBone = -1)
{
    private const uint FlagSpherical = 0x8;
    private const uint FlagLockX = 0x10;
    private const uint FlagLockY = 0x20;
    private const uint FlagLockZ = 0x40;

    public M2BillboardKind Billboard =>
        (Flags & FlagSpherical) != 0 ? M2BillboardKind.Spherical
        : (Flags & FlagLockX) != 0 ? M2BillboardKind.LockModelX
        : (Flags & FlagLockY) != 0 ? M2BillboardKind.LockModelY
        : (Flags & FlagLockZ) != 0 ? M2BillboardKind.LockModelZ
        : M2BillboardKind.None;
}

public readonly record struct M2Attachment(int Id, int Bone, Vector3 Position);

public sealed record M2Sequence(
    int Index,
    int Id,
    int Variation,
    float DurationSeconds,
    float MovementSpeed,
    uint Flags,
    int Frequency,
    int AliasNext,
    Vector3 BoundsMin,
    Vector3 BoundsMax)
{
    private const uint FlagInModel = 0x20;
    private const uint FlagAlias = 0x40;

    public bool InModel => (Flags & FlagInModel) != 0;

    public bool IsAlias => (Flags & FlagAlias) != 0;

    /// <summary>The file holding a sequence's keys when they aren't in the model.</summary>
    public string AnimPath(string m2Path) => $"{Path.ChangeExtension(m2Path, null)}{Id:D4}-{Variation:D2}.anim";
}

public enum M2Interpolation
{
    Step = 0,
    Linear = 1,

    /// <summary>A curve, whose tangents aren't read, so it plays as linear.</summary>
    Curve = 2,
}

/// <summary>A bone's keys for one sequence, in Godot axes. Empty when it has none.</summary>
public sealed record M2Track<T>(M2Interpolation Interpolation, int GlobalSequence, float[] Times, T[] Values)
{
    public bool IsEmpty => Times.Length == 0;
}

/// <summary>What a sequence does to each bone.</summary>
/// <param name="EventTimes">For each of the model's events, when it fires in this clip, in seconds.</param>
public sealed record M2Clip(M2Sequence Sequence, M2Track<Vector3>[] Translations, M2Track<Quaternion>[] Rotations, M2Track<Vector3>[] Scales, float[][] EventTimes);

/// <summary>Something a model asks for at moments in its clips, such as a footstep.</summary>
/// <param name="Id">4 characters, usually $ and 3 more, such as $FD1.</param>
public readonly record struct M2EventInfo(string Id, uint Data, int Bone, Vector3 Position);

/// <summary>Triangles of one geoset and material, indexing the whole model's vertices.</summary>
public sealed record M2Batch(int Geoset, int TextureType, string? Texture, ModelBlend Blend, bool Unlit, bool TwoSided, bool Repeat, int[] Indices, M2Effect? Effect = null);

/// <summary>An M2 with what's needed to animate it. Vertices and bones are in Godot axes.</summary>
public sealed class M2SkinnedModel
{
    private const int MaxAliasHops = 8;

    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }
    public required Vector2[] TexCoords2 { get; init; }

    /// <summary>4 per vertex, adding up to 255.</summary>
    public required byte[] BoneWeights { get; init; }

    /// <summary>4 per vertex, indexing <see cref="Bones"/>.</summary>
    public required byte[] BoneIndices { get; init; }

    public required IReadOnlyList<M2Batch> Batches { get; init; }
    public required M2Bone[] Bones { get; init; }
    public required IReadOnlyList<M2Sequence> Sequences { get; init; }
    public required IReadOnlyList<M2Attachment> Attachments { get; init; }

    public IReadOnlyList<M2Emitter> Emitters { get; init; } = [];
    public required IReadOnlyList<M2EventInfo> Events { get; init; }

    /// <summary>Each global sequence's length in seconds. These run on their own clock, whatever else plays.</summary>
    public required float[] GlobalSequences { get; init; }

    /// <summary>The clips asked for that could be read.</summary>
    public required IReadOnlyList<M2Clip> Clips { get; init; }

    /// <param name="wanted">Which sequences to read keys for.</param>
    /// <param name="readAnim">Gives a sequence's .anim file, or null when it's missing.</param>
    public static unsafe M2SkinnedModel Parse(
        ReadOnlySpan<byte> m2,
        ReadOnlySpan<byte> skin,
        Func<M2Sequence, bool> wanted,
        Func<M2Sequence, byte[]?> readAnim)
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
            ModelNativeMethods.M2VertexBones(model, out var weights, out var boneIndices);
            var vertices = checked((int)mesh.VertexCount);

            var positions = new Vector3[vertices];
            var normals = new Vector3[vertices];
            var texCoords = new Vector2[vertices];
            var texCoords2 = new Vector2[vertices];
            var secondUvs = ModelNativeMethods.M2TexCoords2(model);
            for (var i = 0; i < vertices; i++)
            {
                positions[i] = WowCoords.ModelToGodot(new Vector3(mesh.Positions[i * 3], mesh.Positions[i * 3 + 1], mesh.Positions[i * 3 + 2]));
                normals[i] = WowCoords.ModelToGodot(new Vector3(mesh.Normals[i * 3], mesh.Normals[i * 3 + 1], mesh.Normals[i * 3 + 2]));
                texCoords[i] = new Vector2(mesh.TexCoords[i * 2], mesh.TexCoords[i * 2 + 1]);
                texCoords2[i] = new Vector2(secondUvs[i * 2], secondUvs[i * 2 + 1]);
            }

            var bones = new M2Bone[ModelNativeMethods.M2BoneCount(model)];
            for (var i = 0; i < bones.Length; i++)
            {
                ModelNativeMethods.M2Bone(model, (uint)i, out var bone);
                bones[i] = new M2Bone(bone.Parent, bone.Flags, WowCoords.ModelToGodot(new Vector3(bone.Pivot[0], bone.Pivot[1], bone.Pivot[2])), bone.KeyBone);
            }

            var sequences = ReadSequences(model);
            var attachments = new M2Attachment[ModelNativeMethods.M2AttachmentCount(model)];
            for (var i = 0; i < attachments.Length; i++)
            {
                ModelNativeMethods.M2Attachment(model, (uint)i, out var attachment);
                attachments[i] = new M2Attachment(
                    (int)attachment.Id,
                    (int)attachment.Bone,
                    WowCoords.ModelToGodot(new Vector3(attachment.Position[0], attachment.Position[1], attachment.Position[2])));
            }

            ModelNativeMethods.M2GlobalSequences(model, out var globalLengths, out var globalCount);
            var globalSequences = new float[globalCount];
            for (var i = 0; i < globalSequences.Length; i++)
            {
                globalSequences[i] = globalLengths[i] / 1000f;
            }

            var events = new M2EventInfo[ModelNativeMethods.M2EventCount(model)];
            for (var i = 0; i < events.Length; i++)
            {
                ModelNativeMethods.M2Event(model, (uint)i, out var m2Event);
                events[i] = new M2EventInfo(
                    System.Text.Encoding.ASCII.GetString(m2Event.Identifier, 4),
                    m2Event.Data,
                    (int)m2Event.Bone,
                    WowCoords.ModelToGodot(new Vector3(m2Event.Position[0], m2Event.Position[1], m2Event.Position[2])));
            }

            var clips = new List<M2Clip>();
            foreach (var sequence in sequences)
            {
                if (wanted(sequence) && ReadClip(model, sequences, sequence, bones.Length, readAnim) is { } clip)
                {
                    clips.Add(clip);
                }
            }

            return new M2SkinnedModel
            {
                Positions = positions,
                Normals = normals,
                TexCoords = texCoords,
                TexCoords2 = texCoords2,
                BoneWeights = new ReadOnlySpan<byte>(weights, vertices * 4).ToArray(),
                BoneIndices = new ReadOnlySpan<byte>(boneIndices, vertices * 4).ToArray(),
                Batches = ReadBatches(model, mesh),
                Bones = bones,
                Sequences = sequences,
                Attachments = attachments,
                Emitters = M2Emitter.ReadAll(model),
                Events = events,
                GlobalSequences = globalSequences,
                Clips = clips,
            };
        }
        finally
        {
            ModelNativeMethods.M2Free(model);
        }
    }

    private static unsafe List<M2Batch> ReadBatches(nint model, in WaMesh mesh)
    {
        var looks = M2BatchLooks.Read(model, mesh.BatchCount, BlendOf);
        var batches = new List<M2Batch>((int)mesh.BatchCount);
        for (var b = 0; b < mesh.BatchCount; b++)
        {
            var batch = mesh.Batches[b];
            var look = looks[batch.Material];
            if (batch.IndexCount < 3 || look.Hidden || ModelNativeMethods.M2Material(model, batch.Material, out var material) != WaStatus.Ok)
            {
                continue;
            }

            // Reversed, because the files wind the other way from Godot.
            var triangles = batch.IndexCount - batch.IndexCount % 3;
            var indices = new int[triangles];
            for (var i = 0; i < triangles; i++)
            {
                indices[i] = (int)mesh.Indices[batch.IndexStart + (i - i % 3) + (2 - i % 3)];
            }

            batches.Add(new M2Batch((int)material.Geoset, look.TextureType, look.Texture, look.Blend, look.Unlit, look.TwoSided, look.Repeat, indices, look.Effect));
        }

        return batches;
    }

    private static unsafe M2Sequence[] ReadSequences(nint model)
    {
        var sequences = new M2Sequence[ModelNativeMethods.M2SequenceCount(model)];
        for (var i = 0; i < sequences.Length; i++)
        {
            ModelNativeMethods.M2Sequence(model, (uint)i, out var sequence);
            var (min, max) = WowCoords.ModelBoundsToGodot(
                new Vector3(sequence.BoundsMin[0], sequence.BoundsMin[1], sequence.BoundsMin[2]),
                new Vector3(sequence.BoundsMax[0], sequence.BoundsMax[1], sequence.BoundsMax[2]));
            sequences[i] = new M2Sequence(
                i,
                (int)sequence.Id,
                (int)sequence.Variation,
                sequence.DurationMs / 1000f,
                sequence.MovementSpeed,
                sequence.Flags,
                sequence.Frequency,
                (int)sequence.AliasNext,
                min,
                max);
        }

        return sequences;
    }

    private static unsafe M2Clip? ReadClip(nint model, M2Sequence[] sequences, M2Sequence sequence, int boneCount, Func<M2Sequence, byte[]?> readAnim)
    {
        // An alias plays another sequence's keys under its own id.
        var source = sequence;
        for (var hops = 0; source.IsAlias && hops < MaxAliasHops; hops++)
        {
            if (source.AliasNext >= sequences.Length)
            {
                return null;
            }

            source = sequences[source.AliasNext];
        }

        byte[]? anim = null;
        if (!source.InModel)
        {
            anim = readAnim(source);
            if (anim is not { Length: > 0 })
            {
                return null;
            }
        }

        nint clip;
        fixed (byte* animData = anim)
        {
            clip = ModelNativeMethods.M2ClipOpen(model, (uint)source.Index, animData, (nuint)(anim?.Length ?? 0));
        }

        if (clip == 0)
        {
            throw new WowAlchemyException(NativeMethods.LastErrorMessage());
        }

        try
        {
            var translations = new M2Track<Vector3>[boneCount];
            var rotations = new M2Track<Quaternion>[boneCount];
            var scales = new M2Track<Vector3>[boneCount];
            for (var bone = 0; bone < boneCount; bone++)
            {
                translations[bone] = ReadTrack(clip, bone, TrackKind.Translation, 3, (v, i) => WowCoords.ModelToGodot(new Vector3(v[i], v[i + 1], v[i + 2])));
                rotations[bone] = ReadTrack(clip, bone, TrackKind.Rotation, 4, (v, i) => WowCoords.ModelRotationToGodot(new Quaternion(v[i], v[i + 1], v[i + 2], v[i + 3])));
                scales[bone] = ReadTrack(clip, bone, TrackKind.Scale, 3, (v, i) => WowCoords.ModelScaleToGodot(new Vector3(v[i], v[i + 1], v[i + 2])));
            }

            var eventCount = (int)ModelNativeMethods.M2EventCount(model);
            var eventTimes = new float[eventCount][];
            for (var e = 0; e < eventCount; e++)
            {
                ModelNativeMethods.M2ClipEventTimes(clip, (uint)e, out var times, out var count);
                eventTimes[e] = new float[count];
                for (var key = 0; key < count; key++)
                {
                    eventTimes[e][key] = times[key] / 1000f;
                }
            }

            return new M2Clip(sequence, translations, rotations, scales, eventTimes);
        }
        finally
        {
            ModelNativeMethods.M2ClipFree(clip);
        }
    }

    private enum TrackKind : uint
    {
        Translation = 0,
        Rotation = 1,
        Scale = 2,
    }

    private unsafe delegate T ValueReader<out T>(float* values, int index);

    private static unsafe M2Track<T> ReadTrack<T>(nint clip, int bone, TrackKind kind, int stride, ValueReader<T> read)
    {
        ModelNativeMethods.M2ClipTrack(clip, (uint)bone, (uint)kind, out var track);
        var count = checked((int)track.KeyCount);
        var times = new float[count];
        var values = new T[count];
        for (var key = 0; key < count; key++)
        {
            times[key] = track.Times[key] / 1000f;
            values[key] = read(track.Values, key * stride);
        }

        var interpolation = track.Interpolation switch
        {
            0 => M2Interpolation.Step,
            1 => M2Interpolation.Linear,
            _ => M2Interpolation.Curve,
        };
        return new M2Track<T>(interpolation, track.GlobalSequence, times, values);
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
