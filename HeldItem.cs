using System;
using System.Collections.Generic;
using System.Globalization;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocasaver
{
    // Autosaves drop held items through GrabItem before pausing. No save-time physics overrides or hand restoration.
    // The public HandPose data API remains for Apocapocket; this mod does not apply those poses to objects.

    /// Per-item hand-local pose store, kept in the current save file next to the game's own item data.
    /// Values are "x,y,z,qx,qy,qz,qw". Writes are queued and flushed ~1 s after the game finished storing its save
    /// (the game saves through an Easy Save cache and stores it at the end, which would overwrite anything written earlier).
    public static class HandPose
    {
        private const string Prefix = "apocasaver.pose.";
        private static readonly Dictionary<string, string> _pending = new Dictionary<string, string>();

        /// Queue a pose for the next save (or update it in memory now).
        public static void Set(string itemName, string pose)
        {
            if (string.IsNullOrEmpty(itemName)) return;
            _pending[Prefix + itemName] = pose ?? "";
        }

        public static void Set(string itemName, Vector3 localPos, Quaternion localRot) { Set(itemName, Fmt(localPos, localRot)); }

        /// Pose for an item from the current save file (or the queued value), null if none.
        public static string Get(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return null;
            string v;
            if (_pending.TryGetValue(Prefix + itemName, out v) && !string.IsNullOrEmpty(v)) return v;
            return SaveStore.TryLoadAny(Runner.SaveFileCandidates(), Prefix + itemName, out v) ? v : null;
        }

        public static bool TryGet(string itemName, out Vector3 localPos, out Quaternion localRot)
        {
            return Parse(Get(itemName), out localPos, out localRot);
        }

        internal static void QueueRaw(string key, string value) { _pending[key] = value ?? ""; }
        internal static void Clear() { _pending.Clear(); }
        internal static int PendingCount { get { return _pending.Count; } }

        internal static int Flush(string file)
        {
            int n = 0;
            int total = _pending.Count;
            var saved = new List<string>();
            foreach (var kv in _pending) if (SaveStore.SaveString(file, kv.Key, kv.Value)) { n++; saved.Add(kv.Key); }
            foreach (string key in saved) _pending.Remove(key);
            Plugin.Log.LogInfo("Hand poses: wrote " + n + "/" + total + " keys to " + file);
            return n;
        }

        public static string Fmt(Vector3 p, Quaternion q)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(",", new[] { p.x.ToString("R", c), p.y.ToString("R", c), p.z.ToString("R", c), q.x.ToString("R", c), q.y.ToString("R", c), q.z.ToString("R", c), q.w.ToString("R", c) });
        }

        public static bool Parse(string s, out Vector3 p, out Quaternion q)
        {
            p = Vector3.zero; q = Quaternion.identity;
            if (string.IsNullOrEmpty(s)) return false;
            var a = s.Split(',');
            if (a.Length != 7) return false;
            var f = new float[7];
            for (int i = 0; i < 7; i++) if (!float.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return false;
            p = new Vector3(f[0], f[1], f[2]); q = new Quaternion(f[3], f[4], f[5], f[6]);
            return true;
        }
    }

    /// Easy Save 3 string keys in a named save file. Note the ES3.Load overloads: Load<T>(key, string filePath, settings)
    /// exists, so never pass a string default value as the second argument.
    internal static class SaveStore
    {
        private static ES3Settings Settings(string file, ES3.Location loc)
        {
            var s = new ES3Settings(file);
            s.location = loc;
            return s;
        }

        internal static bool SaveString(string file, string key, string value)
        {
            if (string.IsNullOrEmpty(file)) return false;
            bool ok = false;
            try { ES3.Save<string>(key, value, Settings(file, ES3.Location.Cache)); ok = true; }
            catch (Exception e) { Plugin.Log.LogWarning("ES3 save (cache) " + key + ": " + e.Message); }
            try { ES3.Save<string>(key, value, Settings(file, ES3.Location.File)); ok = true; }
            catch (Exception e) { Plugin.Log.LogWarning("ES3 save (file) " + key + ": " + e.Message); }
            return ok;
        }

        internal static bool TryLoad(string file, string key, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                var fs = Settings(file, ES3.Location.File);
                if (System.IO.File.Exists(fs.FullPath) && ES3.KeyExists(key, fs)) { value = ES3.Load<string>(key, fs); return true; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ES3 load (file) " + key + ": " + e.Message); }
            try
            {
                var cs = Settings(file, ES3.Location.Cache);
                if (ES3.KeyExists(key, cs)) { value = ES3.Load<string>(key, cs); return true; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("ES3 load (cache) " + key + ": " + e.Message); }
            return false;
        }

        internal static bool TryLoadAny(IEnumerable<string> files, string key, out string value)
        {
            value = null;
            var seen = new HashSet<string>();
            foreach (var f in files) { if (string.IsNullOrEmpty(f) || !seen.Add(f)) continue; if (TryLoad(f, key, out value)) return true; }
            return false;
        }
    }

    /// Vehicle parts (cassette, radio, headlight...) carry a CheckTag FSM whose start state reparents the object to the scene
    /// root. Switching the vehicle camera deactivates PlayerCamera; when it comes back PlayMaker restarts every FSM on the
    /// re-enabled objects (RestartOnEnable), so a held vehicle part is thrown out of the hand. While an item is in the hand the
    /// restart is switched off for the FSMs whose start path has side effects, and restored when it leaves.
    internal static class RestartGuard
    {
        private static readonly string[] Fsms = { "CheckTag", "LockPhysics" };
        private static GameObject _guarded;
        private static readonly Dictionary<PlayMakerFSM, bool> _original = new Dictionary<PlayMakerFSM, bool>();

        internal static void Track(GameObject held)
        {
            if (held == _guarded) return;
            // On transfer to a pocket, keep the restart flags chosen by Apocapocket (including save normalization).
            bool pocketOwnsPrevious = _guarded != null && PocketCompatibility.Owns(_guarded);
            if (!pocketOwnsPrevious)
                foreach (var pair in _original) if (pair.Key != null && pair.Key.Fsm != null) pair.Key.Fsm.RestartOnEnable = pair.Value;
            _original.Clear();
            _guarded = held;
            if (held == null) return;
            foreach (var f in held.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || Array.IndexOf(Fsms, f.FsmName) < 0) continue;
                _original[f] = f.Fsm.RestartOnEnable;
                f.Fsm.RestartOnEnable = false;
            }
        }
    }

    /// Drives the held-item logic; ticked every frame by Apocasaver's Runner, which also reports SaveLoadGame state changes.
    internal static class HeldItemKeeper
    {
        private static PlayMakerFSM _grab;
        private static FsmGameObject _itemVar;
        private static float _nextScan, _nextPoll, _flushAt = -1f;
        private static bool _saveActive, _saveStateSeen;
        private static bool _grabGuarded, _grabRestart;
        private static string _pendingFile;
        private static GameObject _ccItem;
        private static bool _ccResult;
        private static float _ccAt;
        private static void V(string s) { Plugin.Log.LogDebug(s); }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            // Apocapocket runs early in Update. Release stale custody before vanilla hand actions can use it again.
            ReleaseStalePocketReference();
            if (_flushAt >= 0f && now >= _flushAt)
            {
                _flushAt = -1f;
                if (HandPose.PendingCount > 0)
                {
                    string file = _pendingFile ?? Runner.CurrentSaveFile();
                    if (string.IsNullOrEmpty(file)) Plugin.Log.LogWarning("Hand poses: unknown save file, nothing written");
                    else HandPose.Flush(file);
                }
                _pendingFile = null;
            }
            if (now >= _nextPoll)
            {
                _nextPoll = now + 0.2f;
                RefreshHandGuard();
            }
        }

        /// From the Runner: SaveLoadGame changed state (only called on changes).
        internal static void OnSaveLoadState(string st)
        {
            if (st == "Start" || st == "setSeed" || st == "LoadGame") ResetPerGame();
            bool saving = st == "wait" || st == "SaveGame";
            if (saving)
            {
                if (!_saveActive) BeginSave("state " + st);
                _saveStateSeen = true;
            }
            else if (_saveActive && _saveStateSeen) EndSave("state " + st);
        }

        /// From the Harmony hook: the game sends "SaveGame" to every saveable object when writing a save.
        internal static void OnSaveEvent()
        {
            if (_saveActive) return;
            DropHeldItemForSave();
            BeginSave("SaveGame event");
        }

        private static void BeginSave(string why)
        {
            _saveActive = true; _saveStateSeen = false;
            _pendingFile = Runner.CurrentSaveFile();
            V("Game is saving (" + why + ") -> " + _pendingFile);
        }

        private static void EndSave(string why)
        {
            if (!_saveActive) return;
            _saveActive = false; _saveStateSeen = false;
            _flushAt = Time.unscaledTime + 1f;   // after the game stored its cache to disk
            V("Save finished (" + why + ")");
        }

        // ---- helpers
        private static void ResetPerGame()
        {
            _flushAt = -1f; _pendingFile = null;
            HandPose.Clear();
            if (_saveActive) { _saveActive = false; _saveStateSeen = false; }
        }

        internal static void ResetForScene()
        {
            ResetPerGame(); RestartGuard.Track(null);
            RestoreGrabRestart();
            _grab = null; _itemVar = null; _ccItem = null; _nextScan = _nextPoll = 0f;
        }

        internal static void OnGrabState(Fsm fsm)
        {
            if (fsm.GameObject == null || fsm.GameObject.name != "PlayerCamera" ||
                fsm.GameObject.scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene()) return;
            if (_grab != fsm.FsmComponent)
            {
                RestoreGrabRestart(); RestartGuard.Track(null);
                _grab = fsm.FsmComponent; _itemVar = _grab.FsmVariables.GetFsmGameObject("Item");
            }
            RefreshHandGuard();
        }

        private static void RefreshHandGuard()
        {
            // Camera switches deactivate this hierarchy. Keep custody until its native FSM resumes.
            if (_grab != null && !_grab.gameObject.activeInHierarchy) return;
            var item = RawHeldItem();
            if (IsContainer(item)) item = null;
            RestartGuard.Track(item);
            if (item != null && !_grabGuarded)
            {
                _grabRestart = _grab.Fsm.RestartOnEnable; _grabGuarded = true;
                _grab.Fsm.RestartOnEnable = false;
            }
            else if (item == null) RestoreGrabRestart();
        }

        private static void RestoreGrabRestart()
        {
            if (_grabGuarded && _grab != null && _grab.Fsm != null) _grab.Fsm.RestartOnEnable = _grabRestart;
            _grabGuarded = false;
        }

        private static void Scan()
        {
            if (_grab != null && _grab.gameObject != null) return;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null || f.gameObject.scene != UnityEngine.SceneManagement.SceneManager.GetActiveScene()) continue;
                if (f.FsmName == "GrabItem" && f.gameObject.name == "PlayerCamera" && f.gameObject.activeInHierarchy)
                { _grab = f; _itemVar = f.FsmVariables.GetFsmGameObject("Item"); break; }
            }
        }

        internal static GameObject RawHeldItem()
        {
            if (_grab == null || _grab.gameObject == null || _itemVar == null) return null;
            var item = _itemVar.Value;
            return HeldItemPolicy.PhysicallyHeld(SafeState(_grab), UnderHand(item)) ? item : null;
        }

        private static bool UnderHand(GameObject item)
        {
            if (item == null || _grab == null) return false;
            var hand = _grab.transform.Find("Hand");
            return hand != null && item.transform.IsChildOf(hand);
        }

        private static void ReleaseStalePocketReference()
        {
            var item = _itemVar != null ? _itemVar.Value : null;
            if (item == null || UnderHand(item) || !PocketCompatibility.Owns(item)) return;
            // Pocket() has custody. Do not send drop/not_Hold: those actions change its colliders/gravity/parent.
            _itemVar.Value = null;
            var held = _grab.FsmVariables.GetFsmGameObject("ItemInHand");
            if (held != null && held.Value == item) held.Value = null;
            var name = _grab.FsmVariables.GetFsmString("ItemInHandName");
            if (name != null) name.Value = "";
            if (HeldItemPolicy.HeldState(SafeState(_grab))) _grab.Fsm.SetState("idle");
            Plugin.Log.LogInfo("Apocapocket compatibility: released stale GrabItem reference to stored " + item.name);
        }

        /// Use vanilla drop for every item, including containers. Rotation/movement must finish before saving.
        internal static DropResult DropHeldItemForSave()
        {
            Scan();
            ReleaseStalePocketReference();
            var item = RawHeldItem();
            if (item == null) return DropResult.Ready;
            bool pocketOwned = PocketCompatibility.Owns(item);
            if (!HeldItemPolicy.CanDrop(SafeState(_grab), UnderHand(item), pocketOwned))
                return pocketOwned ? DropResult.Ready : DropResult.Busy;
            RestartGuard.Track(null);
            RestoreGrabRestart();
            Plugin.Log.LogInfo("Save: dropping held " + item.name + " through vanilla GrabItem");
            _grab.SendEvent("drop");
            return DropResult.Dropped;
        }

        /// Crate / box: by prefab name (crate_*, box_cardboard...) or structurally, i.e. it has other saveable items parented inside it.
        internal static bool IsContainer(GameObject item)
        {
            if (item == null) return false;
            if (item == _ccItem && Time.unscaledTime - _ccAt < 0.5f) return _ccResult;
            _ccItem = item; _ccAt = Time.unscaledTime; _ccResult = ComputeIsContainer(item);
            return _ccResult;
        }
        private static bool ComputeIsContainer(GameObject item)
        {
            try
            {
                string n = item.name.ToLowerInvariant();
                if (n.StartsWith("crate") || n.StartsWith("box_cardboard") || n.Contains("_crate")) return true;
                foreach (var f in item.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f != null && f.FsmName == "saveItemVar" && f.gameObject != item) return true;
            }
            catch { }
            return false;
        }

        internal static PlayMakerFSM FindFsm(GameObject go, string name)
        {
            if (go == null) return null;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == name) return f;
            return null;
        }

        private static string SafeState(PlayMakerFSM f)
        {
            try { return f != null && f.Fsm != null && f.Fsm.Initialized ? f.Fsm.ActiveStateName : ""; } catch { return ""; }
        }
    }

}
