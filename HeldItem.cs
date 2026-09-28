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
    //  Keeping the item in the player's hand across saves and loads.
    //
    //  Vanilla facts (from the FSM dumps):
    //   * GrabItem's ItemInHand state polls GetMouseButtonDown(Left) -> drop every frame, and PlayMaker keeps running while
    //     the pause menu is open, so the click on any menu button (Save, Settings, ...) drops the held item.
    //   * A held item has trigger colliders + no gravity. Easy Save stores those flags; after loading, Hand/DebugDrop gives it
    //     gravity back but the colliders stay triggers, so it falls through the world forever (what an autosave used to do).
    //   * The game itself never restores the hand: after a load the item just lies where it was.
    //
    //   * Vehicle parts restart their CheckTag FSM when the vehicle camera toggles PlayerCamera off and on; its start state
    //     unparents the part, so a held cassette/radio/headlight is thrown out of the hand.
    //
    //  What this does:
    //   * MenuClickFix:    skip GrabItem's mouse poll while __GameManager__/Menu is not in "play".
    //   * SaveFix:         on the SaveGame event, make the held item's colliders solid for the duration of the save
    //                      (collisions with the player ignored meanwhile), restore afterwards.
    //   * KeepHeldItem:    on save, record "<item>|<seed>" and the hand-local pose in the save file; after a load, find the
    //                      item again and hand it to GrabItem's Grab state.
    //   * VehiclePartFix:  switch off RestartOnEnable on the held item's CheckTag/LockPhysics FSMs while it is in the hand.
    //   * Crates/boxes (anything with saveable items inside) are left entirely to the game; an autosave drops them first.
    //   * HandPose:        public per-item pose store other mods may use (Apocapocket does, via reflection).
    // =====================================================================================================================

    /// Per-item hand-local pose store, kept in the current save file next to the game's own item data.
    /// Values are "x,y,z,qx,qy,qz,qw". Writes are queued and flushed ~1 s after the game finished storing its save
    /// (the game saves through an Easy Save cache and stores it at the end, which would overwrite anything written earlier).
    public static class HandPose
    {
        private const string Prefix = "apocasaver.pose.";
        internal const string HeldKey = "apocasaver.held";
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
            return SaveStore.TryLoadAny(HeldItemKeeper.SaveFileCandidates(), Prefix + itemName, out v) ? v : null;
        }

        public static bool TryGet(string itemName, out Vector3 localPos, out Quaternion localRot)
        {
            return Parse(Get(itemName), out localPos, out localRot);
        }

        internal static void QueueRaw(string key, string value) { _pending[key] = value ?? ""; }
        internal static void Clear() { _pending.Clear(); }

        internal static int Flush(string file)
        {
            int n = 0;
            foreach (var kv in _pending) if (SaveStore.SaveString(file, kv.Key, kv.Value)) n++;
            int total = _pending.Count;
            _pending.Clear();
            Plugin.Log.LogInfo("Held-item data: wrote " + n + "/" + total + " keys to " + file);
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

    /// Drives the held-item logic; ticked every frame by Apocasaver's Runner.
    internal static class HeldItemKeeper
    {
        private static PlayMakerFSM _grab, _saveLoad, _menu;
        private static Transform _hand, _handItemUse;
        private static float _nextScan;

        // last item seen in the hand; used on save only if it left the hand moments ago (e.g. forced out by the save itself)
        private static GameObject _lastHeld; private static Vector3 _lastPos; private static Quaternion _lastRot; private static float _lastHeldLostAt = -1f;
        private const float LastHeldGrace = 3f;
        private static bool _saveSeen; private static string _lastState = ""; private static float _flushAt = -1f; private static string _pendingFile;
        private static bool _restoreChecked; private static string _restoreName; private static float _restoreAt;

        // ---- save-safety (colliders solid for the duration of the save)
        private static GameObject _safeItem;
        private static readonly List<Collider> _triggers = new List<Collider>();
        private static readonly List<Collider> _itemCols = new List<Collider>();
        private static readonly List<Collider> _playerCols = new List<Collider>();
        private static float _safeDeadline;

        // container check cache (items can be put into a held box, so it is re-evaluated twice a second)
        private static GameObject _ccItem; private static bool _ccResult; private static float _ccAt;
        private static GameObject _lastContainerLogged;

        internal static bool MenuOpen { get { return _menu != null && _menu.gameObject != null && _menu.Fsm.Initialized && _menu.ActiveStateName != "play"; } }

        private static void V(string s) { if (Plugin.VerboseHeld.Value) Plugin.Log.LogInfo(s); }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            if (_grab == null || _grab.gameObject == null || _saveLoad == null || _saveLoad.gameObject == null) return;

            // Remember what is in the hand. It is forgotten when dropped/thrown/put away, when a crate is picked up instead,
            // when it went into a slot or pocket under the camera, or a few seconds after it left the hand.
            var raw = RawHeldItem();
            var h = raw != null && !IsContainer(raw) ? raw : null;
            if (Plugin.VehiclePartFix.Value) RestartGuard.Track(h); else RestartGuard.Track(null);
            if (h != null && h.transform.parent != null) { _lastHeld = h; _lastPos = h.transform.localPosition; _lastRot = h.transform.localRotation; _lastHeldLostAt = -1f; }
            else if (_lastHeld != null)
            {
                string gs = SafeState(_grab);
                if (_lastHeldLostAt < 0f) _lastHeldLostAt = now;
                if ((raw != null && raw != _lastHeld) || gs == "Drop" || gs == "Throw" || gs == "notHold" || now - _lastHeldLostAt > LastHeldGrace
                    || (_lastHeld.transform.parent != null && _lastHeld.transform.parent != _hand && _lastHeld.transform.IsChildOf(_grab.transform)))
                    _lastHeld = null;
            }

            // Follow the game's save/load flow.
            string st = SafeState(_saveLoad);
            if (st != _lastState)
            {
                if (st == "Start" || st == "setSeed" || st == "LoadGame") ResetPerGame();
                if (_lastState == "SaveGame" && st == "isPlay") _flushAt = now + 1.0f;
                _lastState = st;
            }
            bool saving = st == "wait" || st == "SaveGame";
            if (saving && !_saveSeen) { _saveSeen = true; OnGameSaving("state " + st); }
            else if (!saving) _saveSeen = false;
            if (_safeItem != null && (!saving || now >= _safeDeadline)) EndSafe();

            if (_flushAt >= 0f && now >= _flushAt)
            {
                _flushAt = -1f;
                string file = _pendingFile ?? SaveFileName();
                if (string.IsNullOrEmpty(file)) Plugin.Log.LogWarning("Held-item data: unknown save file, nothing written");
                else HandPose.Flush(file);
            }

            RestoreAfterLoad();
        }

        /// From the Harmony hook: the game sends "SaveGame" to every saveable object when writing a save.
        internal static void OnSaveEvent()
        {
            if (_grab == null || _saveSeen) return;
            _saveSeen = true;
            OnGameSaving("SaveGame event");
        }

        private static void OnGameSaving(string why)
        {
            _pendingFile = SaveFileName();
            V("Game is saving (" + why + ") -> " + _pendingFile);
            try
            {
                var held = HeldItem();
                if (held != null && Plugin.SaveFix.Value) BeginSafe(held);
                if (!Plugin.KeepHeldItem.Value) return;
                if (held == null && _lastHeld != null) held = _lastHeld;   // left the hand moments before the save (see Tick)
                if (held != null)
                {
                    bool inHand = held.transform.parent == _hand;
                    HandPose.Set(held.name, inHand ? held.transform.localPosition : _lastPos, inHand ? held.transform.localRotation : _lastRot);
                    HandPose.QueueRaw(HandPose.HeldKey, held.name + "|" + Seed());
                    Plugin.Log.LogInfo("Save: remembering held item " + held.name);
                }
                else HandPose.QueueRaw(HandPose.HeldKey, "");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Held item on save: " + e.Message); }
        }

        private static void RestoreAfterLoad()
        {
            if (_restoreChecked || !Plugin.KeepHeldItem.Value) return;
            if (SafeState(_saveLoad) != "isPlay" || Time.timeScale <= 0f || MenuOpen) return;
            if (_restoreName == null)
            {
                string v;
                if (!SaveStore.TryLoadAny(SaveFileCandidates(), HandPose.HeldKey, out v) || string.IsNullOrEmpty(v)) { _restoreChecked = true; return; }
                int i = v.IndexOf('|');
                if (i <= 0) { _restoreChecked = true; return; }
                string name = v.Substring(0, i), seed = v.Substring(i + 1);
                if (seed != Seed()) { V("Held-item record is for another world (seed " + seed + " vs " + Seed() + "); ignoring"); _restoreChecked = true; return; }
                _restoreName = name; _restoreAt = Time.unscaledTime + 1.5f;   // let Hand/DebugDrop etc. settle first
                return;
            }
            if (Time.unscaledTime < _restoreAt) return;
            _restoreChecked = true;
            if (HeldItem() != null || (_handItemUse != null && _handItemUse.childCount > 0)) { V("Not restoring " + _restoreName + ": hands busy"); return; }
            GameObject item = null;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.FsmName == "saveItemVar" && f.gameObject.name == _restoreName && f.gameObject.scene.IsValid()) { item = f.gameObject; break; }
            if (item == null) { Plugin.Log.LogWarning("Held item " + _restoreName + " not found in the loaded world"); return; }
            if (IsContainer(item)) { V("Not restoring " + _restoreName + ": crate/container, vanilla handling"); HandPose.QueueRaw(HandPose.HeldKey, ""); SaveStore.SaveString(SaveFileName(), HandPose.HeldKey, ""); return; }
            if (item.transform.parent != null && item.transform.parent.IsChildOf(_grab.transform)) { V("Held item " + _restoreName + " is already under the camera (slot / hand); leaving it"); return; }
            Vector3 p; Quaternion q;
            bool hasPose = HandPose.TryGet(item.name, out p, out q);
            Plugin.Log.LogInfo("Restoring held item after load: " + item.name);
            Grab(item, hasPose, p, q);
            HandPose.QueueRaw(HandPose.HeldKey, "");
            SaveStore.SaveString(SaveFileName(), HandPose.HeldKey, "");
        }

        /// Hand an item lying in the world to GrabItem's Grab state (the game's own pickup, minus the mouse look).
        private static void Grab(GameObject item, bool hasPose, Vector3 localPos, Quaternion localRot)
        {
            var handVar = _grab.FsmVariables.GetFsmGameObject("Hand") ?? FsmVariables.GlobalVariables.GetFsmGameObject("Hand");
            Transform hand = _hand;
            if (handVar != null) { if (handVar.Value != null) hand = handVar.Value.transform; else handVar.Value = _hand.gameObject; }
            item.transform.SetParent(hand, false);
            if (hasPose) { item.transform.localPosition = localPos; item.transform.localRotation = localRot; }
            else
            {
                var guide = _grab.transform.Find("Guide");
                if (guide != null) item.transform.position = guide.position; else item.transform.localPosition = new Vector3(0f, 0f, 1f);
                item.transform.rotation = Quaternion.LookRotation(_grab.transform.forward, Vector3.up);
            }
            if (item.layer == 0) item.layer = 9;
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null) { rb = item.AddComponent<Rigidbody>(); var lp = FindFsm(item, "LockPhysics"); var m = lp != null ? lp.FsmVariables.GetFsmFloat("mass") : null; if (m != null && m.Value > 0) rb.mass = m.Value; }
            rb.isKinematic = false; rb.useGravity = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            var lockFsm = FindFsm(item, "LockPhysics");
            if (lockFsm != null) { lockFsm.enabled = true; try { if (lockFsm.Fsm.Initialized && lockFsm.ActiveStateName != "off") lockFsm.Fsm.SetState("off"); } catch { } }
            var itemVar = _grab.FsmVariables.GetFsmGameObject("Item");
            if (itemVar != null) itemVar.Value = item;
            var nameVar = _grab.FsmVariables.GetFsmString("ItemInHandName");
            if (nameVar != null) nameVar.Value = "";
            _grab.Fsm.SetState("Grab");
            _lastHeld = item; _lastPos = item.transform.localPosition; _lastRot = item.transform.localRotation;
            V("Grabbed " + item.name + " -> GrabItem state " + _grab.ActiveStateName);
        }

        // ---- save-safety
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
            _safeDeadline = Time.unscaledTime + 6f;
            V("Save-safe: " + item.name + " (" + _triggers.Count + " trigger colliders made solid for the save)");
        }

        private static void EndSafe()
        {
            if (_safeItem == null) return;
            try
            {
                foreach (var c in _triggers) if (c != null) c.isTrigger = true;
                foreach (var ic in _itemCols) foreach (var pc in _playerCols) if (ic != null && pc != null) { try { Physics.IgnoreCollision(ic, pc, false); } catch { } }
                V("Save-safe: restored " + _safeItem.name);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Save-safe restore: " + e.Message); }
            _triggers.Clear(); _itemCols.Clear(); _playerCols.Clear();
            _safeItem = null;
        }

        // ---- helpers
        private static void ResetPerGame()
        {
            _restoreChecked = false; _restoreName = null; _lastHeld = null; _lastHeldLostAt = -1f; _flushAt = -1f; _pendingFile = null;
            HandPose.Clear();
            EndSafe();
        }

        private static void Scan()
        {
            if (_grab != null && _grab.gameObject != null && _saveLoad != null && _saveLoad.gameObject != null && _menu != null && _menu.gameObject != null) return;
            PlayMakerFSM grab = null, weapons = null, saveLoad = null, menu = null;
            var grabs = new List<PlayMakerFSM>();
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                string go = f.gameObject.name, n = f.FsmName;
                if (n == "GrabItem" && go == "PlayerCamera") grabs.Add(f);
                else if (n == "Weapons" && go == "Weapons") weapons = f;
                else if (go == "SaveLoadGame" && n == "SaveLoadGame") saveLoad = f;
                else if (go == "__GameManager__" && n == "Menu") menu = f;
            }
            foreach (var g in grabs) if (grab == null || (weapons != null && weapons.transform.IsChildOf(g.transform)) || g.gameObject.activeInHierarchy) grab = g;
            bool changed = grab != _grab;
            _grab = grab; _saveLoad = saveLoad; _menu = menu;
            if (_grab != null) { _hand = _grab.transform.Find("Hand"); _handItemUse = _grab.transform.Find("HandItemUse"); }
            if (changed && _grab != null) { V("Held-item keeper: player refs found"); ResetPerGame(); }
        }

        /// The item in the hand, or null. Crates/containers report null: they carry other items inside and all of the
        /// held-item logic (save-safe colliders, remember/restore, menu-click and camera guards) is left to the vanilla game for them.
        private static GameObject HeldItem()
        {
            var item = RawHeldItem();
            if (item == null) return null;
            if (IsContainer(item)) { if (_lastContainerLogged != item) { _lastContainerLogged = item; V("Held item " + item.name + " is a crate/container: vanilla handling"); } return null; }
            return item;
        }

        internal static GameObject RawHeldItem()
        {
            if (_grab == null || _grab.gameObject == null) return null;
            string s = SafeState(_grab);
            if (s != "ItemInHand" && s != "Rotate" && s != "Forward" && s != "Backward" && s != "Grab") return null;
            var v = _grab.FsmVariables.GetFsmGameObject("Item");
            return v != null ? v.Value : null;
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

        private static string Seed() { try { var v = _saveLoad.FsmVariables.GetFsmInt("seed"); return v != null ? v.Value.ToString() : ""; } catch { return ""; } }

        /// All places the game keeps the current save file name; none is right in every phase, so readers try them all.
        internal static List<string> SaveFileCandidates()
        {
            var list = new List<string>();
            try { var p = ES3Settings.defaultSettings.path; if (!string.IsNullOrEmpty(p)) list.Add(System.IO.Path.GetFileName(p)); } catch { }
            try
            {
                var reg = GameObject.Find("NewGO_ArrayList");
                var f = reg != null ? FindFsm(reg, "Save_NewGO_ArrayList") : null;
                var v = f != null ? f.FsmVariables.GetFsmString("SaveFile") : null;
                if (v != null && !string.IsNullOrEmpty(v.Value)) list.Add(v.Value);
            }
            catch { }
            try { var v = _saveLoad != null && _saveLoad.gameObject != null ? _saveLoad.FsmVariables.GetFsmString("SaveFile") : null; if (v != null && !string.IsNullOrEmpty(v.Value)) list.Add(v.Value); } catch { }
            return list;
        }

        private static string SaveFileName() { var c = SaveFileCandidates(); return c.Count > 0 ? c[0] : null; }

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
            if (!Plugin.MenuClickFix.Value) return true;
            var fsm = __instance.Fsm;
            if (fsm == null || fsm.Name != "GrabItem") return true;
            if (!HeldItemKeeper.MenuOpen) return true;
            if (HeldItemKeeper.IsContainer(HeldItemKeeper.RawHeldItem())) return true;   // crates: vanilla behaviour
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }

    /// The game's SaveGame event: earliest reliable moment to prepare the held item for the save.
    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class Fsm_ProcessEvent_Patch
    {
        static void Prefix(Fsm __instance, FsmEvent fsmEvent)
        {
            if (fsmEvent == null || fsmEvent.Name != "SaveGame") return;
            HeldItemKeeper.OnSaveEvent();
        }
    }
}
