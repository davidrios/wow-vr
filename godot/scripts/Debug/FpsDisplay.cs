using Godot;

namespace AzerothVR.Debug;

/// <summary>
/// Frame rate readout and frame time graph, attached to the active camera. It's in
/// 3D because Godot doesn't draw 2D UI into a headset, so one display works in both modes.
/// </summary>
public partial class FpsDisplay : Label3D
{
    private const double UpdateInterval = 0.25;
    private const int GraphFrames = 180;
    private const float GraphWidth = 0.36f;
    private const float GraphHeight = 0.09f;
    private const float GraphTop = -0.05f;
    private const float BudgetLineThickness = 0.001f;

    /// <summary>The graph's top is this many frame budgets, so spikes clip but normal frames stay readable.</summary>
    private const double GraphBudgets = 3;

    /// <summary>Frame pacing jitters slightly around the budget, which isn't a missed frame.</summary>
    private const double OnBudgetTolerance = 1.1;

    private static readonly Color BackgroundColor = new(0, 0, 0, 0.5f);
    private static readonly Color OnBudgetColor = new(0.3f, 0.9f, 0.3f);
    private static readonly Color OverBudgetColor = new(1f, 0.8f, 0.2f);
    private static readonly Color FarOverBudgetColor = new(1f, 0.25f, 0.2f);

    private readonly double[] _frames = new double[GraphFrames];
    private readonly ImmediateMesh _graph = new();
    private int _nextFrame;
    private double _sinceUpdate;
    private double _worstFrame;

    public override void _Ready()
    {
        Position = new Vector3(-0.25f, -0.3f, -1f);
        PixelSize = 0.001f;
        FontSize = 32;
        OutlineSize = 8;
        NoDepthTest = true;
        Shaded = false;
        RenderPriority = 100;
        OutlineRenderPriority = 99;
        HorizontalAlignment = HorizontalAlignment.Left;

        AddChild(new MeshInstance3D
        {
            Mesh = _graph,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                NoDepthTest = true,
                RenderPriority = 98,
            },
        });
    }

    public override void _Process(double delta)
    {
        _frames[_nextFrame] = delta;
        _nextFrame = (_nextFrame + 1) % GraphFrames;

        var refreshRate = RefreshRate();
        DrawGraph(1.0 / refreshRate);

        _worstFrame = Mathf.Max(_worstFrame, delta);
        _sinceUpdate += delta;
        if (_sinceUpdate >= UpdateInterval)
        {
            Text = $"{Engine.GetFramesPerSecond():F0} FPS / {refreshRate:F0} Hz\nworst {_worstFrame * 1000:F1} ms";
            _sinceUpdate = 0;
            _worstFrame = 0;
        }
    }

    /// <summary>The headset's rate in VR, otherwise the monitor's.</summary>
    private static double RefreshRate()
    {
        if (XRServer.FindInterface("OpenXR") is OpenXRInterface { DisplayRefreshRate: > 0 } openXr && openXr.IsInitialized())
        {
            return openXr.DisplayRefreshRate;
        }

        var monitor = DisplayServer.ScreenGetRefreshRate();
        return monitor > 0 ? monitor : 60;
    }

    private void DrawGraph(double budget)
    {
        var bottom = GraphTop - GraphHeight;
        var barWidth = GraphWidth / GraphFrames;

        _graph.ClearSurfaces();
        _graph.SurfaceBegin(Mesh.PrimitiveType.Triangles);

        AddRectangle(0, GraphWidth, bottom, GraphTop, BackgroundColor);

        // Oldest on the left, newest on the right.
        for (var i = 0; i < GraphFrames; i++)
        {
            var frame = _frames[(_nextFrame + i) % GraphFrames];
            var height = (float)Mathf.Min(frame / (budget * GraphBudgets), 1) * GraphHeight;
            AddRectangle(i * barWidth, (i + 1) * barWidth, bottom, bottom + height, FrameColor(frame, budget));
        }

        var budgetLine = bottom + GraphHeight / (float)GraphBudgets;
        AddRectangle(0, GraphWidth, budgetLine, budgetLine + BudgetLineThickness, Colors.White);

        _graph.SurfaceEnd();
    }

    private static Color FrameColor(double frame, double budget)
    {
        if (frame <= budget * OnBudgetTolerance)
        {
            return OnBudgetColor;
        }

        return frame <= budget * 2 ? OverBudgetColor : FarOverBudgetColor;
    }

    private void AddRectangle(float left, float right, float bottom, float top, Color color)
    {
        AddVertex(left, top, color);
        AddVertex(right, top, color);
        AddVertex(right, bottom, color);
        AddVertex(left, top, color);
        AddVertex(right, bottom, color);
        AddVertex(left, bottom, color);
    }

    private void AddVertex(float x, float y, Color color)
    {
        _graph.SurfaceSetColor(color);
        _graph.SurfaceAddVertex(new Vector3(x, y, 0));
    }
}
