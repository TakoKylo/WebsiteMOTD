using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace WebsiteMOTD
{
    public class ContentElement
    {
        public enum ElementType
        {
            Heading1, Heading2, Heading3,
            Paragraph,
            ListItem,       // bullet
            NumberedItem,   // 1. 2. 3.
            Separator,
            Link,
            Blockquote,
            Code,
            Image,
            Video,          // embeddable video (direct URL or YouTube/Vimeo)
            SearchInput,    // GET-form search bar
            CardOpen,       // visual card/box boundary open
            CardClose,
        }

        public ElementType Type;
        public string Text;      // rich-text safe (may contain <b>,<i>,<color> tags)
        public string Url;       // for Link, Image, Video, SearchInput (action URL)
        public string ExtraData; // SearchInput: GET param name (e.g. "q", "tags")
        public bool IsEmbed;     // for Video: true = YouTube/iframe (no native play)
        public int ListNumber;   // for NumberedItem
        public Color? FgColor;   // explicit override (from inline style)
        public Color? BgColor;
        public bool HasCard;     // render with a bordered card background
    }

    public static class MOTDWebContent
    {
        private static GameObject _coroutineHost;
        private static CoroutineRunner _runner;

        public static void Fetch(string url, Action<List<ContentElement>> onSuccess, Action<string> onError)
        {
            EnsureCoroutineRunner();
            _runner.StartCoroutine(FetchCoroutine(url, onSuccess, onError));
        }

        public static void FetchImage(string imageUrl, Action<Texture2D> onDone, string referer = null)
        {
            EnsureCoroutineRunner();
            _runner.StartCoroutine(FetchImageCoroutine(imageUrl, onDone,
                referer ?? DeriveReferer(imageUrl)));
        }

        public static void FetchGif(string gifUrl, Action<GifFrame[]> onDone, string referer = null)
        {
            EnsureCoroutineRunner();
            _runner.StartCoroutine(FetchGifCoroutine(gifUrl, onDone,
                referer ?? DeriveReferer(gifUrl)));
        }

        public static Coroutine RunCoroutine(IEnumerator routine)
        {
            EnsureCoroutineRunner();
            return _runner.StartCoroutine(routine);
        }

        public static void StopManagedCoroutine(Coroutine c)
        {
            if (_runner != null && c != null)
                _runner.StopCoroutine(c);
        }

        // Derive a plausible Referer for known CDN hosts
        public static string DeriveReferer(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var uri = new Uri(url);
                string host = uri.Host.ToLowerInvariant();
                if (host.EndsWith(".poncepuck.net") || host == "poncepuck.net")       return "https://poncepuck.net/";
                return uri.GetLeftPart(UriPartial.Authority) + "/";
            }
            catch { return null; }
        }

        public static void Cleanup()
        {
            if (_coroutineHost != null)
            {
                UnityEngine.Object.Destroy(_coroutineHost);
                _coroutineHost = null;
                _runner = null;
            }
        }

        private static void EnsureCoroutineRunner()
        {
            if (_coroutineHost == null)
            {
                _coroutineHost = new GameObject("MOTD_CoroutineRunner");
                _coroutineHost.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_coroutineHost);
                _runner = _coroutineHost.AddComponent<CoroutineRunner>();
            }
        }

        private static IEnumerator FetchCoroutine(string url, Action<List<ContentElement>> onSuccess, Action<string> onError)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = 15;
                req.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                req.SetRequestHeader("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                { onError?.Invoke(req.error); yield break; }
                onSuccess?.Invoke(ParseHtml(req.downloadHandler.text, url));
            }
        }

        private static IEnumerator FetchImageCoroutine(string imageUrl, Action<Texture2D> onDone, string referer)
        {
            // First do a HEAD-style byte-range check to avoid downloading a 50MB JPEG raw
            using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
            {
                req.timeout = 20;
                req.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                req.SetRequestHeader("Accept", "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
                if (!string.IsNullOrEmpty(referer))
                    req.SetRequestHeader("Referer", referer);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onDone?.Invoke(null);
                    yield break;
                }

                Texture2D tex = DownloadHandlerTexture.GetContent(req);
                if (tex == null) { onDone?.Invoke(null); yield break; }

                // Cap to 2048 on either dimension to prevent OOM on huge images
                const int MAX_DIM = 2048;
                if (tex.width > MAX_DIM || tex.height > MAX_DIM)
                {
                    float scale = Mathf.Min((float)MAX_DIM / tex.width, (float)MAX_DIM / tex.height);
                    int newW = Mathf.Max(1, Mathf.RoundToInt(tex.width  * scale));
                    int newH = Mathf.Max(1, Mathf.RoundToInt(tex.height * scale));

                    var rt = new RenderTexture(newW, newH, 0);
                    Graphics.Blit(tex, rt);
                    UnityEngine.Object.Destroy(tex);

                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var scaled = new Texture2D(newW, newH, TextureFormat.RGB24, false);
                    scaled.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
                    scaled.Apply();
                    RenderTexture.active = prev;
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);

                    tex = scaled;
                }

                onDone?.Invoke(tex);
            }
        }

        private static IEnumerator FetchGifCoroutine(string gifUrl, Action<GifFrame[]> onDone, string referer)
        {
            using (var req = UnityWebRequest.Get(gifUrl))
            {
                req.timeout = 30;
                req.SetRequestHeader("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                req.SetRequestHeader("Accept", "image/gif,image/*,*/*;q=0.8");
                if (!string.IsNullOrEmpty(referer))
                    req.SetRequestHeader("Referer", referer);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onDone?.Invoke(null);
                    yield break;
                }

                GifFrame[] frames = GifDecoder.Decode(req.downloadHandler.data);
                onDone?.Invoke(frames);
            }
        }

        // ─── Main Parser ────────────────────────────────────────────

        public static List<ContentElement> ParseHtml(string html, string baseUrl = "")
        {
            var out_ = new List<ContentElement>();

            // Strip truly non-content blocks
            html = RxDel(html, @"<script[^>]*>[\s\S]*?</script>");
            html = RxDel(html, @"<style[^>]*>[\s\S]*?</style>");
            html = RxDel(html, @"<head[^>]*>[\s\S]*?</head>");
            html = RxDel(html, @"<noscript[^>]*>[\s\S]*?</noscript>");
            html = RxDel(html, @"<!--[\s\S]*?-->");

            // Convert GET search forms → synthetic <motd-search> tags (preserves position).
            // POST forms and forms with no visible text/search input are stripped entirely.
            int _formCount = 0;
            html = Regex.Replace(html, @"<form([^>]*)>([\s\S]*?)</form>", m =>
            {
                string formAttrs = m.Groups[1].Value;
                string inner     = m.Groups[2].Value;
                bool isPost = Regex.IsMatch(formAttrs,
                    @"method\s*=\s*[""']post[""']", RegexOptions.IgnoreCase);
                if (isPost) { Plugin.Log("[MOTD ParseHtml] Skipping POST form"); return ""; }

                // Extract action attribute (may be relative or absent)
                string actionAttr = AttrVal(formAttrs, "action");
                // Decode HTML entities in the action URL (&amp; → &, etc.)
                if (!string.IsNullOrEmpty(actionAttr))
                    actionAttr = DecodeEntities(actionAttr);
                string action;
                
                if (string.IsNullOrEmpty(actionAttr))
                {
                    // HTML spec: if action is missing, default to the form's current location (baseUrl)
                    action = baseUrl;
                    Plugin.Log("[MOTD ParseHtml] GET form has no action — defaulting to baseUrl: " + baseUrl);
                }
                else
                {
                    // Resolve relative URLs (e.g., "/search" → "https://domain.com/search")
                    action = ResolveUrl(actionAttr, baseUrl);
                    Plugin.Log("[MOTD ParseHtml] GET form action resolved: " + action);
                }

                // Collect hidden inputs so they become part of the encoded URL
                var hiddenSb = new System.Text.StringBuilder();
                foreach (Match hi in Regex.Matches(inner,
                    @"<input[^>]*type\s*=\s*[""']hidden[""'][^>]*>", RegexOptions.IgnoreCase))
                {
                    string hname = AttrVal(hi.Value, "name");
                    string hval  = AttrVal(hi.Value, "value") ?? "";
                    if (!string.IsNullOrEmpty(hname))
                    {
                        hiddenSb.Append(hiddenSb.Length == 0 ? "?" : "&");
                        hiddenSb.Append(Uri.EscapeDataString(hname));
                        hiddenSb.Append('=');
                        hiddenSb.Append(Uri.EscapeDataString(hval));
                    }
                }
                if (hiddenSb.Length > 0 && action.Contains("?"))
                {
                    // action already has query string: swap ? → & for our hidden params
                    string hidden = hiddenSb.ToString().TrimStart('?');
                    action = action + "&" + hidden;
                    hiddenSb.Clear();
                }
                string fullAction = action + hiddenSb;

                // Find the primary text/search input
                // Be more lenient: accept inputs without explicit type (defaults to text),
                // or with type=text, type=search, type=email, etc.
                var inputM = Regex.Match(inner, @"<input([^>]*)>", RegexOptions.IgnoreCase);
                while (inputM.Success)
                {
                    string typeAttr = AttrVal(inputM.Value, "type");
                    string type = string.IsNullOrEmpty(typeAttr) ? "text" : typeAttr.ToLowerInvariant();
                    
                    // Accept text-like inputs: text, search, email, url (but not hidden, submit, checkbox, etc.)
                    if (type == "text" || type == "search" || type == "email" || type == "url")
                    {
                        string name = AttrVal(inputM.Value, "name") ?? "q";
                        string ph   = AttrVal(inputM.Value, "placeholder");
                        
                        // If no placeholder, try to find a label for this input
                        if (string.IsNullOrEmpty(ph))
                        {
                            // Look for <label for="inputId">...</label>
                            string inputId = AttrVal(inputM.Value, "id");
                            if (!string.IsNullOrEmpty(inputId))
                            {
                                var labelRx = new Regex(@"<label[^>]*for\s*=\s*[""']" + Regex.Escape(inputId) + @"[""'][^>]*>(.*?)</label>",
                                    RegexOptions.IgnoreCase);
                                var labelM = labelRx.Match(inner);
                                if (labelM.Success)
                                    ph = ToRichText(labelM.Groups[1].Value).Trim();
                            }
                        }
                        
                        ph = (ph ?? "Search...").Replace("\"", "'"); // avoid breaking the synthetic tag
                        string tag = string.Format(
                            "<motd-search action=\"{0}\" name=\"{1}\" placeholder=\"{2}\">",
                            fullAction, name, ph);
                        Plugin.Log("[MOTD ParseHtml] Form → search bar: action=" + fullAction + " name=" + name + " placeholder=" + ph);
                        _formCount++;
                        return tag;
                    }
                    inputM = inputM.NextMatch();
                }
                Plugin.Log("[MOTD ParseHtml] GET form found but no text/search input inside");
                return "";
            }, RegexOptions.IgnoreCase);
            Plugin.Log("[MOTD ParseHtml] Form conversion done: " + _formCount + " search bar(s) generated from baseUrl=" + baseUrl);

            // Strip <button> elements (JS-only, can't navigate) but keep <a class="btn">
            html = RxDel(html, @"<button[^>]*>[\s\S]*?</button>");

            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

            // Block-level split pattern — ordered from most-specific to least
            const string split =
                @"(<header[^>]*>[\s\S]*?</header>" +
                @"|<nav[^>]*>[\s\S]*?</nav>" +
                @"|<footer[^>]*>[\s\S]*?</footer>" +
                @"|<video[^>]*>[\s\S]*?</video>" +
                @"|<video[^>]*/>" +
                @"|<iframe[^>]*>[\s\S]*?</iframe>" +
                @"|<figure[^>]*>[\s\S]*?</figure>" +
                @"|<h[1-6][^>]*>[\s\S]*?</h[1-6]>" +
                @"|<(?:ol|ul)[^>]*>[\s\S]*?</(?:ol|ul)>" +
                @"|<blockquote[^>]*>[\s\S]*?</blockquote>" +
                @"|<pre[^>]*>[\s\S]*?</pre>" +
                @"|<p[^>]*>[\s\S]*?</p>" +
                @"|<(?:td|th|caption|figcaption|dt|dd)[^>]*>[\s\S]*?</(?:td|th|caption|figcaption|dt|dd)>" +
                @"|<img[^>]*/?>|<hr\s*/?>|<motd-search[^>]*>)";

            foreach (var part in Regex.Split(html, split, RegexOptions.IgnoreCase))
            {
                string t = part.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                ParseBlock(t, baseUrl, out_);
            }

            return out_;
        }

        private static void ParseBlock(string t, string baseUrl, List<ContentElement> out_)
        {
            Match m;

            // ── Header / Nav / Footer ──
            m = Rx(t, @"<(?:header|nav|footer)[^>]*>([\s\S]*?)</(?:header|nav|footer)>");
            if (m.Success)
            {
                string inner = m.Groups[1].Value;
                // Pull out any search bars embedded in the nav/header before link extraction
                ExtractSearchBars(inner, baseUrl, out_);
                ExtractLinks(inner, baseUrl, out_);
                return;
            }

            // ── Video (native <video>) ──
            m = Rx(t, @"<video[^>]*(?:src\s*=\s*[""']([^""']+)[""'])?[^>]*>([\s\S]*?)</video>");
            if (m.Success)
            {
                string src = m.Groups[1].Value;
                if (string.IsNullOrEmpty(src))
                {
                    var sm = Rx(m.Groups[2].Value, @"<source[^>]*src\s*=\s*[""']([^""']+)[""']");
                    if (sm.Success) src = sm.Groups[1].Value;
                }
                if (!string.IsNullOrEmpty(src))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Video, Url = ResolveUrl(src, baseUrl) });
                return;
            }

            // ── Self-closing <video .../>  ──
            m = Rx(t, @"<video[^>]*src\s*=\s*[""']([^""']+)[""'][^>]*/>");
            if (m.Success)
            {
                out_.Add(new ContentElement { Type = ContentElement.ElementType.Video, Url = ResolveUrl(m.Groups[1].Value, baseUrl) });
                return;
            }

            // ── Iframe (YouTube / Vimeo embed) ──
            m = Rx(t, @"<iframe[^>]*src\s*=\s*[""']([^""']+)[""']");
            if (m.Success)
            {
                string src = m.Groups[1].Value;
                bool isVideo = src.Contains("youtube") || src.Contains("youtu.be")
                            || src.Contains("vimeo") || src.Contains("dailymotion");
                if (isVideo)
                {
                    out_.Add(new ContentElement
                    {
                        Type = ContentElement.ElementType.Video,
                        Url = src,
                        IsEmbed = true,
                        Text = "Embedded video"
                    });
                }
                return;
            }

            // ── Figure (image + caption) ──
            m = Rx(t, @"<figure[^>]*>([\s\S]*?)</figure>");
            if (m.Success) { ParseBlock(m.Groups[1].Value, baseUrl, out_); return; }

            // ── Headings ──
            m = Rx(t, @"<h([1-6])[^>]*>([\s\S]*?)</h\1>");
            if (m.Success)
            {
                string rich = ToRichText(m.Groups[2].Value);
                if (!string.IsNullOrWhiteSpace(rich))
                {
                    int lvl = int.Parse(m.Groups[1].Value);
                    var et = lvl <= 1 ? ContentElement.ElementType.Heading1
                           : lvl == 2 ? ContentElement.ElementType.Heading2
                                      : ContentElement.ElementType.Heading3;
                    out_.Add(new ContentElement { Type = et, Text = rich });
                }
                return;
            }

            // ── Ordered list ──
            m = Rx(t, @"<ol[^>]*>([\s\S]*?)</ol>");
            if (m.Success)
            {
                int n = 1;
                foreach (Match li in Regex.Matches(m.Groups[1].Value, @"<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase))
                {
                    string txt = ToRichText(li.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(txt))
                        out_.Add(new ContentElement { Type = ContentElement.ElementType.NumberedItem, Text = txt, ListNumber = n++ });
                }
                return;
            }

            // ── Unordered list ──
            m = Rx(t, @"<ul[^>]*>([\s\S]*?)</ul>");
            if (m.Success)
            {
                foreach (Match li in Regex.Matches(m.Groups[1].Value, @"<li[^>]*>([\s\S]*?)</li>", RegexOptions.IgnoreCase))
                {
                    string txt = ToRichText(li.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(txt))
                        out_.Add(new ContentElement { Type = ContentElement.ElementType.ListItem, Text = txt });
                }
                return;
            }

            // ── Standalone list item ──
            m = Rx(t, @"<li[^>]*>([\s\S]*?)</li>");
            if (m.Success)
            {
                string txt = ToRichText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.ListItem, Text = txt });
                return;
            }

            // ── Blockquote ──
            m = Rx(t, @"<blockquote[^>]*>([\s\S]*?)</blockquote>");
            if (m.Success)
            {
                string txt = ToRichText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Blockquote, Text = txt });
                return;
            }

            // ── Pre/Code ──
            m = Rx(t, @"<pre[^>]*>([\s\S]*?)</pre>");
            if (m.Success)
            {
                string txt = StripTags(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Code, Text = txt });
                return;
            }

            // ── Paragraph ──
            m = Rx(t, @"<p[^>]*>([\s\S]*?)</p>");
            if (m.Success)
            {
                ExtractLinks(m.Groups[1].Value, baseUrl, out_,
                    defaultType: ContentElement.ElementType.Paragraph);
                return;
            }

            // ── Table cells / figcaption / dt / dd ──
            m = Rx(t, @"<(?:td|th|caption|figcaption|dt|dd)[^>]*>([\s\S]*?)</(?:td|th|caption|figcaption|dt|dd)>");
            if (m.Success)
            {
                ExtractLinks(m.Groups[1].Value, baseUrl, out_);
                return;
            }

            // ── Images ──
            m = Rx(t, @"<img[^>]*>");
            if (m.Success)
            {
                // Try src first, but skip data: URIs and obvious placeholder blobs
                string src = AttrVal(t, "src");
                if (src != null && (src.StartsWith("data:") || src.StartsWith("blob:")))
                    src = null;

                src = src
                    ?? AttrVal(t, "data-src")
                    ?? AttrVal(t, "data-original")
                    ?? AttrVal(t, "data-lazy-src")
                    ?? AttrVal(t, "data-lazy")
                    ?? AttrVal(t, "data-cfsrc")
                    ?? AttrVal(t, "data-actual-src");

                // Also try srcset (pick first URL)
                if (string.IsNullOrEmpty(src))
                {
                    string ss = AttrVal(t, "srcset") ?? AttrVal(t, "data-srcset");
                    if (!string.IsNullOrEmpty(ss))
                        src = ss.Split(',')[0].Trim().Split(' ')[0];
                }
                if (!string.IsNullOrEmpty(src))
                {
                    src = ResolveUrl(src, baseUrl);
                    string alt = AttrVal(t, "alt") ?? "";
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Image, Url = src, Text = alt });
                }
                return;
            }

            // ── Horizontal rule ──
            if (Regex.IsMatch(t, @"<hr\s*/?>", RegexOptions.IgnoreCase))
            {
                out_.Add(new ContentElement { Type = ContentElement.ElementType.Separator });
                return;
            }

            // ── Search bar (synthetic motd-search tag) ──
            m = Rx(t, @"<motd-search([^>]*)>");
            if (m.Success)
            {
                string tagAttrs = m.Groups[1].Value; // use only this tag's own attributes
                string action = AttrVal(tagAttrs, "action")      ?? baseUrl;
                string name   = AttrVal(tagAttrs, "name")        ?? "q";
                string ph     = AttrVal(tagAttrs, "placeholder") ?? "Search...";
                Plugin.Log("[MOTD ParseBlock] motd-search found: action=" + action + " name=" + name);
                out_.Add(new ContentElement
                {
                    Type      = ContentElement.ElementType.SearchInput,
                    Text      = ph,
                    Url       = ResolveUrl(action, baseUrl),
                    ExtraData = name,
                });
                return;
            }

            // ── Remaining text — extract any links, fall back to paragraph ──
            ExtractLinks(t, baseUrl, out_);
        }

        // ─── Search Bar Extractor ────────────────────────────────────

        private static void ExtractSearchBars(string html, string baseUrl, List<ContentElement> out_)
        {
            var rx = new Regex(@"<motd-search([^>]*)>", RegexOptions.IgnoreCase);
            foreach (Match m in rx.Matches(html))
            {
                string attrs  = m.Groups[1].Value;
                string action = AttrVal(attrs, "action")      ?? baseUrl;
                string name   = AttrVal(attrs, "name")        ?? "q";
                string ph     = AttrVal(attrs, "placeholder") ?? "Search...";
                out_.Add(new ContentElement
                {
                    Type      = ContentElement.ElementType.SearchInput,
                    Text      = ph,
                    Url       = ResolveUrl(action, baseUrl),
                    ExtraData = name,
                });
            }
        }

        // ─── Link Extractor ─────────────────────────────────────────

        private static void ExtractLinks(string html, string baseUrl, List<ContentElement> out_,
            ContentElement.ElementType defaultType = ContentElement.ElementType.Paragraph)
        {
            var linkRx = new Regex(@"<a[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase);
            var matches = linkRx.Matches(html);

            if (matches.Count == 0)
            {
                string txt = ToRichText(html);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = defaultType, Text = txt });
                return;
            }

            int last = 0;
            foreach (Match lm in matches)
            {
                // Text before link
                if (lm.Index > last)
                {
                    string before = ToRichText(html.Substring(last, lm.Index - last));
                    if (!string.IsNullOrWhiteSpace(before))
                        out_.Add(new ContentElement { Type = defaultType, Text = before });
                }

                string href = lm.Groups[1].Value.Trim();
                string linkText = ToRichText(lm.Groups[2].Value).Trim();

                if (!string.IsNullOrEmpty(href) && !href.StartsWith("javascript") && href != "#")
                {
                    href = ResolveUrl(href, baseUrl);
                    if (string.IsNullOrEmpty(linkText)) linkText = href;
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Link, Text = linkText, Url = href });
                }
                else if (!string.IsNullOrEmpty(linkText))
                    out_.Add(new ContentElement { Type = defaultType, Text = linkText });

                last = lm.Index + lm.Length;
            }

            // Text after last link
            if (last < html.Length)
            {
                string after = ToRichText(html.Substring(last));
                if (!string.IsNullOrWhiteSpace(after))
                    out_.Add(new ContentElement { Type = defaultType, Text = after });
            }
        }

        // ─── Rich Text Conversion ────────────────────────────────────

        /// <summary>
        /// Converts HTML inline formatting to Unity rich text tags.
        /// Preserves bold, italic, underline, and CSS color spans.
        /// Strips all other tags.
        /// </summary>
        public static string ToRichText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            // Strip nested block tags we don't want here
            html = RxDel(html, @"<(?:script|style)[^>]*>[\s\S]*?</(?:script|style)>");

            // <br>
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

            // Bold
            html = Regex.Replace(html, @"<(?:strong|b)(?:\s[^>]*)?>", "<b>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:strong|b)>", "</b>", RegexOptions.IgnoreCase);

            // Italic
            html = Regex.Replace(html, @"<(?:em|i)(?:\s[^>]*)?>", "<i>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:em|i)>", "</i>", RegexOptions.IgnoreCase);

            // Underline
            html = Regex.Replace(html, @"<u(?:\s[^>]*)?>", "<u>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</u>", "</u>", RegexOptions.IgnoreCase);

            // Strikethrough → remove text (can't render strikethrough in UI Toolkit)
            html = Regex.Replace(html, @"<(?:s|strike|del)(?:\s[^>]*)?>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:s|strike|del)>", "", RegexOptions.IgnoreCase);

            // Colored spans
            html = Regex.Replace(html,
                @"<span[^>]*style\s*=\s*[""'][^""']*color\s*:\s*([^;""'\s]+)[^""']*[""'][^>]*>",
                m =>
                {
                    string css = Regex.Match(m.Value, @"color\s*:\s*([^;""'\s]+)").Groups[1].Value.Trim();
                    string hex = CssColorToHex(css);
                    return string.IsNullOrEmpty(hex) ? "" : "<color=" + hex + ">";
                }, RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<span[^>]*>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</span>", "</color>", RegexOptions.IgnoreCase);

            // Strip links (keep link text, urls handled separately)
            html = Regex.Replace(html, @"<a[^>]*>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</a>", "", RegexOptions.IgnoreCase);

            // Strip all remaining tags
            html = Regex.Replace(html, @"<[^>]+>", "");

            // Decode entities
            html = DecodeEntities(html);

            // Collapse whitespace
            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @"\n{3,}", "\n\n");

            return html.Trim();
        }

        // ─── CSS Color Parsing ───────────────────────────────────────

        public static string CssColorToHex(string css)
        {
            if (string.IsNullOrEmpty(css)) return null;
            css = css.Trim().ToLowerInvariant();

            if (css.StartsWith("#"))
            {
                string h = css.Substring(1);
                if (h.Length == 3 || h.Length == 4)
                    h = "" + h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
                return h.Length >= 6 ? "#" + h.Substring(0, 6) : null;
            }

            var rgba = Regex.Match(css, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
            if (rgba.Success)
                return string.Format("#{0:X2}{1:X2}{2:X2}",
                    int.Parse(rgba.Groups[1].Value),
                    int.Parse(rgba.Groups[2].Value),
                    int.Parse(rgba.Groups[3].Value));

            // Named colors
            switch (css)
            {
                case "white":   return "#FFFFFF"; case "black":   return "#000000";
                case "red":     return "#FF4444"; case "green":   return "#44CC44";
                case "blue":    return "#4488FF"; case "yellow":  return "#FFEE44";
                case "orange":  return "#FF8C00"; case "purple":  return "#AA44BB";
                case "gray": case "grey": return "#888888";
                case "cyan":    return "#00CCCC"; case "magenta": return "#CC44CC";
                case "silver":  return "#CCCCCC"; case "gold":    return "#FFD700";
                case "navy":    return "#003388"; case "teal":    return "#008888";
                case "pink":    return "#FF88BB"; case "lime":    return "#88FF44";
                case "coral":   return "#FF6655"; case "salmon":  return "#FA8072";
                case "wheat":   return "#F5DEB3"; case "khaki":   return "#BDB76B";
                case "indigo":  return "#4B0082"; case "violet":  return "#EE82EE";
                default: return null;
            }
        }

        // ─── Helpers ────────────────────────────────────────────────

        public static string ResolveUrl(string href, string baseUrl)
        {
            if (string.IsNullOrEmpty(href)) return baseUrl ?? href;
            if (href.StartsWith("http://") || href.StartsWith("https://") || href.StartsWith("//"))
            {
                if (href.StartsWith("//")) href = "https:" + href;
                return href;
            }
            if (string.IsNullOrEmpty(baseUrl)) return href;
            try { return new Uri(new Uri(baseUrl), href).ToString(); }
            catch { return href; }
        }

        private static string AttrVal(string tag, string attr)
        {
            var m = Regex.Match(tag, attr + @"\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static Match Rx(string input, string pattern)
            => Regex.Match(input, pattern, RegexOptions.IgnoreCase);

        private static string RxDel(string input, string pattern)
            => Regex.Replace(input, pattern, "", RegexOptions.IgnoreCase);

        public static string StripTags(string html)
        {
            string text = Regex.Replace(html, @"<[^>]+>", " ");
            return DecodeEntities(text).Trim();
        }

        private static string DecodeEntities(string text)
        {
            return text
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
                .Replace("&nbsp;", " ").Replace("&#160;", " ")
                .Replace("&mdash;", "\u2014").Replace("&ndash;", "\u2013")
                .Replace("&bull;", "\u2022").Replace("&hellip;", "\u2026")
                .Replace("&copy;", "\u00A9").Replace("&reg;", "\u00AE")
                .Replace("&trade;", "\u2122").Replace("&laquo;", "\u00AB")
                .Replace("&raquo;", "\u00BB").Replace("&lsquo;", "\u2018")
                .Replace("&rsquo;", "\u2019").Replace("&ldquo;", "\u201C")
                .Replace("&rdquo;", "\u201D");
        }

        private class CoroutineRunner : MonoBehaviour { }
    }
}
