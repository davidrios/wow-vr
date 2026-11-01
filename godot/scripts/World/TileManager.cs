using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Assets;
using WowVR.Formats.Terrain;
using Godot;

namespace WowVR.World;

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

    private readonly Dictionary<(int X, int Y), MeshInstance3D?> _tiles = [];
    private readonly ConcurrentQueue<((int X, int Y) Tile, ArrayMesh? Mesh)> _built = new();

    private IAssetSource _source = null!;
    private WdtMap _map = null!;
    private TerrainBuilder _builder = null!;
    private Node3D _player = null!;
    private double _originX;
    private double _originZ;
    private bool _building;
    private bool _checkerboard = Array.IndexOf(OS.GetCmdlineUserArgs(), "--checkerboard") >= 0;
    private bool _swapVertexColors = Array.IndexOf(OS.GetCmdlineUserArgs(), "--swap-vertex-colors") >= 0;

    public int LoadedTileCount => _tiles.Values.Count(t => t is not null);

    public bool IsBuilding => _building;

    /// <summary>Starts streaming with the given Godot world position at the local origin.</summary>
    public void Start(IAssetSource source, WdtMap map, Node3D player, double originX, double originZ)
    {
        _source = source;
        _map = map;
        _player = player;
        _originX = originX;
        _originZ = originZ;
        _builder = new TerrainBuilder(source, map);
        TerrainBuilder.LoadShared();
    }

    /// <summary>A Godot world position in this scene's local space.</summary>
    public Vector3 WorldToLocal(double x, double y, double z) =>
        new((float)(x - _originX), (float)y, (float)(z - _originZ));

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

        CommitOneBuiltTile();
        UnloadFarTiles(center);
        StartNextBuild(center);
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
        _player.GlobalPosition -= shift;

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
        if (built.Mesh is null || !_tiles.ContainsKey(built.Tile))
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
        _tiles[built.Tile] = node;
    }

    private void UnloadFarTiles((int X, int Y) center)
    {
        foreach (var tile in _tiles.Keys.Where(t => Distance(t, center) > UnloadRadius).ToList())
        {
            _tiles[tile]?.QueueFree();
            _tiles.Remove(tile);
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
                double dx = x - center.X, dy = y - center.Y;
                var distance = dx * dx + dy * dy;
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
        try
        {
            var stopwatch = Stopwatch.StartNew();
            mesh = _builder.Build(tile.X, tile.Y);
            GD.Print($"Tile {tile.X}_{tile.Y} built in {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            GD.PushError($"Could not build tile {tile.X}_{tile.Y}: {e.Message}");
        }

        _built.Enqueue((tile, mesh));
    }

    private static int Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public override void _ExitTree() => _source?.Dispose();
}
