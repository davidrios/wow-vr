# 0009: Billboarded bones and creature sounds

**Status:** accepted (2026-11-02)

## Decision

- **Doodads turn in their vertex shader.** A model with a billboarded bone always takes the baked path from `0008-m2-effects.md`, even if nothing else moves. Each vertex of such a batch stores the pivot of the bone that turns it and how, so copies in a `MultiMesh` each face the viewer, in each eye in VR.
- **Creatures turn in a `SkeletonModifier3D`,** because a skeleton has moved the vertices before a vertex shader sees them. It uses the one camera, so it isn't exact per eye, and it pauses with the animation cap.
- **M2 events time the sounds.** `$FL`, `$FR`, `$RL`, `$RR`, `$BL` and `$BR` are footsteps, `$FD1` to `$FD5` fidgets and `$WNG` wing flaps. `$FSD` also fires on most walks, and the parser's notes only guess that it's a footstep, so it's left out to avoid doubling them.
- **Footsteps depend on the ground,** through the terrain layer showing most in that cell of the chunk, and from there `GroundEffectTexture`, `TerrainType` and `FootstepTerrainLookup`.
- **Sounds only play within 40 yards,** from 16 shared voices, and are skipped when those are busy or the file takes over a second to load. Creatures past the animation cap make no sound.
- **Creature sounds use the ambient volume,** because the wrist menu has no room for another slider.

## Consequences

- Timed fidgets use the `fidgetDelaySeconds` fields, which the wiki marks as a guess. They only apply to creatures whose row has a delay.
- Each creature with a loop sound gets its own player for as long as it has a node.
- Wet footsteps aren't played, so wading sounds like walking.
- Combat, death and aggro sounds aren't played, as nothing fights. Neither are doodad events such as `$DSL`, or the player's own footsteps.
- A child of a billboarded bone turns with its ancestor's pivot on doodads, which is only right if it doesn't move much relative to that ancestor.
- Sounds aren't in step between players, which is fine for what they are, but M7 may want footsteps from the shared clock.
- None of this has been heard or seen in a headset. wow.export's newest code has no billboarding or creature sounds to compare with.
