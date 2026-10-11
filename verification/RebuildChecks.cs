using Apocasaver;

static class RebuildChecks
{
    public static void Run(Action<bool, string> check)
    {
        check(SaveFailurePolicy.IsDiskFailure(new IOException("disk full")), "Actual IO failure remains fatal");
        check(SaveFailurePolicy.IsDiskFailure(new UnauthorizedAccessException("read-only destination")), "Denied disk access remains fatal");
        check(SaveFailurePolicy.IsDiskFailure(new System.Reflection.TargetInvocationException(new IOException("disk locked"))), "Reflected disk failures remain fatal");
        foreach (var error in new Exception[] { new InvalidDataException("metadata layout"), new EndOfStreamException("old metadata"), new MissingFieldException("companion field"), new InvalidOperationException("unconfirmed object"), new TimeoutException("receipt missing") })
            check(!SaveFailurePolicy.IsDiskFailure(error), "Compatibility/validation error does not veto saving: " + error.GetType().Name);
        var root = Path.Combine(Path.GetTempPath(), "ApocasaverChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string primary = Path.Combine(root, "SaveGame3.es3"), sidecar = Path.Combine(root, "SaveGame3.es3.bin");
            File.WriteAllText(primary, "OLD WORLD"); File.WriteAllText(sidecar, "OLD PATROL");
            var tx = new SaveDiskTransaction(Path.Combine(root, "Backups"), "SaveGame3.es3") { Label = "OLD LABEL", LabelExisted = true };
            string mainStage = tx.Track(primary), modStage = tx.Track(sidecar);
            bool rejected = false; try { tx.ResetPrimary(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && File.ReadAllText(primary) == "OLD WORLD", "Deletion requires a verified durable backup journal");
            tx.Prepare(); tx.ResetPrimary();
            check(!File.Exists(primary) && File.ReadAllText(tx.Files[0].Backup) == "OLD WORLD", "Reset occupied destination only after backup");
            File.WriteAllText(mainStage, "NEW WORLD");
            try { tx.Publish(); } catch (IOException) { }
            check(File.ReadAllText(primary) == "NEW WORLD", "Exercise interrupted bundle after main file publication");
            var recovered = SaveDiskTransaction.Read(tx.JournalPath, p => p.StartsWith(root + Path.DirectorySeparatorChar));
            bool label = false; recovered.Rollback((slot, existed, value) => label = slot == "SaveGame3.es3" && existed && value == "OLD LABEL");
            check(File.ReadAllText(primary) == "OLD WORLD" && File.ReadAllText(sidecar) == "OLD PATROL" && label, "Crash recovery restores complete previous bundle and slot label");
            check(File.Exists(Path.Combine(tx.DirectoryPath, "recovered")), "Recovered journal remains auditable");
            var success = new SaveDiskTransaction(Path.Combine(root, "Backups"), "SaveGame3.es3");
            mainStage = success.Track(primary); modStage = success.Track(sidecar); success.Prepare(); success.ResetPrimary();
            File.WriteAllText(mainStage, "COMMITTED WORLD"); File.WriteAllText(modStage, "COMMITTED PATROL"); success.Publish(); success.Complete();
            success.Rollback((s, e, v) => throw new Exception("Committed bundle must not roll back"));
            check(File.ReadAllText(primary) == "COMMITTED WORLD" && File.ReadAllText(sidecar) == "COMMITTED PATROL", "Commit publishes matching main/participant files");
            check(SaveDiskTransaction.Read(success.JournalPath, p => true).Committed, "Durable commit distinguishes successful save on restart");
            var optional = new SaveDiskTransaction(Path.Combine(root, "Backups"), "SaveGame3.es3");
            mainStage = optional.Track(primary); optional.Track(sidecar, true); optional.Prepare(); optional.ResetPrimary();
            File.WriteAllText(mainStage, "WORLD WITHOUT OPTIONAL SNAPSHOT"); optional.Publish(); optional.Complete();
            check(File.ReadAllText(primary) == "WORLD WITHOUT OPTIONAL SNAPSHOT" && File.ReadAllText(sidecar) == "COMMITTED PATROL", "Unavailable optional snapshot cannot veto primary disk publication");
            string empty = Path.Combine(root, "SaveGame8.es3");
            var fresh = new SaveDiskTransaction(Path.Combine(root, "Backups"), "SaveGame8.es3"); fresh.Track(empty); fresh.Prepare(); fresh.ResetPrimary(); File.WriteAllText(empty, "partial");
            fresh.Rollback((s, e, v) => { }); check(!File.Exists(empty), "Failed first save restores an empty destination");
            var corrupt = new SaveDiskTransaction(Path.Combine(root, "Backups"), "SaveGame3.es3"); corrupt.Track(primary); corrupt.Prepare();
            File.WriteAllText(corrupt.Files[0].Backup, "CORRUPT"); rejected = false;
            try { corrupt.Rollback((s, e, v) => { }); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(primary) == "WORLD WITHOUT OPTIONAL SNAPSHOT", "Bad backup never replaces existing destination");
            rejected = false; try { SaveDiskTransaction.Read(success.JournalPath, p => false); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Recovery rejects paths outside approved directories");
            foreach (var flags in new[] { (false, false, true), (true, true, true), (true, false, false) })
                check(!CleanPolicy.Eligible(flags.Item1, flags.Item2, flags.Item3, 2000, 600), "Unknown ownership/protection/category veto cleanup");
            check(!CleanPolicy.Eligible(true, false, true, 1000, 600) && !CleanPolicy.Eligible(true, false, true, 2000, 599), "Cleanup needs both distance and abandonment time");
            check(CleanPolicy.Eligible(true, false, true, 1001, 600), "Eligible abandoned object can be excluded");
        }
        finally { if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true); }
    }
}
