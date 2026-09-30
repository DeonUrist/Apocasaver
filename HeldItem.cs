using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace Apocasaver
{
    // =====================================================================================================================
    //  The item in the player's hand around saves and loads.
    //
    //  Vanilla facts (from the FSM dumps):
    //   * GrabItem's ItemInHand state polls GetMouseButtonDown(Left) -> drop every frame, and PlayMaker keeps running while
    //     the pause menu is open, so the click on any menu button (Save, Settings, ...) drops the held item.
    //   * A held item has trigger colliders + no gravity. Easy Save stores those flags; after loading, the item lies where the
    //     hand was but its colliders are still triggers, so it falls through the world forever (what an autosave used to do).
    //   * The game itself never restores the hand.
    //   * Vehicle parts restart their CheckTag FSM when the vehicle camera toggles PlayerCamera off and on; its start state
    //     unparents the part, so a held cassette/radio/headlight is thrown out of the hand.
    //
    //  What this does (always on since 1.8):
    //   * Menu clicks:     skip GrabItem's mouse poll while __GameManager__/Menu is not in "play".
    //   * Save-safe item:  for the duration of the save the held item is turned into a proper world item — trigger colliders
    //                      solid, gravity on, rigidbody awake — held in place at its hand pose (collisions with the player
    //                      ignored meanwhile). The save file therefore describes an item that simply drops to the ground
    //                      after loading; nothing is put back into the hand (1.8, replaces the old remember/restore).
    //   * Vehicle parts:   RestartOnEnable switched off on the held item's CheckTag/LockPhysics FSMs while it is in the hand.
    //   * Crates/boxes (anything with saveable items inside) are left entirely to the game; an autosave drops them first.
    //   * HandPose:        public per-item pose store other mods may use (Apocapocket does, via reflection). Queued values are
    //                      written to the save file ~1 s after the game finished storing it.
    //
    //  Cost: nothing per frame outside a save except a 5 Hz look at GrabItem's Item variable (for the vehicle-part guard).
    //  The Runner tells this class about SaveLoadGame state changes; it does not poll FSM states itself.
    // =====================================================================================================================

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
            foreach (var kv in _pending) if (SaveStore.SaveString(file, kv.Key, kv.Value)) n++;
            int total = _pending.Count;
            _pending.Clear();
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

        internal static void Track(GameObject held)
        {
            if (held == _guarded) return;
            Set(_guarded, false);
            _guarded = held;
            Set(held, true);
        }

        internal static void Set(GameObject item, bool guarded)
        {
            if (item == null) return;
            foreach (var f in item.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || Array.IndexOf(Fsms, f.FsmName) < 0) continue;
                f.Fsm.RestartOnEnable = !guarded;
            }
        }
    }

    /// Drives the held-item logic; ticked every frame by Apocasaver's Runner, which also reports SaveLoadGame state changes.
    internal static class HeldItemKeeper
    {
        private static PlayMakerFSM _grab, _menu;
        private static FsmGameObject _itemVar;   // GrabItem's "Item" variable (the object in the hand)
        private static float _nextScan, _nextPoll;
        private const float PollInterval = 0.2f;  // how often the vehicle-part guard looks at the hand
        private const float SaveTimeout = 8f;     // give the item back its hand physics even if SaveLoadGame never reports the end

        // ---- one save (from the SaveGame event, or SaveLoadGame entering wait/SaveGame, until it leaves those states)
        private static bool _saveActive, _saveStateSeen;
        private static float _saveDeadline, _flushAt = -1f;
        private static string _pendingFile;

        // ---- the held item made save-safe for the duration of the save
        private static GameObject _safeItem;
        private static Rigidbody _safeRb;
        private static bool _safeGravity, _safeKinematic;
        private static Transform _safeParent;
        private static Vector3 _safePos;
        private static Quaternion _safeRot;
        private static readonly List<Collider> _triggers = new List<Collider>();
        private static readonly List<Collider> _itemCols = new List<Collider>();
        private static readonly List<Collider> _playerCols = new List<Collider>();

        // container check cache (items can be put into a held box, so it is re-evaluated twice a second)
        private static GameObject _ccItem; private static bool _ccResult; private static float _ccAt;
        private static GameObject _lastContainerLogged;

        internal static bool MenuOpen { get { return _menu != null && _menu.gameObject != null && _menu.Fsm.Initialized && _menu.ActiveStateName != "play"; } }

        private static void V(string s) { Plugin.Log.LogDebug(s); }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            if (_saveActive)
            {
                HoldSafe();
                if (now >= _saveDeadline) EndSave("timeout");
            }
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
                _nextPoll = now + PollInterval;
                RestartGuard.Track(HeldItem());
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
            if (_grab == null || _saveActive) return;
            BeginSave("SaveGame event");
        }

        private static void BeginSave(string why)
        {
            _saveActive = true; _saveStateSeen = false;
            _saveDeadline = Time.unscaledTime + SaveTimeout;
            _pendingFile = Runner.CurrentSaveFile();
            V("Game is saving (" + why + ") -> " + _pendingFile);
            try
            {
                var held = HeldItem();
                if (held != null) BeginSafe(held);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Held item on save: " + e.Message); }
        }

        private static void EndSave(string why)
        {
            if (!_saveActive) return;
            _saveActive = false; _saveStateSeen = false;
            EndSafe();
            _flushAt = Time.unscaledTime + 1f;   // after the game stored its cache to disk
            V("Save finished (" + why + ")");
        }

        // ---- save-safety: the held item is saved as a plain world item that drops to the ground after loading
        private static void BeginSafe(GameObject item)
        {
            if (_safeItem != null) EndSafe();
            _safeItem = item;
            _triggers.Clear(); _itemCols.Clear(); _playerCols.Clear();
            foreach (var c in item.GetComponentsInChildren<Collider>(true)) if (c != null && c.isTrigger) { c.isTrigger = false; _triggers.Add(c); }
            var player = GameObject.Find("Player");
            if (player != null) foreach (var pc in player.GetComponentsInChildren<Collider>(true)) if (pc != null && pc.enabled) _playerCols.Add(pc);
            foreach (var c in item.GetComponentsInChildren<Collider>(true)) if (c != null && c.enabled) _itemCols.Add(c);
            foreach (var ic in _itemCols) foreach (var pc in _playerCols) { try { Physics.IgnoreCollision(ic, pc, true); } catch { } }
            // gravity on, so the saved rigidbody is that of a free item (the game pauses physics while it writes the save;
            // HoldSafe keeps the item at its hand pose in case it does not)
            _safeRb = item.GetComponent<Rigidbody>();
            if (_safeRb != null)
            {
                _safeGravity = _safeRb.useGravity; _safeKinematic = _safeRb.isKinematic;
                _safeRb.useGravity = true;
                _safeRb.isKinematic = false;
                _safeRb.velocity = Vector3.zero; _safeRb.angularVelocity = Vector3.zero;
            }
            var t = item.transform;
            _safeParent = t.parent; _safePos = t.localPosition; _safeRot = t.localRotation;
            Plugin.Log.LogInfo("Save: " + item.name + " in hand made save-safe (" + _triggers.Count + " trigger colliders solid, gravity on); it will lie on the ground after loading");
        }

        /// Every frame during the save: keep the item where the hand holds it.
        private static void HoldSafe()
        {
            if (_safeItem == null) return;
            try
            {
                var t = _safeItem.transform;
                if (t.parent == _safeParent) { t.localPosition = _safePos; t.localRotation = _safeRot; }
                if (_safeRb != null) { _safeRb.velocity = Vector3.zero; _safeRb.angularVelocity = Vector3.zero; }
            }
            catch { }
        }

        private static void EndSafe()
        {
            if (_safeItem == null) return;
            try
            {
                foreach (var c in _triggers) if (c != null) c.isTrigger = true;
                foreach (var ic in _itemCols) foreach (var pc in _playerCols) if (ic != null && pc != null) { try { Physics.IgnoreCollision(ic, pc, false); } catch { } }
                if (_safeRb != null) { _safeRb.useGravity = _safeGravity; _safeRb.isKinematic = _safeKinematic; if (!_safeKinematic) { _safeRb.velocity = Vector3.zero; _safeRb.angularVelocity = Vector3.zero; } }
                V("Save-safe: restored " + _safeItem.name);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Save-safe restore: " + e.Message); }
            _triggers.Clear(); _itemCols.Clear(); _playerCols.Clear();
            _safeItem = null; _safeRb = null; _safeParent = null;
        }

        // ---- helpers
        private static void ResetPerGame()
        {
            _flushAt = -1f; _pendingFile = null;
            HandPose.Clear();
            if (_saveActive) { _saveActive = false; _saveStateSeen = false; }
            EndSafe();
        }

        private static void Scan()
        {
            if (_grab != null && _grab.gameObject != null && _menu != null && _menu.gameObject != null) return;
            PlayMakerFSM grab = null, weapons = null, menu = null;
            var grabs = new List<PlayMakerFSM>();
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                string go = f.gameObject.name, n = f.FsmName;
                if (n == "GrabItem" && go == "PlayerCamera") grabs.Add(f);
                else if (n == "Weapons" && go == "Weapons") weapons = f;
                else if (go == "__GameManager__" && n == "Menu") menu = f;
            }
            foreach (var g in grabs) if (grab == null || (weapons != null && weapons.transform.IsChildOf(g.transform)) || g.gameObject.activeInHierarchy) grab = g;
            bool changed = grab != _grab;
            _grab = grab; _menu = menu;
            _itemVar = _grab != null ? _grab.FsmVariables.GetFsmGameObject("Item") : null;
            if (changed && _grab != null) { V("Held-item keeper: player refs found"); ResetPerGame(); }
        }

        /// The item in the hand, or null. Crates/containers report null: they carry other items inside and all of the
        /// held-item logic (save-safe colliders, menu-click and camera guards) is left to the vanilla game for them.
        private static GameObject HeldItem()
        {
            var item = RawHeldItem();
            if (item == null) return null;
            if (IsContainer(item)) { if (_lastContainerLogged != item) { _lastContainerLogged = item; V("Held item " + item.name + " is a crate/container: vanilla handling"); } return null; }
            return item;
        }

        internal static GameObject RawHeldItem()
        {
            if (_grab == null || _grab.gameObject == null || _itemVar == null) return null;
            string s = SafeState(_grab);
            if (s != "ItemInHand" && s != "Rotate" && s != "Forward" && s != "Backward" && s != "Grab") return null;
            return _itemVar.Value;
        }

        /// Autosave is about to run: a held crate/container is dropped first (vanilla drop), so its contents are saved like any
        /// items lying in the world. Returns true when a drop was issued (caller waits a moment before saving).
        internal static bool DropHeldContainerForSave()
        {
            var item = RawHeldItem();
            if (item == null || !IsContainer(item)) return false;
            if (SafeState(_grab) != "ItemInHand") return false;   // rotating / moving it: try again next frame
            Plugin.Log.LogInfo("Autosave: dropping held " + item.name + " (crate/container) first");
            _grab.SendEvent("drop");
            return true;
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

    /// Menu clicks must not reach GrabItem's mouse poll (that is what drops the held item when saving from the menu).
    [HarmonyPatch(typeof(GetMouseButtonDown), "OnUpdate")]
    internal static class GetMouseButtonDown_Patch
    {
        static bool Prefix(GetMouseButtonDown __instance)
        {
            var fsm = __instance.Fsm;
            if (fsm == null || fsm.Name != "GrabItem") return true;
            if (!HeldItemKeeper.MenuOpen) return true;
            if (HeldItemKeeper.IsContainer(HeldItemKeeper.RawHeldItem())) return true;   // crates: vanilla behaviour
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }
}
