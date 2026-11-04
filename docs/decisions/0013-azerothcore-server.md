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

## The screens before the world

The game opens on a login screen, then a realm list (skipped with one realm, or the last one used), then the account's characters on a stage, where one can be entered, made or deleted. "Explore offline" plays from spawn files as before.

- **Laid out like VRChat's menus, in the game's colors.** In VR one panel floats in front of the player, at their left, placed where they look when the screens open, with the laser pointer on the right hand and XR Tools' virtual keyboard below it while a text field has focus. On desktop the same panel is on screen. Nothing uses popups, which don't open on a panel in VR: deleting asks for a second press instead. Its colors are the game's: near-black panels with gold edges, dark red buttons and gold text, in the client's own font, `Fonts\FRIZQT__.TTF`.
- **The void around it is from the client:** Hellfire Peninsula's sky and colors, as its light tables give them, with a thick fog in its horizon and fog colors where the sky has no ground, and the Celestial Planetarium's floor as the stage. The loading screen shows the same sky and fog in its void.
- **Characters are drawn as the game draws players**, not NPCs: `CreatureDisplayTable.ResolveCharacter` and `CharacterTexture` paint the body from skin, underwear, face, facial hair, scalp and each worn item's pieces, following WoWModelViewer 0.7.0.6's CharControl and CharTexture. The character list gives the look and each slot's ItemDisplayInfo row. New characters choose from CharSections' player rows.
- **Remembering the password** is a choice, kept in the system's own store by `CredentialStore`: DPAPI on Windows, the Keychain on macOS, the Secret Service through `secret-tool` on Linux. Without one the box is off. The server, account, realm and character are remembered in `settings.cfg`'s `[login]`.

## The game menu

Opening the menu, with Esc or Tab on desktop or X on the wrist in VR, shows a game menu first, as the game's own Esc menu: Return to game, Character selection, Log out, Exit game, then Options (the landmarks and settings it had before) and Video. Offline it offers the login screen instead. Leaving the world asks the server to log out: it refuses in combat or falling, lets the character go at once in an inn or city, and otherwise after 20 seconds, which the menu counts down with a Cancel. Then the scene reloads into the character list on the same connection, since a logged-out session is back at the list, or into the login screen.

## The body

Entering the world puts the character's own body at the player's feet (`PlayerAvatar`). It walks or runs to match their speed, and stands while flying or swimming. On desktop the camera hangs behind on a boom that stops short of walls: the mouse wheel moves it from first person, where the head is hidden, out to 15 yards. In VR the player stands in the body, whose head is always hidden, and it turns to follow where they look once the head has turned far, or as they move. Its hands aren't the tracked ones.

## Settings

The login screen is the usual way in. `--server=host` and `--character=Name` skip it, as does `[server]` in `settings.cfg` with `host`, `account`, `password` and `character`, the earlier way. `WOWVR_AC_USER` and `WOWVR_AC_PASS` override the account. `--offline` and `--start=` skip it to play offline, and checks, tours, benchmarks and screenshots skip it unless given `--login` or `--login-check`. `tools/ac-probe` logs in the same way and prints what the server shows, without Godot; `--list` prints the characters and their gear.

## Consequences

- **The server keeps the position.** Moving in the game moves the character on the server, which saves it. Logging in again starts where the player left.
- The server's own Z is trusted for creatures, since it has its maps. Ground snapping applies only to spawn-file creatures.
- Continents' menu buttons are hidden on a server, because only the server can move a character to another map. Landmarks within the map work, since the server takes the new position.
- The account's password sits in `settings.cfg` as written. That's fine for a server at home, not for one on the internet.
- `Item.dbc` turns the server's item ids into the displays creatures hold.

## Not done yet

- Game objects still come from spawn files. The server sends them too, holidays and transports included.
- Other players aren't shown yet. The model code for them is done for the player's own body; what's left is reading their looks from the server's player fields and their movement packets.
- The sky keeps its own time. The server sends its time of day at login (`SMSG_LOGIN_SETTIMESPEED`).
- Creatures that run, like chasing ones, play their walk faster. Emote states, mounts and display changes after a unit appears are ignored.
- No queues, reconnecting, or other maps. The character screen's backdrop isn't the client's own per-race scene, which needs the M2's cameras through wow-alchemy's C ABI. New characters wear their class's starting outfit from `CharStartOutfit`.
