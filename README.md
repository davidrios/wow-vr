# wow VR

A multiplayer world viewer for the 3.3.5a World of Warcraft client, playable in VR or on desktop, built with Godot 4 (.NET). It contains no game assets: it reads them at runtime from your own client. See [PLAN.md](PLAN.md).

**To play**, download the Windows build from [Releases](https://github.com/davidrios/wow-vr/releases) and read [docs/playing.md](docs/playing.md). The rest of this page is for building it yourself.

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

Without a client set, it asks for the WoW folder first. Then the login screen opens, for a 3.3.5a server, or "Explore offline". The controls are WoW's; see [docs/playing.md](docs/playing.md), and `CLAUDE.md` for the command-line options and checks.

For VR, turn on your headset and OpenXR runtime, then add `--xr-mode on`.

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

## Releases

`.github/workflows/windows.yml` builds the Windows zip: our patched Godot's release template, wow-alchemy's library at the commit it pins (`WOW_ALCHEMY_COMMIT`), the HumanoidIK3D extension, and the game exported with the official 4.7.2 .NET editor. Each part is cached on exactly what it's built from, so a release that only changes the game skips the engine, and one that changes a Godot patch recompiles only what the patch touches.

- **Publish:** push a signed tag, which makes a GitHub release with the zip:
  ```
  git tag -s v0.1.0 -m "wow VR 0.1.0"
  git push origin v0.1.0
  ```
- **Try a build:** run the workflow by hand from the Actions tab; the zip is the run's artifact.
- **Locally:** `scripts/build-godot.sh template_release`, then `scripts/export-windows.sh` (into `build/windows`).

When wow-alchemy changes, push it and update `WOW_ALCHEMY_COMMIT`; when the Rust toolchain or SCons should move, their versions are pinned there too.

## License

Licensed under either of

- Apache License, Version 2.0 ([LICENSE-APACHE](LICENSE-APACHE) or <http://www.apache.org/licenses/LICENSE-2.0>)
- MIT license ([LICENSE-MIT](LICENSE-MIT) or <http://opensource.org/licenses/MIT>)

at your option. This covers the project's own code only: World of Warcraft and its files belong to Blizzard Entertainment and aren't part of it.

### Contribution

Unless you explicitly state otherwise, any contribution intentionally submitted
for inclusion in the work by you, as defined in the Apache-2.0 license, shall
be dual licensed as above, without any additional terms or conditions.
