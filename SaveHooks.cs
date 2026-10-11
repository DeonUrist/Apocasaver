using System;
using System.Collections.Generic;
using System.Reflection;
using ES3Internal;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocasaver
{
    internal static class SaveHooks
    {
        private static HashSet<GameObject> _roots = new HashSet<GameObject>();
        internal static void RefreshRoots(HashSet<GameObject> roots) { _roots = roots; }
        internal static bool Dynamic(GameObject go)
        {
            for (var t = go == null ? null : go.transform; t != null; t = t.parent)
                if (_roots.Contains(t.gameObject) || (t.name.Contains("(Clone)") &&
                    (t.GetComponent<ES3Prefab>() != null || Array.Exists(t.GetComponents<PlayMakerFSM>(), f => f.FsmName == "RpmGear" || f.FsmName == "TrailerAttached")))) return true;
            return false;
        }
        internal static RecordKind Kind(GameObject go, string key)
        {
            if (go != null && Dynamic(go)) return RecordKind.Dynamic;
            for (var t = go == null ? null : go.transform; t != null; t = t.parent) if (t.tag == "SpawnerStatic") return RecordKind.History;
            return SaveRecords.PositionKey(key) ? RecordKind.History : RecordKind.Session;
        }
        internal static string Key(ES3PlayMaker.SettingsAction action)
        { var save = action as ES3PlayMaker.Save; if (save != null) return save.key.Value; var all = action as ES3PlayMaker.SaveAll; return all == null ? null : all.key.Value; }
        internal static string StableHinge(Fsm fsm)
        {
            var go = fsm.GameObject;
            if (fsm.Name != "saveItemVar" || go == null || !go.name.StartsWith("hinge_", StringComparison.Ordinal) || !Dynamic(go)) return null;
            var mgr = ES3ReferenceMgrBase.Current; if (mgr == null) throw new InvalidOperationException("Easy Save reference manager unavailable for vehicle hinge");
            long id = mgr.Get(go); if (id < 0) id = mgr.Add(go);
            return "apocasaver.object." + id + ".hinge-rotation";
        }
    }
    [HarmonyPatch(typeof(ES3Settings), "get_FullPath")]
    internal static class SavePathPatch
    {
        static bool Prefix(ES3Settings __instance, ref string __result)
        { string path = SaveCoordinator.Redirect(__instance); if (path == null) return true; __result = path; return false; }
    }
    [HarmonyPatch]
    internal static class SaveFilePatch
    {
        static MethodBase TargetMethod() { return AccessTools.Method(typeof(ES3).Assembly.GetType("ES3Internal.ES3IO", true), "CommitBackup"); }
        static void Postfix(ES3Settings settings) { SaveCoordinator.FileWritten(settings); }
        static Exception Finalizer(Exception __exception) { if (__exception != null) SaveCoordinator.ReportDiskError(__exception); return __exception; }
    }
    [HarmonyPatch]
    internal static class SaveActionPatch
    {
        internal sealed class State { internal FsmString OldKey; internal ES3PlayMaker.SettingsAction Action; internal bool Entered; }
        static IEnumerable<MethodBase> TargetMethods()
        { yield return AccessTools.Method(typeof(ES3PlayMaker.Save), "Enter"); yield return AccessTools.Method(typeof(ES3PlayMaker.SaveAll), "Enter"); }
        static bool Prefix(ES3PlayMaker.SettingsAction __instance, out State __state)
        {
            __state = new State { Action = __instance };
            if (SaveCoordinator.DeferredLabel(__instance) || SaveCoordinator.Excluded(__instance.Fsm.GameObject)) return false;
            if (SaveCoordinator.WriteFailed && !WorldHistory.Capturing) return false;
            var save = __instance as ES3PlayMaker.Save;
            if (save != null && save.value.Type == VariableType.Vector3)
            {
                try
                {
                    string stable = SaveHooks.StableHinge(save.Fsm);
                    if (stable != null) { __state.OldKey = save.key; save.key = new FsmString { Value = stable }; }
                }
                catch (Exception e) { SaveCoordinator.Diagnostic("Stable hinge key unavailable; using native key: " + e); }
            }
            __state.Entered = true; return true;
        }
        static void Postfix(ES3PlayMaker.SettingsAction __instance, State __state)
        { if (__state != null && __state.Entered) { try { SaveCoordinator.Written(__instance); } catch (Exception e) { SaveCoordinator.Diagnostic("Save write tracking unavailable: " + e); } } }
        static Exception Finalizer(Exception __exception, State __state)
        {
            if (__state != null && __state.OldKey != null) ((__state.Action as ES3PlayMaker.Save)).key = __state.OldKey;
            if (__exception != null) { WorldHistory.ReportError(__exception); SaveCoordinator.ReportError(__exception); }
            return __exception;
        }
    }
    [HarmonyPatch]
    internal static class SaveLoadKeyPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        { yield return AccessTools.Method(typeof(ES3PlayMaker.Load), "Enter"); yield return AccessTools.Method(typeof(ES3PlayMaker.LoadAll), "Enter"); }
        static bool Prefix(ES3PlayMaker.SettingsAction __instance, out FsmString __state)
        {
            __state = null; var load = __instance as ES3PlayMaker.Load;
            if (load != null && load.value.Type == VariableType.Vector3)
            {
                try
                {
                    string stable = SaveHooks.StableHinge(load.Fsm);
                    if (stable != null && ES3.KeyExists(stable, load.GetSettings())) { __state = load.key; load.key = new FsmString { Value = stable }; }
                }
                catch (Exception e) { SaveCoordinator.Diagnostic("Stable hinge load key unavailable; using native key: " + e); }
            }
            if (WorldHistory.Restoring)
            {
                string key = load != null ? load.key.Value : ((__instance as ES3PlayMaker.LoadAll)).key.Value;
                return ES3.KeyExists(key, __instance.GetSettings());
            }
            return true;
        }
        static Exception Finalizer(ES3PlayMaker.SettingsAction __instance, FsmString __state, Exception __exception)
        { if (__state != null) ((__instance as ES3PlayMaker.Load)).key = __state; return __exception; }
    }
}
