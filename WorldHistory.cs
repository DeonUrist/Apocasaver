using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ES3Internal;
using HarmonyLib;
using UnityEngine;

namespace Apocasaver
{
    internal static class WorldHistory
    {
        internal static bool Capturing, Restoring;
        private const string CaptureFile = "apocasaver-history-capture.es3";
        private static Exception _error;
        private static bool _installed;
        private static readonly Dictionary<string, PlayMakerFSM> Templates = new Dictionary<string, PlayMakerFSM>();
        // Fallback for callers without a pool prototype. Do not pin unrelated loaded assets.
        private static readonly Dictionary<string, WeakReference> Prefabs = new Dictionary<string, WeakReference>();
        private static FieldInfo _instances, _prototype, _prefab;
        private static ES3File _restoreCache;
        internal static void HistoryChanged() { _restoreCache = null; }
        internal static void Reset()
        { Templates.Clear(); Prefabs.Clear(); HistoryChanged(); }
        internal static void Install()
        {
            if (_installed) return;
            var tile = AccessTools.TypeByName("MapMagic.Terrains.TerrainTile");
            var pool = AccessTools.TypeByName("Den.Tools.ObjectsPool+Pool");
            if (tile == null || pool == null) throw new MissingMemberException("Terrain history bindings unavailable");
            _instances = AccessTools.Field(pool, "instances"); _prototype = AccessTools.Field(pool, "prototype");
            _prefab = _prototype == null ? null : AccessTools.Field(_prototype.FieldType, "prefab");
            if (_instances == null || _prototype == null || _prefab == null) throw new MissingFieldException("Terrain pool prototype bindings unavailable");
            var harmony = new Harmony(Plugin.GUID + ".history");
            foreach (string name in new[] { "Move", "Remove", "ResetTerrain" })
            {
                Plugin.Log.LogDebug("Installing terrain hook " + name);
                harmony.Patch(AccessTools.Method(tile, name), prefix: new HarmonyMethod(typeof(WorldHistory), nameof(TileBefore)));
            }
            harmony.Patch(AccessTools.Method(pool, "MoveItems"), prefix: new HarmonyMethod(typeof(WorldHistory), nameof(PoolBefore)));
            harmony.Patch(AccessTools.Method(pool, "ClampCount"), prefix: new HarmonyMethod(typeof(WorldHistory), nameof(PoolClampBefore)));
            harmony.Patch(AccessTools.Method(pool, "MoveItems"), postfix: new HarmonyMethod(typeof(WorldHistory), nameof(PoolAfter)));
            _installed = true;
        }
        private static void TileBefore(Component __instance) { if (__instance != null) Capture(__instance.gameObject); }
        private static void PoolBefore(object __instance, int startNum, int endNum)
        {
            if (!SaveCoordinator.HasSession || Capturing || Restoring || startNum >= endNum) return;
            var objects = (IList)_instances.GetValue(__instance);
            for (int i = Math.Max(0, startNum); i < Math.Min(endNum, objects.Count); i++) Capture(objects[i] as GameObject);
        }
        private static void PoolClampBefore(object __instance, int targetCount)
        {
            if (!SaveCoordinator.HasSession || Capturing || Restoring) return;
            var objects = (IList)_instances.GetValue(__instance);
            for (int i = Math.Max(0, targetCount); i < objects.Count; i++) Capture(objects[i] as GameObject);
        }
        private static void PoolAfter(object __instance, int startNum, int endNum)
        {
            if (!SaveCoordinator.HasSession || Capturing || Restoring || startNum >= endNum) return;
            var objects = (IList)_instances.GetValue(__instance);
            var prefab = _prefab.GetValue(_prototype.GetValue(__instance)) as GameObject;
            for (int i = Math.Max(0, startNum); i < Math.Min(endNum, objects.Count); i++) RestoreFromPrefab(objects[i] as GameObject, prefab);
        }
        internal static void ReportError(Exception error) { if (Capturing || Restoring) _error = error; }
        internal static void Record(string path, string key)
        {
            if (path != CaptureFile) return;
            ES3File file; ES3Data data;
            if (ES3File.cachedFiles.TryGetValue(CaptureFile, out file) && SaveRecords.Data(file).TryGetValue(key, out data)) SaveCoordinator.CaptureHistory(key, data);
        }
        private static PlayMakerFSM[] SiteFsms(GameObject root)
        {
            return root.GetComponentsInChildren<PlayMakerFSM>(true).Where(f => f != null && f.enabled && f.Fsm.Initialized &&
                f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "Save") &&
                f.Fsm.States.Any(s => s.Actions.Any(a => a is ES3PlayMaker.Save || a is ES3PlayMaker.SaveAll))).ToArray();
        }
        internal static void Capture(GameObject root)
        {
            if (root == null || !SaveCoordinator.HasSession || Capturing || Restoring) return;
            var fsms = SiteFsms(root); if (fsms.Length == 0) return;
            Run(fsms, "Save", false);
            if (SaveCoordinator.Active) SaveCoordinator.Diagnostic("Terrain locations changed during saving; retaining history and skipping cleanup");
        }
        private static void Restore(GameObject root)
        { RestoreFromPrefab(root, null); }
        private static void RestoreFromPrefab(GameObject root, GameObject prefab)
        {
            if (root == null || !SaveCoordinator.HasSession || Capturing || Restoring) return;
            var fsms = SiteFsms(root).Where(f => f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "Load")).ToArray();
            if (fsms.Length == 0) return;
            if (prefab == null) prefab = Prefab(BaseName(root.name));
            foreach (var f in fsms) ResetTemplate(f, root, prefab);
            Run(fsms, "Load", true);
        }
        private static string BaseName(string name)
        { int clone = name.IndexOf("(Clone)", StringComparison.Ordinal); return clone < 0 ? name : name.Substring(0, clone); }
        private static void ResetTemplate(PlayMakerFSM fsm, GameObject root, GameObject prefab)
        {
            // A missing root asset can appear later; only memoize missing children/FSMs of a known prefab.
            if (prefab == null) return;
            string relative = ""; for (var t = fsm.transform; t != null && t != root.transform; t = t.parent) relative = "/" + t.name + relative;
            string key = prefab.GetInstanceID() + relative + "/" + fsm.FsmName;
            PlayMakerFSM template;
            // Runtime-spawned children often have no prefab counterpart. Memoize those misses,
            // while retrying a template whose Unity component has since been destroyed.
            if (!Templates.TryGetValue(key, out template) || (!ReferenceEquals(template, null) && template == null))
            {
                var go = relative.Length == 0 ? prefab.transform : prefab.transform.Find(relative.TrimStart('/'));
                template = go == null ? null : go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == fsm.FsmName);
                Templates[key] = template;
            }
            if (template == null) return;
            foreach (var v in fsm.FsmVariables.IntVariables) { var def = template.FsmVariables.GetFsmInt(v.Name); if (def != null) v.Value = def.Value; }
            foreach (var v in fsm.FsmVariables.FloatVariables) { var def = template.FsmVariables.GetFsmFloat(v.Name); if (def != null) v.Value = def.Value; }
            foreach (var v in fsm.FsmVariables.BoolVariables) { var def = template.FsmVariables.GetFsmBool(v.Name); if (def != null) v.Value = def.Value; }
        }
        private static GameObject Prefab(string name)
        {
            WeakReference cached;
            GameObject prefab;
            if (Prefabs.TryGetValue(name, out cached) && (prefab = cached.Target as GameObject) != null) return prefab;
            Prefabs.Clear();
            foreach (var g in Resources.FindObjectsOfTypeAll<GameObject>())
                if (g != null && !g.scene.IsValid() && g.transform.parent == null && !Prefabs.ContainsKey(g.name)) Prefabs[g.name] = new WeakReference(g);
            return Prefabs.TryGetValue(name, out cached) ? cached.Target as GameObject : null;
        }
        private static void Run(PlayMakerFSM[] fsms, string evt, bool restoring)
        {
            string path = ES3Settings.defaultSettings.path; var location = ES3Settings.defaultSettings.location;
            try
            {
                _error = null; Capturing = !restoring; Restoring = restoring;
                if (restoring)
                {
                    // ES3 marks saves/deletes dirty. Rebuild if a Load event wrote into this otherwise read-only snapshot.
                    if (_restoreCache == null || SaveRecords.IsDirty(_restoreCache))
                        _restoreCache = SaveRecords.ReplaceCache(CaptureFile, SaveCoordinator.History, false);
                    else ES3File.cachedFiles[CaptureFile] = _restoreCache;
                }
                else SaveRecords.ReplaceCache(CaptureFile, new Dictionary<string, ES3Data>());
                ES3Settings.defaultSettings.path = CaptureFile; ES3Settings.defaultSettings.location = ES3.Location.Cache;
                foreach (var fsm in fsms) fsm.SendEvent(evt);
                if (_error != null) throw _error;
            }
            catch (Exception e) { SaveCoordinator.Diagnostic("Location history capture/restore unavailable: " + e); }
            finally { ES3Settings.defaultSettings.path = path; ES3Settings.defaultSettings.location = location; ES3File.cachedFiles.Remove(CaptureFile); Capturing = Restoring = false; }
        }
    }
}
