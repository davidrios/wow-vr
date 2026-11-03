using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Linq;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// Counts what the .NET runtime allocates, by type and thread, from its allocation samples (one
/// about every 100 KB), to find what makes the garbage collector pause. Only benchmarks use it.
/// </summary>
public sealed class AllocationSampler : EventListener
{
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    private readonly ConcurrentDictionary<string, long> _bytes = new();
    private long _mainThread = -1;
    private bool _recording;

    /// <summary>Call on the main thread. Events arrive on another thread, so the main one is told apart by its OS id.</summary>
    public void Start()
    {
        _mainThread = OperatingSystem.IsWindows() ? GetCurrentThreadId() : -1;
        _recording = true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_recording || !e.EventName!.StartsWith("GCAllocationTick") || e.Payload is null)
        {
            return;
        }

        var type = e.PayloadNames!.IndexOf("TypeName") is var t and >= 0 ? e.Payload[t] as string ?? "?" : "?";
        var amount = e.PayloadNames.IndexOf("AllocationAmount64") is var a and >= 0 ? Convert.ToInt64(e.Payload[a]) : 100_000;
        var thread = e.OSThreadId == _mainThread ? "main" : "worker";
        _bytes.AddOrUpdate($"{thread}: {type}", amount, (_, sum) => sum + amount);
    }

    public void Report(double seconds)
    {
        GD.Print($"Allocation samples, KB a second by type, over {seconds:F0} s:");
        foreach (var (key, bytes) in _bytes.OrderByDescending(b => b.Value).Take(25))
        {
            GD.Print($"  {key}: {bytes / 1024.0 / seconds:F0}");
        }
    }
}
