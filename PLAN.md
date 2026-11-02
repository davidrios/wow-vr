# wow VR — Project Plan

A multiplayer exploration experience of the Wrath of the Lich King (3.3.5a) World of Warcraft world, playable in **VR or on desktop**, built in **Godot 4 (.NET / C#)**. Players walk, teleport or fly through the zones, see creatures and NPCs going about their routines, and explore together with friends.

Not a game client: no combat, quests, or emulator protocol. It's a **world viewer** that reads game data from each player's own 3.3.5a client.

---

## 1. Goals and non-goals

**Goals**
- Explore the 3.3.5a world at a comfortable, stable framerate: Eastern Kingdoms and Kalimdor first, then Outland and Northrend.
- Every feature works in both VR and desktop mode. Desktop is the default launch and the first test of each milestone (`docs/decisions/0003-desktop-and-vr.md`).
- Faithful terrain, buildings, doodads, water, sky and zone ambience.
- Creatures and NPCs at their original spawn points, idling, wandering and patrolling.
- Small-group multiplayer: see other players' avatars (head + hands), voice chat.

**Non-goals (for now)**
- Combat, quests, spells, inventory, NPC interaction.
- Speaking the WoW network protocol or running an emulator server.
- Distributing any Blizzard assets.

---

## 2. Legal ground rules (non-negotiable)

1. **No Blizzard assets in the repo or releases.** Not models, textures, maps, DBCs, music, or converted derivatives. The app reads them from a path the player configures.
2. **Asset caches stay on the player's machine** (`user://cache`), and are never uploaded or shared between players.
3. **Spawn data**: the AzerothCore world database is community-made and open-source licensed. Ship the **exporter script**, not exported data, unless the license is confirmed compatible.
4. Non-commercial. No use of Blizzard trademarks in the project name or branding.
5. Tests that need game files are skipped when `WOWVR_CLIENT_PATH` is unset, so CI never needs them.

---

## 3. Key decision: client data

**Decided: the original 3.3.5a (build 12340) client, with AzerothCore for spawn data.** See `docs/decisions/0001-client-target.md`.

- **Archive:** MPQ, with patch archives layered over the base archives. Code still goes through `IAssetSource`, with `MpqAssetSource` as the only implementation.
- **Formats:** monolithic ADT with `MH2O` liquids, M2 with external `.skin` files, WMO root + groups, BLP2, DBC. No DB2, split ADTs or FileDataIDs.
- **Spawn data:** AzerothCore's world database targets 3.3.5a, so its IDs match the client's DBCs directly.
- **Availability:** Blizzard no longer distributes this client. Players must supply their own copy, and the project never provides or links to one.

---

## 4. Architecture

```
┌──────────────────────── Godot app (C#) ────────────────────────┐
│                                                                  │
│  Player layer: one of these is chosen at startup                 │
│    DesktopPlayer (mouse + keyboard)                              │
│    XRPlayer (OpenXR + Godot XR Tools)                            │
│    locomotion · comfort · hands · scale · menus                  │
│                                                                  │
│  World streaming                                                 │
│    TileManager ── loads/unloads ADT tiles around the player      │
│    DoodadManager / WmoManager / CreatureManager                  │
│                                                                  │
│  Converters (format → Godot)                                     │
│    TerrainBuilder · M2Builder · WmoBuilder · BlpDecoder          │
│    → ArrayMesh, Skeleton3D, Animation, ImageTexture              │
│    → cached as Godot resources in user://cache                   │
│                                                                  │
│  WowVR.Formats (C#, no Godot dependency, unit-tested)        │
│    IAssetSource · safe wrappers over the native library          │
│                                                                  │
│  wow-alchemy FFI (native Rust library, C ABI)                    │
│    WDT · ADT · WMO · M2 · SKIN · BLP · DBC · MPQ (wow-mpq)       │
│                                                                  │
│  Networking (Godot high-level multiplayer / ENet)                │
│    player transforms · shared world clock · voice                │
└──────────────────────────────────────────────────────────────────┘

Offline tool: tools/spawn-exporter (Python)
  AzerothCore world DB (local MySQL) → spawns/<map>/<tileX>_<tileY>.json
```

**Principles**
- **Parsing lives in [wow-alchemy](https://github.com/davidrios/wow-alchemy)**, a public Rust project developed alongside this one so others can use it. See `docs/decisions/0002-rust-parsers.md`.
- **`WowVR.Formats` wraps it in safe C#**, with no `Godot.*` references, so it stays fast to unit test.
- **Converters** are the only layer that touches both parsed data and Godot types.
- **Everything heavy runs off the main thread** (parsing, mesh building), and hands finished resources to the scene tree.
- **Cache aggressively.** First visit to a tile converts it; later visits load the cached resource.

### Coordinate system

WoW world space: X = north, Y = west, Z = up (yards). Godot: Y up, −Z forward, right-handed.

```
godot = Vector3(-wowY, wowZ, -wowX)   // east = +X, up = +Y, north = −Z
```

- Map tiles are 533.333 yards square in a 64×64 grid. Each tile has 16×16 chunks (MCNK) of 33.333 yards.
- Keep **1 Godot unit = 1 yard** internally. Apply VR scale to the XR origin, not the world.
- Put conversion in one place (`WowCoords.cs`) with unit tests. Check tile index formulas against the wowdev wiki before relying on them.
- **Floating point:** the world spans ~34,000 yards. Use a **floating origin**: periodically re-center the world around the player to avoid jitter far from (0,0,0).

---

## 5. Repository layout

```
wow-vr/
├── CLAUDE.md
├── PLAN.md
├── README.md
├── .gitignore                 # ignores user cache, any *.mpq/*.adt/*.m2/*.blp/*.dbc etc.
├── godot/                     # the Godot project
│   ├── project.godot
│   ├── addons/godot-xr-tools/
│   ├── scenes/
│   │   ├── Main.tscn
│   │   ├── XRPlayer.tscn
│   │   └── debug/             # test scenes: single tile, model viewer, etc.
│   ├── scripts/
│   │   ├── Xr/
│   │   ├── World/             # TileManager, managers, floating origin
│   │   ├── Converters/
│   │   └── Net/
│   └── shaders/               # terrain splat, water, sky
├── src/
│   ├── WowVR.Formats/     # C# wrappers over the wow-alchemy FFI (class library)
│   └── WowVR.Formats.Tests/
├── tools/
│   ├── spawn-exporter/        # Python: AzerothCore DB → spawn JSON
│   └── inspect/               # CLI: dump a file's chunks, export a BLP to PNG, etc.
└── docs/
    ├── formats.md             # notes on each file format as implemented
    └── decisions/             # short ADRs (one per decision)
```

---

## 6. Milestones

Each milestone ends with something visible and a short acceptance check, run in desktop mode first. VR checks can wait until a headset session is worthwhile, but mustn't be dropped.

### M0 — Project skeleton
- Godot 4 .NET project, OpenXR enabled, Godot XR Tools installed.
- Grey test floor with two player scenes: `DesktopPlayer` (default) and `XRPlayer` with hands (`--xr-mode on`, PC VR through SteamVR or Quest via Link/Air Link).
- Settings: client path (`WOWVR_CLIENT_PATH` env var or in-app setting), stored in `user://settings.cfg`.
- Solution with `WowVR.Formats` + tests wired up; `dotnet test` passes.
- Record the client target (section 3) in `docs/decisions/0001-client-target.md`.

**Done when:** you can move around the grey floor in desktop mode (passed 2026-11-01), and the test suite runs. Headset check: stand on the floor with tracked hands (passed 2026-11-01 on SteamVR; locomotion comes in M3).

### M1 — File access
- Add an FFI crate to wow-alchemy exposing a C ABI, and P/Invoke wrappers in `WowVR.Formats`.
- `IAssetSource` + `MpqAssetSource` over `wow-mpq`, honouring 3.3.5a patch order.
- BLP decoding through wow-alchemy (DXT1/3/5 + palettized) → Godot `Image`.
- DBC access through wow-alchemy with typed record definitions for `Map`, `AreaTable`.
- `tools/inspect`: list files, dump a BLP to PNG, print DBC rows.

**Done when:** `inspect` exports a recognizable texture and lists Azeroth's map entry (passed 2026-11-01).

### M2 — Terrain, one tile then streaming
- Parse WDT (which tiles exist) and ADT (MCNK heights, normals, holes, texture layers, alpha maps).
- `TerrainBuilder`: 145-vertex chunk grid (9×9 outer + 8×8 inner) → `ArrayMesh`.
- Terrain shader: up to 4 layers blended by alpha maps.
- Debug scene: load Elwynn Forest's Goldshire tile.
- `TileManager`: stream a 3×3 tile window around the player, then 5×5; background loading; unload behind.
- Floating origin.

**Done when:** you can walk (flat-screen debug camera is fine) from Goldshire to Northshire without hitches. Passed on 2026-11-01: the scripted flight (`--tour`) had no hitches at a 60 Hz cap, and hands-on flying holds 90 FPS in the headset with 1024 textures and 16x anisotropic filtering.

### M3 — Movement and comfort
- Terrain collision (`HeightMapShape3D` per chunk, or a trimesh).
- Desktop: walk with terrain collision, plus the same fly mode and menu actions as VR.
- Teleport, smooth locomotion, snap/smooth turn, comfort vignette (XR Tools).
- Fly mode with configurable speed (Azeroth is big).
- Scale setting: "player-sized" vs "true scale". Playtesting found true scale correct and player-sized too big, so true scale is the default.
- Wrist menu: teleport to landmarks, toggle fly, settings.

**Done when:** a 15-minute session in Elwynn feels comfortable and holds framerate. Passed on 2026-11-01 after a long headset session. Desktop also passes `--movement-check` and `--tour=walk` (worst frame 7.7 ms uncapped). `docs/decisions/0004-terrain-collision.md` covers collision.

### M4 — Doodads and buildings
- M2 static rendering: geometry, `.skin` submeshes, textures, basic render flags (alpha test, blend, two-sided).
- Place ADT doodads (MDDF) with `MultiMeshInstance3D` batching for repeated models (trees, rocks, fences).
- WMO: root + groups, materials, vertex colors, collision. Place via ADT MODF.
- Distance culling and simple LOD (drop small doodads first).

**Done when:** Goldshire and Stormwind's gates look right, and you can walk into the Lion's Pride Inn. Passed on 2026-11-01: it looks right and holds 90 FPS in the headset, and `--movement-check` walks into the inn in both modes. See `docs/decisions/0005-doodads-and-wmos.md`.

### M5 — Water, sky, ambience
- Liquids (MH2O in ADTs, MLIQ in WMOs): water/lava/slime with a simple animated shader.
- Sky colors and fog from `Light`/`LightParams` data, with a day/night cycle.
- Zone detection via `AreaTable`; zone music and ambient sound from the client's files.
- Zone name pop-up when crossing borders.

**Done when:** sunset at Crystal Lake, with the right music, is genuinely nice. Passed on 2026-11-02 after a hands-on session in the headset. Some music still seems wrong at times, which isn't looked into yet. See `docs/decisions/0006-liquids-sky-zones.md`.

### M6 — Creatures and NPCs
- `tools/spawn-exporter`: connect to a locally imported AzerothCore world DB and export per-tile JSON:
  `{ guid, entry, name, displayId, pos, orientation, wanderRadius, movementType, waypoints[] }`.
  (Verify table and column names against the current AzerothCore schema.)
- Resolve `displayId` → model + texture variant through `CreatureDisplayInfo` / `CreatureModelData`.
- M2 skinned meshes: bones → `Skeleton3D`, animation sequences → `Animation` (idle, walk, run, emote).
- Behaviors: stand/idle, random wander within radius, follow waypoints. Ground snapping to terrain height.
- Stream creatures with tiles; cap active animated creatures for performance.
- Name plates (toggleable).

**Done when:** Goldshire's guards, Marshal Dughan, and the kobolds in Fargodeep Mine are all where they should be, moving naturally. Built on 2026-11-02, and `--creature-check` passes on desktop with made-up spawns. It now runs from real AzerothCore tables, turned into spawn files by `tools/spawn-exporter`, waiting for hands-on checking and a headset session. See `docs/decisions/0007-creatures.md`.

### M7 — Multiplayer
- Headless dedicated server build (Godot `--headless`) using high-level multiplayer over ENet.
- Join by IP/code. Server sends a **shared world clock**, which drives day/night and creature simulation.
- Creature movement is **deterministic per client** (seeded by spawn guid + world clock), so creatures don't need network sync.
- Player avatars: head + hands + name, interpolated (desktop players have no tracked hands). Simple stylized avatars (not Blizzard models in this layer).
- Voice chat: evaluate a Godot voice addon (Opus over the same connection) vs an external service.

**Done when:** two people in headsets can meet in Goldshire, talk, and see the same patrolling guard.

### M8 — Polish and distribution
- First-run wizard: locate client, validate version, pre-convert starter zones with a progress bar.
- Graphics presets, framerate/foveation options.
- Investigate Quest standalone (Android export, C# support status, much tighter performance budget).
- README with setup instructions for players and server hosts.

---

## 7. Performance budget (PC VR)

- Target 90 fps (11.1 ms/frame) on a mid-range GPU; 72 fps minimum.
- Draw calls: batch doodads with MultiMesh; merge static WMO group geometry by material.
- Streaming must never block the main thread; budget main-thread upload work per frame.
- Profile each milestone with Godot's profiler before adding the next feature.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| Format parsing rabbit holes | Build `inspect` early; compare against reference tools (wow.export, Noggit, WebWowViewer); document in `docs/formats.md` |
| Terrain shader cost in VR | Bake distant chunks to a single texture; fewer layers at distance |
| Scale/comfort feels wrong | Make scale a setting from M3; playtest early and often |
| Spawn IDs don't match client data | Validate display IDs at export; log and skip unknowns |
| Legal objection from Blizzard | No assets distributed, non-commercial, no branding; accept that a takedown is still possible |

---

## 9. References

- wowdev wiki: file format documentation (ADT, WDT, WMO, M2, BLP, DBC)
- wow.export, Noggit, WebWowViewer: reference implementations to compare against
- StormLib: MPQ archive access
- Godot XR Tools: locomotion and interaction
- AzerothCore world database: spawn and waypoint data

---

## 10. First Claude Code session

Suggested opening prompt:

> Read PLAN.md and CLAUDE.md. Start Milestone 0: create the Godot 4 .NET project with OpenXR enabled, add Godot XR Tools, build an XRPlayer scene on a grey floor, and set up the WowVR.Formats class library with a test project and a passing placeholder test. Create the .gitignore described in the plan. Stop when M0's "done when" check can be performed and tell me how to run it.
