using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Apocasaver
{
    // Owns only the temporary pause and a visual copy of the native loading screen.
    internal sealed class AutosavePresentation
    {
        private GameObject _root;
        private PlayMakerFSM _menu;
        private Canvas _saveCanvas;
        private bool _canvasEnabled, _menuEnabled, _paused;
        private EventSystem _events;
        private bool _eventsEnabled;
        private float _timeScale;
        private CursorLockMode _cursorLock;
        private bool _cursorVisible;

        internal void Begin(PlayMakerFSM menu, Canvas saveCanvas, GameObject loadingScreen)
        {
            _menu = menu; _saveCanvas = saveCanvas;
            _menuEnabled = menu.enabled; _canvasEnabled = saveCanvas.enabled;
            _timeScale = Time.timeScale; _cursorLock = Cursor.lockState; _cursorVisible = Cursor.visible;
            _events = EventSystem.current; _eventsEnabled = _events != null && _events.enabled;
            _paused = true; // Cleanup also runs if entering the menu or creating the screen throws.
            menu.Fsm.SetState("pause");
            menu.enabled = false; // Prevent ESC and the pause state's time interpolation during the save.
            saveCanvas.enabled = true;
            if (_events != null) _events.enabled = false;
            Time.timeScale = 0f;

            _root = new GameObject("Apocasaver_Loading", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            _root.SetActive(false); // Even an active source under an inactive parent must never run cloned FSMs.
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 31000;
            var sourceCanvas = loadingScreen.GetComponentInParent<Canvas>();
            var sourceScaler = sourceCanvas != null ? sourceCanvas.GetComponent<CanvasScaler>() : null;
            var scaler = _root.GetComponent<CanvasScaler>();
            if (sourceScaler != null)
            {
                scaler.uiScaleMode = sourceScaler.uiScaleMode; scaler.referenceResolution = sourceScaler.referenceResolution;
                scaler.screenMatchMode = sourceScaler.screenMatchMode; scaler.matchWidthOrHeight = sourceScaler.matchWidthOrHeight;
                scaler.referencePixelsPerUnit = sourceScaler.referencePixelsPerUnit; scaler.scaleFactor = sourceScaler.scaleFactor;
            }
            var copy = UnityEngine.Object.Instantiate(loadingScreen, _root.transform, false);
            var rect = copy.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero; rect.localScale = Vector3.one;
            }
            // Source is inactive in gameplay. Strip logic before activation so no load actions can run.
            foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                if (!(script is Graphic) && !(script is CanvasScaler) && !(script is LayoutGroup) &&
                    !(script is ContentSizeFitter) && !(script is AspectRatioFitter) && !(script is BaseMeshEffect))
                    UnityEngine.Object.DestroyImmediate(script);
            foreach (var graphic in copy.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            int titles = 0;
            foreach (var text in copy.GetComponentsInChildren<Text>(true))
                if (IsTitle(text.gameObject.name, text.text))
                {
                    text.text = "AUTOSAVING";
                    text.resizeTextMaxSize = text.fontSize; text.resizeTextMinSize = Math.Max(12, text.fontSize / 2);
                    text.resizeTextForBestFit = true; titles++;
                }
            foreach (var text in copy.GetComponentsInChildren<TMPro.TMP_Text>(true))
                if (IsTitle(text.gameObject.name, text.text))
                {
                    text.text = "AUTOSAVING"; text.fontSizeMax = text.fontSize;
                    text.fontSizeMin = Math.Max(12f, text.fontSize / 2f); text.enableAutoSizing = true; titles++;
                }
            if (titles == 0) throw new InvalidOperationException("The native loading screen has no loading title");
            foreach (var childCanvas in copy.GetComponentsInChildren<Canvas>(true)) { childCanvas.enabled = true; childCanvas.overrideSorting = false; }
            copy.SetActive(true);
            _root.SetActive(true);
        }

        private static bool IsTitle(string name, string text)
        {
            return string.Equals(name, "Loading", StringComparison.OrdinalIgnoreCase) ||
                (text ?? "").Trim().StartsWith("LOADING", StringComparison.OrdinalIgnoreCase);
        }

        internal void Tick() { if (_paused) Time.timeScale = 0f; }

        internal void End(bool resume)
        {
            if (_root != null) { _root.SetActive(false); UnityEngine.Object.Destroy(_root); }
            _root = null;
            if (!_paused) return;
            _paused = false;
            if (_events != null) _events.enabled = _eventsEnabled;
            if (_saveCanvas != null) _saveCanvas.enabled = _canvasEnabled;
            try
            {
                if (_menu != null)
                {
                    _menu.enabled = _menuEnabled;
                    if (resume && _menuEnabled) _menu.SendEvent("Activate"); // Native menu close restores Look, GUI and movement.
                }
            }
            finally
            {
                if (resume)
                {
                    Time.timeScale = _timeScale;
                    Cursor.lockState = _cursorLock; Cursor.visible = _cursorVisible;
                }
                _menu = null; _saveCanvas = null; _events = null;
            }
        }
    }
}
