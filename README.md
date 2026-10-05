# Apocasaver

**Autosave** and **Save naming** for Apocalypter (BepInEx 5 plugin).

Saves the game at a configurable interval into the slot the current character was last saved to or loaded from, and stamps the save
slots so you can tell autosaves and manual saves apart.

## Features

- Autosave every *N* minutes (default 10) to the character's current slot. It only fires while actually playing: no menu open,
  alive, awake, on foot for ≥ 2 s, time not paused. If the interval elapses while driving, it saves shortly after you get out.
- A red **5 → 4 → 3 → 2 → 1** countdown precedes every autosave. If you enter a menu/vehicle or stop being eligible, the
  countdown restarts when you can save again.
- Drops the held item with vanilla GrabItem and gives it 0.75 seconds of live physics before pausing.
- Enters the native pause menu and clicks the selected slot's native save button. The game's loading-screen artwork covers
  the save with **AUTOSAVING**; gameplay resumes through the native menu-close event when the save is committed.
- Synchronizes all save paths before saving, waits for both native save systems, and explicitly commits and verifies the
  selected slot on disk. This fixes the stale registry slot after loading a save (v1.9).
- Slot labels are stamped `Autosave (Seed: …) dd/MM/yyyy HH:mm` or `Manual (Seed: …) …`, with the label font auto-fit to the box.
- On-screen status: optional *Autosave in 30 sec* / *15 sec* early reminders; a reminder to save at least once if the character has never been saved
  (autosave never writes to a slot that was not chosen by you).
- **Save naming** (v1.6, on by default since v1.7): a small popup asks for a name before every manual save; the slot then reads `Name (Seed: …) date` and autosaves to that slot keep the name. Cancel aborts the save. With [Apocasetter](../Apocasetter) installed the popup uses its theme and blocks game input while you type; without it, a plain popup.
- Held items, including crates/boxes, use vanilla drop behavior when saved. There is no hand restoration on load or
  save-time holding/physics override (v1.9).
- **Vehicle-part camera guard**: a held cassette/radio/headlight survives switching the vehicle camera. Guarded FSMs retain
  their state while the camera hierarchy is disabled, and their previous restart flags are restored when the item leaves.
- Opt-in entry in the [Apocasetter](../Apocasetter) Mods menu (no dependency on it).
- Light: nothing is looked up per frame; the current save slot is read once a second and around saves.

## Installation

Install [BepInEx 5.x](https://github.com/BepInEx/BepInEx/releases) (win_x64), run the game once, then copy `Apocasaver.dll` to
`BepInEx\plugins\`.

Config: `BepInEx\config\com.denis.apocalypter.apocasaver.cfg`, section `[General]`:

| Key | Default | Description |
| --- | --- | --- |
| `Save naming` | `true` | Ask for a name when you save (slot menu, ESC menu or a save point). The name replaces *Manual* in the slot label, autosaves keep it, Cancel aborts the save. Uses the Apocasetter look when installed |
| `Autosave enabled` | `true` | Turn autosaving on/off (was `Enabled` before 1.8; the old value is carried over once) |
| `IntervalMinutes` | `10` | Minutes between autosaves (1–120) |
| `Autosave warning (sec)` | `30` | Early reminders every 15 s. `0` disables early reminders. The final red five-second countdown always runs |
| `Apocasetter` | `true` | Show in the Apocasetter Mods menu |

The held-item fixes have no switches since 1.8 (the old `[HeldItem]` section is removed from the file automatically).

## Building

- `dotnet build -c Release` (override the game path with `-p:GameDir=...`); deploys to `BepInEx\plugins` after build.
  Use `-p:SkipDeploy=true` to build without deployment, or
- `./build.sh` with mono `mcs` (`MANAGED` / `BEPCORE` env vars).

Needs `Assembly-CSharp-firstpass.dll` (Easy Save 3: `ES3`, `ES3Settings`), `0Harmony.dll` and `UnityEngine.PhysicsModule.dll`
in addition to the usual Unity/BepInEx/PlayMaker references.

## How it works

The current slot is `ES3Settings.defaultSettings.path` (set by the game's slot buttons). The mod watches the `SaveLoadGame` FSM:
entering `SaveGame` or the load states arms the autosave on that slot; a new game, scene change or slot switch disarms it.
After the countdown and vanilla item drop, an autosave synchronizes `SaveLoadGame.SaveFile` and
`NewGO_ArrayList/Save_NewGO_ArrayList.SaveFile`, enters the pause menu, and sends `Clicked` to `save_game_N/Continue`.
The slot's existing custom name is preserved. The loading-screen copy contains visual components only, so it cannot execute
load actions. Save callbacks verify new `Player` and `global` writes, the registry cache flush, and both FSMs' completion.
The selected cache is then explicitly stored to disk and a unique commit marker is read back before gameplay resumes.
Timeouts/errors restore the pause/UI state and report failure. Scene changes clear cached game references and cancel the
transaction without overriding the new scene's time scale. If verification hooks cannot be installed, autosave stays inactive
and logs the reason.

### Held item

`HeldItem.cs` sends the native `drop` event from `GrabItem/ItemInHand`. Autosave waits before pausing; manual menu clicks retain
vanilla behavior. No transform teleport, collider override, or held-item restoration is performed. The vehicle-part guard
protects `CheckTag`/`LockPhysics` and keeps GrabItem's state across a camera switch; it releases custody on drop/throw.

`Apocasaver.HandPose` (`Get(name)` / `Set(name, "x,y,z,qx,qy,qz,qw")`) is public so other mods can keep per-item hand poses in the
save file; [Apocapocket](../Apocapocket) uses it (by reflection, optional) for pocketed items. This API only stores/reads metadata;
Apocasaver never applies the poses to objects. Reads stay within the selected slot. Queued metadata is flushed into an autosave
before its final commit, or about 1 s after a manual save.

## Verification

- `dotnet run --project verification/Verification.csproj -c Release`: 51 transaction checks, including repeated load/autosave
  cycles, countdown interruption, slot switches, item settling/re-grabbing, native errors, commit errors and timeouts.
- `powershell.exe -NoProfile -File verification/CheckBindings.ps1`: verifies native hook targets/signatures and the public
  Apocapocket bridge against the installed game assemblies. It does not run Unity or detour methods outside the game.

The transaction tests use a simulated runtime. The native visual flow and the reported rollback still require an in-game
replay: save manually, move and autosave, reload, move again and autosave, then reload and confirm the newest position.
