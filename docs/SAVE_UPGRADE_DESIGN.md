# Apocasaver save rebuild and CleanSaves proposal

**2.0.2 policy correction (11 October 2026):** the original strict validation gates below are superseded. Only disk
persistence errors fail an attempted write. Missing callbacks, optional compatibility checks, object changes, metadata
layout checks and completion timeouts are diagnostics. Preserve prior useful records, refresh native registry arrays and
skip uncertain cleanup. Unavailable rebuild integration uses native saving without deleting the destination or blocking loads.

Research date: 10 October 2026. Proposal only; no production plugin or player save was changed.

## Recommended behavior

Every supported manual save and autosave builds a fresh save for the current playthrough, regardless of whether the destination slot is empty or occupied. An occupied destination is backed up, its disk file is deleted, its ES3 cache is replaced, and the new save is written and verified. Any save failure restores the destination and its metadata, cancels late writes, and displays a warning.

Fresh rebuilding must also carry forward useful history from the current playthrough. Thus old and new slots use the same upgraded algorithm; neither is limited to the incomplete snapshot vanilla writes to an empty slot. This resolves the apparent conflict between “save like a new slot” and “keep all useful data.” Save size can still grow legitimately with world progress and retained objects.

`CleanSaves = false` by default. When enabled, it additionally removes a restricted set of abandoned disposable objects from the saved world and, after successful commit, from the running world. Fresh rebuilding alone never deletes live objects.

## Research findings that affect the design

1. **Vanilla retains keys that are not rewritten.** Installed ES3File.Save replaces one dictionary entry. StoreCachedFile serializes the cache. Deleting only the disk file will not clear its cached contents. The direct-file ES3 writer can also merge old keys. Fresh rebuilding therefore needs an explicitly new cache and control of writes throughout the transaction.
2. **Moving objects can create new keys without creating new objects.** Several `hinge_door_L/saveItemVar` definitions get their current world position and use that vector as the key for door rotation. Each new position can leave an obsolete record. Some hood definitions use local positions instead. A string that looks like a coordinate does not reliably distinguish static history from movable state.
3. **Some old position records remain useful.** Static camp/building/spawner records retain spawn flags, visited state and resource state. Only currently active SpawnerStatic objects are scanned at save time. A new slot must inherit useful records for unloaded locations from the current playthrough.
4. **Missing records are not always harmless.** The shipped ES3PlayMaker.LoadAll uses ES3.Load without a default value. Load also requires a record when no defaultValue is supplied. Dropping a zero-valued or apparently default record can produce load errors; default-value elision is excluded from the first release.
5. **Mod persistence extends beyond native completion.** Apocapocket normalizes inventory before saving, injects a mapping before the registry flush, then verifies/writes it about a second later. Apocapatrol writes its sidecar on SaveGame and embeds it about a second after isPlay. Its sidecar is also a load fallback. Those writes must join the transaction, including rollback.
6. **Broad cleanup categories lose resources.** Scorpion_Small_Dead is edible. Scorpion_Big_Dead and Nightwalker_Dead can be butchered into meat. Even meat_rotten has a Food FSM. Liquid containers retain a LiquidCapacity and remain after depletion; they are reusable. Consumed dogfood and depleted ammunition already have native DestroySelf paths.

The expanded scan parsed **8,129 FSMs**, found **681 owners of ES3 actions**, and encountered **zero parse errors and zero DeleteKey/DeleteFile actions** in the extracted asset set. This scan includes the menu scene. It is not a full audit of all native C# or other mods, and the extracted assets must be checked against the installed game version before shipping adapters.

The console probe uses the installed vanilla ES3File implementation in RAM, with no Apocasaver loaded. It confirms that unchanged repeated writes do not grow the cache, whereas 100 changing position keys retain 100 records. These tests establish library behavior, not a completed gameplay A/B replay.

## What to keep, rebuild or discard

| Data | Handling | Why |
|---|---|---|
| Player, global, PlayerInCar, ammo, faction relations, starter-area state, itemNameID counter and other native singleton records | Regenerate from the current session; validate expected writes | These describe the current character/world. Never transplant them from the destination slot |
| Cars, items, sandstorms and their transforms/prefab/reference IDs | Rebuild the three native registries from current valid object graphs, including inactive saved objects | Root membership alone is insufficient: attached parts, cargo, inventories and referenced children are required |
| Current object condition, liquids, ammo counts, purchase state, perishable state, door rotations and other component state | Regenerate using native save actions; record the writer and owning object | Removing these resets gameplay state. Current moving-position keys replace the old keys for that owner |
| Static location spawn/visited/resource/door state | Preserve useful unloaded-location history from the current playthrough; overwrite with current writes when loaded | Without it, visiting an old location can restore the wrong state |
| Apocapocket.Slots and retained item hand poses | Generate current mapping through the pocket participant; retain poses only for retained objects when ownership is known | Pocketed items temporarily enter the world during normalization and must remain protected |
| Apocapatrol.Patrol and its sidecar | Generate and validate through the patrol participant | Preserve vehicle ownership, crew, cooldown and customization; prevent stale sidecar fallback |
| Map exploration and other opaque mod data | Preserve source-playthrough bytes unless the owning mod provides a replacement or pruning rule | No format-specific cleanup without understanding the format |
| Unknown legacy records | Carry forward conservatively from the source playthrough, report as unclassified | Avoid deleting useful data based only on syntax. Both empty and occupied destinations inherit the same source records |
| Prior keys owned by a moved component, when current replacement is known | Discard | Obsolete position-key records cannot restore that component at its current saved location |
| Prior records exclusively owned by a confirmed destroyed object and unreachable from retained objects/mod state | Discard | No surviving object can consume them |
| Prior transaction markers and expired Apocasaver-only bookkeeping | Replace or compact | Keep one current marker and bounded metadata, not a growing transaction history |

Do not remove supposedly redundant copies of live object serialization or individual PMDataWrapper variables in this release. The loader, reference manager and mods can depend on them. Current saves show these payloads dominate size; additional serializer optimization requires a separate compatibility audit.

## Record ownership and history

Introduce a `RecordIndex` that records successful native Save/SaveAll writes with the resolved file, key, type, owning FSM and object, nearest saved prefab/root reference ID, and lifetime category. Discover actual paths through GetSettings; inspector default paths are often unused.

The index distinguishes `SessionState`, `DynamicObjectState`, `StaticWorldHistory`, `ModState` and `Unknown`. Classification comes from the actual writer and object hierarchy, not `'(x,y,z)'`, `'(Clone)'`, distance, active state, or payload type alone. A record can have multiple owners; it may only be pruned when every owner/reference permits it.

Track history from successful loads, successful writes and static-location unload/recreation. A session ledger merges preserved source history with current writes. Store necessary ownership metadata in the main save so it travels with the save; avoid making a separate history file required for loading.

Capture changed static-location state even when the player travels out of a tile before their first save. The terrain code supplies `TerrainTile.OnBeforeResetTerrain`, but tile removal and object-pool reuse are separate paths: `TerrainTile.Move/Remove` and the private `Den.Tools.ObjectsPool.Pool` reposition/clamp operations need audited pre-change hooks. Pool reuse can change an object's world location without destroying it. Capture the old site before that happens, and restore the new site's ledger state before its spawners can run. `OnTileApplied` is a possible restoration integration point, but its ordering relative to instantiated spawners must be verified. Do not rely on OnDestroy or a once-per-second scan. Serialize through a dedicated history collector; do not broadcast a full game save or flush slot files during terrain generation. The timing and initialization order of these terrain adapters require native tests.

Add a playthrough ID and schema version. Generate an ID when starting a new playthrough or adopting the explicitly loaded legacy save. Retain it when saving to another slot. Terrain seed is not a unique playthrough ID. Never scan other same-seed slots and merge them. Never import the destination's old contents into the session: they are rollback data only.

For legacy saves, classify known native schemas and conservatively retain ambiguous records. The first upgraded save may therefore be larger than a vanilla fresh-slot save. The mod cannot reconstruct history already lost by prior fresh-slot saves. Ownership becomes more accurate after observed saves; no blanket “delete unused keys” migration.

Detect keys written by multiple independent objects. Local-position hinge keys can collide between vehicles; fresh rebuilding does not fix this. The prototype must test actual collisions and introduce a versioned Save/Load adapter for affected native component schemas where needed, using persisted root/component identity. Read the old native key only as a migration fallback when the new key is absent. If legacy data was already overwritten, the missing prior value cannot be recovered. This adapter requires multi-vehicle load tests and cannot be implemented as a generic key renamer.

## Save transaction

Use one `SaveCoordinator` for every supported manual and automatic entry point, independent of the autosave-enabled setting. Its states are:

`Idle → Prepare → BackupVerified → DestinationReset → NativeSaving → ParticipantsReady → Verify → Committed`

Any failure before `Committed` enters `Rollback`; failed rollback enters `RecoveryRequired` and blocks further saves.

1. **Prepare.** Complete name selection first. Lock out another save/load/slot switch and stop background cache flushes for transaction files. Capture source playthrough and destination separately, before native slot buttons change paths or labels. Drain prior delayed writes through participants. Require bindings and history to be readable. Capture the approved CleanSaves exclusion set without destroying anything.
2. **Back up.** Copy the existing target byte-for-byte to a transaction-specific backup and verify its size/hash. Back up participant sidecars and record whether each file existed. Capture target slot metadata (`saveTimeN`), UI labels, original save paths and the ES3 cache baseline. Write and flush a recovery journal before deletion. If backup/journal creation fails, abort before resetting the destination.
3. **Reset destination.** Delete the existing target disk file as requested, remove/replace its ES3 cached file, and create an empty replacement. Seed it only with the useful history/opaque records selected from the source playthrough. Synchronize all native save paths. Preserve the verified backup independently of ES3's internal temporary/backup files.
4. **Run native saving.** Replay exactly one native save action. Preserve held-item settling and pocket normalization. Record completion receipts for required native writers and all applicable retained object state. Store/auto-flush operations during this phase must not publish a supposedly finished save before participant completion. Route intercepted flushes to transaction staging where supported; otherwise fence them and defer publishing until the final flush.
5. **Complete participants.** Collect fresh inventory, hand-pose, patrol, exploration and supported external records. Stage sidecars with the same transaction ID. Discard delayed writes from aborted generations. Explicit participant readiness is required; waiting an arbitrary one second is insufficient.
6. **Verify.** Validate all required native singleton records, all three registries, expected retained/excluded object identities, required per-object write receipts, world-history carryover and participant schemas. Generate a unique commit marker and reopen the proposed disk save through an independent file reader. Verify the marker, all required records and candidate hashes. A marker alone proves a write occurred, not that the save is complete. Full Unity load/replay verification remains part of release tests.
7. **Commit.** Publish staged slot data, target label and participant sidecars with a journaled multi-file commit. Use same-directory replacement/moves where supported. A collection of individually replaced files is not one atomic transaction; recovery must reconcile the complete bundle. Mark `Committed` only after every required component verifies. Arm autosave/update last-save time and show success only here.
8. **Apply cleanup.** After commit, destroy the approved excluded objects from the running world. Never destroy them before the save can succeed. If post-commit world removal needs retry, retain exclusion metadata so the next save cannot reintroduce them and report cleanup status separately from save failure.

On save failure, fence/cancel late callbacks first; restore backed-up files and only this slot's settings keys/labels, restore native paths and the original cache, restore temporary registry exclusions and participant state, verify restored file hashes, then resume. Do not replace all of SaveSettings.es3 and reset unrelated user settings. If the target was originally empty, remove the partial result and restore its empty state.

Warning after successful rollback: **“Saving failed. The previous save was restored.”** For an initially empty slot: **“Saving failed. No save was created.”** If restoration fails: **“Saving failed and automatic recovery could not finish. Your backup is at …”** Keep every recovery artifact and block subsequent writes in that state.

Crash recovery runs before the menu can load affected slots. An unfinished journal restores the last coherent backup bundle unless a fully verified committed bundle is recorded. Preserve the last three successful pre-save backups per occupied slot, plus every unresolved transaction backup; prune only after verified success.

## Mod integration

Define optional participants with `Prepare(context)`, `WriteSnapshot(context)`, `Ready`, `Validate`, `Commit` and `Rollback`. They receive source/destination IDs, transaction generation, retained/excluded object identities, and staged output paths. They also declare protected objects and record ownership where applicable.

Provide tested adapters for the installed Apocapocket and Apocapatrol versions; support future explicit bridges rather than depending permanently on private field reflection. The current pocket SaveBusy property does not include its delayed mapping verification, so it cannot be the completion barrier. Patrol sidecars must be staged/restored and its pending embedded write must be canceled on rollback.

Unknown source mod records are retained. That protects existing opaque data, but cannot guarantee fresh state for an unknown asynchronous writer. If a detected required writer cannot be fenced or validated, abort the rebuild before deletion with a specific warning; do not claim a transactional save succeeded. Document a tested compatibility matrix. This is a release blocker to resolve in the prototype, not an assurance that arbitrary mods are already covered.

## CleanSaves prototype

Recommended initial scope: **distant, abandoned, unprotected non-resource corpses and confirmed exhausted disposable items**. It intentionally trades abandoned disposable world objects for smaller saves and fewer live objects. It does not reset locations or respawn their contents.

```ini
[Saving]
CleanSaves = false
```

Settings copy:

> **CleanSaves**
> Remove abandoned non-resource corpses and exhausted disposable items when saving. Items must be over 1 km away for 10 minutes. Your stored items, reusable containers, vehicles and world progress are kept.

The **1,000 m / 10 minutes** thresholds are proposed tuning values, not proven optimal settings. Time advances only during active gameplay; returning within the radius resets the timer. Run classification against registered live instances, including inactive ones, and consult the complete protected/reference graph before excluding anything.

| Prototype category | Initial decision |
|---|---|
| Ordinary non-resource corpse with no Food/Carcasse interaction, loot/valuable descendants, quest role or protection | Candidate. Exact prefab allowlist only; Zombie_Runner_Dead is a research candidate, pending runtime inspection/load tests |
| Depleted disposable ammunition/battery pack or native-consumed object left behind by interrupted destruction | Candidate only for audited schemas with confirmed exhaustion and no remaining sale/refill/resource value. Native destruction already handles many; expected additional benefit is limited |
| Already destroyed object records and obsolete moving-component keys | Removed by normal rebuilding regardless of CleanSaves; no extra world deletion |
| Scorpions, Nightwalkers, edible/butcherable carcasses, rotten food | Keep in first release |
| Empty water/fuel/oil containers, boxes/crates, tools, weapons, repairable parts | Keep; empty/broken alone does not mean disposable |
| Vehicles, trailers, wrecks with salvage/cargo, merchant stock, living actors, bosses and unique objects | Keep in first release. Patrol-owned cleanup remains under Apocapatrol's rules |
| Unloaded locations and their spawn/visited/resource records | Keep; distance is not a reason to erase history |
| Unknown prefab/mod-owned/legacy ownership | Keep unless an audited owner supplies a cleanup rule |

Protection includes held/pocketed/equipped/stored objects, anything under a retained vehicle/container, player-entered vehicles and their graphs, merchant/quest/unique objects, objects referenced by retained persistent state, and owner-mod vetoes. Add persistent `ever handled`/explicit keep metadata for newly observed objects. Existing legacy objects with unknown handling history are conservatively protected rather than assumed abandoned. A type name containing `_Dead`, an inactive object, health zero, condition zero, and layer alone are never sufficient deletion rules.

Before implementation enables deletion, run the same classifier as a dry-run and expose a short preview, for example:

```
CleanSaves: Off
Would remove: 12 abandoned non-resource corpses, 2 exhausted disposable packs
Kept: 3 food carcasses, 4 reusable containers, 1 vehicle, 6 stored items
Unknown ownership: 8 objects kept
Estimated save reduction: measured during serialization; no invented percentage
```

For the real save, use reversible registry exclusions or an audited serialization filter while gameplay is paused. Do not deactivate or reparent arbitrary candidates: RestartOnEnable FSMs can restart behavior, and OnDisable affects mod registries. If a candidate cannot be excluded without mutating retained state or leaving references dangling, keep it. On failure restore exclusions; on success remove exactly the excluded identities, without taking unrelated nearby loot with them.

## Implementation order and acceptance criteria

1. Add diagnostics, writer provenance and dry-run classification. Confirm changing-position hinge growth in a throwaway native replay, record useful static history, and inspect candidate prefab children. Version-check bindings against installed assets.
2. Introduce the shared coordinator, verified backups/recovery journal and participant barriers. Cover manual saves with naming on/off, autosaves, save points and supported direct save triggers. Complete crash/rollback tests before enabling default rebuilding.
3. Rebuild from current state plus the source history ledger. Test same-character empty/occupied destination equivalence, source slot A to target B, target B belonging to a different character, and two different playthroughs using the same seed. Semantic equivalence excludes intentional slot labels/paths/transaction IDs.
4. Ship CleanSaves off by default only after dry-run decisions pass reference-graph and rollback tests. Measure saved object counts, file size, save duration and load time separately. Do not promise a fixed percentage improvement.

Required tests include: moving a vehicle and saving repeatedly without retaining prior door-position keys; multiple vehicles with identical hinge-local positions; loading after new-slot saves and revisiting an unloaded cleared/drained location; invisible/inactive, attached, container and pocketed items; patrol crew/ownership/cooldown and stale-sidecar fallback; map/mod data; naming cancel with no file mutation; all three registries empty/nonempty; native writer errors and participant errors; missing required records despite a successful commit marker; disk full/locked files and backup failure; timeout, double clicks and slot/scene transitions; delayed writes after rollback; crash after backup, deletion, native flush, verification and each bundle-publication step; failed restore preserving backups; and no early live-object destruction.

A successful save must be behaviorally equivalent between empty and occupied destinations, retain useful history, contain no proven obsolete records, and reload correctly. A failed save must leave a verified previous destination, or clearly enter recovery-required state. CleanSaves must preserve the same result except for its explicitly approved removal set.

## Evidence and references

- Expanded catalog: `V:/Repos/Apocalypter/_tools/ApocasaverSaveAudit/native-save-catalog.json` and generator `catalog_save_actions.py`.
- Audited examples: `V:/Repos/Apocalypter/_tools/ApocasaverSaveAudit/native-object-examples.txt`.
- Library probe and original analysis: `V:/Repos/Apocalypter/_tools/ApocasaverSaveAudit/CacheProbe.cs`, `ES3File.decompiled.cs`, `ES3Writer.decompiled.cs`, `README.md`.
- Native save flow: `V:/Repos/Apocalypter/world-generation-deep-dive/research/persistence-fsm.txt`; native LoadAll/SettingsAction in `research/persistence-code`.
- Current integration: `V:/Repos/Apocalypter/Apocasaver/Plugin.cs`, `SaveNaming.cs`, `PocketInventoryView.cs`; `Apocapocket/Persistence.cs`; `Apocapatrol/Persistence.cs`, `Cleanup.cs`, `Patrol.cs`.
- [Easy Save caching](https://docs.moodkie.com/easy-save-3/es3-guides/caching/) documents RAM caching and scheduled flushing.
- [Easy Save backups](https://docs.moodkie.com/easy-save-3/es3-guides/backups/) documents its backup API; this design requires a separately journaled, verified bundle.
- [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=netframework-4.7.2) documents replacement with backup and same-volume restrictions. It does not make the entire multi-file bundle atomic or supply crash recovery by itself.
