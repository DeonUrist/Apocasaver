using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Apocasaver
{
    /// A native-looking status label (clone of the game's menu button, minus the logic) pinned to the top-left
    /// of its own always-on-top canvas, so it is visible during gameplay as well as in menus.
    internal static class StatusLabel
    {
        private static GameObject _canvasGo;
        private static GameObject _label;
        private static Vector2 _templateSize = new Vector2(300, 55);
        private static bool _caps = true;
        private static float _hideAt;
        private static string _current;
        private static bool _frame = true;
        private static float _nextTry;

        /// frame=false hides the button background art and shows the text alone, tight to the top-left corner.
        public static void Show(string text, float seconds, bool frame)
        {
            _hideAt = Time.realtimeSinceStartup + seconds;
            if (!Ensure()) return;
            if (_current != text || _frame != frame) SetText(text, frame);
            if (!_label.activeSelf) _label.SetActive(true);
        }

        public static void Hide()
        {
            _hideAt = 0;
            if (_label != null && _label.activeSelf) _label.SetActive(false);
        }

        /// Call every frame.
        public static void Tick()
        {
            if (_label != null && _label.activeSelf && Time.realtimeSinceStartup > _hideAt) _label.SetActive(false);
        }

        private static bool Ensure()
        {
            if (_label != null && _canvasGo != null) return true;
            if (Time.realtimeSinceStartup < _nextTry) return false;
            _nextTry = Time.realtimeSinceStartup + 1f;
            try
            {
                // Template: the game's "Settings" menu button (title screen or pause menu; either is fine, inactive is fine).
                var template = Resources.FindObjectsOfTypeAll<Button>()
                    .Where(b => b != null && b.gameObject.scene.IsValid() && b.gameObject.name == "Settings" && GoPath(b.transform).Contains("MainMenu_Canvas"))
                    .FirstOrDefault();
                if (template == null)
                    template = Resources.FindObjectsOfTypeAll<Button>()
                        .FirstOrDefault(b => b != null && b.gameObject.scene.IsValid() && (b.gameObject.name == "Settings" || b.gameObject.name == "Codex" || b.gameObject.name == "Credits"));
                if (template == null) { Plugin.Log.LogWarning("StatusLabel: no template button found yet"); return false; }

                if (_canvasGo == null)
                {
                    _canvasGo = new GameObject("Apocasaver_Canvas");
                    UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
                    var cv = _canvasGo.AddComponent<Canvas>();
                    cv.renderMode = RenderMode.ScreenSpaceOverlay;
                    cv.sortingOrder = 30000;
                    var scaler = _canvasGo.AddComponent<CanvasScaler>();
                    var src = template.GetComponentInParent<CanvasScaler>();
                    if (src == null) { var t = template.transform; while (t != null && src == null) { src = t.GetComponent<CanvasScaler>(); t = t.parent; } }
                    if (src != null)
                    {
                        scaler.uiScaleMode = src.uiScaleMode; scaler.referenceResolution = src.referenceResolution;
                        scaler.screenMatchMode = src.screenMatchMode; scaler.matchWidthOrHeight = src.matchWidthOrHeight;
                        scaler.referencePixelsPerUnit = src.referencePixelsPerUnit; scaler.scaleFactor = src.scaleFactor;
                    }
                    else { scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 0.5f; }
                }

                var go = UnityEngine.Object.Instantiate(template.gameObject, _canvasGo.transform);
                go.name = "Apocasaver_Status";
                foreach (var f in go.GetComponentsInChildren<PlayMakerFSM>(true)) UnityEngine.Object.DestroyImmediate(f);
                foreach (var f in go.GetComponentsInChildren<PlayMakerProxyBase>(true)) UnityEngine.Object.DestroyImmediate(f);
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                {
                    var c = go.transform.GetChild(i);
                    bool isText = c.GetComponentInChildren<Text>(true) != null || c.GetComponentInChildren<TMPro.TMP_Text>(true) != null;
                    bool isGraphic = c.GetComponent<Graphic>() != null;
                    if (!isText && !isGraphic) UnityEngine.Object.DestroyImmediate(c.gameObject);
                    else c.gameObject.SetActive(true);
                }
                // it's a label, not a button: no click logic, no raycasts
                foreach (var b in go.GetComponentsInChildren<Button>(true)) UnityEngine.Object.DestroyImmediate(b);
                foreach (var s in go.GetComponentsInChildren<Selectable>(true)) UnityEngine.Object.DestroyImmediate(s);
                foreach (var g in go.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
                var cg = go.GetComponent<CanvasGroup>(); if (cg != null) { cg.alpha = 1; cg.interactable = false; cg.blocksRaycasts = false; }
                foreach (var tx in go.GetComponentsInChildren<Text>(true)) { _caps = Caps(tx.text); tx.horizontalOverflow = HorizontalWrapMode.Overflow; tx.resizeTextForBestFit = false; }
                foreach (var tx in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) { _caps = Caps(tx.text); tx.enableWordWrapping = false; tx.overflowMode = TMPro.TextOverflowModes.Overflow; tx.enableAutoSizing = false; }

                var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
                var trt = template.GetComponent<RectTransform>();
                if (trt != null && trt.rect.width >= 10 && trt.rect.height >= 10) _templateSize = trt.rect.size;
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 1);
                rt.sizeDelta = _templateSize;
                rt.anchoredPosition = new Vector2(30, -30);
                rt.localScale = Vector3.one;
                go.SetActive(false);
                _label = go;
                _current = null;
                Plugin.Log.LogInfo("Status label created from '" + GoPath(template.transform) + "' (" + (int)_templateSize.x + "x" + (int)_templateSize.y + ")");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("StatusLabel: creation failed: " + e);
                if (_label != null) UnityEngine.Object.Destroy(_label);
                _label = null;
                return false;
            }
        }

        private static void SetText(string text, bool frame)
        {
            _current = text; _frame = frame;
            string t = _caps ? text.ToUpperInvariant() : text;
            // background art (root image + any non-text graphics) only when framed
            foreach (var g in _label.GetComponentsInChildren<Graphic>(true))
                if (!(g is Text) && !(g is TMPro.TMP_Text)) g.enabled = frame;
            foreach (var tx in _label.GetComponentsInChildren<Text>(true))
            {
                tx.text = t;
                tx.alignment = frame ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
                if (!frame) StretchToParent(tx.rectTransform);
            }
            foreach (var tx in _label.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                tx.text = t;
                tx.alignment = frame ? TMPro.TextAlignmentOptions.Center : TMPro.TextAlignmentOptions.TopLeft;
                if (!frame) StretchToParent(tx.rectTransform);
            }
            var rt = _label.GetComponent<RectTransform>();
            if (frame)
            {
                // widen the frame for long messages (the game's button art is a 9-slice, so it stretches cleanly)
                float w = _templateSize.x * Mathf.Max(1f, text.Length / 9f);
                rt.sizeDelta = new Vector2(w, _templateSize.y);
                rt.anchoredPosition = new Vector2(30, -30);
            }
            else
            {
                rt.sizeDelta = new Vector2(1200, _templateSize.y);
                rt.anchoredPosition = new Vector2(20, -15);
            }
        }

        private static void StretchToParent(RectTransform r)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.pivot = new Vector2(0, 1);
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
        }

        private static string GoPath(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }

        private static bool Caps(string s) { return string.IsNullOrEmpty(s) || s == s.ToUpperInvariant(); }
    }
}
