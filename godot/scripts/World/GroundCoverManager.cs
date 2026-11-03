using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Debug;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// Grass, flowers and small stones. A whole tile could hold over 100,000 of them, so only
/// chunks near the player get any: they're made off the main thread and dropped behind it.
/// </summary>
public partial class GroundCoverManager : Node
{
    /// <summary>In yards from a chunk's middle, across the ground.</summary>
    private const float LoadRange = 95f;
    private const float KeepRange = 120f;

    /// <summary>Hidden past this, measured to a batch's middle, and faded over the margin.</summary>
    private const float VisibleRange = 75f;
    private const float FadeMargin = 20f;

    private const double CheckSeconds = 0.25;
    private const int MaxChunksAddedPerFrame = 1;
    private const int MaxBuilding = 2;

    private sealed record TileState((int X, int Y) Tile, Node3D Node, TerrainHeights Heights, Node3D Container);

    private sealed record Built((int X, int Y) Tile, int Ix, int Iy, List<MultiMesh> Batches);

    private readonly Dictionary<(int X, int Y), TileState> _tiles = [];

    /// <summary>Null while a chunk is being made.</summary>
    private readonly Dictionary<((int X, int Y) Tile, int Ix, int Iy), Node3D?> _chunks = [];
    private readonly ConcurrentQueue<Built> _built = new();
    private GroundEffectTable _table = null!;
    private TileManager _world = null!;
    private double _timer;
    private int _building;
    private bool _enabled;
    private bool _underwater;

    /// <summary>
    /// Fading draws in the transparent pass, after the underwater view has read the screen, so
    /// a fading blade comes out dark against the fog. Fog hides anything that far away under
    /// water anyway, so there it can just stop.
    /// </summary>
    public bool Underwater
    {
        set
        {
            if (value == _underwater)
            {
                return;
            }

            _underwater = value;
            foreach (var node in _chunks.Values)
            {
                if (node is not null)
                {
                    foreach (var batch in node.GetChildren().OfType<MultiMeshInstance3D>())
                    {
                        batch.VisibilityRangeFadeMode = FadeMode;
                    }
                }
            }
        }
    }

    private GeometryInstance3D.VisibilityRangeFadeModeEnum FadeMode => _underwater
        ? GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled
        : GeometryInstance3D.VisibilityRangeFadeModeEnum.Self;

    public void Start(GroundEffectTable table, TileManager world)
    {
        _table = table;
        _world = world;
        _enabled = AppSettings.GroundCover;
        world.TileAdded += (tile, node, heights) =>
        {
            var container = new Node3D { Name = "GroundCover", Visible = _enabled };
            node.AddChild(container);
            _tiles[tile] = new TileState(tile, node, heights, container);
        };
        world.TileRemoved += tile =>
        {
            // Their nodes leave with the tile's.
            _tiles.Remove(tile);
            foreach (var key in _chunks.Keys.Where(k => k.Tile == tile).ToList())
            {
                _chunks.Remove(key);
            }
        };
        AppSettings.Changed += ApplySettings;
    }

    public override void _ExitTree() => AppSettings.Changed -= ApplySettings;

    private void ApplySettings()
    {
        _enabled = AppSettings.GroundCover;
        foreach (var state in _tiles.Values)
        {
            state.Container.Visible = _enabled;
        }
    }

    public override void _Process(double delta)
    {
        if (_world is null)
        {
            return;
        }

        using var probe = FrameProbe.Time("ground cover");
        for (var added = 0; added < MaxChunksAddedPerFrame && _built.TryDequeue(out var built); added++)
        {
            _building--;
            Add(built);
        }

        _timer -= delta;
        if (_timer <= 0 && _enabled)
        {
            _timer = CheckSeconds;
            Update();
        }
    }

    private static float GroundDistance(TileState state, int ix, int iy, Vector3 feet)
    {
        var half = WowCoords.ChunkSize / 2;
        var wowX = WowCoords.MapOrigin - state.Tile.Y * WowCoords.TileSize - iy * WowCoords.ChunkSize - half;
        var wowY = WowCoords.MapOrigin - state.Tile.X * WowCoords.TileSize - ix * WowCoords.ChunkSize - half;
        var local = WowCoords.TileLocal(wowX, wowY, 0f, state.Tile.X, state.Tile.Y);
        var position = state.Node.Position;
        return new Vector2(position.X + local.X - feet.X, position.Z + local.Z - feet.Z).Length();
    }

    private void Update()
    {
        var feet = _world.Player.Feet;
        foreach (var (key, node) in _chunks.Where(c => c.Value is not null).ToList())
        {
            if (GroundDistance(_tiles[key.Tile], key.Ix, key.Iy, feet) > KeepRange)
            {
                node!.QueueFree();
                _chunks.Remove(key);
            }
        }

        var wanted = new List<(float Distance, TileState State, int Ix, int Iy)>();
        foreach (var state in _tiles.Values)
        {
            for (var iy = 0; iy < AdtTerrain.ChunksPerSide; iy++)
            {
                for (var ix = 0; ix < AdtTerrain.ChunksPerSide; ix++)
                {
                    var distance = GroundDistance(state, ix, iy, feet);
                    if (distance < LoadRange && !_chunks.ContainsKey((state.Tile, ix, iy)))
                    {
                        wanted.Add((distance, state, ix, iy));
                    }
                }
            }
        }

        var models = _world.Models;
        foreach (var (_, state, ix, iy) in wanted.OrderBy(w => w.Distance))
        {
            if (_building >= MaxBuilding)
            {
                break;
            }

            _building++;
            _chunks[(state.Tile, ix, iy)] = null;
            Task.Run(() => _built.Enqueue(Build(state, ix, iy, models)));
        }
    }

    /// <summary>Off the main thread.</summary>
    private Built Build(TileState state, int ix, int iy, ModelLibrary models)
    {
        var batches = new List<MultiMesh>();
        try
        {
            foreach (var group in GroundCover.PlaceChunk(state.Heights, _table, state.Tile.X, state.Tile.Y, ix, iy).GroupBy(i => i.Model))
            {
                if (models.GetM2(group.Key) is not { } model)
                {
                    continue;
                }

                var items = group.ToList();
                var buffer = new float[items.Count * 12];
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var local = WowCoords.TileLocal(item.WowX, item.WowY, item.WowZ, state.Tile.X, state.Tile.Y);
                    var (sin, cos) = MathF.SinCos(item.Yaw);
                    var row = i * 12;
                    (buffer[row], buffer[row + 2], buffer[row + 3]) = (cos, sin, local.X);
                    (buffer[row + 5], buffer[row + 7]) = (1f, local.Y);
                    (buffer[row + 8], buffer[row + 10], buffer[row + 11]) = (-sin, cos, local.Z);
                }

                batches.Add(new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    Mesh = model.Mesh,
                    InstanceCount = items.Count,
                    Buffer = buffer,
                });
            }
        }
        catch (Exception e)
        {
            GD.PushError($"Could not place ground cover: {e.Message}");
        }

        return new Built(state.Tile, ix, iy, batches);
    }

    private void Add(Built built)
    {
        var key = (built.Tile, built.Ix, built.Iy);
        if (!_tiles.TryGetValue(built.Tile, out var state) || !_chunks.ContainsKey(key))
        {
            return;
        }

        var chunk = new Node3D();
        foreach (var mesh in built.Batches)
        {
            chunk.AddChild(new MultiMeshInstance3D
            {
                Multimesh = mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = TileManager.OutsideLayer,
                VisibilityRangeEnd = VisibleRange,
                VisibilityRangeEndMargin = FadeMargin,
                VisibilityRangeFadeMode = FadeMode,
            });
        }

        state.Container.AddChild(chunk);
        _chunks[key] = chunk;
    }
}
