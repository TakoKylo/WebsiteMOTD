using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Steamworks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UIElements;

namespace WebsiteMOTD
{
    /// <summary>
    /// Full-screen MOTD overlay with embedded web content rendering.
    /// Links navigate within the overlay. Editable URL bar. Back button for history.
    /// Auto-opens Steam browser for JS-heavy sites.
    /// </summary>
    public static class MOTDUI
    {
        private static VisualElement _overlay;
        private static VisualElement _contentArea;
        private static ScrollView _scrollView;
        private static Label _statusLabel;
        private static TextField _urlField;
        private static Button _backBtn;
        private static Button _fwdBtn;
        private static bool _isVisible;
        private static string _url;
        private static readonly Stack<string> _history = new Stack<string>();
        private static readonly Stack<string> _forwardHistory = new Stack<string>();
        private static readonly List<MOTDVideoHost> _videoHosts     = new List<MOTDVideoHost>();
        private static readonly List<Coroutine>     _gifCoroutines  = new List<Coroutine>();
        // Decoded textures (GIF frames + downloaded images) need explicit Destroy to
        // free GPU memory — Unity's GC handles the C# wrapper, not the underlying
        // texture. Without tracking, every page nav with images leaks them.
        private static readonly List<Texture2D>     _managedTextures = new List<Texture2D>();
        private static string _homeUrl;

        // ── WebView mode ──
        private static MOTDWebView _webView;
        private static VisualElement _webViewElement;
        private static bool _useWebView;
        private static Button _webViewToggleBtn;
        private static int _lastWebViewWidth;
        private static int _lastWebViewHeight;

        // ── Queue panel ──
        private static VisualElement _queuePanel;          // outer container (tab + content)
        private static VisualElement _queueTab;            // thin clickable tab (always visible)
        private static Label _queueTabLabel;               // arrow/label inside the tab
        private static VisualElement _queueContent;        // expanded content (300px)
        private static VisualElement _queueNowPlayingBox;
        private static VisualElement _queueListBox;
        // Either a Button (owner veto) or a custom progress-track VisualElement
        // (vote-skip showing fill = votes/threshold). Typed as the common base so
        // both cleanup paths in Hide() work uniformly.
        private static VisualElement _voteSkipBtn;
        private static TextField _queueUrlField;
        private static Label _queueErrorLabel;
        private static IVisualElementScheduledItem _queueErrorHide;
        private static bool _queueEventSubscribed;
        // Default expanded so the vote-skip / now-playing controls are visible the
        // first time the overlay opens. Persists across Show/Hide once the user
        // collapses it, but a fresh session starts with the panel open.
        private static bool _queueExpanded = true;

        // ── Audio / screen controls (client-side only) ──
        private static float _globalVolume = 0.5f;
        private static bool _isMuted;
        private static bool _screensDisabled;
        private static Action<float> _volumeSliderSetter;
        private static Button _muteBtn;
        private static Button _screenToggleBtn;

        // ── Settings panel & minimize ──
        private static VisualElement _settingsPanel;
        private static Button _settingsBtn;
        private static Button _minimizeBtn;
        private static bool _settingsOpen;
        private static float _zoomLevel = 1.0f;
        private static bool _isMinimized;
        private static VisualElement _card;
        private static VisualElement _cardBody;
        private static VisualElement _miniBar;
        private static Button _miniMuteBtn;
        private static Action<float> _zoomSliderSetter;
        private static Button _muteSettingsBtn;

        // ── Settings persistence (delegated to ClientConfig) ──
        private static bool _settingsLoaded;

        // ── Site confirmation ──
        private static HashSet<string> _trustedDomains;
        private static VisualElement _confirmOverlay; // the confirmation dialog

        // ── Async-navigation token ──
        // Bumped on every NavigateTo / GoBack / GoForward / Hide. Fetch callbacks
        // capture the value at request time and bail if it doesn't match anymore —
        // otherwise a slow fetch from a previous navigation could render its result
        // on top of the new page, or worse, into a different overlay after re-Show.
        private static int _navToken;

        // The last URL we wrote into the URL field (any path). Used to detect whether
        // the user has been editing — if _urlField.value still equals this, they
        // haven't touched it and it's safe to overwrite with new navigation events.
        // (Focus-controller checks were unreliable: UI Toolkit panel focus can stick
        // to the URL field's inner text-input even after the user clicked back into
        // the WebView, which caused legitimate onLoaded/onStarted updates to be
        // silently swallowed.)
        private static string _lastUrlFieldValue;

        // Action maps disabled by BlockGameplayInput. Restored by UnblockGameplayInput
        // so the game's input setup goes back to whatever it was before we touched it.
        // null when we're not currently blocking (overlay closed).
        private static List<InputActionMap> _disabledActionMaps;

        // Non-zero when the overlay is currently displaying a specific queue item
        // (the URL was loaded via the queue path, not a user navigation). Injected
        // into the page as window.__motdItemId so the videoEnded JS hook can include
        // it in the message — the server then validates _current.Id == reportedId
        // and drops stale events. Cleared on any user-initiated navigation so a
        // random video the player happens to be browsing can't accidentally advance
        // the queue when it ends.
        private static long _overlayQueueItemId;

        // ── ESC-to-close poller ──
        // UI Toolkit KeyDownEvents only fire when an inner element has focus, and the
        // WebView consumes its own key input. A small polling MonoBehaviour catches ESC
        // regardless of focus state.
        private static MOTDEscPoller _escPoller;

        // ── F2 toggle poller ──
        // Lives for the whole plugin lifetime so the hotkey works even before the
        // overlay has ever been opened. Distinct from _escPoller (which only exists
        // while the overlay IS open) because that one would never get a chance to
        // see the OPEN keypress.
        private static MOTDHotkeyPoller _hotkeyPoller;

        public static bool IsVisible => _isVisible;

        /// <summary>
        /// True whenever any MOTD layer is on screen — the main overlay OR just the
        /// confirm dialog. Used by the input-gating patches so chat / scoreboard /
        /// pause stay blocked during the confirm dialog too (otherwise ESC there
        /// dismisses the dialog AND opens the pause menu underneath).
        /// </summary>
        public static bool IsAnyOverlayVisible => _isVisible || _confirmOverlay != null;

        // ─── Public API ─────────────────────────────────────────────

        public static void Show(string url)
        {
            if (Application.isBatchMode) return;

            url = (url ?? "").Trim();
            if (string.IsNullOrEmpty(url))
            {
                Plugin.LogError("MOTDUI.Show called with empty URL — ignoring.");
                return;
            }
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            string domain = GetDomain(url);
            LoadTrustedDomains();

            // If this domain is already trusted, go straight to the browser
            if (_trustedDomains.Contains(domain))
            {
                ShowConfirmed(url);
                return;
            }

            // Otherwise, show a confirmation dialog first
            ShowConfirmDialog(url, domain);
        }

        /// <summary>
        /// Actually show the MOTD overlay after the user has approved (or the domain was trusted).
        /// On platforms where the native WebView isn't available (Linux/macOS), pop the
        /// page in the Steam overlay browser instead of trying to render it inline — the
        /// HTML fallback can't handle JS-heavy MOTD pages, and Steam's overlay works on
        /// every platform the game runs on.
        /// </summary>
        private static void ShowConfirmed(string url)
        {
            if (!MOTDWebView.IsSupportedPlatform())
            {
                Plugin.Log("Opening MOTD in Steam overlay (native WebView unavailable on this platform): " + url);
                TryOpenSteamBrowser(url);
                // Nothing is shown in-game, so make sure the ESC poller doesn't linger
                // (the confirm-dialog "Open Website" path leaves it running until Hide).
                DestroyEscPoller();
                return;
            }

            _url = url;
            _homeUrl = url;
            _history.Clear();
            _forwardHistory.Clear();

            var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
            var root = uiManager?.RootVisualElement;
            if (root == null)
            {
                Plugin.LogError("MOTDUI: RootVisualElement is null, cannot show.");
                return;
            }

            DestroyMiniBar();
            _isMinimized = false;
            _overlay?.RemoveFromHierarchy();
            _confirmOverlay?.RemoveFromHierarchy();

            // If we already had a WebView, clean it up so InitWebViewIfNeeded() will
            // re-create _webViewElement and insert it into the new overlay hierarchy.
            if (_webView != null) CleanupWebView();

            LoadSettings();
            if (_screensDisabled)
                MOTDWorldScreen.SetScreensVisible(false);

            Build();
            root.Add(_overlay);

            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            _isVisible = true;
            EnsureEscPoller();
            BlockGameplayInput();
            DismissOpenChatInput();

            Plugin.Log("MOTD overlay shown for URL: " + url);

            // Start in WebView mode by default if available
            if (InitWebViewIfNeeded())
            {
                _useWebView = true;
                EnsureWebViewVisible();
                UpdateWebViewToggleButton();
            }

            NavigateTo(url, addToHistory: false);
        }

        public static void Hide()
        {
            // Invalidate in-flight HTML fetches so their callbacks don't write into a
            // freshly-rebuilt overlay if the user calls Show again before they resolve.
            _navToken++;

            DestroyMiniBar();
            if (_confirmOverlay != null)
            {
                _confirmOverlay.RemoveFromHierarchy();
                _confirmOverlay = null;
            }
            if (_overlay != null)
            {
                _overlay.RemoveFromHierarchy();
                _overlay = null;
                _contentArea = null;
                _scrollView = null;
                _statusLabel = null;
                _urlField = null;
                _lastUrlFieldValue = null;
                _backBtn = null;
                _fwdBtn = null;
                _queuePanel = null;
                _queueTab = null;
                _queueTabLabel = null;
                _queueContent = null;
                _queueNowPlayingBox = null;
                _queueListBox = null;
                _voteSkipBtn = null;
                _queueUrlField = null;
                _queueErrorLabel = null;
                _queueErrorHide = null;
                _volumeSliderSetter = null;
                _muteBtn = null;
                _screenToggleBtn = null;
                _webViewToggleBtn = null;
                _settingsBtn = null;
                _settingsPanel = null;
                _minimizeBtn = null;
                _muteSettingsBtn = null;
                _settingsOpen = false;
                _isMinimized = false;
                _card = null;
                _cardBody = null;
                _miniBar = null;
                _history.Clear();
                _forwardHistory.Clear();
                CleanupVideoHosts();
                CleanupWebView();
                UnityEngine.Cursor.visible = true;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                _isVisible = false;
            }

            if (_queueEventSubscribed)
            {
                Plugin.OnQueueChanged -= RefreshQueuePanel;
                _queueEventSubscribed = false;
            }

            DestroyEscPoller();
            UnblockGameplayInput();
        }

        /// <summary>
        /// If the game's chat input was already in-progress when the overlay opens,
        /// dismiss it. Without this, the chat field keeps focus underneath the overlay
        /// and every keystroke the user types in the URL bar also goes into chat.
        /// </summary>
        private static void DismissOpenChatInput()
        {
            try
            {
                var ui = MonoBehaviourSingleton<UIManager>.Instance;
                ui?.Chat?.StopInput();
            }
            catch (Exception ex)
            {
                Plugin.LogError("DismissOpenChatInput failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Block all gameplay/mod input while the overlay is open. Does three things:
        ///   1. Sets <c>IsMouseRequired=true</c> so PlayerInput.UpdateInputs and the
        ///      per-action handlers (Jump/Dash/BladeAngle) short-circuit. A Harmony
        ///      patch on <c>UIManager.CheckMouseRequirement</c> keeps the flag sticky
        ///      while stock UIViews toggle around us.
        ///   2. Disables every <see cref="InputActionMap"/> on every loaded
        ///      <see cref="InputActionAsset"/> — except the one feeding the UI
        ///      Toolkit input module. This is the only way to reliably block keys
        ///      bound by other mods (custom UIs, debug commands, hotkey toggles)
        ///      that don't go through stock <c>UIManager</c> handlers we've patched.
        ///   3. The WebView itself reads input through <c>Keyboard.current</c>
        ///      (device-level), not through actions, so its own typing/keystroke
        ///      forwarding keeps working with all maps disabled.
        /// </summary>
        private static void BlockGameplayInput()
        {
            try
            {
                if (!GlobalStateManager.UIState.IsMouseRequired)
                {
                    GlobalStateManager.SetUIState(new Dictionary<string, object>
                    {
                        { "isMouseRequired", true }
                    });
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("BlockGameplayInput failed: " + ex.Message);
            }

            DisableForeignActionMaps();
        }

        /// <summary>
        /// Disable every currently-enabled InputActionMap so game and mod actions
        /// stop firing. The map(s) backing the UI Toolkit input module are kept
        /// alive so the overlay's own clicks/keys still work. Idempotent — calling
        /// while already blocking is a no-op.
        /// </summary>
        private static void DisableForeignActionMaps()
        {
            if (_disabledActionMaps != null) return; // already blocking

            try
            {
                InputActionAsset uiAsset = GetUIInputAsset();
                var disabled = new List<InputActionMap>();

                // FindObjectsOfTypeAll catches assets that aren't in the active scene
                // (e.g. the global InputSystem.actions asset) which FindObjectsByType
                // misses.
                foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
                {
                    if (asset == null) continue;
                    foreach (var map in asset.actionMaps)
                    {
                        if (map == null || !map.enabled) continue;
                        // Keep UI maps alive — that's what feeds the overlay's mouse
                        // clicks and keyboard navigation. Detection by either (a) being
                        // the asset the UI input module references, or (b) containing
                        // canonical UI actions in case some game consolidated them
                        // into a non-default asset.
                        if (asset == uiAsset || IsUIActionMap(map)) continue;

                        try
                        {
                            map.Disable();
                            disabled.Add(map);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogError("Disable map '" + map.name + "' failed: " + ex.Message);
                        }
                    }
                }
                _disabledActionMaps = disabled;
            }
            catch (Exception ex)
            {
                Plugin.LogError("DisableForeignActionMaps failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Catch action maps that got enabled AFTER our initial block sweep — e.g.
        /// a mod that lazily registers an action map the first time the user uses
        /// its feature. Called periodically from <see cref="MOTDHotkeyPoller"/>
        /// while the overlay is up. Maps we already disabled are skipped via
        /// reference equality, so we don't disable the same one twice or replace
        /// existing entries.
        /// </summary>
        internal static void ReDisableForeignActionMaps()
        {
            if (_disabledActionMaps == null) return; // not currently blocking
            try
            {
                InputActionAsset uiAsset = GetUIInputAsset();
                foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
                {
                    if (asset == null || asset == uiAsset) continue;
                    foreach (var map in asset.actionMaps)
                    {
                        if (map == null || !map.enabled) continue;
                        if (IsUIActionMap(map)) continue;
                        if (_disabledActionMaps.Contains(map)) continue;

                        try
                        {
                            map.Disable();
                            _disabledActionMaps.Add(map);
                        }
                        catch (Exception ex)
                        {
                            Plugin.LogError("Late-disable map '" + map.name + "' failed: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("ReDisableForeignActionMaps failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Re-enable any maps we disabled in <see cref="DisableForeignActionMaps"/>.
        /// Skips maps that the game/mods have already re-enabled themselves and ones
        /// that have been destroyed since (scene change, mod reload).
        /// </summary>
        private static void RestoreForeignActionMaps()
        {
            if (_disabledActionMaps == null) return;
            foreach (var map in _disabledActionMaps)
            {
                if (map == null) continue;
                try
                {
                    if (!map.enabled) map.Enable();
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Re-enable map '" + map.name + "' failed: " + ex.Message);
                }
            }
            _disabledActionMaps = null;
        }

        /// <summary>
        /// Resolve the InputActionAsset feeding UI Toolkit's input module so we don't
        /// kill the overlay's own input when blocking. Returns null if the asset
        /// can't be found — we fall back to <see cref="IsUIActionMap"/> heuristics.
        /// </summary>
        private static InputActionAsset GetUIInputAsset()
        {
            try
            {
                var es = EventSystem.current;
                if (es != null)
                {
                    var module = es.GetComponent<InputSystemUIInputModule>();
                    if (module != null) return module.actionsAsset;
                }
                // Fallback: scan loaded modules in case EventSystem.current isn't set.
                foreach (var m in Resources.FindObjectsOfTypeAll<InputSystemUIInputModule>())
                {
                    if (m != null && m.actionsAsset != null) return m.actionsAsset;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("GetUIInputAsset failed: " + ex.Message);
            }
            return null;
        }

        /// <summary>
        /// Heuristic: a map containing canonical UI actions (Point/Click/Navigate/etc.)
        /// is treated as a UI map and kept enabled. Catches cases where a game/mod
        /// puts UI actions in a non-default asset that GetUIInputAsset doesn't find.
        /// </summary>
        private static bool IsUIActionMap(InputActionMap map)
        {
            foreach (var a in map.actions)
            {
                if (a == null || string.IsNullOrEmpty(a.name)) continue;
                string n = a.name;
                if (n == "Point" || n == "Click" || n == "Navigate"
                    || n == "Submit" || n == "Cancel" || n == "ScrollWheel"
                    || n == "MiddleClick" || n == "RightClick" || n == "TrackedDevicePosition"
                    || n == "TrackedDeviceOrientation")
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Re-runs the game's own <c>CheckMouseRequirement</c> so the flag reflects
        /// whatever stock views are still open (pause menu, scoreboard, etc.). We
        /// reflect into the private method rather than clearing the flag outright,
        /// because clearing it would re-enable movement while the player still has
        /// the pause menu up.
        /// </summary>
        private static void UnblockGameplayInput()
        {
            // Re-enable action maps first so input is ready by the time the mouse-
            // required flag clears. Order matters: if a game system re-evaluates
            // input state on flag-change, it should see the maps already alive.
            RestoreForeignActionMaps();

            try
            {
                var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
                if (uiManager == null)
                {
                    // The UIManager is torn down (scene change, app shutdown). Don't
                    // touch the flag — clearing it here could re-enable movement if
                    // some other view still needs the mouse. Whatever loads next will
                    // recompute state from its own UIView list.
                    return;
                }

                var method = typeof(UIManager).GetMethod(
                    "CheckMouseRequirement",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(uiManager, null);
                    return;
                }

                // Reflection failed (game rename / decompile drift). Best effort: only
                // clear the flag if no other view has registered as mouse-required.
                if (!GlobalStateManager.UIState.IsInteracting)
                {
                    GlobalStateManager.SetUIState(new Dictionary<string, object>
                    {
                        { "isMouseRequired", false }
                    });
                }
                else
                {
                    Plugin.Log("UnblockGameplayInput: CheckMouseRequirement reflection missing and other views are interactive; leaving flag alone.");
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("UnblockGameplayInput failed: " + ex.Message);
            }
        }

        /// <summary>
        /// ESC handler: closes whichever MOTD layer is open. The confirm dialog
        /// takes priority (treated as Deny) so users can dismiss it without
        /// clobbering an already-open browser overlay underneath.
        /// </summary>
        internal static void OnEscapePressedFromPoller()
        {
            if (_confirmOverlay != null)
            {
                Plugin.Log("ESC pressed — denying MOTD confirmation dialog.");
                _confirmOverlay.RemoveFromHierarchy();
                _confirmOverlay = null;
                if (!_isVisible)
                {
                    DestroyEscPoller();
                    UnblockGameplayInput();
                }
                return;
            }
            if (_isVisible)
            {
                Plugin.Log("ESC pressed — closing MOTD overlay.");
                Hide();
            }
        }

        private static void EnsureEscPoller()
        {
            if (_escPoller != null) return;
            var go = new GameObject("MOTD_EscPoller");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _escPoller = go.AddComponent<MOTDEscPoller>();
        }

        private static void DestroyEscPoller()
        {
            if (_confirmOverlay != null || _isVisible) return;
            if (_escPoller == null) return;
            UnityEngine.Object.Destroy(_escPoller.gameObject);
            _escPoller = null;
        }

        /// <summary>
        /// Spawn the F2-toggle poller. Called from Plugin setup on non-dedicated
        /// clients. Safe on Linux: the poller only reads the keyboard device and
        /// calls <see cref="Show"/>, which itself falls back to the Steam overlay
        /// when the native WebView isn't available on the platform.
        /// </summary>
        public static void EnableHotkeys()
        {
            if (_hotkeyPoller != null) return;
            try
            {
                var go = new GameObject("MOTD_HotkeyPoller");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _hotkeyPoller = go.AddComponent<MOTDHotkeyPoller>();
            }
            catch (Exception ex)
            {
                Plugin.LogError("EnableHotkeys failed: " + ex.Message);
            }
        }

        /// <summary>Tear down the F2 poller. Called on plugin disable.</summary>
        public static void DisableHotkeys()
        {
            if (_hotkeyPoller == null) return;
            try { UnityEngine.Object.Destroy(_hotkeyPoller.gameObject); }
            catch (Exception ex) { Plugin.LogError("DisableHotkeys failed: " + ex.Message); }
            _hotkeyPoller = null;
        }

        // ─── Navigation ─────────────────────────────────────────────

        /// <summary>
        /// Navigate the in-overlay browser to a new URL.
        /// Called by links, the Go button, and the Back button.
        ///
        /// <paramref name="queueItemId"/> is non-zero ONLY when the caller is the
        /// queue-load path (see <see cref="Plugin.LoadCurrentOnWorldScreens"/>) —
        /// it gets injected into the page so videoEnded events can be bound to a
        /// specific item id, preventing stale "ended" messages from advancing the
        /// queue and stopping random browsing from triggering an advance.
        /// </summary>
        public static void NavigateTo(string url, bool addToHistory = true, long queueItemId = 0, int startOffsetSec = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            // Any user-driven navigation breaks the queue-item binding: the page
            // they're going to is NOT the queue's current item (or if it happens
            // to be, the queue-load path will set this again on its own).
            _overlayQueueItemId = queueItemId;

            // Auto-restore if the player navigates while the browser is minimized
            if (_isMinimized) ToggleMinimize();

            url = url.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            // Push current page to history before navigating; clear forward on new nav
            if (addToHistory && !string.IsNullOrEmpty(_url) && _url != url)
            {
                _history.Push(_url);
                _forwardHistory.Clear();
            }

            _url = url;
            if (_urlField != null)
            {
                _urlField.value = url;
                _lastUrlFieldValue = url;
            }

            UpdateBackButton();
            UpdateForwardButton();
            ClearContent();

            // ── WebView mode: let the real browser handle everything ──
            if (_useWebView && _webView != null)
            {
                EnsureWebViewVisible();
                string embedUrl = ConvertToEmbedUrl(url, startOffsetSec);
                _webView.LoadURL(embedUrl);
                Plugin.Log("WebView navigating to (t=" + startOffsetSec + "s): " + embedUrl);
                return;
            }

            // ── Direct image URL → show it inline without HTML parsing ──
            if (IsDirectImageUrl(url))
            {
                ShowDirectImage(url);
                return;
            }

            // ── Direct video URL → play it inline ──
            if (IsDirectVideoUrl(url))
            {
                ShowDirectVideo(url);
                return;
            }

            // ── Known JS-only / video platform → skip HTML, show card + Steam browser ──
            string platformName = GetKnownPlatformName(url);
            if (platformName != null)
            {
                ShowPlatformFallback(url, platformName);
                TryOpenSteamBrowser(url);
                return;
            }

            _statusLabel = new Label("Loading...");
            _statusLabel.style.fontSize = 14f;
            _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.marginTop = 40f;
            _contentArea.Add(_statusLabel);

            Plugin.Log("Navigating to: " + url);

            int navToken = ++_navToken;
            MOTDWebContent.Fetch(url,
                onSuccess: elements =>
                {
                    if (navToken != _navToken) return; // superseded by a later nav
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();

                    if (elements.Count == 0)
                    {
                        ShowSpaFallback();
                        TryOpenSteamBrowser(url);
                    }
                    else
                    {
                        RenderContent(elements);
                    }
                },
                onError: error =>
                {
                    if (navToken != _navToken) return;
                    if (_contentArea == null || _overlay == null) return;
                    Plugin.LogError("Fetch failed: " + error);
                    RemoveStatusLabel();
                    ShowErrorState(error);
                }
            );
        }

        private static bool IsDirectImageUrl(string url)
        {
            string lower = url.Split('?')[0].ToLowerInvariant();
            return lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png")
                || lower.EndsWith(".gif")  || lower.EndsWith(".webp") || lower.EndsWith(".bmp")
                || lower.EndsWith(".svg");
        }

        private static bool IsDirectVideoUrl(string url)
        {
            string lower = url.Split('?')[0].ToLowerInvariant();
            return lower.EndsWith(".mp4") || lower.EndsWith(".webm") || lower.EndsWith(".ogv")
                || lower.EndsWith(".mov") || lower.EndsWith(".avi")  || lower.EndsWith(".mkv");
        }

        private static void ShowDirectImage(string url)
        {
            if (_contentArea == null) return;
            Plugin.Log("Direct image URL detected: " + url);
            AddImage(_contentArea, url, "");
        }

        private static void ShowDirectVideo(string url)
        {
            if (_contentArea == null) return;
            Plugin.Log("Direct video URL detected: " + url);
            AddVideoElement(_contentArea, url, "", false);
        }

        /// <summary>
        /// Returns a display name if the URL is a known JS-heavy platform
        /// that cannot be meaningfully parsed without a real browser engine.
        /// Returns null if normal HTML fetching should proceed.
        /// </summary>
        private static string GetKnownPlatformName(string url)
        {
            string lower = url.ToLowerInvariant();

            // Video platforms
            if (lower.Contains("youtube.com")    || lower.Contains("youtu.be"))   return "YouTube";
            if (lower.Contains("vimeo.com"))                                       return "Vimeo";
            if (lower.Contains("twitch.tv"))                                       return "Twitch";
            if (lower.Contains("dailymotion.com"))                                 return "Dailymotion";

            // Social / heavy SPA platforms
            if (lower.Contains("twitter.com")    || lower.Contains("x.com"))      return "Twitter / X";
            if (lower.Contains("instagram.com"))                                   return "Instagram";
            if (lower.Contains("tiktok.com"))                                      return "TikTok";
            if (lower.Contains("facebook.com")   || lower.Contains("fb.com"))     return "Facebook";
            if (lower.Contains("reddit.com"))                                      return "Reddit";
            if (lower.Contains("discord.com")    || lower.Contains("discord.gg")) return "Discord";
            if (lower.Contains("netflix.com"))                                     return "Netflix";
            if (lower.Contains("spotify.com"))                                     return "Spotify";

            return null;
        }

        private static void ShowPlatformFallback(string url, string platformName)
        {
            if (_contentArea == null) return;
            Plugin.Log(platformName + " detected — opening in Steam browser.");

            // Icon row
            var row = new VisualElement();
            row.style.flexDirection  = FlexDirection.Column;
            row.style.alignItems     = Align.Center;
            row.style.marginTop      = 40f;
            row.style.marginBottom   = 20f;
            _contentArea.Add(row);

            var icon = new Label("\uD83C\uDF10");
            icon.style.fontSize = 48f;
            icon.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(icon);

            var heading = new Label(platformName);
            heading.style.fontSize = 22f;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.color = Color.white;
            heading.style.marginTop = 10f;
            heading.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(heading);

            var sub = new Label("This site requires JavaScript and cannot be displayed inline.");
            sub.style.fontSize = 14f;
            sub.style.color = new Color(0.7f, 0.7f, 0.75f);
            sub.style.marginTop = 6f;
            sub.style.whiteSpace = WhiteSpace.Normal;
            sub.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(sub);

            var openedMsg = new Label("It has been opened in the Steam overlay browser.");
            openedMsg.style.fontSize = 13f;
            openedMsg.style.color = new Color(0.4f, 0.85f, 0.4f);
            openedMsg.style.marginTop = 4f;
            openedMsg.style.unityTextAlign = TextAnchor.MiddleCenter;
            row.Add(openedMsg);

            AddSeparator(_contentArea);

            // Quick action buttons
            var btnRow = new VisualElement();
            btnRow.style.flexDirection  = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;
            btnRow.style.flexWrap       = Wrap.Wrap;
            btnRow.style.marginTop      = 8f;
            _contentArea.Add(btnRow);

            string capturedUrl = url;
            var steamBtn = CreateStyledButton("Open in Steam Browser", new Color(0.2f, 0.45f, 0.7f), () =>
                TryOpenSteamBrowser(capturedUrl));
            steamBtn.style.marginRight = 10f;
            steamBtn.style.marginBottom = 8f;
            btnRow.Add(steamBtn);

            var extBtn = CreateStyledButton("Open in System Browser", new Color(0.35f, 0.35f, 0.4f), () =>
                OpenExternal(capturedUrl));
            extBtn.style.marginBottom = 8f;
            btnRow.Add(extBtn);
        }

        private static void GoBack()
        {
            // Back/forward is a user-driven navigation — break the queue-item
            // binding so the destination page isn't treated as the queue's
            // active item just because it happens to share a URL with one.
            _overlayQueueItemId = 0;
            _webView?.EvaluateJS("window.__motdItemId=0;");

            if (_useWebView && _webView != null)
            {
                _webView.GoBack();
                UpdateBackForwardButtons();
                return;
            }
            if (_history.Count == 0) return;
            if (!string.IsNullOrEmpty(_url))
                _forwardHistory.Push(_url);
            string prev = _history.Pop();
            _url = prev;
            if (_urlField != null) { _urlField.value = prev; _lastUrlFieldValue = prev; }
            UpdateBackButton();
            UpdateForwardButton();
            ClearContent();

            _statusLabel = new Label("Loading...");
            _statusLabel.style.fontSize = 14f;
            _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.marginTop = 40f;
            _contentArea.Add(_statusLabel);

            int navToken = ++_navToken;
            MOTDWebContent.Fetch(prev,
                onSuccess: elements =>
                {
                    if (navToken != _navToken) return;
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    if (elements.Count == 0) { ShowSpaFallback(); TryOpenSteamBrowser(prev); }
                    else RenderContent(elements);
                },
                onError: error =>
                {
                    if (navToken != _navToken) return;
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    ShowErrorState(error);
                }
            );
        }

        private static void GoForward()
        {
            // See GoBack — same reasoning for clearing the queue-item binding.
            _overlayQueueItemId = 0;
            _webView?.EvaluateJS("window.__motdItemId=0;");

            if (_useWebView && _webView != null)
            {
                _webView.GoForward();
                UpdateBackForwardButtons();
                return;
            }
            if (_forwardHistory.Count == 0) return;
            if (!string.IsNullOrEmpty(_url))
                _history.Push(_url);
            string next = _forwardHistory.Pop();
            _url = next;
            if (_urlField != null) { _urlField.value = next; _lastUrlFieldValue = next; }
            UpdateBackButton();
            UpdateForwardButton();
            ClearContent();

            _statusLabel = new Label("Loading...");
            _statusLabel.style.fontSize = 14f;
            _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.marginTop = 40f;
            _contentArea.Add(_statusLabel);

            int navToken = ++_navToken;
            MOTDWebContent.Fetch(next,
                onSuccess: elements =>
                {
                    if (navToken != _navToken) return;
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    if (elements.Count == 0) { ShowSpaFallback(); TryOpenSteamBrowser(next); }
                    else RenderContent(elements);
                },
                onError: error =>
                {
                    if (navToken != _navToken) return;
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    ShowErrorState(error);
                }
            );
        }

        /// <summary>
        /// Whether the overlay should auto-navigate when the world-screen URL changes.
        /// True when the user hasn't browsed away from the queue's current page.
        ///
        /// The reliable signal is <see cref="_overlayQueueItemId"/>: it's set when the
        /// queue-load path navigates the overlay (see Plugin.LoadCurrentOnWorldScreens)
        /// and cleared the moment the user does anything browsing-like — typing a URL,
        /// clicking a link (NavigateTo with queueItemId=0), GoBack/GoForward, or a
        /// WebView SPA navigation (navTo:). URL string comparison alone is unreliable
        /// because WebView's onLoaded rewrites <c>_url</c> to a post-redirect/canonical
        /// form that drifts from the raw <c>previousWorldUrl</c> the caller has.
        /// </summary>
        public static bool ShouldFollowWorldScreen(string previousWorldUrl)
        {
            // Bound to a queue item and untouched since — definitely follow.
            if (_overlayQueueItemId != 0) return true;
            // Not yet navigated, or sitting on the home/MOTD page — safe to follow.
            if (string.IsNullOrEmpty(_url)) return true;
            if (_url == _homeUrl) return true;
            if (_url == Plugin.MOTD_URL) return true;
            return false;
        }

        /// <summary>
        /// Update the URL bar from a WebView event, but skip if the user has edited
        /// the field since we last set it. Detection works by storing the last value
        /// we wrote and comparing — if the current field value differs, the user is
        /// mid-edit and we leave it alone.
        /// </summary>
        private static void SetUrlFieldIfNotEditing(string url)
        {
            if (_urlField == null || !_useWebView) return;
            // If the user has typed something different from what we last set, they're
            // editing — don't clobber. Otherwise (matches our last write, or first
            // update), it's safe to overwrite.
            if (_lastUrlFieldValue != null && _urlField.value != _lastUrlFieldValue)
                return;
            _urlField.value = url;
            _lastUrlFieldValue = url;
        }

        private static void UpdateBackButton()
        {
            if (_backBtn != null)
            {
                bool canBack = _useWebView && _webView != null ? _webView.CanGoBack : _history.Count > 0;
                _backBtn.SetEnabled(canBack);
                _backBtn.style.opacity = canBack ? 1f : 0.35f;
            }
        }

        private static void UpdateForwardButton()
        {
            if (_fwdBtn != null)
            {
                bool canFwd = _useWebView && _webView != null ? _webView.CanGoForward : _forwardHistory.Count > 0;
                _fwdBtn.SetEnabled(canFwd);
                _fwdBtn.style.opacity = canFwd ? 1f : 0.35f;
            }
        }

        // Called after WebView GoBack/GoForward — schedules a re-check after the
        // WebView has had a moment to update its own CanGoBack/CanGoForward state.
        private static void UpdateBackForwardButtons()
        {
            // Immediate update (may still read old state)
            UpdateBackButton();
            UpdateForwardButton();
            // Delayed re-check after WebView processes the navigation
            if (_webView != null)
                _webView.StartCoroutine(DelayedNavButtonUpdate());
        }

        private static System.Collections.IEnumerator DelayedNavButtonUpdate()
        {
            yield return new WaitForSeconds(0.4f);
            UpdateBackButton();
            UpdateForwardButton();
        }

        private static void ClearContent()
        {
            if (_contentArea == null) return;
            CleanupVideoHosts();
            _contentArea.Clear();
            _statusLabel = null;

            // Hide webview element when clearing for HTML mode
            if (_webViewElement != null && !_useWebView)
                _webViewElement.style.display = DisplayStyle.None;
        }

        // ─── WebView Mode ──────────────────────────────────────────

        private static void ToggleWebViewMode()
        {
            if (_useWebView)
            {
                _useWebView = false;
                UpdateWebViewToggleButton();
                // Only touch content visibility when settings panel is closed
                if (!_settingsOpen)
                {
                    HideWebViewElement();
                    if (_scrollView != null) _scrollView.style.display = DisplayStyle.Flex;
                    if (!string.IsNullOrEmpty(_url))
                        NavigateTo(_url, addToHistory: false);
                }
            }
            else
            {
                if (!InitWebViewIfNeeded())
                {
                    Plugin.LogError("WebView not available — WebView.dll not found.");
                    return;
                }
                _useWebView = true;
                UpdateWebViewToggleButton();
                // Only touch content visibility when settings panel is closed
                if (!_settingsOpen)
                {
                    if (_scrollView != null) _scrollView.style.display = DisplayStyle.None;
                    EnsureWebViewVisible();
                    if (!string.IsNullOrEmpty(_url))
                        _webView.LoadURL(_url);
                }
            }
        }

        private static bool InitWebViewIfNeeded()
        {
            if (_webView != null) return true;
            if (!MOTDWebView.PreloadNativeDLL()) return false;

            // Create the VisualElement that will display the webview texture
            _webViewElement = new VisualElement();
            _webViewElement.name = "WebViewDisplay";
            _webViewElement.style.flexGrow = 1f;
            _webViewElement.style.display = DisplayStyle.None;
            // Texture is sized to match the element's aspect ratio (see GeometryChangedEvent
            // below), so a plain 100%/100% fill leaves no dead space and no stretch/squish.
            _webViewElement.style.backgroundSize = new BackgroundSize(new Length(100f, LengthUnit.Percent), new Length(100f, LengthUnit.Percent));
            // Flip Y: WebView2 bitmap is top-down, Unity texture row 0 is bottom
            _webViewElement.style.scale = new StyleScale(new Scale(new Vector3(1f, -1f, 1f)));

            // Insert webview element as sibling to scrollview (inside the card)
            if (_scrollView != null && _scrollView.parent != null)
                _scrollView.parent.Insert(_scrollView.parent.IndexOf(_scrollView) + 1, _webViewElement);

            // Initial viewport — replaced by the GeometryChangedEvent below as soon as
            // the element is laid out, so the texture aspect matches the card.
            int wvWidth = 1920;
            int wvHeight = 1080;
            _lastWebViewWidth = wvWidth;
            _lastWebViewHeight = wvHeight;
            Plugin.Log("WebView initial viewport: " + wvWidth + "x" + wvHeight);

            // Resize the WebView texture whenever the display element's layout changes,
            // so the browser viewport always matches the card aspect ratio AND the
            // texture matches the element's actual on-screen pixel size. The old code
            // pinned the texture to 1080p reference and let UI Toolkit upscale it,
            // which looked fuzzy on 1440p / 4K displays and downscale-fuzzy on small
            // windowed panels. Reading the panel's scaledPixelsPerPoint converts the
            // panel-space rect into real screen pixels, giving (close to) 1:1 sampling.
            // Clamped to [720p, 2160p] so we don't waste GPU on extreme outliers.
            _webViewElement.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                if (_webView == null) return;
                float w = evt.newRect.width;
                float h = evt.newRect.height;
                if (w <= 1f || h <= 1f) return;

                float ppp = 1f;
                if (_webViewElement.panel != null)
                {
                    float p = _webViewElement.panel.scaledPixelsPerPoint;
                    if (p > 0.01f) ppp = p;
                }

                int targetH = Mathf.Clamp(Mathf.RoundToInt(h * ppp), 720, 2160);
                int targetW = Mathf.Clamp(Mathf.RoundToInt(w * ppp), 1280, 3840);

                if (Mathf.Abs(targetW - _lastWebViewWidth) < 16 && Mathf.Abs(targetH - _lastWebViewHeight) < 16)
                    return;
                _lastWebViewWidth = targetW;
                _lastWebViewHeight = targetH;
                _webView.SetSize(targetW, targetH);
            });

            _webView = MOTDWebView.Create(
                _webViewElement,
                wvWidth, wvHeight,
                onLoaded: url =>
                {
                    // Hook is already installed via AddScriptOnLoad below — this is a
                    // no-op fast path for clarity. EvaluateJS at OnLoaded still pushes
                    // the up-to-date volume value into __motdVol after every navigation.

                    Plugin.Log("WebView loaded: " + url);
                    _url = url;
                    SetUrlFieldIfNotEditing(url);
                    InjectAdBlockJS();
                    ApplyWebViewVolume();
                    // Bind the page to its queue item id (if any) BEFORE the media
                    // hook installs — the hook reads window.__motdItemId when it
                    // fires "ended". 0 means "not a queue item, ignore endings."
                    _webView?.EvaluateJS("window.__motdItemId=" + _overlayQueueItemId + ";");
                    InjectMediaHelperJS();
                    ApplyWebViewZoom();
                    UpdateBackForwardButtons();
                },
                onStarted: url =>
                {
                    Plugin.Log("WebView started: " + url);
                    _url = url;
                    SetUrlFieldIfNotEditing(url);
                    UpdateBackForwardButtons();
                },
                onError: err =>
                {
                    Plugin.LogError("WebView error: " + err);
                },
                onJS: msg =>
                {
                    if (msg.StartsWith("videoEnded:"))
                    {
                        if (long.TryParse(msg.Substring(11), out long endedId))
                            Plugin.VideoEnded(endedId);
                    }
                    else if (msg.StartsWith("navTo:"))
                    {
                        // SPA navigation reported from the in-page nav-tracker hook —
                        // see NavTrackerJS. The native plugin only fires onLoaded for
                        // full document loads, so without this the URL bar stayed
                        // stale while clicking around YouTube etc.
                        string newUrl = msg.Substring(6);
                        if (!string.IsNullOrEmpty(newUrl))
                        {
                            _url = newUrl;
                            // SPA navigation breaks the queue-item binding — the
                            // player just clicked away from whatever queue item
                            // they were on. Re-bind window.__motdItemId to 0 so
                            // an "ended" event on this new page can't be mistaken
                            // for the queue item ending.
                            _overlayQueueItemId = 0;
                            _webView?.EvaluateJS("window.__motdItemId=0;");
                            SetUrlFieldIfNotEditing(newUrl);
                            UpdateBackForwardButtons();
                        }
                    }
                }
            );

            // Install the volume hook BEFORE any page script can run. WebView2 honors
            // this for every future navigation, so we no longer race against the page's
            // own player setup — the engine default 1.0 never reaches the speakers.
            _webView?.AddScriptOnLoad(PersistentVolumeHookJS);
            // SPA URL tracker — see NavTrackerJS. Catches history.pushState etc. that
            // the native NavigationStarting/Completed events miss.
            _webView?.AddScriptOnLoad(NavTrackerJS);

            // Try to load any bundled extension (uBlock Origin) into the shared
            // profile. Profile-bound + idempotent, so this is a no-op on the
            // second/subsequent WebView spawn. Logs result via the message queue.
            if (_webView != null)
                MOTDWebView.TryLoadBundledExtensions(_webView);

            return _webView != null;
        }

        private static void EnsureWebViewVisible()
        {
            if (_webViewElement != null)
                _webViewElement.style.display = DisplayStyle.Flex;
            if (_scrollView != null)
                _scrollView.style.display = DisplayStyle.None;
        }

        private static void HideWebViewElement()
        {
            if (_webViewElement != null)
                _webViewElement.style.display = DisplayStyle.None;
        }

        private static void CleanupWebView()
        {
            _useWebView = false;
            if (_webView != null)
            {
                _webView.Cleanup();
                _webView = null;
            }
            if (_webViewElement != null)
            {
                _webViewElement.RemoveFromHierarchy();
                _webViewElement = null;
            }
        }

        // ─── URL Conversion (autoplay/fullscreen embeds) ────────────

        /// <summary>
        /// Converts YouTube, YouTube Music, Twitch, and Spotify URLs into
        /// embeddable autoplay/fullscreen variants for the WebView.
        /// Returns the original URL unchanged if no conversion applies.
        ///
        /// <paramref name="startOffsetSec"/>, when &gt; 0, appends a platform-
        /// appropriate seek param so a late-joining client jumps into the
        /// video where it's actually playing (mid-join sync). Ignored for
        /// platforms with no meaningful seek (Twitch live, Spotify).
        /// </summary>
        public static string ConvertToEmbedUrl(string url, int startOffsetSec = 0)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string lower = url.ToLowerInvariant();

            // ── YouTube watch / short / live links ──
            string ytId = null;
            if (lower.Contains("youtube.com/watch"))
            {
                ytId = ExtractQueryParam(url, "v");
            }
            else if (lower.Contains("youtu.be/"))
            {
                try { ytId = new Uri(url).AbsolutePath.TrimStart('/').Split('?')[0].Split('/')[0]; } catch { }
            }
            else if (lower.Contains("youtube.com/shorts/"))
            {
                try
                {
                    int idx = lower.IndexOf("/shorts/") + 8;
                    ytId = url.Substring(idx).Split('?')[0].Split('/')[0];
                }
                catch { }
            }
            else if (lower.Contains("youtube.com/live/"))
            {
                try
                {
                    int idx = lower.IndexOf("/live/") + 6;
                    ytId = url.Substring(idx).Split('?')[0].Split('/')[0];
                }
                catch { }
            }
            // YouTube Music – music.youtube.com/watch?v=ID
            else if (lower.Contains("music.youtube.com/watch"))
            {
                ytId = ExtractQueryParam(url, "v");
            }

            if (!string.IsNullOrEmpty(ytId))
            {
                // Use the full watch page, NOT the /embed/ URL.
                // The embed player requires an iframe origin and returns error 153 when loaded
                // directly in WebView2. youtube.com/watch works natively in WebView2 (Edge).
                string baseUrl = "https://www.youtube.com/watch?v=" + ytId + "&autoplay=1";
                if (startOffsetSec > 0) baseUrl += "&t=" + startOffsetSec + "s";
                return baseUrl;
            }

            // ── Twitch channels and VODs ──
            if (lower.Contains("twitch.tv"))
            {
                try
                {
                    var uri = new Uri(url);
                    string path = uri.AbsolutePath.TrimStart('/');
                    if (path.StartsWith("videos/"))
                    {
                        string vodId = path.Substring(7).Split('/')[0].Split('?')[0];
                        string vodUrl = "https://player.twitch.tv/?video=" + vodId
                            + "&parent=localhost&autoplay=true&muted=false";
                        // Twitch VOD seek uses HhMmSs (the embed player accepts the same
                        // format as the watch page). Live channels (handled below) can't
                        // be seeked into — joining mid-stream gets the live edge.
                        if (startOffsetSec > 0)
                            vodUrl += "&time=" + FormatHhMmSs(startOffsetSec);
                        return vodUrl;
                    }
                    string channel = path.Split('/')[0];
                    if (!string.IsNullOrEmpty(channel)
                        && channel != "directory" && channel != "settings"
                        && channel != "downloads" && channel != "subscriptions")
                    {
                        return "https://player.twitch.tv/?channel=" + channel
                            + "&parent=localhost&autoplay=true&muted=false";
                    }
                }
                catch { }
            }

            // ── Spotify ──
            if (lower.Contains("open.spotify.com/"))
            {
                try
                {
                    var uri = new Uri(url);
                    string path = uri.AbsolutePath.TrimStart('/');
                    if (path.StartsWith("embed/")) return url;
                    if (path.StartsWith("track/") || path.StartsWith("album/")
                        || path.StartsWith("playlist/") || path.StartsWith("episode/")
                        || path.StartsWith("show/"))
                    {
                        return "https://open.spotify.com/embed/" + path;
                    }
                }
                catch { }
            }

            // ── Direct media files: HTML5 media-fragment seek (#t=Ns) ──
            // Works for <video>/<audio> the browser plays natively from a URL.
            if (startOffsetSec > 0 && (IsDirectVideoUrl(url)))
            {
                // Don't double-append if the caller already supplied a fragment.
                if (url.IndexOf('#') < 0) return url + "#t=" + startOffsetSec;
            }

            return url;
        }

        private static string FormatHhMmSs(int totalSec)
        {
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            return h + "h" + m + "m" + s + "s";
        }

        private static string ExtractQueryParam(string url, string key)
        {
            int q = url.IndexOf('?');
            if (q < 0) return null;
            string queryStr = url.Substring(q + 1);
            string[] pairs = queryStr.Split('&');
            foreach (var pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        // ─── Ad-Block & Volume JS Injection ─────────────────────────

        internal static readonly string AdBlockJS =
            "(function(){" +
            "if(window.__motdAdBlock)return;" +
            "window.__motdAdBlock=true;" +
            "var s=document.createElement('style');" +
            "s.textContent='" +
            ".ytp-ad-module,.ytp-ad-overlay-container,.video-ads," +
            ".ytd-promoted-sparkles-web-renderer,.ytd-display-ad-renderer," +
            ".ytd-companion-slot-renderer,.ytd-action-companion-ad-renderer," +
            ".ytd-in-feed-ad-layout-renderer,.ytp-ad-text,.ytp-ad-image," +
            "#player-ads,.ytd-merch-shelf-renderer,.ad-container,.ad-banner," +
            "[id^=google_ads],.adsbygoogle," +
            "[data-a-target=video-ad-countdown]," +
            "[data-test-selector=sad-overlay],.stream-display-ad," +
            ".ad-slot,[data-testid=ad-slot]" +
            "{display:none!important;visibility:hidden!important;height:0!important;}';" +
            "document.head.appendChild(s);" +
            "setInterval(function(){" +
            "var skip=document.querySelector('.ytp-ad-skip-button,.ytp-ad-skip-button-modern,.ytp-skip-ad-button');" +
            "if(skip)skip.click();" +
            "var v=document.querySelector('video');" +
            "if(v&&document.querySelector('.ad-showing')){v.currentTime=v.duration||9999;v.playbackRate=16;}" +
            "},300);" +
            "})();";

        public static float EffectiveVolume => _isMuted ? 0f : _globalVolume;

        /// <summary>
        /// The persistent hook script installed once at WebView creation (via
        /// AddScriptToExecuteOnDocumentCreated). It runs before any page JS on
        /// every navigation, so the engine's default 1.0 volume never reaches
        /// the audio output. <see cref="BuildVolumeJS"/> only pushes the value;
        /// the actual clamping is enforced by this hook.
        ///
        /// What it installs:
        ///   • A safe default for window.__motdVol (0) so videos can't play loud
        ///     before the first BuildVolumeJS arrives via EvaluateJS.
        ///   • A getter/setter override on HTMLMediaElement.prototype.volume that
        ///     snaps every write to __motdVol.
        ///   • A play() wrapper that sets the volume immediately before playback.
        ///   • A capture-phase loadstart listener for lazy-loaded players.
        ///   • A MutationObserver as a final safety net.
        /// </summary>
        /// <summary>
        /// SPA navigation tracker. The native WebView2 plugin only fires
        /// CallOnStarted/CallOnLoaded for full-document navigations, so URL changes
        /// driven by history.pushState/replaceState (YouTube video-to-video clicks,
        /// any modern SPA) never reach C# — the URL bar stays stale.
        ///
        /// This hook monkey-patches history.pushState/replaceState and listens for
        /// popstate + hashchange, posting "navTo:&lt;url&gt;" back to C# whenever the
        /// address actually changes. Installed via AddScriptOnLoad so it runs at
        /// document-start, before any page script can shim history itself.
        ///
        /// Guarded by window.__motdNavHook to stay idempotent across persistent
        /// documents (SPA pushState doesn't reload, so the same script wouldn't run
        /// twice — but full navigations get a fresh window and re-install cleanly).
        /// </summary>
        internal const string NavTrackerJS =
            "(function(){" +
            "if(window.__motdNavHook)return;" +
            "window.__motdNavHook=true;" +
            "var last=location.href;" +
            "function send(){" +
            "try{var h=location.href;if(h===last)return;last=h;" +
            "if(window.Unity&&typeof window.Unity.call==='function')" +
            "window.Unity.call('navTo:'+h);}catch(_e){}" +
            "}" +
            "try{var p=history.pushState;history.pushState=function(){" +
            "var r=p.apply(this,arguments);send();return r;};}catch(_e){}" +
            "try{var rp=history.replaceState;history.replaceState=function(){" +
            "var r=rp.apply(this,arguments);send();return r;};}catch(_e){}" +
            "window.addEventListener('popstate',send);" +
            "window.addEventListener('hashchange',send);" +
            // Light safety net for SPAs that route via a custom mechanism — 1Hz poll
            // is imperceptible cost. Real SPAs (YouTube, Twitch) use the history API
            // so this rarely fires, but cheap insurance.
            "setInterval(send,1000);" +
            "})();";

        internal const string PersistentVolumeHookJS =
            "(function(){" +
            "if(window.__motdVolHook)return;" +
            "window.__motdVolHook=true;" +
            // Default to silence until C# pushes a real value. Without this, the
            // setter override would let through the engine default of 1.0 because
            // __motdVol would be undefined.
            "if(window.__motdVol==null)window.__motdVol=0;" +
            "function apply(){try{document.querySelectorAll('video,audio').forEach(function(e){try{e.volume=window.__motdVol;}catch(_e){}});}catch(_e){}}" +
            "try{var p=HTMLMediaElement.prototype;var d=Object.getOwnPropertyDescriptor(p,'volume');" +
            "if(d&&d.set&&d.get){Object.defineProperty(p,'volume',{configurable:true," +
            "get:function(){return d.get.call(this);}," +
            "set:function(v){var o=window.__motdVol;d.set.call(this,(o!=null)?o:v);}});}}catch(_e){}" +
            "try{var op=HTMLMediaElement.prototype.play;HTMLMediaElement.prototype.play=function(){try{this.volume=window.__motdVol;}catch(_e){}return op.apply(this,arguments);};}catch(_e){}" +
            "try{document.addEventListener('loadstart',function(e){var t=e.target;if(t&&(t.tagName==='VIDEO'||t.tagName==='AUDIO')){try{t.volume=window.__motdVol;}catch(_e){}}},true);}catch(_e){}" +
            "try{new MutationObserver(apply).observe(document.body||document.documentElement,{childList:true,subtree:true});}catch(_e){}" +
            "})();";

        /// <summary>
        /// Push a volume value into the page AND install the clamping hooks if they
        /// aren't already in place. The hook setup is normally done once at WebView
        /// creation by <see cref="PersistentVolumeHookJS"/> — but if the native plugin
        /// is older than the C# mod (missing _CWebViewPlugin_AddScriptOnLoad), the
        /// persistent injection silently no-ops and this is the only thing keeping
        /// videos from playing at the engine default 1.0.
        ///
        /// The hook block is guarded by <c>window.__motdVolHook</c>, so it's idempotent
        /// — runs once per page, regardless of how many times BuildVolumeJS is invoked.
        /// </summary>
        internal static string BuildVolumeJS(float volume)
        {
            string volStr = volume.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return
                "(function(){" +
                "window.__motdVol=" + volStr + ";" +
                "function apply(){try{document.querySelectorAll('video,audio').forEach(function(e){try{e.volume=window.__motdVol;}catch(_e){}});}catch(_e){}}" +
                "apply();" +
                "if(!window.__motdVolHook){" +
                "window.__motdVolHook=true;" +
                // Setter override: every volume write snaps to __motdVol. Without this,
                // a re-render or DOM swap (e.g. chat-notification UI churn that causes
                // YouTube to re-mount its player) lets a fresh element play at 1.0
                // until the next observer tick.
                "try{var p=HTMLMediaElement.prototype;var d=Object.getOwnPropertyDescriptor(p,'volume');" +
                "if(d&&d.set&&d.get){Object.defineProperty(p,'volume',{configurable:true," +
                "get:function(){return d.get.call(this);}," +
                "set:function(v){var o=window.__motdVol;d.set.call(this,(o!=null)?o:v);}});}}catch(_e){}" +
                "try{var op=HTMLMediaElement.prototype.play;HTMLMediaElement.prototype.play=function(){try{this.volume=window.__motdVol;}catch(_e){}return op.apply(this,arguments);};}catch(_e){}" +
                "try{document.addEventListener('loadstart',function(e){var t=e.target;if(t&&(t.tagName==='VIDEO'||t.tagName==='AUDIO')){try{t.volume=window.__motdVol;}catch(_e){}}},true);}catch(_e){}" +
                "try{new MutationObserver(apply).observe(document.body||document.documentElement,{childList:true,subtree:true});}catch(_e){}" +
                "}" +
                "})();";
        }

        private static void InjectAdBlockJS()
        {
            if (_webView == null) return;
            _webView.EvaluateJS(AdBlockJS);
        }

        /// <summary>
        /// Injected on every page load in the overlay WebView.
        /// Listens for the video 'ended' event (fires synchronously before the player
        /// can change src or navigate away) and notifies C# to advance the queue.
        /// A MutationObserver attaches the listener to video elements added after inject.
        /// (YouTube chrome is NOT hidden here — the user is browsing interactively.)
        ///
        /// The "ended" message includes window.__motdItemId so the server can validate
        /// it's still the active queue item before advancing. If __motdItemId is 0
        /// (random page the player navigated to), the hook silently drops the event
        /// — random browsing must not advance the shared queue.
        /// </summary>
        private static void InjectMediaHelperJS()
        {
            if (_webView == null) return;
            _webView.EvaluateJS(
                "(function(){" +
                "if(window.__motdMedia)return;" +
                "window.__motdMedia=true;" +
                // YouTube autoplay-next killer. The autoplay-toggle button in the
                // player chrome controls whether the next recommended video plays
                // when the current one ends. If it's ON, YouTube swaps the player
                // src to the next video within ~1s of 'ended' firing, and our
                // delayed notifyEnded check below incorrectly bails (it sees a
                // video "still playing" and assumes ad→main transition). Click
                // the toggle off as soon as we can find it; the preference then
                // persists via YouTube's PREF cookie, so we don't have to keep
                // watching.
                //
                // Selector + aria-label safety: YouTube periodically renames its
                // class names, so we verify the matched element's aria-label
                // mentions "autoplay" before clicking. Avoids us hitting an
                // unrelated button if .ytp-autonav-toggle-button gets reused for
                // something else in a future YT release.
                //
                // Observer auto-disconnects: once we've successfully toggled the
                // button, or after 30s (player chrome is loaded by then in every
                // case I've seen), we stop watching. Without this, we'd run a
                // querySelector on every DOM mutation forever — YouTube mutates
                // the DOM dozens of times per second during playback, so the
                // ambient cost is non-trivial.
                "var _ytObs=null,_ytKilled=false;" +
                "function killYtAutoplay(){" +
                "if(_ytKilled)return;" +
                "try{var t=document.querySelector('.ytp-autonav-toggle-button');" +
                "if(!t)return;" +
                "var label=(t.getAttribute('aria-label')||'').toLowerCase();" +
                "if(label.indexOf('autoplay')===-1)return;" + // wrong button — bail
                "if(t.getAttribute('aria-checked')==='true'){try{t.click();}catch(_e){}}" +
                "_ytKilled=true;" +
                "if(_ytObs){_ytObs.disconnect();_ytObs=null;}" +
                "}catch(_e){}" +
                "}" +
                "setTimeout(killYtAutoplay,800);" +
                "setTimeout(killYtAutoplay,2500);" +
                "setTimeout(killYtAutoplay,5000);" +
                "try{_ytObs=new MutationObserver(killYtAutoplay);" +
                "_ytObs.observe(document.body||document.documentElement,{childList:true,subtree:true});" +
                "setTimeout(function(){if(_ytObs){_ytObs.disconnect();_ytObs=null;}},30000);" +
                "}catch(_e){}" +
                "var _sent=false;" +
                "function notifyEnded(){" +
                "var id=window.__motdItemId||0;" +
                "if(id===0)return;" + // not a queue item — drop
                // Ad → main transition can fire 'ended' on the ad video; by the time
                // this delayed notify runs, the main video is already playing in the
                // same element. Recheck — but ONLY inside the main player container,
                // because hover-preview loops on YouTube's recommended-videos rail are
                // also <video> elements that report !paused && !ended, and the overlay
                // browse view shows those previews; checking them all would reset the
                // guard for legitimate end-of-video events.
                "var mp=document.querySelector('#movie_player, .html5-video-player');" +
                "if(mp){" +
                "var vids=mp.querySelectorAll('video');" +
                "for(var i=0;i<vids.length;i++){" +
                "var w=vids[i];" +
                "if(w&&!w.paused&&!w.ended&&w.currentTime>0.5&&isFinite(w.duration)&&w.currentTime<w.duration-1){" +
                "_sent=false;return;}}" +
                "}" +
                "if(window.Unity&&typeof window.Unity.call==='function')" +
                "window.Unity.call('videoEnded:'+id);" +
                "}" +
                // onEnded: guard against ads and duplicate signals
                "function onEnded(e){" +
                "var v=e.target;" +
                "if(!v||!isFinite(v.duration)||v.duration<=5)return;" +
                // AdBlock JS fast-forwards ads via playbackRate=16; surviving rate at
                // 'ended' time tells us this was an ad we sped past, not the queued video.
                "if(v.playbackRate>3)return;" +
                // Fallback: YouTube ad markers on the player container.
                "var p=v.closest&&v.closest('.html5-video-player');" +
                "if(p&&(p.classList.contains('ad-showing')||p.classList.contains('ad-interrupting')))return;" +
                "if(document.querySelector('.ad-showing'))return;" +
                "if(_sent)return;" +
                "_sent=true;" +
                "setTimeout(notifyEnded,1500);" +
                "}" +
                // Attach the ended listener once per element
                "function attach(v){" +
                "if(!v||v.__motdBound)return;" +
                "v.__motdBound=true;" +
                "v.addEventListener('ended',onEnded);" +
                "}" +
                // Attach to any video elements already in the DOM
                "document.querySelectorAll('video').forEach(attach);" +
                // Watch for video elements added later (async player init, SPA navigation)
                "new MutationObserver(function(){" +
                "document.querySelectorAll('video').forEach(attach);" +
                "}).observe(document.body||document.documentElement,{childList:true,subtree:true});" +
                "})();"
            );
        }

        private static void ApplyWebViewVolume()
        {
            if (_webView == null) return;
            float vol = _isMuted ? 0f : _globalVolume;
            _webView.EvaluateJS(BuildVolumeJS(vol));
        }

        private static void ApplyWorldScreenVolume()
        {
            float vol = _isMuted ? 0f : _globalVolume;
            MOTDWorldScreen.SetVolume(vol);
        }

        private static void ApplyAllVolumes()
        {
            float effectiveVol = _isMuted ? 0f : _globalVolume;
            ApplyWebViewVolume();
            ApplyWorldScreenVolume();
            foreach (var h in _videoHosts)
                if (h != null) h.SetVolume(effectiveVol);
        }

        // ─── Audio / Screen Toggle ──────────────────────────────────

        private static void ToggleMute()
        {
            _isMuted = !_isMuted;
            UpdateMuteButton();
            if (_volumeSliderSetter != null)
                _volumeSliderSetter(_isMuted ? 0f : _globalVolume);
            ApplyAllVolumes();
            SaveSettings();
        }

        private static void UpdateMuteButton()
        {
            if (_muteBtn != null)
            {
                _muteBtn.text = _isMuted ? "Muted" : "Mute";
                _muteBtn.tooltip = _isMuted ? "Unmute" : "Mute";
                _muteBtn.style.backgroundColor = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.25f, 0.25f, 0.3f);
                _muteBtn.style.color = _isMuted ? new Color(1f, 0.7f, 0.7f) : Color.white;
            }
            if (_muteSettingsBtn != null)
            {
                _muteSettingsBtn.text = _isMuted ? "Muted" : "Unmuted";
                _muteSettingsBtn.style.backgroundColor = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.18f, 0.25f, 0.35f);
                _muteSettingsBtn.style.color = _isMuted ? new Color(1f, 0.4f, 0.4f) : new Color(0.75f, 0.75f, 0.8f);
            }
            if (_miniMuteBtn != null)
            {
                _miniMuteBtn.text    = _isMuted ? "Muted" : "Mute";
                _miniMuteBtn.tooltip = _isMuted ? "Unmute" : "Mute";
                _miniMuteBtn.style.color = _isMuted ? new Color(1f, 0.7f, 0.7f) : Color.white;
                _miniMuteBtn.style.backgroundColor = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.25f, 0.25f, 0.3f);
            }
        }

        private static void ToggleScreens()
        {
            _screensDisabled = !_screensDisabled;
            MOTDWorldScreen.SetScreensVisible(!_screensDisabled);
            UpdateScreenToggleButton();
            SaveSettings();
        }

        private static void UpdateScreenToggleButton()
        {
            if (_screenToggleBtn == null) return;
            _screenToggleBtn.text = _screensDisabled ? "Screens Off" : "Screens On";
            _screenToggleBtn.tooltip = _screensDisabled ? "Enable Screens" : "Disable Screens";
            _screenToggleBtn.style.backgroundColor = _screensDisabled
                ? new Color(0.5f, 0.15f, 0.15f) : new Color(0.15f, 0.42f, 0.22f);
        }

        // ─── Settings Panel & Minimize ──────────────────────────────

        private static void ToggleSettings()
        {
            _settingsOpen = !_settingsOpen;
            if (_settingsPanel != null)
                _settingsPanel.style.display = _settingsOpen ? DisplayStyle.Flex : DisplayStyle.None;
            if (_settingsOpen)
            {
                if (_scrollView != null)    _scrollView.style.display     = DisplayStyle.None;
                if (_webViewElement != null) _webViewElement.style.display = DisplayStyle.None;
            }
            else
            {
                // Restore whatever rendering mode is currently active
                if (_useWebView && _webView != null) EnsureWebViewVisible();
                else if (_scrollView != null)        _scrollView.style.display = DisplayStyle.Flex;
            }
            UpdateSettingsButton();
        }

        private static void UpdateSettingsButton()
        {
            if (_settingsBtn == null) return;
            if (_settingsOpen)
            {
                _settingsBtn.style.color        = new Color(0.85f, 0.55f, 1.0f);
                _settingsBtn.style.borderTopWidth    = 0f;
                _settingsBtn.style.borderBottomWidth = 0f;
                _settingsBtn.style.borderLeftWidth   = 0f;
                _settingsBtn.style.borderRightWidth  = 0f;
            }
            else
            {
                _settingsBtn.style.color        = new Color(0.7f, 0.7f, 0.7f);
                _settingsBtn.style.borderTopWidth    = 0f;
                _settingsBtn.style.borderBottomWidth = 0f;
                _settingsBtn.style.borderLeftWidth   = 0f;
                _settingsBtn.style.borderRightWidth  = 0f;
            }
        }

        private static void ToggleMinimize()
        {
            _isMinimized = !_isMinimized;
            if (_isMinimized)
            {
                // Hide the main overlay (card + backdrop)
                if (_overlay != null) _overlay.style.display = DisplayStyle.None;
                // Build and show floating mini-bar in bottom-right of root
                BuildAndShowMiniBar();
            }
            else
            {
                // Restore main overlay
                if (_overlay != null) _overlay.style.display = DisplayStyle.Flex;
                DestroyMiniBar();
                if (_minimizeBtn != null) _minimizeBtn.text = "Min";
            }
        }

        private static void BuildAndShowMiniBar()
        {
            DestroyMiniBar();
            var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
            var root = uiManager?.RootVisualElement;
            if (root == null) return;

            _miniBar = new VisualElement();
            _miniBar.style.position = Position.Absolute;
            _miniBar.style.right    = 20f;
            _miniBar.style.bottom   = 20f;
            _miniBar.style.flexDirection   = FlexDirection.Row;
            _miniBar.style.alignItems      = Align.Center;
            _miniBar.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f, 0.95f);
            _miniBar.style.paddingLeft  = 10f;
            _miniBar.style.paddingRight = 10f;
            _miniBar.style.paddingTop    = 6f;
            _miniBar.style.paddingBottom = 6f;
            _miniBar.style.borderTopLeftRadius    = 8f;
            _miniBar.style.borderTopRightRadius   = 8f;
            _miniBar.style.borderBottomLeftRadius  = 8f;
            _miniBar.style.borderBottomRightRadius = 8f;
            _miniBar.style.borderTopWidth    = 1f;
            _miniBar.style.borderBottomWidth = 1f;
            _miniBar.style.borderLeftWidth   = 1f;
            _miniBar.style.borderRightWidth  = 1f;
            _miniBar.style.borderTopColor    = new Color(0.25f, 0.25f, 0.3f);
            _miniBar.style.borderBottomColor = new Color(0.25f, 0.25f, 0.3f);
            _miniBar.style.borderLeftColor   = new Color(0.25f, 0.25f, 0.3f);
            _miniBar.style.borderRightColor  = new Color(0.25f, 0.25f, 0.3f);

            // Mute button \u2014 text label so it renders regardless of font glyph coverage
            _miniMuteBtn = new Button(ToggleMute);
            _miniMuteBtn.text    = _isMuted ? "Muted" : "Mute";
            _miniMuteBtn.tooltip = _isMuted ? "Unmute" : "Mute";
            _miniMuteBtn.style.fontSize = 12f;
            _miniMuteBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _miniMuteBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _miniMuteBtn.style.backgroundColor = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.25f, 0.25f, 0.3f);
            _miniMuteBtn.style.color = _isMuted ? new Color(1f, 0.7f, 0.7f) : Color.white;
            _miniMuteBtn.style.borderTopWidth = _miniMuteBtn.style.borderBottomWidth =
                _miniMuteBtn.style.borderLeftWidth = _miniMuteBtn.style.borderRightWidth = 0f;
            _miniMuteBtn.style.borderTopLeftRadius = _miniMuteBtn.style.borderTopRightRadius =
                _miniMuteBtn.style.borderBottomLeftRadius = _miniMuteBtn.style.borderBottomRightRadius = 4f;
            _miniMuteBtn.style.paddingLeft = _miniMuteBtn.style.paddingRight = 8f;
            _miniMuteBtn.style.paddingTop  = _miniMuteBtn.style.paddingBottom = 2f;
            _miniMuteBtn.style.height = 26f;
            _miniMuteBtn.style.marginRight = 6f;
            // Keep in sync with global mute state — mutate on toggle
            Action<float> _;  // unused setter
            AddCustomSlider(_miniBar, 80f, _isMuted ? 0f : _globalVolume,
                onChange: v =>
                {
                    _globalVolume = v;
                    if (_isMuted) { _isMuted = false; UpdateMuteButton(); }
                    _volumeSliderSetter?.Invoke(v);
                    ApplyAllVolumes();
                    SaveSettings();
                },
                setter: out _);
            _miniBar.Insert(0, _miniMuteBtn); // mute goes before the slider

            // Maximize button \u2014 text label so it renders regardless of font glyph coverage
            var maxBtn = new Button(ToggleMinimize);
            maxBtn.text    = "Max";
            maxBtn.tooltip = "Maximize";
            maxBtn.style.fontSize   = 12f;
            maxBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            maxBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            maxBtn.style.color      = Color.white;
            maxBtn.style.backgroundColor = new Color(0.25f, 0.25f, 0.3f);
            maxBtn.style.borderTopWidth = maxBtn.style.borderBottomWidth =
                maxBtn.style.borderLeftWidth = maxBtn.style.borderRightWidth = 0f;
            maxBtn.style.borderTopLeftRadius = maxBtn.style.borderTopRightRadius =
                maxBtn.style.borderBottomLeftRadius = maxBtn.style.borderBottomRightRadius = 4f;
            maxBtn.style.paddingLeft = maxBtn.style.paddingRight = 8f;
            maxBtn.style.paddingTop  = maxBtn.style.paddingBottom = 2f;
            maxBtn.style.height    = 26f;
            maxBtn.style.marginLeft = 6f;
            _miniBar.Add(maxBtn);

            // Close button
            var miniClose = new Button(Hide);
            miniClose.text    = "Close";
            miniClose.tooltip = "Close";
            miniClose.style.fontSize = 12f;
            miniClose.style.unityFontStyleAndWeight = FontStyle.Bold;
            miniClose.style.unityTextAlign = TextAnchor.MiddleCenter;
            miniClose.style.color    = Color.white;
            miniClose.style.backgroundColor = new Color(0.5f, 0.2f, 0.2f);
            miniClose.style.borderTopWidth = miniClose.style.borderBottomWidth =
                miniClose.style.borderLeftWidth = miniClose.style.borderRightWidth = 0f;
            miniClose.style.borderTopLeftRadius = miniClose.style.borderTopRightRadius =
                miniClose.style.borderBottomLeftRadius = miniClose.style.borderBottomRightRadius = 4f;
            miniClose.style.paddingLeft = miniClose.style.paddingRight = 8f;
            miniClose.style.paddingTop  = miniClose.style.paddingBottom = 2f;
            miniClose.style.height    = 26f;
            miniClose.style.marginLeft = 2f;
            _miniBar.Add(miniClose);

            root.Add(_miniBar);
        }

        private static void DestroyMiniBar()
        {
            if (_miniBar != null)
            {
                _miniBar.RemoveFromHierarchy();
                _miniBar = null;
            }
            _miniMuteBtn = null;
        }

        private static void ApplyWebViewZoom()
        {
            if (_webView == null) return;
            string zStr = _zoomLevel.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            _webView.EvaluateJS("document.documentElement.style.zoom='" + zStr + "';");
        }

        // ─── Build Settings Panel ──────────────────────────────────

        private static void BuildSettingsPanel(VisualElement body)
        {
            _settingsPanel = new VisualElement();
            _settingsPanel.name = "MOTDSettings";
            _settingsPanel.style.flexGrow = 1f;
            _settingsPanel.style.display  = DisplayStyle.None;
            _settingsPanel.style.backgroundColor = new Color(0.10f, 0.10f, 0.12f);
            body.Add(_settingsPanel);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            _settingsPanel.Add(scroll);

            var inner = scroll.contentContainer;
            inner.style.paddingLeft   = 48f;
            inner.style.paddingRight  = 48f;
            inner.style.paddingTop    = 32f;
            inner.style.paddingBottom = 48f;
            inner.style.maxWidth  = 820f;
            inner.style.alignSelf = Align.Center;

            // ── Page header ──────────────────────────────────────────
            var pageHeader = new Label("\u2699  Browser Settings");
            pageHeader.style.fontSize = 22f;
            pageHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            pageHeader.style.color = Color.white;
            pageHeader.style.marginBottom = 28f;
            inner.Add(pageHeader);

            // ════════════════════════════════════════════════════════
            // RENDERING
            // ════════════════════════════════════════════════════════
            AddSettingsSectionHeader(inner, "RENDERING");

            AddSettingsRow(inner, "Rendering Mode",
                "Switch between the built-in WebView2 engine (full browser) and the lightweight HTML parser.",
                container =>
                {
                    _webViewToggleBtn = CreateStyledButton(
                        _useWebView ? "HTML" : "WebView",
                        _useWebView ? new Color(0.3f, 0.55f, 0.3f) : new Color(0.5f, 0.3f, 0.6f),
                        ToggleWebViewMode);
                    _webViewToggleBtn.style.width  = 100f;
                    _webViewToggleBtn.style.height = 32f;
                    container.Add(_webViewToggleBtn);
                });

            // ════════════════════════════════════════════════════════
            // DISPLAY
            // ════════════════════════════════════════════════════════
            AddSettingsSectionHeader(inner, "DISPLAY", firstSection: false);

            // Page Zoom
            AddSettingsRow(inner, "Page Zoom",
                "Scale page content in WebView mode. 100% = default size.",
                container =>
                {
                    var zoomLabel = new Label(Mathf.RoundToInt(_zoomLevel * 100f) + "%");
                    zoomLabel.style.fontSize = 13f;
                    zoomLabel.style.color    = Color.white;
                    zoomLabel.style.width    = 42f;
                    zoomLabel.style.unityTextAlign = TextAnchor.MiddleRight;
                    zoomLabel.style.marginLeft = 10f;

                    float initZoom = (_zoomLevel - 0.5f) / 1.5f;
                    AddCustomSlider(container, 200f, initZoom,
                        onChange: v =>
                        {
                            _zoomLevel = 0.5f + v * 1.5f;
                            zoomLabel.text = Mathf.RoundToInt(_zoomLevel * 100f) + "%";
                            ApplyWebViewZoom();
                            SaveSettings();
                        },
                        setter: out _zoomSliderSetter);
                    container.Add(zoomLabel);
                });

            // Zoom min/max labels
            var zHint50  = new Label("50%");
            var zHint200 = new Label("200%");
            var zoomHints = new VisualElement();
            zoomHints.style.flexDirection = FlexDirection.Row;
            zoomHints.style.justifyContent = Justify.SpaceBetween;
            zoomHints.style.marginBottom = 6f;
            zHint50.style.fontSize = 11f; zHint50.style.color = new Color(0.45f, 0.45f, 0.5f);
            zHint200.style.fontSize = 11f; zHint200.style.color = new Color(0.45f, 0.45f, 0.5f);
            zoomHints.Add(zHint50); zoomHints.Add(zHint200);
            inner.Add(zoomHints);

            // Level Screens
            AddSettingsRow(inner, "Level Screens",
                "Show or hide the video screens displayed on the in-game rink.",
                container =>
                {
                    _screenToggleBtn = new Button(ToggleScreens);
                    _screenToggleBtn.text    = _screensDisabled ? "Screens Off" : "Screens On";
                    _screenToggleBtn.tooltip = _screensDisabled ? "Enable Screens" : "Disable Screens";
                    _screenToggleBtn.style.fontSize    = 13f;
                    _screenToggleBtn.style.color       = Color.white;
                    _screenToggleBtn.style.paddingLeft  = 16f;
                    _screenToggleBtn.style.paddingRight = 16f;
                    _screenToggleBtn.style.paddingTop    = 7f;
                    _screenToggleBtn.style.paddingBottom = 7f;
                    _screenToggleBtn.style.backgroundColor = _screensDisabled
                        ? new Color(0.5f, 0.15f, 0.15f) : new Color(0.15f, 0.42f, 0.22f);
                    _screenToggleBtn.style.borderTopLeftRadius    = 4f;
                    _screenToggleBtn.style.borderTopRightRadius   = 4f;
                    _screenToggleBtn.style.borderBottomLeftRadius  = 4f;
                    _screenToggleBtn.style.borderBottomRightRadius = 4f;
                    _screenToggleBtn.style.borderTopWidth    = 0f;
                    _screenToggleBtn.style.borderBottomWidth = 0f;
                    _screenToggleBtn.style.borderLeftWidth   = 0f;
                    _screenToggleBtn.style.borderRightWidth  = 0f;
                    _screenToggleBtn.RegisterCallback<MouseEnterEvent>(e =>
                        _screenToggleBtn.style.backgroundColor = _screensDisabled
                            ? new Color(0.65f, 0.22f, 0.22f) : new Color(0.22f, 0.55f, 0.3f));
                    _screenToggleBtn.RegisterCallback<MouseLeaveEvent>(e =>
                        _screenToggleBtn.style.backgroundColor = _screensDisabled
                            ? new Color(0.5f, 0.15f, 0.15f) : new Color(0.15f, 0.42f, 0.22f));
                    container.Add(_screenToggleBtn);
                });

            // ════════════════════════════════════════════════════════
            // PLAYBACK
            // ════════════════════════════════════════════════════════
            AddSettingsSectionHeader(inner, "PLAYBACK", firstSection: false);

            // Master Volume
            AddSettingsRow(inner, "Master Volume",
                "Controls media volume for in-browser video and level screens.",
                container =>
                {
                    var volLabel = new Label(Mathf.RoundToInt(_globalVolume * 100f) + "%");
                    volLabel.style.fontSize = 13f;
                    volLabel.style.color    = Color.white;
                    volLabel.style.width    = 42f;
                    volLabel.style.unityTextAlign = TextAnchor.MiddleRight;
                    volLabel.style.marginLeft = 10f;

                    Action<float> _;
                    AddCustomSlider(container, 200f, _globalVolume,
                        onChange: v =>
                        {
                            _globalVolume = v;
                            volLabel.text = Mathf.RoundToInt(v * 100f) + "%";
                            if (_isMuted) { _isMuted = false; UpdateMuteButton(); }
                            _volumeSliderSetter?.Invoke(v);
                            ApplyAllVolumes();
                            SaveSettings();
                        },
                        setter: out _);
                    container.Add(volLabel);
                });

            // Mute
            AddSettingsRow(inner, "Audio Mute",
                "Mute or unmute all media playback including level screens.",
                container =>
                {
                    Color muteAccent = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.18f, 0.25f, 0.35f);
                    _muteSettingsBtn = CreateStyledButton(
                        _isMuted ? "Muted" : "Unmuted",
                        muteAccent, ToggleMute);
                    _muteSettingsBtn.style.color = _isMuted ? new Color(1f, 0.4f, 0.4f) : new Color(0.75f, 0.75f, 0.8f);
                    container.Add(_muteSettingsBtn);
                });
        }

        private static void AddSettingsSectionHeader(VisualElement parent, string title, bool firstSection = true)
        {
            if (!firstSection)
            {
                var sep = new VisualElement();
                sep.style.height = 1f;
                sep.style.backgroundColor = new Color(0.22f, 0.22f, 0.28f);
                sep.style.marginTop    = 24f;
                sep.style.marginBottom = 20f;
                parent.Add(sep);
            }
            var label = new Label(title);
            label.style.fontSize = 11f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.5f, 0.5f, 0.6f);
            label.style.marginBottom = 16f;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            parent.Add(label);
        }

        private static void AddSettingsRow(VisualElement parent, string label, string description,
            Action<VisualElement> buildControl)
        {
            var row = new VisualElement();
            row.style.flexDirection  = FlexDirection.Row;
            row.style.alignItems     = Align.Center;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginBottom   = 22f;
            parent.Add(row);

            // Left column: name + description
            var leftCol = new VisualElement();
            leftCol.style.flexGrow   = 1f;
            leftCol.style.flexShrink = 1f;
            leftCol.style.marginRight = 28f;
            row.Add(leftCol);

            var nameLabel = new Label(label);
            nameLabel.style.fontSize = 15f;
            nameLabel.style.color    = new Color(0.92f, 0.92f, 0.95f);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.marginBottom = 2f;
            leftCol.Add(nameLabel);

            if (!string.IsNullOrEmpty(description))
            {
                var descLabel = new Label(description);
                descLabel.style.fontSize   = 12f;
                descLabel.style.color      = new Color(0.55f, 0.55f, 0.62f);
                descLabel.style.whiteSpace = WhiteSpace.Normal;
                leftCol.Add(descLabel);
            }

            // Right column: control
            var ctrl = new VisualElement();
            ctrl.style.flexShrink   = 0f;
            ctrl.style.flexDirection = FlexDirection.Row;
            ctrl.style.alignItems   = Align.Center;
            row.Add(ctrl);

            buildControl(ctrl);
        }

        // ─── Site Confirmation Dialog ───────────────────────────────

        private static string GetDomain(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host.ToLowerInvariant();
            }
            catch { return url; }
        }

        // Settings + trusted-sites are persisted through ClientConfig
        // (single ClientMOTD.json). These wrappers hydrate the UI's
        // in-memory cache fields from disk and push changes back.

        private static void LoadTrustedDomains()
        {
            if (_trustedDomains != null) return;
            _trustedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (Plugin.IsDedicatedServer()) return; // client-only file

            ClientConfig.EnsureLoaded();
            foreach (string d in ClientConfig.TrustedSites)
                _trustedDomains.Add(d);
        }

        private static void SaveTrustedDomain(string domain)
        {
            if (string.IsNullOrEmpty(domain)) return;
            _trustedDomains?.Add(domain);
            ClientConfig.AddTrusted(domain);
            Plugin.Log("Saved trusted domain: " + domain);
        }

        private static void LoadSettings() => EnsureSettingsLoaded();

        /// <summary>
        /// Hydrate the volume / mute / screens-disabled / zoom static fields from
        /// ClientMOTD.json. Idempotent. Called both lazily from Show() and eagerly
        /// from <see cref="Plugin.Setup"/> on non-dedicated clients — without the
        /// eager call, the world screens spawned on join (in response to the MOTD
        /// or queue-state messages) would ignore the saved preferences until the
        /// user first opens the overlay.
        /// </summary>
        public static void EnsureSettingsLoaded()
        {
            if (_settingsLoaded) return;
            if (Plugin.IsDedicatedServer()) return; // client-only config

            ClientConfig.EnsureLoaded();
            _globalVolume    = ClientConfig.Volume;
            _isMuted         = ClientConfig.Muted;
            _screensDisabled = ClientConfig.ScreensDisabled;
            _zoomLevel       = ClientConfig.Zoom;
            _settingsLoaded  = true;
            Plugin.Log("MOTD settings loaded from ClientMOTD.json.");
        }

        /// <summary>True if the user has disabled the in-world screens locally.</summary>
        public static bool ScreensDisabled
        {
            get { EnsureSettingsLoaded(); return _screensDisabled; }
        }

        private static void SaveSettings()
        {
            if (Plugin.IsDedicatedServer()) return; // client-only config
            // Single disk write for all four fields (task 4: batch saves).
            ClientConfig.SaveSettings(_globalVolume, _isMuted, _screensDisabled, _zoomLevel);
        }

        private static void ShowConfirmDialog(string url, string domain)
        {
            var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
            var root = uiManager?.RootVisualElement;
            if (root == null) return;

            _confirmOverlay?.RemoveFromHierarchy();

            // Full-screen dark backdrop
            _confirmOverlay = new VisualElement();
            _confirmOverlay.style.position = Position.Absolute;
            _confirmOverlay.style.left = 0f;
            _confirmOverlay.style.top = 0f;
            _confirmOverlay.style.right = 0f;
            _confirmOverlay.style.bottom = 0f;
            _confirmOverlay.style.alignItems = Align.Center;
            _confirmOverlay.style.justifyContent = Justify.Center;
            _confirmOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.85f);

            // Dialog card
            var dialog = new VisualElement();
            dialog.style.width = 520f;
            dialog.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 0.98f);
            dialog.style.borderTopLeftRadius = 10f;
            dialog.style.borderTopRightRadius = 10f;
            dialog.style.borderBottomLeftRadius = 10f;
            dialog.style.borderBottomRightRadius = 10f;
            dialog.style.paddingLeft = 28f;
            dialog.style.paddingRight = 28f;
            dialog.style.paddingTop = 24f;
            dialog.style.paddingBottom = 24f;
            dialog.style.borderTopWidth = 1f;
            dialog.style.borderBottomWidth = 1f;
            dialog.style.borderLeftWidth = 1f;
            dialog.style.borderRightWidth = 1f;
            dialog.style.borderTopColor = new Color(0.35f, 0.35f, 0.4f);
            dialog.style.borderBottomColor = new Color(0.35f, 0.35f, 0.4f);
            dialog.style.borderLeftColor = new Color(0.35f, 0.35f, 0.4f);
            dialog.style.borderRightColor = new Color(0.35f, 0.35f, 0.4f);
            _confirmOverlay.Add(dialog);

            // Shield icon + title
            var title = new Label("In Game WebView Confirmation");
            title.style.fontSize = 20f;
            title.style.color = new Color(1f, 1f, 1f);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = 16f;
            dialog.Add(title);

            // Warning message — note the destination differs by platform. The inline
            // WebView only works on Windows, so other platforms fall back to the
            // Steam overlay browser (handled in ShowConfirmed).
            string destination = MOTDWebView.IsSupportedPlatform()
                ? "in the MOTD browser"
                : "in the Steam overlay browser";
            var msg = new Label("The server wants to open a webpage " + destination + ":");
            msg.style.fontSize = 14f;
            msg.style.color = new Color(0.8f, 0.8f, 0.8f);
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginBottom = 10f;
            dialog.Add(msg);

            // URL display
            var urlLabel = new Label(url);
            urlLabel.style.fontSize = 13f;
            urlLabel.style.color = new Color(0.5f, 0.8f, 1f);
            urlLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            urlLabel.style.whiteSpace = WhiteSpace.Normal;
            urlLabel.style.overflow = Overflow.Hidden;
            urlLabel.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f);
            urlLabel.style.borderTopLeftRadius = 4f;
            urlLabel.style.borderTopRightRadius = 4f;
            urlLabel.style.borderBottomLeftRadius = 4f;
            urlLabel.style.borderBottomRightRadius = 4f;
            urlLabel.style.paddingLeft = 10f;
            urlLabel.style.paddingRight = 10f;
            urlLabel.style.paddingTop = 8f;
            urlLabel.style.paddingBottom = 8f;
            urlLabel.style.marginBottom = 12f;
            dialog.Add(urlLabel);

            // Rules-agreement notice. Either button (Open Website or Deny) closes the
            // MOTD and is treated as the player acknowledging the server rules — make
            // that explicit so it can't be missed.
            var rulesNotice = new Label("By clicking Open Website or Deny, you acknowledge that you have read and agree to the server rules.");
            rulesNotice.style.fontSize = 12f;
            rulesNotice.style.color = new Color(1f, 0.85f, 0.4f);
            rulesNotice.style.unityFontStyleAndWeight = FontStyle.Bold;
            rulesNotice.style.whiteSpace = WhiteSpace.Normal;
            rulesNotice.style.unityTextAlign = TextAnchor.MiddleCenter;
            rulesNotice.style.backgroundColor = new Color(0.18f, 0.14f, 0.06f);
            rulesNotice.style.borderTopLeftRadius = 4f;
            rulesNotice.style.borderTopRightRadius = 4f;
            rulesNotice.style.borderBottomLeftRadius = 4f;
            rulesNotice.style.borderBottomRightRadius = 4f;
            rulesNotice.style.paddingLeft = 10f;
            rulesNotice.style.paddingRight = 10f;
            rulesNotice.style.paddingTop = 8f;
            rulesNotice.style.paddingBottom = 8f;
            rulesNotice.style.marginBottom = 16f;
            dialog.Add(rulesNotice);

            // "Don't ask again" toggle. We draw the checkbox ourselves
            // because Unity's built-in Toggle renders an invisible box on
            // this dark dialog (its default background image doesn't
            // contrast with the theme — only the checkmark shows through).
            bool dontAskAgain = false;
            var toggleRow = new VisualElement();
            toggleRow.style.flexDirection = FlexDirection.Row;
            toggleRow.style.alignItems = Align.Center;
            toggleRow.style.marginBottom = 20f;

            var checkBox = new VisualElement();
            checkBox.style.width = 18f;
            checkBox.style.height = 18f;
            checkBox.style.marginRight = 10f;
            checkBox.style.backgroundColor = new Color(0.18f, 0.18f, 0.22f);
            checkBox.style.borderTopWidth = 1f;
            checkBox.style.borderBottomWidth = 1f;
            checkBox.style.borderLeftWidth = 1f;
            checkBox.style.borderRightWidth = 1f;
            checkBox.style.borderTopColor = new Color(0.6f, 0.6f, 0.65f);
            checkBox.style.borderBottomColor = new Color(0.6f, 0.6f, 0.65f);
            checkBox.style.borderLeftColor = new Color(0.6f, 0.6f, 0.65f);
            checkBox.style.borderRightColor = new Color(0.6f, 0.6f, 0.65f);
            checkBox.style.borderTopLeftRadius = 3f;
            checkBox.style.borderTopRightRadius = 3f;
            checkBox.style.borderBottomLeftRadius = 3f;
            checkBox.style.borderBottomRightRadius = 3f;
            checkBox.style.alignItems = Align.Center;
            checkBox.style.justifyContent = Justify.Center;

            var checkMark = new Label("✓");
            checkMark.style.color = new Color(0.4f, 0.9f, 0.5f);
            checkMark.style.fontSize = 14f;
            checkMark.style.unityFontStyleAndWeight = FontStyle.Bold;
            checkMark.style.unityTextAlign = TextAnchor.MiddleCenter;
            checkMark.style.display = DisplayStyle.None;
            checkMark.pickingMode = PickingMode.Ignore;
            checkBox.Add(checkMark);

            Action toggleCheck = () =>
            {
                dontAskAgain = !dontAskAgain;
                checkMark.style.display = dontAskAgain ? DisplayStyle.Flex : DisplayStyle.None;
                checkBox.style.borderTopColor    = dontAskAgain ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.6f, 0.6f, 0.65f);
                checkBox.style.borderBottomColor = dontAskAgain ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.6f, 0.6f, 0.65f);
                checkBox.style.borderLeftColor   = dontAskAgain ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.6f, 0.6f, 0.65f);
                checkBox.style.borderRightColor  = dontAskAgain ? new Color(0.4f, 0.9f, 0.5f) : new Color(0.6f, 0.6f, 0.65f);
            };
            checkBox.RegisterCallback<ClickEvent>(_ => toggleCheck());
            toggleRow.Add(checkBox);

            var toggleLabel = new Label("Don't ask again for " + domain);
            toggleLabel.style.fontSize = 13f;
            toggleLabel.style.color = new Color(0.7f, 0.7f, 0.7f);
            // Clicking the label also toggles — matches how native checkboxes behave.
            toggleLabel.RegisterCallback<ClickEvent>(_ => toggleCheck());
            toggleRow.Add(toggleLabel);
            dialog.Add(toggleRow);

            // Button row
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;

            // Allow button
            var allowBtn = CreateStyledButton("Open Website", new Color(0.2f, 0.5f, 0.3f), () =>
            {
                if (dontAskAgain)
                    SaveTrustedDomain(domain);
                _confirmOverlay?.RemoveFromHierarchy();
                _confirmOverlay = null;
                ShowConfirmed(url);
            });
            allowBtn.style.paddingLeft = 24f;
            allowBtn.style.paddingRight = 24f;
            allowBtn.style.paddingTop = 10f;
            allowBtn.style.paddingBottom = 10f;
            allowBtn.style.height = 38f;
            allowBtn.style.fontSize = 15f;
            allowBtn.style.marginRight = 12f;
            btnRow.Add(allowBtn);

            // Deny button
            var denyBtn = CreateStyledButton("Deny", new Color(0.5f, 0.2f, 0.2f), () =>
            {
                Plugin.Log("User denied MOTD for: " + url);
                _confirmOverlay?.RemoveFromHierarchy();
                _confirmOverlay = null;
                if (!_isVisible)
                {
                    DestroyEscPoller();
                    UnblockGameplayInput();
                }
            });
            denyBtn.style.paddingLeft = 24f;
            denyBtn.style.paddingRight = 24f;
            denyBtn.style.paddingTop = 10f;
            denyBtn.style.paddingBottom = 10f;
            denyBtn.style.height = 38f;
            denyBtn.style.fontSize = 15f;
            btnRow.Add(denyBtn);

            dialog.Add(btnRow);

            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            root.Add(_confirmOverlay);
            EnsureEscPoller();
            // Block gameplay input + dismiss any open chat right away — the dialog
            // is modal, so we don't want stick input or chat keys leaking through
            // while the player is deciding whether to open the page.
            BlockGameplayInput();
            DismissOpenChatInput();
        }

        private static void UpdateWebViewToggleButton()
        {
            if (_webViewToggleBtn == null) return;
            if (_useWebView)
            {
                _webViewToggleBtn.text = "HTML";
                _webViewToggleBtn.style.backgroundColor = new Color(0.3f, 0.55f, 0.3f);
            }
            else
            {
                _webViewToggleBtn.text = "WebView";
                _webViewToggleBtn.style.backgroundColor = new Color(0.5f, 0.3f, 0.6f);
            }
        }

        private static void CleanupVideoHosts()
        {
            foreach (var host in _videoHosts)
                if (host != null) host.Cleanup();
            _videoHosts.Clear();

            foreach (var c in _gifCoroutines)
                MOTDWebContent.StopManagedCoroutine(c);
            _gifCoroutines.Clear();

            // Decoded textures (GIF frames + downloaded images) need explicit Destroy —
            // GC won't free GPU memory on its own.
            foreach (var tex in _managedTextures)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            _managedTextures.Clear();
        }

        private static void RemoveStatusLabel()
        {
            if (_statusLabel != null)
            {
                _statusLabel.RemoveFromHierarchy();
                _statusLabel = null;
            }
        }

        private static void ShowSpaFallback()
        {
            if (_contentArea == null) return;
            AddHeading(_contentArea, "This page uses JavaScript to render content", 16f, new Color(1f, 0.85f, 0.4f));
            AddParagraph(_contentArea, "The page can't be displayed inline because it requires a full browser engine. " +
                "It has been opened in the Steam overlay browser for you.");
            AddSeparator(_contentArea);
            AddParagraph(_contentArea, "If the Steam browser didn't open, click the buttons below.");
        }

        private static void ShowErrorState(string error)
        {
            if (_contentArea == null) return;
            AddHeading(_contentArea, "Could not load page", 16f, new Color(1f, 0.5f, 0.4f));
            AddParagraph(_contentArea, error);
            AddSeparator(_contentArea);
            AddParagraph(_contentArea, "Try using the browser buttons below, or enter a different URL.");
        }

        private static void TryOpenSteamBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                // ActivateGameOverlayToWebPage silently no-ops when the user has
                // the Steam overlay disabled in client settings — Steamworks
                // returns success without doing anything. SteamUtils.IsOverlayEnabled
                // tells us whether the overlay is actually usable; if not, route
                // through the OS default browser so the user still sees the page
                // (matches the "Open in System Browser" button's behavior).
                bool overlayOk = SteamManager.IsInitialized && SteamUtils.IsOverlayEnabled();
                if (overlayOk)
                {
                    SteamFriends.ActivateGameOverlayToWebPage(url);
                    return;
                }
                Plugin.Log("Steam overlay disabled — opening in system browser instead.");
                Application.OpenURL(url);
            }
            catch (Exception ex)
            {
                Plugin.LogError("Steam overlay failed: " + ex.Message);
                // Last-ditch: try the system browser so the user isn't stranded.
                try { Application.OpenURL(url); } catch { }
            }
        }

        // ─── Content Rendering ──────────────────────────────────────

        private static void RenderContent(List<ContentElement> elements)
        {
            if (_contentArea == null) return;

            VisualElement cardContainer = null;

            foreach (var el in elements)
            {
                // Resolve target parent (inside card or main content)
                var target = cardContainer ?? _contentArea;

                switch (el.Type)
                {
                    case ContentElement.ElementType.CardOpen:
                        cardContainer = CreateCard(el.BgColor);
                        _contentArea.Add(cardContainer);
                        break;

                    case ContentElement.ElementType.CardClose:
                        cardContainer = null;
                        break;

                    case ContentElement.ElementType.Heading1:
                    {
                        var c = el.FgColor ?? Color.white;
                        AddHeading(target, el.Text, 24f, c);
                        break;
                    }
                    case ContentElement.ElementType.Heading2:
                    {
                        var c = el.FgColor ?? new Color(0.3f, 0.75f, 1f);
                        AddHeading(target, el.Text, 20f, c);
                        break;
                    }
                    case ContentElement.ElementType.Heading3:
                    {
                        var c = el.FgColor ?? new Color(0.6f, 0.85f, 1f);
                        AddHeading(target, el.Text, 16f, c);
                        break;
                    }
                    case ContentElement.ElementType.Paragraph:
                    {
                        var c = el.FgColor ?? new Color(0.88f, 0.88f, 0.88f);
                        AddRichParagraph(target, el.Text, c);
                        break;
                    }
                    case ContentElement.ElementType.ListItem:
                        AddListItem(target, el.Text);
                        break;

                    case ContentElement.ElementType.NumberedItem:
                        AddNumberedItem(target, el.ListNumber, el.Text);
                        break;

                    case ContentElement.ElementType.Separator:
                        AddSeparator(target);
                        break;

                    case ContentElement.ElementType.Blockquote:
                        AddBlockquote(target, el.Text);
                        break;

                    case ContentElement.ElementType.Code:
                        AddCodeBlock(target, el.Text);
                        break;

                    case ContentElement.ElementType.Image:
                        AddImage(target, el.Url, el.Text);
                        break;

                    case ContentElement.ElementType.Link:
                        AddClickableLink(target, el.Text, el.Url);
                        break;

                    case ContentElement.ElementType.Video:
                        AddVideoElement(target, el.Url, el.Text, el.IsEmbed);
                        break;

                    case ContentElement.ElementType.SearchInput:
                        AddSearchBar(target, el.Text, el.Url, el.ExtraData);
                        break;
                }
            }
        }

        // ─── UI Construction ────────────────────────────────────────

        private static void Build()
        {
            _overlay = new VisualElement();
            _overlay.name = "MOTDOverlay";
            _overlay.style.position = Position.Absolute;
            _overlay.style.left   = 0f;
            _overlay.style.top    = 0f;
            _overlay.style.right  = 0f;
            _overlay.style.bottom = 0f;
            _overlay.style.alignItems     = Align.Center;
            _overlay.style.justifyContent = Justify.Center;
            _overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.75f);

            var card = new VisualElement();
            card.style.width  = new Length(88f, LengthUnit.Percent);
            card.style.maxWidth  = 1600f;
            card.style.height = new Length(88f, LengthUnit.Percent);
            card.style.backgroundColor       = new Color(0.10f, 0.10f, 0.12f, 0.98f);
            card.style.borderTopLeftRadius    = 10f;
            card.style.borderTopRightRadius   = 10f;
            card.style.borderBottomLeftRadius  = 10f;
            card.style.borderBottomRightRadius = 10f;
            card.style.flexDirection = FlexDirection.Column;
            card.style.overflow = Overflow.Hidden;
            card.style.borderTopWidth    = 1f;
            card.style.borderBottomWidth = 1f;
            card.style.borderLeftWidth   = 1f;
            card.style.borderRightWidth  = 1f;
            card.style.borderTopColor    = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderBottomColor = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderLeftColor   = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderRightColor  = new Color(0.25f, 0.25f, 0.3f);
            _card = card;
            _overlay.Add(card);

            BuildTitleBar(card);
            BuildContentArea(card);
            BuildFooter(card);
        }

        private static void BuildTitleBar(VisualElement card)
        {
            var titleBar = new VisualElement();
            titleBar.style.flexDirection   = FlexDirection.Row;
            titleBar.style.alignItems      = Align.Center;
            titleBar.style.paddingLeft     = 10f;
            titleBar.style.paddingRight    = 10f;
            titleBar.style.paddingTop      = 8f;
            titleBar.style.paddingBottom   = 8f;
            titleBar.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f);
            titleBar.style.flexShrink = 0f;
            card.Add(titleBar);

            // Back button
            _backBtn = CreateStyledButton("Back", new Color(0.25f, 0.25f, 0.3f), GoBack);
            _backBtn.style.paddingLeft  = 10f;
            _backBtn.style.paddingRight = 10f;
            _backBtn.style.paddingTop = 2f;
            _backBtn.style.paddingBottom = 2f;
            _backBtn.style.height = 28f;
            _backBtn.style.flexShrink = 0f;
            _backBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _backBtn.style.marginRight = 2f;
            _backBtn.SetEnabled(false);
            _backBtn.style.opacity = 0.35f;
            titleBar.Add(_backBtn);

            // Forward button
            _fwdBtn = CreateStyledButton("Fwd", new Color(0.25f, 0.25f, 0.3f), GoForward);
            _fwdBtn.style.paddingLeft  = 10f;
            _fwdBtn.style.paddingRight = 10f;
            _fwdBtn.style.paddingTop = 2f;
            _fwdBtn.style.paddingBottom = 2f;
            _fwdBtn.style.height = 28f;
            _fwdBtn.style.flexShrink = 0f;
            _fwdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _fwdBtn.style.marginRight = 6f;
            _fwdBtn.SetEnabled(false);
            _fwdBtn.style.opacity = 0.35f;
            titleBar.Add(_fwdBtn);

            // All title-bar buttons use ASCII labels: Puck's UI font does not include
            // most BMP symbols (arrows, gear, house, music notes, etc.) and emoji never
            // render. Labels keep the buttons visible regardless of font coverage.
            var refreshBtn = CreateStyledButton("Reload", new Color(0.25f, 0.25f, 0.3f), () =>
            {
                if (_useWebView && _webView != null)
                    _webView.Reload();
                else if (!string.IsNullOrEmpty(_url))
                    NavigateTo(_url, addToHistory: false);
            });
            refreshBtn.style.paddingLeft  = 10f;
            refreshBtn.style.paddingRight = 10f;
            refreshBtn.style.height = 28f;
            refreshBtn.style.flexShrink = 0f;
            refreshBtn.style.marginRight = 2f;
            titleBar.Add(refreshBtn);

            var homeBtn = CreateStyledButton("Home", new Color(0.25f, 0.25f, 0.3f), () =>
            {
                if (!string.IsNullOrEmpty(_homeUrl))
                    NavigateTo(_homeUrl);
            });
            homeBtn.style.paddingLeft  = 10f;
            homeBtn.style.paddingRight = 10f;
            homeBtn.style.height = 28f;
            homeBtn.style.flexShrink = 0f;
            homeBtn.style.marginRight = 6f;
            titleBar.Add(homeBtn);

            // Editable URL bar — flex-grow so it fills the remaining space between
            // the nav buttons (left) and audio/settings/window controls (right).
            // All sibling buttons set flexShrink=0 so they keep their natural width
            // and the URL field absorbs leftover space without ever pushing them off.
            _urlField = new TextField();
            _urlField.value = _url ?? "";
            _lastUrlFieldValue = _urlField.value;
            _urlField.style.flexGrow = 1f;
            _urlField.style.flexShrink = 1f;
            _urlField.style.minWidth = 120f;
            _urlField.style.height = 28f;
            _urlField.style.marginRight = 6f;

            var textInput = _urlField.Q<VisualElement>("unity-text-input");
            if (textInput != null)
            {
                textInput.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f);
                textInput.style.color = new Color(0.9f, 0.9f, 0.9f);
                textInput.style.fontSize = 14f;
                textInput.style.unityTextAlign = TextAnchor.MiddleLeft;
                textInput.style.borderTopLeftRadius    = 4f;
                textInput.style.borderTopRightRadius   = 4f;
                textInput.style.borderBottomLeftRadius  = 4f;
                textInput.style.borderBottomRightRadius = 4f;
                textInput.style.borderTopWidth    = 1f;
                textInput.style.borderBottomWidth = 1f;
                textInput.style.borderLeftWidth   = 1f;
                textInput.style.borderRightWidth  = 1f;
                textInput.style.borderTopColor    = new Color(0.25f, 0.25f, 0.3f);
                textInput.style.borderBottomColor = new Color(0.25f, 0.25f, 0.3f);
                textInput.style.borderLeftColor   = new Color(0.25f, 0.25f, 0.3f);
                textInput.style.borderRightColor  = new Color(0.25f, 0.25f, 0.3f);
                textInput.style.paddingLeft   = 10f;
                textInput.style.paddingRight  = 10f;
                textInput.style.paddingTop = 4f;
                textInput.style.paddingBottom = 4f;
            }

            _urlField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    NavigateTo(_urlField.value);
                    e.StopPropagation();
                }
            });
            titleBar.Add(_urlField);

            // Go button
            var goBtn = CreateStyledButton("Go", new Color(0.25f, 0.55f, 0.3f), () =>
            {
                NavigateTo(_urlField.value);
            });
            goBtn.style.paddingLeft  = 12f;
            goBtn.style.paddingRight = 12f;
            goBtn.style.height = 28f;
            goBtn.style.flexShrink = 0f;
            goBtn.style.marginRight = 6f;
            titleBar.Add(goBtn);

            // ── Volume / Mute controls (fixed-size – does not shrink) ──
            var audioGroup = new VisualElement();
            audioGroup.style.flexDirection = FlexDirection.Row;
            audioGroup.style.alignItems = Align.Center;
            audioGroup.style.flexShrink = 0f;
            audioGroup.style.marginLeft = 6f;
            audioGroup.style.marginRight = 6f;
            titleBar.Add(audioGroup);

            // mute toggle — text label so it renders regardless of font glyph coverage
            _muteBtn = new Button(ToggleMute);
            _muteBtn.text = _isMuted ? "Muted" : "Mute";
            _muteBtn.style.fontSize = 13f;
            _muteBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _muteBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _muteBtn.style.backgroundColor = _isMuted ? new Color(0.4f, 0.12f, 0.12f) : new Color(0.25f, 0.25f, 0.3f);
            _muteBtn.style.color = _isMuted ? new Color(1f, 0.7f, 0.7f) : Color.white;
            _muteBtn.style.paddingLeft = 10f;
            _muteBtn.style.paddingRight = 10f;
            _muteBtn.style.paddingTop = 2f;
            _muteBtn.style.paddingBottom = 2f;
            _muteBtn.style.height = 28f;
            _muteBtn.style.flexShrink = 0f;
            _muteBtn.style.marginRight = 6f;
            _muteBtn.style.borderTopWidth = 0f;
            _muteBtn.style.borderBottomWidth = 0f;
            _muteBtn.style.borderLeftWidth = 0f;
            _muteBtn.style.borderRightWidth = 0f;
            _muteBtn.style.borderTopLeftRadius = 4f;
            _muteBtn.style.borderTopRightRadius = 4f;
            _muteBtn.style.borderBottomLeftRadius = 4f;
            _muteBtn.style.borderBottomRightRadius = 4f;
            _muteBtn.tooltip = _isMuted ? "Unmute" : "Mute";
            audioGroup.Add(_muteBtn);

            Action<float> volSetter;
            AddCustomSlider(audioGroup, 80f, _isMuted ? 0f : _globalVolume,
                onChange: v =>
                {
                    _globalVolume = v;
                    if (_isMuted) { _isMuted = false; UpdateMuteButton(); }
                    ApplyAllVolumes();
                    SaveSettings();
                },
                setter: out volSetter);
            _volumeSliderSetter = volSetter;

            // Settings / Minimize / Close \u2014 text labels with a subtle background so they
            // stay visible regardless of glyph coverage (Puck's UI font doesn't render
            // most non-ASCII symbols, so the original gear/minus/cross would be invisible).
            _settingsBtn = new Button(ToggleSettings);
            _settingsBtn.text = "Settings";
            _settingsBtn.tooltip = "Settings";
            _settingsBtn.style.fontSize = 13f;
            _settingsBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _settingsBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _settingsBtn.style.backgroundColor = new Color(0.25f, 0.25f, 0.3f);
            _settingsBtn.style.color = Color.white;
            _settingsBtn.style.paddingLeft = 10f;
            _settingsBtn.style.paddingRight = 10f;
            _settingsBtn.style.paddingTop = 2f;
            _settingsBtn.style.paddingBottom = 2f;
            _settingsBtn.style.height = 28f;
            _settingsBtn.style.marginLeft = 4f;
            _settingsBtn.style.flexShrink = 0f;
            _settingsBtn.style.borderTopLeftRadius = 4f;
            _settingsBtn.style.borderTopRightRadius = 4f;
            _settingsBtn.style.borderBottomLeftRadius = 4f;
            _settingsBtn.style.borderBottomRightRadius = 4f;
            _settingsBtn.style.borderTopWidth = 0f;
            _settingsBtn.style.borderBottomWidth = 0f;
            _settingsBtn.style.borderLeftWidth = 0f;
            _settingsBtn.style.borderRightWidth = 0f;
            _settingsBtn.RegisterCallback<MouseEnterEvent>(e => _settingsBtn.style.backgroundColor = new Color(0.35f, 0.35f, 0.4f));
            _settingsBtn.RegisterCallback<MouseLeaveEvent>(e => _settingsBtn.style.backgroundColor = _settingsOpen ? new Color(0.4f, 0.4f, 0.45f) : new Color(0.25f, 0.25f, 0.3f));
            titleBar.Add(_settingsBtn);

            _minimizeBtn = new Button(ToggleMinimize);
            _minimizeBtn.text = "Min";
            _minimizeBtn.tooltip = "Minimize";
            _minimizeBtn.style.fontSize = 13f;
            _minimizeBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            _minimizeBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _minimizeBtn.style.backgroundColor = new Color(0.25f, 0.25f, 0.3f);
            _minimizeBtn.style.color = Color.white;
            _minimizeBtn.style.paddingLeft = 10f;
            _minimizeBtn.style.paddingRight = 10f;
            _minimizeBtn.style.paddingTop = 2f;
            _minimizeBtn.style.paddingBottom = 2f;
            _minimizeBtn.style.height = 28f;
            _minimizeBtn.style.marginLeft = 2f;
            _minimizeBtn.style.flexShrink = 0f;
            _minimizeBtn.style.borderTopLeftRadius = 4f;
            _minimizeBtn.style.borderTopRightRadius = 4f;
            _minimizeBtn.style.borderBottomLeftRadius = 4f;
            _minimizeBtn.style.borderBottomRightRadius = 4f;
            _minimizeBtn.style.borderTopWidth = 0f;
            _minimizeBtn.style.borderBottomWidth = 0f;
            _minimizeBtn.style.borderLeftWidth = 0f;
            _minimizeBtn.style.borderRightWidth = 0f;
            _minimizeBtn.RegisterCallback<MouseEnterEvent>(e => _minimizeBtn.style.backgroundColor = new Color(0.35f, 0.35f, 0.4f));
            _minimizeBtn.RegisterCallback<MouseLeaveEvent>(e => _minimizeBtn.style.backgroundColor = new Color(0.25f, 0.25f, 0.3f));
            titleBar.Add(_minimizeBtn);

            var closeBtn = new Button(Hide);
            closeBtn.text = "Close";
            closeBtn.style.fontSize = 13f;
            closeBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            closeBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            closeBtn.style.color = Color.white;
            closeBtn.style.backgroundColor = new Color(0.5f, 0.2f, 0.2f);
            closeBtn.style.borderTopLeftRadius = 4f;
            closeBtn.style.borderTopRightRadius = 4f;
            closeBtn.style.borderBottomLeftRadius = 4f;
            closeBtn.style.borderBottomRightRadius = 4f;
            closeBtn.style.borderTopWidth    = 0f;
            closeBtn.style.borderBottomWidth = 0f;
            closeBtn.style.borderLeftWidth   = 0f;
            closeBtn.style.borderRightWidth  = 0f;
            closeBtn.style.paddingLeft   = 12f;
            closeBtn.style.paddingRight  = 12f;
            closeBtn.style.paddingTop    = 2f;
            closeBtn.style.paddingBottom = 2f;
            closeBtn.style.height = 28f;
            closeBtn.style.marginLeft = 2f;
            closeBtn.style.flexShrink = 0f;
            closeBtn.RegisterCallback<MouseEnterEvent>(e => closeBtn.style.backgroundColor = new Color(0.7f, 0.3f, 0.3f));
            closeBtn.RegisterCallback<MouseLeaveEvent>(e => closeBtn.style.backgroundColor = new Color(0.5f, 0.2f, 0.2f));
            titleBar.Add(closeBtn);
        }

        private static void BuildContentArea(VisualElement card)
        {
            // Horizontal container: queue panel on left, browser on right
            var body = new VisualElement();
            body.style.flexDirection = FlexDirection.Row;
            body.style.flexGrow = 1f;
            body.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            _cardBody = body;
            card.Add(body);

            // Queue panel on the left
            BuildQueuePanel(body);

            // Browser scroll view on the right
            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.style.flexGrow = 1f;
            _scrollView.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            body.Add(_scrollView);

            // Settings panel (replaces scroll/webview when ⚙ is active)
            BuildSettingsPanel(body);

            _contentArea = _scrollView.contentContainer;
            _contentArea.style.paddingLeft   = 28f;
            _contentArea.style.paddingRight  = 28f;
            _contentArea.style.paddingTop    = 20f;
            _contentArea.style.paddingBottom = 24f;

            // Initial queue render
            RefreshQueuePanel();

            // Subscribe to queue updates (once per session)
            if (!_queueEventSubscribed)
            {
                Plugin.OnQueueChanged += RefreshQueuePanel;
                _queueEventSubscribed = true;
            }
        }

        // ─── Queue Panel ────────────────────────────────────────────

        private static void BuildQueuePanel(VisualElement parent)
        {
            // Skip the entire panel when the server has the queue disabled — there's
            // nothing actionable in it, and showing a dead tab is worse than a clean
            // overlay. RefreshQueuePanel / ShowQueueError already null-guard so the
            // queue-disabled feedback for AddToQueue still surfaces via LocalChat.
            if (!Plugin.IsQueueEnabled) return;

            // Outer wrapper: horizontal row containing a thin tab + (optional) expanded content.
            _queuePanel = new VisualElement();
            _queuePanel.style.flexDirection = FlexDirection.Row;
            _queuePanel.style.flexShrink = 0f;
            parent.Add(_queuePanel);

            // ── Thin clickable tab (always visible) ──
            _queueTab = new VisualElement();
            _queueTab.style.width = 28f;
            _queueTab.style.flexShrink = 0f;
            _queueTab.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f);
            _queueTab.style.borderRightWidth = 1f;
            _queueTab.style.borderRightColor = new Color(0.25f, 0.25f, 0.3f);
            _queueTab.style.alignItems = Align.Center;
            _queueTab.style.justifyContent = Justify.Center;
            _queueTab.style.paddingTop = 8f;
            _queueTab.style.paddingBottom = 8f;
            _queuePanel.Add(_queueTab);

            _queueTabLabel = new Label(_queueExpanded ? "◀\nQ\nU\nE\nU\nE" : "▶\nQ\nU\nE\nU\nE");
            _queueTabLabel.style.fontSize = 12f;
            _queueTabLabel.style.color = new Color(0.85f, 0.85f, 0.9f);
            _queueTabLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _queueTabLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _queueTabLabel.style.whiteSpace = WhiteSpace.Normal;
            _queueTab.Add(_queueTabLabel);

            _queueTab.RegisterCallback<MouseEnterEvent>(e =>
                _queueTab.style.backgroundColor = new Color(0.12f, 0.12f, 0.16f));
            _queueTab.RegisterCallback<MouseLeaveEvent>(e =>
                _queueTab.style.backgroundColor = new Color(0.05f, 0.05f, 0.07f));
            _queueTab.RegisterCallback<ClickEvent>(e => ToggleQueuePanel());

            // ── Expanded content (hidden when collapsed) ──
            _queueContent = new VisualElement();
            _queueContent.style.width = 300f;
            _queueContent.style.flexShrink = 0f;
            _queueContent.style.backgroundColor = new Color(0.08f, 0.08f, 0.10f);
            _queueContent.style.borderRightWidth = 1f;
            _queueContent.style.borderRightColor = new Color(0.25f, 0.25f, 0.3f);
            _queueContent.style.paddingLeft = 12f;
            _queueContent.style.paddingRight = 12f;
            _queueContent.style.paddingTop = 12f;
            _queueContent.style.paddingBottom = 12f;
            _queueContent.style.display = _queueExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _queuePanel.Add(_queueContent);

            var header = new Label("Screen Queue");
            header.style.fontSize = 16f;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.color = Color.white;
            header.style.marginBottom = 10f;
            _queueContent.Add(header);

            // Now playing box
            _queueNowPlayingBox = new VisualElement();
            _queueNowPlayingBox.style.backgroundColor = new Color(0.14f, 0.14f, 0.18f);
            _queueNowPlayingBox.style.borderTopLeftRadius = 6f;
            _queueNowPlayingBox.style.borderTopRightRadius = 6f;
            _queueNowPlayingBox.style.borderBottomLeftRadius = 6f;
            _queueNowPlayingBox.style.borderBottomRightRadius = 6f;
            _queueNowPlayingBox.style.paddingLeft = 10f;
            _queueNowPlayingBox.style.paddingRight = 10f;
            _queueNowPlayingBox.style.paddingTop = 10f;
            _queueNowPlayingBox.style.paddingBottom = 10f;
            _queueNowPlayingBox.style.marginBottom = 10f;
            _queueContent.Add(_queueNowPlayingBox);

            // Queue list
            var queueHeader = new Label("Up Next");
            queueHeader.style.fontSize = 13f;
            queueHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            queueHeader.style.color = new Color(0.75f, 0.75f, 0.8f);
            queueHeader.style.marginBottom = 6f;
            _queueContent.Add(queueHeader);

            var listScroll = new ScrollView(ScrollViewMode.Vertical);
            listScroll.style.flexGrow = 1f;
            listScroll.style.marginBottom = 10f;
            _queueContent.Add(listScroll);
            _queueListBox = listScroll.contentContainer;

            // Add-to-queue input
            var addHeader = new Label("Add URL");
            addHeader.style.fontSize = 13f;
            addHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            addHeader.style.color = new Color(0.75f, 0.75f, 0.8f);
            addHeader.style.marginBottom = 4f;
            _queueContent.Add(addHeader);

            // Inline error banner, shown when the server rejects a queue add.
            _queueErrorLabel = new Label();
            _queueErrorLabel.style.display = DisplayStyle.None;
            _queueErrorLabel.style.backgroundColor = new Color(0.4f, 0.12f, 0.12f, 0.9f);
            _queueErrorLabel.style.color = new Color(1f, 0.85f, 0.85f);
            _queueErrorLabel.style.fontSize = 12f;
            _queueErrorLabel.style.paddingLeft = 8f;
            _queueErrorLabel.style.paddingRight = 8f;
            _queueErrorLabel.style.paddingTop = 6f;
            _queueErrorLabel.style.paddingBottom = 6f;
            _queueErrorLabel.style.borderTopLeftRadius = 4f;
            _queueErrorLabel.style.borderTopRightRadius = 4f;
            _queueErrorLabel.style.borderBottomLeftRadius = 4f;
            _queueErrorLabel.style.borderBottomRightRadius = 4f;
            _queueErrorLabel.style.marginBottom = 6f;
            _queueErrorLabel.style.whiteSpace = WhiteSpace.Normal;
            _queueContent.Add(_queueErrorLabel);

            _queueUrlField = new TextField();
            _queueUrlField.value = "";
            _queueUrlField.style.height = 28f;
            _queueUrlField.style.marginBottom = 6f;
            var qInput = _queueUrlField.Q<VisualElement>("unity-text-input");
            if (qInput != null)
            {
                qInput.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f);
                qInput.style.color = new Color(0.9f, 0.9f, 0.9f);
                qInput.style.fontSize = 12f;
                qInput.style.paddingLeft = 8f;
                qInput.style.paddingRight = 8f;
            }
            _queueUrlField.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    SubmitQueueUrl();
                    e.StopPropagation();
                }
            });
            _queueContent.Add(_queueUrlField);

            var addRow = new VisualElement();
            addRow.style.flexDirection = FlexDirection.Row;
            _queueContent.Add(addRow);

            var addBtn = CreateStyledButton("Add to Queue", new Color(0.25f, 0.55f, 0.3f), SubmitQueueUrl);
            addBtn.style.flexGrow = 1f;
            addBtn.style.height = 30f;
            addBtn.style.marginRight = 4f;
            addRow.Add(addBtn);

            var useCurrentBtn = CreateStyledButton("Use Current", new Color(0.3f, 0.4f, 0.6f), () =>
            {
                if (!string.IsNullOrEmpty(_url))
                {
                    _queueUrlField.value = _url;
                    SubmitQueueUrl();
                }
            });
            useCurrentBtn.style.height = 30f;
            useCurrentBtn.style.paddingLeft = 8f;
            useCurrentBtn.style.paddingRight = 8f;
            addRow.Add(useCurrentBtn);

            // Quick browse shortcuts — navigate the in-overlay WebView to the
            // major queue-friendly platforms so players can find a video and
            // copy its URL without alt-tabbing. NavigateTo handles the trusted-
            // domain bypass and goes through the same WebView the rest of the
            // overlay uses.
            var browseRow = new VisualElement();
            browseRow.style.flexDirection = FlexDirection.Row;
            browseRow.style.marginTop = 8f;
            _queueContent.Add(browseRow);

            var browseLabel = new Label("Browse:");
            browseLabel.style.fontSize = 11f;
            browseLabel.style.color = new Color(0.6f, 0.6f, 0.65f);
            browseLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            browseLabel.style.marginRight = 6f;
            browseLabel.style.minWidth = 48f;
            browseRow.Add(browseLabel);

            var ytBtn = CreateStyledButton("YouTube", new Color(0.7f, 0.15f, 0.15f),
                () => NavigateTo("https://www.youtube.com/"));
            ytBtn.style.height = 26f;
            ytBtn.style.flexGrow = 1f;
            ytBtn.style.fontSize = 12f;
            ytBtn.style.paddingLeft = 6f;
            ytBtn.style.paddingRight = 6f;
            ytBtn.style.marginRight = 4f;
            browseRow.Add(ytBtn);

            var twitchBtn = CreateStyledButton("Twitch", new Color(0.4f, 0.25f, 0.65f),
                () => NavigateTo("https://www.twitch.tv/directory"));
            twitchBtn.style.height = 26f;
            twitchBtn.style.flexGrow = 1f;
            twitchBtn.style.fontSize = 12f;
            twitchBtn.style.paddingLeft = 6f;
            twitchBtn.style.paddingRight = 6f;
            browseRow.Add(twitchBtn);
        }

        private static void ToggleQueuePanel()
        {
            _queueExpanded = !_queueExpanded;
            if (_queueContent != null)
                _queueContent.style.display = _queueExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            if (_queueTabLabel != null)
                _queueTabLabel.text = _queueExpanded ? "◀\nQ\nU\nE\nU\nE" : "▶\nQ\nU\nE\nU\nE";
        }

        private static void SubmitQueueUrl()
        {
            if (_queueUrlField == null) return;
            string url = _queueUrlField.value?.Trim();
            if (string.IsNullOrEmpty(url)) return;
            Plugin.AddToQueue(url);
            _queueUrlField.value = "";
        }

        /// <summary>
        /// Show a transient error banner in the queue panel (e.g. when the
        /// server rejects an add because the URL isn't on the allowlist).
        /// Auto-hides after ~6 seconds. No-ops if the queue panel isn't built.
        /// </summary>
        public static void ShowQueueError(string text)
        {
            if (_queueErrorLabel == null) return;
            _queueErrorLabel.text = text ?? "";
            _queueErrorLabel.style.display = DisplayStyle.Flex;
            _queueErrorHide?.Pause();
            _queueErrorHide = _queueErrorLabel.schedule.Execute(() =>
            {
                if (_queueErrorLabel != null)
                    _queueErrorLabel.style.display = DisplayStyle.None;
            }).StartingIn(6000);
        }

        private static void RefreshQueuePanel()
        {
            if (_queueNowPlayingBox == null || _queueListBox == null) return;

            // ── Now playing ──
            _queueNowPlayingBox.Clear();

            var current = Plugin.Current;
            if (current == null)
            {
                var empty = new Label("Nothing playing");
                empty.style.fontSize = 13f;
                empty.style.color = new Color(0.6f, 0.6f, 0.6f);
                empty.style.unityFontStyleAndWeight = FontStyle.Italic;
                _queueNowPlayingBox.Add(empty);
            }
            else
            {
                var nowHeader = new Label("Now Playing");
                nowHeader.style.fontSize = 11f;
                nowHeader.style.color = new Color(0.5f, 0.8f, 0.5f);
                nowHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
                nowHeader.style.marginBottom = 2f;
                _queueNowPlayingBox.Add(nowHeader);

                var playerLabel = new Label(current.Username);
                playerLabel.style.fontSize = 14f;
                playerLabel.style.color = Color.white;
                playerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                _queueNowPlayingBox.Add(playerLabel);

                var urlLabel = new Label(Truncate(current.Url, 48));
                urlLabel.style.fontSize = 11f;
                urlLabel.style.color = new Color(0.65f, 0.8f, 1f);
                urlLabel.style.whiteSpace = WhiteSpace.Normal;
                urlLabel.style.marginBottom = 8f;
                _queueNowPlayingBox.Add(urlLabel);

                // Skip control. Owners can drop their own video without needing votes
                // (single-click button). Everyone else gets a progress-bar vote-skip:
                // a clickable track whose fill = current votes / threshold, so you can
                // see the tally fill up as players vote without needing to read text.
                //
                // Sizing rule: explicit pixel height + flexShrink=0. The previous
                // implementation used flexGrow=1, which Yoga can resolve to zero
                // when the column parent has already laid itself out around earlier
                // content (the bug that ate the vote-skip bar). flexShrink=0 keeps
                // the child at its requested size regardless of what the parent's
                // remaining-space algorithm decides.
                if (Plugin.IsLocalOwnerOfCurrent())
                {
                    _voteSkipBtn = CreateStyledButton(
                        "Skip (your video)",
                        new Color(0.6f, 0.35f, 0.2f),
                        Plugin.OwnerVetoCurrent);
                    _voteSkipBtn.style.height = 30f;
                    _voteSkipBtn.style.flexShrink = 0f;
                }
                else
                {
                    _voteSkipBtn = BuildVoteSkipProgressBar(
                        current.VoteSkippers.Count,
                        Plugin.GetVoteSkipThreshold(),
                        Plugin.HasLocalVotedSkip());
                    _voteSkipBtn.style.flexShrink = 0f;
                }
                _queueNowPlayingBox.Add(_voteSkipBtn);

                // Admin force-skip — always visible to admins regardless of
                // ownership, so the same admin can drop someone else's video
                // (e.g. inappropriate content, broken stream) without rounding
                // up votes. Server validates the admin status independently.
                if (Plugin.IsLocalClientAdmin())
                {
                    var fSkip = CreateStyledButton(
                        "Force Skip (admin)",
                        new Color(0.55f, 0.18f, 0.18f),
                        Plugin.AdminForceSkip);
                    fSkip.style.height = 26f;
                    fSkip.style.marginTop = 6f;
                    fSkip.style.fontSize = 12f;
                    fSkip.style.flexShrink = 0f;
                    // Tighter vertical padding than CreateStyledButton's 7px default
                    // so the 12pt label sits centered inside the 26px button.
                    fSkip.style.paddingTop = 3f;
                    fSkip.style.paddingBottom = 3f;
                    _queueNowPlayingBox.Add(fSkip);
                }
            }

            // ── Queue list ──
            _queueListBox.Clear();

            var queue = Plugin.Queue;
            if (queue.Count == 0)
            {
                var empty = new Label("(empty)");
                empty.style.fontSize = 12f;
                empty.style.color = new Color(0.5f, 0.5f, 0.5f);
                empty.style.unityFontStyleAndWeight = FontStyle.Italic;
                _queueListBox.Add(empty);
            }
            else
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                ulong localClientId = nm != null ? nm.LocalClientId : ulong.MaxValue;

                for (int i = 0; i < queue.Count; i++)
                {
                    var item = queue[i];
                    long itemId = item.Id; // capture for lambda — id is stable across shifts

                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.backgroundColor = new Color(0.14f, 0.14f, 0.18f);
                    row.style.marginBottom = 4f;
                    row.style.paddingLeft = 8f;
                    row.style.paddingRight = 6f;
                    row.style.paddingTop = 6f;
                    row.style.paddingBottom = 6f;
                    row.style.borderTopLeftRadius = 4f;
                    row.style.borderTopRightRadius = 4f;
                    row.style.borderBottomLeftRadius = 4f;
                    row.style.borderBottomRightRadius = 4f;

                    var info = new VisualElement();
                    info.style.flexGrow = 1f;
                    info.style.flexShrink = 1f;
                    row.Add(info);

                    var posLabel = new Label((i + 1) + ". " + item.Username);
                    posLabel.style.fontSize = 12f;
                    posLabel.style.color = Color.white;
                    posLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                    info.Add(posLabel);

                    var urlLabel = new Label(Truncate(item.Url, 36));
                    urlLabel.style.fontSize = 10f;
                    urlLabel.style.color = new Color(0.6f, 0.75f, 0.95f);
                    urlLabel.style.whiteSpace = WhiteSpace.Normal;
                    info.Add(urlLabel);

                    // Remove button (only for your own items)
                    if (item.ClientId == localClientId)
                    {
                        var rmBtn = CreateStyledButton("✕", new Color(0.55f, 0.2f, 0.2f), () =>
                        {
                            Plugin.RemoveFromQueue(itemId);
                        });
                        rmBtn.style.width = 24f;
                        rmBtn.style.height = 24f;
                        rmBtn.style.paddingLeft = 0f;
                        rmBtn.style.paddingRight = 0f;
                        rmBtn.style.fontSize = 12f;
                        rmBtn.style.marginLeft = 4f;
                        row.Add(rmBtn);
                    }

                    _queueListBox.Add(row);
                }
            }
        }

        /// <summary>
        /// Build the vote-skip control as a clickable progress track. Fill width
        /// represents votes/threshold so the tally is visible at a glance. Clicking
        /// anywhere on the track toggles the local player's vote.
        /// </summary>
        private static VisualElement BuildVoteSkipProgressBar(int votes, int needed, bool voted)
        {
            // Clamp progress to [0, 1] — if for some reason votes exceeds needed (e.g.
            // server already advanced), avoid a fill wider than the track.
            float progress = needed > 0 ? Mathf.Clamp01((float)votes / needed) : 0f;

            // Colors: bright red when not voted (urgent action), warmer amber when voted
            // (acknowledged). Track is a dim version, fill is the saturated version.
            Color trackColor = voted ? new Color(0.22f, 0.16f, 0.10f) : new Color(0.30f, 0.10f, 0.10f);
            Color trackHover = voted ? new Color(0.30f, 0.22f, 0.14f) : new Color(0.42f, 0.16f, 0.16f);
            Color fillColor  = voted ? new Color(0.85f, 0.62f, 0.30f) : new Color(0.85f, 0.30f, 0.30f);

            var track = new VisualElement();
            // Explicit height + no flexGrow. Caller sets flexShrink=0 on the
            // returned element so Yoga can't collapse it in the column parent —
            // the v1.0.1 bug where the bar disappeared was flexGrow=1 letting
            // the remaining-space algorithm resolve to zero.
            track.style.height = 30f;
            track.style.position = Position.Relative;
            track.style.overflow = Overflow.Hidden;
            track.style.backgroundColor = trackColor;
            track.style.borderTopLeftRadius     = 4f;
            track.style.borderTopRightRadius    = 4f;
            track.style.borderBottomLeftRadius  = 4f;
            track.style.borderBottomRightRadius = 4f;

            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.left   = 0f;
            fill.style.top    = 0f;
            fill.style.bottom = 0f;
            fill.style.width  = new Length(progress * 100f, LengthUnit.Percent);
            fill.style.backgroundColor = fillColor;
            fill.pickingMode = PickingMode.Ignore; // clicks go through to the track
            track.Add(fill);

            var label = new Label((voted ? "Voted Skip " : "Vote Skip ") + "(" + votes + "/" + needed + ")");
            label.style.position = Position.Absolute;
            label.style.left   = 0f;
            label.style.right  = 0f;
            label.style.top    = 0f;
            label.style.bottom = 0f;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.color = Color.white;
            label.style.fontSize = 13f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Soft shadow keeps the text readable against either the dim track
            // background (low fill) or the saturated fill colour (high fill).
            label.style.textShadow = new TextShadow { offset = new Vector2(0f, 1f), blurRadius = 2f, color = new Color(0f, 0f, 0f, 0.85f) };
            label.pickingMode = PickingMode.Ignore;
            track.Add(label);

            track.RegisterCallback<ClickEvent>(_ => Plugin.ToggleVoteSkip());
            track.RegisterCallback<MouseEnterEvent>(_ => track.style.backgroundColor = trackHover);
            track.RegisterCallback<MouseLeaveEvent>(_ => track.style.backgroundColor = trackColor);
            return track;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static void BuildFooter(VisualElement card)
        {
            var footer = new VisualElement();
            footer.style.flexDirection   = FlexDirection.Row;
            footer.style.justifyContent  = Justify.SpaceBetween;
            footer.style.alignItems      = Align.Center;
            footer.style.paddingLeft     = 14f;
            footer.style.paddingRight    = 14f;
            footer.style.paddingTop      = 9f;
            footer.style.paddingBottom   = 9f;
            footer.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f);
            footer.style.flexShrink = 0f;
            card.Add(footer);

            var leftButtons = new VisualElement();
            leftButtons.style.flexDirection = FlexDirection.Row;
            leftButtons.style.alignItems = Align.Center;
            footer.Add(leftButtons);

            var steamBtn = CreateStyledButton("Open in Steam Browser", new Color(0.2f, 0.45f, 0.7f), () =>
            {
                string url = _urlField != null ? _urlField.value.Trim() : _url;
                TryOpenSteamBrowser(url);
                if (!SteamManager.IsInitialized) OpenExternal(url);
            });
            steamBtn.style.marginRight = 8f;
            leftButtons.Add(steamBtn);

            var extBtn = CreateStyledButton("External Browser", new Color(0.35f, 0.35f, 0.4f), () =>
            {
                string url = _urlField != null ? _urlField.value.Trim() : _url;
                OpenExternal(url);
            });
            leftButtons.Add(extBtn);

            // Rules-acknowledgement notice — any way of dismissing/leaving this MOTD
            // (Got it!, X, opening an external browser) is treated as the player
            // acknowledging the server rules, so state it where they can't miss it.
            var rulesNotice = new Label("Closing this window means you agree to the server rules.");
            rulesNotice.style.fontSize = 12f;
            rulesNotice.style.color = new Color(0.3f, 0.55f, 1f);
            rulesNotice.style.unityFontStyleAndWeight = FontStyle.Bold;
            rulesNotice.style.unityTextAlign = TextAnchor.MiddleCenter;
            rulesNotice.style.whiteSpace = WhiteSpace.Normal;
            rulesNotice.style.flexShrink = 1f;
            rulesNotice.style.marginLeft = 16f;
            rulesNotice.style.marginRight = 6f;
            footer.Add(rulesNotice);

            var gotItBtn = CreateStyledButton("Got it!", new Color(0.3f, 0.55f, 1f), Hide);
            gotItBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            footer.Add(gotItBtn);
        }

        private static void OpenExternal(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) url = _url;
                if (string.IsNullOrWhiteSpace(url)) return; // nothing to open
                if (!url.StartsWith("http")) url = "https://" + url;
                Application.OpenURL(url);
            }
            catch (Exception ex)
            {
                Plugin.LogError("Failed to open URL: " + ex.Message);
            }
        }

        // ─── Content Element Builders ───────────────────────────────

        private static void AddHeading(VisualElement parent, string text, float size, Color color)
        {
            var label = new Label(text);
            label.enableRichText = true;
            label.style.fontSize = size;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop    = 16f;
            label.style.marginBottom = 8f;
            parent.Add(label);
        }

        private static void AddParagraph(VisualElement parent, string text)
        {
            AddRichParagraph(parent, text, new Color(0.88f, 0.88f, 0.88f));
        }

        private static void AddRichParagraph(VisualElement parent, string text, Color color)
        {
            var label = new Label(text);
            label.enableRichText = true;
            label.style.fontSize   = 14f;
            label.style.color      = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 10f;
            parent.Add(label);
        }

        /// <summary>
        /// Renders a clickable link that navigates within our overlay browser.
        /// </summary>
        private static void AddClickableLink(VisualElement parent, string text, string url)
        {
            var linkColor = new Color(0.4f, 0.7f, 1f);
            var hoverColor = new Color(0.6f, 0.85f, 1f);

            var label = new Label(text);
            label.style.fontSize   = 14f;
            label.style.color      = linkColor;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 6f;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;

            // Navigate within our overlay on click
            string capturedUrl = url;
            label.RegisterCallback<MouseDownEvent>(e =>
            {
                if (!string.IsNullOrEmpty(capturedUrl))
                    NavigateTo(capturedUrl);
            });
            label.RegisterCallback<MouseEnterEvent>(e => label.style.color = hoverColor);
            label.RegisterCallback<MouseLeaveEvent>(e => label.style.color = linkColor);

            parent.Add(label);
        }

        private static void AddListItem(VisualElement parent, string text)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 6f;
            row.style.marginLeft = 16f;

            var bullet = new Label("\u2022");
            bullet.style.fontSize = 14f;
            bullet.style.color = new Color(0.3f, 0.75f, 1f);
            bullet.style.marginRight = 8f;
            bullet.style.flexShrink = 0f;
            row.Add(bullet);

            var label = new Label(text);
            label.enableRichText = true;
            label.style.fontSize = 14f;
            label.style.color = new Color(0.88f, 0.88f, 0.88f);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1f;
            label.style.flexGrow = 1f;
            row.Add(label);

            parent.Add(row);
        }

        private static void AddBlockquote(VisualElement parent, string text)
        {
            var container = new VisualElement();
            container.style.borderLeftWidth = 3f;
            container.style.borderLeftColor = new Color(0.3f, 0.6f, 1f, 0.6f);
            container.style.paddingLeft   = 14f;
            container.style.paddingTop    = 6f;
            container.style.paddingBottom = 6f;
            container.style.marginBottom  = 10f;
            container.style.marginLeft    = 8f;
            container.style.backgroundColor = new Color(0.14f, 0.14f, 0.17f);

            var label = new Label(text);
            label.style.fontSize   = 13f;
            label.style.color      = new Color(0.75f, 0.75f, 0.8f);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityFontStyleAndWeight = FontStyle.Italic;
            container.Add(label);

            parent.Add(container);
        }

        private static void AddCodeBlock(VisualElement parent, string text)
        {
            var container = new VisualElement();
            container.style.backgroundColor    = new Color(0.08f, 0.08f, 0.1f);
            container.style.borderTopLeftRadius    = 4f;
            container.style.borderTopRightRadius   = 4f;
            container.style.borderBottomLeftRadius  = 4f;
            container.style.borderBottomRightRadius = 4f;
            container.style.paddingLeft   = 14f;
            container.style.paddingRight  = 14f;
            container.style.paddingTop    = 10f;
            container.style.paddingBottom = 10f;
            container.style.marginBottom  = 10f;
            container.style.borderTopWidth    = 1f;
            container.style.borderBottomWidth = 1f;
            container.style.borderLeftWidth   = 1f;
            container.style.borderRightWidth  = 1f;
            container.style.borderTopColor    = new Color(0.2f, 0.2f, 0.25f);
            container.style.borderBottomColor = new Color(0.2f, 0.2f, 0.25f);
            container.style.borderLeftColor   = new Color(0.2f, 0.2f, 0.25f);
            container.style.borderRightColor  = new Color(0.2f, 0.2f, 0.25f);

            var label = new Label(text);
            label.style.fontSize   = 12f;
            label.style.color      = new Color(0.7f, 0.9f, 0.7f);
            label.style.whiteSpace = WhiteSpace.Normal;
            container.Add(label);

            parent.Add(container);
        }

        private static void AddImage(VisualElement parent, string imageUrl, string altText)
        {
            var imgContainer = new VisualElement();
            imgContainer.style.marginBottom = 12f;
            imgContainer.style.alignSelf = Align.Center;
            imgContainer.style.maxWidth = new Length(100f, LengthUnit.Percent);

            bool isGif = !string.IsNullOrEmpty(imageUrl) && imageUrl.Split('?')[0].ToLowerInvariant().EndsWith(".gif");

            string placeholderText = !string.IsNullOrEmpty(altText) ? "[Image: " + altText + "]" : "[Loading image...]";
            if (isGif) placeholderText = "[Loading GIF (static)...]";
            var placeholder = new Label(placeholderText);
            placeholder.style.fontSize = 12f;
            placeholder.style.color = new Color(0.5f, 0.5f, 0.5f);
            placeholder.style.unityFontStyleAndWeight = FontStyle.Italic;
            placeholder.style.unityTextAlign = TextAnchor.MiddleCenter;
            placeholder.style.paddingTop  = 16f;
            placeholder.style.paddingBottom = 16f;
            imgContainer.Add(placeholder);
            parent.Add(imgContainer);

            if (!string.IsNullOrEmpty(imageUrl))
            {
                string imgReferer = MOTDWebContent.DeriveReferer(imageUrl) ?? _url;

                if (isGif)
                {
                    // Fetch raw bytes and decode all frames for animation
                    MOTDWebContent.FetchGif(imageUrl, frames =>
                    {
                        if (imgContainer.parent == null)
                        {
                            // Late callback — destroy decoded frames inline so they don't
                            // sit on the GPU forever.
                            if (frames != null)
                                foreach (var f in frames)
                                    if (f.Texture != null) UnityEngine.Object.Destroy(f.Texture);
                            return;
                        }
                        if (frames == null || frames.Length == 0)
                        {
                            placeholder.text = "[GIF failed to load]";
                            placeholder.style.color = new Color(0.6f, 0.4f, 0.4f);
                            return;
                        }

                        imgContainer.Clear();

                        // Track each decoded frame so CleanupVideoHosts can free GPU memory —
                        // GifDecoder allocates a fresh Texture2D per frame.
                        for (int fi = 0; fi < frames.Length; fi++)
                            if (frames[fi].Texture != null)
                                _managedTextures.Add(frames[fi].Texture);

                        float maxW = 900f;
                        var first = frames[0].Texture;
                        float scale = (first.width > maxW) ? maxW / first.width : 1f;

                        var imgEl = new VisualElement();
                        imgEl.style.width  = first.width  * scale;
                        imgEl.style.height = first.height * scale;
                        imgEl.style.backgroundImage = new StyleBackground(first);
                        imgEl.style.alignSelf = Align.Center;
                        imgContainer.Add(imgEl);

                        if (frames.Length > 1)
                        {
                            var c = MOTDWebContent.RunCoroutine(AnimateGif(frames, imgEl, imgContainer));
                            _gifCoroutines.Add(c);
                        }
                        else
                        {
                            var note = new Label("(GIF — single frame)");
                            note.style.fontSize = 10f;
                            note.style.color = new Color(0.45f, 0.45f, 0.5f);
                            note.style.unityFontStyleAndWeight = FontStyle.Italic;
                            note.style.unityTextAlign = TextAnchor.MiddleCenter;
                            imgContainer.Add(note);
                        }
                    }, imgReferer);
                }
                else
                {
                    MOTDWebContent.FetchImage(imageUrl, tex =>
                    {
                        if (imgContainer.parent == null)
                        {
                            // Late callback for a container that's already been cleared/destroyed.
                            // Destroy the texture inline since it'll never be tracked.
                            if (tex != null) UnityEngine.Object.Destroy(tex);
                            return;
                        }

                        if (tex == null)
                        {
                            placeholder.text = !string.IsNullOrEmpty(altText)
                                ? "[Could not load: " + altText + "]"
                                : "[Image failed to load]";
                            placeholder.style.color = new Color(0.6f, 0.4f, 0.4f);
                            return;
                        }

                        _managedTextures.Add(tex);

                        imgContainer.Clear();

                        float maxW = 900f;
                        float scale = (tex.width > maxW) ? maxW / tex.width : 1f;

                        var imgEl = new VisualElement();
                        imgEl.style.width  = tex.width  * scale;
                        imgEl.style.height = tex.height * scale;
                        imgEl.style.backgroundImage = new StyleBackground(tex);
                        imgEl.style.alignSelf = Align.Center;
                        imgContainer.Add(imgEl);

                        if (!string.IsNullOrEmpty(altText))
                        {
                            var caption = new Label(altText);
                            caption.style.fontSize = 11f;
                            caption.style.color = new Color(0.5f, 0.5f, 0.55f);
                            caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                            caption.style.marginTop = 4f;
                            imgContainer.Add(caption);
                        }
                    }, referer: imgReferer);
                }
            }
        }

        private static IEnumerator AnimateGif(GifFrame[] frames, VisualElement imgEl, VisualElement container)
        {
            int i = 0;
            while (container.parent != null)
            {
                imgEl.style.backgroundImage = new StyleBackground(frames[i].Texture);
                yield return new WaitForSeconds(frames[i].Delay);
                i = (i + 1) % frames.Length;
            }
        }

        private static void AddNumberedItem(VisualElement parent, int number, string text)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 6f;
            row.style.marginLeft = 16f;

            var numLabel = new Label(number + ".");
            numLabel.style.fontSize = 14f;
            numLabel.style.color = new Color(0.3f, 0.75f, 1f);
            numLabel.style.marginRight = 8f;
            numLabel.style.flexShrink = 0f;
            numLabel.style.minWidth = 22f;
            numLabel.style.unityTextAlign = TextAnchor.UpperRight;
            row.Add(numLabel);

            var label = new Label(text);
            label.enableRichText = true;
            label.style.fontSize = 14f;
            label.style.color = new Color(0.88f, 0.88f, 0.88f);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 1f;
            label.style.flexGrow = 1f;
            row.Add(label);

            parent.Add(row);
        }

        private static void AddSearchBar(VisualElement parent, string placeholder, string actionUrl, string paramName)
        {
            if (string.IsNullOrEmpty(actionUrl)) return;

            var row = new VisualElement();
            row.style.flexDirection  = FlexDirection.Row;
            row.style.alignItems     = Align.Center;
            row.style.marginTop      = 10f;
            row.style.marginBottom   = 10f;
            row.style.paddingLeft    = 4f;
            row.style.paddingRight   = 4f;
            row.style.paddingTop     = 6f;
            row.style.paddingBottom  = 6f;
            row.style.backgroundColor = new Color(0.1f, 0.1f, 0.13f);
            row.style.borderTopLeftRadius    = 6f;
            row.style.borderTopRightRadius   = 6f;
            row.style.borderBottomLeftRadius  = 6f;
            row.style.borderBottomRightRadius = 6f;
            row.style.borderTopWidth    = 1f;
            row.style.borderBottomWidth = 1f;
            row.style.borderLeftWidth   = 1f;
            row.style.borderRightWidth  = 1f;
            row.style.borderTopColor    = new Color(0.3f, 0.3f, 0.4f);
            row.style.borderBottomColor = new Color(0.3f, 0.3f, 0.4f);
            row.style.borderLeftColor   = new Color(0.3f, 0.3f, 0.4f);
            row.style.borderRightColor  = new Color(0.3f, 0.3f, 0.4f);

            var field = new TextField();
            field.style.flexGrow = 1f;
            field.style.marginRight = 6f;
            var textInput = field.Q<VisualElement>("unity-text-input");
            if (textInput != null)
            {
                textInput.style.backgroundColor = new Color(0.07f, 0.07f, 0.1f);
                textInput.style.color = new Color(0.9f, 0.9f, 0.9f);
                textInput.style.fontSize = 13f;
            }
            // Show placeholder text via label overlay
            var ph = new Label(!string.IsNullOrEmpty(placeholder) ? placeholder : "Search...");
            ph.style.position = Position.Absolute;
            ph.style.left = 8f; ph.style.top = 4f;
            ph.style.color = new Color(0.45f, 0.45f, 0.5f);
            ph.style.fontSize = 13f;
            ph.style.unityFontStyleAndWeight = FontStyle.Italic;
            ph.pickingMode = PickingMode.Ignore;
            field.Add(ph);
            field.RegisterValueChangedCallback(e =>
                ph.style.display = string.IsNullOrEmpty(e.newValue) ? DisplayStyle.Flex : DisplayStyle.None);
            row.Add(field);

            string capturedAction = actionUrl;
            string capturedParam  = string.IsNullOrEmpty(paramName) ? "q" : paramName;
            var goBtn = CreateStyledButton("Search", new Color(0.25f, 0.5f, 0.85f), () =>
            {
                string query = field.value.Trim();
                if (string.IsNullOrEmpty(query)) return;
                string sep = capturedAction.Contains("?") ? "&" : "?";
                NavigateTo(capturedAction + sep + capturedParam + "=" +
                    Uri.EscapeDataString(query));
            });
            goBtn.style.height = 28f;
            row.Add(goBtn);

            // Also navigate on Enter key
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    string query = field.value.Trim();
                    if (!string.IsNullOrEmpty(query))
                    {
                        string sep = capturedAction.Contains("?") ? "&" : "?";
                        NavigateTo(capturedAction + sep + capturedParam + "=" +
                            Uri.EscapeDataString(query));
                    }
                }
            });

            parent.Add(row);
        }

        private static void AddVideoElement(VisualElement parent, string url, string title, bool isEmbed)
        {
            // Embeds (YouTube/Vimeo iframes) can't be decoded natively — show a card
            if (isEmbed)
            {
                AddEmbedCard(parent, url, title);
                return;
            }

            // ── Direct video URL → inline VideoPlayer ──
            var container = new VisualElement();
            container.style.marginBottom = 12f;
            container.style.alignSelf = Align.Center;
            container.style.maxWidth = new Length(100f, LengthUnit.Percent);
            container.style.backgroundColor = new Color(0.04f, 0.04f, 0.06f);
            container.style.borderTopLeftRadius    = 6f;
            container.style.borderTopRightRadius   = 6f;
            container.style.borderBottomLeftRadius  = 6f;
            container.style.borderBottomRightRadius = 6f;
            container.style.overflow = Overflow.Hidden;

            // Video frame
            var videoFrame = new VisualElement();
            videoFrame.style.width  = 600f;
            videoFrame.style.height = 340f;
            videoFrame.style.backgroundColor = new Color(0.02f, 0.02f, 0.04f);
            container.Add(videoFrame);

            // Status label overlaid on frame
            var statusLabel = new Label("Downloading video...");
            statusLabel.style.fontSize = 12f;
            statusLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            statusLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            statusLabel.style.position = Position.Absolute;
            statusLabel.style.left  = 0f;
            statusLabel.style.right = 0f;
            statusLabel.style.top   = new Length(45f, LengthUnit.Percent);
            videoFrame.Add(statusLabel);

            MOTDVideoHost host = null;

            // ── Progress bar row ──
            var progressRow = new VisualElement();
            progressRow.style.paddingLeft   = 10f;
            progressRow.style.paddingRight  = 10f;
            progressRow.style.paddingTop    = 6f;
            progressRow.style.paddingBottom = 6f;
            progressRow.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f);
            progressRow.style.flexDirection = FlexDirection.Row;
            progressRow.style.alignItems    = Align.Center;

            Action<float> setProgress = null;
            AddCustomSlider(progressRow, -1f, 0f,
                onChange: v => host?.SeekTo(v),
                setter: out setProgress);

            container.Add(progressRow);

            // ── Controls bar ──
            var controls = new VisualElement();
            controls.style.flexDirection   = FlexDirection.Row;
            controls.style.alignItems      = Align.Center;
            controls.style.paddingLeft     = 8f;
            controls.style.paddingRight    = 8f;
            controls.style.paddingTop      = 5f;
            controls.style.paddingBottom   = 5f;
            controls.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f);

            // Play / Pause
            var playPauseBtn = CreateStyledButton("\u23F8", new Color(0.25f, 0.25f, 0.3f), () =>
                host?.TogglePlayPause());
            playPauseBtn.style.paddingLeft  = 8f;
            playPauseBtn.style.paddingRight = 8f;
            playPauseBtn.style.height = 24f;
            playPauseBtn.style.marginRight = 6f;
            controls.Add(playPauseBtn);

            // Time label
            var timeLabel = new Label("0:00 / 0:00");
            timeLabel.style.fontSize   = 11f;
            timeLabel.style.color      = new Color(0.55f, 0.55f, 0.6f);
            timeLabel.style.marginRight = 8f;
            timeLabel.style.flexShrink  = 0f;
            controls.Add(timeLabel);

            // Spacer
            var spacer = new VisualElement();
            spacer.style.flexGrow = 1f;
            controls.Add(spacer);

            // Volume icon
            var volIcon = new Label("Vol");
            volIcon.style.fontSize   = 10f;
            volIcon.style.color      = new Color(0.6f, 0.6f, 0.65f);
            volIcon.style.marginRight = 4f;
            volIcon.style.flexShrink  = 0f;
            controls.Add(volIcon);

            // Volume custom slider (starts at 1.0)
            Action<float> setVolume = null;
            AddCustomSlider(controls, 70f, 1f,
                onChange: v => host?.SetVolume(v),
                setter: out setVolume);
            ((VisualElement)controls[controls.childCount - 1]).style.marginRight = 8f;

            // Steam browser fallback
            string capturedUrl = url;
            var steamFallback = CreateStyledButton("Steam Browser", new Color(0.2f, 0.35f, 0.55f), () =>
                TryOpenSteamBrowser(capturedUrl));
            steamFallback.style.height    = 24f;
            steamFallback.style.fontSize  = 11f;
            controls.Add(steamFallback);

            container.Add(controls);
            parent.Add(container);

            host = MOTDVideoHost.Create(url, _url, videoFrame, statusLabel);
            host.ConnectControls(setProgress, t => timeLabel.text = t);
            // Honour the user's global mute/volume — starting at 1.0 ignored the slider
            // and the mute button until the user re-touched a control.
            host.SetVolume(EffectiveVolume);
            _videoHosts.Add(host);
        }

        /// <summary>
        /// Renders a clickable card for embedded videos (YouTube, Vimeo)
        /// that cannot be decoded by Unity's VideoPlayer.
        /// </summary>
        private static void AddEmbedCard(VisualElement parent, string url, string title)
        {
            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
            card.style.borderTopLeftRadius    = 6f;
            card.style.borderTopRightRadius   = 6f;
            card.style.borderBottomLeftRadius  = 6f;
            card.style.borderBottomRightRadius = 6f;
            card.style.borderTopWidth    = 1f;
            card.style.borderBottomWidth = 1f;
            card.style.borderLeftWidth   = 1f;
            card.style.borderRightWidth  = 1f;
            card.style.borderTopColor    = new Color(0.3f, 0.3f, 0.4f);
            card.style.borderBottomColor = new Color(0.3f, 0.3f, 0.4f);
            card.style.borderLeftColor   = new Color(0.3f, 0.3f, 0.4f);
            card.style.borderRightColor  = new Color(0.3f, 0.3f, 0.4f);
            card.style.paddingLeft   = 14f;
            card.style.paddingRight  = 14f;
            card.style.paddingTop    = 12f;
            card.style.paddingBottom = 12f;
            card.style.marginBottom  = 12f;
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems    = Align.Center;

            var icon = new Label("\u25B6");
            icon.style.fontSize = 20f;
            icon.style.color = new Color(0.4f, 0.7f, 1f);
            icon.style.marginRight = 12f;
            icon.style.flexShrink = 0f;
            card.Add(icon);

            var textCol = new VisualElement();
            textCol.style.flexGrow = 1f;
            textCol.style.flexShrink = 1f;

            string displayTitle = !string.IsNullOrEmpty(title) ? title : "Embedded Video";
            var titleLabel = new Label(displayTitle);
            titleLabel.style.fontSize = 13f;
            titleLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.whiteSpace = WhiteSpace.Normal;
            textCol.Add(titleLabel);

            var subLabel = new Label("Click to open in Steam browser");
            subLabel.style.fontSize = 11f;
            subLabel.style.color = new Color(0.5f, 0.5f, 0.55f);
            subLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            textCol.Add(subLabel);

            card.Add(textCol);

            string capturedUrl = url;
            card.RegisterCallback<MouseDownEvent>(e => TryOpenSteamBrowser(capturedUrl));
            card.RegisterCallback<MouseEnterEvent>(e =>
            {
                card.style.backgroundColor = new Color(0.12f, 0.12f, 0.18f);
                icon.style.color = new Color(0.6f, 0.85f, 1f);
            });
            card.RegisterCallback<MouseLeaveEvent>(e =>
            {
                card.style.backgroundColor = new Color(0.08f, 0.08f, 0.12f);
                icon.style.color = new Color(0.4f, 0.7f, 1f);
            });

            parent.Add(card);
        }

        private static VisualElement CreateCard(Color? bgColor)
        {
            var card = new VisualElement();
            card.style.backgroundColor = bgColor ?? new Color(0.14f, 0.14f, 0.17f);
            card.style.borderTopLeftRadius    = 6f;
            card.style.borderTopRightRadius   = 6f;
            card.style.borderBottomLeftRadius  = 6f;
            card.style.borderBottomRightRadius = 6f;
            card.style.borderTopWidth    = 1f;
            card.style.borderBottomWidth = 1f;
            card.style.borderLeftWidth   = 1f;
            card.style.borderRightWidth  = 1f;
            card.style.borderTopColor    = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderBottomColor = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderLeftColor   = new Color(0.25f, 0.25f, 0.3f);
            card.style.borderRightColor  = new Color(0.25f, 0.25f, 0.3f);
            card.style.paddingLeft   = 16f;
            card.style.paddingRight  = 16f;
            card.style.paddingTop    = 12f;
            card.style.paddingBottom = 12f;
            card.style.marginBottom  = 12f;
            return card;
        }

        /// <summary>
        /// Custom slider built from plain VisualElements — fully styled, works in all Unity versions.
        /// width &lt; 0 means flexGrow=1 (fill available space).
        /// setter: Action that updates the visual position without triggering onChange.
        /// </summary>
        private static void AddCustomSlider(VisualElement parent, float width, float initialValue,
            Action<float> onChange, out Action<float> setter)
        {
            var track = new VisualElement();
            if (width < 0)
                track.style.flexGrow = 1f;
            else
                track.style.width = width;
            track.style.height          = 4f;
            track.style.marginTop       = 8f;
            track.style.marginBottom    = 8f;
            track.style.backgroundColor = new Color(0.18f, 0.18f, 0.23f);
            track.style.borderTopLeftRadius     = 2f;
            track.style.borderTopRightRadius    = 2f;
            track.style.borderBottomLeftRadius  = 2f;
            track.style.borderBottomRightRadius = 2f;
            track.style.position  = Position.Relative;
            track.style.flexShrink = 0f;

            // Filled portion
            var fill = new VisualElement();
            fill.style.position  = Position.Absolute;
            fill.style.left      = 0f;
            fill.style.top       = 0f;
            fill.style.bottom    = 0f;
            fill.style.width     = new Length(Mathf.Clamp01(initialValue) * 100f, LengthUnit.Percent);
            fill.style.backgroundColor = new Color(0.35f, 0.6f, 1f);
            fill.style.borderTopLeftRadius     = 2f;
            fill.style.borderTopRightRadius    = 2f;
            fill.style.borderBottomLeftRadius  = 2f;
            fill.style.borderBottomRightRadius = 2f;
            track.Add(fill);

            // Thumb
            var thumb = new VisualElement();
            thumb.style.position  = Position.Absolute;
            thumb.style.width     = 12f;
            thumb.style.height    = 12f;
            thumb.style.top       = -4f;
            thumb.style.left      = new Length(Mathf.Clamp01(initialValue) * 100f, LengthUnit.Percent);
            thumb.style.backgroundColor = Color.white;
            thumb.style.borderTopLeftRadius     = 6f;
            thumb.style.borderTopRightRadius    = 6f;
            thumb.style.borderBottomLeftRadius  = 6f;
            thumb.style.borderBottomRightRadius = 6f;
            track.Add(thumb);

            // Visual-only update (no onChange)
            Action<float> updateVisual = v =>
            {
                v = Mathf.Clamp01(v);
                fill.style.width = new Length(v * 100f, LengthUnit.Percent);
                thumb.style.left = new Length(v * 100f, LengthUnit.Percent);
            };
            setter = updateVisual;

            // Pointer interaction
            bool dragging = false;
            Action<float> applyDrag = v =>
            {
                updateVisual(v);
                onChange?.Invoke(v);
            };

            track.RegisterCallback<PointerDownEvent>(e =>
            {
                dragging = true;
                track.CapturePointer(e.pointerId);
                float w = track.resolvedStyle.width;
                if (w > 0) applyDrag(e.localPosition.x / w);
            });
            track.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!dragging) return;
                float w = track.resolvedStyle.width;
                if (w > 0) applyDrag(e.localPosition.x / w);
            });
            track.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!dragging) return;
                dragging = false;
                track.ReleasePointer(e.pointerId);
            });

            parent.Add(track);
        }

        private static void AddSeparator(VisualElement parent)
        {
            var line = new VisualElement();
            line.style.height = 1f;
            line.style.backgroundColor = new Color(0.3f, 0.3f, 0.35f);
            line.style.marginTop    = 12f;
            line.style.marginBottom = 12f;
            parent.Add(line);
        }

        private static Button CreateStyledButton(string text, Color accentColor, Action onClick)
        {
            var btn = new Button(onClick);
            btn.text = text;
            btn.style.fontSize        = 13f;
            btn.style.backgroundColor = accentColor;
            btn.style.color           = Color.white;
            btn.style.paddingLeft     = 16f;
            btn.style.paddingRight    = 16f;
            btn.style.paddingTop      = 7f;
            btn.style.paddingBottom   = 7f;
            // Center label both axes — Unity Button's default is upper-left, which
            // leaves the text hugging a corner inside our taller/wider buttons.
            btn.style.unityTextAlign  = TextAnchor.MiddleCenter;
            btn.style.borderTopLeftRadius     = 4f;
            btn.style.borderTopRightRadius    = 4f;
            btn.style.borderBottomLeftRadius   = 4f;
            btn.style.borderBottomRightRadius  = 4f;

            var hoverColor = new Color(
                Mathf.Min(accentColor.r + 0.15f, 1f),
                Mathf.Min(accentColor.g + 0.15f, 1f),
                Mathf.Min(accentColor.b + 0.15f, 1f)
            );
            btn.RegisterCallback<MouseEnterEvent>(e => btn.style.backgroundColor = hoverColor);
            btn.RegisterCallback<MouseLeaveEvent>(e => btn.style.backgroundColor = accentColor);

            return btn;
        }
    }

    /// <summary>
    /// Polls the keyboard each frame and routes ESC to MOTDUI.OnEscapePressed.
    /// UI Toolkit's KeyDownEvent only fires for the focused element, and the
    /// WebView consumes its own key input, so a top-level poller is the only
    /// way to catch ESC reliably regardless of where focus lives.
    /// </summary>
    internal class MOTDEscPoller : MonoBehaviour
    {
        void Update()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                    MOTDUI.OnEscapePressedFromPoller();
            }
            catch (Exception ex)
            {
                Plugin.LogError("EscPoller: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Persistent poller that toggles the MOTD overlay when F2 is pressed.
    /// Lives for the lifetime of the plugin (separate from the ESC poller, which
    /// only exists while the overlay is open). Uses device-level Keyboard.current
    /// polling so this still works even when our BlockGameplayInput has disabled
    /// every InputActionMap — and is cross-platform (Unity Input System works on
    /// Linux too), so this never reaches the native WebView code path on its own.
    /// MOTDUI.Show internally falls back to the Steam overlay on non-Windows, so
    /// pressing F2 on Linux is safe: it never tries to load WebView.dll.
    /// </summary>
    internal class MOTDHotkeyPoller : MonoBehaviour
    {
        // Periodic re-snapshot interval for the action-map blocker. The user can't
        // see this — it just defends against mods that lazily register a new
        // InputActionMap after the overlay was already open.
        private const int ActionMapRescanFrameInterval = 30; // ~0.5s @ 60fps

        void Update()
        {
            try
            {
                var kb = Keyboard.current;
                if (kb != null && kb.f2Key.wasPressedThisFrame)
                {
                    // Use IsAnyOverlayVisible so F2 also dismisses the trust-
                    // confirm dialog — otherwise pressing F2 with the dialog up
                    // would re-trigger Show and just rebuild the same dialog.
                    if (MOTDUI.IsAnyOverlayVisible)
                        MOTDUI.Hide();
                    else
                        MOTDUI.Show(Plugin.MOTD_URL);
                }

                // While the overlay is open, periodically catch action maps that
                // got enabled after we did our initial sweep (e.g. mods that
                // lazily register a binding the first time their feature is used).
                if (MOTDUI.IsAnyOverlayVisible && Time.frameCount % ActionMapRescanFrameInterval == 0)
                    MOTDUI.ReDisableForeignActionMaps();
            }
            catch (Exception ex)
            {
                Plugin.LogError("HotkeyPoller: " + ex.Message);
            }
        }
    }
}
