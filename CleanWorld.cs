using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocasaver
{
    internal static class CleanWorld
    {
        private static readonly HashSet<string> Handled = new HashSet<string>(), Known = new HashSet<string>(), PatrolItems = new HashSet<string>();
        private static readonly Dictionary<string, float> Far = new Dictionary<string, float>();
        private static HashSet<string> _lastNames = new HashSet<string>();
        private static bool _baseline;
        private static float _next, _last;
        internal const string Key = "apocasaver.cleanup.v2";
        internal static void Protect(GameObject go) { if (go != null) Handled.Add(go.name); }
        internal static void NotePatrol(GameObject go) { if (go != null) { Known.Add(go.name); PatrolItems.Add(go.name); } }
        internal static List<PlayMakerArrayListProxy> Registries()
        {
            var go = GameObject.Find("NewGO_ArrayList");
            return go == null ? new List<PlayMakerArrayListProxy>() : go.GetComponents<PlayMakerArrayListProxy>().Where(p =>
                p.referenceName == "ArrayList_Items" || p.referenceName == "ArrayList_Cars" || p.referenceName == "ArrayList_Sandstorms").ToList();
        }
        internal static HashSet<GameObject> Objects()
        {
            var result = new HashSet<GameObject>();
            foreach (var p in Registries()) foreach (var item in p.arrayList) { var go = item as GameObject; if (go != null) result.Add(go); }
            return result;
        }
        internal static HashSet<string> AllNames()
        {
            var names = new HashSet<string>();
            foreach (var go in Objects()) foreach (var t in go.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
            return names;
        }
        internal static void Tick(bool playing)
        {
            Protect(HeldItemKeeper.RawHeldItem());
            float now = Time.realtimeSinceStartup;
            if (now < _next) return; _next = now + 1f;
            float dt = playing && _last > 0 ? Math.Min(1.5f, now - _last) : 0; _last = playing ? now : 0;
            if (!playing || SaveCoordinator.Active) return;
            var objects = Objects(); var names = new HashSet<string>(objects.Select(g => g.name));
            foreach (var go in objects)
            {
                if (_baseline && !_lastNames.Contains(go.name)) Known.Add(go.name);
                if (PocketCompatibility.Owns(go)) Protect(go);
            }
            _baseline = true; _lastNames = names;
            var player = GameObject.Find("Player"); if (player == null) return;
            var markers = SaveParticipants.Markers;
            if (markers != null) foreach (var marker in markers)
            {
                var component = marker as Component; if (component == null) continue;
                foreach (var root in SaveParticipants.PatrolGraph(marker)) NotePatrol(root);
                if (SaveParticipants.Entered(marker)) Protect(component.gameObject);
            }
            foreach (var go in objects)
            {
                float elapsed; Far.TryGetValue(go.name, out elapsed);
                Far[go.name] = Vector3.Distance(go.transform.position, player.transform.position) > CleanPolicy.Distance ? elapsed + dt : 0;
            }
        }
        private static bool Protected(GameObject go, bool assembly)
        {
            if (go == null || go.name == "Player" || go.tag == "Player" || Handled.Contains(go.name) || PocketCompatibility.Owns(go)) return true;
            if (!assembly && go.layer == 22) return true;
            for (var parent = go.transform.parent; parent != null; parent = parent.parent)
                if (parent.name == "Player" || parent.name.Contains("Slot") || parent.name.Contains("Hand") || (!assembly && parent.name == "PhysicsLock")) return true;
            var fsms = go.GetComponents<PlayMakerFSM>();
            foreach (var f in fsms)
            {
                var buy = f.FsmVariables.GetFsmInt("ForBuyInt"); if (buy != null && buy.Value != 0) return true;
                if (f.FsmName.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0 || f.FsmName.IndexOf("Quest", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
        private static bool References(HutongGames.PlayMaker.FsmVariables vars, HashSet<GameObject> tree)
        {
            foreach (var v in vars.GameObjectVariables) if (v.Value != null && tree.Contains(v.Value)) return true;
            foreach (var v in vars.ObjectVariables)
            { var go = v.Value as GameObject; var component = v.Value as Component; if (go == null && component != null) go = component.gameObject; if (go != null && tree.Contains(go)) return true; }
            foreach (var v in vars.ArrayVariables) if (v.Values != null) foreach (var item in v.Values)
            { var go = item as GameObject; var component = item as Component; if (go == null && component != null) go = component.gameObject; if (go != null && tree.Contains(go)) return true; }
            return false;
        }
        private static bool ReferencedOutside(HashSet<GameObject> tree)
        {
            if (References(FsmVariables.GlobalVariables, tree)) return true;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.gameObject.scene.IsValid() && !tree.Contains(f.gameObject) && f.FsmName != "Save_NewGO_ArrayList" &&
                    f.Fsm.States.Any(s => s.Actions.Any(a => a is ES3PlayMaker.SaveAll)) && References(f.FsmVariables, tree)) return true;
            foreach (var p in Resources.FindObjectsOfTypeAll<PlayMakerArrayListProxy>())
                if (p != null && p.gameObject.scene.IsValid() && p.gameObject.name != "NewGO_ArrayList" && !tree.Contains(p.gameObject) && p.arrayList != null)
                    foreach (var item in p.arrayList) { var go = item as GameObject; if (go != null && tree.Contains(go)) return true; }
            return false;
        }
        internal sealed class Exclusion
        {
            internal readonly HashSet<GameObject> Objects = new HashSet<GameObject>();
            internal readonly List<GameObject> Roots = new List<GameObject>();
            private readonly List<Tuple<IList, int, object>> _removed = new List<Tuple<IList, int, object>>();
            internal void Apply()
            {
                var lists = Registries().Select(p => (IList)p.arrayList).ToList(); var markers = SaveParticipants.Markers; if (markers != null) lists.Add(markers);
                foreach (var list in lists) for (int i = list.Count - 1; i >= 0; i--)
                {
                    var go = list[i] as GameObject; var component = list[i] as Component; if (go == null && component != null) go = component.gameObject;
                    if (go != null && Objects.Contains(go)) { _removed.Add(Tuple.Create(list, i, list[i])); list.RemoveAt(i); }
                }
            }
            internal void Restore()
            { for (int i = _removed.Count - 1; i >= 0; i--) { var item = _removed[i]; if (!item.Item1.Contains(item.Item3)) item.Item1.Insert(Math.Min(item.Item2, item.Item1.Count), item.Item3); } _removed.Clear(); }
            internal void Destroy()
            { foreach (var go in Roots) if (go != null) UnityEngine.Object.Destroy(go); _removed.Clear(); }
            internal void Validate()
            {
                if (Objects.Count == 0) return;
                foreach (var root in Roots) if (root != null)
                    foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (!Objects.Contains(t.gameObject)) throw new InvalidOperationException("Cleanup assembly changed during saving");
                if (Objects.Any(g => g != null && Protected(g, true)) || ReferencedOutside(Objects)) throw new InvalidOperationException("A cleanup candidate became protected during saving");
            }
        }
        internal static Exclusion Plan()
        {
            var result = new Exclusion(); if (!Plugin.CleanSaves.Value) return result;
            var player = GameObject.Find("Player"); if (player == null) return result;
            var markerList = SaveParticipants.Markers;
            if (markerList != null) foreach (var marker in markerList.Cast<object>().ToArray())
            {
                var component = marker as Component; if (component == null) continue; var car = component.gameObject;
                float age; Far.TryGetValue(car.name, out age);
                if (!CleanPolicy.Eligible(true, SaveParticipants.Entered(marker), true, Vector3.Distance(car.transform.position, player.transform.position), age)) continue;
                var graph = new HashSet<GameObject>(); var roots = SaveParticipants.PatrolGraph(marker).ToArray();
                foreach (var root in roots) foreach (var t in root.GetComponentsInChildren<Transform>(true)) graph.Add(t.gameObject);
                if (graph.Any(g => Protected(g, true)) || ReferencedOutside(graph)) continue;
                result.Objects.UnionWith(graph); result.Roots.AddRange(roots);
            }
            foreach (var go in Objects())
            {
                if (result.Objects.Contains(go)) continue;
                if (go.transform.parent != null) continue; // retained vehicle/container graphs remain intact
                var fsms = go.GetComponentsInChildren<PlayMakerFSM>(true);
                string prefab = go.name.Split(new[] { "(Clone)" }, StringSplitOptions.None)[0];
                bool corpse = prefab == "Zombie_Runner_Dead" && !fsms.Any(f => f.FsmName == "Food" || f.FsmName == "Carcasse");
                var ammo = fsms.FirstOrDefault(f => f.FsmName == "Ammo" || f.FsmName == "Batteries");
                var amount = ammo == null ? null : ammo.FsmVariables.GetFsmInt(ammo.FsmName == "Ammo" ? "ammo" : "batteries");
                var value = fsms.FirstOrDefault(f => f.FsmName == "Value"); var sale = value == null ? null : value.FsmVariables.GetFsmInt("SellValue");
                bool spent = amount != null && amount.Value <= 0 && sale != null && sale.Value <= 0;
                bool liveActor = fsms.Any(f => f.FsmName == "Health" && f.FsmVariables.GetFsmFloat("Health") != null && f.FsmVariables.GetFsmFloat("Health").Value > 0);
                bool patrol = PatrolItems.Contains(go.name) && !liveActor && !fsms.Any(f => f.FsmName == "RpmGear" || f.FsmName == "TrailerAttached");
                float age; Far.TryGetValue(go.name, out age);
                var tree = go.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToArray();
                if (!CleanPolicy.Eligible(Known.Contains(go.name), tree.Any(g => Protected(g, false)) || ReferencedOutside(new HashSet<GameObject>(tree)), corpse || spent || patrol,
                    Vector3.Distance(go.transform.position, player.transform.position), age)) continue;
                result.Objects.UnionWith(tree); result.Roots.Add(go);
            }
            Plugin.Log.LogInfo("CleanSaves planned " + result.Roots.Count + " abandoned root(s), " + result.Objects.Count + " object(s), including patrol assemblies/items when present");
            return result;
        }
        internal static string Encode(HashSet<string> names)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(2); writer.Write(names.Count);
                foreach (var name in names) { writer.Write(name); writer.Write(Handled.Contains(name)); writer.Write(Known.Contains(name)); writer.Write(PatrolItems.Contains(name)); float far; Far.TryGetValue(name, out far); writer.Write(far); }
                writer.Flush(); return Convert.ToBase64String(stream.ToArray());
            }
        }
        internal static void Load(string encoded)
        {
            Handled.Clear(); Known.Clear(); PatrolItems.Clear(); Far.Clear(); _lastNames.Clear(); _baseline = false; _last = _next = 0;
            if (string.IsNullOrEmpty(encoded)) return;
            using (var reader = new BinaryReader(new MemoryStream(Convert.FromBase64String(encoded))))
            {
                if (reader.ReadInt32() != 2) throw new InvalidDataException("Unsupported cleanup metadata");
                int count = reader.ReadInt32(); if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid cleanup metadata");
                for (int i = 0; i < count; i++) { string name = reader.ReadString(); if (reader.ReadBoolean()) Handled.Add(name); if (reader.ReadBoolean()) Known.Add(name); if (reader.ReadBoolean()) PatrolItems.Add(name); Far[name] = Math.Max(0, reader.ReadSingle()); }
            }
        }
    }
}
