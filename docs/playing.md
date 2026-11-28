# Playing wow VR

wow VR lets you walk World of Warcraft: Wrath of the Lich King's world on your desktop or in a VR headset, and play on a 3.3.5a server such as AzerothCore. It contains no game files: it reads everything from your own copy of the game, which you need to have.

It's an early, unofficial fan project, not affiliated with Blizzard Entertainment.

## What you need

- **Windows 10 or 11, 64-bit**, with a graphics card and driver that support Vulkan (any recent NVIDIA, AMD or Intel card).
- **World of Warcraft 3.3.5a (build 12340)**: the folder with `Wow.exe` and `Data`. It isn't included and can't be downloaded here. Only enUS clients have been tried.
- **To play online:** an account on a 3.3.5a server. It's made for [AzerothCore](https://www.azerothcore.org/); others may work.
- **For VR (optional):** a PC VR headset and an OpenXR runtime, such as SteamVR, or the Meta Quest Link app for Quest headsets.

## Install

1. Download `WowVR-<version>-windows-x86_64.zip` from the [releases page](https://github.com/davidrios/wow-vr/releases).
2. Unzip it anywhere, keeping all the files together.
3. Run `WowVR.exe`.

Windows may warn that the app is from an unknown publisher, since it isn't signed. Choose **More info**, then **Run anyway**.

The first time, it asks for your World of Warcraft folder: choose the one with `Wow.exe` and `Data` in it (choosing `Data` itself works too). It remembers it.

## Play

The game opens on a login screen. Enter your server's address, your account and password, pick a realm and a character, and **Enter world**. You can make and delete characters there too. **Explore offline** walks the world without a server; it's empty of creatures unless you've added spawn files.

### Desktop

The controls are WoW's:

| | |
|---|---|
| Mouse | The cursor. Hold the right button to turn and look, the left to look around your character, both to run forward. Left click selects, right click talks, loots, attacks or opens. The wheel zooms, into first person. |
| W / S | Forward and back |
| A / D | Turn (strafe while holding the right button) |
| Q / E | Strafe |
| Space | Jump (up while flying or swimming; X goes down) |
| Num Lock | Run on its own |
| Tab | The next enemy |
| Enter or / | Chat: `/s`, `/y`, `/w Name`, and `.commands` |
| 1 to 0, -, = | The action bar. Drag spells from the spellbook and items from your bags onto it. |
| B, C, P | Bags, character, spellbook |
| F | Fly (off a server) |
| Esc | Close windows, clear the target, then the game menu (options, video settings, log out, exit) |

### VR

Turn on your headset and your OpenXR runtime first, then start the game with `--xr-mode on`. The easiest way is a shortcut: right-click `WowVR.exe`, **Create shortcut**, then in the shortcut's properties add ` --xr-mode on` after the target, so it reads `...\WowVR.exe" --xr-mode on`.

| | |
|---|---|
| Left stick | Move |
| Right stick | Turn, or scroll the panel the laser points at |
| Left trigger | Aim a teleport, release to go |
| Left Y | Fly (off a server) |
| Left X | The wrist menu |
| Right trigger | Click on panels; on a server, select what the laser points at |
| Right A | Use the target: talk, attack, loot |
| Right B | Chat, with the virtual keyboard |

On a server, the HUD (your frames, chat and action bar) is above your left wrist: raise it to look. The wrist menu's VR alignment page calibrates your hands to your controllers and adjusts the laser.

Choose the WoW folder on the desktop first: the folder picker shows on the monitor, not in the headset.

## Settings and logs

They're in `%APPDATA%\Godot\app_userdata\WoW VR`:

- `settings.cfg`: your options, video settings, the WoW folder and the last login. To choose another WoW folder, run `WowVR.exe -- --client-path="D:\Games\WoW 3.3.5a"` once, or delete the `[client]` lines.
- `logs\`: what happened in each run. Include the latest one when reporting a problem.

A remembered password is stored encrypted for your Windows account, in `credentials` there.

## When something goes wrong

- **A black or grey window, or it closes at once:** update your graphics driver; the game needs Vulkan. The log says what failed.
- **It keeps asking for the folder:** the folder you chose has no `Data` folder with `.MPQ` files in it.
- **VR doesn't start:** make sure the headset and SteamVR or Quest Link are running before the game, and that your runtime is set as the system's OpenXR runtime (SteamVR: Settings, OpenXR).
- **Can't log in:** check the server address (a name or IP, without a port: the game uses 3724) and that the server is a 3.3.5a one.

Problems and ideas go to the [issues page](https://github.com/davidrios/wow-vr/issues).
