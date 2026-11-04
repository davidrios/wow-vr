using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Godot;

using WowVR.Debug;

namespace WowVR.World;

/// <summary>Where a tile's creatures come from.</summary>
public interface ISpawnSource
{
    IReadOnlyList<CreatureSpawn> Read(int tileX, int tileY, TerrainHeights heights);
}

/// <summary>No creatures from files, as when a server sends them instead.</summary>
public sealed class NoSpawns : ISpawnSource
{
    public IReadOnlyList<CreatureSpawn> Read(int tileX, int tileY, TerrainHeights heights) => [];
}

/// <summary>The files tools/spawn-exporter writes. A tile without one simply has no creatures.</summary>
public sealed class SpawnFiles(string folder, int mapId) : ISpawnSource
{
    public IReadOnlyList<CreatureSpawn> Read(int tileX, int tileY, TerrainHeights heights)
    {
        var path = CreatureSpawn.FilePath(folder, mapId, tileX, tileY);
        return File.Exists(path) ? CreatureSpawn.ParseFile(File.ReadAllText(path)) : [];
    }
}

/// <summary>
/// Streams creatures with their tiles. All of a tile's spawns are known once it loads, but
/// only those near the player get a node, and only the nearest of those animate. A server's
/// creatures come and go through <see cref="AddLive"/> and <see cref="RemoveLive"/> instead.
/// </summary>
public partial class CreatureManager : Node
{
    /// <summary>Creatures are kept this much further out than they show, so walking back and forth doesn't rebuild.</summary>
    private const float KeepMargin = 25f;

    private const int MaxNodes = 150;

    /// <summary>Making a skeleton is slow enough to see, so at most this many start per frame.</summary>
    private const int MaxNewNodesPerFrame = 1;

    private const double RankSeconds = 0.25;

    /// <summary>A spawn further than this from the terrain is on a floor, a bridge or in a cave.</summary>
    private const float TerrainSnapRange = 3f;

    /// <summary>Near the player, physics has buildings too, so walking creatures ask it.</summary>
    private const float RayRange = 35f;
    private const float RayAbove = 2f;
    private const float RayBelow = 4f;
    private const double RaySeconds = 0.2;
    private const float HeightSmoothing = 10f;

    /// <summary>
    /// How near a creature's light is looked up. A WMO group has collision within 40 yards of
    /// the player, so a creature this near and standing in one always has it to hit.
    /// </summary>
    private const float LightRange = 40f;

    /// <summary>How often a walking creature, then one standing on terrain, asks what it's on. Collision may come later.</summary>
    private const double WalkingLightSeconds = 0.5;
    private const double StandingLightSeconds = 2;

    /// <summary>Doodads say nothing about light, so a ray goes through this many to reach the floor.</summary>
    private const int MaxDoodadsThrough = 3;

    private sealed class Entry(CreatureSpawn spawn, (int X, int Y) tile, ICreatureMotion motion)
    {
        public CreatureSpawn Spawn { get; } = spawn;
        public (int X, int Y) Tile { get; } = tile;
        public ICreatureMotion Motion { get; set; } = motion;
        public CreatureNode? Node { get; set; }
        public float Distance { get; set; } = float.PositiveInfinity;
        public float Height { get; set; } = spawn.Z;
        public bool HasHeight { get; set; }
        public double NextRay { get; set; }
        public double NextLightRay { get; set; }
        public bool LightFound { get; set; }
        public CreatureSoundSet? Sounds { get; set; }
        public double NextFidget { get; set; }
        public bool Dead { get; set; }
        public bool Selected { get; set; }
    }

    private sealed record TileState(Node3D Node, TerrainHeights Heights, Node3D Container, List<Entry> Entries);

    private readonly Dictionary<(int X, int Y), TileState> _tiles = [];
    private readonly ConcurrentQueue<((int X, int Y) Tile, IReadOnlyList<CreatureSpawn> Spawns)> _readSpawns = new();
    private readonly ConcurrentQueue<(string Key, CreatureAsset? Asset)> _builtAssets = new();
    private readonly Dictionary<string, CreatureAsset?> _assets = [];
    private readonly List<Entry> _nodes = [];
    private readonly List<Entry> _wanted = [];

    /// <summary>A server's creatures, whose tile may not be loaded yet.</summary>
    private readonly Dictionary<long, Entry> _live = [];
    private int _reading;
    private bool _building;
    private double _rankTimer;
    private double _clock;
    private CreatureLibrary _library = null!;
    private TileManager _world = null!;
    private ISpawnSource _spawns = null!;
    private CreatureSoundPlayer _sounds = null!;
    private FootstepTable _footsteps = null!;

    /// <summary>Seconds on the clock that decides where creatures are: spawn files' motion, and how far along a server's paths are.</summary>
    public double Clock => _clock;

    /// <summary>Anything that's near the player but not in the scene yet.</summary>
    public bool IsBusy => _reading > 0 || _building || !_builtAssets.IsEmpty || !_readSpawns.IsEmpty || _wanted.Count > 0;

    public int SpawnCount => _tiles.Values.Sum(t => t.Entries.Count);

    public IEnumerable<CreatureNode> Nodes => _nodes.Select(e => e.Node!);

    public int NodeCount => _nodes.Count;

    /// <summary>The nearest of a server's creatures, its distance and whether its tile is loaded, for the log.</summary>
    public string NearestLive => _live.Values.MinBy(e => e.Distance) is { } nearest
        ? $"{nearest.Spawn.Name} at {nearest.Distance:F0} yd, tile {nearest.Tile.X}_{nearest.Tile.Y}{(_tiles.ContainsKey(nearest.Tile) ? "" : " (not loaded)")}"
        : "none";

    /// <summary>A placed creature with this name, for checks.</summary>
    public IEnumerable<CreatureNode> NodesNamed(string name) =>
        _nodes.Where(e => e.Spawn.Name == name).Select(e => e.Node!);

    public CreatureSoundPlayer Sounds => _sounds;

    public void Start(CreatureLibrary library, TileManager world, ISpawnSource spawns, CreatureSoundPlayer sounds, FootstepTable footsteps)
    {
        _sounds = sounds;
        _footsteps = footsteps;
        _library = library;
        _world = world;
        _spawns = spawns;
        world.TileAdded += OnTileAdded;
        world.TileRemoved += OnTileRemoved;
    }

    /// <summary>A creature the server shows, at its position's tile. It appears once that tile loads.</summary>
    public void AddLive(CreatureSpawn spawn, ICreatureMotion motion)
    {
        RemoveLive(spawn.Guid);
        var now = motion.At(_clock);
        var entry = new Entry(spawn, WowCoords.TileAt(now.X, now.Y), motion);
        _live[spawn.Guid] = entry;
        if (_tiles.TryGetValue(entry.Tile, out var state))
        {
            state.Entries.Add(entry);
        }
    }

    /// <summary>The server has it moving differently. It keeps its tile, which is only where it's measured from.</summary>
    public void SetLiveMotion(long guid, ICreatureMotion motion)
    {
        if (_live.TryGetValue(guid, out var entry))
        {
            entry.Motion = motion;
        }
    }

    /// <summary>
    /// Another player changed what they wear: they're built again in their new looks, where they
    /// are and going the same way. The old body goes at once and the new one comes when built.
    /// </summary>
    public void Redress(long guid, CharacterAppearance character)
    {
        if (!_live.TryGetValue(guid, out var entry) || entry.Spawn.Character?.Key == character.Key)
        {
            return;
        }

        var (motion, dead, selected) = (entry.Motion, entry.Dead, entry.Selected);
        AddLive(entry.Spawn with { Character = character }, motion);
        SetLiveState(guid, dead, selected);
        _rankTimer = 0;
    }

    /// <summary>The server says it died or came back, and whether the player has it selected.</summary>
    public void SetLiveState(long guid, bool dead, bool selected)
    {
        if (_live.TryGetValue(guid, out var entry))
        {
            entry.Dead = dead;
            entry.Selected = selected;
            entry.Node?.SetState(dead, selected);
        }
    }

    /// <summary>It swung at something.</summary>
    public void PlayAttack(long guid)
    {
        if (_live.TryGetValue(guid, out var entry))
        {
            entry.Node?.PlayAttack();
        }
    }

    /// <summary>Where a server unit is now, in WoW coordinates, standing on what's under it once that's known.</summary>
    public System.Numerics.Vector3? LivePosition(long guid)
    {
        if (!_live.TryGetValue(guid, out var entry))
        {
            return null;
        }

        var now = entry.Motion.At(_clock);
        return new System.Numerics.Vector3((float)now.X, (float)now.Y, entry.HasHeight ? entry.Height : now.Z ?? entry.Height);
    }

    /// <summary>A server unit's node, if it's in the scene.</summary>
    public CreatureNode? LiveNode(long guid) => _live.TryGetValue(guid, out var entry) ? entry.Node : null;

    /// <summary>The server's units that are in the scene, for picking.</summary>
    public IEnumerable<(long Guid, CreatureNode Node)> LiveNodes
    {
        get
        {
            foreach (var (guid, entry) in _live)
            {
                if (entry.Node is { } node && node.Visible)
                {
                    yield return (guid, node);
                }
            }
        }
    }

    public void RemoveLive(long guid)
    {
        if (!_live.Remove(guid, out var entry))
        {
            return;
        }

        if (_tiles.TryGetValue(entry.Tile, out var state))
        {
            state.Entries.Remove(entry);
        }

        _wanted.Remove(entry);
        if (entry.Node is { } node)
        {
            _nodes.Remove(entry);
            node.QueueFree();
            entry.Node = null;
        }
    }

    private void OnTileAdded((int X, int Y) tile, Node3D node, TerrainHeights heights)
    {
        var container = new Node3D { Name = "Creatures" };
        node.AddChild(container);
        _tiles[tile] = new TileState(node, heights, container, [.. _live.Values.Where(e => e.Tile == tile)]);

        _reading++;
        Task.Run(() =>
        {
            IReadOnlyList<CreatureSpawn> spawns = [];
            try
            {
                spawns = _spawns.Read(tile.X, tile.Y, heights);
            }
            catch (Exception e)
            {
                GD.PushError($"Could not read the creatures of tile {tile.X}_{tile.Y}: {e.Message}");
            }

            _readSpawns.Enqueue((tile, spawns));
        });
    }

    private void OnTileRemoved((int X, int Y) tile)
    {
        // Their nodes leave with the tile's. A server's creatures wait for it to come back.
        _nodes.RemoveAll(e => e.Tile == tile);
        _wanted.RemoveAll(e => e.Tile == tile);
        _tiles.Remove(tile);
        foreach (var entry in _live.Values)
        {
            if (entry.Tile == tile)
            {
                entry.Node = null;
                entry.Distance = float.PositiveInfinity;
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_world is null)
        {
            return;
        }

        _clock += delta;
        CollectFinishedWork();

        _rankTimer -= delta;
        if (_rankTimer <= 0)
        {
            _rankTimer = RankSeconds;
            using (FrameProbe.Time("creature rank"))
            {
                Rank();
            }
        }

        using (FrameProbe.Time("creature add"))
        {
            AddWantedNodes();
        }

        using (FrameProbe.Time("creature move"))
        {
            MoveNodes(delta);
        }
    }

    private void CollectFinishedWork()
    {
        while (_readSpawns.TryDequeue(out var read))
        {
            _reading--;
            if (_tiles.TryGetValue(read.Tile, out var state))
            {
                state.Entries.AddRange(read.Spawns.Select(s => new Entry(s, read.Tile, new CreatureMotion(s))));
            }

            // Ranking soon means something on screen right after a tile loads.
            _rankTimer = 0;
        }

        while (_builtAssets.TryDequeue(out var built))
        {
            _building = false;
            _assets[built.Key] = built.Asset;
        }
    }

    /// <summary>Works out who should exist and animate, a few times per second because it visits every spawn.</summary>
    private void Rank()
    {
        var feet = _world.Player.Feet;
        var visibleRange = AppSettings.CreatureRange;
        var keepRange = visibleRange + KeepMargin;
        var candidates = new List<Entry>();
        foreach (var state in _tiles.Values)
        {
            var local = feet - state.Node.Position;
            foreach (var entry in state.Entries)
            {
                var motion = entry.Motion.At(_clock);
                var position = WowCoords.TileLocal(motion.X, motion.Y, entry.Height, entry.Tile.X, entry.Tile.Y);
                entry.Distance = new Vector3(position.X, position.Y, position.Z).DistanceTo(local);
                if (entry.Distance < keepRange)
                {
                    candidates.Add(entry);
                }
            }
        }

        candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        _nodes.RemoveAll(entry =>
        {
            if (entry.Distance < keepRange)
            {
                return false;
            }

            entry.Node!.QueueFree();
            entry.Node = null;
            return true;
        });

        _wanted.Clear();
        var count = 0;
        foreach (var entry in candidates)
        {
            if (count >= MaxNodes || entry.Distance >= visibleRange)
            {
                break;
            }

            count++;
            if (entry.Node is null && !_assets.TryGetValue(AssetKey(entry.Spawn), out var known) | known is not null)
            {
                _wanted.Add(entry);
            }
        }

        StartNextAsset();
    }

    /// <summary>Creatures look alike when they share a display and what they hold, and players when they share every choice and item.</summary>
    private static string AssetKey(CreatureSpawn spawn) => spawn.Character is { } character
        ? $"character:{character.Key}"
        : $"{spawn.DisplayId}:{string.Join(',', spawn.Weapons)}";

    /// <summary>Models load one at a time, nearest first.</summary>
    private void StartNextAsset()
    {
        if (_building)
        {
            return;
        }

        foreach (var entry in _wanted)
        {
            var key = AssetKey(entry.Spawn);
            if (!_assets.ContainsKey(key))
            {
                var (display, weapons, character) = (entry.Spawn.DisplayId, entry.Spawn.Weapons, entry.Spawn.Character);
                _building = true;
                Task.Run(() =>
                {
                    CreatureAsset? asset = null;
                    try
                    {
                        asset = character is not null ? _library.BuildCharacter(character) : _library.Build(display, weapons);
                    }
                    catch (Exception e)
                    {
                        GD.PushError($"Could not build creature display {display}: {e.Message}");
                    }

                    _builtAssets.Enqueue((key, asset));
                });
                return;
            }
        }
    }

    private void AddWantedNodes()
    {
        var added = 0;
        for (var i = 0; i < _wanted.Count && added < MaxNewNodesPerFrame;)
        {
            var entry = _wanted[i];
            if (!_assets.TryGetValue(AssetKey(entry.Spawn), out var asset))
            {
                // Still loading, and whoever's next might not be.
                i++;
                StartNextAsset();
                continue;
            }

            _wanted.RemoveAt(i);
            if (asset is null || !_tiles.TryGetValue(entry.Tile, out var state))
            {
                continue;
            }

            var node = new CreatureNode { Name = $"Creature_{entry.Spawn.Guid}" };

            // Built before it joins the scene, so nothing reacts to each bone and child as it's added.
            using (FrameProbe.Time("creature add: build"))
            {
                node.Build(asset, entry.Spawn.Name);
            }

            using (FrameProbe.Time("creature add: enter scene"))
            {
                state.Container.AddChild(node);
            }

            using (FrameProbe.Time("creature add: first animation"))
            {
                node.StartIdle();
            }
            node.Scale = Vector3.One * Math.Max(entry.Spawn.Scale, 0.01f);
            entry.Node = node;
            node.SetState(entry.Dead, entry.Selected);
            entry.Sounds = asset.Sounds;
            entry.NextFidget = _clock + NextFidgetDelay(entry);
            node.SoundEvent += sound => PlaySound(entry, sound.Kind, sound.Index);
            StartLoopSound(entry, node);
            _nodes.Add(entry);
            MoveNode(entry, state, 0);
            added++;
        }
    }

    private void MoveNodes(double delta)
    {
        BillboardBones.Camera = GetViewport().GetCamera3D()?.GlobalTransform;
        var fullRate = AppSettings.FullRateAnimation;
        var interior = _world.Interior;
        foreach (var entry in _nodes)
        {
            // Every creature in the scene animates: far ones update less often, so freezing them saved little.
            if (_tiles.TryGetValue(entry.Tile, out var state))
            {
                MoveNode(entry, state, delta);

                // Inside a WMO with no view out, only creatures in its rooms in view can be seen. The
                // rest keep walking, but aren't drawn or posed until they could be.
                var node = entry.Node!;
                var seen = interior is null || interior.Shows(node.GlobalPosition);
                if (node.Visible != seen)
                {
                    node.Visible = seen;
                }

                if (seen)
                {
                    node.Animate(delta, fullRate ? 0 : AnimationInterval(entry.Distance));
                }

                if (entry.Distance < CreatureSoundPlayer.MaxDistance)
                {
                    entry.Node.CheckSoundEvents();
                    PlayTimedFidget(entry);
                }
            }
        }
    }

    /// <summary>
    /// Seconds between a creature's animation updates: every frame up close, less often further away,
    /// where it's small on screen. Running creatures on worker threads was tried and was slower, since
    /// every bone a worker uploads goes through the engine's locked command queue.
    /// </summary>
    private static double AnimationInterval(float distance) => distance switch
    {
        < 25f => 0,
        < 50f => 1.0 / 30,
        < 100f => 1.0 / 20,
        _ => 1.0 / 12,
    };

    private void PlaySound(Entry entry, CreatureSoundKind kind, int index)
    {
        if (entry.Sounds is not { } sounds || entry.Node is not { } node || !_tiles.TryGetValue(entry.Tile, out var state))
        {
            return;
        }

        var sound = 0;
        switch (kind)
        {
            case CreatureSoundKind.Footstep:
                var motion = entry.Motion.At(_clock);
                sound = _footsteps.SoundFor(sounds.Footstep, state.Heights.EffectAt(motion.X, motion.Y));
                break;
            case CreatureSoundKind.Fidget when index < sounds.Fidgets.Length:
                sound = sounds.Fidgets[index];
                break;
            case CreatureSoundKind.WingFlap:
                sound = sounds.WingFlap;
                break;
        }

        _sounds.Play(sound, node.GlobalPosition);
    }

    /// <summary>A fidget between its table's delays, for creatures with them, as well as those their clips ask for.</summary>
    private void PlayTimedFidget(Entry entry)
    {
        if (_clock < entry.NextFidget || entry.Sounds is not { FidgetDelayMax: > 0f } sounds)
        {
            return;
        }

        entry.NextFidget = _clock + NextFidgetDelay(entry);
        var fidgets = sounds.Fidgets.Where(f => f != 0).ToArray();
        if (fidgets.Length > 0)
        {
            _sounds.Play(fidgets[Random.Shared.Next(fidgets.Length)], entry.Node!.GlobalPosition);
        }
    }

    private static double NextFidgetDelay(Entry entry) => entry.Sounds is { } sounds
        ? sounds.FidgetDelayMin + Random.Shared.NextDouble() * Math.Max(sounds.FidgetDelayMax - sounds.FidgetDelayMin, 0f)
        : 0;

    private async void StartLoopSound(Entry entry, CreatureNode node)
    {
        if (entry.Sounds is not { Loop: not 0 } sounds || await _sounds.LoadLoopAsync(sounds.Loop) is not { } loop)
        {
            return;
        }

        // Back on the main thread here, but the creature may have left while this loaded.
        Callable.From(() =>
        {
            if (IsInstanceValid(node))
            {
                var player = CreatureSoundPlayer.NewPlayer();
                player.Stream = loop.Stream;
                player.VolumeDb = CreatureSoundPlayer.VolumeDb(loop.Entry.Volume);
                CreatureSoundPlayer.ApplyRange(player, loop.Entry);
                player.Autoplay = true;
                node.AddChild(player);
            }
        }).CallDeferred();
    }

    private void MoveNode(Entry entry, TileState state, double delta)
    {
        var motion = entry.Motion.At(_clock);
        var height = HeightOf(entry, state, motion, delta);
        var position = WowCoords.TileLocal(motion.X, motion.Y, height, entry.Tile.X, entry.Tile.Y);
        entry.Node!.Move(new Vector3(position.X, position.Y, position.Z), motion.Facing, motion.Moving, entry.Motion.MoveSpeed, entry.Distance, delta);
        UpdateLight(entry, state, motion, new Vector3(position.X, position.Y, position.Z));
        entry.Node.FadeLight(delta);
    }

    /// <summary>Asks what the creature stands on. One that's standing on a WMO needn't ask again until it moves.</summary>
    private void UpdateLight(Entry entry, TileState state, CreatureMotionState motion, Vector3 local)
    {
        if (motion.Moving)
        {
            entry.LightFound = false;
        }

        if (entry.LightFound || entry.Distance >= LightRange || _clock < entry.NextLightRay)
        {
            return;
        }

        entry.NextLightRay = _clock + (motion.Moving ? WalkingLightSeconds : StandingLightSeconds);

        var ray = CastDown(state.Node.Position + local + Vector3.Up * RayAbove);
        for (var through = 0; through <= MaxDoodadsThrough; through++)
        {
            if (!ray.IsColliding())
            {
                // Keeps what it had, since this says nothing about where it is.
                return;
            }

            var collider = ray.GetCollider();
            if (collider is Node node && node.IsInGroup(TileObjectsNode.DoodadCollisionGroup))
            {
                ray.AddExceptionRid(ray.GetColliderRid());
                ray.ForceRaycastUpdate();
                continue;
            }

            if (collider is WmoGroupBody group)
            {
                entry.Node!.SetGroundLight(group.CreatureLightAt(ray.GetCollisionPoint()));
                entry.LightFound = !motion.Moving;
            }
            else
            {
                entry.Node!.SetGroundLight(null);
            }

            return;
        }
    }

    private float HeightOf(Entry entry, TileState state, CreatureMotionState motion, double delta)
    {
        if (motion.Z is { } exact)
        {
            return entry.Height = exact;
        }

        if (!entry.HasHeight)
        {
            entry.HasHeight = true;
            entry.Height = entry.Spawn.Z;
        }

        if (!motion.Moving && entry.Height == entry.Spawn.Z)
        {
            return entry.Height;
        }

        // Far from the player there's only the terrain, unless the spawn isn't on it at all.
        var terrain = state.Heights.HeightAt(motion.X, motion.Y);
        var target = Mathf.Abs(terrain - entry.Spawn.Z) <= TerrainSnapRange ? terrain : entry.Spawn.Z;

        if (entry.Distance < RayRange && _clock >= entry.NextRay)
        {
            entry.NextRay = _clock + RaySeconds;
            if (RayHeight(entry, state, motion, target) is { } hit)
            {
                target = hit;
            }
        }

        entry.Height = delta > 0 ? Mathf.Lerp(entry.Height, target, 1f - Mathf.Exp(-HeightSmoothing * (float)delta)) : target;
        return entry.Height;
    }

    private float? RayHeight(Entry entry, TileState state, CreatureMotionState motion, float around)
    {
        var local = WowCoords.TileLocal(motion.X, motion.Y, around, entry.Tile.X, entry.Tile.Y);
        var ray = CastDown(state.Node.Position + new Vector3(local.X, local.Y + RayAbove, local.Z));
        return ray.IsColliding() ? ray.GetCollisionPoint().Y : null;
    }

    private RayCast3D? _ray;

    /// <summary>
    /// Casts down from a point through one ray node, which can be cast again after adding
    /// exceptions. A space state query makes several objects each call, garbage that made the
    /// collector pause now and then with many creatures about.
    /// </summary>
    private RayCast3D CastDown(Vector3 from)
    {
        if (_ray is null)
        {
            _ray = new RayCast3D { TopLevel = true, Enabled = false, CollisionMask = TileManager.TerrainLayer };
            AddChild(_ray);
        }

        _ray.ClearExceptions();
        _ray.GlobalPosition = from;
        _ray.TargetPosition = Vector3.Down * (RayAbove + RayBelow);
        _ray.ForceRaycastUpdate();
        return _ray;
    }
}
