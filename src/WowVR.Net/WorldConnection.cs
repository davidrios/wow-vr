using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WowVR.Net;

public readonly record struct WorldPacket(ushort Opcode, byte[] Data)
{
    public PacketReader Reader() => new(Data);
}

/// <summary>
/// The worldserver connection: framing, the auth session, and the RC4 header
/// encryption that starts right after it.
/// </summary>
public sealed class WorldConnection : IDisposable
{
    readonly TcpClient _tcp = new();
    NetworkStream _stream = null!;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    Rc4? _decrypt;
    Rc4? _encrypt;

    public async Task ConnectAsync(string host, int port, string account, byte[] sessionKey, byte realmId)
    {
        await _tcp.ConnectAsync(host, port);
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();

        var challenge = await ReceiveAsync();
        if (challenge.Opcode != Opcodes.SMSG_AUTH_CHALLENGE)
            throw new IOException($"expected the auth challenge, got 0x{challenge.Opcode:X3}");
        var r = challenge.Reader();
        r.U32();
        var serverSeed = r.Bytes(4);

        var clientSeed = RandomNumberGenerator.GetBytes(4);
        var digest = SHA1.HashData([.. System.Text.Encoding.ASCII.GetBytes(account), 0, 0, 0, 0, .. clientSeed, .. serverSeed, .. sessionKey]);
        var session = new PacketWriter()
            .U32(12340).U32(0).CString(account).U32(0)
            .Bytes(clientSeed).U32(0).U32(0).U32(realmId).U64(0)
            .Bytes(digest)
            .U32(0) // no addon list
            .ToArray();
        await SendAsync(Opcodes.CMSG_AUTH_SESSION, session);

        // Server-to-client headers use the first seed, client-to-server the second.
        byte[] serverEncrypt = [0xCC, 0x98, 0xAE, 0x04, 0xE8, 0x97, 0xEA, 0xCA, 0x12, 0xDD, 0xC0, 0x93, 0x42, 0x91, 0x53, 0x57];
        byte[] serverDecrypt = [0xC2, 0xB3, 0x72, 0x3C, 0xC6, 0xAE, 0xD9, 0xB5, 0x34, 0x3C, 0x53, 0xEE, 0x2F, 0x43, 0x67, 0xCE];
        _decrypt = new Rc4(HMACSHA1.HashData(serverEncrypt, sessionKey));
        _encrypt = new Rc4(HMACSHA1.HashData(serverDecrypt, sessionKey));
    }

    public async Task<WorldPacket> ReceiveAsync(CancellationToken ct = default)
    {
        var first = new byte[1];
        await _stream.ReadExactlyAsync(first, ct);
        _decrypt?.Apply(first);
        bool large = (first[0] & 0x80) != 0;
        var rest = new byte[large ? 4 : 3];
        await _stream.ReadExactlyAsync(rest, ct);
        _decrypt?.Apply(rest);

        int size;
        ushort opcode;
        if (large)
        {
            size = ((first[0] & 0x7F) << 16) | (rest[0] << 8) | rest[1];
            opcode = (ushort)(rest[2] | (rest[3] << 8));
        }
        else
        {
            size = (first[0] << 8) | rest[0];
            opcode = (ushort)(rest[1] | (rest[2] << 8));
        }
        var data = new byte[size - 2];
        await _stream.ReadExactlyAsync(data, ct);
        return new WorldPacket(opcode, data);
    }

    public async Task SendAsync(ushort opcode, byte[] payload)
    {
        var header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), opcode);
        await _sendLock.WaitAsync();
        try
        {
            _encrypt?.Apply(header);
            await _stream.WriteAsync(header);
            await _stream.WriteAsync(payload);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose() => _tcp.Dispose();

    /// <summary>ARC4 with the first 1024 bytes of keystream dropped.</summary>
    sealed class Rc4
    {
        readonly byte[] _s = new byte[256];
        int _i, _j;

        public Rc4(byte[] key)
        {
            for (int i = 0; i < 256; i++)
                _s[i] = (byte)i;
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + _s[i] + key[i % key.Length]) & 0xFF;
                (_s[i], _s[j]) = (_s[j], _s[i]);
            }
            Apply(new byte[1024]);
        }

        public void Apply(Span<byte> data)
        {
            for (int n = 0; n < data.Length; n++)
            {
                _i = (_i + 1) & 0xFF;
                _j = (_j + _s[_i]) & 0xFF;
                (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
                data[n] ^= _s[(_s[_i] + _s[_j]) & 0xFF];
            }
        }
    }
}
