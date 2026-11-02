using System.Threading.Tasks;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--water-check</c> puts the player in deep water near the start, and prints PASS or FAIL
/// for floating at the surface, and for rising again from below it with the view underwater.
/// Start it near water, with <c>--start</c>.
/// </summary>
public partial class WaterCheck : Node
{
    private const float MinDepth = 4f;
    private const int SearchRadius = 60;
    private const int SearchStep = 3;
    private const float FloatTolerance = 0.3f;

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }
    public required PlayerWater Water { get; init; }

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        while (Tiles.LoadedTileCount == 0 || Tiles.IsBusy)
        {
            await Seconds(0.5);
        }

        var start = Player.Feet;
        Vector3? spot = null;
        for (var x = -SearchRadius; x <= SearchRadius && spot is null; x += SearchStep)
        {
            for (var z = -SearchRadius; z <= SearchRadius && spot is null; z += SearchStep)
            {
                var point = start + new Vector3(x, 500f, z);
                if (Tiles.LiquidAt(point with { Y = -500f }) is null && Tiles.DeepestLiquidAt(point) is { } deep && deep.Depth > MinDepth)
                {
                    spot = point with { Y = deep.Surface + 3f };
                }
            }
        }

        if (spot is not { } place)
        {
            Finish("FAIL: no deep water near the start");
            return;
        }

        Player.Flying = false;
        Player.PlaceAt(place);
        await Seconds(6);
        var floats = Water.Swimming && Mathf.Abs(Player.Feet.Y - Water.FloatHeight) < FloatTolerance && !Water.Underwater;
        GD.Print($"Water check: floating {Water.Swimming}, feet {Player.Feet.Y:F2} against {Water.FloatHeight:F2}, underwater {Water.Underwater}");

        Player.PlaceAt(new Vector3(place.X, Water.FloatHeight - 2.5f, place.Z));
        await Seconds(0.3);
        var sawUnder = Water.Underwater;
        GD.Print($"Water check: below, feet {Player.Feet.Y:F2}, underwater {sawUnder}");
        await Seconds(8);
        var rose = !Water.Underwater && Mathf.Abs(Player.Feet.Y - Water.FloatHeight) < FloatTolerance;
        GD.Print($"Water check: after rising, feet {Player.Feet.Y:F2}, underwater {Water.Underwater}");

        Finish(floats && sawUnder && rose ? "PASS" : $"FAIL: floats {floats}, went under {sawUnder}, rose {rose}");
    }

    private async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private void Finish(string result)
    {
        GD.Print($"Water check: {result}");
        GetTree().Quit(result == "PASS" ? 0 : 1);
    }
}
