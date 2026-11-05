# 0015: A texture brush for trying upscales

**Status:** accepted (2026-11-05). A dev tool, not a feature of the game.

## Decision

- **Textures are replaced from a folder of PNGs laid out as the MPQs are**, in lower case (the client names one file in several cases; MPQs ignore case, not every file system does). Any texture found there is used instead of the client's, for models, buildings, creatures, items and the ground, so upscales can be tried in the game and the folder packed back into an MPQ later. Sky, liquids and icons keep the client's.
- **The brush tags textures by pointing**, with the same pointer as selecting, while it's on in the menu: all the textures of the building, doodad or creature under it, or every layer of the ground's chunk there, with the grass, flowers and stones those layers scatter (each noted under its own model, at its own size), since they have no collision to point at. A texture's area per repeat is summed over all the surfaces using it there. It saves the texture as a PNG unless one is there, which may be an upscale, so tagging never overwrites work.
- **Notes say how big to make an upscale.** Beside each PNG, a `.txt` gives, for each place it was tagged, how many square yards one repeat of the texture covers there (the surface's area in the world over its area in texture coordinates) and the texels per square yard and per yard. The ground repeats each texture once per cell, 4.17 yards. The largest area is listed first.
- **Tagged things are tinted only while the brush is on**, by a `dev_tint` parameter in the model, building and terrain shaders, and the albedo color of plain materials, so upscales are seen as they are otherwise.
- **The ground's replacements go through wow-alchemy's BC1 encoder** (`wa_rgba_to_bc1`), as its own textures do, since a tile's layers are one BC1 texture array. A replacement larger than a layer is still drawn at most 1024 square.
