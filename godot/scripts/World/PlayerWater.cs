using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using WowVR.Formats.Assets;
using WowVR.Formats.Database;
using WowVR.Formats.World;
using Godot;

namespace WowVR.World;

/// <summary>
/// Follows the player in and out of liquid: starts and stops swimming, plays the splash and the
/// underwater sound, muffles other sounds, and shows the underwater view.
/// </summary>
public partial class PlayerWater : Node
{
    /// <summary>How deep the feet must be to swim, in player heights. Shallower water is waded through.</summary>
    private const float SwimDepth = 0.65f;

    /// <summary>Must get this much shallower again to stop, so it can't flicker at the edge.</summary>
    private const float StopSwimDepth = 0.55f;

    /// <summary>How far under the feet rest when floating, in player heights, leaving the head out.</summary>
    private const float FloatDepth = 0.72f;

    private const float PlayerHeightYards = 1.75f;

    /// <summary>A fall or walk any slower than this, in yards per second, doesn't splash.</summary>
    private const float SplashSpeed = 1f;

    private const string UnderwaterLoop = @"Sound\Ambience\UndwaterLoop.wav";
    private const string Splash = @"Sound\Character\Footsteps\EnterWaterSplash\EnterWaterMediumA.wav";
    private const float MuffledHz = 900f;

    private readonly AudioStreamPlayer _loop = new() { Name = "UnderwaterLoop" };
    private readonly AudioStreamPlayer _splash = new() { Name = "Splash" };
    private MeshInstance3D _view = null!;
    private ShaderMaterial _viewMaterial = null!;
    private AudioEffectLowPassFilter? _muffle;
    private TileManager _tiles = null!;
    private IPlayer _player = null!;
    private SkyController _sky = null!;
    private Dictionary<int, LiquidKind> _kinds = [];
    private bool _swimming;
    private bool _feetIn;
    private float _lastFeetY;

    /// <summary>The eyes are under liquid.</summary>
    public bool Underwater { get; private set; }

    public bool Swimming => _swimming;

    /// <summary>Where the feet rest when floating here.</summary>
    public float FloatHeight { get; private set; }

    public void Start(IAssetSource source, TileManager tiles, IPlayer player, SkyController sky)
    {
        _tiles = tiles;
        _player = player;
        _sky = sky;
        _kinds = LiquidTypeRecord.ReadAll(source).ToDictionary(t => t.Id, t => t.Kind);

        AddChild(_loop);
        AddChild(_splash);
        Task.Run(() =>
        {
            var loop = source.Read(UnderwaterLoop) is { Length: > 0 } a ? ZoneAmbience.ToStream(UnderwaterLoop, a) : null;
            var splash = source.Read(Splash) is { Length: > 0 } b ? ZoneAmbience.ToStream(Splash, b) : null;
            if (loop is AudioStreamWav wav)
            {
                wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
                wav.LoopEnd = (int)(wav.GetLength() * wav.MixRate);
            }

            return (loop, splash);
        }).ContinueWith(t => Callable.From(() =>
        {
            if (IsInstanceValid(this))
            {
                (_loop.Stream, _splash.Stream) = t.Result;
            }
        }).CallDeferred(), TaskContinuationOptions.OnlyOnRanToCompletion);

        _viewMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/underwater.gdshader") };
        _view = new MeshInstance3D
        {
            Name = "UnderwaterView",
            Mesh = new QuadMesh { Size = Vector2.One * 2f, Material = _viewMaterial },
            ExtraCullMargin = 16384f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_view);
    }

    public override void _ExitTree() => SetMuffled(false);

    public override void _PhysicsProcess(double delta)
    {
        if (_tiles is null)
        {
            return;
        }

        var feet = _player.Feet;
        var height = PlayerHeightYards * AppSettings.WorldScale;
        var liquid = _tiles.LiquidAt(feet + Vector3.Up * height);
        var depth = liquid is { } here ? here.Surface - feet.Y : 0f;

        var feetIn = depth > 0f;
        var falling = (_lastFeetY - feet.Y) / (float)delta;
        if (feetIn && !_feetIn && falling > SplashSpeed && falling < 200f && _splash.Stream is not null)
        {
            _splash.VolumeDb = CreatureSoundPlayer.VolumeDb(1f);
            _splash.Play();
        }

        (_feetIn, _lastFeetY) = (feetIn, feet.Y);

        _swimming = !_player.Flying && liquid is not null && depth > (_swimming ? StopSwimDepth : SwimDepth) * height;
        if (liquid is { } water)
        {
            FloatHeight = water.Surface - FloatDepth * height;
        }

        _player.SetSwimming(_swimming, FloatHeight);
        UpdateView();
    }

    private void UpdateView()
    {
        if (GetViewport().GetCamera3D() is not { } camera)
        {
            return;
        }

        var eye = camera.GlobalPosition;
        var above = _tiles.LiquidAt(eye);
        var under = above is { } liquid && eye.Y < liquid.Surface;
        if (under != Underwater)
        {
            Underwater = under;
            _view.Visible = under;
            SetMuffled(under);
            if (under && _loop.Stream is not null)
            {
                _loop.VolumeDb = CreatureSoundPlayer.VolumeDb(1f);
                _loop.Play();
            }
            else
            {
                _loop.Stop();
            }
        }

        var kind = under ? _kinds.GetValueOrDefault(above!.Value.Type) : (LiquidKind?)null;
        _sky.Underwater = kind;
        if (kind is { } current)
        {
            _viewMaterial.SetShaderParameter("tint", current switch
            {
                LiquidKind.Magma => new Vector3(1f, 0.55f, 0.35f),
                LiquidKind.Slime => new Vector3(0.65f, 0.95f, 0.5f),
                _ => new Vector3(0.65f, 0.85f, 0.95f),
            });
            _viewMaterial.SetShaderParameter("haze", current switch
            {
                LiquidKind.Magma => new Vector3(0.7f, 0.2f, 0.05f),
                LiquidKind.Slime => new Vector3(0.25f, 0.4f, 0.1f),
                _ => new Vector3(0.22f, 0.4f, 0.42f),
            });
        }
    }

    /// <summary>A sound heard through water loses its highs. The effect stays on the bus across reloads, so it's found again.</summary>
    private void SetMuffled(bool on)
    {
        const int Master = 0;
        if (_muffle is null)
        {
            for (var i = 0; i < AudioServer.GetBusEffectCount(Master); i++)
            {
                if (AudioServer.GetBusEffect(Master, i) is AudioEffectLowPassFilter existing)
                {
                    _muffle = existing;
                    AudioServer.SetBusEffectEnabled(Master, i, on);
                    return;
                }
            }

            if (!on)
            {
                return;
            }

            _muffle = new AudioEffectLowPassFilter { CutoffHz = MuffledHz };
            AudioServer.AddBusEffect(Master, _muffle);
        }

        for (var i = 0; i < AudioServer.GetBusEffectCount(Master); i++)
        {
            if (AudioServer.GetBusEffect(Master, i) == _muffle)
            {
                AudioServer.SetBusEffectEnabled(Master, i, on);
            }
        }
    }
}
