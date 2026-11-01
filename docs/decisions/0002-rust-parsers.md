# 0002: Parse game data with wow-alchemy over FFI

**Status:** accepted (2026-11-01)

## Decision

Use the Rust project [wow-alchemy](https://github.com/davidrios/wow-alchemy) for all file parsing instead of writing C# parsers, and keep developing it as a public library others can use. The `wow-mpq` crate from crates.io (MIT OR Apache-2.0) provides MPQ access, since wow-alchemy removed its own. C# calls it through a C ABI from a new FFI crate in wow-alchemy.

## Consequences

- Format fixes and improvements go to wow-alchemy, so other projects benefit. Format notes in `docs/formats.md` cover only what this project needs to know about using them.
- `AzerothVR.Formats` holds `IAssetSource` and safe C# wrappers, and stays free of Godot.
- The FFI should stay coarse: opaque handles, plus flat buffers ready for mesh and texture upload. Calls run off the main thread, so per-call cost is small, and the C# side never mirrors Rust structs field by field.
- Builds now need a Rust toolchain, and each platform needs its native library shipped next to the game.

## Open until M1

- How this repo pins wow-alchemy (submodule, or a released version).
