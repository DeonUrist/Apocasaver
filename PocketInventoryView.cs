using System;
using System.Collections;
using System.Reflection;

namespace Apocasaver
{
    // Reads ownership by object identity, including during Apocapocket's temporary save normalization.
    // Kept independent of Unity so the real reflection adapter can be tested with inventory fixtures.
    internal sealed class PocketInventoryView
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly FieldInfo _instance, _slots, _content, _operation, _queued, _save, _loading, _normalised;
        private readonly PropertyInfo _ready;

        internal PocketInventoryView(Type runner)
        {
            _instance = Require(runner, "Instance"); _slots = Require(runner, "Slots");
            _operation = Require(runner, "CurrentOp"); _queued = Require(runner, "_queued");
            _save = Require(runner, "Save");
            _content = Require(_slots.FieldType.GetElementType(), "Content");
            _loading = Require(_save.FieldType, "Loading"); _normalised = Require(_save.FieldType, "Normalised");
            _ready = runner.GetProperty("Ready", Fields);
            if (_ready == null) throw new MissingMemberException(runner.FullName, "Ready");
        }

        private static FieldInfo Require(Type type, string name)
        {
            var field = type != null ? type.GetField(name, Fields) : null;
            if (field == null) throw new MissingMemberException(type != null ? type.FullName : "slot type", name);
            return field;
        }

        internal bool SaveBusy
        {
            get
            {
                var run = _instance.GetValue(null);
                if (run == null || !(bool)_ready.GetValue(run, null)) return true;
                var save = _save.GetValue(run);
                return save == null || (bool)_loading.GetValue(save) || (bool)_normalised.GetValue(save);
            }
        }

        internal bool Busy
        {
            get
            {
                var run = _instance.GetValue(null);
                return SaveBusy || _operation.GetValue(run) != null || _queued.GetValue(run) != null;
            }
        }

        internal bool Owns(object item)
        {
            if (item == null) return false;
            var run = _instance.GetValue(null);
            var slots = run != null ? _slots.GetValue(run) as IEnumerable : null;
            if (slots == null) return false;
            foreach (var slot in slots)
            {
                var content = slot != null ? _content.GetValue(slot) : null;
                if (content != null && (ReferenceEquals(content, item) || content.Equals(item))) return true;
            }
            return false;
        }
    }

    internal static class HeldItemPolicy
    {
        internal static bool HeldState(string state)
        {
            return state == "ItemInHand" || state == "Rotate" || state == "Forward" || state == "Backward" || state == "Grab";
        }

        internal static bool PhysicallyHeld(string state, bool underHand) { return underHand && HeldState(state); }
        internal static bool CanDrop(string state, bool underHand, bool pocketOwned) { return state == "ItemInHand" && underHand && !pocketOwned; }
    }
}
