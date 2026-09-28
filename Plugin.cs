using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        public const string VERSION = "1.6.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IntervalMinutes;
        internal static ConfigEntry<int> WarningSeconds;
        internal static ConfigEntry<bool> SaveNamingEnabled;
        internal static Runner Current;
        internal static ConfigEntry<bool> KeepHeldItem, SaveFix, MenuClickFix, VehiclePartFix, VerboseHeld;
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Enable autosave.");
            IntervalMinutes = Config.Bind("General", "IntervalMinutes", 10f,
                new ConfigDescription("Minutes between autosaves. The game is saved over the slot this character was last saved to (or loaded from). " +
                                      "If you are in a vehicle when the time is up, the autosave happens as soon as you get out.",
                                      new AcceptableValueRange<float>(1f, 120f)));
            SaveNamingEnabled = Config.Bind("General", "Save naming", false,
                "Ask for a name when you save (slot menu or ESC menu). The name replaces \"Manual\" in the slot label and autosaves keep it. Cancel aborts the save.");
            WarningSeconds = Config.Bind("General", "Autosave warning (sec)", 30,
                new ConfigDescription("Show \"Autosave in X sec\" this many seconds before an autosave, then again every 15 seconds. 0 = no warning.",
                                      new AcceptableValueRange<int>(0, 300)));

            KeepHeldItem = Config.Bind("HeldItem", "KeepHeldItem", true, "Remember the item in your hand when the game is saved and put it back in your hand after loading.");
            SaveFix = Config.Bind("HeldItem", "SaveFix", true, "Fix the vanilla bug where an item saved while held falls through the world after loading (its colliders are made solid for the duration of the save).");
            MenuClickFix = Config.Bind("HeldItem", "MenuClickFix", true, "Fix the vanilla bug where clicking any pause-menu button (e.g. Save) drops the item in your hand.");
            VehiclePartFix = Config.Bind("HeldItem", "VehiclePartFix", true, "Fix the vanilla bug where a held vehicle part (cassette, radio, headlight...) is dropped when the vehicle camera is switched to third person and back.");
            VerboseHeld = Config.Bind("HeldItem", "VerboseLog", false, "Log the held-item bookkeeping in detail.");

            try { new Harmony(GUID).PatchAll(typeof(Plugin).Assembly); }
            catch (Exception e) { Logger.LogError("Harmony patching failed (held-item fixes inactive): " + e); }

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureRunner("Awake");
            Log.LogInfo(NAME + " " + VERSION + " loaded. Autosave every " + IntervalMinutes.Value + " min.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_runnerGo != null) { var r = _runnerGo.GetComponent<Runner>(); if (r != null) { r.Disarm("scene loaded: " + scene.name); r.ResetSlotLabelFit(); } }
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
    internal class Runner : MonoBehaviour
    {
        private void Awake() { Plugin.Current = this; }
        private void OnGUI() { SaveNaming.OnGUI(); }
        private void LateUpdate() { SaveNaming.LateUpdate(); }

        // Game FSMs we watch / drive.
        private PlayMakerFSM _saveLoad;   // SaveLoadGame [SaveLoadGame]  — SaveFile var, state isPlay/SaveGame/LoadGame
        private PlayMakerFSM _saveButton; // SaveGame_Canvas/SaveGame/savegame [Continue] — the game's own "save now" button
        private PlayMakerFSM _menu;       // __GameManager__ [Menu] — play/pause
        private PlayMakerFSM _inCar;      // Player [InCar] — OnFoot/InCar
        private PlayMakerFSM _health;     // Player [Health] — playerHealth/playerDeath
        private PlayMakerFSM _sleep;      // Player [Sleep] — Awake/...
        private float _nextScan;

        private bool _armed;              // saw the game load or save this session → SaveFile is this character's slot
        private float _lastSave;          // realtime of the last save (ours or the game's)
        private float _onFootSince;       // realtime when the player last got out of a car
        private bool _wasOnFoot;
        private string _lastState = "";
        private bool _saving;
        private float _savingSince;
        private bool _dumped;

        private static bool Alive(PlayMakerFSM f) { return f != null && f.gameObject != null; }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            StatusLabel.Tick();
            try { HeldItemKeeper.Tick(); } catch (Exception e) { if (Time.frameCount % 600 == 0) Plugin.Log.LogWarning("HeldItemKeeper: " + e.Message); }
            if (now >= _nextScan) { _nextScan = now + 1f; Scan(); }
            if (!Alive(_saveLoad)) { Disarm("SaveLoadGame gone"); _lastState = ""; return; }

            // The slot the game will write to. If it changes for any reason other than the game loading/saving, disarm.
            string file = SaveFileName();
            if (file != _armedFile && _armed) Disarm("SaveFile changed to " + file);

            // Watch the game's own save/load flow.
            string st = SafeState(_saveLoad);
            if (st != _lastState)
            {
                if (st == "SaveGame")
                {
                    _lastSave = now; Arm("game saved");
                    if (!_saving)   // the player saved (slot menu or ESC quick save)
                    {
                        string kind = Plugin.SaveNamingEnabled.Value && SaveNaming.PendingName != null ? SaveNaming.PendingName : "Manual";
                        SaveNaming.PendingName = null;
                        StampSlotLabel(SaveFileName(), kind, true);
                    }
                }
                else if (st == "LoadGame" || st == "generateTerrain 2" || st == "LoadVar") { _lastSave = now; Arm("game loaded"); }
                else if (st == "Start" || st == "setSeed" || st == "generateTerrain") Disarm("new game (" + st + ")");
                if (st == "isPlay" && !_armed && (_lastState == "play" || _lastState == "")) _lastSave = now; // baseline for the "no slot" reminder
                if (_saving && st == "isPlay" && _lastState == "SaveGame") { _saving = false; StatusLabel.Hide(); Plugin.Log.LogInfo("Autosave finished"); }
                _lastState = st;
            }
            if (_saving && now - _savingSince > 30f) { _saving = false; StatusLabel.Hide(); Plugin.Log.LogWarning("Autosave: no SaveGame state seen within 30s, giving up on this one"); }

            bool onFoot = Alive(_inCar) && SafeState(_inCar) == "OnFoot";
            if (onFoot && !_wasOnFoot) _onFootSince = now;
            _wasOnFoot = onFoot;

            if (!Plugin.Enabled.Value || _saving) return;
            float remaining = Plugin.IntervalMinutes.Value * 60f - (now - _lastSave);
            if (remaining > 0f)
            {
                WarnCountdown(remaining);
                return;
            }

            if (!_armed)
            {
                // Time for an autosave but this character has never been saved (or loaded): tell the player, retry next interval.
                if (!InPlay()) return;
                _lastSave = now;
                StatusLabel.Show("Cannot autosave, save at least once", 6f, false);
                Plugin.Log.LogInfo("Autosave skipped: no save slot for this character yet");
                return;
            }

            if (!CanSaveNow(now, onFoot)) return;
            TryAutosave(now);
        }

        // ---- "Autosave in X sec" countdown: at WarningSeconds, then every 15 s, never at 0 ----
        private float _warnCycle = -1f;   // _lastSave value the current countdown belongs to
        private int _warnNext;            // next threshold (seconds) to announce

        private void WarnCountdown(float remaining)
        {
            if (!_armed) return;
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

        private string _armedFile;        // SaveFile value at the moment we armed

        internal void Disarm(string why)
        {
            if (_armed) Plugin.Log.LogInfo("Autosave disarmed (" + why + ")");
            _armed = false; _armedFile = null;
        }

        private void Arm(string why)
        {
            _armedFile = SaveFileName();
            if (string.IsNullOrEmpty(_armedFile)) { Disarm("empty SaveFile"); return; }
            if (!_armed) Plugin.Log.LogInfo("Autosave armed (" + why + "), slot file = " + _armedFile);
            _armed = true;
            if (!_dumped) { _dumped = true; DumpSaveFsms(); }
        }

        /// The slot file the game will actually write to: Easy Save's default path, which the slot buttons set through
        /// ES3SettingsMod.SetSavePath (SaveLoadGame's own SaveFile variable is only updated on load, not on save-to-other-slot).
        /// Current slot file as the game will write it (ES3 default path), or null.
        internal static string CurrentSaveFile()
        {
            var r = Plugin.Current;
            return r != null ? r.SaveFileName() : null;
        }

        internal string SaveFileName()
        {
            try
            {
                var p = ES3Settings.defaultSettings != null ? ES3Settings.defaultSettings.path : null;
                if (!string.IsNullOrEmpty(p)) return Path.GetFileName(p);
            }
            catch (Exception e) { if (!_warnedEs3) { _warnedEs3 = true; Plugin.Log.LogWarning("ES3Settings.defaultSettings unavailable, falling back to SaveLoadGame.SaveFile: " + e.Message); } }
            try { var v = _saveLoad.FsmVariables.GetFsmString("SaveFile"); return v != null ? v.Value : null; } catch { return null; }
        }
        private bool _warnedEs3;

        private static bool SlotFileExists(string file)
        {
            try { if (ES3.FileExists(file)) return true; } catch { }
            try { return File.Exists(Path.Combine(Application.persistentDataPath, file)); } catch { return false; }
        }

        private bool CanSaveNow(float now, bool onFoot)
        {
            if (!InPlay()) return false;
            if (!onFoot || now - _onFootSince < 2f) return false;                    // in a car (or just got out)
            if (Alive(_sleep) && SafeState(_sleep) != "Awake") return false;         // sleeping
            if (!Alive(_saveButton)) return false;
            string file = SaveFileName();
            if (string.IsNullOrEmpty(file) || file != _armedFile) return false;
            if (!SlotFileExists(file)) return false;
            return true;
        }

        private float _dropWaitUntil = -1f;   // a held crate was dropped; save once it has left the hand

        private void TryAutosave(float now)
        {
            // Holding a crate/box: drop it (vanilla drop) and save half a second later, so the crate and its contents are
            // saved as world items instead of being handled by the held-item code.
            try { if (HeldItemKeeper.DropHeldContainerForSave()) { _dropWaitUntil = now + 0.5f; return; } }
            catch (Exception e) { Plugin.Log.LogWarning("Crate drop before autosave failed: " + e.Message); }
            if (now < _dropWaitUntil) return;
            _dropWaitUntil = -1f;

            string file = SaveFileName();
            Plugin.Log.LogInfo("Autosaving to " + file + " ...");
            try
            {
                string kind = "Autosave";
                if (Plugin.SaveNamingEnabled.Value) { var keep = SaveNaming.NameForAutosave(file); if (keep != null) kind = keep; }
                StampSlotLabel(file, kind, false);
                _saveButton.SendEvent("Clicked");
                _saving = true; _savingSince = now; _lastSave = now;
                StatusLabel.Show("Autosaving", 30f, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Autosave failed: " + e);
                _lastSave = now; // don't retry every frame
            }
        }

        private bool _slotLabelsFitted;
        internal void ResetSlotLabelFit() { _slotLabelsFitted = false; }

        /// The slot date labels ("save N time" / "load N time") have a fixed box sized for a bare date. Let them shrink the
        /// font to fit so the longer autosave stamp stays on one line. Applied to all slots once per scene (harmless for short text).
        private bool FitSlotLabels()
        {
            int n = 0;
            try
            {
                foreach (var tx in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>())
                {
                    if (tx == null || !tx.gameObject.scene.IsValid() || !IsSlotTimeLabel(tx.gameObject.name)) continue;
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
                    if (tx == null || !tx.gameObject.scene.IsValid() || !IsSlotTimeLabel(tx.gameObject.name)) continue;
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
                string stamp = kind + " (Seed: " + seed + ") " + DateTime.Now.ToString("dd/MM/yyyy HH:mm");
                int hits = 0;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
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

        /// Locate the game FSMs we need (cheap enough once a second; only rescans what is missing).
        private void Scan()
        {
            if (!_slotLabelsFitted) _slotLabelsFitted = FitSlotLabels();
            if (Alive(_saveLoad) && Alive(_saveButton) && Alive(_menu) && Alive(_inCar) && Alive(_health) && Alive(_sleep)) return;
            var all = Resources.FindObjectsOfTypeAll<PlayMakerFSM>();
            foreach (var f in all)
            {
                if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                string go = f.gameObject.name, fsm = f.FsmName;
                if (!Alive(_saveLoad) && go == "SaveLoadGame" && fsm == "SaveLoadGame") _saveLoad = f;
                else if (!Alive(_saveButton) && go == "savegame" && fsm == "Continue" && GoPath(f.transform).Contains("SaveGame_Canvas")) _saveButton = f;
                else if (!Alive(_menu) && go == "__GameManager__" && fsm == "Menu") _menu = f;
                else if (go == "Player" && f.transform.parent == null)
                {
                    if (!Alive(_inCar) && fsm == "InCar") _inCar = f;
                    else if (!Alive(_health) && fsm == "Health") _health = f;
                    else if (!Alive(_sleep) && fsm == "Sleep") _sleep = f;
                }
            }
        }

        private static string GoPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        // ---- one-time diagnostic dump of the save-related FSMs (log only) ----
        private void DumpSaveFsms()
        {
            try
            {
                var sb = new StringBuilder("\n==== save FSM dump ====\n");
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                    string p = GoPath(f.transform);
                    bool want = (f.gameObject.name == "SaveLoadGame") || (f.gameObject.name == "savegame") ||
                                (f.gameObject.name == "Yes_Save") || (f.gameObject.name == "Save_Game" && p.Contains("MainMenu")) ||
                                (p.Contains("SaveGame_Canvas") && f.gameObject.name == "save_game_1");
                    if (!want) continue;
                    sb.Append("== ").Append(p).Append("  [").Append(f.FsmName).Append("]  active=").Append(f.gameObject.activeInHierarchy).Append(" state=").Append(SafeState(f)).Append('\n');
                    if (f.Fsm == null || !f.Fsm.Initialized) continue;
                    foreach (var v in f.FsmVariables.GetAllNamedVariables()) sb.Append("   var ").Append(v.Name).Append(" = ").Append(Fmt(v, 0)).Append('\n');
                    foreach (var s in f.FsmStates)
                    {
                        sb.Append("   state ").Append(s.Name).Append('\n');
                        foreach (var a in s.Actions)
                        {
                            sb.Append("      ").Append(a.GetType().Name).Append(": ");
                            foreach (var fi in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                            {
                                object val = null; try { val = fi.GetValue(a); } catch { }
                                sb.Append(fi.Name).Append('=').Append(Fmt(val, 0)).Append("; ");
                            }
                            sb.Append('\n');
                        }
                    }
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e) { Plugin.Log.LogWarning("dump failed: " + e); }
        }

        private static string Fmt(object v, int depth)
        {
            if (v == null) return "null";
            if (v is string) return "\"" + v + "\"";
            if (v is FsmEvent) return "event:" + ((FsmEvent)v).Name;
            if (v is FsmString) return "\"" + ((FsmString)v).Value + "\"" + (((FsmString)v).UsesVariable ? "{" + ((FsmString)v).Name + "}" : "");
            if (v is FsmBool) return ((FsmBool)v).Value.ToString();
            if (v is FsmFloat) return ((FsmFloat)v).Value.ToString();
            if (v is FsmInt) return ((FsmInt)v).Value.ToString();
            if (v is FsmGameObject) { var g = ((FsmGameObject)v).Value; return "GO:" + (g != null ? g.name : "null") + "{" + ((FsmGameObject)v).Name + "}"; }
            if (v is FsmOwnerDefault) { var od = (FsmOwnerDefault)v; return od.OwnerOption == OwnerDefaultOption.UseOwner ? "Owner" : Fmt(od.GameObject, depth + 1); }
            if (v is FsmEventTarget) { var t = (FsmEventTarget)v; return "target(" + t.target + " go=" + Fmt(t.gameObject, depth + 1) + " fsm=" + Fmt(t.fsmName, depth + 1) + " excludeSelf=" + Fmt(t.excludeSelf, depth + 1) + " children=" + Fmt(t.sendToChildren, depth + 1) + ")"; }
            if (v is NamedVariable) return "{" + ((NamedVariable)v).Name + "}";
            if (v is UnityEngine.Object) return v.GetType().Name + ":" + ((UnityEngine.Object)v).name;
            if (v is Array) { var arr = (Array)v; var sb = new StringBuilder("["); int n = 0; foreach (var e in arr) { if (n++ > 0) sb.Append(','); if (n > 8) { sb.Append("..."); break; } sb.Append(Fmt(e, depth + 1)); } return sb.Append(']').ToString(); }
            return v.ToString();
        }
    }
}
