# 2.0.1

- Fix the false startup error "Save participant is still loading" when the game destroys Apocapatrol/Apocapocket's native
  plugin components while their managed wrappers and persistent runners remain available. Resolve bindings from the managed
  wrapper using CLR reference identity instead of Unity's overloaded null comparison.
- Defer genuinely pending companion initialization and retry on later frames; do not permanently latch recovery failure.
- Report integration-binding failures separately from damaged/missing backup recovery failures.
- Add native regression tests for destroyed companion components and temporarily unconstructed plugin instances.

Validation: clean Release build, 91 automated checks, 27 native no-companion checks and 39 native companion checks.

# 2.0.0

- Rebuild supported manual/automatic saves from an empty ES3 cache; verified backups, staged writes, rollback and startup
  recovery cover the slot, its label and supported companion sidecars. Keep three successful pre-save backups per slot.
- Preserve source-playthrough location history and opaque mod data across slots; avoid importing the overwritten target's
  previous world. Capture/reapply static site state around terrain removal/reuse.
- Replace moving/local-position vehicle hinge save keys with stable reference keys and retain legacy loading fallback.
- Add `CleanSaves` (off by default): distant abandoned disposable remains/packs and, with Apocapatrol, unclaimed patrol
  vehicles, their attached crew/cargo/parts and generated detached items. Protect handled, stored, owned, merchant/unique
  and referenced objects. Apply destruction only after successful commit; failed saves restore exclusions.
- Coordinate Apocapatrol 2.7.0 sidecars/embedded state and Apocapocket 2.0.7 mapping verification. Mirror file-only ES3 writes
  into the new cache. Fence patrol construction and cleanup until the transaction finishes.
- Validate all required records and staged raw records; detect world registration/terrain changes and native writer errors.
- Fix build reference resolution so old DLLs in verification runtimes cannot override explicit game/plugin references.

Validation: clean Release build; 91 filesystem/policy/compatibility checks; isolated native Unity fixture runs with and
without companion mods. Full interactive/long-session gameplay validation remains pending; see docs/2.0.0-verification.md.

# 1.9.1

- Add optional Apocapocket inventory compatibility. Wait for inventory operations and pocket load restoration before saving.
- Verify physical hand parenting before treating GrabItem's cached `Item` reference as held.
- Protect pocket-owned objects in every slot, including the last item stored and objects temporarily moved into the world
  by Apocapocket's save normalization. Release stale native grab references without executing drop actions on stored items.
- Wait for pocket normalization to finish before completing autosave; queued re-equip operations do not block resuming.
- Preserve Apocapocket's own vehicle-part restart flags when an item transfers from the hand into its custody.

Validation: clean Release build; 76 simulated transaction/compatibility checks; native hook targets and installed
Apocapocket inventory bindings verified. Interactive confirmation of the reported last-stored-item case is pending.

Install: replace `BepInEx\plugins\Apocasaver.dll` with the DLL in `Apocasaver-1.9.1.zip`, then restart the game. No Apocapocket
replacement is required; compatibility bindings were checked against Apocapocket 2.0.4.

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
