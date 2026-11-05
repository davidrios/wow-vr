# WowVR multiplayer protocol extension (v1)

An extension to AzerothCore's 3.3.5a (build 12340) world protocol that lets WowVR clients see each
other's VR bodies: where each player's head and hands are, how their fingers are closed, how they
lean, and where they point. The server only checks and relays it; it never interprets a pose.
Players on stock 3.3.5a clients are unaffected: they never receive any of it, and see the
character as before. See `decisions/0016-vr-multiplayer.md` for why it's done this way.

This document is the contract between the server work (in AzerothCore) and the client work (in
this repo). Byte order is little-endian throughout, as in the rest of the protocol, except the
packet header's size, as usual.

## 1. Server parts

### 1.1 Core patch (small, required)

A stock core closes the connection of any client that sends an opcode `>= NUM_MSG_TYPES` (`0x521`)
(`ClientPktHeader::IsValidOpcode` in `WorldSocket.h`), and of one with no handler in the table.
Server-to-client opcodes are never checked. So the core needs:

1. In `src/server/game/Server/Protocol/Opcodes.h`, new opcodes after `TC9_SMSG_READY_FOR_REDIRECT`
   (0x520), and `NUM_MSG_TYPES` raised to `0x530`, keeping `0x527`-`0x52F` free for later versions:

   | Value   | Name                  | Direction        |
   |---------|-----------------------|------------------|
   | `0x521` | `CMSG_WOWVR_HELLO`    | client to server |
   | `0x522` | `SMSG_WOWVR_HELLO`    | server to client |
   | `0x523` | `CMSG_WOWVR_STATE`    | client to server |
   | `0x524` | `SMSG_WOWVR_STATE`    | server to client |
   | `0x525` | `CMSG_WOWVR_POSE`     | client to server |
   | `0x526` | `SMSG_WOWVR_POSE`     | server to client |

2. In `Opcodes.cpp`'s `OpcodeTable::Initialize()`, entries for them, the client ones with
   `STATUS_LOGGEDIN` so they reach the module's `CanPacketReceive` hook:

   ```cpp
   DEFINE_HANDLER(CMSG_WOWVR_HELLO, STATUS_LOGGEDIN, PROCESS_THREADUNSAFE, &WorldSession::Handle_NULL);
   DEFINE_SERVER_OPCODE_HANDLER(SMSG_WOWVR_HELLO, STATUS_NEVER);
   DEFINE_HANDLER(CMSG_WOWVR_STATE, STATUS_LOGGEDIN, PROCESS_THREADUNSAFE, &WorldSession::Handle_NULL);
   DEFINE_SERVER_OPCODE_HANDLER(SMSG_WOWVR_STATE, STATUS_NEVER);
   DEFINE_HANDLER(CMSG_WOWVR_POSE, STATUS_LOGGEDIN, PROCESS_THREADUNSAFE, &WorldSession::Handle_NULL);
   DEFINE_SERVER_OPCODE_HANDLER(SMSG_WOWVR_POSE, STATUS_NEVER);
   ```

   `0x527`-`0x52F` stay as they are (null), which closes the connection of a client sending them, as
   any unknown opcode does today. `PROCESS_THREADUNSAFE` runs them in `World::UpdateSessions`, where
   reading other players' visibility is safe.

Nothing else in the core changes. Everything below lives in the module.

### 1.2 Module `mod-wowvr`

- A `ServerScript` whose `CanPacketReceive` returns `false` (consumed) for the three `CMSG_WOWVR_*`
  opcodes after handling them, and `true` for everything else.
- A `PlayerScript` for `OnPlayerLogin` (announce, 2.1), `OnPlayerLogout` (forget the session's
  state), `OnPlayerUpdate` (send poses out, 3.4) and `OnPlayerMapChanged` (see 3.5).
- Per session (keyed by the session or account id, cleared on logout): the negotiated version and
  capabilities, the last state, the last pose payload and when it came, and when one was last sent.
- Configuration (`mod_wowvr.conf.dist`):

  | Key                     | Default | Meaning |
  |-------------------------|---------|---------|
  | `WowVR.Enable`          | 1       | Off: no announcement, and `CMSG_WOWVR_*` are dropped. |
  | `WowVR.SendRate`        | 20      | Poses a second the server sends out per VR player, at most. |
  | `WowVR.MaxReceiveRate`  | 40      | Poses a second a client may send; more are dropped. |
  | `WowVR.PoseRange`       | 60      | Yards: players further than this don't get the poses. |
  | `WowVR.PoseTimeoutMs`   | 2000    | A pose older than this isn't sent to players who come into view. |
  | `WowVR.MaxOffset`       | 8.0     | Yards: any position further than this from the character is refused. |

- AntiDOS rows in the world database's `antidos_opcode_policies`, as a module SQL update:
  `(0x525, 5 /* DropPacket */, 60)`, `(0x521, 1 /* Kick */, 5)`, `(0x523, 1 /* Kick */, 10)`.
- A GM command `.wowvr status` listing the sessions that negotiated, with each one's version,
  capabilities, state and poses received and sent in the last second, for testing.

## 2. Handshake

A client must never send a `CMSG_WOWVR_*` opcode to a server that hasn't announced itself: a stock
core would disconnect it. So the server speaks first, through a channel every client tolerates.

### 2.1 Announcement (server to client, on every login)

On `OnPlayerLogin`, when enabled, the server sends the player an addon whisper from themselves:
`SMSG_MESSAGECHAT` with type `CHAT_MSG_WHISPER`, language `LANG_ADDON`, sender and receiver the
player's own GUID, and the text

```
WOWVR\t1
```

that is, the prefix `WOWVR`, a tab, and the highest protocol version the server speaks, in decimal.
Stock clients hand addon messages to addons, so with none listening nothing shows. A client that
sees it may go on to 2.2. One that doesn't, within 10 seconds of entering the world, treats the
server as stock and sends none of this.

### 2.2 `CMSG_WOWVR_HELLO` (0x521)

| Field          | Type   | Meaning |
|----------------|--------|---------|
| `version`      | uint16 | The protocol version the client wants: the lower of its own and the announced one. 1 here. |
| `capabilities` | uint32 | Bit 0: it sends poses (it's a VR player, or may become one). Bit 1: it wants other players' poses. Other bits 0. |

The server stores both and answers. A second hello in a session replaces the first.

### 2.3 `SMSG_WOWVR_HELLO` (0x522)

| Field            | Type   | Meaning |
|------------------|--------|---------|
| `version`        | uint16 | The version agreed: the lower of the client's and the server's. |
| `capabilities`   | uint32 | What the server accepted, of the client's bits. |
| `sendRate`       | uint16 | `WowVR.SendRate`: how many poses a second the client need send. |
| `maxReceiveRate` | uint16 | `WowVR.MaxReceiveRate`. |
| `poseTimeoutMs`  | uint16 | `WowVR.PoseTimeoutMs`. |

Right after it, for a client with bit 1, the server sends what's known of every VR player it can
already see: their `SMSG_WOWVR_STATE`, then their last `SMSG_WOWVR_POSE` if it's newer than the
timeout.

## 3. Poses

### 3.1 The character's space

Everything is relative to the character as everyone else draws it: its position and orientation as
the server last heard them from its movement packets. Its origin is that position, at the feet.
`+X` is forward, along the orientation; `+Y` is to the character's left; `+Z` is up, as in WoW's
own axes. Distances are in yards **at the character's own size**: a VR client draws its body scaled
to the player's real height, so it divides by that scale before sending, and observers draw the
character at its normal size.

- **Positions** are 3 `int16`s, `x, y, z`, in thousandths of a yard (±32.767 yards).
- **Rotations** are a unit quaternion `(x, y, z, w)` in the character's space, packed in a `uint32`
  as "smallest three": the index of the component with the largest magnitude in bits 30-31
  (0 = x, 1 = y, 2 = z, 3 = w), with the quaternion negated first if that component is negative;
  then the other three in order, each mapped from [-1/√2, 1/√2] to an integer
  `round(c * √2 * 511) + 512` in 0-1023, in bits 20-29, 10-19 and 0-9. The receiver rebuilds the
  largest as `sqrt(1 - a² - b² - c²)`.
- **The head's** position is the point between the eyes; its identity rotation looks along `+X`
  with the top of the head toward `+Z`.
- **A hand's** position is the wrist joint. Its identity rotation has the fingers (wrist to middle
  knuckle) along `+X` and the back of the hand toward `+Z`, a hand held out flat, palm down. The
  thumb is then toward `-Y` for the left hand and `+Y` for the right.

### 3.2 `CMSG_WOWVR_POSE` (0x525)

A fixed header, then the parts its `parts` bits say are there, in this order.

| Field     | Type   | Meaning |
|-----------|--------|---------|
| `sequence`| uint16 | Counts up by one each pose, wrapping. The receiver drops one older than the last it drew. |
| `time`    | uint16 | The sender's clock, in milliseconds, wrapping, for spacing poses out when they arrive in bursts. |
| `state`   | uint8  | Bit 0: in VR. Bit 1: seated (the body shouldn't stand up to the head). Bit 2: the left hand is tracked. Bit 3: the right hand is tracked. Bit 4: pointing (the pointer part says where). Bits 5-7: 0. |
| `parts`   | uint8  | Which parts follow. Bits 7 and above of a later version are refused by v1. |

| Bit | Part      | Bytes | Fields |
|-----|-----------|-------|--------|
| 0   | Body      | 6     | `int16 x, y`: where the body stands, when the player leans and the body stays behind. `int16 yaw`: the body's facing from the orientation, in ten-thousandths of a radian. |
| 1   | Head      | 10    | Position (6), rotation (4). |
| 2   | Left hand | 10    | Position (6), rotation (4). |
| 3   | Right hand| 10    | Position (6), rotation (4). |
| 4   | Triggers  | 4     | `uint8` left grip, left trigger, right grip, right trigger, 0 open to 255 closed. |
| 5   | Fingers   | 10    | `uint8` curl of each finger, left hand then right, thumb to little finger, 0 straight to 255 curled: for hand tracking and controllers that sense fingers. |
| 6   | Pointer   | 3     | `uint8` hand (0 left, 1 right). `uint16` length of the ray in hundredths of a yard, 0 for unending. Drawn from that hand along its `+X`. |

The largest pose is 6 + 53 = 59 bytes. A client sends one at `sendRate`, and may send fewer while
nothing moves, at least one a second while in VR, so observers' timeouts don't run out.

### 3.3 Checks on arrival

The server drops a pose, without disconnecting, when:

- the session hasn't negotiated, or didn't ask for bit 0;
- its length isn't exactly what its `parts` say, or it has a bit the version doesn't define;
- any position component, or the body offset, is further than `WowVR.MaxOffset` from the origin;
- it comes sooner than `1000 / WowVR.MaxReceiveRate` ms after the last one kept (the AntiDOS row
  is a backstop for floods).

It doesn't check rotations: the receiver normalizes them anyway.

### 3.4 `SMSG_WOWVR_POSE` (0x526) and sending out

| Field    | Type        | Meaning |
|----------|-------------|---------|
| `guid`   | packed GUID | The player it's from, as the server knows them, never as the client claims. |
| `pose`   | bytes       | The `CMSG_WOWVR_POSE` payload as it arrived, unchanged. |

The server keeps only the newest pose per player. In `OnPlayerUpdate`, when there's one newer than
the last sent and at least `1000 / WowVR.SendRate` ms have passed since the last send, it sends it to
each player in `DoForAllVisiblePlayers` whose session asked for bit 1 and who is within
`WowVR.PoseRange`. Not to the sender. Keeping the newest and sending at a fixed rate bounds what
each observer receives however a client sends.

### 3.5 `CMSG_WOWVR_STATE` (0x523) and `SMSG_WOWVR_STATE` (0x524)

The client's state changes are also in every pose, but a pose stops coming when the player leaves
VR, and observers would otherwise wait for the timeout. So the client sends a state when it
changes, and always on leaving VR:

| Field   | Type  | Meaning |
|---------|-------|---------|
| `state` | uint8 | As in the pose. |

The server stores it, forgets the stored pose when bit 0 clears, and sends `SMSG_WOWVR_STATE`
(`packed GUID`, then the same byte) to the players who'd get its poses, at once. When a player who
can be seen leaves the map or logs out, observers learn it from the object being destroyed, as now.
A map change keeps the session's negotiation; the server sends the newcomer's surroundings as in 2.3
once the player is in the new map.

## 4. Clients

- **Sending:** a VR client sends a pose at `sendRate` from the frame's head, wrists, triggers and
  lean, in the character's space at its own size, with `sequence` counting up. A desktop client
  sends none, but asks for bit 1 to see VR players.
- **Drawing:** an observer keeps the last two poses per player and draws between them, about one
  send interval behind, so poses arriving unevenly still move smoothly. It solves the body as for
  its own player, from the head, hands and lean, at the character's normal size. Untracked hands
  fall back to the character's animation. Without a pose for `poseTimeoutMs`, or with state bit 0
  clear, the character goes back to its normal animation, blended over a moment.
- **Stock servers:** without the announcement, nothing of this is sent, and nothing changes.

## 5. Bandwidth

A full pose with head, both hands, triggers and lean is 6 + 40 = 46 bytes, 52 with the client's
header. At 20 a second that's about 1 KB/s up per VR player, and about 1 KB/s down for each VR
player in view.

## 6. Checking the server work

Without the WowVR client, with two sessions on one server (a small script speaking the protocol, as
`tools/ac-probe` does):

1. With the module off, no announcement comes, and a `CMSG_WOWVR_HELLO` closes the connection, as
   on a stock core.
2. With it on, each login gets `WOWVR\t1` as an addon whisper from oneself, a stock client shows
   nothing, and a hello gets its answer with the configured rates.
3. A pose from session A, standing beside B, reaches B as `SMSG_WOWVR_POSE` with A's GUID and the
   bytes unchanged, and doesn't come back to A.
4. Poses sent at 100 a second reach B at `WowVR.SendRate`, the newest each time.
5. B out of range (beyond `WowVR.PoseRange`, or not seeing A, as with A stealthed) gets none.
6. A pose with a wrong length, an unknown part bit, or a position 20 yards out is dropped, and the
   session stays connected.
7. B coming into view of A gets A's state and last pose at once if it's newer than the timeout.
8. A state with bit 0 clear reaches B at once, and B no longer gets the stored pose on coming into
   view.
9. `.wowvr status` lists both sessions with their rates.
