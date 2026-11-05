# 0016: VR bodies in multiplayer

**Status:** accepted (2026-11-05). Replaces 0013's note that head-and-hands avatars need a side channel.

## Decision

- **The AzerothCore protocol is extended** with six opcodes for VR poses, `0x521`-`0x526`, rather
  than a separate service beside it: one connection, one login, and the server already knows who
  can see whom. `docs/vr-multiplayer-protocol.md` is the specification.
- **A small core patch, the rest in a module.** A stock core disconnects a client for an opcode it
  doesn't know, and modules see packets only after that check, so the opcodes go in the core's
  table with `Handle_NULL`, and `mod-wowvr` handles them in `CanPacketReceive`. Taking over an
  unused opcode instead, as `CMSG_UNUSED5`, needs no core change but ties the protocol to a quirk
  of the table.
- **The server speaks first**, with an addon whisper, so a WowVR client never sends a new opcode to
  a stock server, and stock clients never see any of it.
- **The server relays, it doesn't interpret.** It checks a pose's length and bounds, sends the
  newest one per player at a fixed rate to those who can see them, and keeps it for players who come
  into view. Poses are in the character's own space and size, so observers draw them on the
  character as the server places it, whatever size the sender's body is drawn.
