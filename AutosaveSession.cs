using System;

namespace Apocasaver
{
    internal enum DropResult { Ready, Dropped, Busy }

    internal interface IAutosaveRuntime
    {
        bool CanBegin(string file);
        void Countdown(int seconds);
        DropResult DropHeldItem();
        void BeginSave(string file);
        bool SaveComplete { get; }
        void Commit(string file);
        void EndSave(bool success, string error, bool resume);
    }

    // The transaction keeps its slot across frames; menu/vehicle interruptions restart a full countdown.
    internal sealed class AutosaveSession
    {
        private enum Phase { Idle, Countdown, Drop, Saving }
        private readonly IAutosaveRuntime _game;
        private Phase _phase;
        private string _file;
        private float _deadline, _dropUntil, _dropDeadline;
        private int _shown;
        internal bool Active { get { return _phase != Phase.Idle; } }
        internal bool Saving { get { return _phase == Phase.Saving; } }

        internal AutosaveSession(IAutosaveRuntime game) { _game = game; }

        internal void Start(float now, string file)
        {
            if (Active || !IsSlotFile(file) || !_game.CanBegin(file)) return;
            _file = file; _phase = Phase.Countdown; _deadline = now + 5f; _shown = 5;
            _game.Countdown(5);
        }

        internal static bool IsSlotFile(string file)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(file ?? "", @"^SaveGame([1-9]|10)\.es3$");
        }

        internal void Tick(float now)
        {
            if (!Active) return;
            try
            {
                if (Saving)
                {
                    if (_game.SaveComplete)
                    {
                        _game.Commit(_file);
                        Finish(true, null, true);
                    }
                    else if (now >= _deadline) Finish(false, "The game's save did not finish within 60 seconds", true);
                    return;
                }
                if (!_game.CanBegin(_file)) { Finish(false, null, true); return; }
                if (_phase == Phase.Countdown)
                {
                    int seconds = Math.Max(1, (int)Math.Ceiling(_deadline - now));
                    if (now < _deadline)
                    {
                        if (_shown != seconds) { _shown = seconds; _game.Countdown(seconds); }
                        return;
                    }
                    _phase = Phase.Drop; _dropUntil = now; _dropDeadline = now + 8f;
                }
                if (now >= _dropDeadline) { Finish(false, "The held item could not be dropped", true); return; }
                if (now < _dropUntil) return;
                var drop = _game.DropHeldItem();
                if (drop == DropResult.Busy) return;
                if (drop == DropResult.Dropped) { _dropUntil = now + 0.75f; return; }
                // Mark saving before entering the native flow so synchronous hooks see this transaction.
                _phase = Phase.Saving; _deadline = now + 60f;
                _game.BeginSave(_file);
            }
            catch (Exception e) { Finish(false, e.Message, true); }
        }

        internal void Cancel(string error, bool resume) { if (Active) Finish(false, error, resume); }

        private void Finish(bool success, string error, bool resume)
        {
            _phase = Phase.Idle; _file = null;
            _game.EndSave(success, error, resume);
        }
    }
}
