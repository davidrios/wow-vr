# Azeroth VR

A multiplayer world viewer for the 3.3.5a World of Warcraft client, playable in VR or on desktop, built with Godot 4 (.NET). It contains no game assets: it reads them at runtime from your own client. See [PLAN.md](PLAN.md).

## Requirements

- [Godot 4.7.2 .NET](https://godotengine.org/download/archive/4.7.2-stable/)
- .NET SDK 10.0.402 or a later 10.0.4xx patch
- For VR: an OpenXR runtime, such as SteamVR, and a headset
- A 3.3.5a client (not provided)

## Setup

Point the app at your client with the `AZVR_CLIENT_PATH` environment variable, or save the path once:

```
godot --path godot -- --client-path=<path to client>
```

## Run

```
dotnet build AzerothVR.sln
godot --path godot
```

Click the window to capture the mouse and Esc to release it. WASD moves, Q/E go down/up, Shift moves faster.

For VR, turn on your headset and OpenXR runtime, then add `--xr-mode on`.

## Test

```
dotnet test --project src/AzerothVR.Formats.Tests
```
