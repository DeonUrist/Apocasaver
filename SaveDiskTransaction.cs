using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Apocasaver
{
    // Independent of Unity: a durable undo journal for one slot and its participant files.
    internal sealed class SaveDiskTransaction
    {
        internal sealed class Entry
        {
            internal string Target, Backup, Stage, Hash;
            internal bool Existed, Optional;
        }
        internal readonly List<Entry> Files = new List<Entry>();
        internal readonly string DirectoryPath;
        internal string Slot, Label;
        internal bool LabelExisted;
        internal bool Prepared, Committed;
        internal string JournalPath { get { return Path.Combine(DirectoryPath, "undo.bin"); } }

        internal SaveDiskTransaction(string root, string slot)
        {
            Slot = slot;
            DirectoryPath = Path.Combine(root, slot, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }
        private SaveDiskTransaction(string directory) { DirectoryPath = directory; }
        internal string Track(string path, bool optional = false)
        {
            if (Prepared) throw new InvalidOperationException("Backup already prepared");
            var entry = new Entry { Target = Path.GetFullPath(path), Existed = File.Exists(path),
                Backup = Path.Combine(DirectoryPath, "old-" + Files.Count + ".bin"), Stage = Path.Combine(DirectoryPath, "new-" + Files.Count + ".bin"), Optional = optional };
            if (entry.Existed)
            {
                CopyDurable(entry.Target, entry.Backup); entry.Hash = Hash(entry.Target);
                if (Hash(entry.Backup) != entry.Hash) throw new IOException("Backup verification failed for " + path);
            }
            Files.Add(entry); return entry.Stage;
        }
        internal void Prepare()
        {
            using (var stream = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x41505332); writer.Write(Slot); writer.Write(LabelExisted); writer.Write(Label ?? ""); writer.Write(Files.Count);
                foreach (var f in Files) { writer.Write(f.Target); writer.Write(f.Existed); writer.Write(f.Hash ?? ""); }
                writer.Flush(); stream.Flush(true);
            }
            Prepared = true;
        }
        internal void ResetPrimary()
        {
            if (!Prepared) throw new InvalidOperationException("No verified backup journal");
            File.Delete(Files[0].Target);
        }
        internal void Publish()
        {
            foreach (var f in Files)
            {
                if (f.Optional && !File.Exists(f.Stage)) continue;
                if (!File.Exists(f.Stage)) throw new IOException("Participant did not produce " + f.Target);
                string expected = Hash(f.Stage);
                CopyDurable(f.Stage, f.Target + ".apocasaver-publish");
                if (File.Exists(f.Target)) File.Replace(f.Target + ".apocasaver-publish", f.Target, null);
                else File.Move(f.Target + ".apocasaver-publish", f.Target);
                if (Hash(f.Target) != expected) throw new IOException("Commit verification failed for " + f.Target);
            }
        }
        internal void Complete()
        {
            using (var stream = new FileStream(Path.Combine(DirectoryPath, "committed"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.WriteByte(1); stream.Flush(true); }
            Committed = true;
        }
        internal void Rollback(Action<string, bool, string> restoreLabel)
        {
            if (!Prepared || Committed) return;
            // Validate every backup before replacing any destination.
            foreach (var f in Files) if (f.Existed && (!File.Exists(f.Backup) || Hash(f.Backup) != f.Hash))
                throw new IOException("Missing or damaged backup: " + f.Backup);
            foreach (var f in Files)
            {
                if (f.Existed)
                {
                    CopyDurable(f.Backup, f.Target + ".apocasaver-restore");
                    if (File.Exists(f.Target)) File.Replace(f.Target + ".apocasaver-restore", f.Target, null);
                    else File.Move(f.Target + ".apocasaver-restore", f.Target);
                    if (Hash(f.Target) != f.Hash) throw new IOException("Could not verify restored save " + f.Target);
                }
                else File.Delete(f.Target);
                File.Delete(f.Target + ".tmp"); File.Delete(f.Target + ".tmp.bak");
            }
            restoreLabel(Slot, LabelExisted, Label);
            using (var stream = new FileStream(Path.Combine(DirectoryPath, "recovered"), FileMode.Create, FileAccess.Write, FileShare.None)) { stream.WriteByte(1); stream.Flush(true); }
        }
        internal static SaveDiskTransaction Read(string journal, Func<string, bool> allowed)
        {
            var tx = new SaveDiskTransaction(Path.GetDirectoryName(journal));
            using (var reader = new BinaryReader(File.OpenRead(journal)))
            {
                if (reader.ReadInt32() != 0x41505332) throw new InvalidDataException("Unknown recovery journal");
                tx.Slot = reader.ReadString(); tx.LabelExisted = reader.ReadBoolean(); tx.Label = reader.ReadString();
                if (!AutosaveSession.IsSlotFile(tx.Slot)) throw new InvalidDataException("Invalid recovery slot");
                int count = reader.ReadInt32(); if (count < 1 || count > 16) throw new InvalidDataException("Invalid recovery file count");
                for (int i = 0; i < count; i++)
                {
                    string target = Path.GetFullPath(reader.ReadString());
                    if (!allowed(target)) throw new InvalidDataException("Recovery target outside save directories");
                    tx.Files.Add(new Entry { Target = target, Existed = reader.ReadBoolean(), Hash = reader.ReadString(),
                        Backup = Path.Combine(tx.DirectoryPath, "old-" + i + ".bin"), Stage = Path.Combine(tx.DirectoryPath, "new-" + i + ".bin") });
                }
                if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing recovery data");
            }
            tx.Prepared = true; tx.Committed = File.Exists(Path.Combine(tx.DirectoryPath, "committed")); return tx;
        }
        internal static string Hash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(stream)); }
        internal static void CopyDurable(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            using (var input = File.OpenRead(from)) using (var output = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
        }
    }
}
