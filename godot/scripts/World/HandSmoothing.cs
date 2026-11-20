using Godot;

namespace WowVR.World;

/// <summary>
/// Gives a tracked hand some weight: a One Euro filter (Casiez, Roussel and Vogel, 2012) on its
/// position and turn. It smooths hard while the hand is nearly still, where a hand's tremor
/// shakes the tip of a long blade, and less the faster it moves, so a swing doesn't lag.
/// Filtered in the play space, so walking and turning with the sticks don't drag the hand behind.
/// </summary>
public sealed class HandSmoothing
{
    /// <summary>The cutoffs at no weight and at the most, in hertz, while the hand is still.</summary>
    private const float LightTurnCutoff = 8f;
    private const float HeavyTurnCutoff = 0.8f;
    private const float LightMoveCutoff = 10f;
    private const float HeavyMoveCutoff = 1.2f;

    /// <summary>How much the cutoff rises with speed: per radian a second, and per meter a second.</summary>
    private const float TurnBeta = 0.6f;
    private const float MoveBeta = 3f;

    /// <summary>The cutoff for the speed itself, in hertz.</summary>
    private const float SpeedCutoff = 1f;

    private bool _started;
    private Vector3 _position;
    private Quaternion _turn;
    private float _moveSpeed;
    private float _turnSpeed;

    /// <summary>Forgets where the hand was, so the next pose is taken as it is.</summary>
    public void Reset() => _started = false;

    /// <summary>
    /// The wrist, smoothed by <paramref name="weight"/> from 0 (as tracked) to 1. <paramref name="space"/>
    /// is the play space, and <paramref name="scale"/> the world's yards to a meter.
    /// </summary>
    public Transform3D Filter(Transform3D wrist, Transform3D space, float weight, float scale, float delta)
    {
        if (weight <= 0f || delta <= 0f)
        {
            _started = false;
            return wrist;
        }

        var local = space.AffineInverse() * wrist;
        var position = local.Origin / scale;
        var turn = local.Basis.Orthonormalized().GetRotationQuaternion();
        if (!_started)
        {
            (_started, _position, _turn, _moveSpeed, _turnSpeed) = (true, position, turn, 0f, 0f);
            return wrist;
        }

        var speedAlpha = Alpha(SpeedCutoff, delta);
        _moveSpeed = Mathf.Lerp(_moveSpeed, _position.DistanceTo(position) / delta, speedAlpha);
        _turnSpeed = Mathf.Lerp(_turnSpeed, _turn.AngleTo(turn) / delta, speedAlpha);

        var moveCutoff = Mathf.Lerp(LightMoveCutoff, HeavyMoveCutoff, weight) + (MoveBeta * _moveSpeed);
        var turnCutoff = Mathf.Lerp(LightTurnCutoff, HeavyTurnCutoff, weight) + (TurnBeta * _turnSpeed);
        _position = _position.Lerp(position, Alpha(moveCutoff, delta));
        _turn = _turn.Slerp(turn, Alpha(turnCutoff, delta)).Normalized();

        return space * new Transform3D(new Basis(_turn), _position * scale);
    }

    /// <summary>How far to move toward the new value this frame, for a low-pass filter at <paramref name="cutoff"/> hertz.</summary>
    private static float Alpha(float cutoff, float delta)
    {
        var tau = 1f / (Mathf.Tau * cutoff);
        return 1f / (1f + (tau / delta));
    }
}
