using WowVR.Formats;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// Pointing at things in the world, with the mouse or a VR controller's ray: selecting units,
/// and using them or game objects as right-clicking does in WoW. Units are picked as upright
/// capsules of their model's size, and game objects as spheres, since neither has collision.
/// </summary>
public sealed class Interaction(ServerWorld world, CreatureManager creatures, TileManager tiles)
{
    /// <summary>Further than this can't be picked, in yards.</summary>
    private const float Reach = 80f;

    /// <summary>A game object is a sphere this big, in yards.</summary>
    private const float ObjectRadius = 1.5f;

    /// <summary>Game objects worth clicking: doors, buttons, chests, chairs, mailboxes, portals and the like, not decoration.</summary>
    private static readonly uint[] UsableObjectTypes = [0, 1, 3, 7, 10, 13, 18, 19, 22, 23, 25];

    /// <summary>Tab picks hostile units this near, in yards.</summary>
    private const float TabRange = 40f;

    public ServerWorld World => world;

    /// <summary>Aims ground spells; while one is aimed, clicks place it instead of selecting.</summary>
    public GroundTargeter? Ground { get; set; }

    /// <summary>The unit or object nearest along a ray in world space, or null.</summary>
    public (ulong Guid, bool IsObject)? Pick(Vector3 from, Vector3 direction)
    {
        direction = direction.Normalized();
        (ulong Guid, bool IsObject)? best = null;
        var bestT = Reach;
        foreach (var (guid, node) in creatures.LiveNodes)
        {
            var feet = node.GlobalPosition;
            var top = feet + (Vector3.Up * Mathf.Max(node.Height, 1f));
            var t = RayToSegment(from, direction, feet, top, node.Radius);
            if (t is { } hit && hit < bestT)
            {
                bestT = hit;
                best = ((ulong)guid, false);
            }
        }

        foreach (var (guid, obj) in world.GameObjects)
        {
            if (System.Array.IndexOf(UsableObjectTypes, obj.Type) < 0)
            {
                continue;
            }

            var godot = WowCoords.ToGodot(obj.Position);
            var center = tiles.WorldToLocal(godot.X, godot.Y, godot.Z) + (Vector3.Up * 0.5f);
            var t = RayToSegment(from, direction, center, center, ObjectRadius);
            if (t is { } hit && hit < bestT)
            {
                bestT = hit;
                best = (guid, true);
            }
        }

        return best;
    }

    /// <summary>Left-click: selects what's there, or nothing.</summary>
    public void Select(Vector3 from, Vector3 direction)
    {
        if (Pick(from, direction) is { IsObject: false } unit)
        {
            world.SetTarget(unit.Guid);
        }
        else
        {
            world.SetTarget(0);
        }
    }

    /// <summary>Right-click: talks to, loots or attacks a unit, or uses an object. False when nothing is there.</summary>
    public bool Use(Vector3 from, Vector3 direction)
    {
        switch (Pick(from, direction))
        {
            case { IsObject: true } obj:
                world.UseGameObject(obj.Guid);
                return true;
            case { } unit:
                world.Use(unit.Guid);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Tab: the next hostile, living unit in front, nearest first, after the one selected.</summary>
    public void TargetNextEnemy(Vector3 feet, Vector3 forward)
    {
        var candidates = new System.Collections.Generic.List<(ulong Guid, float Distance)>();
        foreach (var (guid, node) in creatures.LiveNodes)
        {
            if (!world.Units.TryGetValue((ulong)guid, out var unit) || unit.IsDead || unit.Type != ObjectType.Unit
                || world.ReactionTo(unit) != Reaction.Hostile || unit.NotSelectable)
            {
                continue;
            }

            var offset = node.GlobalPosition - feet;
            var distance = offset.Length();
            if (distance < TabRange && (distance < 3f || offset.Normalized().Dot(forward) > 0.2f))
            {
                candidates.Add(((ulong)guid, distance));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var current = candidates.FindIndex(c => c.Guid == world.Target);
        world.SetTarget(candidates[(current + 1) % candidates.Count].Guid);
    }

    /// <summary>How far along a ray it first comes within <paramref name="radius"/> of a segment, or null.</summary>
    private static float? RayToSegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius)
    {
        // Closest points between the ray and the segment.
        var u = direction;
        var v = b - a;
        var w = origin - a;
        var uu = u.Dot(u);
        var uv = u.Dot(v);
        var vv = v.Dot(v);
        var uw = u.Dot(w);
        var vw = v.Dot(w);
        var denominator = (uu * vv) - (uv * uv);
        float s, t;
        if (vv < 1e-6f)
        {
            s = 0;
            t = -uw / uu;
        }
        else if (denominator < 1e-6f)
        {
            s = 0;
            t = -uw / uu;
        }
        else
        {
            t = ((uv * vw) - (vv * uw)) / denominator;
            s = Mathf.Clamp(((uu * vw) - (uv * uw)) / denominator, 0f, 1f);
            t = (s * uv - uw) / uu;
        }

        if (t < 0)
        {
            return null;
        }

        var closestRay = origin + (u * t);
        var closestSegment = a + (v * s);
        return closestRay.DistanceTo(closestSegment) <= radius ? t : null;
    }
}
