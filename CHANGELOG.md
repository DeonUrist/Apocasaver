# 1.9.0

- Fix autosaves after reloading: synchronize all native save paths and use the selected slot's own save button.
- Wait for both native save systems and fresh player/global writes, explicitly commit the selected cache, and verify its
  disk write before announcing success.
- Add a red 5–4–3–2–1 countdown before each autosave. Early reminders remain configurable.
- Pause through the native menu, show the native loading-screen artwork with **AUTOSAVING**, then close the menu and resume.
- Drop held items through vanilla GrabItem before pausing. Remove save-time item holding, physics overrides and menu-click
  suppression; no item is restored to the hand after loading.
- Preserve the vehicle-part camera guard, including GrabItem state while the camera is disabled. Restore previous restart
  settings when the item leaves the hand.
- Preserve the optional Apocapocket HandPose metadata API; prevent reads from falling back to another slot.
- Clear game references on scene changes and restore UI/input state after timeouts or save errors.
- Carry forward the previous `Enabled` setting to `Autosave enabled` and remove obsolete held-item switches.

Validation: Release build with no warnings/errors; 51 simulated transaction checks; native hook-target/signature and
Apocapocket API checks against installed game assemblies. An interactive in-game replay is still pending.

Install: replace `BepInEx\plugins\Apocasaver.dll` with the DLL in `Apocasaver-1.9.0.zip`, then restart the game. Requires BepInEx 5.x.
