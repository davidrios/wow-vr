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
    private string _clip = "";
    private double _soundPosition;
    private BillboardBones? _billboards;
    private readonly List<MeshInstance3D> _meshes = [];
    private Color _light = new(1f, 1f, 1f, 0f);
    private Color _lightTarget = new(1f, 1f, 1f, 0f);
    private bool _lightKnown;
    private double _sinceAnimated;

    /// <summary>How far the floor's light has replaced the sky's, from 0 to 1.</summary>
    public float FloorLightAmount => _light.A;

    /// <summary>A clip reached a moment that makes a sound.</summary>
    public event Action<ClipSoundEvent>? SoundEvent;

    /// <summary>Kept here, because reading it from the player makes a new string each time.</summary>
    public string Clip => _clip;

    public Skeleton3D Skeleton { get; private set; } = null!;

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

        // Every skeleton has a hidden, active physical bone simulator for compatibility, and any active
        // modifier otherwise rebuilds the pose and skin every frame, even of a creature that stands still.
        Skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
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
            foreach (var particle in item.Particles)
            {
                itemMesh.AddChild(particle.NewNode(particle.Position));
            }
        }

        foreach (var particle in asset.Particles)
        {
            var attachment = new BoneAttachment3D { BoneName = particle.BoneName };
            Skeleton.AddChild(attachment);
            attachment.AddChild(particle.Particle.NewNode(particle.Offset));
        }

        if (asset.Skeleton.Billboards.Length > 0)
        {
            _billboards = new BillboardBones(asset.Skeleton.Billboards);
        }

        // Advanced by Animate, so creatures further away can update less often.
        _player = new AnimationPlayer { RootNode = new NodePath(".."), CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual };
        _body.AddChild(_player);
        _player.AddAnimationLibrary("", asset.Animations);
        _player.AnimationFinished += _ =>
        {
            _clip = "";
            PlayNextIdle();
        };

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
    public void StartIdle()
    {
        PlayNextIdle();
        _player.Advance(0);

        // Spread out, so creatures on the same rate don't all update in the same frame.
        _sinceAnimated = Random.Shared.NextDouble() * 0.05;
    }

    /// <summary>
    /// Moves the animation on, at most once every <paramref name="interval"/> seconds. Most of a
    /// creature's cost is posing its skeleton, so far ones update less often.
    /// </summary>
    public void Animate(double delta, double interval)
    {
        _sinceAnimated += delta;
        if (_sinceAnimated >= interval)
        {
            _player.Advance(_sinceAnimated);
            _billboards?.Apply(Skeleton);
            _sinceAnimated = 0;
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
        // Setting a transform notifies every bone attachment and mesh below, even when it's the same.
        if (Position != position)
        {
            Position = position;
        }

        var target = WowCoords.YawFromOrientation(orientation);
        var yaw = _placed ? Mathf.LerpAngle(_yaw, target, 1f - Mathf.Exp(-TurnRate * (float)delta)) : target;
        if (!_placed || yaw != _yaw)
        {
            _yaw = Mathf.Abs(Mathf.AngleDifference(yaw, target)) < 1e-4f ? target : yaw;
            _body.Rotation = new Vector3(0, _yaw, 0);
        }

        _placed = true;

        if (moving != _moving)
        {
            _moving = moving;
            if (moving && _asset.Skeleton.WalkClip is { } walk)
            {
                // Plays at the speed that keeps its feet from sliding.
                var speed = _asset.Skeleton.WalkSpeed * _asset.Scale;
                _player.Play(walk, BlendSeconds, speed > 0f ? walkSpeed / speed : 1f);
                _clip = walk;
            }
            else
            {
                PlayNextIdle();
            }
        }

        if (_name is not null && _name.Visible != (AppSettings.NamePlates && distance < NameRange))
        {
            _name.Visible = !_name.Visible;
        }
    }

    /// <summary>Fires the sound events the clip passed since last time. Only worth calling near the player.</summary>
    public void CheckSoundEvents()
    {
        var clip = _clip;
        if (!_asset.Skeleton.ClipSounds.TryGetValue(clip, out var sounds))
        {
            // Still noted, so coming back to a clip with sounds isn't taken for carrying on in it.
            _soundClip = clip;
            return;
        }

        var position = _player.CurrentAnimationPosition;
        var (from, restarted) = clip == _soundClip ? (_soundPosition, position < _soundPosition) : (position, false);
        (_soundClip, _soundPosition) = (clip, position);

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
                _clip = clip.Name;
                return;
            }
        }
    }
}
