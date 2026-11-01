using Godot;

namespace AzerothVR.Xr;

/// <summary>The VR player. Main only creates it once OpenXR is running.</summary>
public partial class XrPlayer : XROrigin3D
{
    private OpenXRInterface _openXr = null!;

    public override void _Ready()
    {
        _openXr = (OpenXRInterface)XRServer.FindInterface("OpenXR");
        _openXr.SessionBegun += OnSessionBegun;

        // The headset paces rendering, so the desktop window must not.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        var viewport = GetViewport();
        viewport.UseXR = true;

        // Godot doesn't support mouse picking in stereo and warns if it's left on.
        viewport.PhysicsObjectPicking = false;
    }

    public override void _ExitTree()
    {
        _openXr.SessionBegun -= OnSessionBegun;
    }

    private void OnSessionBegun()
    {
        var refreshRate = _openXr.DisplayRefreshRate;
        if (refreshRate > 0)
        {
            Engine.PhysicsTicksPerSecond = Mathf.RoundToInt(refreshRate);
            GD.Print($"Headset refresh rate: {refreshRate} Hz");
        }
    }
}
