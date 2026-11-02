namespace WowVR.Formats.World;

/// <summary>Where a creature is at some moment, in WoW coordinates.</summary>
/// <param name="Z">Null when only the ground can say, as when wandering.</param>
/// <param name="Facing">Radians, turning left from north.</param>
public readonly record struct CreatureMotionState(double X, double Y, float? Z, float Facing, bool Moving);

/// <summary>
/// A creature's movement as a function of the world clock, with no state between calls. Two
/// clients with the same clock see the same thing, which is how M7 keeps creatures in step
/// without sending them over the network.
/// </summary>
public sealed class CreatureMotion
{
    /// <summary>WoW's walking speed in yards per second, for spawns that don't say.</summary>
    public const float WalkSpeed = 2.5f;

    private const double WanderPauseSeconds = 4;

    private readonly CreatureSpawn _spawn;
    private readonly double _wanderPeriod;
    private readonly double _offset;
    private readonly (CreatureWaypoint From, CreatureWaypoint To, double Start, double Walk)[] _legs = [];
    private readonly double _pathSeconds;

    public CreatureMotion(CreatureSpawn spawn)
    {
        _spawn = spawn;
        _wanderPeriod = 2 * spawn.WanderRadius / Speed(spawn) + WanderPauseSeconds;

        // Creatures sharing a spawn time shouldn't all set off at once.
        _offset = Unit(spawn.Guid, -1) * 1000;

        if (spawn.MovementType == CreatureMovement.Waypoints && spawn.Waypoints.Count >= 2)
        {
            var legs = new (CreatureWaypoint, CreatureWaypoint, double, double)[spawn.Waypoints.Count];
            var time = 0.0;
            for (var i = 0; i < legs.Length; i++)
            {
                var from = spawn.Waypoints[i];
                var to = spawn.Waypoints[(i + 1) % legs.Length];
                var walk = Math.Sqrt(Square(to.X - from.X) + Square(to.Y - from.Y) + Square(to.Z - from.Z)) / Speed(spawn);
                legs[i] = (from, to, time, walk);
                time += walk + to.DelayMs / 1000.0;
            }

            _legs = legs;
            _pathSeconds = time;
        }
    }

    public CreatureMotionState At(double clock)
    {
        if (_legs.Length > 0 && _pathSeconds > 0)
        {
            return OnPath(clock);
        }

        return _spawn.MovementType == CreatureMovement.Wander && _spawn.WanderRadius > 0
            ? Wandering(clock)
            : new CreatureMotionState(_spawn.X, _spawn.Y, _spawn.Z, _spawn.Orientation, false);
    }

    private CreatureMotionState OnPath(double clock)
    {
        var time = PositiveModulo(clock + _offset, _pathSeconds);
        var index = Array.FindLastIndex(_legs, l => l.Start <= time);
        var (from, to, start, walk) = _legs[Math.Max(index, 0)];
        var elapsed = time - start;
        var facing = FacingAlong(to.X - from.X, to.Y - from.Y);
        if (elapsed >= walk)
        {
            return new CreatureMotionState(to.X, to.Y, to.Z, facing, false);
        }

        var t = elapsed / walk;
        return new CreatureMotionState(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, (float)(from.Z + (to.Z - from.Z) * t), facing, true);
    }

    /// <summary>In each period the creature walks to that period's random point, then waits.</summary>
    private CreatureMotionState Wandering(double clock)
    {
        var slot = (long)Math.Floor((clock + _offset) / _wanderPeriod);
        var elapsed = clock + _offset - slot * _wanderPeriod;
        var (fromX, fromY) = WanderPoint(slot - 1);
        var (toX, toY) = WanderPoint(slot);
        var (dx, dy) = (toX - fromX, toY - fromY);
        var walk = Math.Sqrt(dx * dx + dy * dy) / Speed(_spawn);
        var facing = walk > 0 ? FacingAlong(dx, dy) : _spawn.Orientation;

        if (elapsed >= walk)
        {
            return new CreatureMotionState(toX, toY, null, facing, false);
        }

        var t = elapsed / walk;
        return new CreatureMotionState(fromX + dx * t, fromY + dy * t, null, facing, true);
    }

    private (double X, double Y) WanderPoint(long slot)
    {
        var angle = Unit(_spawn.Guid, slot * 2) * Math.Tau;
        var distance = Math.Sqrt(Unit(_spawn.Guid, slot * 2 + 1)) * _spawn.WanderRadius;
        return (_spawn.X + Math.Cos(angle) * distance, _spawn.Y + Math.Sin(angle) * distance);
    }

    /// <summary>Radians turning left from north. WoW's Y runs west, so that's simply this angle.</summary>
    private static float FacingAlong(double dx, double dy) => (float)Math.Atan2(dy, dx);

    private static float Speed(CreatureSpawn spawn) => spawn.WalkSpeed > 0f ? spawn.WalkSpeed : WalkSpeed;

    private static double Square(double value) => value * value;

    private static double PositiveModulo(double value, double modulus) => value - Math.Floor(value / modulus) * modulus;

    /// <summary>SplitMix64, so the numbers are the same on every platform and .NET version.</summary>
    private static double Unit(long guid, long salt)
    {
        unchecked
        {
            var z = (ulong)guid * 0x9E3779B97F4A7C15UL + (ulong)salt * 0xD1B54A32D192ED03UL + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
