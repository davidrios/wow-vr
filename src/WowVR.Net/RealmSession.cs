using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace WowVR.Net;

public sealed record CharacterInfo(ulong Guid, string Name, byte Level, uint Zone, uint Map, Vector3 Position);

/// <summary>A creature as the game needs it. Items are item ids, for the right hand, left hand and ranged slot.</summary>
public sealed record UnitInfo(ulong Guid, uint Entry, string Name, uint DisplayId, float Scale, uint[] Items, MovePath Path, double PathElapsed);

public abstract record SessionEvent;

public sealed record UnitAppeared(UnitInfo Unit) : SessionEvent;

/// <summary>A unit set off on a new path, or stopped, which is a path of one point.</summary>
public sealed record UnitMoved(ulong Guid, MovePath Path, double PathElapsed) : SessionEvent;

public sealed record UnitGone(ulong Guid) : SessionEvent;

public sealed record SessionClosed(string Reason) : SessionEvent;

/// <summary>
/// A player's session on an AzerothCore server: logs in, enters the world with a character,
/// then reads on its own thread. What it learns waits in <see cref="Events"/> for the game to
/// take on its own thread, and the game says where the player went with <see cref="SendPosition"/>.
/// </summary>
public sealed class RealmSession : IDisposable
{
    /// <summary>A unit waits this long for its name before it's shown without one.</summary>
    private static readonly TimeSpan NameWait = TimeSpan.FromSeconds(2);

    private readonly WorldConnection _world;
    private readonly ObjectTracker _objects = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, string> _creatureNames = [];
    private readonly HashSet<uint> _askedNames = [];
    private readonly Dictionary<ulong, (TrackedObject Unit, TimeSpan Since)> _waitingForName = [];
    private readonly HashSet<ulong> _announced = [];
    private readonly Action<string> _log;
    private uint _pingSerial;

    private RealmSession(WorldConnection world, CharacterInfo character, Action<string> log)
    {
        _world = world;
        Character = character;
        _log = log;
        _objects.Created += OnCreated;
        _objects.Moved += OnMoved;
        _objects.Removed += OnRemoved;
    }

    public CharacterInfo Character { get; }

    public uint MapId { get; private set; }

    /// <summary>Where the server put the character, in WoW coordinates.</summary>
    public Vector3 StartPosition { get; private set; }

    public float StartOrientation { get; private set; }

    /// <summary>Hours since midnight on the server's clock when the character entered, from 0 to 24.</summary>
    public double? ServerTimeOfDay { get; private set; }

    public ConcurrentQueue<SessionEvent> Events { get; } = new();

    public bool IsOpen => !_stop.IsCancellationRequested;

    /// <summary>
    /// Logs in and enters the world with <paramref name="characterName"/>, or the account's first
    /// character, and returns once the server says where it is.
    /// </summary>
    public static async Task<RealmSession> OpenAsync(string host, string account, string password, string? characterName, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        var (auth, realms) = await AuthClient.LoginAsync(host, 3724, account, password);
        var realm = realms.FirstOrDefault() ?? throw new IOException("The server lists no realms.");
        var (worldHost, worldPort) = SplitAddress(realm.Address);

        // A realm set up on the server's own machine gives an address only it can reach.
        if (worldHost is "127.0.0.1" or "localhost")
        {
            worldHost = host;
        }

        var world = new WorldConnection();
        try
        {
            await world.ConnectAsync(worldHost, worldPort, auth.Account, auth.SessionKey, realm.Id);
            var response = await WaitFor(world, Opcodes.SMSG_AUTH_RESPONSE, ct);
            var code = response.Data[0];
            if (code == 27)
            {
                throw new IOException($"The realm has a queue: position {BitConverter.ToUInt32(response.Data, 11)}.");
            }

            if (code != 12)
            {
                throw new IOException($"The realm refused the session (code {code}).");
            }

            var characters = await ListCharacters(world, ct);
            var character = (characterName is null
                ? characters.FirstOrDefault()
                : characters.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                ?? throw new IOException(characterName is null
                    ? $"Account {auth.Account} has no characters on {realm.Name}."
                    : $"Account {auth.Account} has no character named {characterName} on {realm.Name}.");
            log($"Entering {realm.Name} as {character.Name}");

            await world.SendAsync(Opcodes.CMSG_PLAYER_LOGIN, new PacketWriter().U64(character.Guid).ToArray());
            var session = new RealmSession(world, character, log);
            await session.ReadUntilInWorld(ct);
            _ = Task.Run(session.ReadLoop);
            _ = Task.Run(session.PingLoop);
            return session;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }

    public static async Task<List<CharacterInfo>> ListCharacters(WorldConnection world, CancellationToken ct = default)
    {
        await world.SendAsync(Opcodes.CMSG_CHAR_ENUM, []);
        var r = (await WaitFor(world, Opcodes.SMSG_CHAR_ENUM, ct)).Reader();
        int count = r.U8();
        var list = new List<CharacterInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var guid = r.U64();
            var name = r.CString();
            r.Skip(8); // race, class, sex, skin, face, hair style and color, facial hair
            var level = r.U8();
            var zone = r.U32();
            var map = r.U32();
            var position = new Vector3(r.F32(), r.F32(), r.F32());
            r.Skip(4 + 4 + 4 + 1 + 12); // guild, flags, customization, first login, pet
            r.Skip(23 * 9); // equipment and bags
            list.Add(new CharacterInfo(guid, name, level, zone, map, position));
        }

        return list;
    }

    /// <summary>Tells the server where the player is, in WoW coordinates, so it shows what's around them there.</summary>
    public void SendPosition(Vector3 position, float orientation)
    {
        if (!IsOpen)
        {
            return;
        }

        var w = new PacketWriter();
        WritePackedGuid(w, Character.Guid);
        w.U32(0).U16(0).U32((uint)_clock.ElapsedMilliseconds)
            .F32(position.X).F32(position.Y).F32(position.Z).F32(orientation)
            .U32(0); // fall time
        _ = SendQuietly(Opcodes.MSG_MOVE_HEARTBEAT, w.ToArray());
    }

    public void Dispose()
    {
        if (!_stop.IsCancellationRequested)
        {
            _stop.Cancel();
            try
            {
                _world.SendAsync(Opcodes.CMSG_LOGOUT_REQUEST, []).Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception e) when (e is IOException or AggregateException or ObjectDisposedException)
            {
            }
        }

        _world.Dispose();
    }

    private async Task ReadUntilInWorld(CancellationToken ct)
    {
        while (true)
        {
            var p = await _world.ReceiveAsync(ct);
            Handle(p);
            if (p.Opcode == Opcodes.SMSG_CHARACTER_LOGIN_FAILED)
            {
                throw new IOException($"The server wouldn't let {Character.Name} in (code {p.Data[0]}).");
            }

            if (p.Opcode == Opcodes.SMSG_LOGIN_VERIFY_WORLD)
            {
                var r = p.Reader();
                MapId = r.U32();
                StartPosition = new Vector3(r.F32(), r.F32(), r.F32());
                StartOrientation = r.F32();
                return;
            }
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
        Events.Enqueue(new SessionClosed(reason));
    }

    /// <summary>The server drops a quiet connection, and pings keep it from seeming so.</summary>
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
        }
    }

    /// <summary>The server's packed date: minutes in bits 0 to 5, hours in 6 to 10.</summary>
    internal static double TimeOfDay(uint packed) => ((packed >> 6) & 0x1F) + (packed & 0x3F) / 60.0;

    private void OnCreated(TrackedObject obj)
    {
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
        if (_announced.Remove(obj.Guid))
        {
            Events.Enqueue(new UnitGone(obj.Guid));
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
        if (!_announced.Add(obj.Guid))
        {
            return;
        }

        var items = new uint[3];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = obj.Field(UpdateFields.UnitVirtualItems + i);
        }

        var name = _creatureNames.GetValueOrDefault(obj.Entry, "");
        Events.Enqueue(new UnitAppeared(new UnitInfo(obj.Guid, obj.Entry, name, obj.DisplayId, obj.Scale, items, obj.Path!, obj.PathElapsed)));
    }

    private static void WritePackedGuid(PacketWriter w, ulong guid)
    {
        byte mask = 0;
        Span<byte> bytes = stackalloc byte[8];
        var count = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = (byte)(guid >> (i * 8));
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                bytes[count++] = b;
            }
        }

        w.U8(mask).Bytes(bytes[..count]);
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
