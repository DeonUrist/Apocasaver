# Apocasaver

**Autosave** for Apocalypter (BepInEx 5 plugin).

Saves the game at a configurable interval into the slot the current character was last saved to or loaded from, and stamps the save
slots so you can tell autosaves and manual saves apart.

## Features

- Autosave every *N* minutes (default 10) to the character's current slot. It only fires while actually playing: no menu open,
  alive, awake, on foot for ≥ 2 s, time not paused. If the interval elapses while driving, it saves shortly after you get out.
- Uses the game's own save flow (the same one the save-slot menu triggers), so saves are fully compatible.
- Slot labels are stamped `Autosave (Seed: …) dd/MM/yyyy HH:mm` or `Manual (Seed: …) …`, with the label font auto-fit to the box.
- On-screen status: **AUTOSAVING** while the save runs; a reminder to save at least once if the character has never been saved
  (autosave never writes to a slot that was not chosen by you).
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocasaver.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocasaver.cfg`, section `[General]`:

| Key | Default | Description |
| --- | --- | --- |
| `Enabled` | `true` | Turn autosaving on/off |
| `IntervalMinutes` | `10` | Minutes between autosaves (1–120) |
| `Apocasetter` | `true` | Show in the Apocasetter Mods menu |

## Building

- `dotnet build` (override the game path with `-p:GameDir=...`); deploys to `BepInEx\plugins` after build, or
- `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars).

Needs `Assembly-CSharp-firstpass.dll` (Easy Save 3: `ES3`, `ES3Settings`) in addition to the usual Unity/BepInEx/PlayMaker references.

## How it works

The current slot is `ES3Settings.defaultSettings.path` (set by the game's slot buttons). The mod watches the `SaveLoadGame` FSM:
entering `SaveGame` or the load states arms the autosave on that slot; a new game, scene change or slot switch disarms it. An autosave
stamps the slot label and then sends `Clicked` to the `savegame` button FSM, which runs the game's normal save sequence.
