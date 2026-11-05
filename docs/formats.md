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

A DXT mipmap holds `ceil(w/4) * ceil(h/4)` blocks. wow-alchemy used to size it as `ceil(w*h/16)`, which cut the small levels of non-square textures short, so about 160 of them, such as `Creature\PHOENIX\RIBBON_TEST.BLP`, fell back to uncompressed level 0 and now upload their own DXT chain. 866 files store fewer bytes than a level needs; decoding such a level is an error. The alpha type only picks a DXT kind, so `textures\TaxiMaps\TaxiMap00.blp`, palettized with type 4, loads.

An MPQ patch can delete a file with a marker entry (flag `0x02000000`, size 0), as `patch-enUS.MPQ` does for `DBFilesClient\CharVariations.dbc`. The chain treats the file as gone from then on, as StormLib does; before, 5,122 such names read as 0 bytes.

`BlpDecoder.Decode` gives RGBA8 for one mipmap level. Terrain uses `ConvertToBc1` instead, which gives a whole BC1 chain for the GPU:

- A BC2 or BC3 block holds 8 bytes of alpha, then a color block that always has 4 colors. Terrain ignores alpha, so the color block alone makes a BC1 block, with no quality loss.
- BC1 only reads a block as 4 colors when its first endpoint is the larger. Otherwise the endpoints swap, and the indexes swap 0 with 1 and 2 with 3.
- Textures that are smaller, non-square, not DXT, or missing mipmaps are decoded, resized and encoded again, which loses quality and takes longer.

## WDT

- `MAIN` gives which tiles exist. Tile X runs east and tile Y runs south, and ADT files are named `<map>_<x>_<y>.adt`.
- MPHD flags `0x4` and `0x80` both mean big alpha maps, as in wow.export. Azeroth uses neither.
- A map without tiles, as most instances are (the Stockade is `StormwindJail`), names one WMO in `MWMO` and places it in `MODF`. Unlike a tile's placements, its position counts from the map's middle, not its north-west corner: the Stockade's is all zeros, and its WMO's own coordinates are the world's. Some instances have terrain instead (`DeadminesInstance` has 36 tiles).

## ADT

Reached through `wa_adt_open`, which gives `AdtTerrain`. `TerrainMeshData` turns it into one mesh per tile.

- **Chunk position:** the file stores north, west, base height, which `McnkHeader.position` keeps as stored, and a test checks it against each chunk's tile indexes.
- **Vertices:** 145 per chunk, in alternating rows of 9 outer and 8 inner. Columns run east and rows run south.
- **Normals:** signed bytes stored as WoW X, Y, Z. A test against the slope of the heights scored 0.989 for this and at most 0.71 for the others, and WoWModelViewer's `(-b1, b2, -b0)` is the same mapping. wow.export names the bytes x, z, y.
- **Holes:** the low 16 bits are a 4x4 mask, so each bit covers 2x2 cells. High-resolution holes came after this client.
- **Alpha maps:** `McnkChunk::alpha_map` decodes a layer's map, 4-bit, 8-bit or RLE-compressed, to 4096 bytes.
- **MCAL size:** the subchunk's own size is often wrong (0 in about 210,000 chunks of the client, too large or small in 100,000 more), as WoWModelViewer warns. The MCNK header's size, which includes the 8-byte subchunk header, is the real one. Likewise MCLQ's own size is always 0, and MCNR has 13 bytes of padding its size doesn't count. wow-alchemy-adt reads and writes back every ADT of the client byte for byte.
- **Reflection layers:** MCLY flag `0x400` marks a layer whose texture isn't a surface. All 968 flagged layers are in Northrend: 603 `TCB_*` cube maps, always as the base layer, and 365 `tgatest.blp` placeholders. Drawn as ground, they make chunk-sized squares of the wrong color, so `TerrainMeshData.SurfaceTexture` draws the next surface layer instead. The reflection itself isn't rendered yet.
- **Vertex colors:** `MOCV` is BGRA. This is how 3.3.5a's client treats them. As they load, unless `MOHD` flag `0x8` is set, a transition batch's color (MOGP's first batch count, at byte 40) becomes `c >> 1`, keeping its alpha, and any other becomes `((c * a >> 6) + c) >> 1` with alpha 255. No base color is subtracted: that's in the wiki's code from the later build 18179. Unless `MOHD` flag `0x1` is set, transition vertices then attenuate: opacity adds up `1 - 0.15 * distance` over portals to the outside, drops to 0 within a yard of a portal to another room, the color moves towards 127 by it, and alpha becomes it. Pixel programs are `2 * texture * color`, and the programs are in the MPQs as readable ARB assembly at `shaders\\Pixel\\arbfp1\\` and `shaders\\Vertex\\arbvp1\\` (not `_shaders\\`, whose files are empty). A vertex program is chosen by `(shadow level * 15 + local lights) * 2 + lit`. Unlit passes the color through. Lit is `saturate(mocv * saturate(c10 + c11 * N.L) + c29)`, or on the unified path, `MOHD` flag `0x2`, `saturate(saturate(c10 + c11 * N.L) * c28 + mocv + c29)`. `c10` is ambient, `c11` directional light, `c12` its direction; `c28` is 0.5, and `c29` is half of the material's emissive plus, when the camera is in that WMO, the world's ambient. A material with flag `0x20` (window) uses a softer light: directional is the average of ambient and directional, and ambient is that plus 16/255. Unified interiors have `c10` = `MOHD`'s color and `c11` = 0. With `MOCV` on the normal path, interior batches are unlit, exterior batches lit, and a transition batch is drawn twice, lit with blend mode 9 and then unlit with blend mode 7, which mix by alpha. Only the lit pass takes shadows. Without `MOCV`, it draws lit unless material flag `0x1` is set. Blend modes' names are from memory of the wiki's table, and the two passes only make sense as a mix. The floor color for doodads and creatures is `2 * mocv`, plus `MOHD`'s color on the unified path, or `MOHD`'s color alone with no floor. Here the outside's light is added in linear space to match the ground outside, with its sun as dimmed as in shadow. Alpha picks the light by group flag `0x8`, not by which batches a vertex is in, and these aren't drawn: `c29`, window light, and the unified path's different lit form.
- **Alpha map edges:** without MCNK flag `0x8000`, 4-bit maps repeat their second-to-last row and column in the last. WoWModelViewer differs: it always repeats the row, including for big alpha maps, ignores the flag, and its column fix never runs because it tests the wrong loop variable. wow.export does neither. This code hasn't been checked against the wiki or Noggit, so it's worth checking if chunk seams look wrong.

## Liquids

`AdtTerrain.Liquids` comes from `MH2O` through `wa_adt_liquid`, and `WmoGroup.Liquid` from `MLIQ` through `wa_wmo_group_liquid`. `LiquidSurface` turns either into mesh data. See `decisions/0006-liquids-sky-zones.md`.

- **MH2O offsets** count from the start of the chunk's data, and instances are 24 bytes.
- **MCLQ, the older liquid:** 636 tiles have water only in each chunk's `MCLQ` and none in `MH2O`, 386 of them in Outland (`Expansion01`), and also Valgarde70, Sunwell and several instances. wow-alchemy-adt reads it (`McnkChunk.liquid`), but nothing turns it into liquid layers yet, so that water isn't drawn.
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
- **Ground cover:** `GroundEffectTexture` fields 1 to 4 are `GroundEffectDoodad` rows, 5 to 8 their weights and 9 an amount. `GroundEffectDoodad` field 1 is a model under `World\NoDXT\Detail\`. The 8 bytes of the MCNK header that wow-alchemy-adt calls `no_effect_doodad` are a per-cell bitmap of cells to leave clear, whose bit order is assumed to be row by row. Treating the amount as a count per cell, with random positions, is a guess, as no reference says how the client uses it.
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

Reached through `wa_wmo_open` and `wa_wmo_group_open`, which use `wow_alchemy_wmo`'s `WmoRoot` and `WmoGroup`. They read every chunk of version 17 files, and every root and group of the client writes back byte for byte.

- **Group files** are `<root>_NNN.wmo`. `MOGP` wraps all the other chunks, after a 68-byte header.
- **Names:** materials and doodads hold offsets into `MOTX` and `MODN`, which can have gaps, so names must be read by offset. The top byte of a doodad's name offset is flags.
- **Collision:** a triangle collides if its `MOPY` flags have `0x08`, or `0x20` without `0x04`. Material `0xFF` means collision only.
- **Indoor or outdoor:** a group is indoor with `MOGP` flag `0x2000` unless it also has `0x40`, which means lit from outside. 88 of Stormwind's 286 groups and 4 of Undercity's 206 have both, and have no vertex colors, so treating them as indoor draws them fully bright at any hour. Flag `0x8` is plain exterior.
- **Vertex colors:** see the entry on them above. Here only indoor groups use them. WoWModelViewer's upstream found exterior groups' `MOCV` isn't a tint and leaves it out, but the client does draw any group with `MOCV` as `mocv * light`, so this is worth comparing on a group that has it.
- **Doodads' context:** `MODR` in a group lists the doodads standing in it. As in WoWModelViewer's upstream, one listed by an indoor group is indoor, even if an outdoor group lists it too. Doodads placed on a tile (`MDDF`) are always outdoor.
- **Indoor doodads' light:** parts whose M2 material has flag `0x1`, such as a lantern's glass, take no light at all. The rest are drawn unlit, tinted by `MOHD`'s ambient plus their `MODD` color, both stored as `0xAARRGGBB`, and shaded from one direction, outward from the middle of their group, as in acazuc's client. A side facing away keeps 45% of the light, by eye. No reference states the real formula, and `MODD` alpha below 255 may mean a `MOLT` light, which is not used. Alone these colors can give black or past white: 161 of 858 doodads in `MD_CRYPT_C` have both at zero. So a doodad with no color takes the `MOCV` on the floor under it, and every result has its brightness limited, by eye.
- **Closed hands:** a hand with something in it takes its fingers from the first pose of sequence 15, as in WoWModelViewer. Fingers are key bones 8 to 12 on the right and 13 to 17 on the left, and attachments 1 and 2 are the right and left palms.
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

A terrain texture can have a file beside it named `_s.blp`, with the same colors and its shininess in alpha, where the plain file's alpha is solid. About 45% of `TILESET` is these files. The client's shaders in the MPQs read as empty here, so how it shades them is unconfirmed. `terrain.gdshader` follows acazuc's client: Phong with power 10, the texture's color times the mask, times 2, and none in shadow. WoWModelViewer's reading of later clients is `(N.H)^20` times the mask instead, which is the one to try if this looks wrong. Exterior WMO materials with shader 1, 2 or 5 follow acazuc's client: Phong with power 10, plain, times alpha times 4, or times the texture color. M2s have none, by WoWModelViewer's reading. A layer without a `_s` file doesn't shine, where acazuc's falls back to the plain file.

## Clouds and skyboxes

**Clouds are made, not read.** `SkyClouds` follows the client: 3D value noise of 4 octaves, each twice the frequency and half the strength, with cosine fading and time as the third axis, so clouds change shape without drifting. Time moves 2 noise units a second, with 256 to a lattice cell. `thickness = noise * 64 + 128 - (1 - glow) * 255` and `alpha = 255 * (1 - 0.96^thickness)`. Glow is `LightParams`' fifth column, which is 0.65 on Azeroth's global light, 0.5 on Kalimdor's, 0.25 on Outland's and 0 on Northrend's. The lattice's values and order are random, as in the client, though its order is a fixed table there, so the same shapes don't appear. The client makes textures of 128 to 1024, by its `SkyCloudLOD` setting. This uses 256.

Color is `shaded * ((255 - alpha) / 2 + 64) / 255 + ambient + sunlit * max(N.L, 0)`. N comes from the slope of the first 3 octaves, and the light is a point above the texture, at a height of half its size. It sits where the sun is, or the moon by night, from the middle straight up to the edge at 45 degrees. These are `LightIntBand` rows 10 (sunlit), 11 (shaded) and 12 (ambient). That mapping is an inference: where the client's cloud colors sit in its light data suggests it, and it gives dim blue night clouds where rows 9 to 11 give near-white ones. It hasn't been compared with the real client, and noon may be too warm.

The dome is a sphere's top 45 degrees, with its center below the eye so its edge is on the horizon. Its rings are at 0, 0.025, 0.05, 0.075, 0.1, 0.125, 0.15, 0.175, 0.205, 0.23, 0.245 and 0.25 of pi from the top, and each is 1/11 of the texture's radius out. Clouds show fully to ring 8, at half at ring 9, and not at all from ring 10. They blend in gamma space. The client swaps textures when noise time moves, where this fades between frames every 4 seconds.

**Skyboxes** are `LightSkybox` rows, named by `LightParams`' third column, as `.mdx` paths to `.m2` files. A model's animation follows the time of day with flag 1, and flag 2 keeps the made sky, sun, moon, stars and clouds behind it, which otherwise hide once its weight passes 0.99. Models are centered on the eye, unlit, and fade with their light's weight. Their batches all blend and must draw in batch order, so each takes its own render priority: Hellfire's planets sit among nebulae and a lower-dome fog curtain, and in any other order they flicker or show through. Azeroth and Kalimdor's lights use none, and Outland and Northrend's local lights do.

Stars are the model `Environments\Stars\Stars.m2`, a textured half sphere. They show fully until 3:00, fade out by 4:30, and fade back in from 22:30 to midnight, whatever the sun is doing. The client draws them before the sun, moon and clouds. Here they draw after the sky but fade where the moon and clouds cover them. Cloud cover is scaled down from the tables by `CloudAmount` in `SkyController`, to taste.

## Server protocol

The 3.3.5a client protocol as AzerothCore speaks it, in `src/WowVR.Net`. Checked against AzerothCore's `AuthSession.cpp`, `WorldSocket.cpp`, `Object.cpp`, `Unit.cpp` and `MovementPacketBuilder.cpp`. Numbers are little-endian unless said.

- **Login** (authserver, port 3724): the challenge's four-character codes go in reversed (`68x\0`, `niW\0`, `SUne`). SRP6 uses SHA-1, k = 3, g = 7 and a 32-byte N, all as little-endian numbers. The username and password are upper-cased, and x = H(salt, H(USER ":" PASS)). The session key K interleaves the hashes of S's even and odd bytes, after dropping its leading zero bytes in pairs, and is 40 bytes. M1 = H(H(N) xor H(g), H(USER), salt, A, B, K). The version hash can be zeros unless the server sets `StrictVersionCheck`.
- **Realm list**: a 2-byte size, then a 4-byte zero and a 2-byte count. Each realm has a type, a lock byte, flags, a name, an `ip:port` string, a float population, a character count, a timezone and an id, plus 5 bytes of version when flag 4 is set.
- **World headers**: from the server, a big-endian 2-byte size (3 bytes when the first byte's top bit is set) counting the 2-byte opcode. From the client, a big-endian 2-byte size and a 4-byte opcode. After the auth session, headers alone are ARC4-encrypted, dropping 1024 bytes, with keys HMAC-SHA1(seed, K). The seed `CC98AE04...` decrypts what the server sends, and `C2B3723C...` encrypts what the client sends.
- **Auth session**: build, a zero, the account, a zero, the client seed, two zeros, the realm id, 8 zero bytes, H(ACCOUNT, 4 zero bytes, client seed, server seed, K), and an addon list. The list can be just a 4-byte zero size, but not missing.
- **Object updates**: a block count, then blocks of a type byte. A create block has a packed guid, an object type, a movement block and a values block. A values block is a count of 32-bit mask words, the mask, then one 32-bit value per set bit. A living unit's movement block is its movement info, nine speeds, and a spline when movement flag `0x08000000` is set. That spline's points include the server's padding copies of its ends.
- **Monster moves**: a packed guid, a byte, the start point, a spline id, and a facing type (1 means stop, with nothing after). Then the spline flags, the animation if flag `0x200000` is set, the duration in ms, the parabola if `0x800` is set, and the point count. Flying or Catmull-Rom splines (`0x2000`, `0x40000`) list their points. Linear ones give the end point, then each middle point as a 32-bit offset from the middle of start and end. The offset holds X in 11 bits, Y in 11 and Z in 10, all signed quarter yards, subtracted from the middle. A cyclic ground spline ends on a stand-in point of zeros.
- **Update fields**, in 32-bit words: entry 3, scale 4, display 67, the held items 56 to 58 (item ids, so `Item.dbc`'s column 5 gives their display), and for game objects, display 8.
- **Character list** (`SMSG_CHAR_ENUM`): a count, then per character a guid, name, race, class, sex, skin, face, hair style and color, facial hair, level, zone, map, position, guild, flags (`0x400` hides the helm, `0x800` the cloak, `0x2000` a ghost), customization flags, a first-login byte, 12 bytes of pet, and 23 slots of a 4-byte ItemDisplayInfo row, a 1-byte InventoryType and a 4-byte enchant aura. The first 19 slots are the equipment, head first.
- **Making a character** (`CMSG_CHAR_CREATE`): the name, then a byte each of race, class, sex, skin, face, hair style, hair color, facial hair and outfit. `SMSG_CHAR_CREATE`'s byte is `0x2F` for success, else a reason: AzerothCore's `ResponseCodes`. Deleting sends the guid, and `0x47` is success.
- **Moving the player**: a packed guid and the movement info: flags, a 2-byte second set of flags, a time, the position and orientation, the transport's guid, offset, orientation, time and seat when flag `0x200` is set, the pitch when swimming or flying, the fall time, and the jump's speeds when flag `0x1000` is set. A real client sends `MSG_MOVE_START_FORWARD`, `STOP`, `START_STRAFE_LEFT` and the like when the flags change, a heartbeat every half second while moving, and `MSG_MOVE_SET_FACING` when turning on the spot. Without an anticheat module the server takes any position on the map. A Z below the map's minimum height kills the character.
- **Teleports**: the server ignores all movement while it waits for a teleport to be acknowledged, for good if it never is. A near teleport comes as `MSG_MOVE_TELEPORT_ACK` (packed guid, counter, movement info), answered with the packed guid, the counter and a time. Another map comes as `SMSG_TRANSFER_PENDING` and `SMSG_NEW_WORLD` (map, position, orientation), answered with an empty `MSG_MOVE_WORLDPORT_ACK` once the client is ready, after which the server sends everything there again. Dying and releasing teleports the spirit to the graveyard this way.
- **Forced changes**: `SMSG_FORCE_*_SPEED_CHANGE` (packed guid, counter, a byte for run only, the speed), `SMSG_MOVE_SET_CAN_FLY` and `SMSG_FORCE_MOVE_ROOT` and their opposites (packed guid, counter) each want an ack of the packed guid, counter and movement info, with the speed, or a float for flying. An ack with a faster speed than the server's gets the client kicked.
- **The player's own fields**: health 24, power 25 to 31 by power type (byte 3 of field 23), max health 32, max power 33 to 39, level 54, faction template 55, unit flags 59, dynamic flags 79 (1 lootable), NPC flags 82. Player flags 150 (`0x10` ghost), equipment and backpack item guids from 324 (2 words each, 23 equipment and bag slots, then 16 backpack slots), XP 634, next level 635, money 1170. A bag's slots and item guids start at 64 and 66.
- **Chat**: `CMSG_MESSAGECHAT` is a type, a language (the race's: 7 for the Alliance, 1 for the Horde; universal is refused), a target name for whispers, and the text. Commands are sent as chat starting with a dot. `SMSG_MESSAGECHAT` has the type, language, sender guid and 4 bytes, then by type a name, receiver guid, a 4-byte length and the text, then a tag byte. Text carries `|c...|r` colors and `|H...|h` links. GM commands answer as system lines, and `.gps` reports on the selected unit, which the server keeps across logins, rather than the player.
- **Game object rotation** is a 64-bit packed quaternion: X in the top 22 bits at 2^21, Y and Z in 21 bits each at 2^20, and W made positive.

## Player characters

A player has no baked texture, so its body is painted from pieces, as WoWModelViewer 0.7.0.6's CharControl::RefreshModel, AddEquipment and CharTexture::compose do. `CreatureDisplayTable.ResolveCharacter` gives the pieces and `CharacterTexture.Compose` paints them.

- **The model** is ChrRaces' male or female display (columns 4 and 5), through CreatureDisplayInfo as for any creature.
- **The body texture** is 512 square, in WoWModelViewer's regions at twice their 256 grid: arms, hands and face on the left half, torso, legs and feet on the right. Each piece is stretched to its region and drawn by its alpha, lowest layer first, and the result is opaque.
- **CharSections** rows are race, sex, section, three textures, flags, variation and color. Section 0 is the skin (texture 1 the base, layer 0, and texture 2 the fur), 4 the underwear (legs upper and torso upper, layer 1), 1 the face (variation is the face, color the skin; face lower and upper, layer 1), 2 the facial hair (variation the style, color the hair color; layer 2) and 3 the hair (texture 1 is the hair model's, and textures 2 and 3 are the scalp on face lower and upper, layer 3, unless CharHairGeosets says the style is bald). The first matching row is used. Flag 1 marks a choice players can make and flag 4 one only Death Knights get.
- **Gear** paints by slot, in WoWModelViewer's slot order (another order with a robe), from layer 10: ItemDisplayInfo's columns 15 to 22 are arm upper, arm lower, hand, torso upper, torso lower, leg upper, leg lower and foot, in `Item\TextureComponents\<Region>Texture\`. A name's file is tried with the sex's suffix `_M` or `_F`, then `_U`, then bare; one whose second-to-last letter is L (leggings) tries `_U` first. Chest and shirt paint the arms and torso, and the legs too if a robe; the belt torso lower and leg upper; bracers arm lower; pants the legs; gloves the hand and arm lower; boots leg lower and foot; a tabard the torso. Geosets and worn models follow the same rules as NPCs.
- **Starting outfits**: `CharStartOutfit.dbc` rows are an id, then race, class, sex and outfit as four bytes of one column, then 24 item ids, 24 ItemDisplayInfo rows and 24 InventoryTypes. The inventory type says the slot; a second one-handed weapon goes in the off hand.
- **Palettized BLPs**, which character skins and item pieces mostly are, keep their 256 palette entries as blue, green, red and a padding byte. wow-alchemy had red and blue swapped there, and left 1- and 4-bit alpha unscaled; both are fixed, with tests.
- **Item ids to looks**: a server gives creatures' held items as item ids, and `Item.dbc`'s column 5 is the item's ItemDisplayInfo row and column 6 its InventoryType.
- **Other players** come as object updates with their looks in their fields: race, class and sex in bytes 0 to 2 of `UNIT_FIELD_BYTES_0` (word 23), skin, face, hair style and hair color in `PLAYER_BYTES` (word 153), facial hair in byte 0 of `PLAYER_BYTES_2` (154), and the shown item of each of the 19 equipment slots at `PLAYER_VISIBLE_ITEM_1_ENTRYID` (word 283), every second word, the other being its enchantment. Player flags `0x400` and `0x800` hide the helm and cloak.

## Spells and icons

- **Spell.dbc** has 234 fields on build 12340, as AzerothCore's `SpellEntry`: category 1, attributes 4 (`0x40` passive, `0x80` hidden from the spellbook), attributes 2 at 6, the forms it can be cast in 12 (a mask of `1 << (form - 1)`), target flags 16 (`0x40` a place on the ground), cast time index 28 (into `SpellCastTimes.dbc`, whose column 1 is milliseconds), cooldown 29, category cooldown 30, power type 41 and cost 42, effects 71 to 73 and their first implicit targets 86 to 88, the icon 133 (into `SpellIcon.dbc`, whose column 1 is a path without `.blp`), then name 136, rank 153 and description 170, each 16 locale slots and flags, and the global cooldown's category 205 and length 206. Fireball's first rank (133) is a 1.5-second cast with a 1.5-second global cooldown.
- **Needing a form**: as AzerothCore's `CheckShapeshift` has it, an unshifted player can't cast a spell whose form mask is set unless attributes 2 has `0x80000`. The mask alone isn't enough: Lesser Heal lists Spirit of Redemption (form 32).
- **The spellbook** lists the known spells that `SkillLineAbility.dbc` (skill line 1, spell 2) teaches. Its tabs are the skill lines of `SkillLine.dbc` category 7 (column 1; the name is column 3), except pets', which `CreatureFamily.dbc` names in columns 5 and 6; the rest is General. Category 12 is a mix of spells everyone knows: Auto Attack and Summon Friend show, Opening and Duel are hidden by attribute `0x80`, and Honorless Target shows only on a GM taught everything.
- **Radius and range**: Spell.dbc's 92 to 94 index `SpellRadius.dbc` (an id, then yards), and 46 indexes `SpellRange.dbc` (an id, the least ranges at an enemy and a friend, then the most). Blizzard's first rank is 8 yards wide and 30 away; Fireball reaches 35.
- **What a spell aims at** comes from its effects' first targets: 6 an enemy, 21, 57 and 61 a friend, 25 anyone, 28 to 30 and 87 a place. `CMSG_CAST_SPELL` is a cast count, the spell, a flags byte, then target flags (`0x2` a unit, with its packed guid; `0x40` a place, with a packed transport guid of 0 and the position), or 0 for the caster.
- **The server's side**: `SMSG_INITIAL_SPELLS` at login is a byte, a 2-byte count and per spell a 4-byte id and 2 bytes, only active ranks, then cooldowns. `SMSG_ACTION_BUTTONS` is a state byte (2 clears) then 144 buttons, each the id in 24 bits and the kind above (0 spell, `0x40` macro, `0x80` item); `CMSG_SET_ACTION_BUTTON` sends a slot byte and one. `SMSG_SPELL_START` and `_GO` are the item or caster and the caster as packed guids, the cast count, spell, flags (`0x1` the server's own cast) and a time, which in a start is the cast's length. A channel follows its go with `MSG_CHANNEL_START` (packed guid, spell, duration) and ends at an update of 0. Failures are `SMSG_CAST_FAILED` (count, spell, reason) to the caster and `SMSG_SPELL_FAILURE` (packed guid, count, spell, reason); reasons are AzerothCore's `SpellCastResult`, 27 being one not to report.
- **Cooldowns are the client's to work out**: AzerothCore sends `SMSG_SPELL_COOLDOWN` only for pets' and vehicles' spells, so a player's come from Spell.dbc when the spell goes off, the global one from when it's cast, given back if it fails.
- **Spell visuals**: Spell.dbc's 131 indexes `SpellVisual.dbc` (32 fields): kits for the precast 1, cast 2, impact 3, state 4, state done 5 and channel 6, a missile flag 7 and its model 8, its sound 11, the caster's and target's impacts 14 and 15, and the instant, impact and persistent areas 23 to 25. A kit (`SpellVisualKit.dbc`, 38 fields) is its start and main animations (1, 2, in AnimationData's numbering), effects at head, chest, base, left and right hand, breath, both weapons, three specials and the world (3 to 14, into `SpellVisualEffectName.dbc`, whose 2 is the model, named `.mdx`, and 3 its size), the sound 15, and four procedural effects at 17 to 20 with their parameters after. AnimationData's 5 is each animation's fallback. Speed is Spell.dbc's 47. `inspect spellvisual <spell>` prints a spell's kits, and `inspect spellmodel <text>` the spells using a model.
- **Lasting areas** are dynamic objects: caster at word 6, spell at 9 and radius at 10 (a float), placed by a stationary position. Their look is the persistent-area kit, its models at their own size (Consecration's is 23 yards across for its 8-yard radius; scaling them by the radius was wrong).
- **Raining area models** are a kit's procedural effect 9, as player Rain of Fire's and Blizzard's areas and ticks have. Its first parameter, rounded, picks a row of `SpellVisualKitAreaModel.dbc` (an id, a model, an index) by position, the first row when out of range: 0 is `Blizzard_Impact_Base`, 1 `RainOfFire_Impact_Base`. Its second is how many a second (5 for both), times (detail × 1/9 × 0.9 + 0.1) for the client's spell detail setting from 0 to 9. Each frame, owed shards build up by the frame time (capped at 1/15 s) times the rate; each goes a random direction and a random fraction of the radius from the center (not spread evenly by area), tried 16.67 yards up along a line from 0.167 above the center and stopped at 95% of the way to anything in between, then dropped up to 33.3 yards to the ground. It shows after a random 0 to 510 ms and plays its model's animation once. The meteor falls 13.6 yards in 0.8 s, when its emitters switch off.
- **Emitters switch on and off** by their `enabled_in` track, per sequence (or a global sequence's loop), as WoWModelViewer reads it: the meteor's glow stops at 0.57 s and its fire at 0.8 s of a 3.3-second clip. Reading only the first key kept them on all clip, a solid red wash; dropping those that start off lost effects that light later. `wa_m2_emitter_switches` gives the keys.
- **Icons** are `Interface\Icons\*.blp`, 64 square. An item's is ItemDisplayInfo's column 5, a bare name. The question mark is `INV_Misc_QuestionMark`.
