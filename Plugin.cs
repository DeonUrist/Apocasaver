using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Apocasaver
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocasaver";
        public const string NAME = "Apocasaver";
        public const string VERSION = "1.9.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IntervalMinutes;
        internal static ConfigEntry<int> WarningSeconds;
        internal static ConfigEntry<bool> SaveNamingEnabled;
        internal static bool AutosaveHooksReady;

        internal static Runner Current;   // the live runner (re-created after scene loads)
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            BindConfig(Config);
            ApplyPatches();

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureRunner("Awake");
            Log.LogInfo(NAME + " " + VERSION + " loaded. Autosave every " + IntervalMinutes.Value + " min.");
        }

        /// Bind order = order in the cfg file and in the Apocasetter menu. All held-item fixes are always on (no keys).
        internal static void BindConfig(ConfigFile cfg)
        {
            var orphans = OrphanedEntries(cfg);   // keys read from the cfg that nothing has bound yet (older versions' keys among them)
            string oldEnabled = null; bool hadNewEnabled = false;
            if (orphans != null)
            {
                orphans.TryGetValue(new ConfigDefinition("General", "Enabled"), out oldEnabled);
                hadNewEnabled = orphans.ContainsKey(new ConfigDefinition("General", "Autosave enabled"));
            }

            SaveNamingEnabled = cfg.Bind("General", "Save naming", true,
                "Ask for a name when you save (slot menu, ESC menu or a save point). The name replaces \"Manual\" in the slot label and autosaves keep it. Cancel aborts the save. Uses the Apocasetter look when Apocasetter is installed.");
            Enabled = cfg.Bind("General", "Autosave enabled", true, "Save the game automatically (see IntervalMinutes).");
            IntervalMinutes = cfg.Bind("General", "IntervalMinutes", 10f,
                new ConfigDescription("Minutes between autosaves. The game is saved over the slot this character was last saved to (or loaded from). " +
                                      "If you are in a vehicle when the time is up, the autosave happens as soon as you get out.",
                                      new AcceptableValueRange<float>(1f, 120f)));
            WarningSeconds = cfg.Bind("General", "Autosave warning (sec)", 30,
                new ConfigDescription("Show early autosave reminders this many seconds before saving, then every 15 seconds. 0 disables early reminders. A red 5-4-3-2-1 countdown always precedes autosave.",
                                      new AcceptableValueRange<int>(0, 300)));
            cfg.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            MigrateConfig(cfg, orphans, oldEnabled, hadNewEnabled);
        }

        private static Dictionary<ConfigDefinition, string> OrphanedEntries(ConfigFile cfg)
        {
            try
            {
                var p = typeof(ConfigFile).GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                return p != null ? p.GetValue(cfg, null) as Dictionary<ConfigDefinition, string> : null;
            }
            catch (Exception e) { Log.LogDebug("Config orphans not readable: " + e.Message); return null; }
        }

        /// 1.8: "Enabled" became "Autosave enabled" (value carried over once) and the [HeldItem] switches are gone (always on);
        /// the stale keys are dropped from the cfg instead of lingering as orphans.
        private static void MigrateConfig(ConfigFile cfg, Dictionary<ConfigDefinition, string> orphans, string oldEnabled, bool hadNewEnabled)
        {
            try
            {
                bool b;
                if (oldEnabled != null && !hadNewEnabled && bool.TryParse(oldEnabled.Trim(), out b) && b != Enabled.Value) { Enabled.Value = b; Log.LogInfo("Config: Enabled=" + b + " carried over to \"Autosave enabled\""); }
                if (orphans == null) return;
                var dead = new List<ConfigDefinition>();
                foreach (var k in orphans.Keys) if (k.Section == "HeldItem" || (k.Section == "General" && k.Key == "Enabled")) dead.Add(k);
                if (dead.Count == 0) return;
                foreach (var k in dead) orphans.Remove(k);
                cfg.Save();
                Log.LogInfo("Config: removed " + dead.Count + " key(s) from older versions");
            }
            catch (Exception e) { Log.LogWarning("Config migration: " + e.Message); }
        }

        /// Each patch class is applied on its own, so one that fails only disables its own feature.
        private void ApplyPatches()
        {
            var harmony = new Harmony(GUID);
            bool stateHook = false, writeHook = false;
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (!t.IsDefined(typeof(HarmonyPatch), false)) continue;
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                    if (t == typeof(AutosaveStatePatch)) stateHook = true;
                    if (t == typeof(AutosaveWritePatch)) writeHook = true;
                }
                catch (Exception e) { Logger.LogError("Patch " + t.Name + " failed, that feature is inactive: " + e); }
            }
            AutosaveHooksReady = stateHook && writeHook;
            if (!AutosaveHooksReady) Logger.LogError("Autosave disabled for this session: native save verification hooks could not be installed");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_runnerGo != null) { var r = _runnerGo.GetComponent<Runner>(); if (r != null) r.ResetForScene(scene.name); }
            EnsureRunner("sceneLoaded " + scene.name);
        }

        internal static void EnsureRunner(string reason)
        {
            if (_runnerGo != null && _runnerGo.activeInHierarchy) return;
            _runnerGo = new GameObject("Apocasaver.Runner");
            _runnerGo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_runnerGo);
            _runnerGo.AddComponent<Runner>();
            Log.LogInfo("Runner created (" + reason + ")");
        }
    }

    /// Per-frame logic (lives on a hidden GameObject because the game destroys plugin objects on scene load).
    internal class Runner : MonoBehaviour, IAutosaveRuntime
    {
        private void Awake() { Plugin.Current = this; _autosave = new AutosaveSession(this); }
        private void OnGUI() { SaveNaming.OnGUI(); }
        private void LateUpdate() { SaveNaming.LateUpdate(); _presentation.Tick(); }
        private void OnDestroy()
        {
            string state = SafeState(_saveLoad);
            bool resume = Alive(_saveLoad) && _saveLoad.gameObject.scene == SceneManager.GetActiveScene() &&
                (state == "isPlay" || state == "wait" || state == "SaveGame");
            _autosave.Cancel(null, resume); _presentation.End(resume); SaveNaming.Abort();
            HeldItemKeeper.ResetForScene();
            if (Plugin.Current == this) Plugin.Current = null;
        }

        // Game FSMs we watch / drive.
        private PlayMakerFSM _saveLoad;   // SaveLoadGame [SaveLoadGame]  — SaveFile var, state isPlay/SaveGame/LoadGame
        private PlayMakerFSM _saveButton; // SaveGame_Canvas/SaveGame/savegame [Continue] — the game's own "save now" button
        private PlayMakerFSM _menu;       // __GameManager__ [Menu] — play/pause
        private PlayMakerFSM _inCar;      // Player [InCar] — OnFoot/InCar
        private PlayMakerFSM _health;     // Player [Health] — playerHealth/playerDeath
        private PlayMakerFSM _sleep;      // Player [Sleep] — Awake/...
        private PlayMakerFSM _newGoSave;  // NewGO_ArrayList [Save_NewGO_ArrayList] — SaveFile var (set by the slot buttons)
        private float _nextScan;
        private string _slotFile;         // the slot the game will write to; refreshed once a second and around saves/loads, never per frame

        private bool _armed;              // saw the game load or save this session → SaveFile is this character's slot
        private float _lastSave;          // realtime of the last save (ours or the game's)
        private float _onFootSince;       // realtime when the player last got out of a car
        private bool _wasOnFoot;
        private string _lastState = "";
        private AutosaveSession _autosave;
        private readonly AutosavePresentation _presentation = new AutosavePresentation();
        private bool _globalSaved, _registryFlushed, _playerWritten, _globalWritten;
        private string _nativeError;
        private string _autosaveFile;
        private bool _saving { get { return _autosave != null && _autosave.Saving; } }
        internal bool IsAutosaving { get { return _saving; } }
        private string _armedFile;        // slot file at the moment we armed
        private bool _slotLabelsFitted;
        private int _heldErrors;

        private static bool Alive(PlayMakerFSM f) { return f != null && f.gameObject != null; }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            StatusLabel.Tick();
            try { HeldItemKeeper.Tick(); }
            catch (Exception e) { if (_heldErrors++ % 600 == 0) Plugin.Log.LogWarning("HeldItemKeeper (" + _heldErrors + "x): " + e); }   // first one, then every 600th
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            if (!Alive(_saveLoad)) { _autosave.Cancel("SaveLoadGame disappeared", false); Disarm("SaveLoadGame gone"); _lastState = ""; return; }

            // Watch the game's own save/load flow (state changes only; the held-item keeper follows the same changes).
            string st = SafeState(_saveLoad);
            if (st != _lastState)
            {
                try { HeldItemKeeper.OnSaveLoadState(st); }
                catch (Exception e) { Plugin.Log.LogWarning("HeldItemKeeper state " + st + ": " + e); }
                if (st == "SaveGame")
                {
                    _lastSave = now; Arm("game saved");
                    if (!_saving)   // the player saved (slot menu, ESC menu or a save point)
                    {
                        string kind = Plugin.SaveNamingEnabled.Value && SaveNaming.PendingName != null ? SaveNaming.PendingName : "Manual";
                        SaveNaming.PendingName = null;
                        StampSlotLabel(_slotFile, kind, true);   // fresh: Arm() just refreshed it
                    }
                }
                else if (st == "LoadGame" || st == "generateTerrain 2" || st == "LoadVar") { _autosave.Cancel(null, false); _lastSave = now; Arm("game loaded"); }
                else if (st == "Start" || st == "setSeed" || st == "generateTerrain") { _autosave.Cancel(null, false); Disarm("new game (" + st + ")"); }
                if (st == "isPlay" && !_armed && (_lastState == "play" || _lastState == "")) _lastSave = now; // baseline for the "no slot" reminder
                _lastState = st;
            }
            if (!Plugin.Enabled.Value && !_saving) _autosave.Cancel(null, true);
            _autosave.Tick(now);

            bool onFoot = Alive(_inCar) && SafeState(_inCar) == "OnFoot";
            if (onFoot && !_wasOnFoot) _onFootSince = now;
            _wasOnFoot = onFoot;

            if (!Plugin.Enabled.Value || _autosave.Active) return;
            float remaining = Plugin.IntervalMinutes.Value * 60f - (now - _lastSave);
            if (remaining > 5f)
            {
                WarnCountdown(remaining);
                return;
            }

            if (!_armed)
            {
                if (remaining > 0f) return;
                // Time for an autosave but this character has never been saved (or loaded): tell the player, retry next interval.
                if (!InPlay()) return;
                _lastSave = now;
                StatusLabel.Show("Cannot autosave, save at least once", 6f, false);
                Plugin.Log.LogInfo("Autosave skipped: no save slot for this character yet");
                return;
            }

            if (!CanSaveNow(now, onFoot)) return;
            _autosave.Start(now, _armedFile);
        }

        // ---- "Autosave in X sec" countdown: at WarningSeconds, then every 15 s, never at 0 ----
        private float _warnCycle = -1f;   // _lastSave value the current countdown belongs to
        private int _warnNext;            // next threshold (seconds) to announce

        private void WarnCountdown(float remaining)
        {
            if (!_armed || !Plugin.AutosaveHooksReady) return;
            int w = Plugin.WarningSeconds.Value;
            if (w <= 0) return;
            if (_warnCycle != _lastSave) { _warnCycle = _lastSave; _warnNext = w; }
            if (_warnNext <= 0 || remaining > _warnNext) return;
            // skip thresholds we slept through (e.g. paused in a menu) and announce the current one
            while (_warnNext - 15 > 0 && remaining <= _warnNext - 15) _warnNext -= 15;
            int shown = _warnNext;
            _warnNext -= 15;
            if (!InPlay()) return; // stay quiet in menus / loading; the next threshold will still fire
            StatusLabel.Show("Autosave in " + shown + " sec", 4f, false);
        }

        /// Player is actually playing (not in a menu, loading screen, dead...).
        private bool InPlay()
        {
            if (Time.timeScale <= 0f) return false;
            if (SafeState(_saveLoad) != "isPlay") return false;
            if (Alive(_menu) && SafeState(_menu) != "play") return false;
            if (Alive(_health) && SafeState(_health) != "playerHealth") return false;
            return true;
        }

        internal void Disarm(string why)
        {
            if (_armed) Plugin.Log.LogInfo("Autosave disarmed (" + why + ")");
            _armed = false; _armedFile = null;
        }

        internal void ResetForScene(string name)
        {
            _autosave.Cancel(null, false);
            _presentation.End(false);
            SaveNaming.Abort(); HeldItemKeeper.ResetForScene();
            Disarm("scene loaded: " + name);
            _saveLoad = _saveButton = _menu = _inCar = _health = _sleep = _newGoSave = null;
            _slotFile = null; _lastState = ""; _nextScan = 0f; _wasOnFoot = false;
            _slotLabelsFitted = false;
        }

        private void Arm(string why)
        {
            _armedFile = RefreshSlotFile();
            if (!IsSlotFile(_armedFile)) { Disarm("invalid SaveFile"); return; }
            if (!_armed) Plugin.Log.LogInfo("Autosave armed (" + why + "), slot file = " + _armedFile);
            _armed = true;
        }

        // ---- current save slot. Looking it up costs a few FSM variable reads, so it is cached (_slotFile) and only
        // refreshed once a second (Scan) and at the moments that matter: arming, stamping, autosaving, the naming popup.

        /// The slot file the game will write to (fresh lookup), or null.
        internal static string CurrentSaveFile()
        {
            var r = Plugin.Current;
            return r != null ? r.RefreshSlotFile() : null;
        }

        /// Only the selected slot is eligible for metadata reads; never fall back to another slot's stale FSM variable.
        internal static List<string> SaveFileCandidates()
        {
            var r = Plugin.Current;
            var file = r != null ? r.RefreshSlotFile() : null;
            return IsSlotFile(file) ? new List<string> { file } : new List<string>();
        }

        private string RefreshSlotFile()
        {
            try { _slotFile = ES3Settings.defaultSettings.path; }
            catch { _slotFile = null; }
            return _slotFile;
        }

        private static bool SlotFileExists(string file)
        {
            try { if (ES3.FileExists(file)) return true; } catch { }
            try { return File.Exists(Path.Combine(Application.persistentDataPath, file)); } catch { return false; }
        }

        private bool CanSaveNow(float now, bool onFoot)
        {
            if (!Plugin.AutosaveHooksReady) return false;
            if (PocketCompatibility.Busy) return false;
            if (!InPlay()) return false;
            if (!onFoot || now - _onFootSince < 2f) return false;                    // in a car (or just got out)
            if (Alive(_sleep) && SafeState(_sleep) != "Awake") return false;         // sleeping
            if (!Alive(_saveButton) || !Alive(_newGoSave) || !Alive(_menu) || !Alive(_health) || !Alive(_sleep)) return false;
            if (SafeState(_saveButton) != "off" || SafeState(_newGoSave) != "off") return false;
            string file = _slotFile;   // refreshed by Scan within the last second
            if (string.IsNullOrEmpty(file) || file != _armedFile) return false;
            if (!SlotFileExists(file)) return false;
            return true;
        }

        internal static bool IsSlotFile(string file)
        {
            return AutosaveSession.IsSlotFile(file);
        }

        bool IAutosaveRuntime.CanBegin(string file)
        {
            return Plugin.Enabled.Value && _armed && IsSlotFile(file) && file == _armedFile &&
                RefreshSlotFile() == file && CanSaveNow(Time.realtimeSinceStartup, Alive(_inCar) && SafeState(_inCar) == "OnFoot");
        }

        void IAutosaveRuntime.Countdown(int seconds) { StatusLabel.ShowCountdown(seconds); }
        DropResult IAutosaveRuntime.DropHeldItem() { return HeldItemKeeper.DropHeldItemForSave(); }

        void IAutosaveRuntime.BeginSave(string file)
        {
            if (!IsSlotFile(file) || file != _armedFile || RefreshSlotFile() != file) throw new InvalidOperationException("The selected save slot changed");
            PlayMakerFSM slot = null;
            string slotName = "save_game_" + file.Substring(8, file.Length - 12);
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (Alive(f) && f.gameObject.scene == _saveLoad.gameObject.scene && f.gameObject.name == slotName &&
                    f.FsmName == "Continue" && GoPath(f.transform).Contains("SaveGame_Canvas")) { slot = f; break; }
            if (slot == null || !slot.enabled || !slot.gameObject.activeInHierarchy || !slot.Fsm.Initialized)
                throw new InvalidOperationException("The selected slot button is unavailable");
            var canvas = slot.GetComponentInParent<Canvas>();
            GameObject loading = null;
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                if (t != null && t.gameObject.scene == _saveLoad.gameObject.scene && t.name == "LoadingScreen") { loading = t.gameObject; break; }
            if (canvas == null || loading == null || loading.activeInHierarchy) throw new InvalidOperationException("The save menu or loading screen is unavailable");

            var registryFile = _newGoSave.FsmVariables.GetFsmString("SaveFile");
            var globalFile = _saveLoad.FsmVariables.GetFsmString("SaveFile");
            if (registryFile == null || globalFile == null) throw new InvalidOperationException("The game's save slot variables are unavailable");
            ES3Settings.defaultSettings.path = file;
            registryFile.Value = globalFile.Value = file;
            _autosaveFile = file; _globalSaved = _registryFlushed = _playerWritten = _globalWritten = false; _nativeError = null;
            _lastSave = Time.realtimeSinceStartup;
            string kind = Plugin.SaveNamingEnabled.Value ? SaveNaming.NameForAutosave(file) ?? "Autosave" : "Autosave";
            StatusLabel.Hide();
            _presentation.Begin(_menu, canvas, loading);
            Plugin.Log.LogInfo("Autosave: native slot " + file + ", all save paths synchronized");
            slot.SendEvent("Clicked");
            StampSlotLabel(file, kind, true); // Native slot click writes a bare date; stamp and persist our label afterwards.
        }

        bool IAutosaveRuntime.SaveComplete
        {
            get
            {
                if (_nativeError != null) throw new IOException(_nativeError);
                return _globalSaved && _registryFlushed && _playerWritten && _globalWritten && !PocketCompatibility.SaveBusy && SafeState(_saveLoad) == "isPlay" &&
                    SafeState(_newGoSave) == "off" && SafeState(_saveButton) == "off";
            }
        }

        internal void OnNativeStateEntered(Fsm fsm, string state)
        {
            if (!_saving) return;
            if (Alive(_saveLoad) && fsm == _saveLoad.Fsm && state == "SaveGame") _globalSaved = true;
            if (Alive(_newGoSave) && fsm == _newGoSave.Fsm && state == "store cached file") _registryFlushed = true;
        }

        internal void OnSaveActionCompleted(ES3PlayMaker.SettingsAction action)
        {
            if (!_saving) return;
            var save = action as ES3PlayMaker.SaveAll;
            if (save == null || (save.key.Value != "Player" && save.key.Value != "global") || action.GetSettings().path != _autosaveFile) return;
            if (save.key.Value == "Player" && save.Fsm.GameObject.name == "Player" &&
                save.Fsm.GameObject.scene == _saveLoad.gameObject.scene) _playerWritten = true;
            if (save.key.Value == "global" && save.Fsm == _saveLoad.Fsm) _globalWritten = true;
        }

        internal void OnSaveActionFailed(Exception error)
        {
            if (_saving && _nativeError == null) _nativeError = "Native save action failed: " + error.Message;
        }

        void IAutosaveRuntime.Commit(string file)
        {
            if (file != _autosaveFile || RefreshSlotFile() != file ||
                _newGoSave.FsmVariables.GetFsmString("SaveFile").Value != file ||
                _saveLoad.FsmVariables.GetFsmString("SaveFile").Value != file)
                throw new InvalidOperationException("A save path changed during autosave");
            var cache = new ES3Settings(file) { location = ES3.Location.Cache };
            foreach (string key in new[] { "Player", "global", "newGOArray_Items" })
                if (!ES3.KeyExists(key, cache)) throw new InvalidOperationException("The save cache is missing " + key);
            if (HandPose.PendingCount > 0) HandPose.Flush(file);
            const string markerKey = "apocasaver.commit";
            string marker = Guid.NewGuid().ToString("N");
            ES3.Save<string>(markerKey, marker, cache);
            var disk = new ES3Settings(file) { location = ES3.Location.File };
            ES3.StoreCachedFile(disk);
            if (!File.Exists(disk.FullPath) || ES3.Load<string>(markerKey, disk) != marker)
                throw new IOException("The autosave could not be verified on disk");
            Plugin.Log.LogInfo("Autosave committed and verified: " + disk.FullPath);
        }

        void IAutosaveRuntime.EndSave(bool success, string error, bool resume)
        {
            try { _presentation.End(resume); }
            catch (Exception e) { Plugin.Log.LogError("Autosave cleanup: " + e); }
            _autosaveFile = null; _globalSaved = _registryFlushed = _playerWritten = _globalWritten = false; _nativeError = null;
            StatusLabel.Hide();
            if (success) { _lastSave = Time.realtimeSinceStartup; Plugin.Log.LogInfo("Autosave finished; native menu closed"); }
            else if (error != null)
            {
                _lastSave = Time.realtimeSinceStartup;
                Plugin.Log.LogError("Autosave failed: " + error);
                StatusLabel.Show("Autosave failed", 6f, false);
            }
        }

        /// The slot date labels ("save N time" / "load N time") have a fixed box sized for a bare date. Let them shrink the
        /// font to fit so the longer autosave stamp stays on one line. Applied to all slots once per scene (harmless for short text).
        private bool FitSlotLabels()
        {
            int n = 0;
            try
            {
                foreach (var tx in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>())
                {
                    if (tx == null || tx.gameObject.scene != SceneManager.GetActiveScene() || !IsSlotTimeLabel(tx.gameObject.name)) continue;
                    if (!tx.resizeTextForBestFit)
                    {
                        tx.resizeTextForBestFit = true;
                        tx.resizeTextMaxSize = tx.fontSize;
                        tx.resizeTextMinSize = Mathf.Max(8, tx.fontSize / 2);
                        tx.horizontalOverflow = HorizontalWrapMode.Wrap;
                        tx.verticalOverflow = VerticalWrapMode.Truncate;
                    }
                    n++;
                }
                foreach (var tx in Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>())
                {
                    if (tx == null || tx.gameObject.scene != SceneManager.GetActiveScene() || !IsSlotTimeLabel(tx.gameObject.name)) continue;
                    if (!tx.enableAutoSizing)
                    {
                        tx.fontSizeMax = tx.fontSize;
                        tx.fontSizeMin = Mathf.Max(8f, tx.fontSize / 2f);
                        tx.enableAutoSizing = true;
                        tx.enableWordWrapping = false;
                        tx.overflowMode = TMPro.TextOverflowModes.Truncate;
                    }
                    n++;
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("FitSlotLabels failed: " + e); }
            if (n > 0) Plugin.Log.LogInfo("Slot date labels set to best-fit: " + n);
            return n > 0;
        }

        private static bool IsSlotTimeLabel(string name)
        {
            return name != null && name.EndsWith(" time") && (name.StartsWith("save ") || name.StartsWith("load "));
        }

        /// Do what the slot button does for its label, but as "<kind> (Seed: X) date": set the slot's `time` variable and its
        /// on-screen text; persisted via "Save Settings" (key saveTimeN in SaveSettings.es3) so the load menu shows it too.
        private void StampSlotLabel(string file, string kind, bool persist)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(file ?? "", @"(\d+)");
                if (!m.Success) { Plugin.Log.LogWarning("Slot label: cannot read slot number from " + file); return; }
                string n = m.Groups[1].Value;
                string seed = "?";
                try { var sv = _saveLoad.FsmVariables.GetFsmInt("seed"); if (sv != null) seed = sv.Value.ToString(); } catch { }
                string stamp = kind + " (Seed: " + seed + ") " + DateTime.Now.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);   // "/" and ":" are culture separators otherwise (30.09.2026 on many PCs)
                int hits = 0;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.gameObject == null || f.gameObject.scene != SceneManager.GetActiveScene()) continue;
                    if (f.gameObject.name != "save_game_" + n || f.FsmName != "Continue") continue;
                    if (!GoPath(f.transform).Contains("SaveGame_Canvas")) continue;
                    var v = f.FsmVariables.GetFsmString("time");
                    if (v != null) { v.Value = stamp; hits++; }
                    // the label the slot button writes to ("save N time")
                    var slotRoot = f.transform.parent != null ? f.transform.parent : f.transform;
                    foreach (var tx in slotRoot.GetComponentsInChildren<UnityEngine.UI.Text>(true)) if (tx.gameObject.name == "save " + n + " time") { tx.text = stamp; hits++; }
                    foreach (var tx in slotRoot.GetComponentsInChildren<TMPro.TMP_Text>(true)) if (tx.gameObject.name == "save " + n + " time") { tx.text = stamp; hits++; }
                    // persist it the way the game does (slot's saveItemVar FSM: "Save Settings" -> key saveTimeN in SaveSettings.es3);
                    // the autosave path gets this for free from the savegame button's broadcast.
                    if (persist)
                        foreach (var sv2 in f.GetComponents<PlayMakerFSM>()) if (sv2.FsmName == "saveItemVar") { sv2.SendEvent("Save Settings"); hits++; }
                }
                Plugin.Log.LogInfo("Slot " + n + " label -> \"" + stamp + "\" (" + hits + " target(s))");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Slot label update failed: " + e); }
        }

        private static string SafeState(PlayMakerFSM f)
        {
            try { return f.Fsm != null && f.Fsm.Initialized ? f.Fsm.ActiveStateName : ""; } catch { return ""; }
        }

        /// Once a second: locate the game FSMs we need (only rescans what is missing), refresh the cached slot file and
        /// disarm if the slot changed for any reason other than the game loading/saving.
        private void Scan()
        {
            if (!_slotLabelsFitted) _slotLabelsFitted = FitSlotLabels();
            if (!(Alive(_saveLoad) && Alive(_saveButton) && Alive(_menu) && Alive(_inCar) && Alive(_health) && Alive(_sleep) && Alive(_newGoSave)))
            {
                var all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
                foreach (var f in all)
                {
                    if (f == null || f.gameObject == null || f.gameObject.scene != SceneManager.GetActiveScene()) continue;
                    string go = f.gameObject.name, fsm = f.FsmName;
                    if (!Alive(_saveLoad) && go == "SaveLoadGame" && fsm == "SaveLoadGame") _saveLoad = f;
                    else if (!Alive(_saveButton) && go == "savegame" && fsm == "Continue" && GoPath(f.transform).Contains("SaveGame_Canvas")) _saveButton = f;
                    else if (!Alive(_menu) && go == "__GameManager__" && fsm == "Menu") _menu = f;
                    else if (!Alive(_newGoSave) && go == "NewGO_ArrayList" && fsm == "Save_NewGO_ArrayList") _newGoSave = f;
                    else if (go == "Player")   // the player root (parented under the vehicle while driving)
                    {
                        if (!Alive(_inCar) && fsm == "InCar") _inCar = f;
                        else if (!Alive(_health) && fsm == "Health") _health = f;
                        else if (!Alive(_sleep) && fsm == "Sleep") _sleep = f;
                    }
                }
            }
            RefreshSlotFile();
            if (_armed && _slotFile != _armedFile) Disarm("SaveFile changed to " + _slotFile);
        }

        private static string GoPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }
    }

    /// The one hook on PlayMaker's event dispatch (it is hot, so one prefix serves both features):
    ///  * "SaveGame" — the game's save broadcast: earliest reliable moment to make the held item save-safe;
    ///  * "Clicked" on a save button — Save naming may swallow it and ask for a name first.
    /// Fsm.ProcessEvent returns void: returning false skips it, which is what swallows the click.
    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class Fsm_ProcessEvent_Patch
    {
        static bool Prefix(Fsm __instance, FsmEvent fsmEvent)
        {
            if (fsmEvent == null) return true;
            string n = fsmEvent.Name;
            if (n == "SaveGame")
            {
                try { HeldItemKeeper.OnSaveEvent(); }
                catch (Exception e) { Plugin.Log.LogWarning("Held item on SaveGame event: " + e.Message); }
                return true;
            }
            if (n == "Clicked")
            {
                try { return !SaveNaming.Intercept(__instance, fsmEvent); }
                catch (Exception e) { Plugin.Log.LogWarning("Save naming intercept: " + e.Message); }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Fsm), "EnterState")]
    internal static class AutosaveStatePatch
    {
        static void Postfix(Fsm __instance, FsmState __0)
        {
            var runner = Plugin.Current;
            if (runner != null && __0 != null) runner.OnNativeStateEntered(__instance, __0.Name);
            if (__instance.Name == "GrabItem")
            {
                try { HeldItemKeeper.OnGrabState(__instance); }
                catch (Exception e) { Plugin.Log.LogWarning("Vehicle-part guard: " + e.Message); }
            }
        }
    }

    [HarmonyPatch]
    internal static class AutosaveWritePatch
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ES3PlayMaker.SaveAll), "Enter");
            yield return AccessTools.Method(typeof(ES3PlayMaker.Save), "Enter");
            yield return AccessTools.Method(typeof(ES3PlayMaker.StoreCachedFile), "Enter");
        }

        static void Postfix(ES3PlayMaker.SettingsAction __instance)
        {
            var runner = Plugin.Current;
            if (runner == null || !runner.IsAutosaving) return;
            try { runner.OnSaveActionCompleted(__instance); }
            catch (Exception e) { runner.OnSaveActionFailed(e); }
        }

        static Exception Finalizer(Exception __exception)
        {
            var runner = Plugin.Current;
            if (__exception != null && runner != null) runner.OnSaveActionFailed(__exception);
            return __exception;
        }
    }
}
