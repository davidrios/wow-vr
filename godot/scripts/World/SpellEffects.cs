using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Converters;
using WowVR.Formats.Database;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>
/// What spells look and sound like, for every unit the server shows casting, the player too, from
/// SpellVisual's kits: while casting the caster holds its ready animation with effects in its hands
/// and a sound, then plays its cast; a missile flies to each unit hit, which reels with the impact's
/// effect and sound when it arrives.
/// </summary>
public partial class SpellEffects : Node3D
{
    /// <summary>A cast's or impact's effects last this long, in seconds.</summary>
    private const double MomentSeconds = 1.6;

    /// <summary>A landing on the ground shows this long, in seconds, as Blizzard's swirl.</summary>
    private const double AreaMomentSeconds = 3;

    /// <summary>A missile that hasn't arrived by then is let go.</summary>
    private const double MissileGiveUpSeconds = 6;

    /// <summary>More hits than this show no more missiles, as a big area spell's.</summary>
    private const int MaxMissiles = 6;

    /// <summary>
    /// The model's attachment points for each <see cref="KitPlace"/>, in M2's numbering: the spell
    /// hands (21, 22) before the plain ones (2, 1), the helm (11), chest blood (15), base (19),
    /// breath (17) and the three specials (23 to 25). The world's effects stand at the feet.
    /// </summary>
    private static readonly int[][] Attachments =
    [
        [11, 18], [15, 16], [19], [21, 2], [22, 1], [17, 11], [2], [1], [23], [24], [25], [],
    ];

    public required ServerWorld World { get; init; }
    public required CreatureManager Creatures { get; init; }
    public required CreatureLibrary Library { get; init; }
    public required CreatureSoundPlayer Sounds { get; init; }
    public required TileManager Tiles { get; init; }

    private sealed class Held(uint spell)
    {
        public uint Spell { get; } = spell;
        public List<Node3D> Effects { get; } = [];
        public int Version { get; set; }
    }

    private sealed record Missile(Node3D Node, ulong Target, float Speed, SpellKit? Impact, double Started);

    private readonly Dictionary<ulong, Held> _held = [];
    private readonly List<(Node3D Node, double Until)> _timed = [];
    private readonly List<Missile> _missiles = [];
    private int _version;

    public override void _Ready()
    {
        World.UnitCast += OnCast;
    }

    private void OnCast(SessionEvent e)
    {
        switch (e)
        {
            case UnitCastBegan began:
                Begin(began);
                break;
            case UnitSpellWent went:
                Went(went);
                break;
            case UnitCastStopped stopped:
                Stop(stopped.Caster, stopped.Spell);
                break;
            case AreaEffectAppeared area:
                ShowArea(area);
                break;
            case AreaEffectGone gone:
                if (_areaRains.Remove(gone.Guid, out var rain))
                {
                    rain.Until = ServerWorld.Now;
                }

                if (_areas.Remove(gone.Guid, out var shown))
                {
                    foreach (var node in shown.Where(IsInstanceValid))
                    {
                        node.QueueFree();
                    }
                }

                break;
        }
    }

    private readonly Dictionary<ulong, List<Node3D>> _areas = [];

    /// <summary>The rain's frame time is capped at this, so a stall doesn't drop a burst at once.</summary>
    private const double RainStepCap = 1.0 / 15;

    /// <summary>A shard's spot is tried this high above the center, and dropped this far to the ground, in yards.</summary>
    private const float RainHeight = 16.666666f;
    private const float RainDrop = 33.333332f;
    private const float RainStart = 0.16666667f;

    /// <summary>Each shard waits up to this long before it shows, in seconds.</summary>
    private const double RainMaxDelay = 0.51;

    /// <summary>A tick's rain, from a ground spell's instant-area kit, lasts this long, in seconds.</summary>
    private const double TickRainSeconds = 1;

    private sealed class Rain(EffectAsset model, Vector3 center, float radius, float perSecond, double until)
    {
        public EffectAsset Model { get; } = model;
        public Vector3 Center { get; } = center;
        public float Radius { get; } = radius;
        public float PerSecond { get; } = perSecond;
        public double Until { get; set; } = until;
        public double Owed { get; set; }
    }

    private sealed record Shard(Node3D Node, double ShowAt, double Until);

    private static readonly StringName BakeStart = "bake_start";

    /// <summary>
    /// Has an effect's meshes and emitters play their animation once, from some seconds from now,
    /// rather than loop on the clock, as a moment's effect: a cast, an impact, a shard.
    /// </summary>
    private static void PlayOnce(Node root, double fromNow)
    {
        var startAt = Time.GetTicksUsec() / 1_000_000.0 + fromNow;
        var shaderStart = ShaderTime(fromNow);
        foreach (var node in root.FindChildren("*", "", true, false))
        {
            switch (node)
            {
                case MeshInstance3D mesh:
                    mesh.SetInstanceShaderParameter(BakeStart, shaderStart);
                    break;
                case Converters.EmitterFollower follower:
                    follower.StartAt = startAt;
                    break;
            }
        }
    }

    /// <summary>
    /// Shaders' TIME some seconds from now: the engine's running time, which they wrap at
    /// rendering/limits/time/time_rollover_secs, an hour unless the project says otherwise.
    /// </summary>
    private static float ShaderTime(double fromNow)
    {
        var rollover = ProjectSettings.GetSetting("rendering/limits/time/time_rollover_secs", 3600).AsDouble();
        return (float)((Time.GetTicksMsec() / 1000.0 + fromNow) % rollover);
    }

    /// <summary>Shards of rain showing now.</summary>
    public int ShowingShards => _shards.Count(s => s.Node.Visible);

    /// <summary>What's raining now, for checks: rains running, shards made and showing.</summary>
    public string RainReport => $"{_rains.Count} rains ({_areaRains.Count} of areas), {_shards.Count} shards, {_shards.Count(s => s.Node.Visible)} showing";

    private readonly List<Rain> _rains = [];
    private readonly List<Shard> _shards = [];
    private readonly Dictionary<ulong, Rain> _areaRains = [];
    private RayCast3D? _rainRay;

    /// <summary>Starts a kit's rain of area models, if it has one, at a place, until a time (infinity for an area's life).</summary>
    private void StartRain(SpellKit kit, Vector3 center, float radius, double until, ulong? area = null)
    {
        if (World.Spells?.AreaRainOf(kit) is not { } rain || radius <= 0)
        {
            return;
        }

        // The client thins it with its spell detail setting; off with particles is the least.
        var perSecond = rain.PerSecond * (AppSettings.Particles ? 1f : 0.2f);
        WithEffect(rain.Model, model =>
        {
            if (area is { } guid && !_areas.ContainsKey(guid))
            {
                return;
            }

            var running = new Rain(model, center, radius, perSecond, until);
            _rains.Add(running);
            if (area is { } id)
            {
                _areaRains[id] = running;
            }
        });
    }

    /// <summary>
    /// Rains as the client does: owed shards build up at the rate, and each is dropped at a random
    /// spot within the radius, tried high above the center and brought short of what's in the
    /// way, then down onto the ground below, showing after a short random wait for one play of its
    /// animation.
    /// </summary>
    private void UpdateRain(double delta, double now)
    {
        for (var i = _rains.Count - 1; i >= 0; i--)
        {
            var rain = _rains[i];
            if (now >= rain.Until)
            {
                _rains.RemoveAt(i);
                continue;
            }

            rain.Owed += Math.Min(delta, RainStepCap) * rain.PerSecond;
            while (rain.Owed > 1)
            {
                rain.Owed -= 1;
                var node = new Node3D { Name = "RainShard", Visible = false };
                AddChild(node);
                node.GlobalPosition = ShardSpot(rain);
                var showAt = now + Random.Shared.NextDouble() * RainMaxDelay;
                if (rain.Model.Mesh is { } mesh)
                {
                    node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                }

                foreach (var particle in rain.Model.Particles)
                {
                    node.AddChild(particle.NewNode(particle.Position, fresh: true));
                }

                // Its animation plays once from when it shows, as its meteor falls and its glow goes out.
                PlayOnce(node, showAt - now);
                _shards.Add(new Shard(node, showAt, showAt + rain.Model.Seconds));
            }
        }

        for (var i = _shards.Count - 1; i >= 0; i--)
        {
            var shard = _shards[i];
            if (now >= shard.Until || !IsInstanceValid(shard.Node))
            {
                if (IsInstanceValid(shard.Node))
                {
                    shard.Node.QueueFree();
                }

                _shards.RemoveAt(i);
            }
            else if (!shard.Node.Visible && now >= shard.ShowAt)
            {
                shard.Node.Visible = true;
            }
        }
    }

    /// <summary>Where a shard lands: a random direction and a random fraction of the radius from the center, on the ground.</summary>
    private Vector3 ShardSpot(Rain rain)
    {
        if (_rainRay is null)
        {
            _rainRay = new RayCast3D { TopLevel = true, Enabled = false, CollisionMask = TileManager.TerrainLayer };
            AddChild(_rainRay);
        }

        var angle = Random.Shared.NextSingle() * Mathf.Tau;
        var reach = Random.Shared.NextSingle() * rain.Radius;
        var from = rain.Center + Vector3.Up * RainStart;
        var high = rain.Center + new Vector3(Mathf.Cos(angle) * reach, RainHeight, Mathf.Sin(angle) * reach);

        // Short of a ceiling or wall between the center and the spot above it.
        var spot = high;
        _rainRay.GlobalPosition = from;
        _rainRay.TargetPosition = high - from;
        _rainRay.ForceRaycastUpdate();
        if (_rainRay.IsColliding())
        {
            spot = from + (_rainRay.GetCollisionPoint() - from) * 0.95f;
        }

        _rainRay.GlobalPosition = spot;
        _rainRay.TargetPosition = Vector3.Down * RainDrop;
        _rainRay.ForceRaycastUpdate();
        return _rainRay.IsColliding() ? _rainRay.GetCollisionPoint() : rain.Center;
    }

    /// <summary>A lasting area on the ground: its kit's models at the place, grown to its radius, and its sound, until it goes.</summary>
    private void ShowArea(AreaEffectAppeared area)
    {
        if (World.Spells?.VisualOf((int)area.Spell)?.PersistentArea is not { } kit)
        {
            return;
        }

        var nodes = new List<Node3D>();
        _areas[area.Guid] = nodes;
        var at = Tiles.LocalPosition(area.Position);
        PlaySoundAt(kit.Sound, at);
        StartRain(kit, at, area.Radius, double.PositiveInfinity, area.Guid);
        AtPlace(kit, at, area.Radius, node =>
        {
            if (_areas.TryGetValue(area.Guid, out var still) && still == nodes)
            {
                nodes.Add(node);
            }
            else
            {
                node.QueueFree();
            }
        });
    }

    /// <summary>
    /// A kit's models standing at a place in the world rather than on a unit, as a ground spell's
    /// landing: each grown from its own size (SpellVisualEffectName's) to the spell's radius.
    /// </summary>
    private void AtPlace(SpellKit kit, Vector3 at, float radius, Action<Node3D> placed)
    {
        for (var place = 0; place < kit.Effects.Length; place++)
        {
            if (kit.Effects[place] is not { } path)
            {
                continue;
            }

            // Area models are made at their size: Consecration's is 23 yards across for its 8-yard radius.
            WithEffect(path, effect =>
            {
                var node = new Node3D { Name = "AreaEffect" };
                AddChild(node);
                node.GlobalPosition = at;
                if (effect.Mesh is { } mesh)
                {
                    node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                }

                foreach (var particle in effect.Particles)
                {
                    node.AddChild(particle.NewNode(particle.Position, fresh: true));
                }

                placed(node);
            });
        }
    }

    private void PlaySoundAt(int sound, Vector3 at)
    {
        if (sound > 0)
        {
            Sounds.Play(sound, at);
        }
    }

    /// <summary>The caster readies the spell, or channels it: its kit held until the cast ends.</summary>
    private void Begin(UnitCastBegan began)
    {
        if (World.Spells?.VisualOf((int)began.Spell) is not { } visual || NodeOf(began.Caster) is not { } caster)
        {
            return;
        }

        var kit = began.Channel ? visual.Channel ?? visual.Precast : visual.Precast;
        if (kit is null || (!began.Channel && began.DurationMs == 0))
        {
            return;
        }

        Stop(began.Caster, 0);
        var held = new Held(began.Spell) { Version = ++_version };
        _held[began.Caster] = held;
        PlayAnimation(caster, kit, hold: true);
        PlaySound(kit.Sound, caster);
        ForEachEffect(kit, (effect, place) =>
        {
            // Only while it's the same cast: one that ended before its model loaded shows nothing.
            if (_held.TryGetValue(began.Caster, out var still) && still.Version == held.Version && IsInstanceValid(caster))
            {
                held.Effects.Add(caster.AttachEffect(effect, Attachments[(int)place]));
            }
        });
    }

    /// <summary>The spell went off: the caster's cast kit, then its missiles, or the impact on what it hit.</summary>
    private void Went(UnitSpellWent went)
    {
        var spell = World.Spells?.Get((int)went.Spell);
        var visual = World.Spells?.VisualOf((int)went.Spell);

        // A channel keeps its hold through the go that starts it.
        if (_held.TryGetValue(went.Caster, out var held) && held.Spell == went.Spell)
        {
            Stop(went.Caster, went.Spell);
        }

        if (visual is null || spell is null)
        {
            return;
        }

        var caster = NodeOf(went.Caster);
        if (caster is not null && visual.Cast is { } cast)
        {
            PlayAnimation(caster, cast, hold: false);
            PlaySound(cast.Sound, caster);
            ForEachEffect(cast, (effect, place) => Timed(caster, effect, place));
        }

        // Where a ground spell lands: its landing, once, and each tick's.
        if (went.Place is { } place)
        {
            var at = Tiles.LocalPosition(place);
            foreach (var kit in new[] { visual.ImpactArea, visual.InstantArea })
            {
                if (kit is not null)
                {
                    PlaySoundAt(kit.Sound, at);
                    AtPlace(kit, at, spell.Radius, node =>
                    {
                        PlayOnce(node, 0);
                        _timed.Add((node, ServerWorld.Now + AreaMomentSeconds));
                    });
                    StartRain(kit, at, spell.Radius, ServerWorld.Now + TickRainSeconds);
                }
            }
        }

        // What hits each unit besides the impact, as Blizzard's frost at their feet.
        if (visual.TargetImpact is { } targetImpact)
        {
            foreach (var hit in went.Hits.Where(h => h != went.Caster).Take(MaxMissiles))
            {
                if (NodeOf(hit) is { } target)
                {
                    PlaySound(targetImpact.Sound, target);
                    ForEachEffect(targetImpact, (effect, where) => Timed(target, effect, where));
                }
            }
        }

        foreach (var hit in went.Hits.Where(h => h != went.Caster || visual.Missile is null).Take(MaxMissiles))
        {
            if (visual.Missile is { } missile && spell.Speed > 0 && caster is not null && NodeOf(hit) is not null)
            {
                Launch(caster, hit, missile, spell.Speed, visual.Impact);
            }
            else if (visual.Impact is { } impact && NodeOf(hit) is { } target)
            {
                Impact(target, impact);
            }
        }
    }

    /// <summary>The cast ended without going off, or its channel ran out: the caster lets go.</summary>
    private void Stop(ulong caster, uint spell)
    {
        // A stop for another spell than the one held, as a triggered one's, leaves it be.
        if (!_held.TryGetValue(caster, out var held) || (spell != 0 && held.Spell != spell))
        {
            return;
        }

        _held.Remove(caster);

        foreach (var effect in held.Effects.Where(IsInstanceValid))
        {
            effect.QueueFree();
        }

        NodeOf(caster)?.StopSpellAnimation();
    }

    private void Impact(CreatureNode target, SpellKit impact)
    {
        PlayAnimation(target, impact, hold: false);
        PlaySound(impact.Sound, target);
        ForEachEffect(impact, (effect, place) => Timed(target, effect, place));
    }

    /// <summary>A missile from the caster's hands to a unit it hit, which reels when it arrives.</summary>
    private void Launch(CreatureNode caster, ulong target, string model, float speed, SpellKit? impact)
    {
        WithEffect(model, effect =>
        {
            if (!IsInstanceValid(caster))
            {
                return;
            }

            var node = new Node3D { Name = "Missile" };
            AddChild(node);
            node.GlobalPosition = caster.GlobalPosition + Vector3.Up * Mathf.Max(caster.Height * 0.6f, 1f);
            if (effect.Mesh is { } mesh)
            {
                node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            }

            foreach (var particle in effect.Particles)
            {
                node.AddChild(particle.NewNode(particle.Position, fresh: true));
            }

            _missiles.Add(new Missile(node, target, speed, impact, ServerWorld.Now));
        });
    }

    public override void _Process(double delta)
    {
        var now = ServerWorld.Now;
        UpdateRain(delta, now);
        for (var i = _timed.Count - 1; i >= 0; i--)
        {
            if (now >= _timed[i].Until || !IsInstanceValid(_timed[i].Node))
            {
                if (IsInstanceValid(_timed[i].Node))
                {
                    _timed[i].Node.QueueFree();
                }

                _timed.RemoveAt(i);
            }
        }

        for (var i = _missiles.Count - 1; i >= 0; i--)
        {
            var missile = _missiles[i];
            var target = NodeOf(missile.Target);
            if (target is null || now - missile.Started > MissileGiveUpSeconds)
            {
                missile.Node.QueueFree();
                _missiles.RemoveAt(i);
                continue;
            }

            // Flies at the target's chest, which may move, and turns to face where it goes.
            var aim = target.GlobalPosition + Vector3.Up * Mathf.Max(target.Height * 0.55f, 0.8f);
            var to = aim - missile.Node.GlobalPosition;
            var step = missile.Speed * (float)delta;
            if (to.Length() <= step)
            {
                missile.Node.QueueFree();
                _missiles.RemoveAt(i);
                if (missile.Impact is { } impact)
                {
                    Impact(target, impact);
                }

                continue;
            }

            var direction = to.Normalized();
            missile.Node.GlobalPosition += direction * step;
            if (Mathf.Abs(direction.Dot(Vector3.Up)) < 0.99f)
            {
                missile.Node.LookAt(missile.Node.GlobalPosition + direction, Vector3.Up);

                // A model faces its +X, where LookAt points -Z.
                missile.Node.RotateObjectLocal(Vector3.Up, Mathf.Pi / 2);
            }
        }
    }

    private void Timed(CreatureNode on, EffectAsset effect, KitPlace place)
    {
        if (IsInstanceValid(on))
        {
            var node = on.AttachEffect(effect, Attachments[(int)place]);
            PlayOnce(node, 0);
            _timed.Add((node, ServerWorld.Now + Math.Max(MomentSeconds, effect.Seconds)));
        }
    }

    private void PlayAnimation(CreatureNode node, SpellKit kit, bool hold)
    {
        var spells = World.Spells;
        if (spells is null || kit.Animation <= 0)
        {
            return;
        }

        node.PlaySpellAnimation(kit.Animation, hold, spells.AnimationFallback);
    }

    private void PlaySound(int sound, CreatureNode at)
    {
        if (sound > 0)
        {
            Sounds.Play(sound, at.GlobalPosition);
        }
    }

    /// <summary>Each effect model of a kit, read off the main thread the first time, then handed over on it.</summary>
    private void ForEachEffect(SpellKit kit, Action<EffectAsset, KitPlace> use)
    {
        for (var place = 0; place < kit.Effects.Length; place++)
        {
            if (kit.Effects[place] is { } path)
            {
                var where = (KitPlace)place;
                WithEffect(path, effect => use(effect, where));
            }
        }
    }

    private void WithEffect(string path, Action<EffectAsset> use)
    {
        Task.Run(() => Library.Effect(path)).ContinueWith(t => Callable.From(() =>
        {
            if (IsInstanceValid(this) && t.Result is { } effect)
            {
                use(effect);
            }
        }).CallDeferred());
    }

    /// <summary>A unit's model: the player's own body, or a creature or player the server shows.</summary>
    private CreatureNode? NodeOf(ulong guid) =>
        guid == World.Session.SelfGuid ? World.Avatar?.Body : Creatures.LiveNode((long)guid);
}
