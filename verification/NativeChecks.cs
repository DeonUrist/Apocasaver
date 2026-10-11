using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Apocasaver;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;
using ES3Internal;
using UnityEngine.SceneManagement;

[BepInPlugin("com.denis.apocasaver.verifier", "Apocasaver verifier", "1.0")]
[BepInDependency(Apocasaver.Plugin.GUID)]
public class SaveProbe : BaseUnityPlugin
{
    internal static string Root;
    void Awake()
    {
        string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-save-test-root="));
        if (arg == null) { enabled = false; return; }
        Root = Path.GetFullPath(arg.Substring(16)); Directory.CreateDirectory(Root);
        Application.runInBackground = true;
        File.WriteAllText(Path.Combine(Root, "progress.txt"), "Verifier Awake\n");
        new Harmony("apocasaver.nativechecks").PatchAll(typeof(SaveProbe).Assembly);
        File.AppendAllText(Path.Combine(Root, "progress.txt"), "Verifier patches installed\n");
        var host = new GameObject("SaveVerifier"); DontDestroyOnLoad(host); host.AddComponent<NativeSaveRunner>().Execute();
    }
    [HarmonyPatch(typeof(ES3Settings), "get_FullPath")]
    class IsolateSaves
    {
        [HarmonyPriority(Priority.First)]
        static bool Prefix(ES3Settings __instance, ref string __result)
        {
            if (__instance.location != ES3.Location.File) return true;
            string stage = SaveCoordinator.Redirect(__instance);
            __result = stage ?? Path.Combine(Root, Path.GetFileName(__instance.path)); return false;
        }
    }
    [HarmonyPatch(typeof(PlayMakerFSM), "OnEnable")]
    class BlockWorld { static bool Prefix() { return false; } }
}
public class NativeSaveRunner : MonoBehaviour
{
    void Update() { Application.runInBackground = true; }
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    readonly List<string> _report = new List<string>();
    PlayMakerFSM _hinge;
    void Check(bool ok, string message) { if (!ok) throw new Exception(message); _report.Add("PASS: " + message); }
    static ES3Settings Disk(string slot) { return new ES3Settings(slot) { location = ES3.Location.File }; }
    static ES3Settings Cache(string slot) { return new ES3Settings(slot) { location = ES3.Location.Cache }; }
    static void Set(object obj, string name, object value) { obj.GetType().GetField(name, All).SetValue(obj, value); }
    static PlayMakerFSM Fsm(GameObject go, string name, params string[] states)
    {
        var result = go.AddComponent<PlayMakerFSM>(); result.Fsm.Name = name;
        result.Fsm.States = states.Select(n => new FsmState(result.Fsm) { Name = n, Actions = new FsmStateAction[0], Transitions = new FsmTransition[0] }).ToArray();
        result.Fsm.GlobalTransitions = new FsmTransition[0]; result.Fsm.StartState = states[0]; result.Fsm.Init(result); result.Fsm.SetState(states[0]); return result;
    }
    static void Action(PlayMakerFSM fsm, string state, FsmStateAction action)
    { var s = fsm.Fsm.States.First(x => x.Name == state); s.Actions = new[] { action }; var es = action as ES3PlayMaker.ActionBase; if (es != null) es.errorMessage = ""; action.Init(s); }
    internal void Execute()
    {
        File.AppendAllText(Path.Combine(SaveProbe.Root, "progress.txt"), "Verifier Running\n");
        try { Run(); }
        catch (Exception e) { _report.Add("FAIL: " + e); }
        File.WriteAllLines(Path.Combine(SaveProbe.Root, "report.txt"), _report); Application.Quit();
    }
    void Run()
    {
        var runner = Apocasaver.Plugin.Current; runner.enabled = false;
        Apocasaver.Plugin.Enabled.Value = false; Apocasaver.Plugin.SaveNamingEnabled.Value = false;
        var companionInfos = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Where(p => p.Metadata.GUID == "com.denis.apocalypter.apocapatrol" || p.Metadata.GUID == "com.denis.apocalypter.apocapocket").ToArray();
        if (companionInfos.Length > 0)
        {
            var info = companionInfos[0]; var wrapper = info.Instance;
            var setter = typeof(BepInEx.PluginInfo).GetProperty("Instance").GetSetMethod(true);
            setter.Invoke(info, new object[] { null });
            try
            {
                SaveCoordinator.Initialize();
                Check(SaveCoordinator.Begin("SaveGame10.es3", false) && SaveCoordinator.NativeFallback && !SaveCoordinator.Active && !SaveCoordinator.RecoveryRequired, "Pending companion allows native save without reset or permanent recovery failure");
            }
            finally { setter.Invoke(info, new object[] { wrapper }); }
            foreach (var companion in companionInfos)
            {
                var destroyed = companion.Instance;
                UnityEngine.Object.DestroyImmediate(destroyed);
                Check(destroyed == null && !ReferenceEquals(destroyed, null), "Reproduce scene-destroyed plugin component with valid managed wrapper: " + companion.Metadata.Name);
            }
        }
        SaveCoordinator.Initialize(); Check(SaveCoordinator.Ready && !SaveCoordinator.RecoveryRequired, "Native patches and companion adapters initialize after native plugin components are destroyed");
        var scene = SceneManager.CreateScene("Save verification"); SceneManager.SetActiveScene(scene);
        var refs = new GameObject("Easy Save 3 Manager").AddComponent<ES3ReferenceMgr>();
        var manager = new GameObject("SaveLoadGame"); var main = Fsm(manager, "SaveLoadGame", "isPlay", "SaveGame");
        var seedVar = new FsmInt { Value = 42 }; seedVar.SetName("seed"); main.FsmVariables.IntVariables = new[] { seedVar }; main.FsmVariables.Reinitialize();
        Action(main, "SaveGame", new ES3PlayMaker.SaveAll { key = "global", saveFsmVariables = true, saveGlobalVariables = false });
        var newGo = new GameObject("NewGO_ArrayList"); var reg = Fsm(newGo, "Save_NewGO_ArrayList", "off", "store cached file");
        foreach (string name in new[] { "ArrayList_Items", "ArrayList_Cars", "ArrayList_Sandstorms" })
        { var p = newGo.AddComponent<PlayMakerArrayListProxy>(); p.referenceName = name; p.arrayList.Clear(); }
        var button = Fsm(new GameObject("savegame"), "Continue", "off");
        var player = Fsm(new GameObject("Player"), "saveItemVar", "off", "save");
        Action(player, "save", new ES3PlayMaker.SaveAll { key = "Player", saveFsmVariables = true, saveGlobalVariables = false });
        Set(runner, "_saveLoad", main); Set(runner, "_newGoSave", reg); Set(runner, "_saveButton", button);
        Set(runner, "_armed", false);
        var pocketPlugin = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetType("Apocapocket.Runner", false) != null);
        if (pocketPlugin != null)
        {
            var pocketType = pocketPlugin.GetType("Apocapocket.Runner");
            new Harmony("saveprobe.pocketready").Patch(AccessTools.PropertyGetter(pocketType, "Ready"), prefix: new HarmonyMethod(typeof(NativeSaveRunner), nameof(PocketReady)));
            var pocketRun = pocketType.GetField("Instance", All).GetValue(null);
            if (pocketRun == null) { var host = new GameObject("Pocket test runner"); pocketRun = host.AddComponent(pocketType); }
            var pocketSave = pocketType.GetField("Save", All).GetValue(pocketRun);
            Set(pocketSave, "Loading", false); Set(pocketSave, "Normalised", false);
            ((BepInEx.Configuration.ConfigEntry<bool>)pocketPlugin.GetType("Apocapocket.Plugin").GetField("Enabled", All).GetValue(null)).Value = true;
        }

        var car = new GameObject("VerifierCar(Clone)44"); Fsm(car, "RpmGear", "off");
        newGo.GetComponents<PlayMakerArrayListProxy>().First(p => p.referenceName == "ArrayList_Cars").arrayList.Add(car);
        var hingeGo = new GameObject("hinge_door_L"); hingeGo.transform.SetParent(car.transform); _hinge = Fsm(hingeGo, "saveItemVar", "off", "save", "load");
        Action(_hinge, "save", new ES3PlayMaker.Save { key = "(100.0, 0.0, 0.0)", value = new FsmVar(new FsmVector3 { Value = new Vector3(1, 2, 3) }) });

        string source = "SaveGame6.es3", destination = "SaveGame8.es3";
        ES3Settings.defaultSettings.location = ES3.Location.Cache; ES3Settings.defaultSettings.path = source;
        ES3.Save("OpaqueMod", "source-only", Disk(source)); ES3.CacheFile(Disk(source));
        ES3.Save<object>("(100.0, 0.0, 0.0)", new Vector3(9, 8, 7), Cache(source));
        ES3.Save<object>("(101.0, 0.0, 0.0)", new Vector3(9, 8, 7), Cache(source)); ES3.StoreCachedFile(Disk(source));
        typeof(SaveCoordinator).GetMethod("LoadSource", All).Invoke(null, new object[] { source });
        SaveCoordinator.CaptureHistory("(5000.0, 0.0, 0.0)", new ES3Data(typeof(bool), ES3.Serialize(true)));
        ES3.Save("ForeignWorld", "must not leak", Disk(destination)); ES3.CacheFile(Disk(destination));
        byte[] original = File.ReadAllBytes(Disk(destination).FullPath);
        Check(SaveCoordinator.Begin(destination, false), "Occupied destination begins verified backup and fresh cache");
        Check(!File.Exists(Path.Combine(SaveProbe.Root, destination)), "Original slot removed only after durable backup");
        Check(!ES3.KeyExists("ForeignWorld", Cache(destination)) && ES3.Load<string>("OpaqueMod", Cache(destination)) == "source-only", "Fresh cache inherits source opaque data and rejects destination playthrough");
        Check(!ES3.KeyExists("(100.0, 0.0, 0.0)", Cache(destination)) && !ES3.KeyExists("(101.0, 0.0, 0.0)", Cache(destination)), "Legacy moving-hinge position records omitted from fresh cache");
        Finish(main, player, reg, destination);
        Check(!SaveCoordinator.Active && SaveCoordinator.LastSuccess, "Native save and participant bundle commits: " + SaveCoordinator.LastFailureCause);
        Check(ES3.Load<bool>("(5000.0, 0.0, 0.0)", Disk(destination)), "Unloaded location history survives fresh rebuild");
        Check(!ES3.KeyExists("ForeignWorld", Disk(destination)), "Foreign target records absent on disk");
        Check(ES3.KeyExists("apocasaver.commit", Disk(destination)) && ES3.KeyExists("saveTime8", Disk("SaveSettings.es3")), "Commit marker and slot label independently readable");
        Check(ES3.KeyExists("Apocapatrol.Patrol", Disk(destination)) == SaveParticipants.PatrolLoaded, "Loaded patrol participant writes its embedded snapshot");
        if (pocketPlugin != null) Check(ES3.KeyExists("Apocapocket.Slots", Disk(destination)) && !PocketCompatibility.SaveBusy, "Enabled pocket participant completes mapping verification before commit");
        string hingeKey = ES3.GetKeys(Disk(destination)).Single(k => k.StartsWith("apocasaver.object."));
        Check((Vector3)ES3.Load<object>(hingeKey, Disk(destination)) == new Vector3(1, 2, 3), "Vehicle hinge writes stable identity key with native rotation payload");
        SaveHooks.RefreshRoots(new HashSet<GameObject>());
        var read = new ES3PlayMaker.Load { key = "(999.0, 0.0, 0.0)", value = new FsmVar(new FsmVector3 { Value = Vector3.zero }), defaultValue = new FsmVar() };
        Action(_hinge, "load", read); _hinge.Fsm.SetState("load");
        Check((Vector3)read.value.GetValue() == new Vector3(1, 2, 3), "Hinge load resolves identity without relying on pre-load registry snapshot: " + read.errorMessage.Value);

        byte[] committed = File.ReadAllBytes(Disk(destination).FullPath);
        Check(SaveCoordinator.Begin(destination, false), "Repeated same-slot rebuild starts");
        SaveCoordinator.ReportError(new IOException("injected native failure")); SaveCoordinator.Tick();
        Check(!SaveCoordinator.Active && !SaveCoordinator.LastSuccess && File.ReadAllBytes(Disk(destination).FullPath).SequenceEqual(committed), "Native write failure restores byte-exact previous save");
        ES3.StoreCachedFile(Disk(destination));
        Check(File.ReadAllBytes(Disk(destination).FullPath).SequenceEqual(committed), "Automatic cache flush cannot rewrite a rolled-back save");
        Check(!SaveCoordinator.RecoveryRequired, "Successful rollback remains playable");
        Check(SaveCoordinator.Begin(destination, false), "Real disk lock regression begins");
        using (var diskLock = new FileStream(SaveCoordinator.Redirect(Disk(destination)), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Finish(main, player, reg, destination);
        Check(!SaveCoordinator.Active && !SaveCoordinator.LastSuccess && SaveCoordinator.LastDiskFailure && File.ReadAllBytes(Disk(destination).FullPath).SequenceEqual(committed), "Actual native ES3 disk-lock failure restores byte-exact previous save");

        var siteGo = new GameObject("Camp_Verifier"); siteGo.tag = "SpawnerStatic";
        var site = Fsm(siteGo, "saveItemVar", "off", "save", "load");
        var flag = new FsmInt { Value = 1 }; flag.SetName("OnOff_Item"); site.FsmVariables.IntVariables = new[] { flag };
        Action(site, "save", new ES3PlayMaker.SaveAll { key = "(6000.0, 0.0, 0.0)", saveFsmVariables = true, saveGlobalVariables = false });
        Action(site, "load", new ES3PlayMaker.LoadAll { key = "(6000.0, 0.0, 0.0)", loadFsmVariables = true, loadGlobalVariables = false });
        site.Fsm.GlobalTransitions = new[] { new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("Save"), ToState = "save" }, new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("Load"), ToState = "load" } };
        typeof(Fsm).GetField("initialized", All).SetValue(site.Fsm, false); site.Fsm.Init(site); site.Fsm.OnEnable(); site.Fsm.SetState("off");
        Action(site, "save", new ES3PlayMaker.SaveAll { key = "(6000.0, 0.0, 0.0)", saveFsmVariables = true, saveGlobalVariables = false });
        Action(site, "load", new ES3PlayMaker.LoadAll { key = "(6000.0, 0.0, 0.0)", loadFsmVariables = true, loadGlobalVariables = false });
        WorldHistory.Capture(siteGo);
        Check(SaveCoordinator.History.ContainsKey("(6000.0, 0.0, 0.0)"), "Static site state captured before removal without a full game save; enabled=" + site.enabled + " init=" + site.Fsm.Initialized + " state=" + site.ActiveStateName);
        flag.Value = 0;
        typeof(WorldHistory).GetMethod("Restore", All).Invoke(null, new object[] { siteGo });
        Check(flag.Value == 1 && !SaveCoordinator.RecoveryRequired, "Static history reapplied when a site is regenerated or reused");

        ((ES3PlayMaker.Save)_hinge.Fsm.States.First(s => s.Name == "save").Actions[0]).key = "(999.0, 0.0, 0.0)";
        Check(SaveCoordinator.Begin(destination, false), "Moved vehicle rebuild begins"); Finish(main, player, reg, destination);
        Check(SaveCoordinator.LastSuccess && ES3.GetKeys(Disk(destination)).Count(k => k.StartsWith("apocasaver.object.")) == 1 && ES3.KeyExists(hingeKey, Disk(destination)), "Moving same hinge replaces one stable record without key growth");
        Check(SaveCoordinator.Begin(destination, false), "Direct-file mod writer joins staging transaction");
        ES3.Save("FileOnlyMod", "updated-state", Disk(destination));
        Check(ES3.Load<string>("FileOnlyMod", Cache(destination)) == "updated-state", "File-only writes mirrored into fresh cache rather than lost in final flush");
        Finish(main, player, reg, destination);
        Check(SaveCoordinator.LastSuccess && ES3.Load<string>("FileOnlyMod", Disk(destination)) == "updated-state", "File-only mod state survives verified final commit");

        string fresh = "SaveGame9.es3";
        Check(SaveCoordinator.Begin(fresh, false), "Empty destination begins same rebuild algorithm");
        Finish(main, player, reg, fresh);
        Check(SaveCoordinator.LastSuccess && ES3.Load<string>("OpaqueMod", Disk(fresh)) == "source-only" && ES3.Load<bool>("(5000.0, 0.0, 0.0)", Disk(fresh)), "New slot retains same useful source history and mod data");
        Check(!File.Exists(Path.Combine(SaveProbe.Root, "SaveGame1.es3")), "Verifier never touched an unintended slot");
        var dormant = Fsm(car, "DormantSave", "off", "save");
        Action(dormant, "save", new ES3PlayMaker.SaveAll { key = car.name, saveFsmVariables = true, saveGlobalVariables = false });
        dormant.Fsm.GlobalTransitions = new[] { new FsmTransition { FsmEvent = FsmEvent.GetFsmEvent("SaveGame"), ToState = "save" } };
        ES3Settings.defaultSettings.path = destination; dormant.Fsm.SetState("save");
        ES3.Save("UsefulDormantState", "keep me", Cache(destination));
        typeof(SaveCoordinator).GetMethod("LoadSource", All).Invoke(null, new object[] { destination });
        Check(SaveCoordinator.Begin(destination, false), "Save with unobserved retained object begins");
        SaveCoordinator.CaptureHistory("(7000.0, 0.0, 0.0)", new ES3Data(typeof(bool), ES3.Serialize(true)));
        SaveCoordinator.ReportError(new MissingFieldException("injected optional binding"));
        Set(runner, "_saveButton", null);
        Finish(main, player, reg, destination, () => typeof(SaveCoordinator).GetField("_storms", All).SetValue(null, false));
        Check(SaveCoordinator.LastSuccess && !SaveCoordinator.Active && ES3.KeyExists(car.name, Disk(destination)) && ES3.Load<string>("UsefulDormantState", Disk(destination)) == "keep me", "Missing object/write confirmation, optional binding and save-button FSM do not veto disk save; live object's prior state survives");
        Set(runner, "_saveButton", button);
        Check(ES3.Load<bool>("(7000.0, 0.0, 0.0)", Disk(destination)), "History captured during saving reaches the current disk save");
        Check(SaveCoordinator.Begin(destination, false), "Save without native completion receipt begins");
        typeof(SaveCoordinator).GetField("_deadline", All).SetValue(null, -1f); SaveCoordinator.Tick();
        Check(SaveCoordinator.LastSuccess && ES3.KeyExists("Player", Disk(destination)) && ES3.KeyExists("global", Disk(destination)), "Confirmation timeout writes available cache and retains prior useful session records");
        dormant.Fsm.GlobalTransitions = new FsmTransition[0];
        Check(SaveCoordinator.Begin(destination, false), "Registry mutation regression begins");
        var lateItem = new GameObject("LateLoot(Clone)50");
        newGo.GetComponents<PlayMakerArrayListProxy>().First(p => p.referenceName == "ArrayList_Items").arrayList.Add(lateItem);
        Finish(main, player, reg, destination);
        Check(SaveCoordinator.LastSuccess && ES3.Load<ES3PlayMaker.PMDataWrapper>("newGOArray_Items", Disk(destination)).array.Contains(lateItem), "Registry mutation refreshes saved arrays without vetoing saving");
        typeof(SaveCoordinator).GetField("_rebuildAvailable", All).SetValue(null, false);
        string pendingJournal = Path.Combine(SaveProbe.Root, "Apocasaver", "Backups", destination, "pending-native-fallback");
        Directory.CreateDirectory(pendingJournal); File.WriteAllText(Path.Combine(pendingJournal, "undo.bin"), "unresolved journal fixture");
        Check(SaveCoordinator.Begin(destination, false) && SaveCoordinator.NativeFallback && !SaveCoordinator.Active && File.Exists(Disk(destination).FullPath), "Unavailable rebuild integration allows native saving without deleting destination");
        Finish(main, player, reg, destination);
        Check(SaveCoordinator.LastSuccess && !SaveCoordinator.NativeFallback && ES3.KeyExists("Player", Disk(destination)), "Native fallback completes a real cache flush to disk");
        Check(File.Exists(Path.Combine(pendingJournal, "superseded")), "Successful newer native save prevents replay of an older unresolved journal");
        typeof(SaveCoordinator).GetField("_rebuildAvailable", All).SetValue(null, true);
        ES3.Save("LateFileOnlyMod", "after save", Disk(destination));
        Check(ES3.Load<string>("LateFileOnlyMod", Cache(destination)) == "after save", "File-only mod write after commit refreshes source cache");
        Check(SaveCoordinator.Begin("SaveGame10.es3", false), "Rebuild after delayed mod update begins");
        Finish(main, player, reg, "SaveGame10.es3");
        Check(SaveCoordinator.LastSuccess && ES3.Load<string>("LateFileOnlyMod", Disk("SaveGame10.es3")) == "after save", "Delayed useful mod state survives the next save in a new slot");
        if (SaveParticipants.PatrolLoaded) PatrolCleanup(main, player, reg, newGo, destination);
        _report.Add("PASS: Native ES3 serialization, Harmony save hooks, transaction commit and rollback completed.");
    }
    void Finish(PlayMakerFSM main, PlayMakerFSM player, PlayMakerFSM reg, string slot, System.Action beforeTick = null)
    {
        main.Fsm.SetState("SaveGame"); player.Fsm.SetState("save");
        if (_hinge != null && !SaveCoordinator.Excluded(_hinge.gameObject)) _hinge.Fsm.SetState("save");
        foreach (string key in new[] { "newGOArray_Cars", "newGOArray_Items", "newGOArray_Sandstorms" })
        {
            string registry = key == "newGOArray_Cars" ? "ArrayList_Cars" : key == "newGOArray_Items" ? "ArrayList_Items" : "ArrayList_Sandstorms";
            var proxy = CleanWorld.Registries().First(p => p.referenceName == registry);
            var action = new ES3PlayMaker.Save { key = key, value = new FsmVar(new FsmArray { Name = key, ElementType = VariableType.GameObject, Values = proxy.arrayList.Cast<object>().ToArray() }) };
            Action(reg, "off", action); reg.Fsm.SetState("off");
        }
        Action(reg, "store cached file", new ES3PlayMaker.StoreCachedFile { filePath = slot }); reg.Fsm.SetState("store cached file");
        Action(reg, "off", new HutongGames.PlayMaker.Actions.Wait { time = 0, realTime = true }); reg.Fsm.SetState("off"); main.Fsm.SetState("isPlay");
        if (beforeTick != null) beforeTick(); SaveCoordinator.Tick();
    }
    static bool PocketReady(ref bool __result) { __result = true; return false; }
    void PatrolCleanup(PlayMakerFSM main, PlayMakerFSM player, PlayMakerFSM reg, GameObject newGo, string slot)
    {
        var markers = SaveParticipants.Markers;
        Type markerType = markers.GetType().GetGenericArguments()[0];
        var cars = newGo.GetComponents<PlayMakerArrayListProxy>().First(p => p.referenceName == "ArrayList_Cars");
        var items = newGo.GetComponents<PlayMakerArrayListProxy>().First(p => p.referenceName == "ArrayList_Items");
        var abandoned = new GameObject("PatrolDummy(Clone)45"); abandoned.transform.position = new Vector3(2000, 0, 0);
        var marker = abandoned.AddComponent(markerType); Set(marker, "_bodyPrefab", "PipeRat"); cars.arrayList.Add(abandoned);
        var cargo = new GameObject("PatrolCargo(Clone)46"); cargo.transform.SetParent(abandoned.transform); items.arrayList.Add(cargo);
        var owned = new GameObject("PatrolOwned(Clone)47"); owned.transform.position = new Vector3(2000, 0, 0);
        var owner = owned.AddComponent(markerType); Set(owner, "_bodyPrefab", "PipeRat"); Set(owner, "PlayerEntered", true); cars.arrayList.Add(owned);
        var loose = new GameObject("PatrolLoose(Clone)48"); loose.transform.position = new Vector3(2000, 0, 0); items.arrayList.Add(loose); CleanWorld.NotePatrol(loose);
        var kept = new GameObject("PatrolKept(Clone)49"); kept.transform.position = new Vector3(2000, 0, 0); items.arrayList.Add(kept); CleanWorld.NotePatrol(kept); CleanWorld.Protect(kept);
        var far = (Dictionary<string, float>)typeof(CleanWorld).GetField("Far", All).GetValue(null);
        foreach (var go in new[] { abandoned, owned, loose, kept }) far[go.name] = 600;
        Apocasaver.Plugin.CleanSaves.Value = true;
        Check(SaveCoordinator.Begin(slot, false), "Patrol cleanup rebuild begins");
        Check(SaveCoordinator.Excluded(abandoned) && SaveCoordinator.Excluded(cargo) && SaveCoordinator.Excluded(loose), "Abandoned patrol vehicle, attached cargo and detached generated items excluded together");
        Check(!SaveCoordinator.Excluded(owned) && !SaveCoordinator.Excluded(kept), "Player-entered patrol and handled detached item protected");
        Check(abandoned != null && loose != null && !cars.arrayList.Contains(abandoned) && !markers.Contains(marker), "Cleanup is reversible list exclusion, with objects still alive before commit");
        SaveCoordinator.ReportError(new IOException("cleanup rollback test")); SaveCoordinator.Tick();
        Check(abandoned != null && cargo != null && loose != null && cars.arrayList.Contains(abandoned) && items.arrayList.Contains(cargo) && markers.Contains(marker), "Failed save restores patrol marker and registration lists without destroying objects");
        Check(SaveCoordinator.Begin(slot, false), "Cleanup with changed protection begins");
        CleanWorld.Protect(abandoned);
        Finish(main, player, reg, slot);
        Check(SaveCoordinator.LastSuccess && abandoned != null && cars.arrayList.Contains(abandoned) && markers.Contains(marker), "Cleanup protection change keeps objects and does not veto save");
        var savedCars = ES3.Load<ES3PlayMaker.PMDataWrapper>("newGOArray_Cars", Disk(slot));
        Check(savedCars.array != null && savedCars.array.Contains(abandoned), "Restored cleanup candidate is present in the saved registration array");
        ((HashSet<string>)typeof(CleanWorld).GetField("Handled", All).GetValue(null)).Remove(abandoned.name);
        Check(SaveCoordinator.Begin(slot, false), "Patrol cleanup retries after rollback"); Finish(main, player, reg, slot);
        Check(SaveCoordinator.LastSuccess, "Patrol cleanup commits matching native and patrol snapshots");
        var store = markerType.Assembly.GetType("Apocapatrol.PatrolPersistence");
        string embedded = ES3.Load<string>("Apocapatrol.Patrol", Disk(slot));
        var data = store.GetMethod("Deserialize", All).Invoke(null, new object[] { Convert.FromBase64String(embedded) });
        var entries = (Array)data.GetType().GetField("cars").GetValue(data);
        Check(entries.Cast<object>().All(e => (string)e.GetType().GetField("carName").GetValue(e) != abandoned.name) && entries.Cast<object>().Any(e => (string)e.GetType().GetField("carName").GetValue(e) == owned.name), "Patrol embedded data omits removed vehicle and preserves player-owned vehicle");
    }
}
