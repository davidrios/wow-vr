using Godot;

namespace WowVR.World;

/// <summary>
/// Keeps the player on the world while it streams in: holds a walking player still until the
/// ground has loaded, sets them down after a teleport, and recovers them if they end up with
/// nothing underfoot, such as a hole in the terrain whose building hasn't been added yet.
/// </summary>
public partial class GroundGuard : Node
{
    private const float RayHeight = 6000f;
    private const float StandClearance = 0.1f;

    /// <summary>How far above a remembered height to look for its floor, in yards: about a head's height, so a ceiling isn't taken for it.</summary>
    private const float FloorSearchAbove = 1.5f;

    /// <summary>New bodies take a physics step or two to be found by rays.</summary>
    private const int TicksBeforeActing = 5;

    public required TileManager Tiles { get; init; }
    public required IPlayer Player { get; init; }

    private Vector3? _lastGround;
    private bool _placePending;
    private float? _placeFrom;
    private int _readyTicks;
    private int _noFloorTicks;

    /// <summary>A placement is still waiting for the ground to load.</summary>
    public bool Placing => _placePending;

    /// <summary>
    /// Puts the player on the surface once it has loaded: the highest one, or with
    /// <paramref name="nearHeight"/>, the floor at about that height, which can be inside a building.
    /// </summary>
    public void PlaceOnSurfaceWhenLoaded(float? nearHeight = null)
    {
        _placePending = true;
        _placeFrom = nearHeight;
        _readyTicks = 0;
        _lastGround = null;
    }

    /// <summary>The world moved under the player, so the remembered position moves too.</summary>
    public void OnRecentered(Vector3 shift) => _lastGround -= shift;

    public override void _PhysicsProcess(double delta)
    {
        var feet = Player.Feet;
        var ground = Tiles.GroundAt(feet);

        if (_placePending)
        {
            PlacePending(feet, ground);
            return;
        }

        Player.Frozen = ground == GroundState.Loading;

        if (ground == GroundState.Loading)
        {
            _noFloorTicks = 0;
            return;
        }

        if (RayDown(feet + Vector3.Up, feet.Y - RayHeight) is not null)
        {
            _noFloorTicks = 0;
            if (Player.OnGround && !Player.Flying)
            {
                _lastGround = feet;
            }

            return;
        }

        // Standing on the rim of a hole, with the middle of the body over it, is fine.
        if (Player.OnGround)
        {
            _noFloorTicks = 0;
            return;
        }

        if (++_noFloorTicks >= TicksBeforeActing)
        {
            _noFloorTicks = 0;
            Recover(feet);
        }
    }

    private void PlacePending(Vector3 feet, GroundState ground)
    {
        Player.Frozen = true;

        // Too early, and the surface found is the terrain under a city's floor.
        _readyTicks = ground == GroundState.Ready && Tiles.CollisionSettled ? _readyTicks + 1 : 0;

        if (ground == GroundState.NoTerrain)
        {
            GD.PushWarning("No terrain at the destination, so flying instead.");
            Player.Flying = true;
            _placePending = false;
        }
        else if (_readyTicks >= TicksBeforeActing)
        {
            if ((_placeFrom is { } height ? FloorBelow(feet, height) ?? Surface(feet) : Surface(feet)) is { } surface)
            {
                Player.PlaceAt(surface);
            }

            _placePending = false;
        }
    }

    private void Recover(Vector3 feet)
    {
        if (Surface(feet) is { } surface)
        {
            // Under the terrain, which can happen when flying in before it loads.
            GD.Print("Under the terrain: moving up to the surface.");
            Player.PlaceAt(surface);
        }
        else if (!Player.Flying)
        {
            // Nothing here at all, though flying over that is fine.
            if (_lastGround is { } last)
            {
                GD.Print("Nothing underfoot: going back to the last solid ground.");
                Player.PlaceAt(last);
            }
            else
            {
                GD.Print("Nothing underfoot and nowhere to go back to, so flying instead.");
                Player.Flying = true;
            }
        }
    }

    private Vector3? FloorBelow(Vector3 near, float height) =>
        RayDown(new Vector3(near.X, height + FloorSearchAbove, near.Z), -RayHeight) is { } hit ? hit + Vector3.Up * StandClearance : null;

    private Vector3? Surface(Vector3 near) =>
        RayDown(new Vector3(near.X, RayHeight, near.Z), -RayHeight) is { } hit ? hit + Vector3.Up * StandClearance : null;

    private RayCast3D? _ray;

    /// <summary>
    /// Through one ray node, cast by hand: a space state query makes several objects each call,
    /// and this runs every physics tick, so the garbage made the collector pause now and then.
    /// </summary>
    private Vector3? RayDown(Vector3 from, float toY)
    {
        if (_ray is null)
        {
            _ray = new RayCast3D { TopLevel = true, Enabled = false, CollisionMask = TileManager.TerrainLayer };
            AddChild(_ray);
        }

        _ray.GlobalPosition = from;
        _ray.TargetPosition = new Vector3(0, toY - from.Y, 0);
        _ray.ForceRaycastUpdate();
        return _ray.IsColliding() ? _ray.GetCollisionPoint() : null;
    }
}
