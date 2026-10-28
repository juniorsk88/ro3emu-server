using System;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// IMGUI draws on top of everything but is invisible to the game's EventSystem, so a click on the trade window would
    /// also reach the game (click-to-move, buttons behind it). While the window is visible this keeps one transparent,
    /// raycast-hit Image on its own top-most overlay canvas exactly over the window rect: the game's pointer-over-UI
    /// checks then count the area as UI and ignore the click. Created lazily, recreated if Unity destroyed it, never throws.
    /// </summary>
    internal sealed class TradeInputBlocker
    {
        private GameObject _root;
        private Canvas _canvas;
        private RectTransform _block;
        private bool _failed;

        /// <summary>Covers <paramref name="guiRect"/> (IMGUI coordinates: pixels, origin top-left, y down) or hides the blocker.</summary>
        public void Update(bool show, Rect guiRect)
        {
            if (_failed) return;
            try
            {
                if (!show || TradeWindow.Disabled)
                {
                    if (_root != null && _root.activeSelf) _root.SetActive(false);
                    return;
                }
                if (_root == null || _canvas == null || _block == null) Create();
                if (_root == null) return;
                if (!_root.activeSelf) _root.SetActive(true);
                float k = _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
                // Anchor bottom-left, pivot top-left: the position is the window's top-left corner measured upwards from the screen's bottom.
                _block.anchoredPosition = new Vector2(guiRect.x / k, (Screen.height - guiRect.y) / k);
                _block.sizeDelta = new Vector2(guiRect.width / k, guiRect.height / k);
            }
            catch (Exception e)
            {
                _failed = true; // one log, no retry storm; the window itself keeps working
                Debug.LogError("[Ro3Emu.Trade] input blocker disabled: " + e);
            }
        }

        public void Destroy()
        {
            try
            {
                if (_root != null) UnityEngine.Object.Destroy(_root);
            }
            catch (Exception) { /* shutting down */ }
            _root = null;
            _canvas = null;
            _block = null;
        }

        private void Create()
        {
            Destroy();
            var root = new GameObject("Ro3Emu_TradeInputBlocker");
            root.hideFlags = HideFlags.HideInHierarchy;
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            root.AddComponent<GraphicRaycaster>();

            var child = new GameObject("Block", typeof(RectTransform));
            child.transform.SetParent(root.transform, false);
            var rect = (RectTransform)child.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0f, 1f);
            var image = child.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
            image.canvasRenderer.cullTransparentMesh = false; // a culled (alpha 0) graphic is skipped by the raycaster

            _root = root;
            _canvas = canvas;
            _block = rect;
            Debug.Log("[Ro3Emu.Trade] input blocker created");
        }
    }
}
