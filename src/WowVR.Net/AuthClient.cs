using System.Numerics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WowVR.Net;

/// <summary>A realm the authserver lists. Population is 0 for low, 1 medium and 2 high, as the client shows it.</summary>
public sealed record Realm(string Name, string Address, byte Id, byte Flags, byte Characters, byte Type = 0, bool Locked = false, float Population = 0)
{
    public bool Offline => (Flags & 0x02) != 0;

    /// <summary>The realm's type as the client names it: 1 is PvP, 4 normal, 6 RP and 8 RP PvP.</summary>
    public string TypeName => Type switch
    {
        1 => "PvP",
        6 => "RP",
        8 => "RP PvP",
        _ => "Normal",
    };
}

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

    /// <summary>Logs in and lists the realms. Errors are <see cref="IOException"/>s whose message can be shown as it is.</summary>
    public static async Task<(AuthClient Auth, List<Realm> Realms)> LoginAsync(string host, int port, string user, string password, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await LoginCoreAsync(host, port, user, password, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{host} didn't answer.");
        }
        catch (SocketException e)
        {
            throw new IOException($"Couldn't reach {host}: {e.Message}");
        }
        catch (EndOfStreamException)
        {
            throw new IOException($"{host} closed the connection. Is it an AzerothCore authserver?");
        }
    }

    private static async Task<(AuthClient Auth, List<Realm> Realms)> LoginCoreAsync(string host, int port, string user, string password, CancellationToken ct)
    {
        var client = new AuthClient { Account = user.ToUpperInvariant() };
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        var stream = tcp.GetStream();

        await stream.WriteAsync(client.LogonChallenge(), ct);

        // cmd, unused, result
        var head = await ReadExactAsync(stream, ct, 3);
        if (head[2] != 0)
            throw new IOException(AuthResult(head[2]));
        var B = await ReadExactAsync(stream, ct, 32);
        int gLen = (await ReadExactAsync(stream, ct, 1))[0];
        var g = await ReadExactAsync(stream, ct, gLen);
        int nLen = (await ReadExactAsync(stream, ct, 1))[0];
        var N = await ReadExactAsync(stream, ct, nLen);
        var salt = await ReadExactAsync(stream, ct, 32);
        await ReadExactAsync(stream, ct, 16); // version challenge
        byte securityFlags = (await ReadExactAsync(stream, ct, 1))[0];
        if (securityFlags != 0)
            throw new IOException("The account needs a PIN or authenticator code, which isn't supported.");

        var (A, M1, K) = Srp6(client.Account, password.ToUpperInvariant(), B, g, N, salt);

        var proof = new PacketWriter().U8(0x01).Bytes(A).Bytes(M1).Bytes(new byte[20]).U8(0).U8(0).ToArray();
        await stream.WriteAsync(proof, ct);

        var proofHead = await ReadExactAsync(stream, ct, 2);
        if (proofHead[1] != 0)
            throw new IOException(AuthResult(proofHead[1]));
        var rest = await ReadExactAsync(stream, ct, 20 + 4 + 4 + 2);
        var M2 = rest[..20];
        var expected = SHA1.HashData([.. A, .. M1, .. K]);
        if (!M2.AsSpan().SequenceEqual(expected))
            throw new IOException("The server's proof didn't match. It may not be the server you think it is.");
        client.SessionKey = K;

        // Realm list
        await stream.WriteAsync(new PacketWriter().U8(0x10).U32(0).ToArray(), ct);
        var listHead = await ReadExactAsync(stream, ct, 3);
        int size = listHead[1] | (listHead[2] << 8);
        var body = await ReadExactAsync(stream, ct, size);
        var r = new PacketReader(body);
        r.U32();
        int count = r.U16();
        var realms = new List<Realm>();
        for (int i = 0; i < count; i++)
        {
            byte type = r.U8();
            bool locked = r.U8() != 0;
            byte flags = r.U8();
            string name = r.CString();
            string address = r.CString();
            float population = r.F32();
            byte chars = r.U8();
            r.U8(); // timezone
            byte id = r.U8();
            if ((flags & 0x04) != 0)
                r.Skip(5); // version and build
            realms.Add(new Realm(name, address, id, flags, chars, type, locked, population));
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

    /// <summary>What the authserver's result codes mean, as sentences to show.</summary>
    static string AuthResult(byte code) => code switch
    {
        0x03 => "This account is banned.",
        0x04 or 0x05 => "The account name or password is wrong.",
        0x06 => "This account is already logged in.",
        0x07 => "This account has no game time.",
        0x08 => "The server is busy. Try again in a moment.",
        0x09 => "The server doesn't accept this game version.",
        0x0C => "This account is suspended.",
        0x10 => "This account is locked.",
        0x12 => "This account is locked to another address.",
        _ => $"The server refused the login (code 0x{code:X2}).",
    };

    public static async Task<byte[]> ReadExactAsync(Stream s, CancellationToken ct, int n)
    {
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return buf;
    }
}
