using System.Numerics;
using WowVR.Formats.Models;
using WowVR.Formats.Terrain;
using WowVR.Formats.World;
using Xunit;

namespace WowVR.Formats.Tests;

public class ParticleEmitterTests
{
    [Fact]
    public void AFiresEmitterHasSensibleSettings()
    {
        var client = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        using var source = WowVR.Formats.Assets.MpqAssetSource.Open(client);
        const int CozyFire = 199;
        var path = WowVR.Formats.World.GameObjectDisplayTable.Read(source).ModelOf(CozyFire)!;
        var model = WowVR.Formats.Models.M2Model.Parse(source.Read(path)!, source.Read(WowVR.Formats.Models.M2Model.SkinPath(path))!);

        Assert.NotEmpty(model.Emitters);
        Assert.All(model.Emitters, e =>
        {
            Console.WriteLine($"{path}: {e.Texture} blend {e.Blend} rate {e.Rate} life {e.Lifespan} speed {e.Speed} gravity {e.Gravity} sizes {string.Join(',', e.Sizes)} tiles {e.Rows}x{e.Columns} at {e.Position}");
            Assert.NotNull(source.Read(e.Texture));
            Assert.InRange(e.Lifespan, 0.01f, 60f);
            Assert.InRange(e.Rate, 0.01f, 5000f);
            Assert.InRange(e.Sizes.Max(), 0.001f, 100f);
            Assert.InRange(e.Mid, 0f, 1f);
            Assert.All(e.Colors, c => Assert.InRange(c.W, 0f, 1.01f));
        });
    }
}

public class GameObjectPlacementTests
{
    private static GameObjectSpawn Spawn(float orientation, params float[] rotation) => new()
    {
        Guid = 1,
        DisplayId = 1,
        Pos = [-9456f, 46f, 56f],
        Orientation = orientation,
        Rotation = rotation,
    };

    [Fact]
    public void OrientationPointsTheModelLikeACreature()
    {
        const float Orientation = 1.2f;
        var placement = Spawn(Orientation).Placement(31, 49);

        var yaw = WowCoords.YawFromOrientation(Orientation);
        Assert.Equal(MathF.Cos(yaw), placement.AxisX.X, 4);
        Assert.Equal(-MathF.Sin(yaw), placement.AxisX.Z, 4);
        Assert.Equal(1f, placement.AxisY.Y, 4);
    }

    [Fact]
    public void AQuaternionGivesTheSameAsItsOrientation()
    {
        const float Orientation = 2.1f;
        var byAngle = Spawn(Orientation).Placement(31, 49);
        var byQuaternion = Spawn(0f, 0f, 0f, MathF.Sin(Orientation / 2), MathF.Cos(Orientation / 2)).Placement(31, 49);

        Assert.Equal(byAngle.AxisX.X, byQuaternion.AxisX.X, 4);
        Assert.Equal(byAngle.AxisX.Z, byQuaternion.AxisX.Z, 4);
        Assert.Equal(byAngle.AxisZ.X, byQuaternion.AxisZ.X, 4);
    }

    [Fact]
    public void AxesKeepHandednessAndTheSpawnsPositionIsOnTheTile()
    {
        var placement = Spawn(0.7f, 0.1f, 0.2f, 0.3f, 0.9f).Placement(31, 49);

        Assert.True(System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(placement.AxisX, placement.AxisY), placement.AxisZ) > 0.99f);
        Assert.Equal(WowCoords.TileLocal(-9456f, 46f, 56f, 31, 49), placement.Origin);
    }
}

public class CreatureSoundDistanceTests
{
    [Fact]
    public void SoundsReadTheirDistancesAndUnsetFootstepsAreSilent()
    {
        var client = Environment.GetEnvironmentVariable("WOWVR_CLIENT_PATH");
        if (string.IsNullOrWhiteSpace(client))
        {
            return;
        }

        using var source = WowVR.Formats.Assets.MpqAssetSource.Open(client);
        var entry = WowVR.Formats.Database.SoundEntryRecord.ReadAll(source).First(e => e.Id == 651);
        var footsteps = WowVR.Formats.World.FootstepTable.Read(source);

        Assert.True(entry.MinDistance > 0f && entry.MinDistance < entry.Cutoff);
        Assert.Equal(0, footsteps.SoundFor(0, 0));
        Assert.NotEqual(0, footsteps.SoundFor(8, 0));
    }
}

public class CreatureMotionTests
{
    private static CreatureSpawn Spawn(CreatureMovement movement, float radius = 0f, IReadOnlyList<CreatureWaypoint>? path = null) => new()
    {
        Guid = 1234,
        DisplayId = 1,
        Pos = [100f, 200f, 30f],
        Orientation = 1.5f,
        MovementType = movement,
        WanderRadius = radius,
        Waypoints = path ?? [],
    };

    [Fact]
    public void AnIdleCreatureStaysAtItsSpawn()
    {
        var state = new CreatureMotion(Spawn(CreatureMovement.Idle)).At(1234.5);

        Assert.Equal(new CreatureMotionState(100, 200, 30f, 1.5f, false), state);
    }

    [Fact]
    public void TheSameClockGivesTheSamePlaceWithoutHistory()
    {
        var spawn = Spawn(CreatureMovement.Wander, 10f);
        var walked = new CreatureMotion(spawn);
        for (var clock = 0.0; clock < 500; clock += 0.37)
        {
            walked.At(clock);
        }

        Assert.Equal(walked.At(500), new CreatureMotion(spawn).At(500));
    }

    [Fact]
    public void WanderingStaysInItsRadiusAtWalkingSpeedWithoutJumps()
    {
        const float Radius = 10f;
        const double Step = 0.05;
        var motion = new CreatureMotion(Spawn(CreatureMovement.Wander, Radius));
        var previous = motion.At(0);
        var moved = false;
        for (var clock = Step; clock < 1000; clock += Step)
        {
            var state = motion.At(clock);
            var (dx, dy) = (state.X - 100, state.Y - 200);
            Assert.True(Math.Sqrt(dx * dx + dy * dy) <= Radius + 0.001);

            var step = Math.Sqrt(Math.Pow(state.X - previous.X, 2) + Math.Pow(state.Y - previous.Y, 2));
            Assert.True(step <= CreatureMotion.WalkSpeed * Step * 1.01, $"jumped {step} at {clock}");

            moved |= state.Moving;
            previous = state;
        }

        Assert.True(moved);
    }

    [Fact]
    public void APathLoopsAndWaitsAtItsPoints()
    {
        CreatureWaypoint[] path = [new(0, 0, 0, 2000), new(10, 0, 5, 0), new(10, 10, 0, 0)];
        var motion = new CreatureMotion(Spawn(CreatureMovement.Waypoints, path: path));
        const double Step = 0.05;
        var previous = motion.At(0);
        var (waiting, walking) = (0, 0);
        for (var clock = Step; clock < 200; clock += Step)
        {
            var state = motion.At(clock);
            var step = Math.Sqrt(Math.Pow(state.X - previous.X, 2) + Math.Pow(state.Y - previous.Y, 2));
            Assert.True(step <= CreatureMotion.WalkSpeed * Step * 1.01, $"jumped {step} at {clock}");
            Assert.NotNull(state.Z);
            waiting += state.Moving ? 0 : 1;
            walking += state.Moving ? 1 : 0;
            previous = state;
        }

        Assert.True(waiting > 0 && walking > waiting);
    }

    [Fact]
    public void SpawnsCanWalkAtTheirOwnSpeed()
    {
        CreatureWaypoint[] path = [new(0, 0, 0), new(100, 0, 0)];
        var motion = new CreatureMotion(Spawn(CreatureMovement.Waypoints, path: path) with { WalkSpeed = 5f });
        const double Step = 0.1;
        var fastest = 0.0;
        for (var clock = Step; clock < 100; clock += Step)
        {
            fastest = Math.Max(fastest, Math.Abs(motion.At(clock).X - motion.At(clock - Step).X));
        }

        Assert.Equal(5f * Step, fastest, 2);
    }

    [Fact]
    public void FacingPointsAlongTheWalk()
    {
        // Northward, which is orientation 0, then westward, a quarter turn left.
        CreatureWaypoint[] path = [new(0, 0, 0), new(1000, 0, 0), new(1000, 1000, 0)];
        var motion = new CreatureMotion(Spawn(CreatureMovement.Waypoints, path: path));
        var seen = new HashSet<int>();
        for (var clock = 0.0; clock < 1500; clock += 10)
        {
            seen.Add((int)MathF.Round(motion.At(clock).Facing * 100));
        }

        Assert.Contains(0, seen);
        Assert.Contains(157, seen);
    }
}

public class CreatureCoordinateTests
{
    [Fact]
    public void OrientationZeroFacesNorth()
    {
        var forward = WowCoords.ModelToGodot(Vector3.UnitX);
        var yaw = WowCoords.YawFromOrientation(0);
        var turned = Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

        Assert.Equal(WowCoords.ToGodot(Vector3.UnitX).X, turned.X, 4);
        Assert.Equal(WowCoords.ToGodot(Vector3.UnitX).Z, turned.Z, 4);
    }

    [Fact]
    public void OrientationTurnsTowardWest()
    {
        var forward = WowCoords.ModelToGodot(Vector3.UnitX);
        var yaw = WowCoords.YawFromOrientation(MathF.PI / 2);
        var turned = Vector3.Transform(forward, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));

        Assert.Equal(WowCoords.ToGodot(Vector3.UnitY).X, turned.X, 4);
        Assert.Equal(WowCoords.ToGodot(Vector3.UnitY).Z, turned.Z, 4);
    }

    [Fact]
    public void ModelRotationsMatchRotatingThePoint()
    {
        var model = Quaternion.Normalize(new Quaternion(0.2f, -0.4f, 0.5f, 0.7f));
        var point = new Vector3(1, 2, 3);

        var rotatedThenConverted = WowCoords.ModelToGodot(Vector3.Transform(point, model));
        var convertedThenRotated = Vector3.Transform(WowCoords.ModelToGodot(point), WowCoords.ModelRotationToGodot(model));

        Assert.True(Vector3.Distance(rotatedThenConverted, convertedThenRotated) < 0.001f);
    }
}

public class M2ShaderSelectionTests
{
    private static M2ShaderBatch Batch(int blend, int layer = 0, int material = 0, int count = 1, int shader = 0, int coord0 = 0, int coord1 = 0, uint texture = 1, int weight = 0) => new()
    {
        ShaderId = shader,
        TextureCount = count,
        MaterialIndex = material,
        Layer = layer,
        BlendMode = blend,
        RenderFlags = 0,
        Coord0 = coord0,
        Coord1 = coord1,
        TextureWeight = weight,
        Textures = [texture, texture + 1],
        Transforms = [-1, -1],
    };

    [Theory]
    [InlineData(0, 0, M2Combiners.Opaque, M2UvSource.First)]
    [InlineData(2, 0, M2Combiners.Mod, M2UvSource.First)]
    [InlineData(1, 1, M2Combiners.Mod, M2UvSource.Second)]
    [InlineData(4, -1, M2Combiners.Mod, M2UvSource.SphereMap)]
    public void WithoutATableOneTextureFollowsItsBlendMode(int blend, int coord, int combiner, M2UvSource uv)
    {
        var batch = Batch(blend, coord0: coord);

        M2ShaderSelection.Select([batch], null);

        var result = M2ShaderSelection.Resolve(batch);
        Assert.Equal(combiner, result.Combiner);
        Assert.Equal(uv, result.Uv0);
    }

    [Fact]
    public void TheTableChoosesTheSecondTexturesCombiner()
    {
        var batch = Batch(0, count: 2, shader: 2, coord1: -1);

        M2ShaderSelection.Select([batch], [0, 0, 1, 4]);

        var result = M2ShaderSelection.Resolve(batch);
        Assert.Equal(M2Combiners.Pair(M2Combiners.Opaque, M2Combiners.Mod2x), result.Combiner);
        Assert.Equal(M2UvSource.First, result.Uv0);
        Assert.Equal(M2UvSource.SphereMap, result.Uv1);
    }

    [Fact]
    public void AnEnvironmentLayerMergesIntoTheOneBelow()
    {
        var bottom = Batch(0, layer: 0, material: 0, texture: 5);
        var shine = Batch(4, layer: 1, material: 1, coord0: -1, texture: 9);

        M2ShaderSelection.Select([bottom, shine], null);

        Assert.True(shine.Merged);
        Assert.Equal(2, bottom.TextureCount);
        Assert.Equal([5u, 9u], bottom.Textures);
        Assert.Equal(M2Combiners.OpaqueAddAlpha, M2ShaderSelection.Resolve(bottom).Combiner);
    }

    [Fact]
    public void LayersWithDifferentWeightsStaySeparate()
    {
        var bottom = Batch(0, layer: 0, material: 0, weight: 0);
        var shine = Batch(4, layer: 1, material: 1, coord0: -1, weight: 1);

        M2ShaderSelection.Select([bottom, shine], null);

        Assert.False(shine.Merged);
        Assert.Equal(1, bottom.TextureCount);
    }
}

public class M2EffectTests
{
    private static M2EffectTrack<float> Floats(float period, bool step, float[] times, float[] values) =>
        new(times, values, period, step, float.Lerp);

    [Fact]
    public void TracksInterpolateAndLoop()
    {
        var track = Floats(10f, false, [0f, 4f], [0f, 8f]);

        Assert.Equal(4f, track.Sample(2), 3);
        Assert.Equal(4f, track.Sample(12), 3);
        Assert.Equal(8f, track.Sample(9), 3);
    }

    [Fact]
    public void StepTracksHoldTheirKey() =>
        Assert.Equal(1f, Floats(10f, true, [0f, 4f], [1f, 8f]).Sample(3), 3);

    [Fact]
    public void ASingleKeyDoesntChange() =>
        Assert.False(Floats(10f, false, [0f], [1f]).Changes);

    [Fact]
    public void RotationTurnsAroundTheTexturesMiddle()
    {
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var track = new M2EffectTrack<Quaternion>([0f], [quarter], 1f, false, Quaternion.Slerp);

        var matrix = new M2UvAnimation(null, track, null).At(0);

        var middle = matrix.Apply(new Vector2(0.5f, 0.5f));
        Assert.Equal(0.5f, middle.X, 4);
        Assert.Equal(0.5f, middle.Y, 4);
    }

    [Fact]
    public void TranslationScrolls()
    {
        var track = new M2EffectTrack<Vector3>([0f, 1f], [Vector3.Zero, new Vector3(1, 0, 0)], 1f, false, Vector3.Lerp);

        var moved = new M2UvAnimation(track, null, null).At(0.25).Apply(Vector2.Zero);

        Assert.Equal(0.25f, moved.X, 4);
    }
}

public class CreatureSoundEventTests
{
    [Theory]
    [InlineData("$FL0", CreatureSoundKind.Footstep, 0)]
    [InlineData("$RR2", CreatureSoundKind.Footstep, 0)]
    [InlineData("$FD1", CreatureSoundKind.Fidget, 0)]
    [InlineData("$FD5", CreatureSoundKind.Fidget, 4)]
    [InlineData("$WNG", CreatureSoundKind.WingFlap, 0)]
    public void ReadsSoundEvents(string id, CreatureSoundKind kind, int index) =>
        Assert.Equal((kind, index), CreatureSoundEvents.Parse(id));

    [Theory]
    [InlineData("$FSD")]
    [InlineData("$FDX")]
    [InlineData("$HIT")]
    [InlineData("WHEE")]
    public void IgnoresOtherEvents(string id) =>
        Assert.Null(CreatureSoundEvents.Parse(id));
}

public class SpawnFileTests
{
    [Fact]
    public void ReadsTheExportersRows()
    {
        const string Json = """
            [
              { "guid": 7, "entry": 1423, "name": "Stormwind Guard", "displayId": 3167,
                "pos": [-9450.5, 60.25, 56.75], "orientation": 3.14, "wanderRadius": 0,
                "movementType": 2,
                "waypoints": [{ "x": 1, "y": 2, "z": 3, "delayMs": 500 }, { "x": 4, "y": 5, "z": 6 }] }
            ]
            """;

        var spawn = Assert.Single(CreatureSpawn.ParseFile(Json));

        Assert.Equal(7, spawn.Guid);
        Assert.Equal("Stormwind Guard", spawn.Name);
        Assert.Equal(-9450.5f, spawn.X);
        Assert.Equal(CreatureMovement.Waypoints, spawn.MovementType);
        Assert.Equal(new CreatureWaypoint(1, 2, 3, 500), spawn.Waypoints[0]);
        Assert.Equal(0, spawn.Waypoints[1].DelayMs);
        Assert.Equal(1f, spawn.Scale);
        Assert.Equal(CreatureMotion.WalkSpeed, spawn.WalkSpeed);
        Assert.Empty(spawn.Weapons);
    }

    [Fact]
    public void SkipsRowsWithoutAPosition()
    {
        Assert.Empty(CreatureSpawn.ParseFile("""[{ "guid": 1, "displayId": 2, "pos": [1, 2] }]"""));
    }
}

public class ClientCreatureTests(ClientFixture client) : IClassFixture<ClientFixture>
{
    private const int GoldshireX = 31;
    private const int GoldshireY = 49;

    /// <summary>Correct rotations came within 0.125 yards on these models, and reversed ones at least 0.16 off.</summary>
    private const float MaxBoundsError = 0.15f;

    private Formats.Assets.MpqAssetSource Source
    {
        get
        {
            if (client.Source is null)
            {
                Assert.Skip("WOWVR_CLIENT_PATH is not set.");
            }

            return client.Source;
        }
    }

    private M2SkinnedModel Load(string path, Func<M2Sequence, bool> wanted) =>
        M2SkinnedModel.Parse(Source.Read(path), Source.Read(M2Model.SkinPath(path)), wanted, s => Source.Read(s.AnimPath(path)));

    /// <summary>Plays a clip the way WoWModelViewer's Bone::calcMatrix does, and gives the vertices.</summary>
    private static Vector3[] Pose(M2SkinnedModel model, M2Clip clip, float time)
    {
        var matrices = new Matrix4x4[model.Bones.Length];
        for (var i = 0; i < matrices.Length; i++)
        {
            var bone = model.Bones[i];
            var local = Matrix4x4.CreateTranslation(-bone.Pivot)
                * Matrix4x4.CreateScale(SampleVector(clip.Scales[i], time, Vector3.One))
                * Matrix4x4.CreateFromQuaternion(SampleRotation(clip.Rotations[i], time))
                * Matrix4x4.CreateTranslation(SampleVector(clip.Translations[i], time, Vector3.Zero) + bone.Pivot);
            matrices[i] = bone.Parent >= 0 ? local * matrices[bone.Parent] : local;
        }

        var result = new Vector3[model.Positions.Length];
        for (var v = 0; v < result.Length; v++)
        {
            var sum = Vector3.Zero;
            for (var slot = 0; slot < 4; slot++)
            {
                var weight = model.BoneWeights[v * 4 + slot] / 255f;
                if (weight > 0)
                {
                    sum += Vector3.Transform(model.Positions[v], matrices[model.BoneIndices[v * 4 + slot]]) * weight;
                }
            }

            result[v] = sum;
        }

        return result;
    }

    private static int KeyAfter(float[] times, float time)
    {
        var index = Array.BinarySearch(times, time);
        return index >= 0 ? index : ~index;
    }

    private static Vector3 SampleVector(M2Track<Vector3> track, float time, Vector3 fallback)
    {
        if (track.IsEmpty)
        {
            return fallback;
        }

        var after = KeyAfter(track.Times, time);
        if (after == 0 || after >= track.Times.Length)
        {
            return track.Values[Math.Clamp(after, 0, track.Times.Length - 1)];
        }

        var t = (time - track.Times[after - 1]) / (track.Times[after] - track.Times[after - 1]);
        return Vector3.Lerp(track.Values[after - 1], track.Values[after], t);
    }

    private static Quaternion SampleRotation(M2Track<Quaternion> track, float time)
    {
        if (track.IsEmpty)
        {
            return Quaternion.Identity;
        }

        var after = KeyAfter(track.Times, time);
        if (after == 0 || after >= track.Times.Length)
        {
            return Quaternion.Normalize(track.Values[Math.Clamp(after, 0, track.Times.Length - 1)]);
        }

        var t = (time - track.Times[after - 1]) / (track.Times[after] - track.Times[after - 1]);
        return Quaternion.Normalize(Quaternion.Slerp(track.Values[after - 1], track.Values[after], t));
    }

    /// <summary>
    /// Each sequence stores the bounds of the model while it plays. Playing it should give
    /// those same bounds, which pins down the bone math, including the rotations' direction.
    /// </summary>
    [Theory]
    [InlineData(@"Creature\Kobold\Kobold.m2", M2SequenceIds.Walk)]
    [InlineData(@"Creature\Wolf\Wolf.m2", M2SequenceIds.Walk)]
    [InlineData(@"Creature\Wolf\Wolf.m2", M2SequenceIds.Run)]
    [InlineData(@"Character\Human\Male\HumanMale.m2", M2SequenceIds.Walk)]
    [InlineData(@"Character\Human\Male\HumanMale.m2", M2SequenceIds.Stand)]
    [InlineData(@"Character\Human\Female\HumanFemale.m2", M2SequenceIds.Run)]
    public void PosedVerticesStayInsideTheSequencesBounds(string path, int sequenceId)
    {
        var model = Load(path, s => s.Id == sequenceId && s.Variation == 0);
        var clip = Assert.Single(model.Clips);
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        const int Steps = 32;
        for (var step = 0; step < Steps; step++)
        {
            foreach (var vertex in Pose(model, clip, clip.Sequence.DurationSeconds * step / Steps))
            {
                min = Vector3.Min(min, vertex);
                max = Vector3.Max(max, vertex);
            }
        }

        var error = Math.Max(
            (min - clip.Sequence.BoundsMin).Length(),
            (max - clip.Sequence.BoundsMax).Length());
        Assert.True(error < MaxBoundsError, $"Posed bounds {min} to {max}, stored {clip.Sequence.BoundsMin} to {clip.Sequence.BoundsMax}, off by {error}");
    }

    [Fact]
    public void EveryCreatureModelParsesWithItsClips()
    {
        var table = CreatureDisplayTable.Read(Source);
        var paths = table.DisplaysOfModel("").Select(id => table.Resolve(id)!.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var (parsed, noClips, globalTracks, curvedTracks, allTracks) = (0, 0, 0, 0, 0);
        var failures = new List<string>();
        foreach (var path in paths)
        {
            var (m2, skin) = (Source.Read(path), Source.Read(M2Model.SkinPath(path)));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                continue;
            }

            try
            {
                var model = M2SkinnedModel.Parse(m2, skin, s => s.Id is M2SequenceIds.Stand or M2SequenceIds.Walk or M2SequenceIds.Run, s => Source.Read(s.AnimPath(path)));
                parsed++;
                noClips += model.Clips.Count == 0 ? 1 : 0;
                foreach (var clip in model.Clips)
                {
                    foreach (var track in clip.Rotations.Where(t => !t.IsEmpty))
                    {
                        allTracks++;
                        globalTracks += track.GlobalSequence >= 0 ? 1 : 0;
                        curvedTracks += track.Interpolation == M2Interpolation.Curve ? 1 : 0;
                    }
                }
            }
            catch (WowAlchemyException e)
            {
                failures.Add($"{path}: {e.Message}");
            }
        }

        TestContext.Current.SendDiagnosticMessage($"{parsed} of {paths.Count} parsed, {noClips} without clips, of {allTracks} rotation tracks {globalTracks} global and {curvedTracks} curved");
        Assert.True(failures.Count == 0, $"{failures.Count}: {string.Join("; ", failures.Take(5))} ({parsed} parsed, {noClips} without clips, {allTracks} tracks, {globalTracks} global, {curvedTracks} curved)");
        Assert.True(parsed > 500);
    }

    [Fact]
    public void WalkingFiresFootstepsThatMakeASound()
    {
        var table = CreatureDisplayTable.Read(Source);
        var footsteps = FootstepTable.Read(Source);
        var map = WdtMap.Read(Source, "Azeroth");
        var terrain = AdtTerrain.Parse(Source.Read(map.TilePath(GoldshireX, GoldshireY))!, map.BigAlpha);
        var ground = new TerrainHeights(terrain, GoldshireX, GoldshireY).EffectAt(-9464, 62);
        var look = table.Resolve(table.DisplaysOfModel(@"Creature\Wolf").First());
        Assert.NotNull(look?.Sounds);

        var model = Load(look.Model, s => s.Id == M2SequenceIds.Walk && s.Variation == 0);
        var clip = Assert.Single(model.Clips);
        var steps = model.Events
            .Select((e, i) => (Event: e, Times: clip.EventTimes[i]))
            .Where(e => CreatureSoundEvents.Parse(e.Event.Id)?.Kind == CreatureSoundKind.Footstep)
            .Sum(e => e.Times.Length);

        Assert.True(steps >= 2, $"{steps} footsteps");
        Assert.True(clip.EventTimes.SelectMany(t => t).All(t => t <= clip.Sequence.DurationSeconds + 0.001f));

        var sound = footsteps.SoundFor(look.Sounds.Footstep, ground);
        Assert.NotEqual(0, sound);
        var entry = Assert.Single(Formats.Database.SoundEntryRecord.ReadAll(Source), e => e.Id == sound);
        Assert.Contains(entry.Files, f => Source.Read($@"{entry.Directory}\{f.Name}") is { Length: > 0 });
    }

    /// <summary>Some rows' folders start with a backslash, as the giant wing flaps' do, which archive paths don't.</summary>
    [Fact]
    public void SoundFoldersJoinFileNamesIntoArchivePaths()
    {
        var entries = Formats.Database.SoundEntryRecord.ReadAll(Source);
        Assert.All(entries, e => Assert.False(e.Directory.StartsWith('\\') || e.Directory.EndsWith('\\'), $"sound {e.Id}: '{e.Directory}'"));

        var wings = Assert.Single(entries, e => e.Files.Any(f => f.Name.Equals("GiantWingFlap1.wav", StringComparison.OrdinalIgnoreCase)));
        Assert.True(Source.Read($@"{wings.Directory}\GiantWingFlap1.wav") is { Length: > 0 }, $"'{wings.Directory}'");
    }

    [Theory]
    [InlineData(@"World\Azeroth\Elwynn\PassiveDoodads\CornCrop\corncrop1.m2", M2BillboardKind.LockModelZ)]
    public void BillboardedDoodadsAreBaked(string path, M2BillboardKind kind)
    {
        var model = Load(path, s => s.Id == M2SequenceIds.Stand && s.Variation == 0);

        Assert.Contains(model.Bones, b => b.Billboard == kind);
        Assert.NotNull(M2BoneBake.TryCreate(model, model.Clips[0]));
    }

    [Fact]
    public void ABakeHoldsThePosesItWasMadeFrom()
    {
        var model = Load(@"Creature\Wolf\Wolf.m2", s => s.Id == M2SequenceIds.Stand && s.Variation == 0);
        var clip = Assert.Single(model.Clips);

        var bake = M2BoneBake.TryCreate(model, clip);

        Assert.NotNull(bake);
        const int Frame = 5;
        var pose = M2BoneBake.Pose(model, clip, bake.Seconds * Frame / bake.Frames);
        var bone = Array.FindIndex(bake.BoneRemap, b => b >= 0);
        var at = (Frame * bake.BoneCount + bake.BoneRemap[bone]) * M2BoneBake.RowsPerBone * 4;
        Assert.Equal(pose[bone].M41, bake.Texels[at + 3], 4);
        Assert.Equal(pose[bone].M42, bake.Texels[at + 7], 4);
        Assert.Equal(pose[bone].M43, bake.Texels[at + 11], 4);
    }

    [Fact]
    public void BakesAcrossWorldDoodadsAreSmall()
    {
        var (animated, still, largest) = (0, 0, 0L);
        foreach (var path in Source.List().Where(f => f.StartsWith("World", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)))
        {
            var (m2, skin) = (Source.Read(path), Source.Read(M2Model.SkinPath(path)));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                continue;
            }

            var model = M2SkinnedModel.Parse(m2, skin, s => s.Id == M2SequenceIds.Stand && s.Variation == 0, s => Source.Read(s.AnimPath(path)));
            var bake = model.Clips.Count > 0 ? M2BoneBake.TryCreate(model, model.Clips[0]) : null;
            animated += bake is null ? 0 : 1;
            still += bake is null ? 1 : 0;
            largest = Math.Max(largest, (bake?.Texels.Length ?? 0L) * sizeof(float));
            Assert.True(bake is null || bake.BoneCount <= 256);
        }

        Assert.True(animated > 100, $"{animated} animate and {still} don't");
        Assert.True(largest < 64 * 1024 * 1024, $"the largest bake is {largest} bytes");
    }

    [Fact]
    public void WaterfallsScrollTheirTextures()
    {
        var model = Load(@"World\Azeroth\Elwynn\PassiveDoodads\Waterfall\ElwynnTallWaterfall01.m2", _ => false);

        var moving = model.Batches.Select(b => b.Effect?.Uv0Animation).OfType<M2UvAnimation>().Where(a => a.Changes).ToList();

        Assert.NotEmpty(moving);
        Assert.All(moving, a => Assert.NotEqual(a.At(0), a.At(0.5)));
    }

    [Fact]
    public void EffectsAcrossEveryModelAreSane()
    {
        var paths = Source.List().Where(f => f.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)).ToList();
        var (parsed, failed, withEffect, twoTextures, animated, sphere) = (0, 0, 0, 0, 0, 0);
        var combiners = new Dictionary<int, int>();
        var unnamed = new HashSet<string>();
        foreach (var path in paths)
        {
            var (m2, skin) = (Source.Read(path), Source.Read(M2Model.SkinPath(path)));
            if (m2 is not { Length: > 0 } || skin is not { Length: > 0 })
            {
                continue;
            }

            try
            {
                var model = M2SkinnedModel.Parse(m2, skin, _ => false, _ => null);
                parsed++;
                foreach (var batch in model.Batches)
                {
                    if (batch.Effect is not { } effect)
                    {
                        continue;
                    }

                    withEffect++;
                    twoTextures += effect.HasTwoTextures ? 1 : 0;
                    animated += effect.Animated ? 1 : 0;
                    sphere += effect.Uv0 == M2UvSource.SphereMap || effect.Uv1 == M2UvSource.SphereMap ? 1 : 0;
                    combiners[effect.Combiner] = combiners.GetValueOrDefault(effect.Combiner) + 1;
                    if (effect.HasTwoTextures && effect.Texture2Name is null && effect.Texture2Type == M2TextureTypes.Named)
                    {
                        unnamed.Add(path);
                    }
                }
            }
            catch (WowAlchemyException)
            {
                failed++;
            }
        }

        Assert.Equal(0, failed);
        Assert.Empty(unnamed);
        Assert.True(twoTextures > 1000 && animated > 1000 && sphere > 1000, $"{twoTextures} two textures, {animated} animated, {sphere} sphere");

        // Every combiner the client's models reach has a case in the shader.
        int[] drawn = [0, 1, 2, 3, 4, 5, 10, 11, 13, 14, 16, 17, 20, 21, 23, 24, 26, 27, 41, 54, 100, 101, 102];
        Assert.All(combiners.Keys, c => Assert.Contains(c, drawn));
    }

    [Fact]
    public void WalkingMovesTheLegs()
    {
        var model = Load(@"Creature\Wolf\Wolf.m2", s => s.Id == M2SequenceIds.Walk && s.Variation == 0);
        var clip = Assert.Single(model.Clips);

        var first = Pose(model, clip, 0);
        var later = Pose(model, clip, clip.Sequence.DurationSeconds / 2);
        var furthest = first.Zip(later, Vector3.Distance).Max();

        Assert.True(furthest > 0.1f, $"moved {furthest}");
    }

    [Fact]
    public void ReadsSequencesKeptInAnimFiles()
    {
        var external = 0;
        foreach (var path in new[] { @"Character\Human\Male\HumanMale.m2", @"Creature\Kobold\Kobold.m2", @"Creature\Wolf\Wolf.m2" })
        {
            var model = Load(path, s => !s.InModel && !s.IsAlias);
            external += model.Clips.Count;
            foreach (var clip in model.Clips)
            {
                Assert.Contains(clip.Translations.Concat<object>(clip.Rotations), t => t is M2Track<Quaternion> { IsEmpty: false } or M2Track<Vector3> { IsEmpty: false });
            }
        }

        Assert.True(external > 0);
    }

    [Fact]
    public void TexturesAndModelsOfDisplaysExist()
    {
        var table = CreatureDisplayTable.Read(Source);
        var missing = new List<string>();
        var checkedLooks = 0;
        foreach (var id in table.DisplaysOfModel("Kobold").Concat(table.ArmoredCharacters(@"Human\Male")).Take(60))
        {
            var look = table.Resolve(id);
            Assert.NotNull(look);
            checkedLooks++;
            foreach (var path in look.Textures.Values.Append(look.Model).Concat(look.Items.Select(i => i.Model)).Concat(look.Items.Select(i => i.Texture).OfType<string>()))
            {
                if (Source.Read(path) is not { Length: > 0 })
                {
                    missing.Add($"{id}: {path}");
                }
            }
        }

        Assert.True(checkedLooks > 0);
        Assert.True(missing.Count <= checkedLooks / 10, string.Join("\n", missing.Take(10)));
    }

    [Fact]
    public void ArmoredCharactersWearTheirHelmetAndShoulders()
    {
        var table = CreatureDisplayTable.Read(Source);

        var look = table.Resolve(table.ArmoredCharacters(@"Human\Male").First());

        Assert.NotNull(look);
        Assert.NotNull(look.Geosets);
        Assert.True(look.Geosets.IsVisible(0));
        Assert.True(look.Textures.ContainsKey(M2TextureTypes.Body));
        Assert.Contains(look.Items, i => i.Attachment == 11 && i.Model.EndsWith("_HuM.m2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(look.Items, i => i.Attachment == 5);
        Assert.Contains(look.Items, i => i.Attachment == 6);
    }

    [Fact]
    public void HelmetsThatHideHairHideIt()
    {
        var table = CreatureDisplayTable.Read(Source);

        var look = table.Resolve(table.ArmoredCharacters(@"Human\Female").First());

        Assert.NotNull(look?.Geosets);
        Assert.DoesNotContain(Enumerable.Range(2, 98), look.Geosets.IsVisible);
        Assert.True(look.Geosets.IsVisible(1), "the scalp covers the head where hair was");
    }

    /// <summary>A server says what a creature holds by item id, which Item.dbc turns into a look.</summary>
    [Fact]
    public void ItemsHeldByServerCreaturesHaveModels()
    {
        var table = CreatureDisplayTable.Read(Source);

        // Items Brill's NPCs hold on an AzerothCore server: Vance Undergloom's and Innkeeper Renee's.
        foreach (var item in new[] { 1899, 2705 })
        {
            var display = table.ItemDisplayOf(item);
            Assert.True(display > 0, $"item {item} has no display");
            Assert.NotNull(table.ItemFiles(display));
        }

        Assert.Equal(0, table.ItemDisplayOf(int.MaxValue));
    }

    /// <summary>NPCs greet and say goodbye through NPCSounds: many human NPC displays have both.</summary>
    [Fact]
    public void NpcsHaveGreetings()
    {
        var table = CreatureDisplayTable.Read(Source);

        var sounds = table.DisplaysOfModel(@"Character\Human").Take(400).Select(d => table.Resolve(d)?.Sounds).OfType<CreatureSoundSet>().ToList();

        Assert.Contains(sounds, s => s.Greeting > 0 && s.Farewell > 0);
        Assert.Contains(sounds, s => s.Attack > 0 && s.Wound > 0 && s.Death > 0);
    }

    /// <summary>
    /// Another player's visible items are item ids: Item.dbc dresses them, saying a robe is a robe.
    /// 1899 is a one-handed sword, 6096 an apprentice's shirt and 56 an apprentice's robe.
    /// </summary>
    [Fact]
    public void PlayersAreDressedFromTheirItems()
    {
        var table = CreatureDisplayTable.Read(Source);
        var items = new int[19];
        items[(int)EquipmentSlot.Shirt] = 6096;
        items[(int)EquipmentSlot.Chest] = 56;
        items[(int)EquipmentSlot.MainHand] = 1899;
        items[(int)EquipmentSlot.Legs] = int.MaxValue;

        var (equipment, types) = table.DressFromItems(items);

        Assert.Equal(table.ItemDisplayOf(1899), equipment[(int)EquipmentSlot.MainHand]);
        Assert.Equal(20, types[(int)EquipmentSlot.Chest]);
        Assert.Equal(4, types[(int)EquipmentSlot.Shirt]);
        Assert.Equal(0, equipment[(int)EquipmentSlot.Legs]);
        var look = table.ResolveCharacter(new CharacterAppearance(5, 1, 0, 0, 0, 0, 0, equipment) { InventoryTypes = types });
        Assert.NotNull(look);
        Assert.Contains(look.BodyLayers!, l => l.Region == BodyRegion.LegUpper && l.Layer >= 10);
    }

    /// <summary>An undead warrior in a mix of gear, as a character list gives one: every piece of the body texture is found.</summary>
    [Fact]
    public void PlayerCharactersPaintTheirBody()
    {
        var table = CreatureDisplayTable.Read(Source);
        var gear = new int[(int)EquipmentSlot.Count];
        gear[(int)EquipmentSlot.Shoulders] = 29964;
        gear[(int)EquipmentSlot.Shirt] = 9995;
        gear[(int)EquipmentSlot.Chest] = 29958;
        gear[(int)EquipmentSlot.Waist] = 16663;
        gear[(int)EquipmentSlot.Legs] = 3442;
        gear[(int)EquipmentSlot.Feet] = 6903;
        gear[(int)EquipmentSlot.Hands] = 16657;
        gear[(int)EquipmentSlot.MainHand] = 23904;

        var look = table.ResolveCharacter(new CharacterAppearance(5, 0, 5, 4, 1, 3, 3, gear));

        Assert.NotNull(look);
        Assert.Equal(@"Character\Scourge\Male\ScourgeMale.m2", look.Model, ignoreCase: true);
        Assert.NotNull(look.BodyLayers);
        Assert.Contains(look.BodyLayers, l => l.Region == BodyRegion.Base);
        Assert.All(look.BodyLayers, l => Assert.Contains(l.Candidates, c => Source.Read(c) is { Length: > 0 }));
        Assert.Contains(look.Items, i => i.Model.Contains("Shoulder", StringComparison.OrdinalIgnoreCase));

        var painted = CharacterTexture.Compose(look.BodyLayers, Source.Read);
        Assert.Equal(CharacterTexture.Size, painted.Width);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(255, painted.Rgba8[i * 4096 * 4 + 3]));
    }

    /// <summary>A new human warrior wears the shirt, pants and boots the game starts them in, and holds a weapon.</summary>
    [Fact]
    public void NewCharactersWearTheirStartingOutfit()
    {
        var (gear, types) = CreatureDisplayTable.Read(Source).StartOutfit(1, 1, 0);

        Assert.NotEqual(0, gear[(int)EquipmentSlot.Chest] + gear[(int)EquipmentSlot.Shirt]);
        Assert.NotEqual(0, gear[(int)EquipmentSlot.Legs]);
        Assert.NotEqual(0, gear[(int)EquipmentSlot.MainHand]);
        Assert.NotEqual(0, types[(int)EquipmentSlot.Legs]);
    }

    [Fact]
    public void EveryRaceAndSexHasChoices()
    {
        var table = CreatureDisplayTable.Read(Source);
        foreach (var race in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 10, 11 })
        {
            foreach (var sex in new[] { 0, 1 })
            {
                var choices = table.ChoicesFor(race, sex, deathKnight: false);
                Assert.True(choices.Skins > 1 && choices.Faces > 1 && choices.HairStyles > 1, $"race {race} sex {sex}: {choices}");
                Assert.NotNull(table.ResolveCharacter(new CharacterAppearance(race, sex, 0, 0, 0, 0, 0, new int[(int)EquipmentSlot.Count])));
            }
        }
    }

    [Fact]
    public void UnknownDisplaysResolveToNothing() =>
        Assert.Null(CreatureDisplayTable.Read(Source).Resolve(int.MaxValue));

    [Fact]
    public void HeightsMatchTheTerrainMesh()
    {
        var map = WdtMap.Read(Source, "Azeroth");
        var terrain = AdtTerrain.Parse(Source.Read(map.TilePath(GoldshireX, GoldshireY))!, map.BigAlpha);
        var heights = new TerrainHeights(terrain, GoldshireX, GoldshireY);
        var mesh = TerrainMeshData.Build(terrain);

        var (originX, originZ) = WowCoords.TileOriginGodot(GoldshireX, GoldshireY);
        for (var i = 0; i < mesh.Positions.Length; i += 37)
        {
            var vertex = mesh.Positions[i];
            var wow = WowCoords.ToWow(new Vector3((float)(vertex.X + originX), vertex.Y, (float)(vertex.Z + originZ)));
            Assert.Equal(vertex.Y, heights.HeightAt(wow.X, wow.Y), 0.1f);
        }
    }
}
