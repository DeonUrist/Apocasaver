using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Apocasaver;
using ES3Internal;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

public partial class NativeSaveRunner
{
    static int _captures, _loads, _snapshots, _indexes, _registryReads, _poseCommits;
    static string _poseFile;
    static void CountCache(string path) { if (WorldHistory.Restoring) _snapshots++; }
    static void CountIndex() { _indexes++; }
    static void CountRegistry() { _registryReads++; }
    static void CountCommit(ES3Settings settings) { if (_poseFile != null && settings.path == _poseFile && settings.location == ES3.Location.File) _poseCommits++; }
    class CountSite : FsmStateAction
    {
        internal bool Loading;
        public override void OnEnter() { if (Loading) _loads++; else _captures++; Finish(); }
    }
    class ScratchWrite : FsmStateAction
    {
        public override void OnEnter() { ES3.Save("performance.scratch-only", true, new ES3Settings { location = ES3.Location.Cache }); Finish(); }
    }
    static void Actions(PlayMakerFSM fsm, string name, params FsmStateAction[] actions)
    {
        var state = fsm.Fsm.States.First(s => s.Name == name); state.Actions = actions;
        foreach (var action in actions) { var es = action as ES3PlayMaker.ActionBase; if (es != null) es.errorMessage = ""; action.Init(state); }
    }
    static PlayMakerFSM Site(GameObject go, int value, string key)
    {
        var fsm = Fsm(go, "saveItemVar", "off", "save", "load");
        var flag = new FsmInt { Value = value }; flag.SetName("flag"); fsm.FsmVariables.IntVariables = new[] { flag };
        fsm.Fsm.GlobalTransitions = new[] { new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("Save"), ToState = "save" }, new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("Load"), ToState = "load" } };
        typeof(Fsm).GetField("initialized", All).SetValue(fsm.Fsm, false); fsm.Fsm.Init(fsm); fsm.Fsm.OnEnable();
        Actions(fsm, "save", new CountSite(), new ES3PlayMaker.SaveAll { key = key, saveFsmVariables = true, saveGlobalVariables = false });
        Actions(fsm, "load", new CountSite { Loading = true }, new ES3PlayMaker.LoadAll { key = key, loadFsmVariables = true, loadGlobalVariables = false });
        fsm.Fsm.SetState("off"); return fsm;
    }
    void PerformanceChecks(PlayMakerFSM main, PlayMakerFSM player, PlayMakerFSM reg, GameObject registries, string destination)
    {
        var harmony = new Harmony("apocasaver.performancechecks");
        harmony.Patch(AccessTools.Method(typeof(SaveRecords), "ReplaceCache"), postfix: new HarmonyMethod(typeof(NativeSaveRunner), nameof(CountCache)));
        harmony.Patch(AccessTools.Constructor(typeof(CleanWorld).GetNestedType("ReferenceIndex", All)), postfix: new HarmonyMethod(typeof(NativeSaveRunner), nameof(CountIndex)));
        harmony.Patch(AccessTools.Method(typeof(CleanWorld), "Objects"), prefix: new HarmonyMethod(typeof(NativeSaveRunner), nameof(CountRegistry)));
        harmony.Patch(AccessTools.Method(typeof(ES3).Assembly.GetType("ES3Internal.ES3IO"), "CommitBackup"), postfix: new HarmonyMethod(typeof(NativeSaveRunner), nameof(CountCommit)));
        try { PoolPerformance(); CleanupPerformance(registries); PosePerformance(destination); }
        finally { harmony.UnpatchSelf(); WorldHistory.Reset(); }
    }
    void PoolPerformance()
    {
        var poolType = AccessTools.TypeByName("Den.Tools.ObjectsPool+Pool");
        var prototypeType = AccessTools.Field(poolType, "prototype").FieldType;
        var prefab = new GameObject("PerformanceSite"); var template = Site(prefab, 77, "performance.template");
        var otherPrefab = new GameObject(prefab.name); var otherTemplate = Site(otherPrefab, 88, "performance.other-template");
        var prototype = Activator.CreateInstance(prototypeType); Set(prototype, "prefab", prefab);
        var pool = Activator.CreateInstance(poolType, new[] { prototype });
        var roots = new List<GameObject>(); var sites = new List<PlayMakerFSM>();
        for (int i = 0; i < 4; i++) { var go = new GameObject("PerformanceSite(Clone)" + i); roots.Add(go); sites.Add(Site(go, i + 10, "performance.site." + i)); }
        Set(pool, "instances", roots);
        var move = AccessTools.Method(poolType, "MoveItems");
        var drafts = (IList)Activator.CreateInstance(move.GetParameters()[0].ParameterType);
        var transition = move.GetParameters()[0].ParameterType.GetGenericArguments()[0];
        for (int i = 0; i < roots.Count; i++) drafts.Add(Activator.CreateInstance(transition, new object[] { (float)(100 + i), 0f }));
        var reset = typeof(WorldHistory).GetMethod("ResetTemplate", All);
        var restore = typeof(WorldHistory).GetMethod("RestoreFromPrefab", All);
        var oldHistory = new Dictionary<string, ES3Data>(SaveCoordinator.History);
        var oldKinds = new Dictionary<string, RecordKind>((Dictionary<string, RecordKind>)typeof(SaveCoordinator).GetField("_kinds", All).GetValue(null));
        try
        {
            WorldHistory.Reset(); _captures = _loads = _snapshots = 0;
            move.Invoke(pool, new object[] { drafts, 1, 3 });
            Check(_captures == 2 && _loads == 2 && roots[0].transform.position == Vector3.zero && roots[1].transform.position.x == 101 && roots[3].transform.position == Vector3.zero, "Native pool movement captures/restores only the moved range");
            Check(_snapshots == 1, "Two restored native sites share one history snapshot");
            Check(sites[0].FsmVariables.GetFsmInt("flag").Value == 10 && sites[1].FsmVariables.GetFsmInt("flag").Value == 11 && sites[3].FsmVariables.GetFsmInt("flag").Value == 13, "Range-limited restoration preserves history and untouched sites");
            _captures = _loads = 0; move.Invoke(pool, new object[] { drafts, 2, 2 });
            Check(_captures == 0 && _loads == 0 && _snapshots == 1, "Empty native movement batch does no history work");
            _captures = _loads = 0; AccessTools.Method(poolType, "ClampCount").Invoke(pool, new object[] { 3 });
            Check(_captures == 1 && _loads == 0 && roots.Count == 3 && SaveCoordinator.History.ContainsKey("performance.site.3"), "Native pool clamp captures only removed instances before destruction");

            reset.Invoke(null, new object[] { sites[0], roots[0], prefab });
            Check(sites[0].FsmVariables.GetFsmInt("flag").Value == 77, "Actual pool prefab supplies template defaults without name discovery");
            reset.Invoke(null, new object[] { sites[0], roots[0], otherPrefab });
            Check(sites[0].FsmVariables.GetFsmInt("flag").Value == 88, "Same-name prefab assets keep distinct template defaults");
            UnityEngine.Object.DestroyImmediate(template); template = Site(prefab, 99, "performance.replaced-template");
            reset.Invoke(null, new object[] { sites[0], roots[0], prefab });
            Check(sites[0].FsmVariables.GetFsmInt("flag").Value == 99, "Destroyed native template wrapper is resolved again");
            var runtimeChild = new GameObject("RuntimeChild"); runtimeChild.transform.SetParent(roots[0].transform); var childFsm = Site(runtimeChild, 1, "performance.child");
            for (int i = 0; i < 100; i++) reset.Invoke(null, new object[] { childFsm, roots[0], prefab });
            var templates = (IDictionary)typeof(WorldHistory).GetField("Templates", All).GetValue(null);
            Check(templates.Values.Cast<object>().Any(v => ReferenceEquals(v, null)), "Missing runtime-child templates remain memoized");
            WorldHistory.Reset(); Check(templates.Count == 0, "Scene/session reset clears template misses and prefab references");

            var data = new ES3Data(typeof(bool), ES3.Serialize(true));
            for (int i = 0; i < 10000; i++) SaveCoordinator.CaptureHistory("performance.history." + i, data);
            _snapshots = 0; var timer = Stopwatch.StartNew();
            for (int i = 0; i < 200; i++) { sites[1].Fsm.SetState("off"); restore.Invoke(null, new object[] { roots[1], prefab }); }
            timer.Stop();
            Check(_snapshots == 1, "200 native restorations with 10,000 history records build one snapshot");
            _report.Add("PERF: 200 native restorations / 10,000 synthetic records / snapshots=" + _snapshots + " / total_ms=" + timer.Elapsed.TotalMilliseconds.ToString("F3"));
            SaveCoordinator.CaptureHistory("performance.changed-history", data); sites[1].Fsm.SetState("off"); restore.Invoke(null, new object[] { roots[1], prefab });
            Check(_snapshots == 2, "Captured history invalidates the shared restoration snapshot");
            Actions(sites[1], "load", new CountSite { Loading = true }, new ScratchWrite());
            sites[1].Fsm.SetState("off"); restore.Invoke(null, new object[] { roots[1], prefab });
            sites[2].Fsm.SetState("off"); restore.Invoke(null, new object[] { roots[2], prefab });
            var cache = (ES3File)typeof(WorldHistory).GetField("_restoreCache", All).GetValue(null);
            Check(_snapshots == 3 && !SaveRecords.Data(cache).ContainsKey("performance.scratch-only") && !SaveCoordinator.History.ContainsKey("performance.scratch-only"), "Load-event writes cannot contaminate the ledger or the next site's snapshot");
            Check(!ES3File.cachedFiles.ContainsKey("apocasaver-history-capture.es3"), "History scratch file is never left registered for automatic disk storage");
        }
        finally
        {
            typeof(SaveCoordinator).GetField("_history", All).SetValue(null, oldHistory);
            typeof(SaveCoordinator).GetField("_kinds", All).SetValue(null, oldKinds);
            WorldHistory.HistoryChanged();
            foreach (var root in roots) if (root != null) UnityEngine.Object.DestroyImmediate(root);
            UnityEngine.Object.DestroyImmediate(prefab); UnityEngine.Object.DestroyImmediate(otherPrefab);
        }
    }
    void CleanupPerformance(GameObject registries)
    {
        var items = registries.GetComponents<PlayMakerArrayListProxy>().First(p => p.referenceName == "ArrayList_Items");
        bool enabled = Apocasaver.Plugin.CleanSaves.Value;
        var far = (Dictionary<string, float>)typeof(CleanWorld).GetField("Far", All).GetValue(null);
        var known = (HashSet<string>)typeof(CleanWorld).GetField("Known", All).GetValue(null);
        var first = new GameObject("Zombie_Runner_Dead(Clone)perf1"); var second = new GameObject("Zombie_Runner_Dead(Clone)perf2");
        var near = new GameObject("Zombie_Runner_Dead(Clone)perf-near"); var cold = new GameObject("PerformanceExternalOwner").AddComponent<PlayMakerFSM>();
        var owner = new FsmGameObject(); owner.SetName("SavedObject"); cold.FsmVariables.GameObjectVariables = new[] { owner }; typeof(Fsm).GetField("initialized", All).SetValue(cold.Fsm, false);
        foreach (var go in new[] { first, second, near }) { items.arrayList.Add(go); known.Add(go.name); }
        try
        {
            Apocasaver.Plugin.CleanSaves.Value = false; _registryReads = 0;
            CleanWorld.Tick(true); CleanWorld.Tick(true);
            Check(_registryReads == 0, "CleanSaves disabled performs no periodic registry scan");
            Apocasaver.Plugin.CleanSaves.Value = true; CleanWorld.Tick(false);
            Check(far.Count == 0, "Enabling cleanup starts fresh distance timing instead of counting disabled time");
            _indexes = 0; var plan = CleanWorld.Plan();
            Check(_indexes == 0 && !plan.Objects.Contains(first), "Ineligible cleanup roots do not build a global reference index");
            first.transform.position = second.transform.position = new Vector3(2000, 0, 0);
            foreach (var go in new[] { first, second, near }) far[go.name] = 600;
            _indexes = 0; plan = CleanWorld.Plan();
            Check(_indexes == 1 && plan.Objects.Contains(first) && plan.Objects.Contains(second) && !plan.Objects.Contains(near), "Multiple eligible roots share one reference scan; nearby remains survive");
            owner.Value = first; _indexes = 0; plan = CleanWorld.Plan();
            Check(_indexes == 1 && !plan.Objects.Contains(first) && plan.Objects.Contains(second), "Uninitialized external owner protects only its referenced candidate");
            owner.Value = null; plan = CleanWorld.Plan(); owner.Value = second;
            bool rejected = false; try { plan.Validate(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Cleanup validation rebuilds references and catches newly protected objects");
        }
        finally
        {
            Apocasaver.Plugin.CleanSaves.Value = enabled;
            foreach (var go in new[] { first, second, near }) { items.arrayList.Remove(go); UnityEngine.Object.DestroyImmediate(go); }
            UnityEngine.Object.DestroyImmediate(cold.gameObject);
        }
    }
    void PosePerformance(string destination)
    {
        const string file = "SaveGame5.es3";
        ES3.Save("OpaquePoseMod", "keep", Disk(file)); ES3.CacheFile(Disk(file)); HandPose.Clear();
        for (int i = 0; i < 3; i++) HandPose.Set("performance.pose." + i, "0,0,0,0,0,0,1");
        _poseFile = file; _poseCommits = 0;
        Check(HandPose.Flush(file) == 3 && HandPose.PendingCount == 0 && _poseCommits == 1, "Three delayed hand poses produce one disk commit");
        _poseFile = null;
        Check(ES3.Load<string>("OpaquePoseMod", Disk(file)) == "keep" && Enumerable.Range(0, 3).All(i => ES3.Load<string>("apocasaver.pose.performance.pose." + i, Disk(file)) == "0,0,0,0,0,0,1"), "Batched metadata preserves unrelated disk records and all poses");
        HandPose.Set("performance.locked", "pose");
        using (var diskLock = new FileStream(Disk(file).FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(HandPose.Flush(file) == 0 && HandPose.PendingCount == 1, "Failed delayed metadata write retains pending poses for retry");
        Check(HandPose.Flush(file) == 1 && HandPose.PendingCount == 0, "Pending metadata succeeds after the disk lock is released");
        Check(SaveCoordinator.Begin(destination, false), "Transactional hand-pose fixture begins");
        try
        {
            HandPose.Set("performance.transaction", "pose"); _poseFile = destination; _poseCommits = 0;
            Check(HandPose.Flush(destination) == 1 && _poseCommits == 0 && ES3.Load<string>("apocasaver.pose.performance.transaction", Cache(destination)) == "pose", "Active save batches metadata into cache without an intermediate disk write");
            Check(HandPose.Get("performance.transaction") == "pose", "Public pose reads see transactional metadata before the final disk flush");
        }
        finally { _poseFile = null; SaveCoordinator.Fail("Performance fixture rollback", true); }
        Check(!ES3.KeyExists("apocasaver.pose.performance.transaction", Disk(destination)), "Metadata-only fixture rollback preserves the previous disk save");
    }
}
