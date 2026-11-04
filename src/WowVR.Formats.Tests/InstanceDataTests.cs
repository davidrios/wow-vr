using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

/// <summary>Checks what playing on a server reads from the client: instance maps, area triggers and factions.</summary>
public class InstanceDataTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private MpqAssetSource Source
    {
        get
        {
            if (client.Source is null)
            {
                Assert.Skip("WOWVR_CLIENT_PATH is not set.");
            }

            return client.Source;
        }
    }

    [Fact]
    public void AnInstanceMadeOfOneWmoNamesIt()
    {
        // The Stockade, under Stormwind.
        var map = WdtMap.Read(Source, "StormwindJail");

        Assert.Equal(0, map.TileCountWithData);
        Assert.NotNull(map.GlobalWmo);
        Assert.EndsWith(".wmo", map.GlobalWmo.Model, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(Source.Read(map.GlobalWmo.Model));
    }

    [Fact]
    public void AContinentHasNoGlobalWmo()
    {
        var map = WdtMap.Read(Source, "Azeroth");

        Assert.Null(map.GlobalWmo);
        Assert.True(map.TileCountWithData > 100);
    }

    [Fact]
    public void TheDeadminesEntranceIsAnAreaTriggerInWestfall()
    {
        // The trigger AzerothCore's areatrigger_teleport sends to the Deadmines (map 36).
        var trigger = AreaTriggerRecord.ReadAll(Source).Single(t => t.Id == 78);

        Assert.Equal(0, trigger.Map);
        Assert.True(trigger.Contains(trigger.Position));
        Assert.False(trigger.Contains(trigger.Position + new System.Numerics.Vector3(100, 0, 0)));
    }

    [Fact]
    public void StormwindGuardsAreFriendlyToHumansAndHostileToOrcs()
    {
        var factions = FactionTemplateRecord.ReadAll(Source);
        var human = factions[1];
        var orc = factions[2];
        var stormwindGuard = factions[11];

        Assert.True(stormwindGuard.IsFriendlyTo(human));
        Assert.False(stormwindGuard.IsHostileTo(human));
        Assert.True(stormwindGuard.IsHostileTo(orc));
    }

    [Fact]
    public void TransportPathsBetweenContinentsTakeMinutesAndStopAtDocks()
    {
        var paths = TaxiPathNodeRecord.ReadAll(Source);
        var crossing = paths.Where(p => p.Select(n => n.Map).Distinct().Count() == 2 && p.Any(n => n.Flags == 2)).ToList();
        Assert.NotEmpty(crossing);

        foreach (var nodes in crossing)
        {
            // The usual speed and acceleration of ships and zeppelins.
            var path = TransportPath.Build(nodes.ToList(), 30f, 1f);
            Assert.NotNull(path);
            Assert.InRange(path.Period, 60_000u, 1_800_000u);
            Assert.Equal(2, path.Maps.Count());

            // Somewhere along the cycle it stands still at a dock for a while.
            var still = 0;
            var last = path.At(0).Position;
            for (ulong t = 1000; t < path.Period; t += 1000)
            {
                var here = path.At(t);
                Assert.True(float.IsFinite(here.Position.X) && float.IsFinite(here.Orientation));
                still += System.Numerics.Vector3.Distance(here.Position, last) < 0.01f ? 1 : 0;
                last = here.Position;
            }

            Assert.True(still >= 5, $"path {nodes.Key} never stops");
        }
    }
}
