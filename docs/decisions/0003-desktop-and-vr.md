# 0003: Support both desktop and VR

**Status:** accepted (2026-11-01)

## Decision

The app runs in desktop or VR mode, and every feature must work in both. Desktop is the default. VR is opt-in with Godot's `--xr-mode on`, so `xr/openxr/enabled` stays `false` in `project.godot`. `Main` picks `DesktopPlayer` or `XRPlayer` at startup, based on whether OpenXR is running.

## Consequences

- World systems must not assume XR. Anything that needs the player's position uses the active player, not an XR node.
- Desktop launches never touch OpenXR or need a runtime.
- Milestones are checked on desktop first. VR checks are deferred, not dropped, so VR problems can surface late.
- An in-app mode switch would need a restart, because OpenXR is set up at engine startup.
