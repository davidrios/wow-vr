using System.Numerics;

namespace WowVR.Formats.Models;

/// <summary>
/// What the client does to a group's MOCV as it loads: <c>CMapObjGroup::FixColorVertexAlpha</c> and
/// <c>CMapObj::AttenTransVerts</c>, from the decompiled code on wowdev.wiki's WMO page. The client's
/// MapObj shaders then draw an interior as <c>2 * texture * color</c>, clamped only at the end, so
/// a color here is half of what it does.
/// </summary>
public static class WmoVertexLight
{
    private const uint RootFlagNoAttenuation = 0x1;
    private const uint RootFlagNoBaseColor = 0x2;
    private const uint RootFlagNoFix = 0x8;

    private const uint GroupFlagExterior = 0x8;

    /// <summary>A portal to a group with either of these leads outside.</summary>
    private const uint GroupFlagsOutside = 0x48;

    /// <summary>How far a portal to the outside reaches, in yards.</summary>
    private const float PortalReach = 6f;

    /// <summary>What attenuation moves a color towards, where the fix leaves colors halved.</summary>
    private const float AttenuatedColor = 127f;

    /// <summary>The first step, on a group's colors as RGBA. Vertices below the end are in its transition batches.</summary>
    public static void Fix(byte[] colors, int transitionEnd, Vector3 rootAmbient, uint rootFlags, uint groupFlags)
    {
        var ambient = BaseColor(rootAmbient, rootFlags);
        var outsideAlpha = (groupFlags & GroupFlagExterior) != 0 ? byte.MaxValue : (byte)0;
        if ((rootFlags & RootFlagNoFix) != 0)
        {
            for (var vertex = transitionEnd; vertex < colors.Length / 4; vertex++)
            {
                colors[vertex * 4 + 3] = outsideAlpha;
            }

            return;
        }

        for (var vertex = 0; vertex < colors.Length / 4; vertex++)
        {
            var at = vertex * 4;
            int alpha = colors[at + 3];
            for (var channel = 0; channel < 3; channel++)
            {
                int color = colors[at + channel];
                var result = vertex < transitionEnd
                    ? (color - ambient[channel]) * (255 - alpha) / 255 / 2
                    : (color * alpha / 64 + color - ambient[channel]) / 2;
                colors[at + channel] = (byte)Math.Clamp(result, 0, byte.MaxValue);
            }

            if (vertex >= transitionEnd)
            {
                colors[at + 3] = outsideAlpha;
            }
        }
    }

    /// <summary>The second step, once it's known what every group is. A missing group is null.</summary>
    public static void Apply(WmoRoot root, IReadOnlyList<WmoGroup?> groups, int index)
    {
        if (groups[index] is not { } group)
        {
            return;
        }

        var attenuates = (root.Flags & (RootFlagNoAttenuation | RootFlagNoFix)) == 0;
        var portals = attenuates ? PortalsOf(root, groups, group) : [];
        var ambient = root.Ambient * 255f / 2f;
        foreach (var surface in group.Surfaces)
        {
            if (surface.Colors is not { } colors)
            {
                continue;
            }

            for (var vertex = 0; vertex < colors.Length / 4; vertex++)
            {
                if (attenuates && surface.Transition?[vertex] == true)
                {
                    var p = surface.Positions[vertex];
                    Attenuate(colors, vertex * 4, Opacity(new Vector3(p.X, -p.Z, p.Y), portals));
                }

                // Half, because the shaders double this: 2 * color + base color matches the real client by eye.
                colors[vertex * 4] = (byte)Math.Min(colors[vertex * 4] + ambient.X, byte.MaxValue);
                colors[vertex * 4 + 1] = (byte)Math.Min(colors[vertex * 4 + 1] + ambient.Y, byte.MaxValue);
                colors[vertex * 4 + 2] = (byte)Math.Min(colors[vertex * 4 + 2] + ambient.Z, byte.MaxValue);
            }
        }
    }

    /// <summary>A portal as this needs it, in the file's axes, with what's behind it.</summary>
    public readonly record struct Portal(Vector3 Normal, float Offset, Vector3[] Corners, int Side, bool LeadsOutside);

    /// <summary>How far the outside reaches a point, from 0 to 1.</summary>
    public static float Opacity(Vector3 position, IReadOnlyList<Portal> portals)
    {
        var opacity = 0f;
        foreach (var portal in portals)
        {
            var distance = Distance(position, portal);
            if (portal.LeadsOutside)
            {
                var near = 1f - (distance >= 0f ? distance / PortalReach : 0f);
                if (near > 0.001f)
                {
                    opacity += near;
                }
            }
            else if (distance > -1f)
            {
                opacity = 0f;
                if (distance < 1f)
                {
                    break;
                }
            }
        }

        return opacity > 0.001f ? MathF.Min(opacity, 1f) : 0f;
    }

    private static void Attenuate(byte[] colors, int at, float opacity)
    {
        for (var channel = 0; channel < 3; channel++)
        {
            colors[at + channel] = (byte)MathF.Round((AttenuatedColor - colors[at + channel]) * opacity + colors[at + channel]);
        }

        colors[at + 3] = (byte)(opacity * byte.MaxValue);
    }

    private static int[] BaseColor(Vector3 ambient, uint rootFlags) => (rootFlags & RootFlagNoBaseColor) != 0
        ? [0, 0, 0]
        : [(int)MathF.Round(ambient.X * 255f), (int)MathF.Round(ambient.Y * 255f), (int)MathF.Round(ambient.Z * 255f)];

    private static List<Portal> PortalsOf(WmoRoot root, IReadOnlyList<WmoGroup?> groups, WmoGroup group)
    {
        var portals = new List<Portal>();
        var (start, count) = group.PortalRange;
        for (var i = start; i < start + count && i < root.PortalRefs.Length; i++)
        {
            var (portal, other, side) = root.PortalRefs[i];
            if (portal < root.Portals.Length && other < groups.Count && groups[other] is { } neighbor)
            {
                var (first, corners, normal, offset) = root.Portals[portal];
                portals.Add(new Portal(normal, offset, root.PortalVertices[first..(first + corners)], side, (neighbor.Flags & GroupFlagsOutside) != 0));
            }
        }

        return portals;
    }

    /// <summary>On the portal's side, to its plane if the point is in front of the portal, and otherwise to its nearest edge.</summary>
    private static float Distance(Vector3 position, in Portal portal)
    {
        var toPlane = Vector3.Dot(position, portal.Normal) + portal.Offset;
        return Inside(position - portal.Normal * toPlane, portal)
            ? portal.Side * toPlane
            : DistanceToEdges(position, portal.Corners);
    }

    /// <summary>Whether a point on a portal's plane is within its corners, seen along the plane's largest axis.</summary>
    private static bool Inside(Vector3 point, in Portal portal)
    {
        var abs = Vector3.Abs(portal.Normal);
        Func<Vector3, Vector2> flat = abs.X >= abs.Y && abs.X >= abs.Z
            ? v => new Vector2(v.Y, v.Z)
            : abs.Y >= abs.Z ? v => new Vector2(v.X, v.Z) : v => new Vector2(v.X, v.Y);
        var p = flat(point);
        var inside = false;
        var corners = portal.Corners;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (a, b) = (flat(corners[i]), flat(corners[j]));
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float DistanceToEdges(Vector3 point, Vector3[] corners)
    {
        var best = float.MaxValue;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var edge = corners[i] - corners[j];
            var length = edge.LengthSquared();
            var along = length > 0f ? Math.Clamp(Vector3.Dot(point - corners[j], edge) / length, 0f, 1f) : 0f;
            best = MathF.Min(best, Vector3.Distance(point, corners[j] + edge * along));
        }

        return best;
    }
}
