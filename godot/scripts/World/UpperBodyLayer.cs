using System;
using System.Collections.Generic;
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

    private readonly int[] _bones;
    private readonly StringName[] _paths;
    private readonly Dictionary<Animation, (int Position, int Rotation, int Scale)[]> _tracks = [];
    private Animation? _clip;
    private (int Position, int Rotation, int Scale)[] _clipTracks = [];
    private double _time;
    private double _fadeIn;
    private double _fadeInSeconds;

    /// <param name="parents">Each bone's parent, or -1.</param>
    /// <param name="spine">The lowest bone of the upper body: it and all below it in the tree are layered.</param>
    /// <param name="pathOf">The animation track path of a bone.</param>
    public UpperBodyLayer(IReadOnlyList<int> parents, int spine, Func<int, string> pathOf)
    {
        var upper = new List<int>();
        for (var bone = 0; bone < parents.Count; bone++)
        {
            for (var b = bone; b >= 0; b = parents[b])
            {
                if (b == spine)
                {
                    upper.Add(bone);
                    break;
                }
            }
        }

        _bones = [.. upper];
        _paths = new StringName[_bones.Length];
        for (var i = 0; i < _bones.Length; i++)
        {
            _paths[i] = pathOf(_bones[i]);
        }
    }

    public bool Playing => _clip is not null;

    /// <summary>
    /// Starts a clip on the upper body at a time into it, fading in over <paramref name="fadeIn"/>
    /// seconds; 0 takes over at once, as from the same clip the whole body was playing.
    /// </summary>
    public void Start(Animation clip, double from, double fadeIn)
    {
        if (!_tracks.TryGetValue(clip, out var tracks))
        {
            tracks = new (int, int, int)[_bones.Length];
            for (var i = 0; i < _bones.Length; i++)
            {
                var path = new NodePath(_paths[i]);
                tracks[i] = (
                    clip.FindTrack(path, Animation.TrackType.Position3D),
                    clip.FindTrack(path, Animation.TrackType.Rotation3D),
                    clip.FindTrack(path, Animation.TrackType.Scale3D));
            }

            _tracks[clip] = tracks;
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
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
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
