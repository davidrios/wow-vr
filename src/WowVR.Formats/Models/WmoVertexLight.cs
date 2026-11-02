using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>
/// What the client does to a group's MOCV when it loads, following acazuc's client. Alpha ends up
/// as how far a vertex takes the world's light rather than its baked color.
/// </summary>
public static class WmoVertexLight
{
    /// <summary>The file's alpha stands. Without this, alpha comes from the distance to portals leading outside.</summary>
    private const uint RootFlagKeepsAlpha = 0x1;
    private const uint RootFlagLightensInteriors = 0x8;

    private const uint GroupFlagOutdoor = 0x8;
    private const uint GroupFlagExteriorLit = 0x40;
    private const uint GroupFlagIndoor = 0x2000;

    /// <summary>How far from a portal to the outside its light reaches, in yards.</summary>
    private const float PortalReach = 6f;

    /// <summary>Rewrites the colors of a group's surfaces. A missing group is null.</summary>
    public static void Apply(WmoRoot root, IReadOnlyList<WmoGroup?> groups, int index)
    {
        if (groups[index] is not { } group)
        {
            return;
        }

        var lightens = (root.Flags & RootFlagLightensInteriors) != 0;
        var fromPortals = (root.Flags & RootFlagKeepsAlpha) == 0;
        if (!lightens && !fromPortals)
        {
            return;
        }

        var planes = fromPortals ? PlanesToOutside(root, groups, group) : [];
        foreach (var surface in group.Surfaces)
        {
            if (surface.Colors is not { } colors)
            {
                continue;
            }

            for (var vertex = 0; vertex < colors.Length / 4; vertex++)
            {
                var at = vertex * 4;
                if (lightens)
                {
                    var alpha = colors[at + 3];
                    colors[at] = Lightened(colors[at], alpha);
                    colors[at + 1] = Lightened(colors[at + 1], alpha);
                    colors[at + 2] = Lightened(colors[at + 2], alpha);
                    colors[at + 3] = byte.MaxValue;
                }

                if (fromPortals)
                {
                    var p = surface.Positions[vertex];
                    colors[at + 3] = PortalAlpha(new Vector3(p.X, -p.Z, p.Y), planes);
                }
            }
        }
    }

    /// <summary>A color channel in a building with the lighten flag: half, and more with more alpha.</summary>
    public static byte Lightened(byte channel, byte alpha) =>
        (byte)Math.Min((channel + alpha * channel / 64) / 2, byte.MaxValue);

    /// <summary>Full on a portal's plane, in the file's axes, falling to none away from the nearest.</summary>
    public static byte PortalAlpha(Vector3 position, IReadOnlyList<(Vector3 Normal, float Offset)> planes)
    {
        var nearest = 1f;
        foreach (var (normal, offset) in planes)
        {
            nearest = MathF.Min(nearest, MathF.Abs(Vector3.Dot(position, normal) + offset) / PortalReach);
        }

        return (byte)(byte.MaxValue - (int)(nearest * byte.MaxValue));
    }

    private static List<(Vector3 Normal, float Offset)> PlanesToOutside(WmoRoot root, IReadOnlyList<WmoGroup?> groups, WmoGroup group)
    {
        var planes = new List<(Vector3, float)>();
        var (start, count) = group.PortalRange;
        for (var i = start; i < start + count && i < root.PortalRefs.Length; i++)
        {
            var (portal, other, _) = root.PortalRefs[i];
            if (portal < root.Portals.Length && other < groups.Count && groups[other] is { } neighbor && LeadsOutside(neighbor.Flags))
            {
                planes.Add((root.Portals[portal].Normal, root.Portals[portal].Offset));
            }
        }

        return planes;
    }

    private static bool LeadsOutside(uint flags) =>
        (flags & GroupFlagIndoor) == 0 || (flags & (GroupFlagExteriorLit | GroupFlagOutdoor)) != 0;
}
