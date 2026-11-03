using System;
using System.Collections.Generic;
using System.Linq;
using WowVR.Ui;
using WowVR.World;
using Godot;

namespace WowVR.Debug;

/// <summary>
/// <c>--bench</c> waits for the world around the player to load, turns the view once around
/// where it stands, then prints frame times and quits. It compares video settings at one spot,
/// which the tours can't, since they move through streaming. Run it with <c>--disable-vsync</c>.
/// <c>--bench=still</c> holds the view for longer instead, and lists each slow frame with its
/// time, to find what spikes now and then.
/// </summary>
public partial class Benchmark : Node
{
    private const int TilesBeforeStart = 9;
    private const double SettleSeconds = 4;
    private const double TurnSeconds = 12;
    private const double StillSeconds = 20;

    /// <summary>A frame this many times the median is listed as slow.</summary>
    private const double SlowFactor = 2;

    private readonly bool _still = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--bench=still") >= 0;
    private readonly List<double> _times = [];

    private readonly List<(double Total, double RenderCpu, double Gpu, double DrawCalls, double Primitives)> _frames = [];

    public required TileManager Tiles { get; init; }
    public required Node3D Player { get; init; }
    public CreatureManager? Creatures { get; init; }
    public LoadingScreen? Loading { get; init; }

    private int[] _gcAtStart = [0, 0, 0];
    private AllocationSampler? _sampler;
    private const double WaitReportSeconds = 5;
    private double _waited;
    private double _nextWaitReport = WaitReportSeconds;
    private long _last;
    private double _settled;
    private double _turned;
    private float _startYaw;

    public override void _Process(double delta)
    {
        if (_settled < SettleSeconds)
        {
            var ready = Tiles.LoadedTileCount >= TilesBeforeStart && !Tiles.IsBusy && Creatures?.IsBusy != true && Loading is not { Showing: true };
            _settled = ready ? _settled + delta : 0;
            _waited += delta;
            if (_waited >= _nextWaitReport)
            {
                // A world that never settles is worth knowing about, and is easy to miss otherwise.
                _nextWaitReport += WaitReportSeconds;
                GD.Print($"Benchmark waiting {_waited:F0} s: {Tiles.LoadedTileCount} tiles, tiles busy {Tiles.IsBusy}, creatures busy {Creatures?.IsBusy}, loading screen {Loading?.Showing}");
            }
            if (_settled >= SettleSeconds)
            {
                RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
                _startYaw = Player.RotationDegrees.Y;
                FrameProbe.Enabled = true;
                _gcAtStart = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
                if (_still)
                {
                    _sampler = new AllocationSampler();
                    _sampler.Start();
                }
                AddChild(new FramePhases());
                AddChild(new FramePhases { IsLast = true });
                var size = GetViewport().GetVisibleRect().Size;
                GD.Print($"Benchmark started at {size.X}x{size.Y}, render scale {GetViewport().Scaling3DScale:0.00}.");
            }

            return;
        }

        // Delta can be smoothed, so the wall clock is what counts.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var wall = _last == 0 ? delta * 1000 : (now - _last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _last = now;
        FrameProbe.EndFrame(wall, _still ? 8 : double.MaxValue);
        _times.Add(_turned);
        var viewport = GetViewport().GetViewportRid();
        _frames.Add((
            wall,
            RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport),
            Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
            Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)));

        _turned += delta;
        if (!_still)
        {
            Player.RotationDegrees = new Vector3(0, _startYaw + (float)(_turned / TurnSeconds * 360.0), 0);
        }

        if (_turned < (_still ? StillSeconds : TurnSeconds))
        {
            return;
        }

        double Median(Func<(double Total, double RenderCpu, double Gpu, double DrawCalls, double Primitives), double> value) =>
            _frames.Select(value).Order().ElementAt(_frames.Count / 2);
        var sorted = _frames.Select(f => f.Total).Order().ToList();
        GD.Print($"Benchmark: {sorted.Count} frames, median {Median(f => f.Total):F2} ms ({1000 / Median(f => f.Total):F0} fps), 95% {sorted[(int)(sorted.Count * 0.95)]:F2} ms, worst {sorted[^1]:F1} ms");
        GD.Print($"Benchmark median ms: render CPU {Median(f => f.RenderCpu):F2}, GPU {Median(f => f.Gpu):F2}; GPU 95% {_frames.Select(f => f.Gpu).Order().ElementAt((int)(_frames.Count * 0.95)):F2}");
        GD.Print($"Benchmark scene: {Median(f => f.DrawCalls):F0} draw calls, {Median(f => f.Primitives):F0} primitives");
        if (_still)
        {
            var slow = Median(f => f.Total) * SlowFactor;
            for (var i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].Total > slow)
                {
                    GD.Print($"Benchmark slow frame at {_times[i]:F2} s: {_frames[i].Total:F1} ms, render CPU {_frames[i].RenderCpu:F1}, GPU {_frames[i].Gpu:F1}");
                }
            }
        }

        var gcs = $"{GC.CollectionCount(0) - _gcAtStart[0]}/{GC.CollectionCount(1) - _gcAtStart[1]}/{GC.CollectionCount(2) - _gcAtStart[2]}";
        var ephemeral = GC.GetGCMemoryInfo(GCKind.Ephemeral);
        var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        GD.Print($"Benchmark GC: {gcs} collections of generation 0/1/2, heap {ephemeral.HeapSizeBytes / 1048576.0:F0} MB, "
            + $"last young pause {ephemeral.PauseDurations[0].TotalMilliseconds:F1} ms promoting {ephemeral.PromotedBytes / 1048576.0:F1} MB, "
            + $"last full pause {full.PauseDurations[0].TotalMilliseconds:F1} ms, latency {System.Runtime.GCSettings.LatencyMode}, server {System.Runtime.GCSettings.IsServerGC}");
        _sampler?.Report(_turned);
        _sampler?.Dispose();
        FrameProbe.Report();
        GetTree().Quit();
    }
}
