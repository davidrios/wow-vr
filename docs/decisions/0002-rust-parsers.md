# 0002: Parse game data with wow-alchemy over FFI

**Status:** accepted (2026-11-01)

## Decision

Use the Rust project [wow-alchemy](https://github.com/davidrios/wow-alchemy) for all file parsing instead of writing C# parsers, and keep developing it as a public library others can use. MPQ access comes from `wow-alchemy-mpq`, forked into wow-alchemy from warcraft-rs's `wow-mpq` 0.7.0 (MIT OR Apache-2.0) after it turned out to misread archives over 4 GiB. It's developed there from now on. C# calls it through a C ABI from a new FFI crate in wow-alchemy.

## Consequences

- Format fixes and improvements go to wow-alchemy, so other projects benefit. Format notes in `docs/formats.md` cover only what this project needs to know about using them.
- `WowVR.Formats` holds `IAssetSource` and safe C# wrappers, and stays free of Godot.
- The FFI should stay coarse: opaque handles, plus flat buffers ready for mesh and texture upload. Calls run off the main thread, so per-call cost is small, and the C# side never mirrors Rust structs field by field.
- Builds now need a Rust toolchain, and each platform needs its native library shipped next to the game.

## Pinning

While the two projects change together, builds use a sibling checkout at `../wow-alchemy` (override with the `WowAlchemyDir` MSBuild property), built with `--locked` so its `Cargo.lock` fixes all Rust dependencies. This doesn't pin wow-alchemy itself, so **before the first distributed build, pin an exact tagged wow-alchemy version**.
