# 0013: Playing on an AzerothCore server

**Status:** accepted (2026-11-03). Changes M7, and drops "speaking the WoW network protocol" from the non-goals.

## Decision

- **An AzerothCore server is the world's source and the multiplayer server.** The game logs in as a 3.3.5a client would, with a character on the account, and shows what the server shows: creatures where they are and moving as they move, with the server's events and pools deciding what exists. Friends play by logging in to the same server with their own characters. This replaces M7's own headless Godot server over ENet.
- **Spawn files stay as the offline mode.** Without a server, or with `--offline`, creatures and objects come from `spawns/` as before (`0007-creatures.md`), with motion from the world clock.
- **The protocol lives in `src/WowVR.Net`**, with no Godot dependency, beside `WowVR.Formats`. It uses only .NET's own libraries: SHA-1, HMAC, `BigInteger` for SRP6, and zlib. It's written against AzerothCore's source, which is GPL; the code is ours, not copied.
- **The server is the authority for creatures, not for the player.** The player moves with our own physics and tells the server where they are (`MSG_MOVE_HEARTBEAT`), so it shows what's around them. Nothing here fights, loots or casts.
- **Head-and-hands avatars and voice don't fit the protocol.** If they're wanted, they need a small side channel beside it, keyed by character.

## How it works

1. `AuthClient` does the SRP6 exchange with the authserver (port 3724) and reads the realm list. A realm whose address is `127.0.0.1` is reached at the authserver's host instead.
2. `WorldConnection` opens the worldserver, answers its challenge with the session key, and from then on encrypts and decrypts packet headers with ARC4 (drop 1024), keyed by HMAC-SHA1 of the session key with the two fixed seeds.
3. `RealmSession` lists the characters, enters the world with the one named or the first, and reads on its own thread. `ObjectTracker` reads object updates, monster moves and destroys. Units wait up to 2 seconds for their name from a creature query, then appear as `UnitAppeared` events.
4. In the game, `ServerWorld` takes the events each frame and hands creatures to `CreatureManager.AddLive`, with a `ServerMotion` that plays the server's path from the moment it was received, less how far along it already was. It sends the player's position 4 times a second while they move, and every 5 seconds otherwise.
5. A path is straight lines at a steady speed through the server's points, ending facing where it says. Linear paths send their middle points packed as quarter-yard offsets from the middle. Catmull-Rom paths are drawn as straight lines too.

`docs/formats.md`'s "Server protocol" has the packet layouts.

## Settings

`--server=host` and `--character=Name`, or `[server]` in `settings.cfg` with `host`, `account`, `password` and `character`. `WOWVR_AC_USER` and `WOWVR_AC_PASS` override the account. Checks, tours, benchmarks and screenshots stay offline unless given `--server=`. `tools/ac-probe` logs in the same way and prints what the server shows, without Godot.

## Consequences

- **The server keeps the position.** Moving in the game moves the character on the server, which saves it. Logging in again starts where the player left.
- The server's own Z is trusted for creatures, since it has its maps. Ground snapping applies only to spawn-file creatures.
- Continents' menu buttons are hidden on a server, because only the server can move a character to another map. Landmarks within the map work, since the server takes the new position.
- The account's password sits in `settings.cfg` as written. That's fine for a server at home, not for one on the internet.
- `Item.dbc` turns the server's item ids into the displays creatures hold.

## Not done yet

- Game objects still come from spawn files. The server sends them too, holidays and transports included.
- Other players aren't shown: their models need composing from race, skin, face, hair and gear, not a creature display's baked texture.
- The sky keeps its own time. The server sends its time of day at login (`SMSG_LOGIN_SETTIMESPEED`).
- Creatures that run, like chasing ones, play their walk faster. Emote states, mounts and display changes after a unit appears are ignored.
- No queues, realm choice, character creation, reconnecting, or other maps.
