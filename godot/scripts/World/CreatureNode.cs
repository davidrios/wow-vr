using System;
using System.Collections.Generic;
using WowVR.Converters;
using WowVR.Formats;
using Godot;

namespace WowVR.World;

/// <summary>One placed creature: its skeleton, mesh, worn items, animation and name.</summary>
public partial class CreatureNode : Node3D
{
    public const string SkeletonName = "Skeleton";

    /// <summary>A name only shows this close, in yards.</summary>
    private const float NameRange = 35f;
    private const float NamePixelSize = 0.008f;
    private const int NameFontSize = 48;
    private const float NameClearance = 0.4f;
    private const float TurnRate = 8f;
    private const double BlendSeconds = 0.2;

    /// <summary>How quickly light changes between the sky's and a floor's, per second.</summary>
    private const float LightFadeRate = 2f;
    private const float LightSettled = 0.002f;
    private const string LightParameter = "floor_light";

    private CreatureAsset _asset = null!;
    private Node3D _body = null!;
    private AnimationPlayer _player = null!;
    private Label3D? _name;
    private float _yaw;
    private bool _moving;
    private bool _placed;
    private string _soundClip = "";
    private double _soundPosition;
    private BillboardBones? _billboards;
    private readonly List<MeshInstance3D> _meshes = [];
    private Color _light = new(1f, 1f, 1f, 0f);
    private Color _lightTarget = new(1f, 1f, 1f, 0f);
    private bool _lightKnown;

    /// <summary>How far the floor's light has replaced the sky's, from 0 to 1.</summary>
    public float FloorLightAmount => _light.A;

    /// <summary>A clip reached a moment that makes a sound.</summary>
    public event Action<ClipSoundEvent>? SoundEvent;

    public string Clip => _player.CurrentAnimation;

    public Skeleton3D Skeleton { get; private set; } = null!;

    public bool Animated => _player.Active;

    public void Build(CreatureAsset asset, string name)
    {
        _asset = asset;
        _body = new Node3D { Scale = Vector3.One * asset.Scale };
        AddChild(_body);

        Skeleton = new Skeleton3D { Name = SkeletonName };
        var bones = asset.Skeleton.Bones;
        foreach (var bone in bones)
        {
            Skeleton.AddBone(bone.Name);
        }

        for (var i = 0; i < bones.Length; i++)
        {
            Skeleton.SetBoneParent(i, bones[i].Parent);
            Skeleton.SetBoneRest(i, new Transform3D(Basis.Identity, bones[i].Offset));
        }

        Skeleton.ResetBonePoses();
        _body.AddChild(Skeleton);
        var body = new MeshInstance3D
        {
            Mesh = asset.Mesh,
            Skin = asset.Skeleton.Skin,
            Skeleton = new NodePath(".."),
        };
        Skeleton.AddChild(body);
        _meshes.Add(body);

        foreach (var item in asset.Items)
        {
            var attachment = new BoneAttachment3D { BoneName = item.BoneName };
            Skeleton.AddChild(attachment);
            var itemMesh = new MeshInstance3D
            {
                Mesh = item.Mesh,
                Position = item.Offset,
            };
            attachment.AddChild(itemMesh);
            _meshes.Add(itemMesh);
        }

        if (asset.Skeleton.Billboards.Length > 0)
        {
            _billboards = new BillboardBones { Bones = asset.Skeleton.Billboards };
            Skeleton.AddChild(_billboards);
        }

        _player = new AnimationPlayer { RootNode = new NodePath("..") };
        _body.AddChild(_player);
        _player.AddAnimationLibrary("", asset.Skeleton.Animations);
        _player.AnimationFinished += _ => PlayNextIdle();

        if (name.Length > 0)
        {
            _name = new Label3D
            {
                Text = name,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                PixelSize = NamePixelSize,
                FontSize = NameFontSize,
                Modulate = new Color(1f, 0.9f, 0.3f),
                OutlineSize = 8,
                Position = Vector3.Up * ((asset.Skeleton.Bounds.End.Y * asset.Scale) + NameClearance),
                NoDepthTest = false,
                Visible = false,
            };
            AddChild(_name);
        }
    }

    /// <summary>Starts animating, once the creature is in the scene.</summary>
    public void StartIdle() => PlayNextIdle();

    /// <summary>Stops bones moving, keeping the current pose, for creatures past the animation cap.</summary>
    public void SetAnimated(bool animated)
    {
        if (_player.Active != animated)
        {
            _player.Active = animated;
            if (_billboards is not null)
            {
                _billboards.Active = animated;
            }
        }
    }

    /// <summary>What light the ground under the creature gives: a gamma-space color, or null for the sky's.</summary>
    public void SetGroundLight(System.Numerics.Vector3? light)
    {
        // Leaving a building keeps the color, so only the amount fades.
        _lightTarget = light is { } c ? new Color(c.X, c.Y, c.Z, 1f) : new Color(_lightTarget.R, _lightTarget.G, _lightTarget.B, 0f);

        // Starting to fade from the sky, the color has nothing to fade from.
        if (_light.A < LightSettled)
        {
            _light = new Color(_lightTarget.R, _lightTarget.G, _lightTarget.B, _light.A);
        }

        // Its first answer isn't a change it could have been seen making.
        if (!_lightKnown)
        {
            _lightKnown = true;
            _light = _lightTarget;
            ApplyLight();
        }
    }

    /// <summary>Moves the light toward what the ground gives.</summary>
    public void FadeLight(double delta)
    {
        if (_light == _lightTarget)
        {
            return;
        }

        _light = _light.Lerp(_lightTarget, 1f - Mathf.Exp(-LightFadeRate * (float)delta));
        if (Mathf.Abs(_light.A - _lightTarget.A) < LightSettled
            && Mathf.Abs(_light.R - _lightTarget.R) < LightSettled
            && Mathf.Abs(_light.G - _lightTarget.G) < LightSettled
            && Mathf.Abs(_light.B - _lightTarget.B) < LightSettled)
        {
            _light = _lightTarget;
        }

        ApplyLight();
    }

    private void ApplyLight()
    {
        foreach (var mesh in _meshes)
        {
            mesh.SetInstanceShaderParameter(LightParameter, _light);
        }
    }

    /// <summary>Applies where the creature should be. The world clock decides this, and only its turning is smoothed.</summary>
    public void Move(Vector3 position, float orientation, bool moving, float walkSpeed, float distance, double delta)
    {
        Position = position;

        var target = WowCoords.YawFromOrientation(orientation);
        _yaw = _placed ? Mathf.LerpAngle(_yaw, target, 1f - Mathf.Exp(-TurnRate * (float)delta)) : target;
        _placed = true;
        _body.Rotation = new Vector3(0, _yaw, 0);

        if (moving != _moving)
        {
            _moving = moving;
            if (moving && _asset.Skeleton.WalkClip is { } walk)
            {
                // Plays at the speed that keeps its feet from sliding.
                var speed = _asset.Skeleton.WalkSpeed * _asset.Scale;
                _player.Play(walk, BlendSeconds, speed > 0f ? walkSpeed / speed : 1f);
            }
            else
            {
                PlayNextIdle();
            }
        }

        if (_name is not null)
        {
            _name.Visible = AppSettings.NamePlates && distance < NameRange;
        }
    }

    /// <summary>Fires the sound events the clip passed since last time. Only worth calling near the player.</summary>
    public void CheckSoundEvents()
    {
        string clip = _player.CurrentAnimation;
        if (clip.Length == 0)
        {
            return;
        }

        var position = _player.CurrentAnimationPosition;
        var (from, restarted) = clip == _soundClip ? (_soundPosition, position < _soundPosition) : (position, false);
        (_soundClip, _soundPosition) = (clip, position);
        if (!_asset.Skeleton.ClipSounds.TryGetValue(clip, out var sounds))
        {
            return;
        }

        foreach (var sound in sounds)
        {
            var passed = restarted ? sound.Time > from || sound.Time <= position : sound.Time > from && sound.Time <= position;
            if (passed)
            {
                SoundEvent?.Invoke(sound);
            }
        }
    }

    private void PlayNextIdle()
    {
        if (_moving)
        {
            return;
        }

        var clips = _asset.Skeleton.IdleClips;
        if (clips.Length == 0)
        {
            return;
        }

        var total = 0;
        foreach (var clip in clips)
        {
            total += clip.Weight;
        }

        var choice = Random.Shared.Next(total);
        foreach (var clip in clips)
        {
            choice -= clip.Weight;
            if (choice < 0)
            {
                // Fidgets are cosmetic, so they needn't match between players.
                _player.Play(clip.Name, BlendSeconds);
                return;
            }
        }
    }
}
