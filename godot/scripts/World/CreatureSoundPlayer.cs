using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>Plays creatures' sounds at their positions, loading each file once, off the main thread.</summary>
public partial class CreatureSoundPlayer : Node
{
    /// <summary>A crowd's footsteps share these, and sounds past them are skipped.</summary>
    private const int MaxVoices = 16;
    public const float MaxDistance = 40f;
    private const float UnitSize = 6f;
    private const double GiveUpSeconds = 1;

    private readonly List<AudioStreamPlayer3D> _voices = [];
    private readonly ConcurrentDictionary<string, Task<AudioStream?>> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(AudioStream Stream, Vector3 Position, float Volume, ulong Asked)> _ready = new();
    private Dictionary<int, SoundEntryRecord> _entries = [];
    private IAssetSource _source = null!;

    public int PlayedCount { get; private set; }

    public void Start(IAssetSource source)
    {
        _source = source;
        _ = Task.Run(() => SoundEntryRecord.ReadAll(source).ToDictionary(e => e.Id))
            .ContinueWith(t => _entries = t.Result, TaskContinuationOptions.OnlyOnRanToCompletion);
        for (var i = 0; i < MaxVoices; i++)
        {
            var voice = NewPlayer();
            _voices.Add(voice);
            AddChild(voice);
        }
    }

    public static AudioStreamPlayer3D NewPlayer() => new()
    {
        UnitSize = UnitSize,
        MaxDistance = MaxDistance,
        AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
    };

    public static float VolumeDb(float volume) => Mathf.LinearToDb(Mathf.Max(volume * AppSettings.AmbienceVolume, 0.0001f));

    /// <summary>A SoundEntries row's sound, at a position in the scene. Picks its file by weight.</summary>
    public void Play(int entryId, Vector3 position)
    {
        if (entryId == 0 || !_entries.TryGetValue(entryId, out var entry) || PathOf(entry) is not { } path)
        {
            return;
        }

        var asked = Time.GetTicksMsec();
        Load(path).ContinueWith(
            t =>
            {
                if (t.Result is { } stream)
                {
                    _ready.Enqueue((stream, position, entry.Volume, asked));
                }
            },
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Loads a looping sound for a player that follows its creature. Null when it has none.</summary>
    public async Task<(AudioStream Stream, float Volume)?> LoadLoopAsync(int entryId)
    {
        if (entryId == 0 || !_entries.TryGetValue(entryId, out var entry) || PathOf(entry) is not { } path || await Load(path) is not { } stream)
        {
            return null;
        }

        switch (stream)
        {
            case AudioStreamWav wav:
                wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                wav.LoopEnd = (int)(wav.GetLength() * wav.MixRate);
                break;
            case AudioStreamMP3 mp3:
                mp3.Loop = true;
                break;
        }

        return (stream, entry.Volume);
    }

    private static string? PathOf(SoundEntryRecord entry)
    {
        if (entry.Files.Count == 0)
        {
            return null;
        }

        var total = entry.Files.Sum(f => Math.Max(f.Weight, 1));
        var choice = Random.Shared.Next(total);
        foreach (var (name, weight) in entry.Files)
        {
            choice -= Math.Max(weight, 1);
            if (choice < 0)
            {
                return $@"{entry.Directory}\{name}";
            }
        }

        return null;
    }

    private Task<AudioStream?> Load(string path) => _streams.GetOrAdd(path, p => Task.Run(() =>
    {
        var data = _source.Read(p);
        if (data is not { Length: > 0 })
        {
            GD.PushWarning($"Sound missing: {p}");
            return null;
        }

        return ZoneAmbience.ToStream(p, data);
    }));

    public override void _Process(double delta)
    {
        while (_ready.TryDequeue(out var sound))
        {
            if ((Time.GetTicksMsec() - sound.Asked) / 1000.0 > GiveUpSeconds)
            {
                continue;
            }

            var voice = _voices.FirstOrDefault(v => !v.Playing);
            if (voice is null)
            {
                continue;
            }

            voice.Stream = sound.Stream;
            voice.GlobalPosition = sound.Position;
            voice.VolumeDb = VolumeDb(sound.Volume);
            voice.Play();
            PlayedCount++;
        }
    }
}
