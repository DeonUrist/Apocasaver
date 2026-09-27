# Apocasaver

**Autosave** for Apocalypter (BepInEx 5 plugin).

Saves the game at a configurable interval into the slot the current character was last saved to or loaded from, and stamps the save
slots so you can tell autosaves and manual saves apart.

## Features

- Autosave every *N* minutes (default 10) to the character's current slot. It only fires while actually playing: no menu open,
  alive, awake, on foot for ≥ 2 s, time not paused. If the interval elapses while driving, it saves shortly after you get out.
- Uses the game's own save flow (the same one the save-slot menu triggers), so saves are fully compatible.
- Slot labels are stamped `Autosave (Seed: …) dd/MM/yyyy HH:mm` or `Manual (Seed: …) …`, with the label font auto-fit to the box.
- On-screen status: *Autosave in 30 sec* / *15 sec* countdown (v1.5), **AUTOSAVING** while the save runs; a reminder to save at least once if the character has never been saved
  (autosave never writes to a slot that was not chosen by you).
- **Keeps the item in your hand across saves and loads** (v1.4): the item you are holding is recorded when the game is saved and
  put back into your hand after loading. Two vanilla bugs are fixed on the way: clicking any pause-menu button (e.g. *Save*)
  no longer drops the held item, and an item saved while held no longer falls through the world after loading (its colliders
  are made solid for the duration of the save).
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocasaver.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocasaver.cfg`, section `[General]`:

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Turn autosaving on/off |
| `IntervalMinutes` | `10` | Minutes between autosaves (1–120) |
| `Autosave warning (sec)` | `30` | Show *Autosave in X sec* this many seconds before an autosave, then every 15 s (never at 0). `0` disables the warning |
| `Apocasetter` | `true` | Show in the Apocasetter Mods menu |

Section `[HeldItem]`:

| Key | Default | Description |
| --- | --- | --- |
| `KeepHeldItem` | `true` | Remember the held item on save and put it back in your hand after loading |
| `SaveFix` | `true` | Make the held item's colliders solid while the game saves (prevents falling through the world after a load) |
| `MenuClickFix` | `true` | Pause-menu clicks no longer reach the grab FSM (no more dropping the item when you click *Save*) |
| `VerboseLog` | `false` | Detailed held-item logging |

## Building

- `dotnet build` (override the game path with `-p:GameDir=...`); deploys to `BepInEx\plugins` after build, or
- `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars).

Needs `Assembly-CSharp-firstpass.dll` (Easy Save 3: `ES3`, `ES3Settings`), `0Harmony.dll` and `UnityEngine.PhysicsModule.dll`
in addition to the usual Unity/BepInEx/PlayMaker references.

## How it works

The current slot is `ES3Settings.defaultSettings.path` (set by the game's slot buttons). The mod watches the `SaveLoadGame` FSM:
entering `SaveGame` or the load states arms the autosave on that slot; a new game, scene change or slot switch disarms it. An autosave
stamps the slot label and then sends `Clicked` to the `savegame` button FSM, which runs the game's normal save sequence.

### Held item

`HeldItem.cs` is self-contained. `GrabItem`'s `ItemInHand` state polls the left mouse button every frame, also while the pause
menu is open, so a menu click is what drops the item in vanilla; a Harmony prefix on PlayMaker's `GetMouseButtonDown` skips that
poll for `GrabItem` while `__GameManager__/Menu` is not in `play`. On the game's `SaveGame` event the held item's trigger colliders
are made solid (collisions with the player ignored via `Physics.IgnoreCollision`, which Easy Save does not store) and restored when
`SaveLoadGame` leaves its save states. The record (`apocasaver.held = "<item>|<seed>"`, `apocasaver.pose.<item>`) is written about
1 s after the save finished, both into Easy Save's cache (the game stores its cache at the end of a save) and straight to the file.
After a load, once `SaveLoadGame` is in `isPlay` and the menu is closed, the item is found by name (seed must match) and handed to
`GrabItem`'s `Grab` state at its saved hand-local pose.

`Apocasaver.HandPose` (`Get(name)` / `Set(name, "x,y,z,qx,qy,qz,qw")`) is public so other mods can keep per-item hand poses in the
save file; [Apocapocket](../Apocapocket) uses it (by reflection, optional) for pocketed items.
