using System;
using System.Collections.Generic;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.Converters;

/// <summary>
/// A creature's visible triangles with their bone weights, kept on the CPU so a ray can be tested
/// against the body as it's posed right now. Picking by bounds selected nearer creatures when the
/// cursor was only beside them, since the bounds cover every clip's reach.
/// </summary>
public sealed class PickMesh
{
    private readonly Vector3[] _positions;
    private readonly int[] _bones;
    private readonly float[] _weights;
    private readonly int[] _triangles;
    private readonly Vector3[] _pivots;

    private PickMesh(Vector3[] positions, int[] bones, float[] weights, int[] triangles, Vector3[] pivots)
    {
        (_positions, _bones, _weights, _triangles, _pivots) = (positions, bones, weights, triangles, pivots);
    }

    /// <summary>The triangles of the batches drawn, without glows and other additive or modulating layers. Null if none.</summary>
    public static PickMesh? Build(M2SkinnedModel data, IEnumerable<M2Batch> drawn)
    {
        var remap = new int[data.Positions.Length];
        Array.Fill(remap, -1);
        var used = new List<int>();
        var triangles = new List<int>();
        foreach (var batch in drawn)
        {
            if (batch.Blend is ModelBlend.Add or ModelBlend.Modulate)
            {
                continue;
            }

            foreach (var vertex in batch.Indices)
            {
                if (remap[vertex] < 0)
                {
                    remap[vertex] = used.Count;
                    used.Add(vertex);
                }

                triangles.Add(remap[vertex]);
            }
        }

        if (triangles.Count < 3)
        {
            return null;
        }

        var positions = new Vector3[used.Count];
        var bones = new int[used.Count * 4];
        var weights = new float[used.Count * 4];
        for (var i = 0; i < used.Count; i++)
        {
            var p = data.Positions[used[i]];
            positions[i] = new Vector3(p.X, p.Y, p.Z);
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

        var pivots = new Vector3[data.Bones.Length];
        for (var b = 0; b < pivots.Length; b++)
        {
            var pivot = data.Bones[b].Pivot;
            pivots[b] = new Vector3(pivot.X, pivot.Y, pivot.Z);
        }

        return new PickMesh(positions, bones, weights, triangles.ToArray(), pivots);
    }

    /// <summary>
    /// How far along a ray in world space it first meets the body posed by <paramref name="skeleton"/>, or null.
    /// Skins every vertex as the GPU does, so it's meant for clicks, not every frame.
    /// </summary>
    public float? Hit(Skeleton3D skeleton, Vector3 origin, Vector3 direction, float reach)
    {
        // Each bone as the skin applies it: out of the model's space around its pivot, then posed.
        var toWorld = skeleton.GlobalTransform;
        var count = Math.Min(skeleton.GetBoneCount(), _pivots.Length);
        var bones = new Transform3D[count];
        for (var b = 0; b < count; b++)
        {
            bones[b] = toWorld * skeleton.GetBoneGlobalPose(b) * new Transform3D(Basis.Identity, -_pivots[b]);
        }

        var skinned = new Vector3[_positions.Length];
        for (var v = 0; v < _positions.Length; v++)
        {
            var sum = Vector3.Zero;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = _weights[v * 4 + slot];
                var bone = _bones[v * 4 + slot];
                if (weight > 0f && bone < count)
                {
                    sum += bones[bone] * _positions[v] * weight;
                }
            }

            skinned[v] = sum;
        }

        float? best = null;
        var bestT = reach;
        for (var i = 0; i + 2 < _triangles.Length; i += 3)
        {
            if (RayTriangle(origin, direction, skinned[_triangles[i]], skinned[_triangles[i + 1]], skinned[_triangles[i + 2]]) is { } t && t < bestT)
            {
                bestT = t;
                best = t;
            }
        }

        return best;
    }

    /// <summary>Möller–Trumbore, from either side.</summary>
    private static float? RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var p = direction.Cross(ac);
        var determinant = ab.Dot(p);
        if (Mathf.Abs(determinant) < 1e-8f)
        {
            return null;
        }

        var inverse = 1f / determinant;
        var s = origin - a;
        var u = s.Dot(p) * inverse;
        if (u < 0f || u > 1f)
        {
            return null;
        }

        var q = s.Cross(ab);
        var v = direction.Dot(q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            return null;
        }

        var t = ac.Dot(q) * inverse;
        return t >= 0f ? t : null;
    }
}
