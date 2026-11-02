// The simulated trackers are XRControllerTrackers, which is what this warning asks for.
#pragma warning disable CS0618

using System;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Formats;
using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Runs the player through the things that can go wrong on real terrain, prints PASS or FAIL
/// for each, and quits. Run with: godot --path godot -- --movement-check
/// Add --xr-player to check the VR player's scene and physics without a headset.
/// </summary>
public partial class MovementCheck : Node
{
    private const int SecondsToLoad = 15;
    private const float StandTolerance = 0.5f;

    /// <summary>A little over walking speed, because stepping onto something moves the player slightly.</summary>
    private const float MaxWalkedInASecond = AppSettings.WalkSpeed * 1.15f;

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }

    private int _failures;

    public override void _Ready() => _ = RunAsync();

    private async Task RunAsync()
    {
        // Lets Main finish starting the world, which comes after adding this node.
        await Seconds(0.1);
        await StandsOnTheGround("At the start");

        var start = Player.Feet;
        await WalksAlongTheGround();

        Player.Flying = true;
        await Seconds(0.2);
        var height = Player.Feet.Y + 50f;
        Player.PlaceAt(new Vector3(Player.Feet.X, height, Player.Feet.Z));
        await Seconds(1);
        Check("Flying doesn't fall", Mathf.Abs(Player.Feet.Y - height) < 1f, $"height {Player.Feet.Y - height:F1} from where it started");

        Player.PlaceAt(new Vector3(600, height, 0), keepVelocity: true);
        await Seconds(0.2);
        Check("Re-centering moves the world back to the player", new Vector2(Player.Feet.X, Player.Feet.Z).Length() < 100f, $"at {Player.Feet}");
        Player.Flying = false;
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("After flying");

        foreach (var landmark in new[] { "Northshire", "Stormwind", "Booty Bay", "Brill", "Goldshire" })
        {
            PressMenuButton(landmark);
            await StandsOnTheGround($"Teleported to {landmark}");
            if (landmark == "Stormwind")
            {
                await ReportFrameCost(landmark);
            }
        }

        var surface = Player.Feet;
        Player.PlaceAt(surface + Vector3.Down * 30f);
        await Seconds(0.5);
        Check("Below the terrain moves up to it", Mathf.Abs(Player.Feet.Y - surface.Y) < 2f, $"at {Player.Feet.Y:F1}, surface {surface.Y:F1}");

        // Booty Bay's tile is at the map's edge of land, with empty ocean tiles close by.
        await Seconds(0.5);
        var ground = Player.Feet;
        Player.PlaceAt(ground + new Vector3(40000, 0, 0));
        await Seconds(1);
        Check("Off the map goes back to solid ground", Player.Feet.DistanceTo(ground) < 5f, $"at {Player.Feet}");

        await VignetteClosesWhenMoving();
        if (Player is Xr.XrPlayer xrPlayer)
        {
            await ControllersWork(xrPlayer);
        }

        ScaleButtonsWork();

        GD.Print(_failures == 0 ? "Movement check passed." : $"Movement check FAILED: {_failures}.");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>Drives the real controller nodes with simulated trackers, as a headset would.</summary>
    private async Task ControllersWork(Xr.XrPlayer xr)
    {
        var left = AddTracker("left_hand", XRPositionalTracker.TrackerHand.Left);
        var right = AddTracker("right_hand", XRPositionalTracker.TrackerHand.Right);
        SetPose(left, new Vector3(-0.2f, 1.1f, -0.3f), -25f);
        SetPose(right, new Vector3(0.2f, 1.1f, -0.3f), 0f);
        await Seconds(0.3);

        // Teleport: hold the trigger to aim, release to go. A natural aim is close to level.
        var teleport = xr.GetNode<Node3D>("LeftController/FunctionTeleport");
        var aimFrom = Player.Feet;
        // XR Tools' arc has narrow blind spots, about 2.5 degrees wide, where it finds no target
        // on flat ground, so this expects most angles to work rather than all of them.
        var (aims, landed) = (0, 0);
        foreach (var pitch in new[] { -60f, -50f, -40f, -30f, -25f, -20f, -10f, -5f, 0f, 5f })
        {
            // From the same spot each time, on a road, so trees don't get in the way of the arc.
            Player.PlaceAt(aimFrom + Vector3.Up);
            Tiles.PlacePlayerOnSurface();
            await StandsOnTheGround($"Ready to aim {pitch} degrees");
            SetPose(left, new Vector3(-0.2f, 1.1f, -0.3f), pitch);
            await Seconds(0.1);
            var start = Player.Feet;
            left.SetInput("trigger_click", true);
            await Seconds(0.3);
            var can = teleport.Get("can_teleport").AsBool();
            left.SetInput("trigger_click", false);
            await Seconds(0.3);
            var moved = new Vector2(Player.Feet.X - start.X, Player.Feet.Z - start.Z).Length();
            aims++;
            // In real meters, because the same aim covers more yards at a larger scale.
            landed += can && moved / AppSettings.WorldScale > MinTeleportMeters ? 1 : 0;
            GD.Print($"  aimed at {pitch} degrees: moved {moved:F1} yards");
        }

        Check("Teleport lands at most aim angles", landed * 5 >= aims * 4, $"{landed} of {aims}");
        SetPose(left, new Vector3(-0.2f, 1.1f, -0.3f), -25f);
        await Seconds(0.3);

        Vector3 before;

        // Walking.
        before = Player.Feet;
        left.SetInput("primary", new Vector2(0, 1));
        await Seconds(1);
        left.SetInput("primary", Vector2.Zero);
        var walked = new Vector2(Player.Feet.X - before.X, Player.Feet.Z - before.Z).Length();
        Check("Left stick walks at walking speed", walked > 3f && walked < MaxWalkedInASecond, $"moved {walked:F1} yards in a second");
        await Seconds(0.5);

        // Turning.
        var basis = xr.GlobalBasis;
        right.SetInput("primary", new Vector2(1, 0));
        await Seconds(0.3);
        right.SetInput("primary", Vector2.Zero);
        Check("Right stick turns", !xr.GlobalBasis.IsEqualApprox(basis), "");
        await Seconds(0.3);

        // Flying.
        left.SetInput("by_button", true);
        left.SetInput("by_button", false);
        await Seconds(0.1);
        Check("Y toggles flying", Player.Flying, "");
        before = Player.Feet;
        left.SetInput("primary", new Vector2(0, 1));
        await Seconds(1);
        left.SetInput("primary", Vector2.Zero);
        Check("Left stick flies", Player.Feet.DistanceTo(before) > 5f, $"moved {Player.Feet.DistanceTo(before):F1} yards in a second");
        left.SetInput("by_button", true);
        left.SetInput("by_button", false);

        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("Back on the ground");

        // Up the inn's step and through its door, with the stick.
        const float doorOffset = -4f, startEast = 12f, innWallEast = 23f;
        var center = WowCoords.ToGodot(new System.Numerics.Vector3(-9464f, 62f, 57f));
        var goldshire = Tiles.WorldToLocal(center.X, center.Y, center.Z);
        Player.PlaceAt(goldshire + new Vector3(startEast, 2f, doorOffset));
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("In front of the door, in VR");
        xr.GlobalBasis = Basis.Identity.Rotated(Vector3.Up, -Mathf.Pi / 2);
        await Seconds(0.2);
        left.SetInput("primary", new Vector2(0, 1));
        await Seconds(2.5);
        left.SetInput("primary", Vector2.Zero);
        await Seconds(0.3);
        var east = Player.Feet.X - goldshire.X;
        Check("VR walks into the inn", east > innWallEast && Player.OnGround, $"{east:F1} yards east of the center, wall at {innWallEast}");

        // Clicking in the menu isn't covered: the simulated pointer's ray doesn't reach it.
        left.SetInput("ax_button", true);
        left.SetInput("ax_button", false);
        await Seconds(0.2);
        var menu = xr.GetNode<Node3D>("LeftController/WristMenu");
        Check("X opens the menu", menu.Visible, "");
        left.SetInput("ax_button", true);
        left.SetInput("ax_button", false);
        await Seconds(0.2);
        Check("X closes the menu", !menu.Visible, "");

        XRServer.RemoveTracker(left);
        XRServer.RemoveTracker(right);
    }

    private static XRControllerTracker AddTracker(string name, XRPositionalTracker.TrackerHand hand)
    {
        var tracker = new XRControllerTracker { Name = name, Hand = hand };
        XRServer.AddTracker(tracker);
        return tracker;
    }

    /// <summary>A pose in real meters from the play space's origin, pitched in degrees.</summary>
    private static void SetPose(XRPositionalTracker tracker, Vector3 position, float pitch) =>
        tracker.SetPose(
            "default",
            new Transform3D(Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(pitch)), position),
            Vector3.Zero,
            Vector3.Zero,
            XRPose.TrackingConfidenceEnum.High);

    private async Task VignetteClosesWhenMoving()
    {
        if (Player is not Xr.XrPlayer xr)
        {
            return;
        }

        var saved = AppSettings.VignetteSize;
        var slider = Player.Menu.FindChild(WorldMenu.VignetteSliderName, true, false) as HSlider;
        Check("Menu has the vignette slider", slider is not null, "");

        slider!.Value = 0.25;
        await Seconds(0.8);
        Check("Changing the size previews it", xr.CurrentVignetteRadius < 0.75f, $"radius {xr.CurrentVignetteRadius:F2}");
        await Seconds(2.5);
        Check("Vignette opens when still", xr.CurrentVignetteRadius >= 1f, $"radius {xr.CurrentVignetteRadius:F2}");

        Player.PlaceAt(Player.Feet + Vector3.Up * 30f);
        await Seconds(0.8);
        var small = xr.CurrentVignetteRadius;
        await Seconds(2.5);

        slider.Value = 1;
        await Seconds(2.5);
        Player.PlaceAt(Player.Feet + Vector3.Up * 30f);
        await Seconds(0.8);
        var large = xr.CurrentVignetteRadius;
        Check("A bigger size closes further", large < small && small < 1f, $"radius {small:F2} at 0.25, {large:F2} at 1");
        await Seconds(2.5);

        slider.Value = 0;
        await Seconds(0.1);
        Player.PlaceAt(Player.Feet + Vector3.Up * 30f);
        await Seconds(0.8);
        Check("Size 0 turns it off", xr.CurrentVignetteRadius >= 1f, $"radius {xr.CurrentVignetteRadius:F2}");
        await Seconds(2.5);

        slider.Value = saved;
    }

    private const float MinTeleportMeters = 1.3f;

    private void ScaleButtonsWork()
    {
        var saved = AppSettings.Scale;
        PressMenuButton("True scale");
        Check("True scale button", AppSettings.Scale == PlayerScale.TrueScale, $"{AppSettings.Scale}");
        PressMenuButton("Player-sized");
        Check("Player-sized button", AppSettings.Scale == PlayerScale.PlayerSized, $"{AppSettings.Scale}");
        AppSettings.Scale = saved;
    }

    private async Task WalksAlongTheGround()
    {
        if (Player is not Desktop.DesktopPlayer desktop)
        {
            return;
        }

        var before = Player.Feet;
        desktop.ScriptedWalk = Vector3.Forward;
        await Seconds(5);
        desktop.ScriptedWalk = null;
        var walked = new Vector2(Player.Feet.X - before.X, Player.Feet.Z - before.Z).Length() / 5f;
        Check("Walks at walking speed", walked < MaxWalkedInASecond, $"{walked:F1} yards per second");
        await StandsOnTheGround("After walking 35 yards");

        await WalksIntoTheInn(desktop);
        await DoodadsBlockThePlayer(desktop);
    }

    /// <summary>Finds the inn's door with rays, so this doesn't depend on measured positions.</summary>
    private async Task WalksIntoTheInn(Desktop.DesktopPlayer desktop)
    {
        var center = WowCoords.ToGodot(new System.Numerics.Vector3(-9464f, 62f, 57f));
        var start = Tiles.WorldToLocal(center.X, center.Y, center.Z);
        desktop.PlaceAt(start);
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("Back at Goldshire's center");

        var height = Player.Feet.Y + 2f;
        // A solid wall, beside the door, stops the player.
        const float innWallEast = 23f, wallOffset = 8f, doorOffset = -4f, startEast = 12f;
        desktop.PlaceAt(start + new Vector3(startEast, 2f, wallOffset));
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("In front of the inn");
        desktop.ScriptedWalk = Vector3.Right;
        await Seconds(4);
        desktop.ScriptedWalk = null;
        Check("A wall stops the player", Player.Feet.X - start.X < innWallEast, $"{innWallEast - (Player.Feet.X - start.X):F1} yards short of the wall");

        // Through the door, measured with rays: a roof overhead and a floor underfoot.
        desktop.PlaceAt(start + new Vector3(startEast, 2f, doorOffset));
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("In front of the door");
        desktop.ScriptedWalk = Vector3.Right;
        await Seconds(2.5);
        desktop.ScriptedWalk = null;
        await Seconds(0.3);
        var feet = Player.Feet;
        var roof = Ray(feet + Vector3.Up * 3f, feet + Vector3.Up * 30f);
        var east = feet.X - start.X;
        Check("Walks into the inn", east > innWallEast && roof is not null && Player.OnGround, $"{east:F1} yards east of the center, wall at {innWallEast}, roof {(roof is { } r ? r.Y - feet.Y : -1):F1} above");
    }

    /// <summary>Walks at the tallest doodad nearby, which should stop the player before its middle.</summary>
    private async Task DoodadsBlockThePlayer(Desktop.DesktopPlayer desktop)
    {
        await Seconds(1.5);

        var bodies = GetTree().GetNodesInGroup(TileObjectsNode.DoodadCollisionGroup).OfType<StaticBody3D>().ToList();
        Check("Doodads near the player have collision", bodies.Count > 0, $"{bodies.Count} bodies");
        if (bodies.Count == 0)
        {
            return;
        }

        var doodad = bodies.MaxBy(b => b.GetChild<CollisionShape3D>(0).Shape is ConcavePolygonShape3D s ? s.Data.Max(p => p.Y) : 0f)!;
        var center = doodad.GlobalPosition;
        const float approach = 8f;
        var from = center + Vector3.Back * approach;

        desktop.PlaceAt(new Vector3(from.X, Player.Feet.Y + 5f, from.Z));
        Tiles.PlacePlayerOnSurface();
        await StandsOnTheGround("In front of a doodad");
        desktop.ScriptedWalk = Vector3.Forward;
        await Seconds(approach / AppSettings.WalkSpeed + 1.5);
        desktop.ScriptedWalk = null;

        var left = Player.Feet.Z - center.Z;
        Check("A doodad blocks the player", left > 0f, $"stopped {left:F1} yards short of its middle");
    }

    private async Task ReportFrameCost(string place)
    {
        await Seconds(3);
        var frames = Engine.GetFramesDrawn();
        var started = Time.GetTicksMsec();
        await Seconds(2);
        var milliseconds = (Time.GetTicksMsec() - started) / (double)Mathf.Max(Engine.GetFramesDrawn() - frames, 1);
        var draws = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
        GD.Print($"  {place}: {milliseconds:F1} ms per frame, {draws} draw calls");
    }

    private Vector3? Ray(Vector3 from, Vector3 to)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, to, TileManager.TerrainLayer);
        var hit = GetViewport().FindWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3() : null;
    }

    private bool InWater() => !Player.OnGround && Tiles.LiquidAt(Player.Feet + Vector3.Up * 2f) is { } liquid && liquid.Surface > Player.Feet.Y;

    private async Task StandsOnTheGround(string when)
    {
        var started = Time.GetTicksMsec();
        while ((Tiles.PlacingPlayer || (!Player.OnGround && !InWater())) && Time.GetTicksMsec() - started < SecondsToLoad * 1000)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }

        var waited = Time.GetTicksMsec() - started;
        await Seconds(0.5);

        var feet = Player.Feet;
        var ray = PhysicsRayQueryParameters3D.Create(
            new Vector3(feet.X, 6000, feet.Z), new Vector3(feet.X, -6000, feet.Z), TileManager.TerrainLayer);
        var hit = GetViewport().FindWorld3D().DirectSpaceState.IntersectRay(ray);

        // Some landmarks are over water, where the player floats rather than stands.
        if (InWater())
        {
            Check(when, true, $"floating over deep water, settled in {waited} ms");
            return;
        }

        var gap = hit.Count > 0 ? feet.Y - hit["position"].AsVector3().Y : float.NaN;
        Check(when, Player.OnGround && Mathf.Abs(gap) < StandTolerance, $"{gap:F2} above the terrain, settled in {waited} ms");
    }

    private void PressMenuButton(string text)
    {
        var button = Player.Menu.FindChildren("*", nameof(Button), true, false)
            .OfType<Button>()
            .FirstOrDefault(b => b.Text == text);

        if (button is null)
        {
            Check($"Menu has {text}", false, "no such button");
            return;
        }

        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void Check(string name, bool passed, string detail)
    {
        GD.Print($"{(passed ? "PASS" : "FAIL")} {name}: {detail}");
        _failures += passed ? 0 : 1;
    }

    private async Task Seconds(double seconds) =>
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
