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
        private static extern void _CWebViewPlugin_AddScriptOnLoad(IntPtr instance, string js);
        [DllImport("WebView", CharSet = CharSet.Ansi)]
        private static extern void _CWebViewPlugin_LoadExtension(IntPtr instance, string extensionFolderPath);
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
        private static bool _preloadAttempted;   // prevents repeated probe + log spam after a failed load
        private static bool _staticInitDone;
        private static bool _bitmapGenSupported = true;
        // Browser extensions are profile-bound (single LOCALAPPDATA\UnityWebView2 dir
        // shared across all WebView instances), so we only need to call
        // AddBrowserExtensionAsync once per process. Tracking the attempt rather
        // than the success keeps the runtime's idempotent "already installed"
        // result from re-emitting an ExtensionLoaded log on every spawn.
        private static bool _extensionLoadAttempted;

        /// <summary>
        /// The native WebView plugin (WebView2) is Windows-only. On Linux/macOS
        /// the kernel32 + WebView DllImports throw DllNotFoundException at the
        /// first call, which crashes the connect handler when the local client
        /// has the MOTD site trusted (skipping the deny dialog). Probe once,
        /// gracefully fall back to HTML-only mode everywhere else.
        /// </summary>
        public static bool IsSupportedPlatform()
        {
            return Application.platform == RuntimePlatform.WindowsPlayer
                || Application.platform == RuntimePlatform.WindowsEditor;
        }

        /// <summary>
        /// Ensures the native WebView2 static init is called exactly once.
        /// Shared between MOTDWebView and MOTDWorldScreen.
        /// </summary>
        public static void EnsureStaticInit()
        {
            if (_staticInitDone) return;
            if (!IsSupportedPlatform()) return;
            bool isDX11 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
            _CWebViewPlugin_InitStatic(false, isDX11);
            _staticInitDone = true;
        }

        /// <summary>
        /// Try to load a bundled browser extension into the given WebView's profile.
        /// First call per process actually dispatches; subsequent calls are no-ops
        /// because the extension persists profile-wide and we don't want repeated
        /// log noise. The extension folder is resolved relative to the mod DLL:
        /// <c>native/x64/extensions/&lt;subfolder&gt;</c>. Returns true if a load was
        /// attempted, false if no extension folder was found (degrades gracefully
        /// — the JS-only ad-block still runs).
        ///
        /// We pick the first subfolder of <c>extensions/</c> that contains a
        /// <c>manifest.json</c>. Lets the user drop in either an unpacked uBlock
        /// build (uBlock0.chromium) or any other extension without code changes.
        /// </summary>
        public static bool TryLoadBundledExtensions(MOTDWebView wv)
        {
            if (wv == null || wv._webView == IntPtr.Zero) return false;
            return TryLoadBundledExtensions(wv._webView);
        }

        /// <summary>
        /// IntPtr overload for callers that hold a raw native instance pointer
        /// (e.g. MOTDWorldScreen, which uses its own DllImports rather than the
        /// MOTDWebView wrapper). Same once-per-process gating.
        /// </summary>
        public static bool TryLoadBundledExtensions(IntPtr instance)
        {
            if (instance == IntPtr.Zero) return false;
            if (_extensionLoadAttempted) return false;
            _extensionLoadAttempted = true;

            try
            {
                string dllDir = Path.GetDirectoryName(typeof(MOTDWebView).Assembly.Location) ?? "";
                string extRoot = Path.Combine(dllDir, "native", "x64", "extensions");
                if (!Directory.Exists(extRoot))
                {
                    Plugin.Log("No extensions directory at " + extRoot + " — ad-block stays in JS-only mode.");
                    return false;
                }

                int loaded = 0;
                foreach (string subdir in Directory.GetDirectories(extRoot))
                {
                    if (!File.Exists(Path.Combine(subdir, "manifest.json"))) continue;
                    Plugin.Log("Loading browser extension: " + subdir);
                    if (!_loadExtensionSupported) break;
                    try { _CWebViewPlugin_LoadExtension(instance, subdir); }
                    catch (EntryPointNotFoundException)
                    {
                        _loadExtensionSupported = false;
                        Plugin.LogError("WebView.dll is older than the C# mod — _CWebViewPlugin_LoadExtension missing. Falling back to JS-only ad-block; rebuild the native plugin to enable uBlock Origin.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogError("LoadExtension threw: " + ex.Message);
                        break;
                    }
                    loaded++;
                }
                if (loaded == 0)
                    Plugin.Log("Extensions directory exists but contains no manifest.json subfolders: " + extRoot);
                return loaded > 0;
            }
            catch (Exception ex)
            {
                Plugin.LogError("TryLoadBundledExtensions failed: " + ex.Message);
                return false;
            }
        }

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
            if (_preloadAttempted) return false;
            _preloadAttempted = true;

            if (!IsSupportedPlatform())
            {
                Plugin.Log("WebView mode skipped: native plugin is Windows-only (platform: " + Application.platform + ").");
                return false;
            }

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
                if (!File.Exists(path)) continue;
                Plugin.Log("Loading WebView.dll from: " + path);

                IntPtr handle;
                try { handle = LoadLibraryW(path); }
                catch (Exception ex)
                {
                    Plugin.LogError("LoadLibraryW threw for " + path + ": " + ex.Message);
                    continue;
                }

                if (handle != IntPtr.Zero)
                {
                    _nativeLoaded = true;
                    Plugin.Log("WebView.dll loaded successfully.");
                    return true;
                }
                int err = Marshal.GetLastWin32Error();
                Plugin.LogError("Failed to load WebView.dll (error " + err + "): " + path);
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
            EnsureStaticInit();

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
            // Track focus state so the global onTextInput hook + PollKeyboard don't
            // bleed keystrokes into the WebView while the user is typing in the URL
            // bar or the queue input. Without this, _hasFocus stays true forever after
            // the first click into the webview and every keystroke goes to both places.
            _targetElement.RegisterCallback<FocusInEvent>(OnFocusIn);
            _targetElement.RegisterCallback<FocusOutEvent>(OnFocusOut);

            _targetElement.focusable = true;

            if (Keyboard.current != null)
                Keyboard.current.onTextInput += OnTextInput;
        }

        private void OnFocusIn(FocusInEvent evt)
        {
            _hasFocus = true;
            _lastInteractTime = Time.unscaledTime;
        }

        private void OnFocusOut(FocusOutEvent evt)
        {
            _hasFocus = false;
        }

        /// <summary>
        /// Map a panel-space pointer position to WebView2 viewport pixels.
        ///
        /// Reference rect is <see cref="VisualElement.worldBound"/> — the actually
        /// rendered rect in panel space — rather than <c>resolvedStyle.width</c>,
        /// which can disagree with the visible rect when ancestors apply transforms.
        /// X drift that scaled with the cursor's horizontal position came from
        /// that mismatch.
        ///
        /// Y needs an explicit flip here. <c>panelPos.y - wb.yMin</c> gives the
        /// distance from the visual TOP of the element (top-down), but the native
        /// plugin's <c>winY = rectHeight - 1 - data->y</c> step assumes the
        /// incoming Y is bottom-up. Send the inverted ratio so the two flips line
        /// up: user clicks visual top → data->y ≈ rectHeight → native flips to 0
        /// → WebView2 mouse at the top of its bitmap → that bitmap row is rendered
        /// at the visual top thanks to the element's <c>scaleY(-1)</c>. The old
        /// <c>evt.localPosition</c>-based code got the right answer because UI
        /// Toolkit's local position is in pre-transform space (visual top mapped
        /// to layout bottom for this element); we're choosing panel-space for
        /// transform robustness, which means we do the flip explicitly.
        /// </summary>
        private (int x, int y) ToWebViewCoords(Vector2 panelPos)
        {
            Rect wb = _targetElement.worldBound;
            float elW = wb.width;
            float elH = wb.height;
            if (elW <= 0f || float.IsNaN(elW)) elW = _viewWidth;
            if (elH <= 0f || float.IsNaN(elH)) elH = _viewHeight;

            float localX = panelPos.x - wb.xMin;
            float localY = panelPos.y - wb.yMin;

            int x = Mathf.Clamp((int)(localX / elW * _viewWidth), 0, _viewWidth);
            // Flip Y so the native plugin's own flip lands the cursor at the
            // visual position the user clicked. See remarks above.
            int y = Mathf.Clamp((int)((1f - localY / elH) * _viewHeight), 0, _viewHeight);
            return (x, y);
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            _hasFocus = true;
            _lastInteractTime = Time.unscaledTime;
            _targetElement.Focus();
            var (x, y) = ToWebViewCoords(evt.position);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, 1); // 1 = down
            _targetElement.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            _lastInteractTime = Time.unscaledTime;
            var (x, y) = ToWebViewCoords(evt.position);
            int state = _targetElement.HasPointerCapture(evt.pointerId) ? 2 : 0; // 2 = drag, 0 = move
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, state);
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (_webView == IntPtr.Zero) return;
            var (x, y) = ToWebViewCoords(evt.position);
            _CWebViewPlugin_SendMouseEvent(_webView, x, y, 0f, 3); // 3 = up
            if (_targetElement.HasPointerCapture(evt.pointerId))
                _targetElement.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnWheel(WheelEvent evt)
        {
            if (_webView == IntPtr.Zero || !_hasFocus) return;
            _lastInteractTime = Time.unscaledTime;
            // WheelEvent doesn't expose a panel-space "position" field on every
            // Unity version, so reconstruct it from the element's worldBound +
            // the wheel event's element-local position.
            Rect wb = _targetElement.worldBound;
            Vector2 panelPos = new Vector2(wb.xMin + evt.localMousePosition.x, wb.yMin + evt.localMousePosition.y);
            var (x, y) = ToWebViewCoords(panelPos);
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
            // ESC intentionally NOT forwarded: MOTDUI uses ESC to close the overlay.
            // Forwarding here would race with Hide() (frame-order dependent) and could
            // also fire side-effects inside the page (e.g. exiting YouTube fullscreen)
            // right before we destroy the WebView.
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

        /// <summary>
        /// Register a script that runs before any page script on every navigation.
        /// Used for hooks that must beat the page's own JS (volume clamps, etc.).
        /// The script persists for the lifetime of this WebView instance. Call once
        /// after creation; subsequent calls add additional scripts (they accumulate).
        /// Older WebView.dll builds don't export this; degrade gracefully rather than
        /// crashing the spawn flow (the existing EvaluateJS fallback still works,
        /// it just leaves the ~100ms loudness-burst window open).
        /// </summary>
        public void AddScriptOnLoad(string js)
        {
            if (_webView == IntPtr.Zero || string.IsNullOrEmpty(js)) return;
            if (!_addScriptOnLoadSupported) return;
            try { _CWebViewPlugin_AddScriptOnLoad(_webView, js); }
            catch (EntryPointNotFoundException)
            {
                _addScriptOnLoadSupported = false;
                Plugin.LogError("WebView.dll is older than the C# mod — _CWebViewPlugin_AddScriptOnLoad missing. Audio bursts on page-load won't be prevented; rebuild the native plugin to fix.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("AddScriptOnLoad threw: " + ex.Message);
            }
        }
        private static bool _addScriptOnLoadSupported = true;

        /// <summary>
        /// Load an unpacked Chromium extension (e.g. uBlock Origin) into this
        /// WebView's profile. <paramref name="extensionFolderPath"/> must be an
        /// absolute path to a directory containing a manifest.json.
        ///
        /// Extensions are profile-bound and persist in the user data folder, so
        /// the call is idempotent across sessions — the runtime re-resolves the
        /// install by id. Result is reported asynchronously through the normal
        /// message queue as <c>ExtensionLoaded:&lt;id&gt;</c> or
        /// <c>ExtensionError:&lt;reason&gt;</c>; see <see cref="Update"/>.
        ///
        /// Gracefully degrades to a no-op on native plugins that don't export
        /// the symbol — older DLLs simply leave ad-block in JS-only mode.
        /// </summary>
        public void LoadExtension(string extensionFolderPath)
        {
            if (_webView == IntPtr.Zero || string.IsNullOrEmpty(extensionFolderPath)) return;
            if (!_loadExtensionSupported) return;
            try { _CWebViewPlugin_LoadExtension(_webView, extensionFolderPath); }
            catch (EntryPointNotFoundException)
            {
                _loadExtensionSupported = false;
                Plugin.LogError("WebView.dll is older than the C# mod — _CWebViewPlugin_LoadExtension missing. Falling back to JS-only ad-block; rebuild the native plugin to enable uBlock Origin.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("LoadExtension threw: " + ex.Message);
            }
        }
        private static bool _loadExtensionSupported = true;

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
        //
        // Refresh strategy: full-speed capture whenever the bitmap is actually
        // changing (video playback, animation, scrolling) OR the user has
        // recently interacted. Only throttle when the page is genuinely
        // static — detected by counting consecutive refresh-checks that found
        // no bitmap change. This avoids the old failure mode where a long
        // video would drop to 7.5fps a few seconds after starting (no input →
        // assumed idle, even though pixels were moving 30× per second).

        private const float ActiveWindowSec = 2.0f;       // input grace window
        private const int   StaticChecksForIdle = 30;     // ~0.5s @ 60fps of no change → throttle
        private const int   IdleFrameInterval = 8;        // ~7.5fps when truly idle
        private const int   NoBitmapGenFrameInterval = 6; // ~10fps when DLL lacks BitmapGeneration export
        // Real-time cap on bitmap captures. The BitmapGeneration gate skips
        // texture uploads when nothing changed, so the cap is purely about how
        // often we ASK the WebView for a new frame — too high and the STA
        // thread is overloaded; too low and high-refresh monitors see judder
        // because we sample below the page's actual paint rate. 120Hz is the
        // sweet spot for modern monitors: a 144Hz refresh sees a fresh capture
        // every ~1.2 frames, video playback looks smooth, and the STA thread
        // still has 8ms of headroom per capture cycle.
        private const float MinCaptureIntervalSec = 1f / 120f;

        private int _consecutiveStaticChecks;
        private float _lastCaptureUT;

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
                        _consecutiveStaticChecks = 0;          // and unblock fast refresh
                        _onLoaded?.Invoke(body);
                        break;
                    case "CallOnStarted":
                        _lastInteractTime = Time.unscaledTime;
                        _consecutiveStaticChecks = 0;
                        _onStarted?.Invoke(body);
                        break;
                    // Async results from LoadExtension. Logged for visibility; no
                    // gameplay reaction is required — the extension either started
                    // filtering or didn't, and a JS fallback covers the latter.
                    case "ExtensionLoaded":
                        Plugin.Log("Browser extension loaded: " + body);
                        break;
                    case "ExtensionError":
                        Plugin.LogError("Browser extension load failed: " + body
                            + " (ad-block falling back to JS-only; check that uBlock files are present and WebView2 runtime is recent enough).");
                        break;
                }
            }

            if (!_visible) return;

            // Decide whether to request a new bitmap capture this frame.
            // - Without BitmapGeneration export: fixed time-based throttle.
            // - Within input grace window OR page actively changing → every frame.
            // - Otherwise (truly idle): throttle to ~7.5fps, but probe often enough
            //   that we accelerate immediately when the page wakes up again.
            float idleSec = Time.unscaledTime - _lastInteractTime;
            bool nearInteraction = idleSec < ActiveWindowSec;
            bool pageActive = _consecutiveStaticChecks < StaticChecksForIdle;

            bool shouldRefresh;
            if (!_bitmapGenSupported)
                shouldRefresh = (Time.frameCount % NoBitmapGenFrameInterval == 0);
            else if (nearInteraction || pageActive)
                shouldRefresh = true;
            else
                shouldRefresh = (Time.frameCount % IdleFrameInterval == 0);

            // Real-time rate cap — see MinCaptureIntervalSec.
            if (shouldRefresh && Time.unscaledTime - _lastCaptureUT < MinCaptureIntervalSec)
                shouldRefresh = false;

            _CWebViewPlugin_Update(_webView, shouldRefresh, 1);

            if (!shouldRefresh) return;
            _lastCaptureUT = Time.unscaledTime;

            // Check if the native bitmap actually changed since last upload.
            // Fall back to always-upload if this DLL export is missing.
            if (_bitmapGenSupported)
            {
                try
                {
                    ulong gen = _CWebViewPlugin_BitmapGeneration(_webView);
                    if (gen == _lastBitmapGen)
                    {
                        // Same bitmap as last time — bump the static counter so we
                        // eventually decide the page is idle and drop to throttle.
                        if (_consecutiveStaticChecks < int.MaxValue)
                            _consecutiveStaticChecks++;
                        return;
                    }
                    _consecutiveStaticChecks = 0; // page is moving — stay fast
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
                _targetElement.UnregisterCallback<FocusInEvent>(OnFocusIn);
                _targetElement.UnregisterCallback<FocusOutEvent>(OnFocusOut);
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
