using System;
using System.Threading.Tasks;
using WowVR.Formats.Assets;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// Watches which area the player is in, shows its name on entering, and plays its music and
/// ambient sound from the client's files. Files load off the main thread.
/// </summary>
public partial class ZoneAmbience : Node
{
    private const double CheckInterval = 0.5;
    private const float FadeSeconds = 3f;
    private const float SilentDb = -60f;
    private const double NameHoldSeconds = 3.5;
    private const double NameFadeSeconds = 1.5;

    private static readonly Color NameColor = new(1f, 0.82f, 0.25f);

    private readonly Random _random = new();
    private readonly AudioStreamPlayer _music = new() { Name = "Music" };
    private readonly AudioStreamPlayer _ambience = new() { Name = "Ambience" };

    private IAssetSource _source = null!;
    private ZoneTable _zones = null!;
    private TileManager _tiles = null!;
    private SkyController _sky = null!;

    private Label3D? _name;
    private double _nameAge;
    private double _sinceCheck;
    private int _areaId = -1;
    private bool _isDay;
    private ZoneInfo? _zone;
    private SoundChoice? _musicChoice;
    private SoundChoice? _ambienceChoice;
    private double _silenceLeft;
    private bool _musicWanted;
    private float _musicLevel = 1f;
    private float _ambienceLevel = 1f;

    /// <summary>The zone the player is in, for checks.</summary>
    public string ZoneName => _zone?.Name ?? "";

    /// <summary>The music file playing, for checks.</summary>
    public string MusicFile { get; private set; } = "";

    public void Start(IAssetSource source, ZoneTable zones, TileManager tiles, SkyController sky)
    {
        _source = source;
        _zones = zones;
        _tiles = tiles;
        _sky = sky;
        AddChild(_music);
        AddChild(_ambience);
        _music.Finished += OnMusicFinished;
        _ambience.Finished += () => _ambience.Play();
    }

    public override void _Process(double delta)
    {
        if (_tiles is null)
        {
            return;
        }

        _sinceCheck += delta;
        if (_sinceCheck >= CheckInterval)
        {
            _sinceCheck = 0;
            CheckZone();
        }

        UpdateVolumes(delta);
        UpdateSilence(delta);
        UpdateName(delta);
    }

    private void CheckZone()
    {
        if (_tiles.AreaAtPlayer() is not { } areaId)
        {
            return;
        }

        var enteredArea = areaId != _areaId;
        var dayChanged = _sky.IsDay != _isDay;
        if (!enteredArea && !dayChanged)
        {
            return;
        }

        _areaId = areaId;
        _isDay = _sky.IsDay;

        if (enteredArea)
        {
            var previousName = _zone?.Name;
            _zone = _zones.Find(areaId);
            if (_zone is not null && _zone.Name != previousName)
            {
                GD.Print($"Entered {_zone.Name} (area {areaId})");
                ShowName(_zone.Name);
            }
        }

        var music = _isDay ? _zone?.Music?.Day : _zone?.Music?.Night;
        if (music != _musicChoice)
        {
            _musicChoice = music;
            _silenceLeft = 0;
            StartMusic();
        }

        var ambience = _isDay ? _zone?.AmbienceDay : _zone?.AmbienceNight;
        if (ambience != _ambienceChoice)
        {
            _ambienceChoice = ambience;
            StartAmbience();
        }
    }

    private void StartMusic()
    {
        _musicWanted = _musicChoice is not null;
        if (_musicChoice is null)
        {
            _music.Stop();
            MusicFile = "";
            return;
        }

        Load(_musicChoice, _music, file => MusicFile = file);
    }

    private void StartAmbience()
    {
        if (_ambienceChoice is null)
        {
            _ambience.Stop();
            return;
        }

        Load(_ambienceChoice, _ambience, _ => { });
    }

    private void Load(SoundChoice choice, AudioStreamPlayer player, Action<string> started)
    {
        var path = choice.Pick(_random.NextDouble());
        Task.Run(() =>
        {
            AudioStream? stream = null;
            try
            {
                if (_source.Read(path) is { Length: > 0 } data)
                {
                    stream = ToStream(path, data);
                }
                else
                {
                    GD.PushWarning($"Sound missing or empty: {path}");
                }
            }
            catch (Exception e)
            {
                GD.PushWarning($"Could not read {path}: {e.Message}");
            }

            if (stream is not null)
            {
                Callable.From(() => Play(player, stream, choice, path, started)).CallDeferred();
            }
        });
    }

    private void Play(AudioStreamPlayer player, AudioStream stream, SoundChoice choice, string path, Action<string> started)
    {
        if (!IsInsideTree())
        {
            return;
        }

        // The zone may have moved on while this loaded.
        if (choice != (player == _music ? _musicChoice : _ambienceChoice))
        {
            return;
        }

        var level = Mathf.Clamp(choice.Volume, 0f, 1f);
        if (player == _music)
        {
            _musicLevel = level;
        }
        else
        {
            _ambienceLevel = level;
        }

        player.Stream = stream;
        player.VolumeDb = SilentDb;
        player.Play();
        GD.Print($"Playing {path}");
        started(path);
    }

    public static AudioStream? ToStream(string path, byte[] data)
    {
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return new AudioStreamMP3 { Data = data };
        }

        return path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioStreamWav.LoadFromBuffer(data) : null;
    }

    private void OnMusicFinished()
    {
        var music = _zone?.Music;
        var (min, max) = (music?.SilenceMinMs ?? 0, music?.SilenceMaxMs ?? 0);
        _silenceLeft = _random.Next(Math.Min(min, max), Math.Max(min, max) + 1) / 1000.0;
        MusicFile = "";
    }

    private void UpdateSilence(double delta)
    {
        if (_silenceLeft <= 0)
        {
            return;
        }

        _silenceLeft -= delta;
        if (_silenceLeft <= 0 && _musicWanted)
        {
            StartMusic();
        }
    }

    private void UpdateVolumes(double delta)
    {
        Fade(_music, _musicLevel * AppSettings.MusicVolume, delta);
        Fade(_ambience, _ambienceLevel * AppSettings.AmbienceVolume, delta);
    }

    private static void Fade(AudioStreamPlayer player, float level, double delta)
    {
        var target = level > 0f ? Mathf.LinearToDb(level) : SilentDb;
        player.VolumeDb = Mathf.MoveToward(player.VolumeDb, target, (float)delta * -SilentDb / FadeSeconds);
    }

    private void ShowName(string name)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera is null)
        {
            return;
        }

        _name?.QueueFree();

        // In 3D because Godot doesn't draw 2D UI into a headset.
        _name = new Label3D
        {
            Text = name,
            Position = new Vector3(0, 0.45f, -2f),
            PixelSize = 0.002f,
            FontSize = 72,
            OutlineSize = 12,
            Modulate = NameColor,
            NoDepthTest = true,
            Shaded = false,
            FixedSize = false,
            RenderPriority = 10,
            OutlineRenderPriority = 9,
        };
        camera.AddChild(_name);
        _nameAge = 0;
    }

    private void UpdateName(double delta)
    {
        if (_name is null)
        {
            return;
        }

        _nameAge += delta;
        if (_nameAge >= NameHoldSeconds + NameFadeSeconds)
        {
            _name.QueueFree();
            _name = null;
            return;
        }

        var alpha = _nameAge <= NameHoldSeconds ? 1f : 1f - (float)((_nameAge - NameHoldSeconds) / NameFadeSeconds);
        _name.Modulate = NameColor with { A = alpha };
        _name.OutlineModulate = new Color(0, 0, 0, alpha);
    }
}
