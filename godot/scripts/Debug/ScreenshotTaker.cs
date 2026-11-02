using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--screenshot=file.png</c> waits for the world around the player to load, saves one frame
/// and quits, to check how things look. The output shows game assets, so don't commit it.
/// </summary>
public partial class ScreenshotTaker : Node
{
    private const int TilesBeforeShot = 9;
    private const double SettleSeconds = 4;
    private const double GiveUpSeconds = 120;

    public required TileManager Tiles { get; init; }
    public required string Path { get; init; }
    public CreatureManager? Creatures { get; init; }
    public LoadingScreen? Loading { get; init; }

    private double _age;
    private double _settled;

    public override void _Process(double delta)
    {
        _age += delta;
        var ready = Tiles.LoadedTileCount >= TilesBeforeShot && !Tiles.IsBusy && Creatures?.IsBusy != true && Loading is not { Hold: false, Showing: true };
        _settled = ready ? _settled + delta : 0;

        if (_settled < SettleSeconds && _age < GiveUpSeconds)
        {
            return;
        }

        var result = GetViewport().GetTexture().GetImage().SavePng(Path);
        GD.Print($"Screenshot {Path}: {result}");
        GetTree().Quit();
    }
}
