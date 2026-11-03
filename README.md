# wow VR

A multiplayer world viewer for the 3.3.5a World of Warcraft client, playable in VR or on desktop, built with Godot 4 (.NET). It contains no game assets: it reads them at runtime from your own client. See [PLAN.md](PLAN.md).

## Requirements

- Godot 4.7.2 .NET, ideally our build of it: `git submodule update --init third_party/godot`, then `scripts/build-godot.sh`, which needs Python with SCons and a C++ toolchain (see [patches/godot](patches/godot/README.md)). Stock [Godot 4.7.2 .NET](https://godotengine.org/download/archive/4.7.2-stable/) also works, with creatures costing more.
- .NET SDK 10.0.402 or a later 10.0.4xx patch
- Rust 1.92 or later, with [wow-alchemy](https://github.com/davidrios/wow-alchemy) checked out next to this repo, on its `ffi-crate` branch
- For VR: an OpenXR runtime, such as SteamVR, and a headset
- A 3.3.5a client (not provided)

## Setup

Point the app at your client with the `WOWVR_CLIENT_PATH` environment variable, or save the path once:

```
godot --path godot -- --client-path=<path to client>
```

## Run

```
dotnet build WowVR.sln
godot --path godot
```

With a client set, you start in Goldshire. Otherwise you get a grey test floor.

Click the window to capture the mouse and Esc to release it. WASD walks, Shift goes faster, F toggles flying, Q/E go down/up while flying, and Tab opens the menu, with landmarks to teleport to and movement settings.

For VR, turn on your headset and OpenXR runtime, then add `--xr-mode on`. The left stick walks, left Y toggles flying where you're looking, and pressing the stick toggles fast flying. Hold the left trigger to aim a teleport. The right stick turns. Left X opens the wrist menu, and the right trigger clicks in it.

## Inspect client files

Set `WOWVR_CLIENT_PATH`, then:

```
dotnet run --project tools/inspect -- list elwynn
dotnet run --project tools/inspect -- dbc DBFilesClient\Map.dbc
dotnet run --project tools/inspect -- blp Tileset\Elwynn\ElwynnGrassBase.blp grass.png
dotnet run --project tools/inspect -- wmos Azeroth 31 49
dotnet run --project tools/inspect -- check-models
```

Exported files come from your client, so don't commit or share them.

## Test

Tests that need game files are skipped when `WOWVR_CLIENT_PATH` is unset.

```
dotnet test --project src/WowVR.Formats.Tests
```
