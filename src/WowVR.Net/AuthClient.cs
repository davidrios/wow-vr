using System.Numerics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WowVR.Net;

public sealed record Realm(string Name, string Address, byte Id, byte Flags, byte Characters);

/// <summary>
/// Logs in to an AzerothCore authserver (port 3724) the way a 3.3.5a client does:
/// the SRP6 exchange, then the realm list. The session key it ends with is what
/// the worldserver checks.
/// </summary>
public sealed class AuthClient
{
    const ushort Build = 12340;

    public byte[] SessionKey { get; private set; } = [];
    public string Account { get; private set; } = "";

    public static async Task<(AuthClient Auth, List<Realm> Realms)> LoginAsync(string host, int port, string user, string password)
    {
        var client = new AuthClient { Account = user.ToUpperInvariant() };
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port);
        var stream = tcp.GetStream();

        await stream.WriteAsync(client.LogonChallenge());

        // cmd, unused, result
        var head = await ReadExactAsync(stream, 3);
        if (head[2] != 0)
            throw new IOException($"logon challenge refused: {AuthResult(head[2])}");
        var B = await ReadExactAsync(stream, 32);
        int gLen = (await ReadExactAsync(stream, 1))[0];
        var g = await ReadExactAsync(stream, gLen);
        int nLen = (await ReadExactAsync(stream, 1))[0];
        var N = await ReadExactAsync(stream, nLen);
        var salt = await ReadExactAsync(stream, 32);
        await ReadExactAsync(stream, 16); // version challenge
        byte securityFlags = (await ReadExactAsync(stream, 1))[0];
        if (securityFlags != 0)
            throw new IOException($"the account needs a PIN, matrix or token (flags {securityFlags}), which this probe doesn't do");

        var (A, M1, K) = Srp6(client.Account, password.ToUpperInvariant(), B, g, N, salt);

        var proof = new PacketWriter().U8(0x01).Bytes(A).Bytes(M1).Bytes(new byte[20]).U8(0).U8(0).ToArray();
        await stream.WriteAsync(proof);

        var proofHead = await ReadExactAsync(stream, 2);
        if (proofHead[1] != 0)
            throw new IOException($"logon proof refused: {AuthResult(proofHead[1])} (wrong password?)");
        var rest = await ReadExactAsync(stream, 20 + 4 + 4 + 2);
        var M2 = rest[..20];
        var expected = SHA1.HashData([.. A, .. M1, .. K]);
        if (!M2.AsSpan().SequenceEqual(expected))
            throw new IOException("the server's proof didn't match ours");
        client.SessionKey = K;

        // Realm list
        await stream.WriteAsync(new PacketWriter().U8(0x10).U32(0).ToArray());
        var listHead = await ReadExactAsync(stream, 3);
        int size = listHead[1] | (listHead[2] << 8);
        var body = await ReadExactAsync(stream, size);
        var r = new PacketReader(body);
        r.U32();
        int count = r.U16();
        var realms = new List<Realm>();
        for (int i = 0; i < count; i++)
        {
            r.U8(); // type
            r.U8(); // locked
            byte flags = r.U8();
            string name = r.CString();
            string address = r.CString();
            r.F32(); // population
            byte chars = r.U8();
            r.U8(); // timezone
            byte id = r.U8();
            if ((flags & 0x04) != 0)
                r.Skip(5); // version and build
            realms.Add(new Realm(name, address, id, flags, chars));
        }
        return (client, realms);
    }

    byte[] LogonChallenge()
    {
        var name = Encoding.ASCII.GetBytes(Account);
        var w = new PacketWriter()
            .U8(0x00).U8(0x08).U16((ushort)(30 + name.Length))
            .Bytes("WoW\0"u8).U8(3).U8(3).U8(5).U16(Build)
            // Four-character codes go in reversed, as the client sends them.
            .Bytes("68x\0"u8).Bytes("niW\0"u8).Bytes("SUne"u8)
            .U32(0).U32(0)
            .U8((byte)name.Length).Bytes(name);
        return w.ToArray();
    }

    static BigInteger Num(ReadOnlySpan<byte> littleEndian) => new(littleEndian, isUnsigned: true, isBigEndian: false);

    static byte[] Bytes32(BigInteger v)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        b.AsSpan(0, Math.Min(32, b.Length)).CopyTo(padded);
        return padded;
    }

    /// <summary>WoW's SRP6 variant: SHA-1, little-endian numbers, k = 3, and an interleaved session key.</summary>
    internal static (byte[] A, byte[] M1, byte[] K) Srp6(string user, string password, byte[] Bbytes, byte[] gBytes, byte[] Nbytes, byte[] salt)
    {
        var N = Num(Nbytes);
        var g = Num(gBytes);
        var B = Num(Bbytes);
        if (B % N == 0)
            throw new IOException("bad B from server");

        var x = Num(SHA1.HashData([.. salt, .. SHA1.HashData(Encoding.ASCII.GetBytes($"{user}:{password}"))]));

        BigInteger a, A;
        do
        {
            a = Num(RandomNumberGenerator.GetBytes(32)) % N;
            A = BigInteger.ModPow(g, a, N);
        } while (A == 0);
        var Abytes = Bytes32(A);

        var u = Num(SHA1.HashData([.. Abytes, .. Bbytes]));
        var kgx = 3 * BigInteger.ModPow(g, x, N);
        var baseVal = ((B - kgx) % N + N) % N;
        var S = BigInteger.ModPow(baseVal, a + u * x, N);
        var K = Interleave(Bytes32(S));

        var nHash = SHA1.HashData(Nbytes);
        var gHash = SHA1.HashData(gBytes);
        var ng = new byte[20];
        for (int i = 0; i < 20; i++)
            ng[i] = (byte)(nHash[i] ^ gHash[i]);
        var userHash = SHA1.HashData(Encoding.ASCII.GetBytes(user));
        var M1 = SHA1.HashData([.. ng, .. userHash, .. salt, .. Abytes, .. Bbytes, .. K]);
        return (Abytes, M1, K);
    }

    internal static byte[] Interleave(byte[] S)
    {
        int p = 0;
        while (p < S.Length && S[p] == 0)
            p++;
        if ((p & 1) != 0)
            p++;
        p /= 2;
        var even = new byte[16 - p];
        var odd = new byte[16 - p];
        for (int i = p; i < 16; i++)
        {
            even[i - p] = S[2 * i];
            odd[i - p] = S[2 * i + 1];
        }
        var h0 = SHA1.HashData(even);
        var h1 = SHA1.HashData(odd);
        var K = new byte[40];
        for (int i = 0; i < 20; i++)
        {
            K[2 * i] = h0[i];
            K[2 * i + 1] = h1[i];
        }
        return K;
    }

    static string AuthResult(byte code) => code switch
    {
        0x03 => "banned",
        0x04 => "unknown account",
        0x05 => "incorrect password",
        0x06 => "already online",
        0x09 => "version invalid",
        0x0C => "suspended",
        0x10 => "locked",
        _ => $"code 0x{code:X2}",
    };

    public static async Task<byte[]> ReadExactAsync(Stream s, int n)
    {
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf);
        return buf;
    }
}
