using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.World;
using WowVR.Formats.Assets;
using WowVR.Formats.Terrain;
using Godot;

using WowVR.Debug;

namespace WowVR.World;

public enum GroundState
{
    /// <summary>There will be ground here shortly.</summary>
    Loading,

    /// <summary>The ground here has collision.</summary>
    Ready,

    /// <summary>There's no tile here, or it couldn't be read.</summary>
    NoTerrain,
}

/// <summary>
/// Streams terrain tiles around the camera and keeps the world near Godot's origin.
/// Tiles build on a background thread; only adding and removing nodes happens here.
/// </summary>
public partial class TileManager : Node3D
{
    /// <summary>Tiles kept in each direction: 2 gives a 5x5 window.</summary>
    private const int TileRadius = 2;

    /// <summary>Tiles are kept one step past the window, so crossing back and forth doesn't reload.</summary>
    private const int UnloadRadius = TileRadius + 1;

    /// <summary>How far the camera may go before the world re-centers, in yards.</summary>
    private const float RecenterDistance = 512f;

    /// <summary>Doodad batches and WMO groups to add per frame, across all tiles.</summary>
    private const int MaxObjectItemsPerFrame = 8;

    /// <summary>The physics layer terrain is on.</summary>
    public const uint TerrainLayer = 1;

    private readonly Dictionary<(int X, int Y), MeshInstance3D?> _tiles = [];
    private readonly HashSet<(int X, int Y)> _failedTiles = [];
    private readonly ConcurrentQueue<((int X, int Y) Tile, ArrayMesh? Mesh, TerrainCollisionData? Collision, AdtTerrain? Terrain, ArrayMesh? Liquid)> _built = new();
    private readonly ConcurrentQueue<((int X, int Y) Tile, TileObjects? Objects)> _builtObjects = new();

    /// <summary>Parsed tiles waiting for their models to load.</summary>
    private readonly Dictionary<(int X, int Y), AdtTerrain> _objectsToBuild = [];

    private readonly Dictionary<(int X, int Y), TileObjectsNode> _objects = [];
    private readonly Dictionary<(int X, int Y), TerrainHeights> _heights = [];

    /// <summary>A building's water counts only this far under its surface, in yards, or a canal would reach the cellar below.</summary>
    private const float WmoLiquidReach = 3f;

    /// <summary>How far under the ground a point can be and still count as in the water over it.</summary>
    private const float GroundAllowance = 1f;

    /// <summary>Each loaded tile's area ids, by chunk.</summary>
    private readonly Dictionary<(int X, int Y), int[]> _areas = [];

    private IAssetSource _source = null!;
    private WdtMap _map = null!;
    private TerrainBuilder _builder = null!;
    private TileObjectsBuilder _objectsBuilder = null!;
    private LiquidLibrary _liquids = null!;
    private bool _buildingObjects;
    private IPlayer _player = null!;
    private TerrainCollision _collision = null!;
    private GroundGuard _guard = null!;
    private double _originX;
    private double _originZ;
    private bool _building;
    private double _materialClock;
    private bool _checkerboard = Array.IndexOf(OS.GetCmdlineUserArgs(), "--checkerboard") >= 0;
    private bool _swapVertexColors = Array.IndexOf(OS.GetCmdlineUserArgs(), "--swap-vertex-colors") >= 0;

    /// <summary>A tile's terrain joined the scene, and things can be added to its node.</summary>
    public event Action<(int X, int Y), Node3D, TerrainHeights>? TileAdded;

    /// <summary>A tile is leaving, along with its node's children.</summary>
    public event Action<(int X, int Y)>? TileRemoved;

    public ModelLibrary Models { get; private set; } = null!;

    public IPlayer Player => _player;

    public int LoadedTileCount => _tiles.Values.Count(t => t is not null);

    public bool IsBuilding => _building;

    /// <summary>Anything still loading or waiting to join the scene.</summary>
    public bool IsBusy => _building || _buildingObjects || !_built.IsEmpty || !_builtObjects.IsEmpty
        || _objectsToBuild.Count > 0 || _objects.Values.Any(o => !o.Finished);

    /// <summary>The player's feet in Godot's axes, relative to the middle of the map.</summary>
    public (double X, double Y, double Z) PlayerWorldPosition
    {
        get
        {
            var feet = _player.Feet;
            return (feet.X + _originX, feet.Y, feet.Z + _originZ);
        }
    }

    /// <summary>The area under the player, or null where no tile has loaded.</summary>
    public int? AreaAtPlayer()
    {
        var chunk = ChunkAt(_player.Feet);
        var tile = (X: chunk.X / WowCoords.ChunksPerTile, Y: chunk.Z / WowCoords.ChunksPerTile);
        if (chunk.X < 0 || chunk.Z < 0 || !_areas.TryGetValue(tile, out var areas))
        {
            return null;
        }

        return areas[chunk.Z % WowCoords.ChunksPerTile * WowCoords.ChunksPerTile + chunk.X % WowCoords.ChunksPerTile];
    }

    /// <summary>Starts streaming with the given Godot world position at the local origin.</summary>
    public void Start(IAssetSource source, WdtMap map, LiquidLibrary liquids, IPlayer player, double originX, double originZ, IGameObjectSource gameObjects)
    {
        _collision = new TerrainCollision { Name = "Collision" };
        AddChild(_collision);
        _guard = new GroundGuard { Name = "GroundGuard", Tiles = this, Player = player };
        AddChild(_guard);

        _source = source;
        _map = map;
        _player = player;
        _originX = originX;
        _originZ = originZ;
        _builder = new TerrainBuilder(source, map);
        _liquids = liquids;
        Models = new ModelLibrary(source, liquids);
        _objectsBuilder = new TileObjectsBuilder(Models, map, gameObjects, GameObjectDisplayTable.Read(source));
        TerrainBuilder.LoadShared();
    }

    /// <summary>A Godot world position in this scene's local space.</summary>
    public Vector3 WorldToLocal(double x, double y, double z) =>
        new((float)(x - _originX), (float)y, (float)(z - _originZ));

    /// <summary>Whether the player can stand at a position in this scene's local space.</summary>
    public GroundState GroundAt(Vector3 local)
    {
        var chunk = ChunkAt(local);
        var tile = (X: chunk.X / WowCoords.ChunksPerTile, Y: chunk.Z / WowCoords.ChunksPerTile);

        var onMap = chunk.X >= 0 && chunk.Z >= 0 && tile.X < WowCoords.TilesPerSide && tile.Y < WowCoords.TilesPerSide;
        if (!onMap || !_map.HasTile(tile.X, tile.Y) || _failedTiles.Contains(tile))
        {
            return GroundState.NoTerrain;
        }

        // A tile's buildings can have floors above its terrain, so they count as ground.
        var objectsDone = _objects.TryGetValue(tile, out var objects) && objects.Finished;
        return objectsDone && _collision.HasChunk(chunk) ? GroundState.Ready : GroundState.Loading;
    }

    /// <summary>Jumps to a landmark, moving the origin there so nothing is ever far from zero.</summary>
    public void TeleportTo(float wowX, float wowY)
    {
        var target = WowCoords.ToGodot(new System.Numerics.Vector3(wowX, wowY, 0));
        _originX = target.X;
        _originZ = target.Z;
        RepositionTiles();

        _player.PlaceAt(new Vector3(0, _player.Feet.Y, 0));
        _guard.PlaceOnSurfaceWhenLoaded();
    }

    public bool PlacingPlayer => _guard.Placing;

    /// <summary>
    /// The liquid a point in the scene is in or under, as its surface's height and LiquidType
    /// row. A building's own water counts first. A terrain lake doesn't reach a point below
    /// the ground, like a cave under it, or inside a building.
    /// </summary>
    public (float Surface, int Type)? LiquidAt(Vector3 point)
    {
        (float Surface, int Type)? best = null;
        foreach (var objects in _objects.Values)
        {
            if (objects.WmoLiquidAt(point) is { } wmo && wmo.Surface >= point.Y - WmoLiquidReach && (best is null || wmo.Surface > best.Value.Surface))
            {
                best = wmo;
            }
        }

        if (best is not null)
        {
            return best;
        }

        if (TerrainLiquidAt(point) is not { } terrain || terrain.Liquid is not { } liquid || point.Y < terrain.Ground - GroundAllowance || terrain.Ground >= liquid.Surface)
        {
            return null;
        }

        return _objects.Values.Any(o => o.Indoors(point)) ? null : liquid;
    }

    /// <summary>The deepest a terrain liquid is over a point, whatever the point's height.</summary>
    public (float Surface, float Depth)? DeepestLiquidAt(Vector3 point) =>
        TerrainLiquidAt(point) is { Liquid: { } liquid } terrain ? (liquid.Surface, liquid.Surface - terrain.Ground) : null;

    private (float Ground, (float Surface, int Type)? Liquid)? TerrainLiquidAt(Vector3 point)
    {
        var chunk = ChunkAt(point);
        var tile = (X: chunk.X / WowCoords.ChunksPerTile, Y: chunk.Z / WowCoords.ChunksPerTile);
        if (chunk.X < 0 || chunk.Z < 0 || !_heights.TryGetValue(tile, out var heights))
        {
            return null;
        }

        var wow = WowCoords.ToWow(new System.Numerics.Vector3((float)(point.X + _originX), point.Y, (float)(point.Z + _originZ)));
        return (heights.HeightAt(wow.X, wow.Y), heights.LiquidAt(wow.X, wow.Y));
    }

    /// <summary>
    /// Every tile's collision has caught up with the player. Any tile, since a WMO belongs to
    /// the tile of its origin, which for a city can be a long way from where it's stood on.
    /// </summary>
    public bool CollisionSettled => _objects.Values.All(o => o.CollisionSettled);

    /// <summary>Waits for the ground, then stands on it.</summary>
    public void PlacePlayerOnSurface() => _guard.PlaceOnSurfaceWhenLoaded();

    private (int X, int Z) ChunkAt(Vector3 local) =>
        WowCoords.ChunkAtGodot(local.X + _originX, local.Z + _originZ);

    public override void _Process(double delta)
    {
        if (_builder is null)
        {
            return;
        }

        var camera = GetViewport().GetCamera3D();
        if (camera is null)
        {
            return;
        }

        RecenterIfNeeded(camera.GlobalPosition);

        var position = camera.GlobalPosition;
        var wow = WowCoords.ToWow(new System.Numerics.Vector3(
            (float)(position.X + _originX), position.Y, (float)(position.Z + _originZ)));
        var center = WowCoords.TileAt(wow.X, wow.Y);

        _materialClock += delta;
        using (FrameProbe.Time("materials"))
        {
            Models.AnimateMaterials(_materialClock);
        }

        using (FrameProbe.Time("commit tile"))
        {
            CommitOneBuiltTile();
        }

        using (FrameProbe.Time("commit objects"))
        {
            CommitBuiltObjects();
        }

        using (FrameProbe.Time("add objects"))
        {
            AddNearestObjects();
        }

        using (FrameProbe.Time("unload and start builds"))
        {
            UnloadFarTiles(center);
            StartNextBuild(center);
            StartNextObjectsBuild(center);
        }

        using (FrameProbe.Time("terrain collision"))
        {
            _collision.Update(ChunkAt(_player.Feet));
        }
    }

    private void ApplyDebugView(MeshInstance3D tile)
    {
        var material = (ShaderMaterial)tile.Mesh.SurfaceGetMaterial(0);
        material.SetShaderParameter("checkerboard", _checkerboard);
        material.SetShaderParameter("swap_vertex_colors", _swapVertexColors);
    }

    private void ApplyDebugViewToAllTiles()
    {
        foreach (var tile in _tiles.Values)
        {
            if (tile is not null)
            {
                ApplyDebugView(tile);
            }
        }
    }


    /// <summary>F2 prints what's under the camera, to pin down rendering problems.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (_builder is null || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (key.PhysicalKeycode == Key.F3)
        {
            _checkerboard = !_checkerboard;
            ApplyDebugViewToAllTiles();
            return;
        }

        if (key.PhysicalKeycode == Key.F4)
        {
            _swapVertexColors = !_swapVertexColors;
            ApplyDebugViewToAllTiles();
            GD.Print($"Vertex colors: {(_swapVertexColors ? "file order RGBA" : "file order BGRA")}");
            return;
        }

        if (key.PhysicalKeycode != Key.F2)
        {
            return;
        }

        var position = GetViewport().GetCamera3D().GlobalPosition;
        var (wowX, wowY) = (-(position.Z + _originZ), -(position.X + _originX));
        Task.Run(() => PrintLocation(wowX, wowY, position.Y));
    }

    private void PrintLocation(double wowX, double wowY, float wowZ)
    {
        try
        {
            var tile = WowCoords.TileAt(wowX, wowY);
            var ix = (int)((WowCoords.MapOrigin - wowY - tile.X * WowCoords.TileSize) / WowCoords.ChunkSize);
            var iy = (int)((WowCoords.MapOrigin - wowX - tile.Y * WowCoords.TileSize) / WowCoords.ChunkSize);
            var terrain = AdtTerrain.Parse(_source.Read(_map.TilePath(tile.X, tile.Y))!, _map.BigAlpha);
            var chunk = terrain.Chunks[iy * AdtTerrain.ChunksPerSide + ix];

            GD.Print($"--start={_map.Directory},{wowX:F0},{wowY:F0},{wowZ:F0} tile {tile.X}_{tile.Y} chunk ({ix},{iy})");
            for (var layer = 0; layer < chunk.LayerCount; layer++)
            {
                GD.Print($"  layer {layer}: {terrain.TextureNames[chunk.TextureIds[layer]]}");
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Could not read the location: {e.Message}");
        }
    }

    private void RecenterIfNeeded(Vector3 camera)
    {
        if (new Vector2(camera.X, camera.Z).Length() < RecenterDistance)
        {
            return;
        }

        var shift = new Vector3(Mathf.Round(camera.X), 0, Mathf.Round(camera.Z));
        _originX += shift.X;
        _originZ += shift.Z;
        _player.PlaceAt(_player.Feet - shift, keepVelocity: true);
        _guard.OnRecentered(shift);
        RepositionTiles();
    }

    private void RepositionTiles()
    {
        foreach (var (tile, node) in _tiles)
        {
            if (node is not null)
            {
                node.Position = TilePosition(tile);
            }
        }
    }

    private Vector3 TilePosition((int X, int Y) tile)
    {
        var (x, z) = WowCoords.TileOriginGodot(tile.X, tile.Y);
        return new Vector3((float)(x - _originX), 0, (float)(z - _originZ));
    }

    /// <summary>One per frame, so a burst of finished tiles can't cause a hitch.</summary>
    private void CommitOneBuiltTile()
    {
        if (!_built.TryDequeue(out var built))
        {
            return;
        }

        _building = false;

        // A failed tile stays in the dictionary so it isn't retried every frame.
        if (built.Mesh is null)
        {
            _failedTiles.Add(built.Tile);
        }

        if (built.Mesh is null || built.Collision is null || built.Terrain is null || !_tiles.ContainsKey(built.Tile))
        {
            return;
        }

        var node = new MeshInstance3D
        {
            Name = $"Tile_{built.Tile.X}_{built.Tile.Y}",
            Mesh = built.Mesh,
            Position = TilePosition(built.Tile),
        };
        ApplyDebugView(node);
        AddChild(node);
        if (built.Liquid is not null)
        {
            node.AddChild(new MeshInstance3D
            {
                Name = "Liquid",
                Mesh = built.Liquid,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            });
        }

        _areas[built.Tile] = built.Terrain.Chunks.Select(c => c.AreaId).ToArray();
        _tiles[built.Tile] = node;
        _collision.AddTile(built.Tile, node, built.Collision);
        _objectsToBuild[built.Tile] = built.Terrain!;
        var heights = new TerrainHeights(built.Terrain, built.Tile.X, built.Tile.Y);
        _heights[built.Tile] = heights;
        TileAdded?.Invoke(built.Tile, node, heights);
    }

    private void UnloadFarTiles((int X, int Y) center)
    {
        foreach (var tile in _tiles.Keys.Where(t => Distance(t, center) > UnloadRadius).ToList())
        {
            TileRemoved?.Invoke(tile);
            _collision.RemoveTile(tile);
            _objectsToBuild.Remove(tile);
            _objects.Remove(tile);
            _heights.Remove(tile);
            _areas.Remove(tile);
            _tiles[tile]?.QueueFree();
            _tiles.Remove(tile);
            _failedTiles.Remove(tile);
        }
    }

    private void StartNextBuild((int X, int Y) center)
    {
        if (_building)
        {
            return;
        }

        (int X, int Y)? next = null;
        var best = double.MaxValue;
        for (var y = center.Y - TileRadius; y <= center.Y + TileRadius; y++)
        {
            for (var x = center.X - TileRadius; x <= center.X + TileRadius; x++)
            {
                var distance = TileDistanceFromPlayer((x, y));
                if (distance < best && _map.HasTile(x, y) && !_tiles.ContainsKey((x, y)))
                {
                    next = (x, y);
                    best = distance;
                }
            }
        }

        if (next is not { } tile)
        {
            return;
        }

        _tiles[tile] = null;
        _building = true;
        Task.Run(() => Build(tile));
    }

    private void Build((int X, int Y) tile)
    {
        ArrayMesh? mesh = null;
        TerrainCollisionData? collision = null;
        AdtTerrain? terrain = null;
        ArrayMesh? liquid = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            (mesh, collision, terrain) = _builder.Build(tile.X, tile.Y);
            liquid = _liquids.BuildMesh(LiquidSurface.FromTile(terrain.Liquids));
            GD.Print($"Tile {tile.X}_{tile.Y} built in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            GD.PushError($"Could not build tile {tile.X}_{tile.Y}: {e.Message}");
        }

        _built.Enqueue((tile, mesh, collision, terrain, liquid));
    }

    /// <summary>Models load one tile at a time, nearest first, without holding up terrain.</summary>
    private void StartNextObjectsBuild((int X, int Y) center)
    {
        if (_buildingObjects || _objectsToBuild.Count == 0)
        {
            return;
        }

        var tile = _objectsToBuild.Keys.MinBy(TileDistanceFromPlayer);
        var terrain = _objectsToBuild[tile];
        _objectsToBuild.Remove(tile);
        _buildingObjects = true;
        Task.Run(() => BuildObjects(tile, terrain));
    }

    private void BuildObjects((int X, int Y) tile, AdtTerrain terrain)
    {
        TileObjects? objects = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            objects = _objectsBuilder.Build(terrain, tile.X, tile.Y);
            GD.Print($"Tile {tile.X}_{tile.Y} objects built in {stopwatch.ElapsedMilliseconds} ms: {objects.Doodads.Count} doodad batches, {objects.Wmos.Count} WMOs");
        }
        catch (Exception e)
        {
            GD.PushError($"Could not build the objects of tile {tile.X}_{tile.Y}: {e.Message}");
        }

        _builtObjects.Enqueue((tile, objects));
    }

    /// <summary>Whatever is closest goes first, whichever tile it's on, so the world fills in as a circle.</summary>
    private void AddNearestObjects()
    {
        var feet = _player.Feet;
        for (var added = 0; added < MaxObjectItemsPerFrame; added++)
        {
            TileObjectsNode? nearest = null;
            var best = float.PositiveInfinity;
            foreach (var objects in _objects.Values)
            {
                var distance = objects.NextItemDistance(feet);
                if (distance < best)
                {
                    (nearest, best) = (objects, distance);
                }
            }

            if (nearest is null)
            {
                return;
            }

            nearest.AddNextItem();
        }
    }

    /// <summary>From the player to the tile's middle, so tiles load in order of real distance.</summary>
    private double TileDistanceFromPlayer((int X, int Y) tile)
    {
        var (x, z) = WowCoords.TileOriginGodot(tile.X, tile.Y);
        var feet = _player.Feet;
        var dx = x + WowCoords.TileSize / 2 - (feet.X + _originX);
        var dz = z + WowCoords.TileSize / 2 - (feet.Z + _originZ);
        return dx * dx + dz * dz;
    }

    private void CommitBuiltObjects()
    {
        if (!_builtObjects.TryDequeue(out var built))
        {
            return;
        }

        _buildingObjects = false;

        if (!_tiles.TryGetValue(built.Tile, out var node) || node is null)
        {
            return;
        }

        var objects = new TileObjectsNode { Name = "Objects", Player = _player };
        objects.Fill(built.Objects ?? new TileObjects([], []));
        node.AddChild(objects);
        _objects[built.Tile] = objects;
    }

    private static int Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

}
