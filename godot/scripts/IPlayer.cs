using WowVR.Ui;
using Godot;

namespace WowVR;

/// <summary>What the world needs from the player, in either mode.</summary>
public interface IPlayer
{
    /// <summary>The point on the ground under the player, in world space.</summary>
    Vector3 Feet { get; }

    bool Flying { get; set; }

    /// <summary>Touching walkable ground.</summary>
    bool OnGround { get; }

    /// <summary>Holds a walking player still, for when the ground hasn't loaded yet.</summary>
    bool Frozen { set; }

    WorldMenu Menu { get; }

    void SetMenuOpen(bool open);

    /// <summary>
    /// Moves without checking for collisions, keeping the player's facing. Re-centering
    /// the world keeps the velocity too, so it can't be felt.
    /// </summary>
    void PlaceAt(Vector3 feet, bool keepVelocity = false);
}
