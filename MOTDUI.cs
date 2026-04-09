using System;
using System.Collections;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;
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
        private static readonly List<MOTDVideoHost> _videoHosts    = new List<MOTDVideoHost>();
        private static readonly List<Coroutine>     _gifCoroutines = new List<Coroutine>();
        private static string _homeUrl;

        // ── WebView mode ──
        private static MOTDWebView _webView;
        private static VisualElement _webViewElement;
        private static bool _useWebView;
        private static Button _webViewToggleBtn;

        public static bool IsVisible => _isVisible;

        // ─── Public API ─────────────────────────────────────────────

        public static void Show(string url)
        {
            if (Application.isBatchMode) return;

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

            _overlay?.RemoveFromHierarchy();

            Build();
            root.Add(_overlay);

            UnityEngine.Cursor.visible = true;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            _isVisible = true;

            Plugin.Log("MOTD overlay shown for URL: " + url);
            NavigateTo(url, addToHistory: false);
        }

        public static void Hide()
        {
            if (_overlay != null)
            {
                _overlay.RemoveFromHierarchy();
                _overlay = null;
                _contentArea = null;
                _scrollView = null;
                _statusLabel = null;
                _urlField = null;
                _backBtn = null;
                _fwdBtn = null;
                _history.Clear();
                _forwardHistory.Clear();
                CleanupVideoHosts();
                CleanupWebView();
                UnityEngine.Cursor.visible = true;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                _isVisible = false;
            }
        }

        // ─── Navigation ─────────────────────────────────────────────

        /// <summary>
        /// Navigate the in-overlay browser to a new URL.
        /// Called by links, the Go button, and the Back button.
        /// </summary>
        public static void NavigateTo(string url, bool addToHistory = true)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

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
                _urlField.value = url;

            UpdateBackButton();
            UpdateForwardButton();
            ClearContent();

            // ── WebView mode: let the real browser handle everything ──
            if (_useWebView && _webView != null)
            {
                EnsureWebViewVisible();
                _webView.LoadURL(url);
                Plugin.Log("WebView navigating to: " + url);
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

            MOTDWebContent.Fetch(url,
                onSuccess: elements =>
                {
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
            if (lower.Contains("twitch.tv"))                                       return "Twitch";

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
            if (_useWebView && _webView != null)
            {
                _webView.GoBack();
                return;
            }
            if (_history.Count == 0) return;
            if (!string.IsNullOrEmpty(_url))
                _forwardHistory.Push(_url);
            string prev = _history.Pop();
            _url = prev;
            if (_urlField != null) _urlField.value = prev;
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

            MOTDWebContent.Fetch(prev,
                onSuccess: elements =>
                {
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    if (elements.Count == 0) { ShowSpaFallback(); TryOpenSteamBrowser(prev); }
                    else RenderContent(elements);
                },
                onError: error =>
                {
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    ShowErrorState(error);
                }
            );
        }

        private static void GoForward()
        {
            if (_useWebView && _webView != null)
            {
                _webView.GoForward();
                return;
            }
            if (_forwardHistory.Count == 0) return;
            if (!string.IsNullOrEmpty(_url))
                _history.Push(_url);
            string next = _forwardHistory.Pop();
            _url = next;
            if (_urlField != null) _urlField.value = next;
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

            MOTDWebContent.Fetch(next,
                onSuccess: elements =>
                {
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    if (elements.Count == 0) { ShowSpaFallback(); TryOpenSteamBrowser(next); }
                    else RenderContent(elements);
                },
                onError: error =>
                {
                    if (_contentArea == null || _overlay == null) return;
                    RemoveStatusLabel();
                    ShowErrorState(error);
                }
            );
        }

        private static void UpdateBackButton()
        {
            if (_backBtn != null)
            {
                _backBtn.SetEnabled(_history.Count > 0);
                _backBtn.style.opacity = _history.Count > 0 ? 1f : 0.35f;
            }
        }

        private static void UpdateForwardButton()
        {
            if (_fwdBtn != null)
            {
                _fwdBtn.SetEnabled(_forwardHistory.Count > 0);
                _fwdBtn.style.opacity = _forwardHistory.Count > 0 ? 1f : 0.35f;
            }
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
                // Switch back to HTML parser mode
                _useWebView = false;
                HideWebViewElement();
                if (_scrollView != null)
                    _scrollView.style.display = DisplayStyle.Flex;
                UpdateWebViewToggleButton();
                // Re-navigate in HTML mode
                if (!string.IsNullOrEmpty(_url))
                    NavigateTo(_url, addToHistory: false);
            }
            else
            {
                // Switch to WebView mode
                if (!InitWebViewIfNeeded())
                {
                    Plugin.LogError("WebView not available — WebView.dll not found.");
                    return;
                }
                _useWebView = true;
                if (_scrollView != null)
                    _scrollView.style.display = DisplayStyle.None;
                EnsureWebViewVisible();
                UpdateWebViewToggleButton();
                if (!string.IsNullOrEmpty(_url))
                    _webView.LoadURL(_url);
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
            _webViewElement.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _webViewElement.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _webViewElement.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);

            // Insert webview element as sibling to scrollview (inside the card)
            if (_scrollView != null && _scrollView.parent != null)
                _scrollView.parent.Insert(_scrollView.parent.IndexOf(_scrollView) + 1, _webViewElement);

            // Create the webview — use a reasonable default size, will be updated
            _webView = MOTDWebView.Create(
                _webViewElement,
                1280, 800,
                onLoaded: url =>
                {
                    Plugin.Log("WebView loaded: " + url);
                    if (_urlField != null && _useWebView)
                        _urlField.value = url;
                },
                onStarted: url =>
                {
                    Plugin.Log("WebView started: " + url);
                },
                onError: err =>
                {
                    Plugin.LogError("WebView error: " + err);
                }
            );

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

        private static void UpdateWebViewToggleButton()
        {
            if (_webViewToggleBtn == null) return;
            if (_useWebView)
            {
                _webViewToggleBtn.text = "HTML Mode";
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
            try
            {
                if (SteamManager.IsInitialized && !string.IsNullOrEmpty(url))
                    SteamFriends.ActivateGameOverlayToWebPage(url);
            }
            catch (Exception ex)
            {
                Plugin.LogError("Steam overlay failed: " + ex.Message);
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
            card.style.width  = new Length(75f, LengthUnit.Percent);
            card.style.maxWidth  = 1000f;
            card.style.height = new Length(82f, LengthUnit.Percent);
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
            _backBtn = CreateStyledButton("\u25C0", new Color(0.25f, 0.25f, 0.3f), GoBack);
            _backBtn.style.paddingLeft  = 8f;
            _backBtn.style.paddingRight = 8f;
            _backBtn.style.paddingTop = 2f;
            _backBtn.style.paddingBottom = 2f;
            _backBtn.style.height = 28f;
            _backBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _backBtn.style.marginRight = 2f;
            _backBtn.SetEnabled(false);
            _backBtn.style.opacity = 0.35f;
            titleBar.Add(_backBtn);

            // Forward button
            _fwdBtn = CreateStyledButton("\u25B6", new Color(0.25f, 0.25f, 0.3f), GoForward);
            _fwdBtn.style.paddingLeft  = 8f;
            _fwdBtn.style.paddingRight = 8f;
            _fwdBtn.style.paddingTop = 2f;
            _fwdBtn.style.paddingBottom = 2f;
            _fwdBtn.style.height = 28f;
            _fwdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
            _fwdBtn.style.marginRight = 6f;
            _fwdBtn.SetEnabled(false);
            _fwdBtn.style.opacity = 0.35f;
            titleBar.Add(_fwdBtn);

            // Refresh button
            var refreshBtn = CreateStyledButton("\u21BB", new Color(0.25f, 0.25f, 0.3f), () =>
            {
                if (!string.IsNullOrEmpty(_url))
                    NavigateTo(_url, addToHistory: false);
            });
            refreshBtn.style.paddingLeft  = 10f;
            refreshBtn.style.paddingRight = 10f;
            refreshBtn.style.height = 28f;
            refreshBtn.style.marginRight = 2f;
            titleBar.Add(refreshBtn);

            // Home button
            var homeBtn = CreateStyledButton("\u2302", new Color(0.25f, 0.25f, 0.3f), () =>
            {
                if (!string.IsNullOrEmpty(_homeUrl))
                    NavigateTo(_homeUrl);
            });
            homeBtn.style.paddingLeft  = 10f;
            homeBtn.style.paddingRight = 10f;
            homeBtn.style.height = 28f;
            homeBtn.style.marginRight = 6f;
            titleBar.Add(homeBtn);

            // Title
            var title = new Label("Browser");
            title.style.fontSize = 14f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = Color.white;
            title.style.marginRight = 8f;
            title.style.flexShrink = 0f;
            titleBar.Add(title);

            // Editable URL bar
            _urlField = new TextField();
            _urlField.value = _url ?? "";
            _urlField.style.flexGrow = 1f;
            _urlField.style.height = 28f;
            _urlField.style.marginRight = 6f;

            var textInput = _urlField.Q<VisualElement>("unity-text-input");
            if (textInput != null)
            {
                textInput.style.backgroundColor = new Color(0.15f, 0.15f, 0.18f);
                textInput.style.color = new Color(0.55f, 0.75f, 0.55f);
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
            goBtn.style.marginRight = 6f;
            titleBar.Add(goBtn);

            // WebView toggle button
            _webViewToggleBtn = CreateStyledButton("WebView", new Color(0.5f, 0.3f, 0.6f), ToggleWebViewMode);
            _webViewToggleBtn.style.paddingLeft  = 8f;
            _webViewToggleBtn.style.paddingRight = 8f;
            _webViewToggleBtn.style.height = 28f;
            _webViewToggleBtn.style.marginRight = 6f;
            _webViewToggleBtn.style.fontSize = 11f;
            titleBar.Add(_webViewToggleBtn);
            UpdateWebViewToggleButton();

            // Close button
            var closeBtn = new Button(Hide);
            closeBtn.text = "\u2715";
            closeBtn.style.fontSize = 16f;
            closeBtn.style.color = new Color(0.7f, 0.7f, 0.7f);
            closeBtn.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            closeBtn.style.borderTopWidth    = 0f;
            closeBtn.style.borderBottomWidth = 0f;
            closeBtn.style.borderLeftWidth   = 0f;
            closeBtn.style.borderRightWidth  = 0f;
            closeBtn.style.paddingLeft   = 8f;
            closeBtn.style.paddingRight  = 8f;
            closeBtn.style.paddingTop    = 2f;
            closeBtn.style.paddingBottom = 2f;
            closeBtn.RegisterCallback<MouseEnterEvent>(e => closeBtn.style.color = Color.white);
            closeBtn.RegisterCallback<MouseLeaveEvent>(e => closeBtn.style.color = new Color(0.7f, 0.7f, 0.7f));
            titleBar.Add(closeBtn);
        }

        private static void BuildContentArea(VisualElement card)
        {
            _scrollView = new ScrollView(ScrollViewMode.Vertical);
            _scrollView.style.flexGrow = 1f;
            _scrollView.style.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
            card.Add(_scrollView);

            _contentArea = _scrollView.contentContainer;
            _contentArea.style.paddingLeft   = 28f;
            _contentArea.style.paddingRight  = 28f;
            _contentArea.style.paddingTop    = 20f;
            _contentArea.style.paddingBottom = 24f;
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

            var gotItBtn = CreateStyledButton("Got it!", new Color(0.3f, 0.55f, 1f), Hide);
            gotItBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            footer.Add(gotItBtn);
        }

        private static void OpenExternal(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) url = _url;
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

        private static void AddBoldParagraph(VisualElement parent, string text)
        {
            AddRichParagraph(parent, "<b>" + text + "</b>", new Color(0.95f, 0.95f, 0.95f));
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
                        if (imgContainer.parent == null) return;
                        if (frames == null || frames.Length == 0)
                        {
                            placeholder.text = "[GIF failed to load]";
                            placeholder.style.color = new Color(0.6f, 0.4f, 0.4f);
                            return;
                        }

                        imgContainer.Clear();

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
                        if (imgContainer.parent == null) return;

                        if (tex == null)
                        {
                            placeholder.text = !string.IsNullOrEmpty(altText)
                                ? "[Could not load: " + altText + "]"
                                : "[Image failed to load]";
                            placeholder.style.color = new Color(0.6f, 0.4f, 0.4f);
                            return;
                        }

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
            host.SetVolume(1f);
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
}
