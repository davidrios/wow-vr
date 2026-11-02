# File format notes

Parsing lives in [wow-alchemy](https://github.com/davidrios/wow-alchemy) (see `decisions/0002-rust-parsers.md`). These notes cover what this project needs to know when using each format with the 3.3.5a client.

## MPQ

`ClientArchives` sets the load order, and later archives override earlier ones:

1. `Data/`: `common`, `common-2`, `expansion`, `lichking`
2. `Data/<locale>/`: `locale`, `speech`, `expansion-locale`, `lichking-locale`, `expansion-speech`, `lichking-speech`
3. `Data/`: `patch`, then `patch-*` in name order
4. `Data/<locale>/`: `patch-<locale>`, then `patch-<locale>-*` in name order

Gotchas:

- `base-<locale>.MPQ` and `backup-<locale>.MPQ` are left out on purpose. They aren't game data archives in this order.
- Paths ignore case and treat `/` and `\` alike, but listed names keep whatever case the archive has.
- Opening every archive takes a few seconds on this client, so open one source and share it.
- Reads on one source run one at a time, behind a lock in the FFI. M2 only builds one tile at a time, so this doesn't limit it yet.

## BLP

`BlpDecoder.Decode` gives RGBA8 for one mipmap level. Terrain uses `ConvertToBc1` instead, which gives a whole BC1 chain for the GPU:

- A BC2 or BC3 block holds 8 bytes of alpha, then a color block that always has 4 colors. Terrain ignores alpha, so the color block alone makes a BC1 block, with no quality loss.
- BC1 only reads a block as 4 colors when its first endpoint is the larger. Otherwise the endpoints swap, and the indexes swap 0 with 1 and 2 with 3.
- Textures that are smaller, non-square, not DXT, or missing mipmaps are decoded, resized and encoded again, which loses quality and takes longer.

## WDT

- `MAIN` gives which tiles exist. Tile X runs east and tile Y runs south, and ADT files are named `<map>_<x>_<y>.adt`.
- MPHD flags `0x4` and `0x80` both mean big alpha maps, as in wow.export. Azeroth uses neither.

## ADT

Reached through `wa_adt_open`, which gives `AdtTerrain`. `TerrainMeshData` turns it into one mesh per tile.

- **Chunk position:** the file stores north, west, base height. `McnkChunk.position` in wow-alchemy-adt reorders these confusingly, so the FFI restores world order, and a test checks it against each chunk's tile indexes.
- **Vertices:** 145 per chunk, in alternating rows of 9 outer and 8 inner. Columns run east and rows run south.
- **Normals:** signed bytes stored as WoW X, Y, Z. A test against the slope of the heights scored 0.989 for this and at most 0.71 for the others, and WoWModelViewer's `(-b1, b2, -b0)` is the same mapping. wow.export names the bytes x, z, y.
- **Holes:** the low 16 bits are a 4x4 mask, so each bit covers 2x2 cells. High-resolution holes came after this client.
- **Alpha maps:** the parser's `alpha_maps` field assumes 4096 bytes per map, which leaves it empty for ordinary 4-bit tiles. Use `raw_alpha` with `decode_alpha_map`, which handles 4-bit, 8-bit and RLE-compressed maps.
- **MCAL size:** the subchunk's own size can be wrong, as WoWModelViewer warns. 1,127 of Northrend's 1,131 tiles failed until the parser also used the MCNK header's size, which includes the 8-byte subchunk header. A test now parses every tile of all four continents.
- **Reflection layers:** MCLY flag `0x400` marks a layer whose texture isn't a surface. All 968 flagged layers are in Northrend: 603 `TCB_*` cube maps, always as the base layer, and 365 `tgatest.blp` placeholders. Drawn as ground, they make chunk-sized squares of the wrong color, so `TerrainMeshData.SurfaceTexture` draws the next surface layer instead. The reflection itself isn't rendered yet.
- **Vertex colors:** MCNK flag `0x40` means an MCCV subchunk of 145 colors, and every Northrend chunk has it. 127 is neutral, and the color multiplies the texture. The byte order is BGRA, confirmed by eye in Northrend with the F4 debug view. WoWModelViewer's notes say RGBA, but its code never uses these values.
- **Alpha map edges:** without MCNK flag `0x8000`, 4-bit maps repeat their second-to-last row and column in the last. WoWModelViewer differs: it always repeats the row, including for big alpha maps, ignores the flag, and its column fix never runs because it tests the wrong loop variable. wow.export does neither. This code hasn't been checked against the wiki or Noggit, so it's worth checking if chunk seams look wrong.

## Liquids

`AdtTerrain.Liquids` comes from `MH2O` through `wa_adt_liquid`, and `WmoGroup.Liquid` from `MLIQ` through `wa_wmo_group_liquid`. `LiquidSurface` turns either into mesh data. See `decisions/0006-liquids-sky-zones.md`.

- **MH2O offsets** count from the start of the chunk's data, and instances are 24 bytes. The crate's older `mh2o` module gets both wrong, so use `read_liquid_layers`.
- **Grid:** columns run east and rows run south, like terrain. A test finds water above its lakebed at Crystal Lake only with this order.
- **Vertex formats:** 0 is heights and depths, 1 heights and texture coordinates, 2 depths, 3 all three. With no vertex data, or format 2, the layer sits flat at its minimum height, as in Noggit. The wiki says format 2 is always at height 0, which this code doesn't check.
- **Exists bits** run through the layer's area with no padding between rows.
- **Heights** can be garbage on vertices no cell uses, so they're limited to the layer's stated range, as Noggit does.
- **MLIQ** is a 30-byte header, 8 bytes per vertex with the height last, then a byte per cell, where a low nibble of `0xF` means no liquid. It can have no cells at all.
- **Liquid type in WMOs** depends on the root's flag `0x4` and the group's flag `0x80000`. `RenderLiquid::liquid_type` follows the wiki and AzerothCore's extractor.
- **LiquidType.dbc:** field 3 is the kind, 14 the shader and 15 the texture pattern, with `%d` for the frame. Shader 3 is procedural, so it draws `lake_a` instead, as Noggit does.

## Sky

`LightTable` reads `Light`, `LightParams`, `LightIntBand` and `LightFloatBand`.

- **Light positions** are stored 36 times larger than yards, as east, up and south from the map's north-west corner. All of `0,0,0` means the map's global light.
- **Bands:** a params row owns 18 `LightIntBand` rows from `id * 18 - 17` and 6 `LightFloatBand` rows from `id * 6 - 5`. `SkyBand` names the int bands. Float band 0 is fog distance, times 36, and band 1 is the fraction of it where fog starts.
- **Colors** are `0xRRGGBB`, and times are half minutes since midnight. Curves wrap around midnight.
- **Direct light isn't usable whole.** Elwynn's noon is `(255, 136, 0)`, which adds to a blue ambient to give warm white. Its midnight is a strong blue.
- **Duskwood** differs from Elwynn mainly in fog, at 181 yards against 500.

## Zone sound

- **AreaTable:** field 7 is the `SoundAmbience` row and 8 the `ZoneMusic` row. Subzones often leave them at 0 and use their parent's.
- **ZoneMusic:** fields 2 and 3 are the shortest silences for day and night, 4 and 5 the longest, and 6 and 7 the sounds.
- **SoundEntries:** fields 3 to 12 are file names, 13 to 22 their weights, 23 the directory and 24 the volume.
- Music is MP3 and ambience is WAV.

## Rendering terrain

- One draw call per tile: a BC1 `Texture2DArray` of the textures the tile draws, a 1024x1024 alpha atlas, and each chunk's 4 texture indexes in `CUSTOM0`.
- A texture array needs one size, so each tile uses the size of its largest texture, up to 1024. Larger textures use their own smaller mipmaps. BC1 isn't available on mobile GPUs, which matters for M8's Quest standalone.
- Atlas coordinates stay half a texel inside each 64x64 cell, because neighbouring chunks can use different textures in the same channel.
- Textures repeat 8 times per chunk.
- **Big alpha maps are weights.** In Northrend, neighbouring chunks stack the same textures in different orders, yet each texture's alpha matches across the shared edge and the alphas sum to about 1. So the base layer gets `1 - sum`, and layer order doesn't matter. Painting layers in order gives a visible seam. Older maps can sum past 2 and are still painted in order, as WoWModelViewer's shaders do. The switch is keyed on the WDT's big alpha flag, which is an inference from this data and not something a reference states.

## M2

Reached through `wa_m2_open` with the model and its `<name>00.skin`, which gives `M2Model`.

- **Paths:** placements often say `.mdx`, from before the format was renamed. The file is `.m2`.
- **Axes:** vertices are X forward, Y left, Z up. `WowCoords.ModelToGodot` gives `(x, z, -y)`, as WoWModelViewer does.
- **Winding:** the files wind the other way from Godot, so `MeshReader` reverses each triangle.
- **Collision:** `bounding_vertices` and `bounding_triangles` are a separate simplified mesh, in the same axes as the model. Models without one, such as grass, are walked through.
- **Submeshes:** `level` holds the high bits of `triangle_start`, for models too big for 16 bits.
- **Particle emitters** are 476 bytes on this client. Their "fake" animation blocks hold 2 arrays, not 3. Reading 3 made 6,365 models fail to parse, because the emitters come last.
- **Empty files:** 63 M2s or their skins are empty in this client. `inspect check-models` reports them, and the game skips them.

## WMO

Reached through `wa_wmo_open` and `wa_wmo_group_open`, which use `wow_alchemy_wmo::render`. The crate's older `parser` and `group_parser` don't match 3.3.5a files: they read materials as 40 bytes where they're 64, use the wrong `MOGP` header and `MOBA` layouts, and test the wrong flag bits. They still serve its editing tools, and should move to `render`'s records.

- **Group files** are `<root>_NNN.wmo`. `MOGP` wraps all the other chunks, after a 68-byte header.
- **Names:** materials and doodads hold offsets into `MOTX` and `MODN`, which can have gaps, so names must be read by offset. The top byte of a doodad's name offset is flags.
- **Collision:** a triangle collides if its `MOPY` flags have `0x08`, or `0x20` without `0x04`. Material `0xFF` means collision only.
- **Vertex colors:** `MOCV` is BGRA. Only indoor groups (`MOGP` flag `0x2000`) use them, as WoWModelViewer does.
- **Doodad sets:** a placement shows set 0 and its own set.

## Placing models

`MDDF` and `MODF` positions run east, up and south from the map's north-west corner, so they're Godot's axes with the origin moved. `MDDF` scale is a `u16` of 1024ths.

`WowCoords.TilePlacement` rotates around Y by `rot.y - 90`, Z by `-rot.x`, then X by `rot.z`, as WoWModelViewer does. `MODF` stores each WMO's world bounds, and a test compares them with the transformed root bounds: the median error is about 0.002 yards on all four continents, including tilted buildings, and at least 96% are within a yard. The exceptions, up to 79 yards off, are specific buildings such as `OILPLATFORMSMALL`, whose stored bounds look stale rather than the rotation being wrong, since others with the same rotations match.

## DBC

- Fields are 32 bits. `DbcFile` reads them untyped, and the record classes give them meaning.
- `Map` and `AreaTable` have 66 and 36 fields on build 12340, checked against the client by tests.
- The wiki at wowdev.wiki blocks scripts, so it needs a real browser. Its pages on `DB/Light`, `DB/LightIntBand`, `ADT/v18` and `WMO` confirmed what's written here.
- Localized strings take 16 slots plus a flags field. A client only fills in its own slot, and enGB uses the enUS slot, so typed records need the source's locale.
- String offsets can point into the middle of a string, so the FFI reads the string block directly rather than using `WdbFile::get_string_by_offset`, which only finds whole strings.
