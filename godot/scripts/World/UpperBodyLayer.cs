using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using WowVR.Converters;
using Godot;

namespace WowVR.World;

/// <summary>
/// Plays one clip on the upper body over whatever the whole body plays, as a swing while the legs
/// walk: after the animation poses the skeleton, the spine and everything above it are moved toward
/// the clip's pose by a weight that fades in, holds, and fades out at the clip's end. The legs and
/// hips keep the body's own clip. Idle, it costs nothing.
/// </summary>
public sealed class UpperBodyLayer
{
    private const double FadeOutSeconds = 0.2;

    /// <summary>What every creature of a model shares: its upper-body bones and their tracks in each clip, found once.</summary>
    private sealed class Shape
    {
        public required int[] Bones { get; init; }
        public required NodePath[] Paths { get; init; }
        public Dictionary<Animation, (int Position, int Rotation, int Scale)[]> Tracks { get; } = [];
    }

    private static readonly ConditionalWeakTable<CreatureSkeletonAsset, Shape> Shapes = new();

    private readonly Shape _shape;
    private Animation? _clip;
    private (int Position, int Rotation, int Scale)[] _clipTracks = [];
    private double _time;
    private double _fadeIn;
    private double _fadeInSeconds;

    /// <param name="skeleton">The model, whose spine and everything below it in the tree are layered.</param>
    /// <param name="pathOf">The animation track path of a bone.</param>
    public UpperBodyLayer(CreatureSkeletonAsset skeleton, Func<int, string> pathOf)
    {
        _shape = Shapes.GetValue(skeleton, s =>
        {
            var upper = new List<int>();
            for (var bone = 0; bone < s.Bones.Length; bone++)
            {
                for (var b = bone; b >= 0; b = s.Bones[b].Parent)
                {
                    if (b == s.SpineBone)
                    {
                        upper.Add(bone);
                        break;
                    }
                }
            }

            return new Shape { Bones = [.. upper], Paths = [.. upper.ConvertAll(b => new NodePath(pathOf(b)))] };
        });
    }

    public bool Playing => _clip is not null;

    /// <summary>
    /// Starts a clip on the upper body at a time into it, fading in over <paramref name="fadeIn"/>
    /// seconds; 0 takes over at once, as from the same clip the whole body was playing.
    /// </summary>
    public void Start(Animation clip, double from, double fadeIn)
    {
        if (!_shape.Tracks.TryGetValue(clip, out var tracks))
        {
            tracks = new (int, int, int)[_shape.Bones.Length];
            for (var i = 0; i < tracks.Length; i++)
            {
                var path = _shape.Paths[i];
                tracks[i] = (
                    clip.FindTrack(path, Animation.TrackType.Position3D),
                    clip.FindTrack(path, Animation.TrackType.Rotation3D),
                    clip.FindTrack(path, Animation.TrackType.Scale3D));
            }

            _shape.Tracks[clip] = tracks;
        }

        (_clip, _clipTracks, _time, _fadeIn, _fadeInSeconds) = (clip, tracks, from, 0, fadeIn);
    }

    public void Stop() => _clip = null;

    /// <summary>Moves the clip on by <paramref name="delta"/> and lays it over the skeleton's pose. Call after the animation has posed it.</summary>
    public void Apply(Skeleton3D skeleton, double delta)
    {
        if (_clip is not { } clip)
        {
            return;
        }

        _time += delta;
        _fadeIn += delta;
        var length = clip.Length;
        if (_time >= length)
        {
            _clip = null;
            return;
        }

        var weight = (float)Math.Min(
            _fadeInSeconds <= 0 ? 1 : Math.Min(_fadeIn / _fadeInSeconds, 1),
            Math.Clamp((length - _time) / FadeOutSeconds, 0, 1));
        var bones = _shape.Bones;
        for (var i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            var (position, rotation, scale) = _clipTracks[i];
            if (position >= 0)
            {
                skeleton.SetBonePosePosition(bone, skeleton.GetBonePosePosition(bone).Lerp(clip.PositionTrackInterpolate(position, _time), weight));
            }

            if (rotation >= 0)
            {
                skeleton.SetBonePoseRotation(bone, skeleton.GetBonePoseRotation(bone).Slerp(clip.RotationTrackInterpolate(rotation, _time).Normalized(), weight));
            }

            if (scale >= 0)
            {
                skeleton.SetBonePoseScale(bone, skeleton.GetBonePoseScale(bone).Lerp(clip.ScaleTrackInterpolate(scale, _time), weight));
            }
        }
    }
}
