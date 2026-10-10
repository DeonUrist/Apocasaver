using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Apocasaver
{
    // Optional adapters are signature-checked before any destination is touched.
    internal static class SaveParticipants
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type _pocketRun, _patrolStore, _marker, _trailers;
        private static FieldInfo _pocketInstance, _pocketSave, _pocketEnabled, _markerAll, _entered;
        private static bool _resolved;
        internal static bool Ready { get { Resolve(); return _resolved; } }
        internal static bool PatrolLoaded { get { Resolve(); return _patrolStore != null; } }
        internal static IList Markers { get { Resolve(); return _markerAll == null ? null : (IList)_markerAll.GetValue(null); } }
        internal static object PocketSave { get { var run = _pocketInstance == null ? null : _pocketInstance.GetValue(null); return run == null ? null : _pocketSave.GetValue(run); } }
        private static FieldInfo Field(Type type, string name)
        { var f = type.GetField(name, Flags); if (f == null) throw new MissingFieldException(type.FullName, name); return f; }
        private static MethodInfo Method(Type type, string name)
        { var m = type.GetMethod(name, Flags); if (m == null) throw new MissingMethodException(type.FullName, name); return m; }
        internal static object Get(object obj, string name) { return Field(obj.GetType(), name).GetValue(obj); }
        internal static void Set(object obj, string name, object value) { Field(obj.GetType(), name).SetValue(obj, value); }
        internal static void Resolve()
        {
            if (_resolved) return;
            // Unity can destroy the plugin's native Component while its managed wrapper,
            // static configuration and persistent runner remain valid. Unity's == null
            // tests native lifetime, not whether Chainloader has constructed the plugin.
            foreach (var plugin in Chainloader.PluginInfos.Values)
                if ((plugin.Metadata.GUID == "com.denis.apocalypter.apocapocket" || plugin.Metadata.GUID == "com.denis.apocalypter.apocapatrol") &&
                    ReferenceEquals(plugin.Instance, null)) return;
            var harmony = new Harmony(Plugin.GUID + ".participants");
            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                if (plugin.Metadata.GUID != "com.denis.apocalypter.apocapocket" && plugin.Metadata.GUID != "com.denis.apocalypter.apocapatrol") continue;
                var assembly = plugin.Instance.GetType().Assembly;
                if (plugin.Metadata.GUID == "com.denis.apocalypter.apocapocket")
                {
                    _pocketRun = assembly.GetType("Apocapocket.Runner", true);
                    _pocketInstance = Field(_pocketRun, "Instance"); _pocketSave = Field(_pocketRun, "Save");
                    _pocketEnabled = Field(assembly.GetType("Apocapocket.Plugin", true), "Enabled");
                    var type = _pocketSave.FieldType;
                    foreach (string n in new[] { "_flushAt", "_json", "_file", "_saving", "Normalised", "Loading" }) Field(type, n);
                    Method(type, "WriteMapping"); Method(type, "EndSave");
                }
                if (plugin.Metadata.GUID == "com.denis.apocalypter.apocapatrol")
                {
                    _patrolStore = assembly.GetType("Apocapatrol.PatrolPersistence", true);
                    _marker = assembly.GetType("Apocapatrol.PatrolMarker", true); _markerAll = Field(_marker, "All"); _entered = Field(_marker, "PlayerEntered");
                    _trailers = assembly.GetType("Apocapatrol.TrailerSupport", false);
                    foreach (string n in new[] { "_pendingSlot", "_pendingData", "_flushAt", "_lastState" }) Field(_patrolStore, n);
                    Method(_patrolStore, "Save"); Method(_patrolStore, "FlushPending"); Method(_patrolStore, "Deserialize");
                    harmony.Patch(Method(_patrolStore, "TryPath"), postfix: new HarmonyMethod(typeof(SaveParticipants), nameof(PatrolPath)));
                    harmony.Patch(Method(assembly.GetType("Apocapatrol.Cleanup", true), "Update"), prefix: new HarmonyMethod(typeof(SaveParticipants), nameof(CleanupAllowed)));
                    harmony.Patch(Method(assembly.GetType("Apocapatrol.Patrol", true), "SaveBusy"), prefix: new HarmonyMethod(typeof(SaveParticipants), nameof(PatrolBusy)));
                    harmony.Patch(Method(assembly.GetType("Apocapatrol.Register", true), "AddNow"), postfix: new HarmonyMethod(typeof(SaveParticipants), nameof(PatrolItem)));
                }
            }
            _resolved = true;
        }
        private static bool CleanupAllowed() { return !SaveCoordinator.Active; }
        private static bool PatrolBusy(ref bool __result) { if (!SaveCoordinator.Active) return true; __result = true; return false; }
        private static void PatrolItem(GameObject go) { CleanWorld.NotePatrol(go); }
        private static void PatrolPath(string slot, ref string path, bool __result)
        { if (__result && SaveCoordinator.Active && !SaveCoordinator.Publishing && slot == SaveCoordinator.Target && SaveCoordinator.PatrolStage != null) path = SaveCoordinator.PatrolStage; }
        internal static bool Entered(object marker) { return marker == null || (bool)_entered.GetValue(marker); }
        internal static IEnumerable<GameObject> PatrolGraph(object marker)
        {
            var component = marker as Component; if (component == null) yield break;
            yield return component.gameObject;
            foreach (string field in new[] { "_driver", "_passenger", "Trailer" })
            { var f = _marker.GetField(field, Flags); var go = f == null ? null : f.GetValue(marker) as GameObject; if (go != null) yield return go; }
            var riders = _marker.GetField("Riders", Flags); var values = riders == null ? null : riders.GetValue(marker) as GameObject[];
            if (values != null) foreach (var go in values) if (go != null) yield return go;
            if (_trailers != null) { var go = Method(_trailers, "Attached").Invoke(null, new object[] { component.gameObject }) as GameObject; if (go != null) yield return go; }
        }
        internal static string PatrolPathFor(string slot)
        { return Path.Combine(Paths.ConfigPath, "Apocapatrol", "Saves", slot + ".bin"); }
        internal static void Drain()
        {
            Resolve();
            if (_patrolStore != null) Method(_patrolStore, "FlushPending").Invoke(null, null);
            var save = PocketSave;
            if (save != null && (float)Get(save, "_flushAt") >= 0)
            {
                if (!(bool)Method(save.GetType(), "WriteMapping").Invoke(null, new[] { Get(save, "_file"), Get(save, "_json") })) throw new IOException("Previous pocket mapping has not finished");
                Set(save, "_flushAt", -1f);
            }
        }
        internal static void CompleteNative(string slot, int seed)
        {
            var pocket = PocketSave;
            bool enabled = _pocketEnabled != null && ((ConfigEntry<bool>)_pocketEnabled.GetValue(null)).Value;
            if (enabled && pocket == null) throw new IOException("Pocket inventory save participant is unavailable");
            if (pocket != null)
            {
                if ((bool)Get(pocket, "Normalised") || (bool)Get(pocket, "Loading")) throw new InvalidOperationException("Pocket inventory is still normalizing");
                var json = Get(pocket, "_json") as string;
                if (enabled && json == null) throw new IOException("Pocket inventory did not produce a fresh mapping");
                if (json != null)
                {
                    if (!(bool)Method(pocket.GetType(), "WriteMapping").Invoke(null, new object[] { slot, json })) throw new IOException("Pocket mapping verification failed");
                    Set(pocket, "_flushAt", -1f);
                    var settings = new ES3Settings(slot) { location = ES3.Location.Cache };
                    if (!ES3.KeyExists("Apocapocket.Slots", settings) || ES3.Load<string>("Apocapocket.Slots", settings) != json) throw new IOException("Pocket mapping missing from save cache");
                }
            }
            if (_patrolStore != null)
            {
                // Snapshot the final exclusion set now, rather than trusting a prior asynchronous Tick snapshot.
                Method(_patrolStore, "Save").Invoke(null, new object[] { slot, seed });
                string pending = Field(_patrolStore, "_pendingData").GetValue(null) as string;
                if (pending == null || (string)Field(_patrolStore, "_pendingSlot").GetValue(null) != slot) throw new IOException("Patrol snapshot failed");
                Method(_patrolStore, "FlushPending").Invoke(null, null);
                var settings = new ES3Settings(slot) { location = ES3.Location.Cache };
                if (!ES3.KeyExists("Apocapatrol.Patrol", settings) || ES3.Load<string>("Apocapatrol.Patrol", settings) != pending ||
                    Convert.ToBase64String(File.ReadAllBytes(SaveCoordinator.PatrolStage)) != pending) throw new IOException("Patrol embedded data and sidecar disagree");
                if (Method(_patrolStore, "Deserialize").Invoke(null, new object[] { Convert.FromBase64String(pending) }) == null) throw new IOException("Invalid patrol snapshot");
            }
        }
        internal static void Cancel()
        {
            if (_patrolStore != null)
            { Field(_patrolStore, "_pendingData").SetValue(null, null); Field(_patrolStore, "_pendingSlot").SetValue(null, null); Field(_patrolStore, "_flushAt").SetValue(null, -1f); Field(_patrolStore, "_lastState").SetValue(null, "isPlay"); }
            var pocket = PocketSave;
            if (pocket != null)
            {
                Set(pocket, "_flushAt", -1f);
                var run = _pocketInstance.GetValue(null);
                if ((bool)Get(pocket, "_saving") && run != null) Method(pocket.GetType(), "EndSave").Invoke(pocket, new[] { run });
                Set(pocket, "_flushAt", -1f); Set(pocket, "_json", null); Set(pocket, "_file", null);
            }
        }
    }
}
