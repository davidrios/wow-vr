using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Times parts of the main thread's work per frame, which Godot's own monitors can't do because
/// they refresh once a second. Off unless a tour turns it on, when a timer costs one check.
/// </summary>
public static class FrameProbe
{
    private const int SlowFramesKept = 40;

    public static bool Enabled { get; set; }

    private static readonly Dictionary<string, long> _current = [];
    private static readonly Dictionary<string, List<double>> _history = [];
    private static readonly List<(double Total, double Pipelines, double TextureMb, double BufferMb, Dictionary<string, double> Parts)> _slow = [];
    private static double _textureMemory;
    private static double _bufferMemory;
    private static int _frames;
    private static Dictionary<string, double> _last = [];
    private static double _pipelines;

    /// <summary>Bytes each part allocated on the main thread, summed over all frames, since garbage is what makes the collector pause.</summary>
    private static readonly Dictionary<string, long> _allocated = [];
    private static long _allocatedAtStart = -1;

    public readonly struct Scope(string name, long start, long bytes) : IDisposable
    {
        public void Dispose()
        {
            if (name is not null)
            {
                AddTicks(name, Stopwatch.GetTimestamp() - start);
                _allocated[name] = _allocated.GetValueOrDefault(name) + GC.GetAllocatedBytesForCurrentThread() - bytes;
            }
        }
    }

    public static Scope Time(string name) => Enabled ? new Scope(name, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread()) : default;

    public static void AddTicks(string name, long ticks)
    {
        if (Enabled)
        {
            _current[name] = _current.GetValueOrDefault(name) + ticks;
        }
    }

    private static double Pipelines() =>
        Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh)
        + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface)
        + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw)
        + Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization);

    /// <summary>Call once per frame. A frame's length arrives with the next frame, so it's matched to what the last one did.</summary>
    public static void EndFrame(double milliseconds, double slowMilliseconds)
    {
        if (_allocatedAtStart < 0)
        {
            _allocatedAtStart = GC.GetTotalAllocatedBytes();
        }

        var pipelines = Pipelines();
        var texture = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed);
        var buffer = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed);
        if (milliseconds > slowMilliseconds)
        {
            _slow.Add((milliseconds, pipelines - _pipelines, (texture - _textureMemory) / 1048576.0, (buffer - _bufferMemory) / 1048576.0, _last));
        }

        (_textureMemory, _bufferMemory) = (texture, buffer);

        _pipelines = pipelines;
        _last = _current.ToDictionary(p => p.Key, p => p.Value * 1000.0 / Stopwatch.Frequency);
        _current.Clear();
        foreach (var (name, value) in _last)
        {
            if (!_history.TryGetValue(name, out var list))
            {
                _history[name] = list = [];
            }

            list.Add(value);
        }

        _frames++;
    }

    public static void Report()
    {
        var total = GC.GetTotalAllocatedBytes() - _allocatedAtStart;
        GD.Print($"Allocated: {total / 1024.0 / Math.Max(_frames, 1):F1} KB a frame on all threads; main thread parts, KB a frame:");
        foreach (var (name, bytes) in _allocated.Where(a => a.Value > 0).OrderByDescending(a => a.Value))
        {
            GD.Print($"  {name}: {bytes / 1024.0 / Math.Max(_frames, 1):F1}");
        }

        GD.Print($"Main thread, by mean ms over {_frames} frames (worst in brackets):");
        foreach (var (name, values) in _history.OrderByDescending(h => h.Value.Sum()))
        {
            GD.Print($"  {name}: {values.Sum() / _frames:F3} ({values.Max():F1})");
        }

        foreach (var slow in _slow.OrderByDescending(s => s.Total).Take(SlowFramesKept))
        {
            var parts = string.Join(", ", slow.Parts.OrderByDescending(p => p.Value).Take(4).Select(p => $"{p.Key} {p.Value:F1}"));
            GD.Print($"Slow {slow.Total:F1} ms: {parts}; pipelines +{slow.Pipelines:F0}, textures {slow.TextureMb:+0.0;-0.0} MB, buffers {slow.BufferMb:+0.0;-0.0} MB");
        }
    }
}
