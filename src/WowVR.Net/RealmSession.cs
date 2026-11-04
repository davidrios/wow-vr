using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace WowVR.Net;

/// <summary>What a character wears in one equipment slot, as the character list gives it.</summary>
public readonly record struct EquippedItem(int DisplayId, byte InventoryType);

/// <summary>A character on the account, with what's needed to draw it.</summary>
public sealed record CharacterInfo(
    ulong Guid,
    string Name,
    byte Level,
    uint Zone,
    uint Map,
    Vector3 Position,
    byte Race = 0,
    byte Class = 0,
    byte Sex = 0,
    byte Skin = 0,
    byte Face = 0,
    byte HairStyle = 0,
    byte HairColor = 0,
    byte FacialHair = 0,
    uint Flags = 0,
    bool FirstLogin = false)
{
    /// <summary>The 19 equipment slots, head first, in the server's order.</summary>
    public EquippedItem[] Equipment { get; init; } = new EquippedItem[19];

    public bool HidesHelm => (Flags & 0x400) != 0;

    public bool HidesCloak => (Flags & 0x800) != 0;

    public bool IsGhost => (Flags & 0x2000) != 0;
}

/// <summary>What a new character looks like. The server checks every choice against its own rules.</summary>
public sealed record CharacterCreation(string Name, byte Race, byte Class, byte Sex, byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialHair);

/// <summary>A creature or another player as the game needs it. Items are item ids, for the right hand, left hand and ranged slot.</summary>
public sealed record UnitInfo(ulong Guid, uint Entry, string Name, uint DisplayId, float Scale, uint[] Items, MovePath Path, double PathElapsed)
{
    public ObjectType Type { get; init; } = ObjectType.Unit;

    /// <summary>Its health, level and so on when it appeared.</summary>
    public UnitStats? Stats { get; init; }
}

public abstract record SessionEvent;

public sealed record UnitAppeared(UnitInfo Unit) : SessionEvent;

/// <summary>A unit set off on a new path, or stopped, which is a path of one point.</summary>
public sealed record UnitMoved(ulong Guid, MovePath Path, double PathElapsed) : SessionEvent;

public sealed record UnitGone(ulong Guid) : SessionEvent;

public sealed record SessionClosed(string Reason) : SessionEvent;

/// <summary>The server's answer to logging out: refused, with why, or started, at once or after its wait.</summary>
public sealed record LogoutAnswer(string? Refused, bool Instant);

/// <summary>
/// A player's session on an AzerothCore realm. It connects to the realm, lists, makes and
/// deletes characters, then enters the world with one, after which it reads on its own
/// thread: what it learns waits in <see cref="Events"/> for the game to take on its own
/// thread, and the game says where the player went with <see cref="SendPosition"/>.
/// </summary>
public sealed partial class RealmSession : IDisposable
{
    public const int MaxCharacters = 10;

    /// <summary>A unit waits this long for its name before it's shown without one.</summary>
    private static readonly TimeSpan NameWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(20);

    private readonly WorldConnection _world;
    private readonly ObjectTracker _objects = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _request = new(1, 1);
    private readonly Dictionary<uint, string> _creatureNames = [];
    private readonly HashSet<uint> _askedNames = [];
    private readonly Dictionary<ulong, (TrackedObject Unit, TimeSpan Since)> _waitingForName = [];
    private readonly HashSet<ulong> _announced = [];
    private readonly Action<string> _log;
    private uint _pingSerial;
    private bool _inWorld;
    private Task? _reader;
    private TaskCompletionSource<LogoutAnswer>? _logoutAnswer;
    private TaskCompletionSource? _loggedOut;

    private RealmSession(WorldConnection world, Realm realm, string account, Action<string> log)
    {
        _world = world;
        Realm = realm;
        Account = account;
        _log = log;
        _objects.Created += OnCreated;
        _objects.Moved += OnMoved;
        _objects.Removed += OnRemoved;
        _objects.ValuesChanged += OnValuesChanged;
    }

    public Realm Realm { get; }

    public string Account { get; }

    /// <summary>Who's in the world, once <see cref="EnterWorldAsync"/> has returned.</summary>
    public CharacterInfo Character { get; private set; } = null!;

    public uint MapId { get; private set; }

    /// <summary>Where the server put the character, in WoW coordinates.</summary>
    public Vector3 StartPosition { get; private set; }

    public float StartOrientation { get; private set; }

    /// <summary>Hours since midnight on the server's clock when the character entered, from 0 to 24.</summary>
    public double? ServerTimeOfDay { get; private set; }

    public ConcurrentQueue<SessionEvent> Events { get; } = new();

    public bool IsOpen => !_stop.IsCancellationRequested;

    /// <summary>Opens a realm with an account the authserver has let in.</summary>
    public static async Task<RealmSession> ConnectAsync(string authHost, AuthClient auth, Realm realm, Action<string>? log = null, CancellationToken ct = default)
    {
        var (host, port) = SplitAddress(realm.Address);

        // A realm set up on the server's own machine gives an address only it can reach.
        if (host is "127.0.0.1" or "localhost")
        {
            host = authHost;
        }

        var world = new WorldConnection();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReplyTimeout);
            await world.ConnectAsync(host, port, auth.Account, auth.SessionKey, realm.Id, timeout.Token);
            var response = await WaitFor(world, Opcodes.SMSG_AUTH_RESPONSE, timeout.Token);
            var code = response.Data[0];
            if (code == 27)
            {
                throw new IOException($"{realm.Name} has a queue: you're number {BitConverter.ToUInt32(response.Data, 11)}.");
            }

            if (code != 12)
            {
                throw new IOException($"{realm.Name} refused the session (code {code}).");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            world.Dispose();
            throw new IOException($"{realm.Name} didn't answer.");
        }
        catch
        {
            world.Dispose();
            throw;
        }

        var session = new RealmSession(world, realm, auth.Account, log ?? (_ => { }));
        _ = Task.Run(session.PingLoop);
        return session;
    }

    /// <summary>
    /// Logs in, connects to the first realm and enters the world with <paramref name="characterName"/>,
    /// or the account's first character, in one go. For <c>--server=</c> and tools.
    /// </summary>
    public static async Task<RealmSession> OpenAsync(string host, string account, string password, string? characterName, Action<string>? log = null, CancellationToken ct = default)
    {
        var (auth, realms) = await AuthClient.LoginAsync(host, 3724, account, password, ct);
        var realm = realms.FirstOrDefault() ?? throw new IOException("The server lists no realms.");
        var session = await ConnectAsync(host, auth, realm, log, ct);
        try
        {
            var characters = await session.ListCharactersAsync(ct);
            var character = (characterName is null
                ? characters.FirstOrDefault()
                : characters.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                ?? throw new IOException(characterName is null
                    ? $"Account {auth.Account} has no characters on {realm.Name}."
                    : $"Account {auth.Account} has no character named {characterName} on {realm.Name}.");
            await session.EnterWorldAsync(character, ct);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task<List<CharacterInfo>> ListCharactersAsync(CancellationToken ct = default)
    {
        var p = await RequestAsync(Opcodes.CMSG_CHAR_ENUM, [], Opcodes.SMSG_CHAR_ENUM, ct);
        return ReadCharacterList(p.Data);
    }

    /// <summary>Null when the server made it, or why it didn't, as a sentence to show.</summary>
    public async Task<string?> CreateCharacterAsync(CharacterCreation c, CancellationToken ct = default)
    {
        var payload = new PacketWriter().CString(c.Name)
            .U8(c.Race).U8(c.Class).U8(c.Sex).U8(c.Skin).U8(c.Face).U8(c.HairStyle).U8(c.HairColor).U8(c.FacialHair)
            .U8(0) // outfit
            .ToArray();
        var p = await RequestAsync(Opcodes.CMSG_CHAR_CREATE, payload, Opcodes.SMSG_CHAR_CREATE, ct);
        return p.Data[0] == 0x2F ? null : ResponseCodes.Describe(p.Data[0]);
    }

    /// <summary>Null when the server deleted it, or why it didn't.</summary>
    public async Task<string?> DeleteCharacterAsync(CharacterInfo character, CancellationToken ct = default)
    {
        var p = await RequestAsync(Opcodes.CMSG_CHAR_DELETE, new PacketWriter().U64(character.Guid).ToArray(), Opcodes.SMSG_CHAR_DELETE, ct);
        return p.Data[0] == 0x47 ? null : ResponseCodes.Describe(p.Data[0]);
    }

    /// <summary>Enters the world and returns once the server says where the character is. Reading goes on its own thread after.</summary>
    public async Task EnterWorldAsync(CharacterInfo character, CancellationToken ct = default)
    {
        await _request.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(ReplyTimeout);
            Character = character;
            _log($"Entering {Realm.Name} as {character.Name}");
            await _world.SendAsync(Opcodes.CMSG_PLAYER_LOGIN, new PacketWriter().U64(character.Guid).ToArray());
            while (true)
            {
                var p = await _world.ReceiveAsync(timeout.Token);
                Handle(p);
                if (p.Opcode == Opcodes.SMSG_CHARACTER_LOGIN_FAILED)
                {
                    throw new IOException($"The server wouldn't let {character.Name} in: {ResponseCodes.Describe(p.Data[0])}");
                }

                if (p.Opcode == Opcodes.SMSG_LOGIN_VERIFY_WORLD)
                {
                    var r = p.Reader();
                    MapId = r.U32();
                    StartPosition = new Vector3(r.F32(), r.F32(), r.F32());
                    StartOrientation = r.F32();
                    break;
                }
            }

            _inWorld = true;
            _reader = Task.Run(ReadLoop);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{Realm.Name} didn't let {character.Name} in in time.");
        }
        finally
        {
            _request.Release();
        }
    }

    public static List<CharacterInfo> ReadCharacterList(byte[] data)
    {
        var r = new PacketReader(data);
        int count = r.U8();
        var list = new List<CharacterInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var guid = r.U64();
            var name = r.CString();
            var (race, cls, sex, skin, face, hairStyle, hairColor, facialHair) = (r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8());
            var level = r.U8();
            var zone = r.U32();
            var map = r.U32();
            var position = new Vector3(r.F32(), r.F32(), r.F32());
            r.U32(); // guild
            var flags = r.U32();
            r.U32(); // customization
            var firstLogin = r.U8() != 0;
            r.Skip(12); // pet
            var equipment = new EquippedItem[19];
            for (var slot = 0; slot < 23; slot++)
            {
                var display = (int)r.U32();
                var type = r.U8();
                r.U32(); // enchant aura
                if (slot < equipment.Length)
                {
                    equipment[slot] = new EquippedItem(display, type);
                }
            }

            list.Add(new CharacterInfo(guid, name, level, zone, map, position, race, cls, sex, skin, face, hairStyle, hairColor, facialHair, flags, firstLogin) { Equipment = equipment });
        }

        return list;
    }

    public bool InWorld => _inWorld;

    /// <summary>
    /// Asks to leave the world for the character list. The server says at once whether it will, and
    /// when it lets the character go, at once in an inn or city or after 20 seconds,
    /// <see cref="WaitForLogoutAsync"/> returns with the session back at the list.
    /// </summary>
    public async Task<LogoutAnswer> RequestLogoutAsync(CancellationToken ct = default)
    {
        if (!_inWorld)
        {
            return new LogoutAnswer(null, true);
        }

        _loggedOut ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _logoutAnswer = new TaskCompletionSource<LogoutAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendQuietly(Opcodes.CMSG_LOGOUT_REQUEST, []);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        timeout.CancelAfter(ReplyTimeout);
        try
        {
            return await _logoutAnswer.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{Realm.Name} didn't answer.");
        }
    }

    /// <summary>Stops a logout that's waiting out its 20 seconds.</summary>
    public void CancelLogout() => _ = SendQuietly(Opcodes.CMSG_LOGOUT_CANCEL, []);

    /// <summary>Returns once the character has left the world and the character list can be asked for again.</summary>
    public async Task WaitForLogoutAsync(CancellationToken ct = default)
    {
        if (_loggedOut is { } loggedOut)
        {
            await loggedOut.Task.WaitAsync(ct);
        }

        if (_reader is { } reader)
        {
            await reader.WaitAsync(ct);
        }
    }

    /// <summary>Tells the server where the player is, in WoW coordinates, so it shows what's around them there.</summary>
    public void SendPosition(Vector3 position, float orientation) =>
        UpdateMovement(new MovementInfo { Position = position, Orientation = orientation });

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            _stop.Cancel();
            try
            {
                if (_inWorld)
                {
                    _world.SendAsync(Opcodes.CMSG_LOGOUT_REQUEST, []).Wait(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception e) when (e is IOException or AggregateException or ObjectDisposedException)
            {
            }
        }

        _world.Dispose();
    }

    /// <summary>One question to the server at a time, before entering the world, with whatever else it sends skipped.</summary>
    private async Task<WorldPacket> RequestAsync(ushort opcode, byte[] payload, ushort reply, CancellationToken ct)
    {
        if (_inWorld)
        {
            throw new InvalidOperationException("The character list is only there before entering the world.");
        }

        await _request.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(ReplyTimeout);
            await _world.SendAsync(opcode, payload);
            return await WaitFor(_world, reply, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{Realm.Name} didn't answer.");
        }
        catch (Exception e) when (e is EndOfStreamException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            _stop.Cancel();
            throw new IOException($"Lost the connection to {Realm.Name}.");
        }
        finally
        {
            _request.Release();
        }
    }

    private async Task ReadLoop()
    {
        var reason = "closed";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var p = await _world.ReceiveAsync(_stop.Token);
                Handle(p);
                ReleaseUnnamed();
                SendInventoryIfChanged();

                // Back at the character list: what comes next is answers to its questions, read by them.
                if (p.Opcode == Opcodes.SMSG_LOGOUT_COMPLETE)
                {
                    LeftWorld();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or EndOfStreamException)
        {
            reason = e.Message;
        }

        _stop.Cancel();
        _logoutAnswer?.TrySetException(new IOException($"Lost the connection to {Realm.Name}."));
        _loggedOut?.TrySetException(new IOException($"Lost the connection to {Realm.Name}."));
        Events.Enqueue(new SessionClosed(reason));
    }

    /// <summary>Forgets the world the character was in, ready to enter again.</summary>
    private void LeftWorld()
    {
        _inWorld = false;
        _objects.Objects.Clear();
        _announced.Clear();
        _waitingForName.Clear();
        while (Events.TryDequeue(out _))
        {
        }

        _loggedOut?.TrySetResult();
        _loggedOut = null;
    }

    /// <summary>The server drops a quiet connection, and pings keep it from seeming so, on the character screen too.</summary>
    private async Task PingLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SendQuietly(Opcodes.CMSG_PING, new PacketWriter().U32(_pingSerial++).U32(0).ToArray());
        }
    }

    private async Task SendQuietly(ushort opcode, byte[] payload)
    {
        try
        {
            await _world.SendAsync(opcode, payload);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _stop.Cancel();
        }
    }

    private void Handle(WorldPacket p)
    {
        switch (p.Opcode)
        {
            case Opcodes.SMSG_UPDATE_OBJECT:
                _objects.ReadUpdate(p.Data);
                break;
            case Opcodes.SMSG_COMPRESSED_UPDATE_OBJECT:
                _objects.ReadCompressedUpdate(p.Data);
                break;
            case Opcodes.SMSG_DESTROY_OBJECT:
                _objects.ReadDestroy(p.Data);
                break;
            case Opcodes.SMSG_MONSTER_MOVE:
                _objects.ReadMonsterMove(p.Data);
                break;
            case Opcodes.SMSG_COMPRESSED_MOVES:
                _objects.ReadCompressedMoves(p.Data);
                break;
            case Opcodes.SMSG_TIME_SYNC_REQ:
                _ = SendQuietly(Opcodes.CMSG_TIME_SYNC_RESP,
                    new PacketWriter().U32(BitConverter.ToUInt32(p.Data)).U32((uint)_clock.ElapsedMilliseconds).ToArray());
                break;
            case Opcodes.SMSG_LOGIN_SETTIMESPEED:
                ServerTimeOfDay = TimeOfDay(new PacketReader(p.Data).U32());
                break;
            case Opcodes.SMSG_CREATURE_QUERY_RESPONSE:
                ReadCreatureName(p.Data);
                break;
            default:
                HandlePlay(p);
                break;
            case Opcodes.SMSG_LOGOUT_RESPONSE:
            {
                var r = new PacketReader(p.Data);
                var result = r.U32();
                var instant = r.U8() != 0;
                _logoutAnswer?.TrySetResult(new LogoutAnswer(result switch
                {
                    0 => null,
                    1 => "You can't log out while fighting.",
                    3 => "You can't log out while falling.",
                    _ => "You can't log out right now.",
                }, instant));
                break;
            }
        }
    }

    /// <summary>The server's packed date: minutes in bits 0 to 5, hours in 6 to 10.</summary>
    internal static double TimeOfDay(uint packed) => ((packed >> 6) & 0x1F) + (packed & 0x3F) / 60.0;

    private void OnCreated(TrackedObject obj)
    {
        if (obj.Guid == Character.Guid)
        {
            AnnounceSelf(obj);
            if ((obj.MoveFlags & MoveFlags.OnTransport) != 0 && obj.TransportGuid != 0)
            {
                Events.Enqueue(new BoardedTransport(obj.TransportGuid, obj.TransportOffset));
            }

            return;
        }

        switch (obj.Type)
        {
            case ObjectType.GameObject:
                OnGameObjectCreated(obj);
                return;
            case ObjectType.Item or ObjectType.Container:
                _inventoryDirty = true;
                return;
            case ObjectType.Corpse:
                if (obj.Field64(UpdateFields.CorpseOwner) == Character.Guid)
                {
                    Events.Enqueue(new CorpseAt(obj.Position, MapId));
                }

                return;
            case ObjectType.Player when obj.Path is not null:
                if (_playerNames.ContainsKey(obj.Guid))
                {
                    Announce(obj);
                }
                else
                {
                    AskPlayerName(obj.Guid, _ => Announce(obj));
                }

                return;
        }

        if (obj.Type != ObjectType.Unit || obj.Path is null)
        {
            return;
        }

        var entry = obj.Entry;
        if (_creatureNames.ContainsKey(entry))
        {
            Announce(obj);
            return;
        }

        _waitingForName[obj.Guid] = (obj, _clock.Elapsed);
        if (_askedNames.Add(entry))
        {
            _ = SendQuietly(Opcodes.CMSG_CREATURE_QUERY, new PacketWriter().U32(entry).U64(obj.Guid).ToArray());
        }
    }

    private void OnMoved(TrackedObject obj)
    {
        if (_announced.Contains(obj.Guid) && obj.Path is { } path)
        {
            Events.Enqueue(new UnitMoved(obj.Guid, path, obj.PathElapsed));
        }
    }

    private void OnRemoved(TrackedObject obj)
    {
        _waitingForName.Remove(obj.Guid);
        _waitingGameObjects.Remove(obj.Guid);
        if (_announced.Remove(obj.Guid))
        {
            Events.Enqueue(obj.Type == ObjectType.GameObject ? new GameObjectGone(obj.Guid) : new UnitGone(obj.Guid));
        }

        if (obj.Type is ObjectType.Item or ObjectType.Container)
        {
            _inventoryDirty = true;
        }
    }

    private void ReadCreatureName(byte[] data)
    {
        var r = new PacketReader(data);
        var entry = r.U32();
        var name = (entry & 0x80000000) == 0 ? r.CString() : "";
        entry &= 0x7FFFFFFF;
        _creatureNames[entry] = name;
        foreach (var (guid, waiting) in _waitingForName.Where(w => w.Value.Unit.Entry == entry).ToList())
        {
            _waitingForName.Remove(guid);
            Announce(waiting.Unit);
        }
    }

    private void ReleaseUnnamed()
    {
        if (_waitingForName.Count == 0)
        {
            return;
        }

        var now = _clock.Elapsed;
        foreach (var (guid, waiting) in _waitingForName.Where(w => now - w.Value.Since > NameWait).ToList())
        {
            _waitingForName.Remove(guid);
            Announce(waiting.Unit);
        }
    }

    private void Announce(TrackedObject obj)
    {
        if (!_objects.Objects.ContainsKey(obj.Guid) || !_announced.Add(obj.Guid))
        {
            return;
        }

        var items = new uint[3];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = obj.Field(UpdateFields.UnitVirtualItems + i);
        }

        var name = NameOf(obj);
        Events.Enqueue(new UnitAppeared(new UnitInfo(obj.Guid, obj.Entry, name, obj.DisplayId, obj.Scale, items, obj.Path!, obj.PathElapsed)
        {
            Type = obj.Type,
            Stats = UnitStats.From(obj, name),
        }));
    }

    private static (string Host, int Port) SplitAddress(string address)
    {
        var colon = address.LastIndexOf(':');
        return colon < 0 ? (address, 8085) : (address[..colon], int.Parse(address[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<WorldPacket> WaitFor(WorldConnection world, ushort opcode, CancellationToken ct)
    {
        while (true)
        {
            var p = await world.ReceiveAsync(ct);
            if (p.Opcode == opcode)
            {
                return p;
            }
        }
    }
}

/// <summary>The server's answers to making, deleting and entering with a character, as sentences.</summary>
public static class ResponseCodes
{
    public static string Describe(byte code) => code switch
    {
        0x30 or 0x31 => "The server couldn't make the character.",
        0x32 => "That name is taken.",
        0x33 => "Making characters is turned off on this realm.",
        0x34 => "You can't have characters on both sides on this realm.",
        0x35 => "The realm is full.",
        0x36 => "This account has as many characters as it can.",
        0x37 => "The realm has a queue for new characters.",
        0x38 => "Only existing players can make characters here.",
        0x39 or 0x3A => "This account doesn't have that expansion.",
        0x3B => "A Death Knight needs another character of level 55 on this realm.",
        0x3C => "This account already has a Death Knight on this realm.",
        0x3E => "That race and class don't go together.",
        0x48 => "The server couldn't delete the character.",
        0x4A => "A guild leader can't be deleted.",
        0x4B => "An arena team captain can't be deleted.",
        0x58 or 0x5C => "Names use letters only.",
        0x59 => "Enter a name.",
        0x5A => "That name is too short.",
        0x5B => "That name is too long.",
        0x5D => "Names can't mix alphabets.",
        0x5E => "That name isn't allowed.",
        0x5F => "That name is reserved.",
        0x60 or 0x61 => "Names can't have apostrophes there.",
        0x62 => "Names can't have three of the same letter in a row.",
        0x63 or 0x64 => "Names can't have spaces.",
        0x4E => "The world server isn't there right now.",
        0x4F => "That character is already in the world.",
        0x50 => "The character's map isn't there right now.",
        0x51 => "The character couldn't be loaded.",
        0x52 => "Entering the world is turned off on this realm.",
        0x53 => "The character wasn't found.",
        0x54 or 0x55 or 0x56 => "That character is locked.",
        _ => $"The server said no (code 0x{code:X2}).",
    };
}
