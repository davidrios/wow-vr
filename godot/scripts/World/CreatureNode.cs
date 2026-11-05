using System;
using System.Collections.Generic;
using WowVR.Converters;
using WowVR.Formats;
using WowVR.Formats.Models;
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
    private static readonly StringName LightParameter = "floor_light"; // Made once, since a string makes a new StringName per call.

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
            if (item.Attachment is RightShoulderAttachment or LeftShoulderAttachment)
            {
                _shoulders.Add(itemMesh);
            }
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
        _player.AnimationFinished += _ => OnClipFinished();

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
        if (_casting && (_castLeft -= delta) <= 0)
        {
            StopSpellAnimation();
        }

        _sinceAnimated += delta;
        if (_sinceAnimated >= interval)
        {
            _player.Advance(_sinceAnimated);
            _billboards?.Apply(Skeleton);
            if (_hiddenBone >= 0)
            {
                // Tiny rather than zero, since what's attached to it needs a basis it can invert.
                Skeleton.SetBonePoseScale(_hiddenBone, Vector3.One * HiddenScale);
            }

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

    private bool _dead;
    private bool _selected;
    private bool _oneShot;
    private Color _nameColor = new(1f, 0.9f, 0.3f);
    private MeshInstance3D? _ring;

    /// <summary>Set by the player's body, which chooses its own clips with <see cref="PlayMotion"/>.</summary>
    private bool _motionChosen;

    /// <summary>The unit's height, for picking it and placing what floats over it.</summary>
    public float Height => _asset.Skeleton.Bounds.End.Y * _asset.Scale * Scale.Y;

    /// <summary>Whether its selection ring shows.</summary>
    public bool RingShown => _ring is { Visible: true };

    /// <summary>The way it faces now, as a WoW orientation, smoothed as it turns.</summary>
    public float Facing => _yaw - Mathf.Pi / 2;

    /// <summary>How wide it is, for picking it.</summary>
    public float Radius => Mathf.Max(Mathf.Max(_asset.Skeleton.Bounds.Size.X, _asset.Skeleton.Bounds.Size.Z) * 0.5f * _asset.Scale * Scale.X, 0.4f);

    public bool IsDead => _dead;

    /// <summary>
    /// Dead units fall and lie still, and the selected one shows its name and a ring at its feet
    /// in <paramref name="color"/>, the way it stands toward the player.
    /// </summary>
    public void SetState(bool dead, bool selected, Color? color = null)
    {
        if (color is { } c && c != _nameColor)
        {
            _nameColor = c;
            if (_name is not null)
            {
                _name.Modulate = c;
            }

            if (_ring?.MaterialOverride is StandardMaterial3D ringMaterial)
            {
                ringMaterial.AlbedoColor = c;
            }
        }

        if (selected != _selected)
        {
            _selected = selected;
            if (selected && _ring is null)
            {
                var radius = Radius / Mathf.Max(Scale.X, 0.01f);
                _ring = new MeshInstance3D
                {
                    Name = "SelectionRing",
                    Mesh = new TorusMesh { InnerRadius = radius * 0.9f, OuterRadius = radius, Rings = 32, RingSegments = 4 },
                    Scale = new Vector3(1f, 0.05f, 1f),
                    Position = Vector3.Up * 0.05f,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    MaterialOverride = new StandardMaterial3D
                    {
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        AlbedoColor = _nameColor,
                    },
                };
                AddChild(_ring);
            }

            if (_ring is not null)
            {
                _ring.Visible = selected;
            }
        }

        if (dead == _dead)
        {
            return;
        }

        _dead = dead;
        _oneShot = false;
        _casting = false;
        if (dead)
        {
            if (ClipFor(M2SequenceIds.Death) is { } death)
            {
                Play(death, 1f);
            }
            else if (ClipFor(M2SequenceIds.Dead) is { } lying)
            {
                Play(lying, 1f);
            }
        }
        else
        {
            _moving = false;
            PlayNextIdle();
        }
    }

    private bool _casting;
    private bool _castLoops;
    private double _castLeft;

    /// <summary>
    /// Plays a spell's animation, as a kit asks: held, looping, until <see cref="StopSpellAnimation"/>
    /// while it casts, or once. A model without it plays the first of its fallbacks it has; one with
    /// none of them stays as it was. False then.
    /// </summary>
    public bool PlaySpellAnimation(int sequence, bool hold, Func<int, int> fallback)
    {
        // A moment's animation doesn't break a held cast or channel, as another spell going off during it.
        if (_dead || (!hold && _casting && _castLoops))
        {
            return false;
        }

        // Up a few links of the fallback chain, stopping before standing.
        string? clip = null;
        for (var tries = 0; sequence > 0 && tries < 4 && (clip = ClipFor(sequence)) is null; tries++)
        {
            sequence = fallback(sequence);
        }

        if (clip is null || sequence <= 0)
        {
            return false;
        }

        _casting = true;
        _castLoops = hold;

        // A once that's a looping clip, as some cast kits use, plays for about a beat.
        _castLeft = hold ? double.PositiveInfinity : M2SequenceIds.PlaysOnce(sequence) ? 3 : 1.2;
        _player.Play(clip, BlendSeconds);
        _clip = clip;
        return true;
    }

    /// <summary>The cast ended: back to standing or walking.</summary>
    public void StopSpellAnimation()
    {
        if (!_casting)
        {
            return;
        }

        _casting = false;
        _moving = false;
        _clip = "";
        PlayNextIdle();
    }

    /// <summary>
    /// Hangs a spell effect on the first of these attachment points the model has, or at its feet
    /// when it has none. The caller removes it when its moment is over.
    /// </summary>
    public Node3D AttachEffect(EffectAsset effect, IReadOnlyList<int> attachments)
    {
        Node3D parent = this;
        var offset = Vector3.Zero;
        foreach (var id in attachments)
        {
            if (_asset.Attachments.TryGetValue(id, out var point))
            {
                var bone = new BoneAttachment3D { BoneName = point.Bone };
                Skeleton.AddChild(bone);
                parent = bone;
                offset = point.Offset;
                break;
            }
        }

        var holder = new Node3D { Name = "SpellEffect", Position = offset };
        parent.AddChild(holder);
        if (effect.Mesh is { } mesh)
        {
            holder.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }

        foreach (var particle in effect.Particles)
        {
            holder.AddChild(particle.NewNode(particle.Position, fresh: true));
        }

        // Removing the effect takes the bone attachment made for it too.
        return parent is BoneAttachment3D ? parent : holder;
    }

    /// <summary>Swings once, then goes back to what it was doing.</summary>
    public void PlayAttack()
    {
        if (_dead || (ClipFor(M2SequenceIds.Attack1H) ?? ClipFor(M2SequenceIds.AttackUnarmed) ?? ClipFor(M2SequenceIds.Attack2H)) is not { } attack)
        {
            return;
        }

        _oneShot = true;
        Play(attack, 1f);
    }

    /// <summary>The clip for a sequence id, or null when the model has none.</summary>
    public string? ClipFor(int sequence) => sequence switch
    {
        M2SequenceIds.Walk => _asset.Skeleton.WalkClip,
        M2SequenceIds.Run => _asset.Skeleton.RunClip ?? _asset.Skeleton.WalkClip,
        _ => _asset.Skeleton.Clips.GetValueOrDefault(sequence),
    };

    /// <summary>How fast a clip moves at its own rate, in yards a second, or 0.</summary>
    public float ClipSpeed(int sequence) => sequence switch
    {
        M2SequenceIds.Walk => _asset.Skeleton.WalkSpeed * _asset.Scale,
        M2SequenceIds.Run => (_asset.Skeleton.RunClip is null ? _asset.Skeleton.WalkSpeed : _asset.Skeleton.RunSpeed) * _asset.Scale,
        _ => _asset.Skeleton.ClipSpeeds.GetValueOrDefault(sequence) * _asset.Scale,
    };

    /// <summary>
    /// For the player's body: plays a sequence by id, at the rate that matches <paramref name="speed"/>
    /// when it moves, or idles with Stand. Falls back to running or standing when the model lacks it.
    /// </summary>
    public void PlayMotion(int sequence, float speed, float? rateOverride = null)
    {
        _motionChosen = true;
        if (_dead || _oneShot || _casting)
        {
            return;
        }

        if (sequence == M2SequenceIds.Stand)
        {
            if (_moving || _clip.Length == 0)
            {
                _moving = false;
                PlayNextIdle();
            }

            return;
        }

        var clip = ClipFor(sequence);
        if (clip is null)
        {
            sequence = sequence is not M2SequenceIds.SwimIdle && speed > 0.1f ? M2SequenceIds.Run : M2SequenceIds.Stand;
            if (sequence == M2SequenceIds.Stand)
            {
                PlayMotion(sequence, 0);
                return;
            }

            clip = ClipFor(sequence);
            if (clip is null)
            {
                return;
            }
        }

        var clipSpeed = ClipSpeed(sequence);
        var rate = rateOverride ?? (clipSpeed > 0.1f && speed > 0.1f ? speed / clipSpeed : 1f);
        _moving = true;
        if (clip != _clip || Math.Abs(rate - _rate) > 0.05 * _rate)
        {
            _player.Play(clip, clip != _clip ? BlendSeconds : 0, (float)rate);
            (_clip, _rate) = (clip, rate);
        }
    }

    private void Play(string clip, float rate)
    {
        _player.Play(clip, BlendSeconds, rate);
        (_clip, _rate) = (clip, rate);
    }

    /// <summary>
    /// Plays a sequence once and holds its last pose, as a door opening, or goes straight to that
    /// pose for one already open when it appears. False when the model has no such sequence.
    /// </summary>
    public bool PlayAndHold(int sequence, bool atEnd)
    {
        if (ClipFor(sequence) is not { } clip)
        {
            return false;
        }

        _holding = true;
        _player.Play(clip, atEnd ? 0 : BlendSeconds);
        if (atEnd)
        {
            _player.Seek(_player.CurrentAnimationLength, update: true);
        }

        _clip = clip;
        return true;
    }

    private bool _holding;

    private void OnClipFinished()
    {
        if (_holding)
        {
            return;
        }

        if (_casting)
        {
            if (!_castLoops)
            {
                StopSpellAnimation();
            }

            return;
        }

        if (_dead)
        {
            // Lies still, on the last pose of dying, or the model's own lying-dead loop.
            if (ClipFor(M2SequenceIds.Dead) is { } lying && _clip != lying)
            {
                Play(lying, 1f);
            }

            return;
        }

        var wasOneShot = _oneShot;
        _oneShot = false;
        _clip = "";
        if (wasOneShot && _moving && !_motionChosen && _stride is { } s)
        {
            Play(s.Clip, (float)s.Rate);
            return;
        }

        if (wasOneShot && _moving)
        {
            // The body picks its clip again on its next frame.
            _moving = false;
        }

        PlayNextIdle();
    }

    private (string Clip, double Rate)? _stride;

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

        if (_name is not null && _name.Visible != (_selected || (AppSettings.NamePlates && distance < NameRange)))
        {
            _name.Visible = !_name.Visible;
        }

        if (_dead || _oneShot || _motionChosen || _casting)
        {
            return;
        }

        var stride = moving ? Stride(walkSpeed) : null;
        _stride = stride;
        if (moving != _moving || (stride is { } wanted && wanted.Clip != _clip))
        {
            _moving = moving;
            if (stride is { } s)
            {
                _player.Play(s.Clip, BlendSeconds, (float)s.Rate);
                (_clip, _rate) = (s.Clip, s.Rate);
            }
            else
            {
                PlayNextIdle();
            }
        }
        else if (stride is { } same && Math.Abs(same.Rate - _rate) > 0.05 * _rate)
        {
            // The same clip carries on, at the new rate.
            _player.Play(same.Clip, BlendSeconds, (float)same.Rate);
            _rate = same.Rate;
        }
    }

    /// <summary>
    /// Walk, or run when going faster than halfway between the two, at the rate that keeps the feet
    /// from sliding. Null for a model with neither.
    /// </summary>
    private (string Clip, double Rate)? Stride(float speed)
    {
        var skeleton = _asset.Skeleton;
        var walk = skeleton.WalkSpeed * _asset.Scale;
        var run = skeleton.RunSpeed * _asset.Scale;
        if (skeleton.RunClip is { } runClip && run > walk && speed > (walk + run) / 2)
        {
            return (runClip, speed / run);
        }

        return skeleton.WalkClip is { } walkClip ? (walkClip, walk > 0f ? speed / walk : 1f) : null;
    }

    /// <summary>Stands still rather than fidgeting, for the player's own body in VR.</summary>
    public bool NoIdle { get; set; }

    private int _hiddenBone = -1;
    private const float HiddenScale = 1e-3f;
    private double _rate = 1;

    private const int RightShoulderAttachment = 5;
    private const int LeftShoulderAttachment = 6;

    /// <summary>Shoulder pieces, hidden with the head: WoW's are big enough to fill the view from inside the body.</summary>
    private readonly List<MeshInstance3D> _shoulders = [];
    private bool _headHidden;

    /// <summary>Hides the head, what's worn on it, and the shoulder pieces, for the player's own body seen from inside it.</summary>
    public bool HeadHidden
    {
        set
        {
            if (value == _headHidden)
            {
                return;
            }

            _headHidden = value;
            var bone = value ? _asset.Skeleton.HeadBone : -1;

            // Shown again, the head is put back to its size, which a clip without scale keys wouldn't do.
            if (_hiddenBone >= 0)
            {
                Skeleton.SetBonePoseScale(_hiddenBone, Vector3.One);
            }

            _hiddenBone = bone;
            foreach (var shoulder in _shoulders)
            {
                shoulder.Visible = !value;
            }
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
        if (_moving || _dead || _oneShot)
        {
            return;
        }

        if (NoIdle)
        {
            // Holds the first frame of standing, so the body does not fidget around the player in VR.
            if (_asset.Skeleton.IdleClips.Length > 0)
            {
                var stand = _asset.Skeleton.IdleClips[0].Name;
                _player.Play(stand, BlendSeconds, 0f);
                _clip = stand;
            }

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
