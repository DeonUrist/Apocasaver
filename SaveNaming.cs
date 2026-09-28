using System;
using System.Text.RegularExpressions;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Apocasaver
{
    /// Optional "Save naming": when the player saves (slot menu button, ESC-menu Yes, or a save point in the world, which
    /// clicks Yes_Save itself), a small popup asks for a name first.
    /// OK replays the game's own click with the name remembered for the slot stamp ("<name> (Seed: X) date"); Cancel aborts
    /// the save (the click never reaches the game). Autosaves keep whatever name the slot already carries.
    internal static class SaveNaming
    {
        private const int MaxLen = 24;
        private static readonly Regex StampRx = new Regex(@"^(.*?)\s*\(Seed: [^)]*\)\s*\d\d/\d\d/\d{4} \d\d:\d\d$");

        // popup state
        private static bool _open;
        private static string _text = "";
        private static Fsm _pendingFsm;
        private static FsmEvent _pendingEvent;
        private static Fsm _allowFsm;           // the replayed click passes the intercept (same FSM, same frame)
        private static int _allowFrame = -1;
        private static float _openedAt;
        private static bool _lockedCursor, _pausedTime;
        private static float _prevTimeScale = 1f;
        private static CursorLockMode _prevLock; private static bool _prevVisible;
        private static bool _eventSystemWasEnabled;
        private static EventSystem _disabledEs;
        private static bool _focusPending;

        /// Name chosen for the save in progress (consumed by the slot stamp); null = no custom name.
        internal static string PendingName;

        /// Custom name carried by a slot label, or null when it is a plain Autosave/Manual/date label.
        internal static string NameFromLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return null;
            var m = StampRx.Match(label.Trim());
            if (!m.Success) return null;
            string n = m.Groups[1].Value.Trim();
            if (n.Length == 0 || string.Equals(n, "Autosave", StringComparison.OrdinalIgnoreCase) || string.Equals(n, "Manual", StringComparison.OrdinalIgnoreCase)) return null;
            return n;
        }

        /// Called from the Fsm.ProcessEvent prefix. Returns true when the event must be swallowed (popup shown instead).
        internal static bool Intercept(Fsm fsm, FsmEvent evt)
        {
            if (!Plugin.SaveNamingEnabled.Value || fsm == null || evt == null || evt.Name != "Clicked") return false;
            if (_allowFsm == fsm && Time.frameCount == _allowFrame) return false;
            var go = fsm.GameObject;
            if (go == null) return false;
            string n = go.name;
            bool slotButton = n.StartsWith("save_game_") && fsm.Name == "Continue";
            bool quickSave = n == "Yes_Save" && fsm.Name == "Continue";
            if (!slotButton && !quickSave) return false;
            if (_open) return true;   // already asking; ignore further clicks

            // default text = the name this slot already carries
            string slot = slotButton ? n.Substring("save_game_".Length) : SlotNumber(Runner.CurrentSaveFile());
            _text = SlotName(slot) ?? "";
            PendingName = null;   // a name from an earlier OK that never turned into a save must not leak into this one
            _pendingFsm = fsm; _pendingEvent = evt;
            Open();
            Plugin.Log.LogInfo("Save naming: asking for a name (" + (slotButton ? "slot " + slot : "quick save, slot " + slot) + ")");
            return true;
        }

        private static string SlotNumber(string file)
        {
            var m = Regex.Match(file ?? "", @"(\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string SlotName(string slot)
        {
            if (string.IsNullOrEmpty(slot)) return null;
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                {
                    if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                    if (f.gameObject.name != "save_game_" + slot || f.FsmName != "Continue") continue;
                    var v = f.FsmVariables.GetFsmString("time");
                    if (v != null) return NameFromLabel(v.Value);
                }
            }
            catch { }
            return null;
        }

        /// Name the autosave should use for this slot file (the slot's existing custom name), or null.
        internal static string NameForAutosave(string file) { return SlotName(SlotNumber(file)); }

        private static void Open()
        {
            _open = true; _focusPending = true; _openedAt = Time.realtimeSinceStartup;
            // Save points in the world (UseSave FSM -> Yes_Save) fire during gameplay: free the cursor and pause while asking.
            try
            {
                _prevLock = Cursor.lockState; _prevVisible = Cursor.visible;
                _lockedCursor = _prevLock != CursorLockMode.None || !_prevVisible;
                if (Time.timeScale > 0f) { _pausedTime = true; _prevTimeScale = Time.timeScale; Time.timeScale = 0f; }   // other mods may run a custom scale
                EnforceCursor();
            }
            catch { }
            try
            {
                var es = EventSystem.current;
                if (es != null && es.enabled) { _disabledEs = es; _eventSystemWasEnabled = true; es.enabled = false; }   // popup clicks must not reach the menu buttons behind it
            }
            catch { }
        }

        private static void Close()
        {
            _open = false;
            try { if (_disabledEs != null && _eventSystemWasEnabled) _disabledEs.enabled = true; } catch { }
            _disabledEs = null; _eventSystemWasEnabled = false;
            try
            {
                if (_pausedTime) { _pausedTime = false; if (Time.timeScale == 0f) Time.timeScale = _prevTimeScale; }
                if (_lockedCursor) { Cursor.lockState = _prevLock; Cursor.visible = _prevVisible; }
                _lockedCursor = false;
            }
            catch { }
        }

        private static void EnforceCursor() { if (_lockedCursor) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }

        /// From the Runner's LateUpdate: keep the cursor free while the popup is open (the game re-locks it every frame).
        internal static void LateUpdate() { if (_open) EnforceCursor(); }

        private static void Confirm()
        {
            string name = Clean(_text);
            PendingName = name.Length > 0 ? name : null;
            var fsm = _pendingFsm; var evt = _pendingEvent;
            Close();
            _pendingFsm = null; _pendingEvent = null;
            if (fsm == null || evt == null) return;
            Plugin.Log.LogInfo("Save naming: OK, name = " + (PendingName ?? "(none)"));
            _allowFsm = fsm; _allowFrame = Time.frameCount;
            try
            {
                fsm.Event(evt);
                // Both save buttons react to Clicked with a global transition into "clicked"; make sure it happened.
                if (fsm.ActiveStateName != "clicked")
                {
                    Plugin.Log.LogWarning("Save naming: replayed click left " + fsm.GameObjectName + " in '" + fsm.ActiveStateName + "', entering 'clicked' directly");
                    fsm.SetState("clicked");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Save naming: replaying the click failed: " + e.Message); }
            _allowFsm = null; _allowFrame = -1;
        }

        private static void Cancel()
        {
            Close();
            _pendingFsm = null; _pendingEvent = null; PendingName = null;
            Plugin.Log.LogInfo("Save naming: cancelled, not saving");
        }

        /// The runner is going away (scene change): close without saving and give the game its time scale and cursor back.
        internal static void Abort() { if (_open) Cancel(); }

        private static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) { if (c == '(' || c == ')' || char.IsControl(c)) continue; sb.Append(c); if (sb.Length >= MaxLen) break; }
            return sb.ToString().Trim();
        }

        // ---- IMGUI popup (drawn by the Runner's OnGUI) ----
        private static GUIStyle _box, _label, _field, _button;

        internal static void OnGUI()
        {
            if (!_open) return;
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.window);
                _label = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
                _field = new GUIStyle(GUI.skin.textField) { fontSize = 18, alignment = TextAnchor.MiddleLeft };
                _button = new GUIStyle(GUI.skin.button) { fontSize = 17 };
            }
            // Enter = OK. Checked before the text field is drawn so the field never sees it. A save point is used with Enter,
            // so presses in the first 0.3 s (the one that opened the popup) are swallowed instead.
            var e = Event.current;
            bool enter = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
            if (enter) { e.Use(); if (Time.realtimeSinceStartup - _openedAt > 0.3f) { Confirm(); return; } }

            float w = 460f, h = 130f;
            var r = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.depth = -1000;
            GUI.Box(r, "", _box);
            GUI.Label(new Rect(r.x + 16, r.y + 12, w - 32, 26), "Save name", _label);
            GUI.SetNextControlName("apocasaver_savename");
            _text = GUI.TextField(new Rect(r.x + 16, r.y + 44, w - 32, 32), _text, MaxLen, _field);
            if (GUI.Button(new Rect(r.x + w - 16 - 200, r.y + h - 44, 96, 32), "OK", _button)) { Confirm(); return; }
            if (GUI.Button(new Rect(r.x + w - 16 - 96, r.y + h - 44, 96, 32), "Cancel", _button)) { Cancel(); return; }
            if (_focusPending) { GUI.FocusControl("apocasaver_savename"); _focusPending = false; }
        }
    }

    /// Intercepts the "Clicked" event of the game's save buttons while Save naming is on.
    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class SaveNaming_ProcessEvent_Patch
    {
        static bool Prefix(Fsm __instance, FsmEvent fsmEvent, ref bool __result)
        {
            try
            {
                if (SaveNaming.Intercept(__instance, fsmEvent)) { __result = true; return false; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Save naming intercept: " + e.Message); }
            return true;
        }
    }
}
