using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>Where an emitter is at even steps through a clip that loops, in the same axes as the model.</summary>
public sealed record M2EmitterPath(Vector3[] Points, float Seconds);

/// <summary>
/// A clip's bone matrices at even steps, for a vertex shader to read from a texture, so that
/// thousands of copies of a doodad can animate without a skeleton each. Only bones with
/// vertices are kept, and vertices use <see cref="BoneRemap"/> to find theirs.
/// </summary>
public sealed class M2BoneBake
{
    public const int RowsPerBone = 3;
    private const int MaxFrames = 256;
    private const float FramesPerSecond = 30f;
    private const float MinMovement = 0.0005f;
    private const float MinEmitterMovement = 0.01f;

    /// <summary>The bones kept, as columns of the texture divided by <see cref="RowsPerBone"/>.</summary>
    public required int BoneCount { get; init; }
    public required int Frames { get; init; }
    public required float Seconds { get; init; }

    /// <summary>4 floats per texel, frame by frame: each bone's 3 rows of its matrix, for column vectors.</summary>
    public required float[] Texels { get; init; }

    /// <summary>A model bone's place in the bake, or -1.</summary>
    public required int[] BoneRemap { get; init; }

    /// <summary>Where every bone is at a moment of a clip, as WoWModelViewer's Bone::calcMatrix does it.</summary>
    public static Matrix4x4[] Pose(M2SkinnedModel model, M2Clip clip, float time)
    {
        var matrices = new Matrix4x4[model.Bones.Length];
        var done = new bool[matrices.Length];
        for (var i = 0; i < matrices.Length; i++)
        {
            Resolve(model, clip, time, i, matrices, done);
        }

        return matrices;
    }

    private static void Resolve(M2SkinnedModel model, M2Clip clip, float time, int index, Matrix4x4[] matrices, bool[] done)
    {
        if (done[index])
        {
            return;
        }

        done[index] = true;
        var bone = model.Bones[index];
        var local = Matrix4x4.CreateTranslation(-bone.Pivot)
            * Matrix4x4.CreateScale(Sample(clip.Scales[index], time, model.GlobalSequences, Vector3.One, Vector3.Lerp))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Sample(clip.Rotations[index], time, model.GlobalSequences, Quaternion.Identity, Quaternion.Slerp)))
            * Matrix4x4.CreateTranslation(Sample(clip.Translations[index], time, model.GlobalSequences, Vector3.Zero, Vector3.Lerp) + bone.Pivot);

        if (bone.Parent >= 0 && bone.Parent < matrices.Length)
        {
            Resolve(model, clip, time, bone.Parent, matrices, done);
            local *= matrices[bone.Parent];
        }

        matrices[index] = local;
    }

    private static T Sample<T>(M2Track<T> track, float time, float[] globals, T fallback, Func<T, T, float, T> lerp)
    {
        if (track.IsEmpty)
        {
            return fallback;
        }

        if (track.GlobalSequence >= 0 && track.GlobalSequence < globals.Length && globals[track.GlobalSequence] > 0f)
        {
            time %= globals[track.GlobalSequence];
        }

        var after = Array.BinarySearch(track.Times, time);
        after = after >= 0 ? after : ~after;
        if (after == 0)
        {
            return track.Values[0];
        }

        if (after >= track.Times.Length || track.Interpolation == M2Interpolation.Step)
        {
            return track.Values[after - 1];
        }

        var span = track.Times[after] - track.Times[after - 1];
        return span > 0f ? lerp(track.Values[after - 1], track.Values[after], (time - track.Times[after - 1]) / span) : track.Values[after];
    }

    /// <summary>Null when none of the model's visible parts move, so a still mesh will do.</summary>
    public static M2BoneBake? TryCreate(M2SkinnedModel model, M2Clip clip)
    {
        var seconds = clip.Sequence.DurationSeconds;
        if (seconds <= 0f && model.GlobalSequences.Length > 0)
        {
            seconds = model.GlobalSequences.Max();
        }

        if (seconds <= 0f)
        {
            return null;
        }

        var remap = new int[model.Bones.Length];
        Array.Fill(remap, -1);
        var count = 0;
        foreach (var batch in model.Batches)
        {
            foreach (var vertex in batch.Indices)
            {
                for (var slot = 0; slot < 4; slot++)
                {
                    var bone = model.BoneIndices[vertex * 4 + slot];
                    if (model.BoneWeights[vertex * 4 + slot] > 0 && bone < remap.Length && remap[bone] < 0)
                    {
                        remap[bone] = count++;
                    }
                }
            }
        }

        if (count == 0)
        {
            return null;
        }

        var billboards = false;
        for (var bone = 0; bone < remap.Length; bone++)
        {
            billboards |= remap[bone] >= 0 && BillboardOf(model, bone).Kind != M2BillboardKind.None;
        }

        var frames = Math.Clamp((int)MathF.Ceiling(seconds * FramesPerSecond), 2, MaxFrames);
        var texels = new float[frames * count * RowsPerBone * 4];
        var first = new Matrix4x4[model.Bones.Length];
        var moves = false;
        for (var frame = 0; frame < frames; frame++)
        {
            var pose = Pose(model, clip, seconds * frame / frames);
            if (frame == 0)
            {
                first = pose;
            }

            for (var bone = 0; bone < pose.Length; bone++)
            {
                if (remap[bone] < 0)
                {
                    continue;
                }

                var m = pose[bone];
                moves |= frame > 0 && Differs(m, first[bone]);
                var at = (frame * count + remap[bone]) * RowsPerBone * 4;
                (texels[at], texels[at + 1], texels[at + 2], texels[at + 3]) = (m.M11, m.M21, m.M31, m.M41);
                (texels[at + 4], texels[at + 5], texels[at + 6], texels[at + 7]) = (m.M12, m.M22, m.M32, m.M42);
                (texels[at + 8], texels[at + 9], texels[at + 10], texels[at + 11]) = (m.M13, m.M23, m.M33, m.M43);
            }
        }

        return moves || billboards
            ? new M2BoneBake { BoneCount = count, Frames = frames, Seconds = seconds, Texels = texels, BoneRemap = remap }
            : null;
    }

    /// <summary>
    /// The way an emitter's bone carries it through a clip, which for one with no speed of its
    /// own is what draws its trail. Null when it stays where it is. Its bone needn't have vertices.
    /// </summary>
    public static M2EmitterPath? EmitterPath(M2SkinnedModel model, M2Clip clip, M2Emitter emitter)
    {
        var seconds = clip.Sequence.DurationSeconds;
        if (seconds <= 0f && model.GlobalSequences.Length > 0)
        {
            seconds = model.GlobalSequences.Max();
        }

        if (seconds <= 0f || emitter.Bone < 0 || emitter.Bone >= model.Bones.Length)
        {
            return null;
        }

        var frames = Math.Clamp((int)MathF.Ceiling(seconds * FramesPerSecond), 2, MaxFrames);
        var points = new Vector3[frames];
        var moves = false;
        for (var frame = 0; frame < frames; frame++)
        {
            points[frame] = Vector3.Transform(emitter.Position, Pose(model, clip, seconds * frame / frames)[emitter.Bone]);
            moves |= Vector3.Distance(points[frame], points[0]) > MinEmitterMovement;
        }

        return moves ? new M2EmitterPath(points, seconds) : null;
    }

    /// <summary>The bone that turns a bone to the viewer, which is itself or the nearest ancestor that does.</summary>
    public static (M2BillboardKind Kind, Vector3 Pivot) BillboardOf(M2SkinnedModel model, int bone)
    {
        const int MaxDepth = 256;
        for (var depth = 0; bone >= 0 && bone < model.Bones.Length && depth < MaxDepth; depth++)
        {
            if (model.Bones[bone].Billboard != M2BillboardKind.None)
            {
                return (model.Bones[bone].Billboard, model.Bones[bone].Pivot);
            }

            bone = model.Bones[bone].Parent;
        }

        return (M2BillboardKind.None, Vector3.Zero);
    }

    private static bool Differs(in Matrix4x4 a, in Matrix4x4 b)
    {
        var difference = a - b;
        return MathF.Abs(difference.M11) + MathF.Abs(difference.M12) + MathF.Abs(difference.M13)
            + MathF.Abs(difference.M21) + MathF.Abs(difference.M22) + MathF.Abs(difference.M23)
            + MathF.Abs(difference.M31) + MathF.Abs(difference.M32) + MathF.Abs(difference.M33)
            + MathF.Abs(difference.M41) + MathF.Abs(difference.M42) + MathF.Abs(difference.M43) > MinMovement;
    }
}
