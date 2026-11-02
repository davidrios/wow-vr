# 0008: M2 second textures, texture and color animation, and doodad animation

**Status:** accepted (2026-11-02)

## Decision

- **Follow the client's own shader selection,** through the wiki's port of it, since this project's copy of WoWModelViewer draws one texture per batch. Its current upstream code has since added a combiner, whose handling of this client's models agrees: one pass with `Opaque_Mod2xNA_Alpha`, with the overlay dropped.
- **WoWModelViewer stays the reference for color tracks** and for when texture transforms apply, since the wiki doesn't cover those.
- **Plain batches keep `StandardMaterial3D`,** so they cost and look the same as before. Only batches that need more use the combiner shader.
- **Each batch with an effect gets its own material,** and every copy of a model shares it. So copies animate in step, and `MultiMesh` doodads stay batched. `ModelLibrary.AnimateMaterials` moves all of them each frame, whether they're on screen or not.
- **Effects run on their own clock,** from the first sequence or a global sequence, whatever a creature is doing. WoWModelViewer reads color and weights the same way.
- **Doodads animate on the GPU.** A model whose stand animation moves any visible bone has it baked into a `M2BoneBake` texture of up to 256 frames, and a vertex shader skins each vertex from it. That keeps `MultiMesh` batching, where a skeleton per copy couldn't handle a forest. Each copy's phase comes from its position, so they don't move as one.
- **Render and wrap modes are fixed in a shader's code,** so each mix gets its own short shader that includes the shared file.

## Consequences

- The clock is local, like creature fidgets, so effects aren't in step between players. They're cosmetic, so M7 doesn't need to share them.
- Batches that used to draw in spite of an alpha of 0 are now hidden, and shine layers no longer draw on their own.
- The combiner shader uses the client's alpha key threshold of 224/255. Plain batches keep the 0.5 they've had since M4, which looks right to date, so the two differ.
- Plain additive batches stay unlit from M4, where the wiki says the client lights them. Effect batches follow the wiki.
- Only the three-way shaders the client's layer merging reaches are handled. Texture counts above 2 use their first two textures, as the client caps it.
- Animated doodads only play stand's first variation, and their collision stays in the rest pose. Global sequences repeat through the bake, so they can jump when it restarts, as on creatures.
- Particles and ribbons still aren't drawn, so fires show their embers without flames.
- The flying tour now has occasional hitches of up to about 150 ms, against a worst of 23 ms at a 60 Hz cap before. The likely cause is new shaders compiling on first use, which hasn't been confirmed. Uncapped, frames over 11.1 ms went from 36 to 112 of about 39,000 once doodads were baked, while the median stayed at 2.1 ms.
