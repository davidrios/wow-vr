using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace WowVR.Net.Tests;

public class Srp6Tests
{
    private static readonly byte[] N = Convert.FromHexString("894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7").Reverse().ToArray();

    private static BigInteger Num(byte[] b) => new(b, isUnsigned: true, isBigEndian: false);

    private static byte[] Bytes32(BigInteger v)
    {
        var b = v.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        b.CopyTo(padded, 0);
        return padded;
    }

    /// <summary>Plays the server's side as AzerothCore's SRP6 does, and checks both end with the same proof.</summary>
    [Fact]
    public void ClientProofMatchesTheServersCheck()
    {
        const string user = "PLAYER";
        const string password = "SECRET";
        var salt = RandomNumberGenerator.GetBytes(32);
        var n = Num(N);
        var g = new BigInteger(7);
        var x = Num(SHA1.HashData([.. salt, .. SHA1.HashData(Encoding.ASCII.GetBytes($"{user}:{password}"))]));
        var v = BigInteger.ModPow(g, x, n);
        var b = Num(RandomNumberGenerator.GetBytes(32)) % n;
        var B = Bytes32((3 * v + BigInteger.ModPow(g, b, n)) % n);

        var (A, M1, K) = AuthClient.Srp6(user, password, B, [7], N, salt);

        var u = Num(SHA1.HashData([.. A, .. B]));
        var serverS = BigInteger.ModPow(Num(A) * BigInteger.ModPow(v, u, n) % n, b, n);
        var serverK = AuthClient.Interleave(Bytes32(serverS));
        Assert.Equal(serverK, K);

        var ng = SHA1.HashData(N).Zip(SHA1.HashData(new byte[] { 7 }), (p, q) => (byte)(p ^ q)).ToArray();
        var expected = SHA1.HashData([.. ng, .. SHA1.HashData(Encoding.ASCII.GetBytes(user)), .. salt, .. A, .. B, .. serverK]);
        Assert.Equal(expected, M1);
    }
}

public class CredentialStoreTests
{
    /// <summary>Only on Windows, whose store is files here. The others' are the user's real keychain or keyring.</summary>
    [Fact]
    public void WindowsKeepsAndForgetsAPassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"wowvr-credentials-{Guid.NewGuid():N}");
        try
        {
            var store = CredentialStore.ForThisSystem(folder);
            Assert.True(store.IsAvailable);
            store.Save("host/ACCOUNT", "pässword");
            Assert.Equal("pässword", store.Load("host/ACCOUNT"));
            Assert.Null(store.Load("host/OTHER"));
            Assert.DoesNotContain(Directory.GetFiles(folder), f => File.ReadAllText(f).Contains("password"));
            store.Delete("host/ACCOUNT");
            Assert.Null(store.Load("host/ACCOUNT"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }
}

public class CharacterListTests
{
    [Fact]
    public void ReadsLooksAndGear()
    {
        var w = new PacketWriter().U8(1)
            .U64(42).CString("Cliffrey")
            .U8(5).U8(1).U8(0).U8(5).U8(4).U8(1).U8(3).U8(3) // race, class, sex, skin, face, hair, hair color, facial hair
            .U8(7).U32(85).U32(0).F32(1).F32(2).F32(3)
            .U32(0).U32(0x400).U32(0).U8(0)
            .U32(0).U32(0).U32(0);
        for (var slot = 0; slot < 23; slot++)
        {
            w.U32(slot == 4 ? 29958u : 0).U8(slot == 4 ? (byte)5 : (byte)0).U32(0);
        }

        var c = Assert.Single(RealmSession.ReadCharacterList(w.ToArray()));
        Assert.Equal("Cliffrey", c.Name);
        Assert.Equal((5, 1, 0, 5, 4, 1, 3, 3), (c.Race, c.Class, c.Sex, c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair));
        Assert.Equal(7, c.Level);
        Assert.True(c.HidesHelm);
        Assert.Equal(new EquippedItem(29958, 5), c.Equipment[4]);
        Assert.Equal(19, c.Equipment.Length);
    }
}

public class MovePathTests
{
    [Fact]
    public void WalksAtASteadySpeedThroughItsPoints()
    {
        // 10 yards east then 10 north, in 8 seconds, with the server's copy of the last point.
        var path = new MovePath([new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(10, 10, 0)], 8);

        Assert.Equal(20f, path.Length, 3);
        Assert.Equal(2.5f, path.Speed, 3);
        var (half, facing, moving) = path.At(2);
        Assert.Equal(new Vector3(5, 0, 0), half);
        Assert.Equal(0f, facing, 3);
        Assert.True(moving);
        Assert.Equal(new Vector3(10, 5, 0), path.At(6).Position);
        Assert.Equal(MathF.PI / 2, path.At(6).Facing, 3);

        var (end, endFacing, stillMoving) = path.At(100);
        Assert.Equal(new Vector3(10, 10, 0), end);
        Assert.Equal(MathF.PI / 2, endFacing, 3);
        Assert.False(stillMoving);
    }

    [Fact]
    public void EndsFacingWhereTheServerSays()
    {
        var path = new MovePath([new(0, 0, 0), new(4, 0, 0)], 2, facing: 1.5f);
        Assert.Equal(1.5f, path.At(3).Facing);
    }

    [Fact]
    public void CyclicPathsStartOver()
    {
        var path = new MovePath([new(0, 0, 0), new(10, 0, 0)], 4, cyclic: true);
        Assert.Equal(new Vector3(5, 0, 0), path.At(6).Position);
        Assert.True(path.At(6).Moving);
    }

    [Fact]
    public void OnePointStands()
    {
        var path = MovePath.Standing(new Vector3(1, 2, 3), 0.5f);
        Assert.Equal((new Vector3(1, 2, 3), 0.5f, false), path.At(10));
    }
}

public class ObjectTrackerTests
{
    private const ulong Guid = 0xF130000000001234;

    /// <summary>A creature's create block, as AzerothCore writes one for a unit standing still.</summary>
    private static byte[] CreateUnit(Vector3 at, uint entry, uint display)
    {
        var w = new PacketWriter().U32(1).U8(2);
        WriteGuid(w, Guid);
        w.U8((byte)ObjectType.Unit)
            .U16(0x20 | 0x40) // living, stationary
            .U32(0).U16(0).U32(1234)
            .F32(at.X).F32(at.Y).F32(at.Z).F32(1f)
            .U32(0); // fall time
        foreach (var speed in new[] { 2.5f, 7f, 4.5f, 4.72f, 2.5f, 7f, 4.5f, 3.14f, 3.14f })
        {
            w.F32(speed);
        }

        // Fields: entry, scale and display, in three mask blocks.
        var mask = new uint[3];
        mask[0] |= 1u << UpdateFields.ObjectEntry;
        mask[0] |= 1u << UpdateFields.ObjectScale;
        mask[UpdateFields.UnitDisplayId / 32] |= 1u << (UpdateFields.UnitDisplayId % 32);
        w.U8(3);
        foreach (var m in mask)
        {
            w.U32(m);
        }

        w.U32(entry).F32(1.5f).U32(display);
        return w.ToArray();
    }

    private static void WriteGuid(PacketWriter w, ulong guid)
    {
        byte mask = 0;
        var bytes = new List<byte>();
        for (var i = 0; i < 8; i++)
        {
            var b = (byte)(guid >> (i * 8));
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                bytes.Add(b);
            }
        }

        w.U8(mask).Bytes(bytes.ToArray());
    }

    /// <summary>AzerothCore's packing of a path's middle points.</summary>
    private static uint Pack(Vector3 v) =>
        ((uint)(int)(v.X / 0.25f) & 0x7FF) | (((uint)(int)(v.Y / 0.25f) & 0x7FF) << 11) | (((uint)(int)(v.Z / 0.25f) & 0x3FF) << 22);

    [Fact]
    public void ReadsACreatedUnit()
    {
        var tracker = new ObjectTracker();
        TrackedObject? created = null;
        tracker.Created += o => created = o;
        tracker.ReadUpdate(CreateUnit(new Vector3(-9449, 64, 56), 1423, 3167));

        Assert.Equal(0, tracker.Errors);
        Assert.NotNull(created);
        Assert.Equal(Guid, created.Guid);
        Assert.Equal(ObjectType.Unit, created.Type);
        Assert.Equal(1423u, created.Entry);
        Assert.Equal(3167u, created.DisplayId);
        Assert.Equal(1.5f, created.Scale);
        Assert.Equal(new Vector3(-9449, 64, 56), created.Position);
        Assert.Equal(2.5f, created.WalkSpeed);
        Assert.False(created.Path!.At(0).Moving);
    }

    [Fact]
    public void ReadsALinearMonsterMove()
    {
        var tracker = new ObjectTracker();
        tracker.ReadUpdate(CreateUnit(new Vector3(0, 0, 0), 1, 1));
        TrackedObject? moved = null;
        tracker.Moved += o => moved = o;

        // From (0,0,0) through (4,2,0) to (8,0,0), in 4 seconds.
        var start = new Vector3(0, 0, 0);
        var end = new Vector3(8, 0, 0);
        var middle = (start + end) / 2;
        var w = new PacketWriter();
        WriteGuid(w, Guid);
        w.U8(0).F32(0).F32(0).F32(0).U32(7).U8(0) // normal facing
            .U32(0).U32(4000).U32(2)
            .F32(end.X).F32(end.Y).F32(end.Z)
            .U32(Pack(middle - new Vector3(4, 2, 0)));
        tracker.ReadMonsterMove(w.ToArray());

        Assert.NotNull(moved);
        Assert.Equal([start, new Vector3(4, 2, 0), end], moved.Path!.Points);
        Assert.Equal(4.0, moved.Path.Duration);
    }

    [Fact]
    public void UnpacksSignedQuarterYards()
    {
        var v = new Vector3(-12.25f, 30.5f, -3.75f);
        Assert.Equal(v, ObjectTracker.Unpack(Pack(v)));
    }

    [Fact]
    public void ReadsTheServersTimeOfDay()
    {
        // 18:45 on some day.
        var packed = (26u << 24) | (10u << 20) | (2u << 14) | (3u << 11) | (18u << 6) | 45u;
        Assert.Equal(18.75, RealmSession.TimeOfDay(packed), 6);
    }
}
