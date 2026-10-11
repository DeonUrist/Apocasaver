using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ES3Internal;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocasaver
{
    internal static class SaveCoordinator
    {
        internal static bool Active { get { return _disk != null; } }
        internal static bool Publishing, RecoveryRequired;
        internal static string Target, PatrolStage;
        internal static int RejectedFrame = -1;
        internal static string LastFile, LastError;
        internal static string LastFailureCause;
        internal static bool LastSuccess;
        internal static bool LastDiskFailure;
        internal static bool NativeFallback;
        private static bool _fallbackStored;
        internal static bool NativeFallbackFinished { get { return NativeFallback && _fallbackStored && Plugin.Current != null && Plugin.Current.NativeSaveReturned; } }
        internal static bool Ready { get { return _initialized && !RecoveryRequired; } }
        private static SaveDiskTransaction _disk;
        private static Dictionary<string, ES3Data> _history = new Dictionary<string, ES3Data>(), _oldCache;
        private static Dictionary<string, RecordKind> _kinds = new Dictionary<string, RecordKind>();
        private static Dictionary<string, RecordKind> _oldKinds;
        private static Dictionary<string, ES3Data> _oldHistory;
        private static Dictionary<string, ES3Data> _stageRecords = new Dictionary<string, ES3Data>();
        private static string _source, _world = Guid.NewGuid().ToString("N"), _kind, _previousPath;
        private static string _loadFile;
        private static float _deadline, _scale;
        private static bool _player, _global, _cars, _items, _storms, _registry, _automatic, _initialized, _rebuildAvailable = true, _conservative;
        private static readonly HashSet<string> _warnings = new HashSet<string>();
        private static string _writeError;
        internal static bool WriteFailed { get { return (Active || NativeFallback) && _writeError != null; } }
        internal static void ReportError(Exception error)
        {
            if (!Active && !NativeFallback) return;
            if (SaveFailurePolicy.IsDiskFailure(error)) ReportDiskError(error);
            else Diagnostic("Native save diagnostic: " + SaveFailurePolicy.Unwrap(error));
        }
        internal static void ReportDiskError(Exception error)
        {
            if ((Active || NativeFallback) && _writeError == null)
            {
                _writeError = error.ToString();
                if (NativeFallback) Warn("Saving failed to disk: " + error.Message);
            }
        }
        internal static void Diagnostic(string message)
        {
            if (Active) _conservative = true;
            if (_warnings.Add(message)) Plugin.Log.LogWarning(message);
        }
        internal static bool Optional(Action action, string name)
        {
            try { action(); return true; }
            catch (Exception e)
            {
                if (SaveFailurePolicy.IsDiskFailure(e)) throw;
                Diagnostic(name + ": " + SaveFailurePolicy.Unwrap(e)); return false;
            }
        }
        private static readonly HashSet<PlayMakerFSM> _expected = new HashSet<PlayMakerFSM>(), _written = new HashSet<PlayMakerFSM>();
        private static readonly List<Tuple<FsmString, string>> _paths = new List<Tuple<FsmString, string>>();
        private static CleanWorld.Exclusion _exclusion;
        private static HashSet<GameObject> _retainedRoots;
        private static string _saveRoot;
        private static string SaveRoot { get { return _saveRoot ?? (_saveRoot = Path.GetDirectoryName(new ES3Settings("SaveGame1.es3") { location = ES3.Location.File }.FullPath)); } }
        private static string BackupRoot { get { return Path.Combine(SaveRoot, "Apocasaver", "Backups"); } }
        internal static string RawString(IDictionary<string, ES3Data> data, string key)
        {
            ES3Data value; if (!data.TryGetValue(key, out value)) return null;
            return ES3.Deserialize<string>(value.bytes, new ES3Settings() { location = ES3.Location.Cache });
        }
        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                if (!SaveRecords.Ready) throw new MissingFieldException("Installed Easy Save cache layout is unsupported");
                try { WorldHistory.Install(); }
                catch (Exception e) { Diagnostic("Terrain history hooks unavailable; native saving remains available: " + e); _rebuildAvailable = false; }
                if (Directory.Exists(BackupRoot)) foreach (string journal in Directory.GetFiles(BackupRoot, "undo.bin", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(journal);
                    if (File.Exists(Path.Combine(dir, "committed")) || File.Exists(Path.Combine(dir, "recovered")) || File.Exists(Path.Combine(dir, "superseded"))) continue;
                    try
                    {
                        var tx = SaveDiskTransaction.Read(journal, AllowedRecoveryPath);
                        tx.Rollback(RestoreLabel); ES3File.cachedFiles.Remove(tx.Slot);
                        Plugin.Log.LogWarning("Recovered interrupted save " + tx.Slot + " from " + dir);
                        StatusLabel.Show("Interrupted save recovered from backup", 12, true);
                    }
                    catch (Exception e) { RecoveryRequired = true; _rebuildAvailable = false; Diagnostic("Backup recovery needs attention at " + dir + "; native saving remains available: " + e); }
                }
            }
            catch (Exception e)
            {
                _rebuildAvailable = false;
                Diagnostic("Save rebuilding unavailable; native saving remains available: " + e);
            }
        }
        private static bool AllowedRecoveryPath(string path)
        {
            string saveRoot = Path.GetFullPath(SaveRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string patrolRoot = Path.GetFullPath(Path.Combine(PathsConfig(), "Apocapatrol", "Saves")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(saveRoot, StringComparison.OrdinalIgnoreCase) && AutosaveSession.IsSlotFile(Path.GetFileName(path)) && Path.GetDirectoryName(path) == saveRoot.TrimEnd(Path.DirectorySeparatorChar) ||
                path.StartsWith(patrolRoot, StringComparison.OrdinalIgnoreCase) && path.EndsWith(".es3.bin", StringComparison.OrdinalIgnoreCase);
        }
        private static string PathsConfig() { return BepInEx.Paths.ConfigPath; }
        internal static bool InterceptClick(Fsm fsm)
        {
            if (fsm == null || fsm.GameObject == null || fsm.Name != "Continue") return true;
            string name = fsm.GameObject.name, file;
            if (name.StartsWith("save_game_", StringComparison.Ordinal)) file = "SaveGame" + name.Substring(10) + ".es3";
            else if (name == "Yes_Save") file = Runner.CurrentSaveFile();
            else return true;
            if (Active || !Begin(file, Plugin.Current != null && Plugin.Current.IsAutosaving))
            { RejectedFrame = Time.frameCount; return false; }
            return true;
        }
        internal static bool Begin(string file, bool automatic)
        {
            Initialize();
            if (Active) return false;
            LastFile = file; LastSuccess = false; LastDiskFailure = false; LastError = null; NativeFallback = false; _fallbackStored = false;
            _writeError = null;
            _warnings.Clear(); _conservative = false;
            if (!_rebuildAvailable || !Plugin.AutosaveHooksReady || !AutosaveSession.IsSlotFile(file))
                return UseNative("Rebuild integration unavailable");
            try
            {
                if (!SaveParticipants.Ready) return UseNative("Companion mods are still initializing");
                if (PocketCompatibility.Busy) return UseNative("Pocket inventory is busy; its native save hooks will handle saving");
                if (!Optional(SaveParticipants.Drain, "Companion pending data")) return UseNative("Companion integration unavailable");
                if (_source == null && Plugin.Current != null && Plugin.Current.SessionSlot != null) LoadSource(Plugin.Current.SessionSlot);
                RefreshSourceRecords();
                _automatic = automatic; _kind = automatic ? SaveNaming.NameForAutosave(file) ?? "Autosave" : SaveNaming.PendingName ?? "Manual";
                _oldHistory = new Dictionary<string, ES3Data>(_history); _oldKinds = new Dictionary<string, RecordKind>(_kinds);
                _oldCache = SaveRecords.Snapshot(file);
                var disk = new SaveDiskTransaction(BackupRoot, file);
                var settings = new ES3Settings(file) { location = ES3.Location.File };
                disk.Track(settings.FullPath);
                disk.LabelExisted = ES3.KeyExists(LabelKey(file), new ES3Settings("SaveSettings.es3") { location = ES3.Location.File });
                disk.Label = disk.LabelExisted ? ES3.Load<string>(LabelKey(file), new ES3Settings("SaveSettings.es3") { location = ES3.Location.File }) : "";
                string patrolStage = SaveParticipants.PatrolLoaded ? disk.Track(SaveParticipants.PatrolPathFor(file), true) : null;
                disk.Prepare(); // no destination mutation before this durable journal
                _disk = disk; Target = file; PatrolStage = patrolStage; LastFile = file; LastSuccess = false; LastError = null;
                _previousPath = ES3Settings.defaultSettings.path; _scale = Time.timeScale;
                _deadline = Time.realtimeSinceStartup + 60; _player = _global = _cars = _items = _storms = _registry = false;
                _writeError = null;
                _stageRecords.Clear();
                _expected.Clear(); _written.Clear(); _paths.Clear();
                var roots = CleanWorld.Objects(); SaveHooks.RefreshRoots(roots); _exclusion = new CleanWorld.Exclusion();
                Optional(() => { _exclusion = CleanWorld.Plan(); _exclusion.Apply(); }, "CleanSaves planning");
                _retainedRoots = CleanWorld.Objects();
                foreach (var root in roots.Where(g => !_exclusion.Objects.Contains(g))) foreach (var f in root.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f.enabled && f.Fsm.Initialized && f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "SaveGame") &&
                        f.Fsm.States.Any(s => s.Actions.Any(a => a.Enabled && (a is ES3PlayMaker.Save || a is ES3PlayMaker.SaveAll)))) _expected.Add(f);
                disk.ResetPrimary();
                var seed = new Dictionary<string, ES3Data>();
                var liveNames = CleanWorld.AllNames();
                foreach (var item in _history)
                {
                    RecordKind kind; if (!_kinds.TryGetValue(item.Key, out kind)) kind = SaveRecords.LegacyKind(item.Key, item.Value);
                    if (item.Key == SaveRecords.IndexKey || item.Key == SaveRecords.WorldKey || item.Key == CleanWorld.Key || item.Key == "apocasaver.commit") continue;
                    // Keep prior session/live-object values until the game replaces them.
                    // Only discard object records whose owner is known to be absent.
                    if (kind == RecordKind.Dynamic && !liveNames.Contains(item.Key)) continue;
                    if (item.Key.StartsWith("apocasaver.pose."))
                    {
                        string name = item.Key.Substring("apocasaver.pose.".Length); ES3Data owner;
                        bool dead = _history.TryGetValue(name, out owner) && SaveRecords.LegacyKind(name, owner) == RecordKind.Dynamic && !liveNames.Contains(name);
                        if (!dead && !_exclusion.Objects.Any(g => g.name == name)) seed[item.Key] = item.Value;
                    }
                    else if (!item.Key.StartsWith("apocasaver.pose.")) seed[item.Key] = item.Value;
                }
                SaveRecords.ReplaceCache(file, seed);
                ES3Settings.defaultSettings.path = file;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>()) if (f != null && f.gameObject.scene.IsValid() && (f.FsmName == "SaveLoadGame" || f.FsmName == "Save_NewGO_ArrayList"))
                { var path = f.FsmVariables.GetFsmString("SaveFile"); if (path != null) { _paths.Add(Tuple.Create(path, path.Value)); path.Value = file; } }
                Plugin.Log.LogInfo("Save rebuild started for " + file + "; backup=" + disk.DirectoryPath + "; preserved=" + seed.Count);
                return true;
            }
            catch (Exception e)
            {
                // Before reset, feature setup failures must not swallow the native save click.
                if (!Active) return UseNative("Rebuild setup failed: " + e);
                if (SaveFailurePolicy.IsDiskFailure(e)) { Fail(e.ToString(), true, true); return false; }
                RestoreForNative(e); return true;
            }
        }
        private static bool UseNative(string reason)
        {
            NativeFallback = true; LastError = null;
            // Preserve source-playthrough data even when optional adapters cannot rebuild.
            // This changes only the native cache; the game still performs its normal disk write.
            if (SaveRecords.Ready && _source != null && AutosaveSession.IsSlotFile(LastFile))
            {
                try { RefreshSourceRecords(); SaveRecords.ReplaceCache(LastFile, _history); }
                catch (Exception e) { Diagnostic("Source data transfer unavailable; retaining native cache: " + e); }
            }
            Diagnostic(reason + "; using the game's normal save without cleanup"); return true;
        }
        private static void RefreshSourceRecords()
        {
            if (_source == null) return;
            foreach (var item in SaveRecords.Snapshot(_source))
            {
                RecordKind kind;
                // Captured unloaded-location state can be newer than the last on-disk save.
                if (_kinds.TryGetValue(item.Key, out kind) && kind == RecordKind.History && _history.ContainsKey(item.Key)) continue;
                _history[item.Key] = item.Value;
            }
        }
        private static void RestoreForNative(Exception error)
        {
            // Restore any reversible work, then let the original click/event continue.
            Fail("Rebuild feature unavailable: " + error, true);
            if (!RecoveryRequired) UseNative("Rebuild feature unavailable");
        }
        internal static string Redirect(ES3Settings settings)
        {
            if (!Active || Publishing || settings.location != ES3.Location.File) return null;
            if (settings.path == Target || settings.path == _disk.Files[0].Target) return _disk.Files[0].Stage;
            return null;
        }
        internal static bool Excluded(GameObject go)
        { return Active && _exclusion != null && go != null && _exclusion.Objects.Contains(go); }
        internal static void FileWritten(ES3Settings settings)
        {
            string file = Active ? Target : _source;
            if (file == null || Publishing || settings.location != ES3.Location.File || settings.path != file) return;
            try
            {
                var disk = new ES3File(settings, true); var written = SaveRecords.Data(disk);
                ES3File cached; if (!ES3File.cachedFiles.TryGetValue(file, out cached)) return;
                var cache = SaveRecords.Data(cached);
                foreach (var item in written)
                {
                    ES3Data previous;
                    if (!_stageRecords.TryGetValue(item.Key, out previous) || !previous.bytes.SequenceEqual(item.Value.bytes))
                    { cache[item.Key] = item.Value; if (!Active) _history[item.Key] = item.Value; }
                }
                foreach (string removed in _stageRecords.Keys.Where(k => !written.ContainsKey(k)).ToArray()) { cache.Remove(removed); if (!Active) _history.Remove(removed); }
                _stageRecords = new Dictionary<string, ES3Data>(written); SaveRecords.MarkDirty(file);
            }
            catch (Exception e) { ReportError(e); }
        }
        internal static void Written(ES3PlayMaker.SettingsAction action)
        {
            string key = SaveHooks.Key(action);
            if (key == null) return;
            string path = action.GetSettings().path;
            var go = action.Fsm.GameObject;
            RecordKind kind = SaveHooks.Kind(go, key);
            if (WorldHistory.Capturing) { WorldHistory.Record(path, key); return; }
            if (!Active || path != Target || Excluded(go)) return;
            _written.Add(action.Fsm.FsmComponent); _kinds[key] = kind;
            if (key == "Player" && go.name == "Player") _player = true;
            if (key == "global" && action.Fsm.Name == "SaveLoadGame") _global = true;
            if (key == "newGOArray_Cars") _cars = true;
            if (key == "newGOArray_Items") _items = true;
            if (key == "newGOArray_Sandstorms") _storms = true;
        }
        internal static void Stored(ES3PlayMaker.SettingsAction action)
        { if (action.Fsm.Name == "Save_NewGO_ArrayList") { if (Active) _registry = true; if (NativeFallback) _fallbackStored = true; } }
        internal static void OnState(Fsm fsm, string state)
        {
            if (fsm == null || fsm.Name != "SaveLoadGame") return;
            if (state == "LoadGame")
            { if (Active) Fail("Load interrupted saving", false); _source = null; _loadFile = ES3Settings.defaultSettings.path; SaveHooks.RefreshRoots(CleanWorld.Objects()); }
            else if (state == "Start" || state == "setSeed")
            { if (Active) Fail("New game interrupted saving", false); _source = null; _loadFile = null; _history.Clear(); _kinds.Clear(); _world = Guid.NewGuid().ToString("N"); CleanWorld.Load(null); }
            else if (state == "isPlay" && _loadFile != null)
            {
                try { LoadSource(_loadFile); }
                catch (Exception e) { _rebuildAvailable = false; Diagnostic("Could not read loaded save history; native saving remains available: " + e); }
                finally { _loadFile = null; }
            }
        }
        private static void LoadSource(string file)
        {
            if (!AutosaveSession.IsSlotFile(file)) return;
            _history = SaveRecords.Snapshot(file); _kinds = new Dictionary<string, RecordKind>();
            try { _kinds = SaveRecords.Decode(RawString(_history, SaveRecords.IndexKey)); }
            catch (Exception e) { Diagnostic("Record index unavailable; retaining records using legacy classification: " + e.Message); }
            try { _world = RawString(_history, SaveRecords.WorldKey) ?? Guid.NewGuid().ToString("N"); }
            catch (Exception e) { Diagnostic("World identifier unavailable: " + e.Message); _world = Guid.NewGuid().ToString("N"); }
            try { CleanWorld.Load(RawString(_history, CleanWorld.Key)); }
            catch (Exception e) { CleanWorld.Load(null); Diagnostic("Cleanup metadata unavailable; unknown objects will be retained: " + e.Message); }
            _source = file;
        }
        internal static void CaptureHistory(string key, ES3Data value)
        {
            _history[key] = value; _kinds[key] = RecordKind.History;
            if (Active && _oldHistory != null)
            {
                _oldHistory[key] = value; _oldKinds[key] = RecordKind.History;
                ES3File cache; if (ES3File.cachedFiles.TryGetValue(Target, out cache)) SaveRecords.Data(cache)[key] = value;
            }
        }
        internal static IDictionary<string, ES3Data> History { get { return _history; } }
        internal static bool HasSession { get { return _initialized && (_source != null || _loadFile == null); } }
        internal static void FlushAvailable() { if (Active) { _deadline = -1f; Tick(); } }
        internal static void Tick()
        {
            Initialize();
            if (!Active)
            {
                if (NativeFallbackFinished && !WriteFailed)
                {
                    try { CompleteNativeFallback(); }
                    catch (Exception e) { _fallbackStored = false; Warn("Saving failed to disk: " + e); }
                }
                return;
            }
            try
            {
                if (_writeError != null) throw new IOException(_writeError);
                bool timeout = Time.realtimeSinceStartup >= _deadline;
                if (!timeout && (!_registry || Plugin.Current == null || !Plugin.Current.NativeSaveReturned)) return;
                if (timeout) Diagnostic("Native save completion not confirmed after 60 seconds; writing available data and retaining prior records");
                if (!_player || !_global || !_cars || !_items || !_storms || !_registry)
                    Diagnostic("Unconfirmed native writes: player=" + _player + ", global=" + _global + ", cars=" + _cars + ", items=" + _items + ", storms=" + _storms + ", registry=" + _registry);
                foreach (var f in _expected.Where(f => f != null && !_written.Contains(f))) Diagnostic("Unconfirmed object write; retaining prior data: " + f.gameObject.name + "/" + f.FsmName);
                Commit();
            }
            catch (Exception e)
            {
                if (SaveFailurePolicy.IsDiskFailure(e)) { Fail(e.ToString(), true, true); return; }
                Diagnostic("Rebuild feature unavailable during commit; writing available data: " + e);
                try
                {
                    CancelCleanup(); SaveRecords.MarkDirty(Target);
                    ES3.StoreCachedFile(new ES3Settings(Target) { location = ES3.Location.File });
                    if (_writeError != null) throw new IOException(_writeError);
                    Publishing = true; _disk.Publish(); _disk.Complete();
                    CompleteSuccess(new Dictionary<string, RecordKind>(_kinds));
                }
                catch (Exception writeError) { Fail(writeError.ToString(), true, true); }
            }
        }
        internal static void CompleteNativeFallback()
        {
            if (WriteFailed) throw new IOException(_writeError);
            ES3.StoreCachedFile(new ES3Settings(LastFile) { location = ES3.Location.File });
            if (!File.Exists(new ES3Settings(LastFile) { location = ES3.Location.File }.FullPath)) throw new IOException("Native save did not produce a disk file");
            // A successfully written newer native save supersedes any unresolved undo journal
            // for that slot. Keep its backups, but never replay it over the newer save on restart.
            string root = Path.Combine(BackupRoot, LastFile);
            if (Directory.Exists(root)) foreach (string journal in Directory.GetFiles(root, "undo.bin", SearchOption.AllDirectories))
            {
                string dir = Path.GetDirectoryName(journal);
                if (File.Exists(Path.Combine(dir, "committed")) || File.Exists(Path.Combine(dir, "recovered")) || File.Exists(Path.Combine(dir, "superseded"))) continue;
                using (var stream = new FileStream(Path.Combine(dir, "superseded"), FileMode.Create, FileAccess.Write, FileShare.None)) { stream.WriteByte(1); stream.Flush(true); }
            }
            LastSuccess = true; LastError = null; NativeFallback = false;
            _source = LastFile;
            try { RefreshSourceRecords(); } catch (Exception e) { Diagnostic("Native save history refresh: " + e); }
            if (Plugin.Current != null) { try { Plugin.Current.SaveCommitted(LastFile); } catch (Exception e) { Diagnostic("Native save bookkeeping: " + e); } }
        }
        private static void Commit()
        {
            var cache = new ES3Settings(Target) { location = ES3.Location.Cache };
            Optional(() =>
            {
                var roots = CleanWorld.Objects();
                if (!_retainedRoots.SetEquals(roots))
                {
                    Diagnostic("Registered world objects changed during saving; refreshing native object data and registry arrays");
                    SaveHooks.RefreshRoots(roots);
                    foreach (var root in roots.Except(_retainedRoots)) foreach (var f in root.GetComponentsInChildren<PlayMakerFSM>(true))
                        if (f.enabled && f.Fsm.Initialized && f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "SaveGame")) Optional(() => f.SendEvent("SaveGame"), "Newly registered object");
                    RewriteRegistries();
                }
                _exclusion.Validate();
            }, "Cleanup validation");
            if (_conservative) CancelCleanup();
            // Native flush has produced a readable staged file for participant disk verification.
            if (!Optional(() => { if (!SaveParticipants.CompleteNative(Target, Plugin.Current == null ? 0 : Plugin.Current.WorldSeed)) throw new InvalidOperationException("Companion snapshot not confirmed"); }, "Companion snapshot"))
            {
                CancelCleanup();
                // Each unavailable participant retains its own prior data. A healthy
                // participant still gets a fresh snapshot, including restored cleanup objects.
                Optional(() => SaveParticipants.CompleteNative(Target, Plugin.Current == null ? 0 : Plugin.Current.WorldSeed), "Retained companion snapshot");
            }
            Optional(() => { if (HandPose.PendingCount > 0) HandPose.Flush(Target); if (HandPose.PendingCount > 0) throw new InvalidOperationException("Hand-pose metadata remains pending"); }, "Hand-pose metadata");
            if (_writeError != null) throw new IOException(_writeError);
            foreach (string key in new[] { "Player", "global", "newGOArray_Cars", "newGOArray_Items", "newGOArray_Sandstorms" })
                if (!ES3.KeyExists(key, cache) || ES3File.cachedFiles[Target].GetKeyType(key) != typeof(ES3PlayMaker.PMDataWrapper)) Diagnostic("Unconfirmed record layout: " + key);
            if (_conservative && _exclusion.Objects.Count > 0)
            {
                CancelCleanup();
                Optional(() => SaveParticipants.CompleteNative(Target, Plugin.Current == null ? 0 : Plugin.Current.WorldSeed), "Retained companion snapshot");
            }
            var names = CleanWorld.AllNames();
            Optional(() => ES3.Save(CleanWorld.Key, CleanWorld.Encode(names), cache), "Cleanup metadata");
            Optional(() => ES3.Save(SaveRecords.WorldKey, _world, cache), "World metadata");
            var data = SaveRecords.Snapshot(Target);
            var index = new Dictionary<string, RecordKind>();
            foreach (var item in data) { RecordKind kind; index[item.Key] = _kinds.TryGetValue(item.Key, out kind) ? kind : SaveRecords.LegacyKind(item.Key, item.Value); }
            Optional(() => ES3.Save(SaveRecords.IndexKey, SaveRecords.Encode(index), cache), "Record index");
            if (_conservative && _exclusion.Objects.Count > 0)
            {
                CancelCleanup();
                Optional(() => SaveParticipants.CompleteNative(Target, Plugin.Current == null ? 0 : Plugin.Current.WorldSeed), "Retained companion snapshot");
            }
            if (_conservative)
            {
                var current = SaveRecords.Data(ES3File.cachedFiles[Target]);
                foreach (var item in _history)
                    if (!current.ContainsKey(item.Key) && item.Key.StartsWith("apocasaver.object.")) current[item.Key] = item.Value;
            }
            string marker = Guid.NewGuid().ToString("N"); ES3.Save("apocasaver.commit", marker, cache);
            SaveRecords.MarkDirty(Target); ES3.StoreCachedFile(new ES3Settings(Target) { location = ES3.Location.File });
            // Publication verifies byte hashes. Semantic checks cannot veto a successful write.
            Publishing = true;
            _disk.Publish();
            if (Plugin.Current != null) Optional(() => Plugin.Current.StampCommitted(Target, _kind), "Slot label");
            _disk.Complete();
            CompleteSuccess(index);
        }
        private static void CompleteSuccess(Dictionary<string, RecordKind> index)
        {
            try { _history = SaveRecords.Snapshot(Target); _kinds = index; _source = Target; }
            catch (Exception e) { _rebuildAvailable = false; Diagnostic("Post-save history unavailable; future saves use native saving: " + e); }
            LastSuccess = true; LastError = null; string file = Target; var exclusion = _exclusion;
            Time.timeScale = _scale; _disk = null; Publishing = false; Target = PatrolStage = null; _paths.Clear();
            if (exclusion != null) { try { exclusion.Destroy(); } catch (Exception e) { Diagnostic("Cleanup destruction: " + e); } }
            if (Plugin.Current != null) { try { Plugin.Current.SaveCommitted(file); } catch (Exception e) { Diagnostic("Post-save bookkeeping: " + e); } }
            Plugin.Log.LogInfo("Save written to disk: " + file + (_warnings.Count == 0 ? "" : "; diagnostics=" + _warnings.Count));
            if (!_automatic) StatusLabel.Show(_warnings.Count == 0 ? "Game saved" : "Save written with warnings; see log", 6, false);
            PruneBackups(file);
        }
        private static void CancelCleanup()
        {
            if (_exclusion == null || _exclusion.Objects.Count == 0) return;
            var restored = _exclusion.Objects.ToArray(); _exclusion.Restore(); _exclusion = new CleanWorld.Exclusion();
            SaveHooks.RefreshRoots(CleanWorld.Objects());
            // Put prior live-object data back before native callbacks refresh restored objects.
            var data = SaveRecords.Data(ES3File.cachedFiles[Target]);
            foreach (var item in _history) if (!data.ContainsKey(item.Key)) data[item.Key] = item.Value;
            foreach (var go in restored.Where(g => g != null)) foreach (var f in go.GetComponents<PlayMakerFSM>())
                if (f.enabled && f.Fsm.Initialized && f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "SaveGame")) Optional(() => f.SendEvent("SaveGame"), "Retained cleanup object");
            RewriteRegistries();
            Diagnostic("CleanSaves skipped for this save; all candidates retained");
        }
        private static void RewriteRegistries()
        {
            var go = GameObject.Find("NewGO_ArrayList");
            var fsm = go == null ? null : go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Save_NewGO_ArrayList");
            if (fsm == null) return;
            foreach (var list in CleanWorld.Registries())
            {
                string key = list.referenceName == "ArrayList_Cars" ? "newGOArray_Cars" : list.referenceName == "ArrayList_Items" ? "newGOArray_Items" : "newGOArray_Sandstorms";
                var action = new ES3PlayMaker.Save { key = key, errorMessage = "", value = new FsmVar(new FsmArray { Name = key, ElementType = VariableType.GameObject, Values = list.arrayList.Cast<object>().ToArray() }) };
                action.Init(fsm.Fsm.States[0]); action.Enter();
            }
        }
        internal static void Fail(string error, bool resume, bool diskFailure = false)
        {
            if (!Active) return;
            var tx = _disk; LastSuccess = false; LastDiskFailure = diskFailure; LastError = error;
            LastFailureCause = error;
            try
            {
                if (Plugin.Current != null) Plugin.Current.AbortNativeSave();
                try { SaveParticipants.Cancel(); } catch (Exception e) { Diagnostic("Companion cancellation: " + e); }
                Publishing = true;
                HeldItemKeeper.CancelMetadataSave();
                tx.Rollback((file, existed, label) => Optional(() => RestoreLabel(file, existed, label), "Restoring slot label"));
                if (tx.Files[0].Existed) SaveRecords.ReplaceCache(Target, _oldCache, false); else ES3File.cachedFiles.Remove(Target);
                _history = _oldHistory; _kinds = _oldKinds;
                if (_exclusion != null) _exclusion.Restore();
                ES3Settings.defaultSettings.path = _previousPath;
                foreach (var path in _paths) path.Item1.Value = path.Item2;
                if (diskFailure)
                {
                    Plugin.Log.LogError("Save disk write failed; previous destination restored: " + error);
                    Warn(tx.Files[0].Existed ? "Saving failed to disk. The previous save was restored." : "Saving failed to disk. No save was created.");
                }
                else Diagnostic("Save rebuild canceled; previous destination restored: " + error);
            }
            catch (Exception e)
            { RecoveryRequired = true; _rebuildAvailable = false; LastDiskFailure = SaveFailurePolicy.IsDiskFailure(e); Plugin.Log.LogError("Recovery required. Backup: " + tx.DirectoryPath + "\n" + e); Warn("Saving failed. Recovery needed; backup: " + tx.DirectoryPath); }
            finally { if (resume) Time.timeScale = _scale; _disk = null; Publishing = false; Target = PatrolStage = null; _paths.Clear(); }
        }
        internal static bool DeferredLabel(ES3PlayMaker.SettingsAction action)
        {
            var save = action as ES3PlayMaker.Save;
            return Active && !Publishing && save != null && action.GetSettings().path == "SaveSettings.es3" && save.key.Value == LabelKey(Target);
        }
        private static string LabelKey(string file) { return "saveTime" + file.Substring(8, file.Length - 12); }
        private static void RestoreLabel(string file, bool existed, string label)
        {
            var settings = new ES3Settings("SaveSettings.es3") { location = ES3.Location.File };
            if (existed) ES3.Save(LabelKey(file), label, settings); else ES3.DeleteKey(LabelKey(file), settings);
            var cache = new ES3Settings("SaveSettings.es3") { location = ES3.Location.Cache };
            if (existed) ES3.Save(LabelKey(file), label, cache); else ES3.DeleteKey(LabelKey(file), cache);
            if (Plugin.Current != null) Plugin.Current.RestoreSlotText(file, label ?? "");
        }
        private static void PruneBackups(string file)
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(BackupRoot, file));
                var completed = Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "committed"))).OrderByDescending(d => d).Skip(3).ToArray();
                foreach (var dir in completed) if (Path.GetFullPath(dir).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(dir, true);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Backup retention: " + e.Message); }
        }
        private static void Warn(string text) { LastError = text; Plugin.Log.LogWarning(text); StatusLabel.Show(text, 15, true); }
    }
}
