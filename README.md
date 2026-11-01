# Azeroth VR

A multiplayer world viewer for the 3.3.5a World of Warcraft client, playable in VR or on desktop, built with Godot 4 (.NET). It contains no game assets: it reads them at runtime from your own client. See [PLAN.md](PLAN.md).

## Requirements

- [Godot 4.7.2 .NET](https://godotengine.org/download/archive/4.7.2-stable/)
- .NET SDK 10.0.402 or a later 10.0.4xx patch
- Rust 1.92 or later, with [wow-alchemy](https://github.com/davidrios/wow-alchemy) checked out next to this repo
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

With a client set, you start in Goldshire. Otherwise you get a grey test floor.

Click the window to capture the mouse and Esc to release it. WASD moves, Q/E go down/up, Shift moves faster.

For VR, turn on your headset and OpenXR runtime, then add `--xr-mode on`. The left stick flies where you're looking, pressing it toggles fast flying, and flicking the right stick turns 45° at a time.

## Inspect client files

Set `AZVR_CLIENT_PATH`, then:

```
dotnet run --project tools/inspect -- list elwynn
dotnet run --project tools/inspect -- dbc DBFilesClient\Map.dbc
dotnet run --project tools/inspect -- blp Tileset\Elwynn\ElwynnGrassBase.blp grass.png
```

Exported files come from your client, so don't commit or share them.

## Test

Tests that need game files are skipped when `AZVR_CLIENT_PATH` is unset.

```
dotnet test --project src/AzerothVR.Formats.Tests
```
