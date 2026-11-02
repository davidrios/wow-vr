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

    /// <summary>Holds the player still even when flying, while the loading screen is up.</summary>
    bool Held { set; }

    WorldMenu Menu { get; }

    /// <summary>Swims rather than walks, floating with the feet at this height when left alone.</summary>
    void SetSwimming(bool swimming, float floatHeight);

    void SetMenuOpen(bool open);

    /// <summary>
    /// Moves without checking for collisions, keeping the player's facing. Re-centering
    /// the world keeps the velocity too, so it can't be felt.
    /// </summary>
    void PlaceAt(Vector3 feet, bool keepVelocity = false);
}
