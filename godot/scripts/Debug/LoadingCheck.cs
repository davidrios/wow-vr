using System.Linq;
using System.Threading.Tasks;
using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--loading-check</c> prints PASS or FAIL for the loading screen hiding the world until it
/// has all loaded, at the start and after a landmark's button, then quits.
/// </summary>
public partial class LoadingCheck : Node
{
    private const double GiveUpSeconds = 180;
    private const string Landmark = "Booty Bay";

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }
    public required CreatureManager Creatures { get; init; }
    public required LoadingScreen Screen { get; init; }

    private int _failures;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        Check("It's up at the start", Screen.Showing, "not showing");
        await Covers("The start");

        var button = Player.Menu.FindChildren("*", nameof(Button), true, false).OfType<Button>().FirstOrDefault(b => b.Text == Landmark);
        if (button is null)
        {
            Check("A landmark's button exists", false, Landmark);
        }
        else
        {
            var before = Player.Feet;
            Screen.WorstLoadingFrame = 0;
            button.EmitSignal(BaseButton.SignalName.Pressed);
            Check("A teleport brings it up at once", Screen.Showing, "not showing");
            await Covers(Landmark);
            Check($"{Landmark}: the player arrived", Tiles.LoadedTileCount > 0 && Player.Feet.Y != before.Y, $"at {Player.Feet}");
        }

        var result = _failures == 0 ? "PASS" : $"FAIL: {_failures} failed";
        GD.Print($"Loading check: {result}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>Watches one load through, from the screen coming up to it having gone.</summary>
    private async Task Covers(string what)
    {
        var camera = GetViewport().GetCamera3D();
        var start = Time.GetTicksMsec();
        var sawHidden = false;
        var leakedWorld = false;
        while (Screen.Showing && Time.GetTicksMsec() - start < GiveUpSeconds * 1000)
        {
            if (Screen.WorldHidden)
            {
                sawHidden = true;
                leakedWorld |= (camera.CullMask & 1u) != 0 && Time.GetTicksMsec() - start > 200;
            }
            else if (sawHidden && Tiles.IsBusy && Tiles.Covered)
            {
                Check($"{what}: nothing is still loading when the world shows", false, $"{Tiles.LoadedFraction:P0}");
                sawHidden = false;
            }

            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        Check($"{what}: it went away", !Screen.Showing, $"after {(Time.GetTicksMsec() - start) / 1000.0:F0} s");
        Check($"{what}: the world wasn't drawn behind it", !leakedWorld, "the camera could see layer 1");
        Check($"{what}: everything near has loaded", Tiles.LoadedFraction >= 1f, $"{Tiles.LoadedFraction:P0}");
        Check($"{what}: the camera sees the world again", (camera.CullMask & 1u) != 0, $"mask {camera.CullMask}");
        Check($"{what}: the sound is back", AudioServer.GetBusVolumeLinear(0) > 0.99f, $"{AudioServer.GetBusVolumeLinear(0)}");
        GD.Print($"Loading check: {what} took {(Time.GetTicksMsec() - start) / 1000.0:F1} s, slowest frame {Screen.WorstLoadingFrame:F0} ms, {Creatures.Nodes.Count()} creatures");
    }

    private void Check(string name, bool passed, string detail)
    {
        if (!passed)
        {
            _failures++;
            GD.PrintErr($"Loading check: {name}: {detail}");
        }
    }
}
