using Apocasaver;

static class Program
{
    static int _checks;
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
        _checks++;
    }

    static void Main()
    {
        foreach (int slot in Enumerable.Range(1, 10)) Check(AutosaveSession.IsSlotFile("SaveGame" + slot + ".es3"), "Valid native slot");
        foreach (string file in new[] { null, "", "SaveFile.es3", "SaveGame0.es3", "SaveGame11.es3", "SaveGame01.es3", "../SaveGame1.es3", "SaveGame1.es3.bak" })
            Check(!AutosaveSession.IsSlotFile(file), "Reject unknown or unsafe slot");

        var game = new Game(); var session = new AutosaveSession(game);
        game.Session = session;
        session.Start(0, game.Slot);
        for (int second = 1; second < 5; second++) session.Tick(second);
        Check(game.Counts.SequenceEqual(new[] { 5, 4, 3, 2, 1 }), "Full countdown in order");
        Check(!game.Paused, "Physics remain live throughout countdown");
        session.Tick(5);
        Check(session.Saving && game.Paused && game.SawSavingInsideBegin, "Native callbacks see active transaction before BeginSave");
        session.Tick(6);
        Check(session.Active && game.Commits == 0, "Do not commit while native save is incomplete");
        game.GlobalDone = true; session.Tick(7);
        Check(game.Commits == 0, "Global save completion alone is insufficient");
        game.RegistryDone = true; session.Tick(8);
        Check(!session.Active && !game.Paused && game.LastSuccess && game.Commits == 1, "Commit before resuming");

        // Original report: manual save, autosave, reload, autosave, reload. Loading resets native slot vars to 1.
        game = new Game(); session = new AutosaveSession(game); game.Session = session;
        game.Position = 10; game.ManualSave();
        game.Position = 20; RunSave(session, game, 0); game.Load();
        Check(game.Position == 20 && game.RegistrySlot == "SaveGame1.es3", "First autosave restores and load resets native registry");
        game.Position = 30; RunSave(session, game, 10); game.Load();
        Check(game.Position == 30 && game.LastCommittedSlot == "SaveGame9.es3", "First save after reload keeps captured slot and latest snapshot");
        game.Position = 40; RunSave(session, game, 20); game.Load();
        Check(game.Position == 40 && game.Commits == 3, "Repeated load/autosave cycles retain newest snapshot");

        game = new Game { Held = true }; session = new AutosaveSession(game); game.Session = session;
        session.Start(0, game.Slot); session.Tick(5);
        Check(game.Drops == 1 && !game.Paused, "Held item drops while physics are live");
        session.Tick(5.5f);
        Check(!session.Saving, "Allow dropped item to fall before pausing");
        game.Held = true; session.Tick(5.75f);
        Check(game.Drops == 2 && !session.Saving, "Drop a newly grabbed item instead of saving it in hand");
        session.Tick(6.5f);
        Check(session.Saving && game.Drops == 2, "Begin save after last drop settles");
        session.Cancel(null, true);

        game = new Game { Rotating = true, Held = true }; session = new AutosaveSession(game); game.Session = session;
        session.Start(0, game.Slot); session.Tick(5); session.Tick(7);
        Check(game.Drops == 0 && !session.Saving, "Do not save an item mid-rotation");
        session.Tick(13);
        Check(!session.Active && !game.Paused && !game.LastSuccess, "Drop timeout leaves game playable");

        game = new Game(); session = new AutosaveSession(game); game.Session = session;
        session.Start(0, game.Slot); session.Tick(1); game.Eligible = false; session.Tick(2);
        Check(!session.Active && game.Begins == 0, "Entering a vehicle/menu cancels countdown");
        game.Eligible = true; session.Start(3, game.Slot); session.Tick(7);
        Check(game.Begins == 0 && game.Counts.Last() == 1, "Return to play starts a fresh full countdown");
        session.Tick(8); session.Cancel(null, false);
        Check(!game.LastResume && !session.Active, "Scene/load cancellation avoids forcing gameplay time scale");

        game = new Game(); session = new AutosaveSession(game); game.Session = session;
        session.Start(0, game.Slot); game.Slot = "SaveGame8.es3"; session.Tick(1);
        Check(!session.Active && game.Begins == 0, "Slot switch aborts pending save");
        session.Start(2, "../SaveGame9.es3");
        Check(!session.Active, "Session rejects invalid path even if runtime allows it");

        foreach (string failure in new[] { "begin", "commit", "native", "timeout" })
        {
            game = new Game { Failure = failure }; session = new AutosaveSession(game); game.Session = session;
            session.Start(0, game.Slot); session.Tick(5);
            game.GlobalDone = game.RegistryDone = true;
            session.Tick(failure == "timeout" ? 65 : 6);
            Check(!session.Active && !game.Paused && !game.LastSuccess, failure + " failure cleans up without false success");
            Check(game.Ends == 1, failure + " cleanup runs once");
        }

        game = new Game(); session = new AutosaveSession(game); game.Session = session;
        session.Start(0, game.Slot); session.Start(1, game.Slot); session.Tick(5);
        Check(game.Begins == 1 && game.Counts.Count == 1, "Repeated scheduling cannot duplicate save");
        session.Cancel(null, true); session.Cancel(null, true);
        Check(game.Ends == 1, "Repeated cancellation is idempotent");
        PocketChecks();
        Console.WriteLine("PASS: " + _checks + " autosave transaction checks");
    }

    static void PocketChecks()
    {
        var pocket = new PocketFixture(); PocketFixture.Instance = pocket;
        var view = new PocketInventoryView(typeof(PocketFixture));
        var first = new PocketObject { Name = "canister" };
        var last = new PocketObject { Name = "meat" };
        pocket.Slots[0].Content = first; pocket.Slots[4].Content = last;
        Check(view.Owns(first) && view.Owns(last), "Protect any pocket slot, not only slot 1");
        Check(!view.Owns(new PocketObject { Name = "meat" }), "Same item name does not establish pocket ownership");
        Check(!view.Owns(null) && !view.Busy, "Empty hand and settled inventory are safe");
        pocket.CurrentOp = new object(); Check(view.Busy, "Wait for pocket selection/storage operation");
        pocket.CurrentOp = null; pocket.Queued = new object(); Check(view.Busy && !view.SaveBusy, "Queued re-equip blocks a new save but cannot deadlock completion");
        pocket.Queued = null; pocket.Save.Normalised = true;
        Check(view.SaveBusy && view.Owns(last), "Ownership survives temporary world placement during saving");
        pocket.Save.Normalised = false; pocket.Save.Loading = true; Check(view.Busy, "Wait for pocket load restoration");
        pocket.Save.Loading = false; pocket.Ready = false; Check(view.Busy, "Do not save an uninitialized inventory");
        pocket.Ready = true;
        foreach (string state in new[] { "ItemInHand", "Grab", "Rotate", "Forward", "Backward" })
        {
            Check(!HeldItemPolicy.PhysicallyHeld(state, false), "Stale grab state/reference without physical custody is not a held item");
            Check(!HeldItemPolicy.CanDrop(state, false, true), "Stored last-used item is never a drop candidate");
        }
        Check(!HeldItemPolicy.PhysicallyHeld("idle", true), "A historical reference is insufficient even under the hand");
        Check(!HeldItemPolicy.CanDrop("ItemInHand", true, true), "Pocket-owned item is reserved for pocket save normalization");
        Check(HeldItemPolicy.CanDrop("ItemInHand", true, false), "A loose item genuinely held still uses vanilla drop");
        Check(!HeldItemPolicy.CanDrop("Rotate", true, false), "Rotation finishes before issuing vanilla drop");
        pocket.Slots[4].Content = null;
        Check(!view.Owns(last), "Pocket ownership is released when an item is taken out");
        PocketFixture.Instance = new PocketFixture();
        Check(!view.Owns(first) && !view.Busy, "Scene reload reads new runner instead of keeping old inventory ownership");
        PocketFixture.Instance = null; Check(view.Busy && !view.Owns(last), "Missing pocket runner safely defers autosave");
    }

    sealed class PocketObject { public string Name; }
    sealed class PocketSlot { public object Content; }
    sealed class PocketSave { public bool Loading, Normalised; }
    sealed class PocketFixture
    {
        public static PocketFixture Instance;
        public readonly PocketSlot[] Slots = Enumerable.Range(0, 6).Select(_ => new PocketSlot()).ToArray();
        public object CurrentOp;
        private object _queued;
        public object Queued { set { _queued = value; } }
        public readonly PocketSave Save = new();
        public bool Ready { get; set; } = true;
    }

    static void RunSave(AutosaveSession session, Game game, float now)
    {
        game.GlobalDone = game.RegistryDone = false;
        session.Start(now, game.Slot); session.Tick(now + 5);
        game.GlobalDone = game.RegistryDone = true; session.Tick(now + 6);
        Check(game.LastSuccess, "Autosave completed");
    }

    sealed class Game : IAutosaveRuntime
    {
        public AutosaveSession Session;
        public string Slot = "SaveGame9.es3", RegistrySlot = "SaveGame9.es3", LastCommittedSlot, Failure;
        public readonly List<int> Counts = new();
        public bool Eligible = true, Held, Rotating, Paused, GlobalDone, RegistryDone, SawSavingInsideBegin, LastSuccess, LastResume;
        public int Position, Snapshot, Begins, Drops, Commits, Ends;
        readonly Dictionary<string, int> _disk = new();
        public bool CanBegin(string file) => Eligible && file == Slot;
        public void Countdown(int seconds) => Counts.Add(seconds);
        public DropResult DropHeldItem()
        {
            if (Rotating) return DropResult.Busy;
            if (!Held) return DropResult.Ready;
            Held = false; Drops++; return DropResult.Dropped;
        }
        public void BeginSave(string file)
        {
            Begins++; Paused = true; SawSavingInsideBegin = Session.Saving;
            if (Failure == "begin") throw new IOException("UI failed after pausing");
            RegistrySlot = file; Snapshot = Position;
        }
        public bool SaveComplete
        {
            get
            {
                if (Failure == "native") throw new IOException("Native serializer failed");
                return Failure != "timeout" && GlobalDone && RegistryDone;
            }
        }
        public void Commit(string file)
        {
            if (!Paused || file != Slot || file != RegistrySlot) throw new Exception("Wrong slot or premature resume");
            if (Failure == "commit") throw new IOException("Disk verification failed");
            _disk[file] = Snapshot; LastCommittedSlot = file; Commits++;
        }
        public void EndSave(bool success, string error, bool resume)
        {
            Ends++; Paused = false; LastSuccess = success; LastResume = resume;
            if (success && Commits == 0) throw new Exception("Success without committing");
        }
        public void ManualSave() { _disk[Slot] = Position; RegistrySlot = Slot; }
        public void Load() { Position = _disk[Slot]; RegistrySlot = "SaveGame1.es3"; }
    }
}
