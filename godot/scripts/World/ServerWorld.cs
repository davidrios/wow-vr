using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Desktop;
using WowVR.Formats;
using WowVR.Formats.Database;
using WowVR.Formats.World;
using WowVR.Net;
using Godot;

namespace WowVR.World;

/// <summary>A creature following a path the server gave, from the moment on the creatures' clock it set off.</summary>
public sealed class ServerMotion(MovePath path, double startClock) : ICreatureMotion
{
    public float MoveSpeed => path.Speed > 0f ? path.Speed : CreatureMotion.WalkSpeed;

    public CreatureMotionState At(double clock)
    {
        var (position, facing, moving) = path.At(clock - startClock);
        return new CreatureMotionState(position.X, position.Y, position.Z, facing, moving);
    }
}

/// <summary>How a unit looks to the player: hostile, neutral or friendly, as names and frames are colored.</summary>
public enum Reaction
{
    Hostile,
    Neutral,
    Friendly,
}

/// <summary>
/// Plays on an AzerothCore server: the creatures it shows around the player come and go and
/// follow its paths, the player's movement goes back to it, and it moves the player when it
/// teleports them. It keeps what the HUD shows: the player's health and power, units' stats,
/// the target, chat, and NPC windows. See <c>docs/decisions/0013-azerothcore-server.md</c>.
/// </summary>
public partial class ServerWorld : Node
{
    /// <summary>A crowd arriving at once is spread over frames.</summary>
    private const int MaxEventsPerFrame = 200;

    /// <summary>How often the log says what the server shows.</summary>
    private const double ReportSeconds = 10;

    /// <summary>Slower than this, in yards a second, isn't moving.</summary>
    private const float MovingSpeed = 0.5f;

    /// <summary>Off the ground this long is falling, not a bump.</summary>
    private const double FallingSeconds = 0.25;

    /// <summary>Teleports further than this go behind the loading screen.</summary>
    private const float FarTeleportYards = 150f;

    /// <summary>How near an area trigger's edge counts as being in it, in yards.</summary>
    private const double AreaTriggerSeconds = 0.25;

    private RealmSession _session = null!;
    private CreatureManager _creatures = null!;
    private TileManager _tiles = null!;
    private CreatureDisplayTable _displays = null!;
    private Node3D _player = null!;
    private IPlayer _iplayer = null!;
    private Dictionary<int, FactionTemplateRecord> _factions = [];
    private List<AreaTriggerRecord> _triggers = [];
    private readonly HashSet<int> _insideTriggers = [];
    private double _sinceTriggers;
    private double _sinceReport = ReportSeconds - 3;
    private int _moves;
    private Vector3 _lastFeet;
    private bool _hasLastFeet;
    private double _offGround;
    private bool _wasFalling;

    public int Creatures { get; private set; }

    public RealmSession Session => _session;

    /// <summary>The player's own stats, once the server has sent them.</summary>
    public PlayerStats? Self { get; private set; }

    /// <summary>Every unit the server shows, by GUID.</summary>
    public Dictionary<ulong, UnitStats> Units { get; } = [];

    /// <summary>The server's game objects, by GUID.</summary>
    public Dictionary<ulong, GameObjectInfo> GameObjects { get; } = [];

    /// <summary>The selected unit, or 0.</summary>
    public ulong Target { get; private set; }

    public bool Attacking { get; private set; }

    public InventoryChanged? Inventory { get; private set; }

    /// <summary>Where the player's corpse lies while they're a ghost, in WoW coordinates.</summary>
    public System.Numerics.Vector3? Corpse { get; private set; }

    public PlayerSpeeds? Speeds { get; private set; }

    public bool CanFly { get; private set; }

    public bool Rooted { get; private set; }

    /// <summary>The server's other game objects, drawn as it shows them.</summary>
    public ServerObjects? Objects { get; set; }

    /// <summary>The ships and zeppelins, which carry the player and need their movement sent relative to them.</summary>
    public Transports? Transports { get; set; }

    /// <summary>The body the player stands in, whose facing goes to the server in VR.</summary>
    public PlayerAvatar? Avatar { get; set; }

    public event Action<ChatLine>? ChatLine;

    /// <summary>A message for the middle of the screen; true for an error.</summary>
    public event Action<string, bool>? ScreenText;

    /// <summary>An NPC window opened or closed: gossip, quest, vendor, spirit healer or resurrect.</summary>
    public event Action<SessionEvent>? Dialog;

    public event Action? StatsChanged;

    public event Action? InventoryUpdated;

    public event Action? TargetChanged;

    /// <summary>The server is moving the player to another map, which needs the world loaded again.</summary>
    public event Action<WorldChanged>? WorldChanging;

    public void Start(RealmSession session, CreatureManager creatures, TileManager tiles, CreatureDisplayTable displays, Node3D player, Dictionary<int, FactionTemplateRecord> factions, List<AreaTriggerRecord> triggers)
    {
        _session = session;
        _creatures = creatures;
        _tiles = tiles;
        _displays = displays;
        _player = player;
        _iplayer = (IPlayer)player;
        _factions = factions;
        _triggers = triggers.FindAll(t => t.Map == session.MapId);
        Self = session.Self;

        // Arrived on a map the server moved the player to: it can put them there now.
        session.AckWorldPort();
    }

    public override void _Process(double delta)
    {
        if (_session is null || !_session.InWorld)
        {
            return;
        }

        for (var i = 0; i < MaxEventsPerFrame && _session.Events.TryDequeue(out var e); i++)
        {
            Apply(e);
            if (e is WorldChanged)
            {
                return;
            }
        }

        SendMovement(delta);
        CheckAreaTriggers(delta);

        _sinceReport += delta;
        if (_sinceReport >= ReportSeconds)
        {
            _sinceReport = 0;
            var (_, x, y, z) = _tiles.PlayerLocation;
            GD.Print($"Server: {Creatures} creatures in view, {_creatures.NodeCount} placed, {Objects?.Shown ?? 0} objects ({Objects?.Count ?? 0} drawn), {_moves} moves since the last report; player at ({x:F0}, {y:F0}, {z:F0}); nearest {_creatures.NearestLive}");
            _moves = 0;
        }
    }

    public override void _ExitTree() => _session?.Dispose();

    /// <summary>Lets go of the session without closing it, for the character screen or the next map to carry on with it.</summary>
    public RealmSession? Release()
    {
        var session = _session;
        _session = null!;
        return session;
    }

    /// <summary>Selects a unit, or nothing with 0, and tells the server.</summary>
    public void SetTarget(ulong guid)
    {
        if (guid == Target)
        {
            return;
        }

        Target = guid;
        _session.Select(guid);
        TargetChanged?.Invoke();
    }

    public UnitStats? TargetStats => Target != 0 && Units.TryGetValue(Target, out var stats) ? stats : null;

    /// <summary>How the unit stands toward the player, from their factions.</summary>
    public Reaction ReactionTo(UnitStats unit)
    {
        if (Self is not { } self || !_factions.TryGetValue((int)self.Unit.Faction, out var mine) || !_factions.TryGetValue((int)unit.Faction, out var theirs))
        {
            return unit.IsNpc ? Reaction.Friendly : Reaction.Neutral;
        }

        if (theirs.IsHostileTo(mine) || mine.IsHostileTo(theirs))
        {
            return Reaction.Hostile;
        }

        return theirs.IsFriendlyTo(mine) ? Reaction.Friendly : Reaction.Neutral;
    }

    /// <summary>
    /// What right-clicking a unit does in WoW: talk to an NPC, loot a corpse, or attack what can be attacked.
    /// </summary>
    public void Use(ulong guid)
    {
        if (!Units.TryGetValue(guid, out var unit))
        {
            return;
        }

        SetTarget(guid);
        if (unit.IsDead)
        {
            if (unit.Lootable)
            {
                _session.Loot(guid);
            }

            return;
        }

        if (unit.IsNpc && ReactionTo(unit) != Reaction.Hostile)
        {
            _session.Interact(unit);
            return;
        }

        if (unit.Type == ObjectType.Unit && !unit.NotAttackable && ReactionTo(unit) != Reaction.Friendly)
        {
            StartAttack(guid);
        }
    }

    public void StartAttack(ulong guid)
    {
        SetTarget(guid);
        _session.Attack(guid);
    }

    public void StopAttack()
    {
        _session.StopAttack();
        Attacking = false;
    }

    public void UseGameObject(ulong guid) => _session.UseGameObject(guid);

    public void Say(string text) => _session.Say(text);

    private void Apply(SessionEvent e)
    {
        switch (e)
        {
            case UnitAppeared { Unit: var unit }:
                _creatures.AddLive(ToSpawn(unit), new ServerMotion(unit.Path, _creatures.Clock - unit.PathElapsed));
                Creatures++;
                if (unit.Stats is { } stats)
                {
                    SetUnit(stats);
                }

                break;
            case PlayerLookChanged changed:
                _creatures.Redress((long)changed.Guid, AppearanceOf(changed.Look));
                break;
            case UnitMoved moved:
                _creatures.SetLiveMotion((long)moved.Guid, new ServerMotion(moved.Path, _creatures.Clock - moved.PathElapsed));
                _moves++;
                break;
            case UnitGone gone:
                _creatures.RemoveLive((long)gone.Guid);
                Units.Remove(gone.Guid);
                Creatures--;
                if (gone.Guid == Target)
                {
                    SetTarget(0);
                }

                break;
            case UnitChanged changed:
                SetUnit(changed.Stats);
                break;
            case SelfChanged self:
                var wasDead = Self?.Unit.IsDead == true || Self?.IsGhost == true;
                Self = self.Stats;
                if (wasDead && !self.Stats.Unit.IsDead && !self.Stats.IsGhost)
                {
                    Corpse = null;
                }

                StatsChanged?.Invoke();
                break;
            case ChatReceived chat:
                ChatLine?.Invoke(chat.Line);
                break;
            case ScreenMessage message:
                ScreenText?.Invoke(message.Text, message.Error);
                if (message.Error)
                {
                    ChatLine?.Invoke(new ChatLine(ChatKind.Error, "", message.Text));
                }

                break;
            case Teleported teleported:
                TeleportPlayer(teleported.Position, teleported.Orientation);
                break;
            case WorldChanged changed:
                WorldChanging?.Invoke(changed);
                break;
            case SpeedChanged speeds:
                Speeds = speeds.Speeds;
                if (_player is DesktopPlayer runner)
                {
                    runner.RunSpeed = speeds.Speeds.Run;
                    runner.RunBackSpeed = speeds.Speeds.RunBack;
                }

                break;
            case CanFlyChanged fly:
                CanFly = fly.CanFly;
                if (!fly.CanFly)
                {
                    _iplayer.Flying = false;
                }

                break;
            case RootChanged root:
                Rooted = root.Rooted;
                if (_player is DesktopPlayer held)
                {
                    held.Rooted = root.Rooted;
                }

                break;
            case InventoryChanged inventory:
                Inventory = inventory;
                InventoryUpdated?.Invoke();
                break;
            case ItemInfoKnown:
                InventoryUpdated?.Invoke();
                break;
            case AttackChanged attack:
                Attacking = attack.Attacking;
                StatsChanged?.Invoke();
                break;
            case SwingHappened swing:
                if (swing.Attacker != _session.SelfGuid)
                {
                    _creatures.PlayAttack((long)swing.Attacker);
                }

                break;
            case GameObjectAppeared appeared:
                GameObjects[appeared.Object.Guid] = appeared.Object;
                if (appeared.Object.Type == Transports.MapObjectTransport)
                {
                    if (Transports?.Has(appeared.Object.Guid) == false)
                    {
                        Transports.Add(appeared.Object);
                    }
                }
                else
                {
                    Objects?.Show(appeared.Object);
                }

                break;
            case GameObjectGone gone:
                GameObjects.Remove(gone.Guid);
                Transports?.Remove(gone.Guid);
                Objects?.Hide(gone.Guid);
                break;
            case BoardedTransport boarded:
                GD.Print($"The server has the player aboard transport {boarded.Transport:X} at {boarded.Offset}");
                Transports?.Board(boarded.Transport, boarded.Offset);
                break;
            case CorpseAt corpse:
                Corpse = corpse.Position;
                break;
            case GossipShown or GossipClosed or QuestShown or VendorShown or SpiritHealerOffer or ResurrectOffer:
                Dialog?.Invoke(e);
                break;
            case SessionClosed closed:
                GD.PushWarning($"The server closed the session: {closed.Reason}. Creatures stay as they were.");
                ScreenText?.Invoke($"Disconnected: {closed.Reason}", true);
                break;
        }
    }

    private void SetUnit(UnitStats stats)
    {
        Units[stats.Guid] = stats;
        _creatures.SetLiveState((long)stats.Guid, stats.IsDead, stats.Guid == Target);
        if (stats.Guid == Target)
        {
            TargetChanged?.Invoke();
        }
    }

    /// <summary>Puts the player where the server says, behind the loading screen when it's far.</summary>
    private void TeleportPlayer(System.Numerics.Vector3 position, float orientation)
    {
        var (_, x, y, z) = _tiles.PlayerLocation;
        var distance = new System.Numerics.Vector3((float)x, (float)y, z) - position;
        GD.Print($"The server teleported the player {distance.Length():F0} yards, to ({position.X:F1}, {position.Y:F1}, {position.Z:F1})");
        _iplayer.Flying = false;
        if (_player is DesktopPlayer desktop)
        {
            desktop.RotationDegrees = new Vector3(0, Mathf.RadToDeg(WowCoords.CameraYawFromOrientation(orientation)), 0);
        }

        void Go() => _tiles.TeleportTo(position.X, position.Y, position.Z);
        if (distance.Length() > FarTeleportYards && GetParent().GetNodeOrNull<Ui.LoadingScreen>("LoadingScreen") is { } loading)
        {
            loading.Cover("", Go);
        }
        else
        {
            Go();
        }

        _hasLastFeet = false;
    }

    private CreatureSpawn ToSpawn(UnitInfo unit)
    {
        var (position, facing, _) = unit.Path.At(unit.PathElapsed);
        return new CreatureSpawn
        {
            Guid = (long)unit.Guid,
            Entry = (int)unit.Entry,
            Name = unit.Name,
            DisplayId = (int)unit.DisplayId,
            Pos = [position.X, position.Y, position.Z],
            Orientation = facing,
            Scale = unit.Scale,
            WalkSpeed = unit.Path.Speed > 0f ? unit.Path.Speed : CreatureMotion.WalkSpeed,
            Weapons = [_displays.ItemDisplayOf((int)unit.Items[0]), _displays.ItemDisplayOf((int)unit.Items[1])],
            Character = unit.Look is { } look ? AppearanceOf(look) : null,
        };
    }

    /// <summary>Another player as a character to paint, dressed from Item.dbc as the client does.</summary>
    private CharacterAppearance AppearanceOf(PlayerLook look)
    {
        var (equipment, types) = _displays.DressFromItems(look.Items.Select(i => (int)i).ToArray());
        return new CharacterAppearance(look.Race, look.Sex, look.Skin, look.Face, look.HairStyle, look.HairColor, look.FacialHair, equipment)
        {
            InventoryTypes = types,
            HideHelm = look.HidesHelm,
            HideCloak = look.HidesCloak,
        };
    }

    /// <summary>The way the player faces, as a WoW orientation: the desktop body's, or the VR body's.</summary>
    private float Facing => _player is DesktopPlayer desktop
        ? WowCoords.OrientationFromCameraYaw(desktop.Rotation.Y)
        : Avatar?.Facing ?? 0f;

    /// <summary>Movement flags from how the player moves against the way they face, sent every frame for the session to pace.</summary>
    private void SendMovement(double delta)
    {
        if (_tiles.PlacingPlayer || !_player.IsInsideTree() || delta <= 0)
        {
            _hasLastFeet = false;
            return;
        }

        var feet = _iplayer.Feet;
        var velocity = _hasLastFeet ? (feet - _lastFeet) / (float)delta : Vector3.Zero;
        _lastFeet = feet;
        _hasLastFeet = true;

        if (Transports?.Riding is not null)
        {
            velocity -= Transports.DeckVelocity;
        }

        var facing = Facing;
        var forward = new Vector3(-Mathf.Sin(facing), 0, -Mathf.Cos(facing));
        var left = new Vector3(-Mathf.Cos(facing), 0, Mathf.Sin(facing));
        var ahead = velocity.Dot(forward);
        var side = velocity.Dot(left);
        var flags = MoveFlags.None;
        if (ahead > MovingSpeed)
        {
            flags |= MoveFlags.Forward;
        }
        else if (ahead < -MovingSpeed)
        {
            flags |= MoveFlags.Backward;
        }

        if (side > MovingSpeed)
        {
            flags |= MoveFlags.StrafeLeft;
        }
        else if (side < -MovingSpeed)
        {
            flags |= MoveFlags.StrafeRight;
        }

        var swimming = _player is DesktopPlayer d ? d.Swimming : false;
        if (_iplayer.Flying)
        {
            flags |= MoveFlags.Flying | MoveFlags.CanFly;
        }
        else if (swimming)
        {
            flags |= MoveFlags.Swimming;
        }

        if (CanFly)
        {
            flags |= MoveFlags.CanFly;
        }

        _offGround = _iplayer.OnGround || _iplayer.Flying || swimming ? 0 : _offGround + delta;
        var falling = _offGround > FallingSeconds;
        if (falling)
        {
            flags |= MoveFlags.Falling;
        }

        var (_, x, y, z) = _tiles.PlayerLocation;
        var move = new MovementInfo
        {
            Flags = flags,
            Position = new System.Numerics.Vector3((float)x, (float)y, z),
            Orientation = WowCoords.NormalizeOrientation(facing),
            FallTime = falling ? (uint)(_offGround * 1000) : 0,
            JumpVerticalSpeed = falling ? -velocity.Y : 0,
        };
        if (Transports?.RideOffset(move.Position, move.Orientation) is { } ride)
        {
            move.Flags |= MoveFlags.OnTransport;
            move.TransportGuid = ride.Guid;
            move.TransportOffset = ride.Offset;
            move.TransportOrientation = ride.Orientation;
            move.TransportTime = (uint)Time.GetTicksMsec();
        }

        _wasFalling = falling;
        _session.UpdateMovement(move);
    }

    /// <summary>Walking into an instance's entrance, or out of its exit, tells the server, which moves the player.</summary>
    private void CheckAreaTriggers(double delta)
    {
        _sinceTriggers += delta;
        if (_sinceTriggers < AreaTriggerSeconds || _tiles.PlacingPlayer || _triggers.Count == 0)
        {
            return;
        }

        _sinceTriggers = 0;
        var (_, x, y, z) = _tiles.PlayerLocation;
        var here = new System.Numerics.Vector3((float)x, (float)y, z + 1f);
        foreach (var trigger in _triggers)
        {
            var inside = trigger.Contains(here);
            if (inside && _insideTriggers.Add(trigger.Id))
            {
                GD.Print($"Area trigger {trigger.Id}");
                _session.EnterAreaTrigger((uint)trigger.Id);
            }
            else if (!inside)
            {
                _insideTriggers.Remove(trigger.Id);
            }
        }
    }
}
