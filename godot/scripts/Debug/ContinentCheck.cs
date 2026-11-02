using System.Linq;
using WowVR.Formats.World;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--continent-check</c> presses each continent's menu button in turn, which reloads the
/// world, and prints PASS or FAIL for the player ending up on the ground of each, then quits.
/// </summary>
public partial class ContinentCheck : Node
{
    private const double GiveUpSeconds = 120;
    private const double SettleSeconds = 2;

    /// <summary>Survives the reloads this causes.</summary>
    private static int _visited;

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }
    public required string Map { get; init; }

    private double _age;
    private double _settled;
    private bool _done;

    public override void _Process(double delta)
    {
        if (_done)
        {
            return;
        }

        _age += delta;
        var ready = Tiles.LoadedTileCount > 0 && !Tiles.IsBusy && !Tiles.PlacingPlayer && Player.OnGround;
        _settled = ready ? _settled + delta : 0;
        if (_age > GiveUpSeconds)
        {
            Finish($"FAIL: {Map} didn't settle: {Tiles.LoadedTileCount} tiles, busy {Tiles.IsBusy}, placing {Tiles.PlacingPlayer}, on ground {Player.OnGround}");
            return;
        }

        if (_settled < SettleSeconds)
        {
            return;
        }

        GD.Print($"Continent check: stood on {Map} with {Tiles.LoadedTileCount} tiles, after {_age:F0} s");

        // A subscriber left behind by the reload would throw here.
        AppSettings.NamePlates = !AppSettings.NamePlates;
        AppSettings.NamePlates = !AppSettings.NamePlates;

        if (_visited == Landmarks.Continents.Count)
        {
            Finish("PASS");
            return;
        }

        var next = Landmarks.Continents[_visited++ % Landmarks.Continents.Count];
        if (next.Map == Map)
        {
            next = Landmarks.Continents[_visited++ % Landmarks.Continents.Count];
        }

        var button = Player.Menu.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.Text == next.Name);
        if (button is null)
        {
            Finish($"FAIL: no button for {next.Name}");
            return;
        }

        _done = true;
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void Finish(string result)
    {
        _done = true;
        GD.Print($"Continent check: {result}");
        GetTree().Quit(result == "PASS" ? 0 : 1);
    }
}
