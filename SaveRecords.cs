using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ES3Internal;

namespace Apocasaver
{
    internal enum RecordKind : byte { Unknown, Session, Dynamic, History, Mod }
    internal static class SaveRecords
    {
        internal const string IndexKey = "apocasaver.records.v2", WorldKey = "apocasaver.world";
        private static readonly FieldInfo Cache = typeof(ES3File).GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Dirty = typeof(ES3File).GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo Sync = typeof(ES3File).GetField("syncWithFile", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static bool Ready { get { return Cache != null && Dirty != null && Sync != null; } }
        internal static Dictionary<string, ES3Data> Data(ES3File file) { return (Dictionary<string, ES3Data>)Cache.GetValue(file); }
        internal static bool IsDirty(ES3File file) { return (bool)Dirty.GetValue(file); }
        internal static Dictionary<string, ES3Data> Snapshot(string path)
        {
            ES3File file;
            if (!ES3File.cachedFiles.TryGetValue(path, out file))
            {
                var disk = new ES3Settings(path) { location = ES3.Location.File };
                if (!File.Exists(disk.FullPath)) return new Dictionary<string, ES3Data>();
                file = new ES3File(disk, true);
            }
            return new Dictionary<string, ES3Data>(Data(file));
        }
        internal static ES3File ReplaceCache(string path, IDictionary<string, ES3Data> seed, bool dirty = true)
        {
            var file = new ES3File(new ES3Settings(path) { location = ES3.Location.Cache }, false);
            Sync.SetValue(file, true); // Store the complete cache; do not merge disk keys back into it.
            var data = Data(file);
            foreach (var item in seed) data[item.Key] = item.Value;
            Dirty.SetValue(file, dirty); ES3File.cachedFiles[path] = file; return file;
        }
        internal static void MarkDirty(string path) { ES3File file; if (ES3File.cachedFiles.TryGetValue(path, out file)) Dirty.SetValue(file, true); }
        internal static Dictionary<string, RecordKind> Decode(string encoded)
        {
            var result = new Dictionary<string, RecordKind>();
            if (string.IsNullOrEmpty(encoded)) return result;
            using (var reader = new BinaryReader(new MemoryStream(Convert.FromBase64String(encoded))))
            {
                if (reader.ReadInt32() != 2) throw new InvalidDataException("Unsupported save record index");
                int count = reader.ReadInt32(); if (count < 0 || count > 1000000) throw new InvalidDataException("Invalid save record count");
                for (int i = 0; i < count; i++) { string key = reader.ReadString(); byte kind = reader.ReadByte(); if (kind > 4) throw new InvalidDataException("Invalid record kind"); result[key] = (RecordKind)kind; }
            }
            return result;
        }
        internal static string Encode(IDictionary<string, RecordKind> records)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { writer.Write(2); writer.Write(records.Count); foreach (var item in records) { writer.Write(item.Key); writer.Write((byte)item.Value); } writer.Flush(); return Convert.ToBase64String(stream.ToArray()); }
        }
        internal static bool PositionKey(string key) { return Regex.IsMatch(key, @"^\([-+0-9., E]+\)$"); }
        internal static RecordKind LegacyKind(string key, ES3Data data)
        {
            if (key.StartsWith("apocasaver.pose.")) return RecordKind.Mod;
            if (key.StartsWith("apocasaver.object.")) return RecordKind.Dynamic;
            if (key.Contains("(Clone)") && data.type != null && (data.type.type == typeof(object) || data.type.type == typeof(ES3PlayMaker.PMDataWrapper))) return RecordKind.Dynamic;
            if (PositionKey(key))
            {
                // Expanded native catalog: position-key Vector3 values are vehicle hinge rotations.
                // Static location fields are primitive flags/resource values or PMDataWrappers.
                string payload = System.Text.Encoding.UTF8.GetString(data.bytes ?? new byte[0]);
                if (data.type != null && data.type.type == typeof(object) && Regex.IsMatch(payload, "\"__type\"\\s*:\\s*\"Vector3\"")) return RecordKind.Dynamic;
                return RecordKind.History;
            }
            if (key == "Player" || key == "global" || key == "PlayerInCar" || key.StartsWith("newGOArray_") || key == "apocasaver.commit") return RecordKind.Session;
            if (key.StartsWith("apocasaver.") || key.StartsWith("Apoca") || key == "ImmersiveMapFog") return RecordKind.Mod;
            return RecordKind.Unknown;
        }
    }
}
