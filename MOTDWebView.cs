/*
 * Based on unity-webview by GREE, Inc. (https://github.com/gree/unity-webview)
 * Copyright (C) 2011 Keijiro Takahashi, Copyright (C) 2012 GREE, Inc.
 * Adapted for WebsiteMOTD mod — Windows-only, UI Toolkit integration.
 *
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 *
 * 1. The origin of this software must not be misrepresented.
 * 2. Altered source versions must be plainly marked as such.
 * 3. This notice may not be removed or altered from any source distribution.
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace WebsiteMOTD
{
    /// <summary>
    /// Wraps the native WebView2 plugin (WebView.dll) and renders web content
    /// into a Texture2D that can be displayed in a UI Toolkit VisualElement.
    /// Windows x64 only.
    /// </summary>
    public class MOTDWebView : MonoBehaviour
    {
        // ─── Native DLL imports ─────────────────────────────────────
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("WebView")]
        private static extern string _CWebViewPlugin_GetAppPath();
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_InitStatic(bool inEditor, bool useDirect3D11);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_IsInitialized(IntPtr instance);
        [DllImport("WebView")]
        private static extern IntPtr _CWebViewPlugin_Init(string gameObject, bool transparent, bool zoom, int width, int height, string ua, bool separated);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_Destroy(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SetRect(IntPtr instance, int width, int height);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SetVisibility(IntPtr instance, bool visibility);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_LoadURL(IntPtr instance, string url);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_LoadHTML(IntPtr instance, string html, string baseUrl);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_EvaluateJS(IntPtr instance, string js);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_Progress(IntPtr instance);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_CanGoBack(IntPtr instance);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_CanGoForward(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_GoBack(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_GoForward(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_Reload(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SendMouseEvent(IntPtr instance, int x, int y, float deltaY, int mouseState);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SendKeyEvent(IntPtr instance, int x, int y, string keyChars, ushort keyCode, int keyState);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_Update(IntPtr instance, bool refreshBitmap, int devicePixelRatio);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_BitmapWidth(IntPtr instance);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_BitmapHeight(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_Render(IntPtr instance, IntPtr textureBuffer);
        [DllImport("WebView")]
        private static extern string _CWebViewPlugin_GetMessage(IntPtr instance);

        // ─── State ─────────────────────────────────────────────────
        private IntPtr _webView = IntPtr.Zero;
        private Texture2D _texture;
        private byte[] _textureDataBuffer;
        private bool _visible = true;
        private bool _hasFocus;
        private string _inputString = "";
        private int _viewWidth;
        private int _viewHeight;

        // UI Toolkit target element where the web content is displayed
        private VisualElement _targetElement;

        // Callbacks
        private Action<string> _onLoaded;
        private Action<string> _onStarted;
        private Action<string> _onError;
        private Action<string> _onJS;

        private static bool _nativeLoaded;
        private static bool _staticInitDone;

        public Texture2D Texture => _texture;
        public bool IsInitialized => _webView != IntPtr.Zero && _CWebViewPlugin_IsInitialized(_webView);
        public bool CanGoBack => _webView != IntPtr.Zero && _CWebViewPlugin_CanGoBack(_webView);
        public bool CanGoForward => _webView != IntPtr.Zero && _CWebViewPlugin_CanGoForward(_webView);
        public int Progress => _webView != IntPtr.Zero ? _CWebViewPlugin_Progress(_webView) : 0;

        // ─── DLL Pre-loading ────────────────────────────────────────

        /// <summary>
        /// Call once before any WebView usage. Loads WebView.dll from the mod's native folder
        /// so the DllImport resolver can find it.
        /// </summary>
        public static bool PreloadNativeDLL()
        {
            if (_nativeLoaded) return true;

            // Try multiple paths where the mod might ship the DLL
            string[] searchPaths = new[]
            {
                // Next to the mod DLL (e.g. Mods/WebsiteMOTD/native/x64/)
                Path.Combine(Path.GetDirectoryName(typeof(MOTDWebView).Assembly.Location) ?? "", "native", "x64", "WebView.dll"),
                // Game root Plugins folder
                Path.Combine(Application.dataPath, "Plugins", "x64", "WebView.dll"),
                // Game root
                Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "WebView.dll"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    Plugin.Log("Loading WebView.dll from: " + path);
                    IntPtr handle = LoadLibraryW(path);
                    if (handle != IntPtr.Zero)
                    {
                        _nativeLoaded = true;
                        Plugin.Log("WebView.dll loaded successfully.");
                        return true;
                    }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        Plugin.LogError("Failed to load WebView.dll (error " + err + "): " + path);
                    }
                }
            }

            Plugin.LogError("WebView.dll not found in any search path. WebView mode unavailable.");
            return false;
        }

        // ─── Factory ────────────────────────────────────────────────

        /// <summary>
        /// Creates and initialises a webview, rendering into the given VisualElement.
        /// </summary>
        public static MOTDWebView Create(
            VisualElement target,
            int width, int height,
            Action<string> onLoaded = null,
            Action<string> onStarted = null,
            Action<string> onError = null,
            Action<string> onJS = null)
        {
            if (!_nativeLoaded && !PreloadNativeDLL())
                return null;

            var go = new GameObject("MOTD_WebView");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);

            var wv = go.AddComponent<MOTDWebView>();
            wv._targetElement = target;
            wv._viewWidth = width;
            wv._viewHeight = height;
            wv._onLoaded = onLoaded;
            wv._onStarted = onStarted;
            wv._onError = onError;
            wv._onJS = onJS;

            wv.InitWebView();
            wv.SetupInputHandlers();

            return wv;
        }

        private void InitWebView()
        {
            if (!_staticInitDone)
            {
                bool isDX11 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
                _CWebViewPlugin_InitStatic(false, isDX11);
                _staticInitDone = true;
            }

            _webView = _CWebViewPlugin_Init(
                gameObject.name,
                true,       // transparent background
                true,       // zoom enabled
                _viewWidth,
                _viewHeight,
                "",         // default user-agent (WebView2 has its own)
                false       // not separated
            );

            if (_webView == IntPtr.Zero)
            {
                Plugin.LogError("WebView native init returned null!");
                return;
            }

            _CWebViewPlugin_SetVisibility(_webView, true);
            _CWebViewPlugin_SetRect(_webView, _viewWidth, _viewHeight);
            Plugin.Log("WebView initialized: " + _viewWidth + "x" + _viewHeight);
        }

        // ─── Input forwarding from UI Toolkit ───────────────────────

        private void SetupInputHandlers()
        {
            if (_targetElement == null) return;

            _targetElement.RegisterCallback<PointerDownEvent>(OnPointerDown);
            _targetElement.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _targetElement.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _targetElement.RegisterCallback<WheelEvent>(OnWheel);
            _targetElement.RegisterCallback<KeyDownEvent>(OnKeyDown);

            // Make focusable so we can receive key events
            _targetElement.focusable = true;
        }

        private (int x, int y) ToWebViewCoords(Vector2 localPos)
        {
            float elW = _targetElement.resolvedStyle.width;
            float elH = _targetElement.resolvedStyle.height;
            if (elW <= 0) elW = _viewWidth;
            if (elH <= 0) elH = _viewHeight;

            int x = Mathf.Clamp((int)(localPos.x / elW * _viewWidth), 0, _viewWidth);
            int y = Mathf.Clamp((int)(localPos.y / elH * _viewHeight), 0, _viewHeight);
            return (x, y);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            _hasFocus = true;
            _targetElement.Focus();
            var (x, y) = ToWebViewCoords(evt.localPosition);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, 1); // 1 = down
            _targetElement.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            var (x, y) = ToWebViewCoords(evt.localPosition);
            int state = _targetElement.HasPointerCapture(evt.pointerId) ? 2 : 0; // 2 = drag, 0 = move
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, state);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            var (x, y) = ToWebViewCoords(evt.localPosition);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, 3); // 3 = up
            if (_targetElement.HasPointerCapture(evt.pointerId))
                _targetElement.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnWheel(WheelEvent evt)
        {
            if (_webView == IntPtr.Zero || !_hasFocus) return;
            var (x, y) = ToWebViewCoords(evt.localMousePosition);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, -evt.delta.y, 0);
            evt.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (_webView == IntPtr.Zero || !_hasFocus) return;
            char c = evt.character;
            if (c == 0) return;
            string keyChars = c.ToString();
            ushort keyCode = (ushort)c;
            _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, keyChars, keyCode, 1);
        }

        // ─── Public API ─────────────────────────────────────────────

        public void LoadURL(string url)
        {
            if (_webView == IntPtr.Zero) return;
            Plugin.Log("WebView loading: " + url);
            _CWebViewPlugin_LoadURL(_webView, url);
        }

        public void LoadHTML(string html, string baseUrl = "")
        {
            if (_webView == IntPtr.Zero) return;
            _CWebViewPlugin_LoadHTML(_webView, html, baseUrl);
        }

        public void EvaluateJS(string js)
        {
            if (_webView == IntPtr.Zero) return;
            _CWebViewPlugin_EvaluateJS(_webView, js);
        }

        public void GoBack()
        {
            if (_webView == IntPtr.Zero) return;
            _CWebViewPlugin_GoBack(_webView);
        }

        public void GoForward()
        {
            if (_webView == IntPtr.Zero) return;
            _CWebViewPlugin_GoForward(_webView);
        }

        public void Reload()
        {
            if (_webView == IntPtr.Zero) return;
            _CWebViewPlugin_Reload(_webView);
        }

        public void SetSize(int width, int height)
        {
            _viewWidth = width;
            _viewHeight = height;
            if (_webView != IntPtr.Zero)
                _CWebViewPlugin_SetRect(_webView, width, height);
        }

        // ─── Per-frame update ───────────────────────────────────────

        void Update()
        {
            if (_webView == IntPtr.Zero) return;

            // Collect keyboard input from legacy Input system as backup
            if (_hasFocus)
                _inputString += Input.inputString;

            // Pump message queue
            for (;;)
            {
                string s = _CWebViewPlugin_GetMessage(_webView);
                if (s == null) break;
                int sep = s.IndexOf(':');
                if (sep == -1) continue;
                string type = s.Substring(0, sep);
                string body = s.Substring(sep + 1);
                switch (type)
                {
                    case "CallFromJS":   _onJS?.Invoke(body); break;
                    case "CallOnError":  _onError?.Invoke(body); break;
                    case "CallOnLoaded": _onLoaded?.Invoke(body); break;
                    case "CallOnStarted": _onStarted?.Invoke(body); break;
                }
            }

            if (!_visible) return;

            // Refresh bitmap every frame (1) for smooth rendering
            _CWebViewPlugin_Update(_webView, true, 1);

            int w = _CWebViewPlugin_BitmapWidth(_webView);
            int h = _CWebViewPlugin_BitmapHeight(_webView);
            if (w <= 0 || h <= 0) return;

            // (Re)create texture if size changed
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false, !linear);
                _texture.filterMode = FilterMode.Bilinear;
                _texture.wrapMode = TextureWrapMode.Clamp;
                _textureDataBuffer = new byte[w * h * 4];
            }

            // Render to buffer and apply
            if (_textureDataBuffer != null && _textureDataBuffer.Length > 0)
            {
                var gch = GCHandle.Alloc(_textureDataBuffer, GCHandleType.Pinned);
                _CWebViewPlugin_Render(_webView, gch.AddrOfPinnedObject());
                gch.Free();
                _texture.LoadRawTextureData(_textureDataBuffer);
                _texture.Apply();

                // Update the UI Toolkit background
                if (_targetElement != null)
                {
                    _targetElement.style.backgroundImage = new StyleBackground(_texture);
                }
            }

            // Send buffered keyboard input
            while (!string.IsNullOrEmpty(_inputString))
            {
                var keyChars = _inputString.Substring(0, 1);
                var keyCode = (ushort)_inputString[0];
                _inputString = _inputString.Substring(1);
                if (keyCode != 0)
                    _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, keyChars, keyCode, 1);
            }
        }

        // ─── Cleanup ────────────────────────────────────────────────

        public void Cleanup()
        {
            if (_targetElement != null)
            {
                _targetElement.UnregisterCallback<PointerDownEvent>(OnPointerDown);
                _targetElement.UnregisterCallback<PointerMoveEvent>(OnPointerMove);
                _targetElement.UnregisterCallback<PointerUpEvent>(OnPointerUp);
                _targetElement.UnregisterCallback<WheelEvent>(OnWheel);
                _targetElement.UnregisterCallback<KeyDownEvent>(OnKeyDown);
                _targetElement.style.backgroundImage = new StyleBackground();
                _targetElement = null;
            }

            if (_webView != IntPtr.Zero)
            {
                var ptr = _webView;
                _webView = IntPtr.Zero;
                _CWebViewPlugin_Destroy(ptr);
            }

            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }

            _textureDataBuffer = null;
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_webView != IntPtr.Zero)
            {
                var ptr = _webView;
                _webView = IntPtr.Zero;
                _CWebViewPlugin_Destroy(ptr);
            }
            if (_texture != null)
                Destroy(_texture);
        }
    }
}
