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

An uncompressed BLP2 can have an alpha type byte of 2, as `textures\SunGlare.blp` does, with an alpha depth of 136. Its pixels hold their own alpha, so neither matters.

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
- **Particles:** follow WoWModelViewer: a spawn rate of `rate / lifespan` per second, which can be well under one per lifespan and differ by emitter, as along a dragon's back, a quad half as wide as `size`, and blend modes 1, 2 and 4 additive. Blend 2 being additive is that tool's own choice, and the wiki calls it alpha blending. Emitters use each setting's first key, so ones animated by a sequence don't change, and the spread applies to plane emitters too, which WoWModelViewer's plain path doesn't do. The horizontal range is a turn around the emitter's axis, often a full circle, so only the vertical range makes the cone. `head_cell_animation` flips through the texture's tiles over a particle's life, which is what makes a flame move: `[0, 7, 8, 16]` at `[0, 0.5, 0.5, 1]` plays two runs of frames. Additive particles use a power of 1.4 rather than a full gamma conversion, by eye, because they add up in gamma space on the client. Particle spin, drag, tails and ribbons aren't drawn.
- **Empty files:** 63 M2s or their skins are empty in this client. `inspect check-models` reports them, and the game skips them.

## Animated M2s

`M2SkinnedModel` reads what a static doodad doesn't need. See `decisions/0007-creatures.md`.

- **Bones:** a vertex's bone indexes point straight at the model's bones, not through a lookup table.
- **The bone formula** is `parent * T(pivot) * T(translation) * R * S * T(-pivot)`. In a skeleton, that's a bone at its pivot, with a rest offset of `pivot - parent's pivot`, the translation keys added on, and a bind of `T(-pivot)`.
- **Rotations** are 4 `i16`s, which unpack as `(v < 0 ? v + 32768 : v - 32767) / 32767`. In Godot's axes they're `(x, z, -y, w)`, not reversed. WoWModelViewer writes `(-x, -z, y, w)` because its matrix builder is the inverse of the usual one.
- **Checking the math:** each sequence stores its bounds while playing. Playing human stand gives them to within 0.0004 yards. Reversing the rotations puts every tested clip at least 0.16 off, where correct ones are within 0.125. A count of vertices inside the bounds doesn't show this, as reversed rotations still leave 97% of them inside.
- **Keys** can be outside the M2. Sequence flag `0x20` means they're inside it. Otherwise they're in `<model><id:04>-<variation:02>.anim`, at the same offsets. Flag `0x40` means an alias with no keys, which plays those of `alias_next`.
- **Track headers** are arrays of arrays, with one inner array per sequence. `read_sequence_keys` reads the outer one from the M2 and the inner one from wherever the sequence's keys are. The crate's `M2BoneData` reads both from the M2, which is garbage for external sequences, so don't use it for those.
- **Global sequences** hold their keys in the first slot, and 16,408 of the 165,781 rotation tracks in stand, walk and run use one. None of those tracks is curved.
- **Models:** 1,098 of the 1,104 models that displays use parse, and the other 6 are empty files.

## M2 textures and effects

`M2BatchLooks` works out how each batch draws, for doodads and creatures alike. Batches with one still texture keep a plain `StandardMaterial3D`. Others get `godot/shaders/m2_combiners.gdshaderinc`. See `decisions/0008-m2-effects.md`.

- **`shader_id` isn't the shader on this client.** It's nearly always 0 on disk, and the client works the shader out when it loads a skin. `M2ShaderSelection` follows the wiki's port of those functions, on `M2/.skin/WotLK_shader_selection`, which calls itself "mostly-working".
- **Bits of the result:** `0x70` is the first texture's combiner and `0x07` the second's, `0x80` and `0x08` mean they're sphere mapped, `0x4000` means the last one uses the second UV set, and `0x8000` means a special case: `0x8000` alone isn't drawn, and `0x8001` to `0x8003` are `Opaque_Mod2xNA_Alpha`, `Opaque_AddAlpha` and `Opaque_AddAlpha_Alpha`.
- **Layers merge.** A shine in its own batch, with a sphere map and add or mod2x blending, merges into the opaque batch below it when their texture weights match. Drawing both, as WoWModelViewer's 3.3.5a tag does, gives the wrong look. Its upstream code from 2026 reaches the same result for these models.
- **Combiner formulas** are on the wiki's `M2/Rendering`. They're in gamma space, so the shader's samplers aren't `source_color` and it converts at the end.
- **Lookups:** a batch's texture, UV set and transform indexes are the first of `texture_count` in their lookup tables.
- **Alpha** is color alpha times the first texture weight. A batch whose alpha is always 0 isn't drawn, whatever its blend mode.
- **Underwater:** `LiquidType` field 3 is the kind: 0 water, 1 ocean, 2 magma, 3 slime. The view, its fog distances, and the swimming numbers are by eye, not from the client. Its murk and caustics are procedural and anchored in the world rather than the screen, for comfort in a headset. The sounds are the client's `Sound\\Ambience\\UndwaterLoop.wav` (spelled that way) and `EnterWaterMediumA.wav`, and nothing here says when the client plays them.
- **Swell** isn't in the client's data either. The water's vertices rise and fall by up to about 0.2 yards on slow noise, and tilt what it reflects by more than the real slope so that the unlit surface shows it, both by eye. It fades out only at the waterline, because most lakes' depth values are low, with a median of about 0.15. Swimming floats at the undisturbed height.
- **Ambient occlusion** isn't in the client's data. Godot's only dims a lit surface's ambient light, so opaque indoor surfaces ignore all lights, and divide their baked color by the sky's ambient for it to be multiplied back. Indoor creatures take their light as emission, so they don't darken, though the floor around them does.
- **Foam** isn't in the client's data. The water shader compares its height with the scene's depth behind it, so it appears wherever something meets water, including shores and creatures, but not transparent things.
- **Blending:** the client blends in gamma space and Godot in linear, which makes faint glows far too strong. Alpha batches raise their alpha to the power of 2.2, and unlit additive ones multiply color by alpha before converting it. Both are exact only over a dark background.
- **Color tracks** replace lighting with a tint. WoWModelViewer draws them this way, through emission, but no reference states what the client does. Opaque batches on creatures are the exception: they multiply the lit color. Human eyelids are such a batch, with a white track, so replacing lighting left them bright against a dark face.
- **Texture transforms** only apply when batch flag `0x10` is clear, as in WoWModelViewer. Rotation is around the texture's middle, as the wiki says, with no real model checked for direction.
- **Time:** tracks on a global sequence loop on its length, and others on the first sequence's. WoWModelViewer also reads color and weights from the first sequence, whatever is playing.
- **Numbers:** of 22,619 non-empty M2s, 10,577 batches use two textures, 8,963 animate and 8,829 are sphere mapped. Every combiner they reach is in the shader. The client's wiki says blend modes 3 and 7 aren't used in 3.3.5.

## Billboards and events

See `decisions/0009-billboards-and-creature-sounds.md`.

- **Billboard flags** on a bone are `0x8` spherical, then `0x10`, `0x20` and `0x40` to turn around the model's X, Y or Z only. The wiki says only one is ever set. Of the client's M2s, 1,797 use spherical, 376 lock Z, 12 lock X and 12 lock Y.
- **Facing:** the model's X points at the viewer and its Z up, as in WoWModelViewer's `Bone::calcMatrix`, which only does spherical. The wiki adds that locked ones exist, and these turn to face the viewer's position.
- **Events** are a 4-byte name, a data value, a bone and a position, followed by a track of times with no values, where each time means "fire now". Their times are per sequence, in the `.anim` file for sequences kept there.
- **What creatures fire** in stand, walk and run, by how many of the 1,098 models: `$BTH` 680, `$RR0` 630, `$RL0` 626, `$FSD` 632, `$FL0` 622, `$FR0` 621, `$HIT` 336, `$CST` 360, `$WNG` 109, `$FD1` 69.

## Creature sounds

- **Which row:** `CreatureDisplayInfo` field 2, or `CreatureModelData` field 13 when that's 0, is a `CreatureSoundData` row.
- **CreatureSoundData** (38 fields): 9 footstep, which is an id for `FootstepTerrainLookup` and not a sound, 11 wing flap, 14 to 18 fidgets, 24 loop, and 31 and 32 fidget delays as floats. The others are `SoundEntries` rows too.
- **Ground cover:** `GroundEffectTexture` fields 1 to 4 are `GroundEffectDoodad` rows, 5 to 8 their weights and 9 an amount. `GroundEffectDoodad` field 1 is a model under `World\NoDXT\Detail\`. The 8 bytes `wow-alchemy` reads as `pred_tex` and `n_effect_doodad` are a per-cell bitmap of cells to leave clear, whose bit order is assumed to be row by row. Treating the amount as a count per cell, with random positions, is a guess, as no reference says how the client uses it.
- **GroundEffectTexture** (11 fields): 10 is the `TerrainType` row. **TerrainType** (6 fields): 4 is the terrain's sound. **FootstepTerrainLookup** (5 fields): 1 creature footstep id, 2 terrain sound, 3 dry `SoundEntries` row, 4 wet.

## Creature displays

`CreatureDisplayTable.Resolve` turns a display id into a `CreatureLook`. Field numbers were checked against build 12340's files, and the rules follow WoWModelViewer's `charcontrol.cpp`.

- **CreatureDisplayInfo** (16 fields): 1 model row, 3 extra row, 4 scale, 6 to 8 skins. A skin named `Foo` is `Foo.blp` beside the model, and fills texture types 11 to 13.
- **CreatureModelData** (28 fields): 2 path, 4 scale.
- **CreatureDisplayInfoExtra** (21 fields): 1 race, 2 sex, 3 skin, 5 hair style, 6 hair color, 7 beard, 8 to 18 items, 20 baked texture. The items run head, shoulder, shirt, chest, belt, pants, boots, wrists, gloves, tabard, cape. The bake already paints clothes and face onto the body, so only geosets and worn models are left to do.
- **Texture types:** 1 is `Textures\BakedNpcTextures\<bake>`, 6 is hair from `CharSections`, 8 is fur, and 2 is the cape, under `Item\ObjectComponents\Cape\`.
- **CharSections** (10 fields): 1 race, 2 sex, 3 section (0 skin, 3 hair), 4 to 6 textures, 8 variation, 9 color.
- **CharHairGeosets** (6 fields): 1 race, 2 sex, 3 style, 4 geoset, 5 whether to show the scalp, which is geoset 1.
- **CharacterFacialHairStyles** (8 fields) has no id: 0 race, 1 sex, 2 style, then the geosets for groups 100, 300 and 200, in that order.
- **ItemDisplayInfo** (25 fields): 1 and 2 models, 3 and 4 their textures, 7 to 9 geoset groups, 13 and 14 `HelmetGeosetVisData` rows for male and female.
- **HelmetGeosetVisData** (8 fields): 1 hair, 2 to 4 the beard groups, 5 ears. Each is a mask of the races it applies to, as `1 << race`. WoWModelViewer has this code commented out and wow.export doesn't use the table, so the reading comes from the data: `4094` is exactly races 1 to 11, and many hair masks leave out bit 6, Tauren, whose hair is horns. Fields 6 and 7 aren't used, and their meaning isn't known.
- **Geosets:** each hundred is a group that shows one member, and every group starts at 1, except beards at 0 and ears at 2. Later items overrule earlier ones, so the dressing order matters. A robe hides boots and tabard.
- **Worn models:** a head is `Item\ObjectComponents\Head\<model>_<race prefix><M or F>.m2`, using `ChrRaces` field 6, at attachment 11. Shoulders use models 1 and 2, at attachments 6 and 5.

## WMO

Reached through `wa_wmo_open` and `wa_wmo_group_open`, which use `wow_alchemy_wmo::render`. The crate's older `parser` and `group_parser` don't match 3.3.5a files: they read materials as 40 bytes where they're 64, use the wrong `MOGP` header and `MOBA` layouts, and test the wrong flag bits. They still serve its editing tools, and should move to `render`'s records.

- **Group files** are `<root>_NNN.wmo`. `MOGP` wraps all the other chunks, after a 68-byte header.
- **Names:** materials and doodads hold offsets into `MOTX` and `MODN`, which can have gaps, so names must be read by offset. The top byte of a doodad's name offset is flags.
- **Collision:** a triangle collides if its `MOPY` flags have `0x08`, or `0x20` without `0x04`. Material `0xFF` means collision only.
- **Indoor or outdoor:** a group is indoor with `MOGP` flag `0x2000` unless it also has `0x40`, which means lit from outside. 88 of Stormwind's 286 groups and 4 of Undercity's 206 have both, and have no vertex colors, so treating them as indoor draws them fully bright at any hour. Flag `0x8` is plain exterior.
- **Vertex colors:** `MOCV` is BGRA. Only indoor groups use them. WoWModelViewer's upstream found exterior groups' `MOCV` isn't a tint, and leaves it out too.
- **Doodads' context:** `MODR` in a group lists the doodads standing in it. As in WoWModelViewer's upstream, one listed by an indoor group is indoor, even if an outdoor group lists it too. Doodads placed on a tile (`MDDF`) are always outdoor.
- **Indoor doodads' light:** this draws them unlit, tinted by `MOHD`'s ambient plus their `MODD` color, both stored as `0xAARRGGBB`. That formula is a guess: upstream hasn't reached interior light yet, and no reference states it. Alone it gives black, past white and strongly colored results: 161 of 858 doodads in `MD_CRYPT_C` have both at zero. So a doodad with no color takes the `MOCV` on the floor under it, and every result has its brightness limited and some of its color removed, by eye.
- **Indoor creatures' light:** the `MOCV` under their feet, keeping less of its color than doodads, since beside a hearth raw `MOCV` turns skin orange. It replaces the sky's light, with a little darkening on surfaces facing down so the body keeps its shape.
- **Doodad sets:** a placement shows set 0 and its own set.
- **Portals:** `MOPV` holds corners, `MOPT` a corner range and plane (20 bytes), and `MOPR` a portal, the group behind it and the side it's seen from (8 bytes). `MOGP`'s header holds the group's box at byte 12 and its `MOPR` range at bytes 36 and 38. Interior parts can stick out of the ground, such as Undercity's chains, so they can only be hidden by these, not by distance. `WmoPortals` starts from the indoor groups whose boxes hold the camera, or else from the exterior groups (flag `0x8`), and only follows a portal on screen, through the part of the screen it covers. The client uses the BSP tree for which group the camera is in, and this uses boxes, which can overlap.

## Placing models

`MDDF` and `MODF` positions run east, up and south from the map's north-west corner, so they're Godot's axes with the origin moved. `MDDF` scale is a `u16` of 1024ths.

`WowCoords.TilePlacement` rotates around Y by `rot.y - 90`, Z by `-rot.x`, then X by `rot.z`, as WoWModelViewer does. `MODF` stores each WMO's world bounds, and a test compares them with the transformed root bounds: the median error is about 0.002 yards on all four continents, including tilted buildings, and at least 96% are within a yard. The exceptions, up to 79 yards off, are specific buildings such as `OILPLATFORMSMALL`, whose stored bounds look stale rather than the rotation being wrong, since others with the same rotations match.

## DBC

- Fields are 32 bits. `DbcFile` reads them untyped, and the record classes give them meaning.
- `Map` and `AreaTable` have 66 and 36 fields on build 12340, checked against the client by tests.
- The wiki at wowdev.wiki blocks scripts, so it needs a real browser. Its pages on `DB/Light`, `DB/LightIntBand`, `ADT/v18` and `WMO` confirmed what's written here.
- Localized strings take 16 slots plus a flags field. A client only fills in its own slot, and enGB uses the enUS slot, so typed records need the source's locale.
- String offsets can point into the middle of a string, so the FFI reads the string block directly rather than using `WdbFile::get_string_by_offset`, which only finds whole strings.

## Terrain specular

A terrain texture can have a file beside it named `_s.blp`, with the same colors and its shininess in alpha, where the plain file's alpha is solid. About 45% of `TILESET` is these files, and `Wow.exe` names the suffix. The client's shaders in the MPQs read as empty here, so how it shades them is unconfirmed. `terrain.gdshader` follows acazuc's client: Phong with power 10, the texture's color times the mask, times 2, and none in shadow. WoWModelViewer's reading of later clients is `(N.H)^20` times the mask instead, which is the one to try if this looks wrong. Exterior WMO materials with shader 1, 2 or 5 follow acazuc's client: Phong with power 10, plain, times alpha times 4, or times the texture color. M2s have none, by WoWModelViewer's reading. A layer without a `_s` file doesn't shine, where acazuc's falls back to the plain file.
