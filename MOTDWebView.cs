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
using UnityEngine.InputSystem;
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
        [DllImport("WebView")]
        private static extern ulong _CWebViewPlugin_BitmapGeneration(IntPtr instance);
        [DllImport("WebView")]
        private static extern ulong _CWebViewPlugin_LastInteractionTick(IntPtr instance);

        // ─── State ─────────────────────────────────────────────────
        private IntPtr _webView = IntPtr.Zero;
        private Texture2D _texture;
        private byte[] _textureDataBuffer;
        private bool _visible = true;
        private bool _hasFocus;
        private int _viewWidth;
        private int _viewHeight;
        private ulong _lastBitmapGen;   // skip texture upload when bitmap hasn't changed
        private float _lastInteractTime; // Unity time of last mouse/key input

        private VisualElement _targetElement;

        // Callbacks
        private Action<string> _onLoaded;
        private Action<string> _onStarted;
        private Action<string> _onError;
        private Action<string> _onJS;

        private static bool _nativeLoaded;
        private static bool _staticInitDone;
        private static bool _bitmapGenSupported = true; // false if DLL lacks _CWebViewPlugin_BitmapGeneration

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

        private void OnTextInput(char c)
        {
            if (!_hasFocus || _webView == IntPtr.Zero) return;
            // Skip control characters handled by PollKeyboard
            if (c == '\b' || c == '\r' || c == '\n' || c == '\t' || c == '\x1b') return;
            _lastInteractTime = Time.unscaledTime;
            // Send character only via keyChars; the native side derives the virtual key
            // via VkKeyScanW and sends the full WM_KEYDOWN → WM_CHAR → WM_KEYUP sequence.
            _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, c.ToString(), 0, 1);
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
            // Capture ALL key events at the panel level so game binds can't fire while webview has focus
            _targetElement.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _targetElement.RegisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);

            _targetElement.focusable = true;

            if (Keyboard.current != null)
                Keyboard.current.onTextInput += OnTextInput;
        }

        private (int x, int y) ToWebViewCoords(Vector2 localPos)
        {
            float elW = _targetElement.resolvedStyle.width;
            float elH = _targetElement.resolvedStyle.height;
            if (elW <= 0) elW = _viewWidth;
            if (elH <= 0) elH = _viewHeight;

            // localPosition is pre-transform (before scaleY(-1)), so y=0 is layout top.
            // WebView2 DLL expects y=0 at bottom (matching Unity screen coords).
            // With scaleY(-1), layout y=0 IS the visual bottom = WebView y=0, so map directly.
            int x = Mathf.Clamp((int)(localPos.x / elW * _viewWidth), 0, _viewWidth);
            int y = Mathf.Clamp((int)(localPos.y / elH * _viewHeight), 0, _viewHeight);
            return (x, y);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            _hasFocus = true;
            _lastInteractTime = Time.unscaledTime;
            _targetElement.Focus();
            var (x, y) = ToWebViewCoords(evt.localPosition);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, 1); // 1 = down
            _targetElement.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            _lastInteractTime = Time.unscaledTime;
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
            _lastInteractTime = Time.unscaledTime;
            var (x, y) = ToWebViewCoords(evt.localMousePosition);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, -evt.delta.y, 0);
            evt.StopPropagation();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!_hasFocus) return;
            // Consume so UI Toolkit doesn't propagate — actual characters come via onTextInput
            evt.StopPropagation();
        }

        private void OnKeyUp(KeyUpEvent evt)
        {
            if (!_hasFocus) return;
            evt.StopPropagation();
        }

        /// <summary>
        /// Polls the new Input System keyboard for text input each frame
        /// and forwards characters to the WebView.
        /// </summary>
        private void PollKeyboard()
        {
            if (!_hasFocus || _webView == IntPtr.Zero) return;
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.backspaceKey.wasPressedThisFrame)
            { _lastInteractTime = Time.unscaledTime; _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, "", 8, 1); }
            if (kb.enterKey.wasPressedThisFrame)
            { _lastInteractTime = Time.unscaledTime; _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, "\n", 13, 1); }
            if (kb.tabKey.wasPressedThisFrame)
            { _lastInteractTime = Time.unscaledTime; _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, "\t", 9, 1); }
            if (kb.escapeKey.wasPressedThisFrame)
            { _lastInteractTime = Time.unscaledTime; _CWebViewPlugin_SendKeyEvent(_webView, 0, 0, "", 27, 1); }
        }

        // ─── Public API ─────────────────────────────────────────────

        public void LoadURL(string url)
        {
            if (_webView == IntPtr.Zero) return;
            _lastInteractTime = Time.unscaledTime;
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

        // Refresh rate tiers based on how recently the user interacted
        private const float ActiveWindowSec  = 1.0f;  // full-speed capture for 1s after input
        private const float WindDownSec      = 3.0f;  // half-speed capture for next 2s
        // After WindDownSec: low-rate (4fps) to catch CSS animations / async content

        void Update()
        {
            if (_webView == IntPtr.Zero) return;

            // Poll keyboard for special keys
            PollKeyboard();

            // Pump message queue (always — even when not refreshing bitmap)
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
                    case "CallFromJS":    _onJS?.Invoke(body); break;
                    case "CallOnError":   _onError?.Invoke(body); break;
                    case "CallOnLoaded":
                        _lastInteractTime = Time.unscaledTime; // page loaded — capture it
                        _onLoaded?.Invoke(body);
                        break;
                    case "CallOnStarted":
                        _lastInteractTime = Time.unscaledTime;
                        _onStarted?.Invoke(body);
                        break;
                }
            }

            if (!_visible) return;

            // Decide whether to request a new bitmap capture this frame.
            // Active interaction → every frame.  Winding down → every 2nd frame.  Idle → every 15th frame (~4fps).
            // When bitmapGen is unavailable we cap to every 6 frames (~10fps) to avoid
            // uploading a full-resolution bitmap unconditionally at 60fps.
            float idleSec = Time.unscaledTime - _lastInteractTime;
            bool shouldRefresh;
            if (!_bitmapGenSupported)
            {
                shouldRefresh = (Time.frameCount % 6 == 0);        // ~10fps — safe without change-detection
            }
            else if (idleSec < ActiveWindowSec)
                shouldRefresh = true;                              // full speed
            else if (idleSec < WindDownSec)
                shouldRefresh = (Time.frameCount % 2 == 0);       // ~30fps
            else
                shouldRefresh = (Time.frameCount % 15 == 0);      // ~4fps idle

            _CWebViewPlugin_Update(_webView, shouldRefresh, 1);

            if (!shouldRefresh) return;

            // Check if the native bitmap actually changed since last upload.
            // Fall back to always-upload if this DLL export is missing.
            if (_bitmapGenSupported)
            {
                try
                {
                    ulong gen = _CWebViewPlugin_BitmapGeneration(_webView);
                    if (gen == _lastBitmapGen) return;
                    _lastBitmapGen = gen;
                }
                catch (System.EntryPointNotFoundException)
                {
                    _bitmapGenSupported = false;
                    Plugin.LogError("WebView: _CWebViewPlugin_BitmapGeneration not found in DLL — falling back to unconditional refresh.");
                }
            }

            int w = _CWebViewPlugin_BitmapWidth(_webView);
            int h = _CWebViewPlugin_BitmapHeight(_webView);
            if (w <= 0 || h <= 0) return;

            // Recreate texture only when size changes
            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false, !linear);
                _texture.filterMode = FilterMode.Bilinear;
                _texture.wrapMode = TextureWrapMode.Clamp;
                _textureDataBuffer = new byte[w * h * 4];
                if (_targetElement != null)
                    _targetElement.style.backgroundImage = new StyleBackground(_texture);
            }

            if (_textureDataBuffer == null) return;

            // Copy bitmap from native, upload to GPU
            var gch = GCHandle.Alloc(_textureDataBuffer, GCHandleType.Pinned);
            _CWebViewPlugin_Render(_webView, gch.AddrOfPinnedObject());
            gch.Free();

            _texture.LoadRawTextureData(_textureDataBuffer);
            _texture.Apply(false);

            _targetElement?.MarkDirtyRepaint();
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
                _targetElement.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                _targetElement.UnregisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);
                _targetElement.style.backgroundImage = new StyleBackground();
                _targetElement = null;
            }

            if (Keyboard.current != null)
                Keyboard.current.onTextInput -= OnTextInput;

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
