using System.Linq;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.Converters;

/// <summary>
/// Moves a doodad's emitter along its bone's path, so one with no speed of its own draws a trail,
/// and switches it on and off as its animation says, as a meteor's glow lighting only as it lands.
/// </summary>
public partial class EmitterFollower : Node
{
    public M2EmitterPath? Path { get; init; }

    /// <summary>The emitter, for its switches. Null for one that's on throughout.</summary>
    public M2Emitter? Emitter { get; init; }

    /// <summary>How long its animation loops over, in seconds; 0 when that isn't known, and switches then apply only played once.</summary>
    public double LoopSeconds { get; init; }

    /// <summary>
    /// When set, the animation plays once from this moment, in seconds of <c>Time.GetTicksUsec</c>, and holds
    /// its end, as a spell's shard landing with its mesh's <c>bake_start</c>. Otherwise it loops.
    /// </summary>
    public double? StartAt { get; set; }

    private GpuParticles3D _emitter = null!;
    private Vector3[] _points = [];
    private double _phase;

    public override void _Ready()
    {
        _emitter = GetParent<GpuParticles3D>();
        _points = Path?.Points.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray() ?? [];

        // The same phase as the doodad's own mesh. See m2_combiners.gdshaderinc.
        var place = _emitter.GetParent<Node3D>().GlobalPosition;
        _phase = place.X * 0.0137 + place.Z * 0.0071;
        Update();
    }

    public override void _Process(double delta) => Update();

    private void Update()
    {
        var seconds = Time.GetTicksUsec() / 1_000_000.0;
        var length = Path?.Seconds ?? LoopSeconds;

        // How far into its animation, in seconds, or null when that isn't known.
        double? into = StartAt is { } start ? System.Math.Max(seconds - start, 0.0)
            : length > 0 ? ((seconds / length + _phase) % 1.0 + 1.0) % 1.0 * length
            : null;

        if (Emitter is { } emitter)
        {
            var on = into is { } t ? emitter.OnAt(t) : emitter.StartsOn;
            if (_emitter.Emitting != on)
            {
                _emitter.Emitting = on;
            }
        }

        if (Path is null || _points.Length == 0 || into is not { } at)
        {
            return;
        }

        var along = StartAt is not null ? System.Math.Clamp(at / Path.Seconds, 0.0, 0.999) : at / Path.Seconds;
        var position = along * _points.Length;
        var frame = (int)position % _points.Length;
        _emitter.Position = _points[frame].Lerp(_points[(frame + 1) % _points.Length], (float)(position - (int)position));
    }
}
