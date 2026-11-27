namespace WowVR.Net;

/// <summary>
/// How another player moves, from their last movement packet: their flags, for the clips their
/// body plays, and in the air the height a jump or fall has them at as time goes on, under the
/// game's gravity, from <paramref name="StartZ"/> where the packet put them.
/// </summary>
/// <param name="UpSpeed">The speed a jump set off upwards with, yards a second; 0 for a fall.</param>
/// <param name="FallSeconds">How long they had been in the air when the packet was sent.</param>
public readonly record struct PlayerMove(MoveFlags Flags, float StartZ, float UpSpeed, float FallSeconds)
{
    /// <summary>The game's gravity, yards a second squared.</summary>
    public const float Gravity = 19.29110527f;

    /// <summary>The fastest anything falls, yards a second.</summary>
    public const float TerminalSpeed = 60.148003f;

    /// <summary>A player's movement as a packet carried it. Packets give a jump's upward speed as negative.</summary>
    public static PlayerMove From(MovementInfo info) =>
        new(info.Flags, info.Position.Z, (info.Flags & MoveFlags.Falling) != 0 ? -info.JumpVerticalSpeed : 0f, info.FallTime / 1000f);

    public bool Airborne => (Flags & MoveFlags.Falling) != 0;

    /// <summary>In the air because they jumped, not because they walked off something.</summary>
    public bool Jumped => Airborne && UpSpeed > 0f;

    /// <summary>Swimming, or flying, which plays the same clips.</summary>
    public bool Swimming => (Flags & (MoveFlags.Swimming | MoveFlags.Flying)) != 0;

    public bool Backward => (Flags & MoveFlags.Backward) != 0;

    /// <summary>Sideways: 1 to the left, -1 to the right, 0 neither.</summary>
    public int Side => (Flags & MoveFlags.StrafeLeft) != 0 ? 1 : (Flags & MoveFlags.StrafeRight) != 0 ? -1 : 0;

    /// <summary>Their height <paramref name="seconds"/> after the packet: where it put them, unless in the air.</summary>
    public float HeightAt(double seconds) =>
        Airborne ? StartZ + Rise(FallSeconds + (float)seconds) - Rise(FallSeconds) : StartZ;

    /// <summary>How far up from where they left the ground, <paramref name="t"/> seconds in, falling no faster than the limit.</summary>
    private float Rise(float t)
    {
        var limit = (UpSpeed + TerminalSpeed) / Gravity;
        return t <= limit
            ? (UpSpeed * t) - (Gravity * t * t / 2f)
            : (UpSpeed * limit) - (Gravity * limit * limit / 2f) - (TerminalSpeed * (t - limit));
    }
}
