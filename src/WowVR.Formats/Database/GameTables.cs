using System.Numerics;
using WowVR.Formats.Assets;

namespace WowVR.Formats.Database;

/// <summary>
/// A spot in the world that does something when the player walks into it, as an instance's
/// entrance does: a sphere when <see cref="Radius"/> is set, or else a box turned by <see cref="BoxOrientation"/>.
/// </summary>
public sealed record AreaTriggerRecord(int Id, int Map, Vector3 Position, float Radius, Vector3 BoxSize, float BoxOrientation)
{
    /// <summary>Whether a point in WoW coordinates is inside it.</summary>
    public bool Contains(Vector3 point)
    {
        if (Radius > 0f)
        {
            return Vector3.DistanceSquared(point, Position) <= Radius * Radius;
        }

        var d = point - Position;
        var cos = MathF.Cos(-BoxOrientation);
        var sin = MathF.Sin(-BoxOrientation);
        var x = (d.X * cos) - (d.Y * sin);
        var y = (d.X * sin) + (d.Y * cos);
        return MathF.Abs(x) <= BoxSize.X / 2f && MathF.Abs(y) <= BoxSize.Y / 2f && MathF.Abs(d.Z) <= BoxSize.Z / 2f;
    }

    public static IReadOnlyList<AreaTriggerRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read(source, @"DBFilesClient\AreaTrigger.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).Select(i => new AreaTriggerRecord(
                dbc.GetInt32(i, 0),
                dbc.GetInt32(i, 1),
                new Vector3(dbc.GetSingle(i, 2), dbc.GetSingle(i, 3), dbc.GetSingle(i, 4)),
                dbc.GetSingle(i, 5),
                new Vector3(dbc.GetSingle(i, 6), dbc.GetSingle(i, 7), dbc.GetSingle(i, 8)),
                dbc.GetSingle(i, 9))).ToList());
}

/// <summary>Which side a faction template is on, and who it's friendly or hostile to.</summary>
public sealed record FactionTemplateRecord(int Id, int Faction, uint Flags, uint OurMask, uint FriendlyMask, uint HostileMask, int[] Enemies, int[] Friends)
{
    public static Dictionary<int, FactionTemplateRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read(source, @"DBFilesClient\FactionTemplate.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).Select(i => new FactionTemplateRecord(
                dbc.GetInt32(i, 0),
                dbc.GetInt32(i, 1),
                dbc.GetUInt32(i, 2),
                dbc.GetUInt32(i, 3),
                dbc.GetUInt32(i, 4),
                dbc.GetUInt32(i, 5),
                [dbc.GetInt32(i, 6), dbc.GetInt32(i, 7), dbc.GetInt32(i, 8), dbc.GetInt32(i, 9)],
                [dbc.GetInt32(i, 10), dbc.GetInt32(i, 11), dbc.GetInt32(i, 12), dbc.GetInt32(i, 13)]))
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First()));

    /// <summary>As the game decides it: listed enemies first, then listed friends, then the masks.</summary>
    public bool IsHostileTo(FactionTemplateRecord other)
    {
        if (other.Faction != 0)
        {
            if (Enemies.Contains(other.Faction))
            {
                return true;
            }

            if (Friends.Contains(other.Faction))
            {
                return false;
            }
        }

        return (HostileMask & other.OurMask) != 0;
    }

    public bool IsFriendlyTo(FactionTemplateRecord other)
    {
        if (other.Faction != 0)
        {
            if (Enemies.Contains(other.Faction))
            {
                return false;
            }

            if (Friends.Contains(other.Faction))
            {
                return true;
            }
        }

        return (FriendlyMask & other.OurMask) != 0 || (OurMask & other.FriendlyMask) != 0;
    }
}

/// <summary>One point of a taxi or transport path, in WoW coordinates.</summary>
public sealed record TaxiPathNodeRecord(int Path, int Index, int Map, Vector3 Position, uint Flags, uint Delay, int ArrivalEvent, int DepartureEvent)
{
    public static ILookup<int, TaxiPathNodeRecord> ReadAll(IAssetSource source) =>
        DbcTables.Read(source, @"DBFilesClient\TaxiPathNode.dbc", dbc =>
            Enumerable.Range(0, dbc.RecordCount).Select(i => new TaxiPathNodeRecord(
                dbc.GetInt32(i, 1),
                dbc.GetInt32(i, 2),
                dbc.GetInt32(i, 3),
                new Vector3(dbc.GetSingle(i, 4), dbc.GetSingle(i, 5), dbc.GetSingle(i, 6)),
                dbc.GetUInt32(i, 7),
                dbc.GetUInt32(i, 8),
                dbc.GetInt32(i, 9),
                dbc.GetInt32(i, 10))).ToList())
            .OrderBy(n => n.Index).ToLookup(n => n.Path);
}
