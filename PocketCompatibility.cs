using System;
using System.Reflection;
using BepInEx.Configuration;

namespace Apocasaver
{
    internal static class PocketCompatibility
    {
        private static bool _resolved, _failed;
        private static PocketInventoryView _view;
        private static FieldInfo _enabled;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Apocapocket") continue;
                try
                {
                    _view = new PocketInventoryView(assembly.GetType("Apocapocket.Runner", true));
                    _enabled = assembly.GetType("Apocapocket.Plugin", true).GetField("Enabled", BindingFlags.Static | BindingFlags.NonPublic);
                    if (_enabled == null) throw new MissingFieldException("Apocapocket.Plugin", "Enabled");
                    Plugin.Log.LogInfo("Apocapocket compatibility: pocket ownership and inventory transitions detected");
                }
                catch (Exception e) { Fail(e); }
                break;
            }
        }

        private static bool Active
        {
            get
            {
                Resolve();
                if (_view == null || _failed) return false;
                var enabled = _enabled.GetValue(null) as ConfigEntry<bool>;
                return enabled == null || enabled.Value;
            }
        }

        private static void Fail(Exception e)
        {
            if (!_failed) Plugin.Log.LogError("Apocapocket compatibility could not be read; autosave deferred: " + e.Message);
            _failed = true;
        }

        internal static bool Busy
        {
            get { try { return Active ? _view.Busy : _failed; } catch (Exception e) { Fail(e); return true; } }
        }

        internal static bool SaveBusy
        {
            get { try { return Active ? _view.SaveBusy : _failed; } catch (Exception e) { Fail(e); return true; } }
        }

        internal static bool Owns(object item)
        {
            try
            {
                bool active = Active;
                return _failed || (active && _view.Owns(item));
            }
            catch (Exception e) { Fail(e); return true; } // Never drop on an uncertain ownership read.
        }
    }
}
