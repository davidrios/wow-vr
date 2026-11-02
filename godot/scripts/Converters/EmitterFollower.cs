using System.Linq;
using WowVR.Formats.Models;
using Godot;

namespace WowVR.Converters;

/// <summary>Moves a doodad's emitter along its bone's path, so one with no speed of its own draws a trail.</summary>
public partial class EmitterFollower : Node
{
    public required M2EmitterPath Path { get; init; }

    private Node3D _emitter = null!;
    private Vector3[] _points = [];
    private double _phase;

    public override void _Ready()
    {
        _emitter = GetParent<Node3D>();
        _points = Path.Points.Select(p => new Vector3(p.X, p.Y, p.Z)).ToArray();

        // The same phase as the doodad's own mesh. See m2_combiners.gdshaderinc.
        var place = _emitter.GetParent<Node3D>().GlobalPosition;
        _phase = place.X * 0.0137 + place.Z * 0.0071;
        Update();
    }

    public override void _Process(double delta) => Update();

    private void Update()
    {
        var seconds = Time.GetTicksUsec() / 1_000_000.0;
        var along = (seconds / Path.Seconds + _phase) % 1.0;
        var position = (along < 0.0 ? along + 1.0 : along) * _points.Length;
        var frame = (int)position % _points.Length;
        _emitter.Position = _points[frame].Lerp(_points[(frame + 1) % _points.Length], (float)(position - (int)position));
    }
}
