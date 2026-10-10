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
        private static SaveDiskTransaction _disk;
        private static Dictionary<string, ES3Data> _history = new Dictionary<string, ES3Data>(), _oldCache;
        private static Dictionary<string, RecordKind> _kinds = new Dictionary<string, RecordKind>();
        private static Dictionary<string, RecordKind> _oldKinds;
        private static Dictionary<string, ES3Data> _oldHistory;
        private static Dictionary<string, ES3Data> _stageRecords = new Dictionary<string, ES3Data>();
        private static string _source, _world = Guid.NewGuid().ToString("N"), _kind, _previousPath;
        private static string _loadFile;
        private static float _deadline, _scale;
        private static bool _player, _global, _cars, _items, _storms, _registry, _automatic, _initialized;
        private static string _writeError;
        internal static bool WriteFailed { get { return Active && _writeError != null; } }
        internal static void ReportError(Exception error) { if (Active && _writeError == null) _writeError = error.ToString(); }
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
                SaveParticipants.Resolve();
                Plugin.Log.LogDebug("Save participants resolved; installing terrain history hooks");
                WorldHistory.Install();
                Plugin.Log.LogDebug("Terrain history hooks installed; checking recovery journals");
                if (Directory.Exists(BackupRoot)) foreach (string journal in Directory.GetFiles(BackupRoot, "undo.bin", SearchOption.AllDirectories))
                {
                    string dir = Path.GetDirectoryName(journal);
                    if (File.Exists(Path.Combine(dir, "committed")) || File.Exists(Path.Combine(dir, "recovered"))) continue;
                    var tx = SaveDiskTransaction.Read(journal, AllowedRecoveryPath);
                    tx.Rollback(RestoreLabel); ES3File.cachedFiles.Remove(tx.Slot);
                    Plugin.Log.LogWarning("Recovered interrupted save " + tx.Slot + " from " + dir);
                    StatusLabel.Show("Interrupted save recovered from backup", 12, true);
                }
            }
            catch (Exception e) { RecoveryRequired = true; Plugin.Log.LogError("Save recovery/bindings failed: " + e); StatusLabel.Show("Saving unavailable: recovery needs attention", 30, true); }
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
            if (Active || RecoveryRequired || !Begin(file, Plugin.Current != null && Plugin.Current.IsAutosaving))
            { RejectedFrame = Time.frameCount; return false; }
            return true;
        }
        internal static bool Begin(string file, bool automatic)
        {
            Initialize();
            if (!Active) { LastFile = file; LastSuccess = false; LastError = null; }
            if (Active || RecoveryRequired || !Plugin.AutosaveHooksReady || !AutosaveSession.IsSlotFile(file))
            { Warn("Saving could not start: save bindings, slot or recovery are unavailable"); return false; }
            try
            {
                if (PocketCompatibility.Busy) throw new InvalidOperationException("Pocket inventory is busy; try saving again when it finishes");
                SaveParticipants.Drain();
                if (_source == null && Plugin.Current != null && Plugin.Current.SessionSlot != null) LoadSource(Plugin.Current.SessionSlot);
                _automatic = automatic; _kind = automatic ? SaveNaming.NameForAutosave(file) ?? "Autosave" : SaveNaming.PendingName ?? "Manual";
                _oldHistory = new Dictionary<string, ES3Data>(_history); _oldKinds = new Dictionary<string, RecordKind>(_kinds);
                _oldCache = SaveRecords.Snapshot(file);
                var disk = new SaveDiskTransaction(BackupRoot, file);
                var settings = new ES3Settings(file) { location = ES3.Location.File };
                disk.Track(settings.FullPath);
                disk.LabelExisted = ES3.KeyExists(LabelKey(file), new ES3Settings("SaveSettings.es3") { location = ES3.Location.File });
                disk.Label = disk.LabelExisted ? ES3.Load<string>(LabelKey(file), new ES3Settings("SaveSettings.es3") { location = ES3.Location.File }) : "";
                string patrolStage = SaveParticipants.PatrolLoaded ? disk.Track(SaveParticipants.PatrolPathFor(file)) : null;
                disk.Prepare(); // no destination mutation before this durable journal
                _disk = disk; Target = file; PatrolStage = patrolStage; LastFile = file; LastSuccess = false; LastError = null;
                _previousPath = ES3Settings.defaultSettings.path; _scale = Time.timeScale; Time.timeScale = 0;
                _deadline = Time.realtimeSinceStartup + 60; _player = _global = _cars = _items = _storms = _registry = false;
                _writeError = null;
                _stageRecords.Clear();
                _expected.Clear(); _written.Clear(); _paths.Clear();
                var roots = CleanWorld.Objects(); SaveHooks.RefreshRoots(roots); _exclusion = CleanWorld.Plan(); _exclusion.Apply();
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
                    if (kind == RecordKind.Session || kind == RecordKind.Dynamic || item.Key == SaveRecords.IndexKey || item.Key == SaveRecords.WorldKey || item.Key == CleanWorld.Key) continue;
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
            catch (Exception e) { if (Active) Fail(e.Message, true); else Warn(e.Message); return false; }
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
            if (!Active || Publishing || settings.location != ES3.Location.File || settings.path != Target) return;
            try
            {
                var disk = new ES3File(settings, true); var written = SaveRecords.Data(disk);
                var cache = SaveRecords.Data(ES3File.cachedFiles[Target]);
                foreach (var item in written)
                {
                    ES3Data previous;
                    if (!_stageRecords.TryGetValue(item.Key, out previous) || !previous.bytes.SequenceEqual(item.Value.bytes)) cache[item.Key] = item.Value;
                }
                foreach (string removed in _stageRecords.Keys.Where(k => !written.ContainsKey(k)).ToArray()) cache.Remove(removed);
                _stageRecords = new Dictionary<string, ES3Data>(written); SaveRecords.MarkDirty(Target);
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
        { if (Active && action.Fsm.Name == "Save_NewGO_ArrayList") _registry = true; }
        internal static void OnState(Fsm fsm, string state)
        {
            if (fsm == null || fsm.Name != "SaveLoadGame") return;
            if (state == "LoadGame")
            { if (Active) Fail("Load interrupted saving", false); _source = null; _loadFile = ES3Settings.defaultSettings.path; SaveHooks.RefreshRoots(CleanWorld.Objects()); }
            else if (state == "Start" || state == "setSeed")
            { if (Active) Fail("New game interrupted saving", false); _source = null; _loadFile = null; _history.Clear(); _kinds.Clear(); _world = Guid.NewGuid().ToString("N"); CleanWorld.Load(null); }
            else if (state == "isPlay" && _loadFile != null)
            { try { LoadSource(_loadFile); _loadFile = null; } catch (Exception e) { RecoveryRequired = true; Warn("Could not read loaded save history: " + e.Message); } }
        }
        private static void LoadSource(string file)
        {
            if (!AutosaveSession.IsSlotFile(file)) return;
            _history = SaveRecords.Snapshot(file); _kinds = SaveRecords.Decode(RawString(_history, SaveRecords.IndexKey));
            _world = RawString(_history, SaveRecords.WorldKey) ?? Guid.NewGuid().ToString("N");
            CleanWorld.Load(RawString(_history, CleanWorld.Key)); _source = file;
        }
        internal static void CaptureHistory(string key, ES3Data value)
        { _history[key] = value; _kinds[key] = RecordKind.History; if (Active && _oldHistory != null) { _oldHistory[key] = value; _oldKinds[key] = RecordKind.History; } }
        internal static IDictionary<string, ES3Data> History { get { return _history; } }
        internal static bool HasSession { get { return _initialized && (_source != null || _loadFile == null); } }
        internal static void Tick()
        {
            Initialize(); if (!Active) return;
            try
            {
                if (Time.realtimeSinceStartup >= _deadline) throw new TimeoutException("The game's save did not finish within 60 seconds");
                if (_writeError != null) throw new IOException(_writeError);
                if (Plugin.Current == null || !Plugin.Current.NativeSaveFinished || !_player || !_global || !_cars || !_items || !_storms || !_registry) return;
                if (_expected.Any(f => f != null && !_written.Contains(f))) throw new IOException("A retained object's native state was not saved: " + _expected.First(f => f != null && !_written.Contains(f)).gameObject.name);
                Commit();
            }
            catch (Exception e) { Fail(e.Message, true); }
        }
        private static void Commit()
        {
            var cache = new ES3Settings(Target) { location = ES3.Location.Cache };
            if (!_retainedRoots.SetEquals(CleanWorld.Objects())) throw new IOException("Registered world objects changed during saving; try again after the operation finishes");
            _exclusion.Validate();
            // Native flush has produced a readable staged file for participant disk verification.
            SaveParticipants.CompleteNative(Target, Plugin.Current.WorldSeed);
            if (HandPose.PendingCount > 0) HandPose.Flush(Target);
            if (HandPose.PendingCount > 0) throw new IOException("Hand-pose metadata could not be saved");
            if (_writeError != null) throw new IOException(_writeError);
            foreach (string key in new[] { "Player", "global", "newGOArray_Cars", "newGOArray_Items", "newGOArray_Sandstorms" })
                if (!ES3.KeyExists(key, cache) || ES3File.cachedFiles[Target].GetKeyType(key) != typeof(ES3PlayMaker.PMDataWrapper)) throw new IOException("Missing or invalid required record: " + key);
            var names = CleanWorld.AllNames();
            ES3.Save(CleanWorld.Key, CleanWorld.Encode(names), cache);
            ES3.Save(SaveRecords.WorldKey, _world, cache);
            var data = SaveRecords.Snapshot(Target);
            var index = new Dictionary<string, RecordKind>();
            foreach (var item in data) { RecordKind kind; index[item.Key] = _kinds.TryGetValue(item.Key, out kind) ? kind : SaveRecords.LegacyKind(item.Key, item.Value); }
            ES3.Save(SaveRecords.IndexKey, SaveRecords.Encode(index), cache);
            string marker = Guid.NewGuid().ToString("N"); ES3.Save("apocasaver.commit", marker, cache);
            SaveRecords.MarkDirty(Target); ES3.StoreCachedFile(new ES3Settings(Target) { location = ES3.Location.File });
            var disk = new ES3Settings(Target) { location = ES3.Location.File };
            if (!File.Exists(disk.FullPath) || ES3.Load<string>("apocasaver.commit", disk) != marker) throw new IOException("Save commit could not be verified");
            var readback = new ES3File(disk, true); var diskRecords = SaveRecords.Data(readback);
            foreach (var item in SaveRecords.Snapshot(Target))
            { ES3Data value; if (!diskRecords.TryGetValue(item.Key, out value) || !value.bytes.SequenceEqual(item.Value.bytes)) throw new IOException("Readback mismatch: " + item.Key); }
            Publishing = true;
            _disk.Publish();
            Plugin.Current.StampCommitted(Target, _kind);
            _disk.Complete();
            _history = SaveRecords.Snapshot(Target); _kinds = index; _source = Target;
            LastSuccess = true; LastError = null; string file = Target; var exclusion = _exclusion;
            Time.timeScale = _scale; _disk = null; Publishing = false; Target = PatrolStage = null; _paths.Clear();
            exclusion.Destroy(); Plugin.Current.SaveCommitted(file);
            Plugin.Log.LogInfo("Save rebuilt and independently verified: " + file);
            if (!_automatic) StatusLabel.Show("Game saved", 4, false);
            PruneBackups(file);
        }
        internal static void Fail(string error, bool resume)
        {
            if (!Active) return;
            var tx = _disk; LastSuccess = false; LastError = error;
            LastFailureCause = error;
            try
            {
                if (Plugin.Current != null) Plugin.Current.AbortNativeSave();
                SaveParticipants.Cancel(); Publishing = true;
                HeldItemKeeper.CancelMetadataSave();
                tx.Rollback(RestoreLabel);
                if (tx.Files[0].Existed) SaveRecords.ReplaceCache(Target, _oldCache, false); else ES3File.cachedFiles.Remove(Target);
                _history = _oldHistory; _kinds = _oldKinds;
                if (_exclusion != null) _exclusion.Restore();
                ES3Settings.defaultSettings.path = _previousPath;
                foreach (var path in _paths) path.Item1.Value = path.Item2;
                Plugin.Log.LogError("Saving failed; previous destination restored: " + error);
                Warn(tx.Files[0].Existed ? "Saving failed. The previous save was restored." : "Saving failed. No save was created.");
            }
            catch (Exception e)
            { RecoveryRequired = true; Plugin.Log.LogError("Recovery required. Backup: " + tx.DirectoryPath + "\n" + e); Warn("Saving failed. Recovery needed; backup: " + tx.DirectoryPath); }
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
