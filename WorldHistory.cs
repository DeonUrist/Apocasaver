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
        internal static void Install()
        {
            if (_installed) return;
            var tile = AccessTools.TypeByName("MapMagic.Terrains.TerrainTile");
            var pool = AccessTools.TypeByName("Den.Tools.ObjectsPool+Pool");
            if (tile == null || pool == null) throw new MissingMemberException("Terrain history bindings unavailable");
            var harmony = new Harmony(Plugin.GUID + ".history");
            foreach (string name in new[] { "Move", "Remove", "ResetTerrain" })
            {
                Plugin.Log.LogDebug("Installing terrain hook " + name);
                harmony.Patch(AccessTools.Method(tile, name), prefix: new HarmonyMethod(typeof(WorldHistory), nameof(TileBefore)));
            }
            foreach (string name in new[] { "MoveItems", "ClampCount" })
                harmony.Patch(AccessTools.Method(pool, name), prefix: new HarmonyMethod(typeof(WorldHistory), nameof(PoolBefore)));
            harmony.Patch(AccessTools.Method(pool, "MoveItems"), postfix: new HarmonyMethod(typeof(WorldHistory), nameof(PoolAfter)));
            _installed = true;
        }
        private static void TileBefore(Component __instance) { if (__instance != null) Capture(__instance.gameObject); }
        private static IEnumerable<GameObject> PoolObjects(object pool)
        { return ((IEnumerable)AccessTools.Field(pool.GetType(), "instances").GetValue(pool)).Cast<GameObject>().Where(g => g != null).ToArray(); }
        private static void PoolBefore(object __instance) { foreach (var root in PoolObjects(__instance)) Capture(root); }
        private static void PoolAfter(object __instance) { foreach (var root in PoolObjects(__instance)) Restore(root); }
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
            if (SaveCoordinator.Active) SaveCoordinator.ReportError(new InvalidOperationException("Terrain locations changed during saving; history was preserved, try saving again"));
        }
        private static void Restore(GameObject root)
        {
            if (root == null || !SaveCoordinator.HasSession || Capturing || Restoring) return;
            var fsms = SiteFsms(root).Where(f => f.Fsm.GlobalTransitions.Any(t => t.FsmEvent.Name == "Load")).ToArray();
            if (fsms.Length == 0) return;
            foreach (var f in fsms) ResetTemplate(f, root);
            Run(fsms, "Load", true);
        }
        private static void ResetTemplate(PlayMakerFSM fsm, GameObject root)
        {
            string relative = ""; for (var t = fsm.transform; t != null && t != root.transform; t = t.parent) relative = "/" + t.name + relative;
            string baseName = root.name.Split(new[] { "(Clone)" }, StringSplitOptions.None)[0], key = baseName + relative + "/" + fsm.FsmName;
            PlayMakerFSM template;
            if (!Templates.TryGetValue(key, out template) || template == null)
            {
                var prefab = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g != null && !g.scene.IsValid() && g.transform.parent == null && g.name == baseName);
                var go = prefab == null ? null : relative.Length == 0 ? prefab.transform : prefab.transform.Find(relative.TrimStart('/'));
                template = go == null ? null : go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == fsm.FsmName);
                Templates[key] = template;
            }
            if (template == null) return;
            foreach (var v in fsm.FsmVariables.IntVariables) { var def = template.FsmVariables.GetFsmInt(v.Name); if (def != null) v.Value = def.Value; }
            foreach (var v in fsm.FsmVariables.FloatVariables) { var def = template.FsmVariables.GetFsmFloat(v.Name); if (def != null) v.Value = def.Value; }
            foreach (var v in fsm.FsmVariables.BoolVariables) { var def = template.FsmVariables.GetFsmBool(v.Name); if (def != null) v.Value = def.Value; }
        }
        private static void Run(PlayMakerFSM[] fsms, string evt, bool restoring)
        {
            string path = ES3Settings.defaultSettings.path; var location = ES3Settings.defaultSettings.location;
            try
            {
                _error = null; Capturing = !restoring; Restoring = restoring;
                SaveRecords.ReplaceCache(CaptureFile, restoring ? SaveCoordinator.History : new Dictionary<string, ES3Data>());
                ES3Settings.defaultSettings.path = CaptureFile; ES3Settings.defaultSettings.location = ES3.Location.Cache;
                foreach (var fsm in fsms) fsm.SendEvent(evt);
                if (_error != null) throw _error;
            }
            catch (Exception e) { Plugin.Log.LogError("Location history capture/restore failed: " + e); SaveCoordinator.RecoveryRequired = true; }
            finally { ES3Settings.defaultSettings.path = path; ES3Settings.defaultSettings.location = location; ES3File.cachedFiles.Remove(CaptureFile); Capturing = Restoring = false; }
        }
    }
}
