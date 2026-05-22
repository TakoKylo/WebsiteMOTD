// ─────────────────────────────────────────────────────────────────────────────
// MOTDHtmlRenderer.cs
// Consolidates all HTML-mode rendering helpers:
//   • GifFrame / GifDecoder    — pure-C# GIF89a decoder
//   • MOTDVideoHost            — downloads + plays videos via Unity VideoPlayer
//   • ContentElement           — parsed HTML element model
//   • MOTDWebContent           — fetches & parses HTML pages into ContentElements
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace WebsiteMOTD
{
    // ═════════════════════════════════════════════════════════════════════════
    // GIF Decoder
    // ═════════════════════════════════════════════════════════════════════════

    public struct GifFrame
    {
        public Texture2D Texture;
        public float Delay; // seconds
    }

    /// <summary>
    /// Pure-C# GIF89a decoder. Handles animated GIFs with LZW decompression,
    /// local/global color tables, transparency, and basic disposal methods.
    /// </summary>
    public static class GifDecoder
    {
        public static GifFrame[] Decode(byte[] data)
        {
            if (data == null || data.Length < 13) return null;

            string sig = System.Text.Encoding.ASCII.GetString(data, 0, 6);
            if (sig != "GIF87a" && sig != "GIF89a") return null;

            int pos = 6;

            // Logical Screen Descriptor
            int screenW = ReadU16(data, pos); pos += 2;
            int screenH = ReadU16(data, pos); pos += 2;
            byte lsd    = data[pos++];
            int bgIdx   = data[pos++];
            pos++; // pixel aspect ratio

            bool hasGct  = (lsd & 0x80) != 0;
            int  gctSize = (lsd & 0x07);

            Color32[] gct = null;
            if (hasGct)
                gct = ReadColorTable(data, ref pos, 2 << gctSize);

            var frames = new List<GifFrame>();

            // Per-frame graphics control state
            float delay        = 0.1f;
            bool  transparent  = false;
            int   transIdx     = 0;
            int   disposal     = 0;

            // Compositing canvas (RGBA)
            var canvas    = new Color32[screenW * screenH];
            var prevCanvas = new Color32[screenW * screenH];
            var bgColor   = (gct != null && bgIdx < gct.Length) ? gct[bgIdx] : new Color32(0, 0, 0, 0);

            for (int i = 0; i < canvas.Length; i++) canvas[i] = bgColor;

            while (pos < data.Length)
            {
                byte b = data[pos++];

                // ── Trailer ──
                if (b == 0x3B) break;

                // ── Extension ──
                if (b == 0x21)
                {
                    byte label = data[pos++];
                    if (label == 0xF9) // Graphic Control Extension
                    {
                        pos++; // block size (always 4)
                        byte gce = data[pos++];
                        delay      = Mathf.Max(ReadU16(data, pos) * 0.01f, 0.02f); pos += 2;
                        transIdx   = data[pos++];
                        pos++;   // block terminator

                        transparent = (gce & 0x01) != 0;
                        disposal    = (gce >> 3) & 0x07;
                    }
                    else
                    {
                        SkipSubBlocks(data, ref pos);
                    }
                    continue;
                }

                // ── Image Separator ──
                if (b == 0x2C)
                {
                    int imgLeft = ReadU16(data, pos); pos += 2;
                    int imgTop  = ReadU16(data, pos); pos += 2;
                    int imgW    = ReadU16(data, pos); pos += 2;
                    int imgH    = ReadU16(data, pos); pos += 2;
                    byte ipk    = data[pos++];

                    bool hasLct    = (ipk & 0x80) != 0;
                    bool interlace = (ipk & 0x40) != 0;
                    int  lctSize   = (ipk & 0x07);

                    Color32[] ct = gct;
                    if (hasLct)
                        ct = ReadColorTable(data, ref pos, 2 << lctSize);

                    byte lzwMin   = data[pos++];
                    byte[] imgData = ReadSubBlocks(data, ref pos);

                    int[] indices = LzwDecode(imgData, lzwMin, imgW * imgH);
                    if (indices == null || ct == null) continue;

                    if (interlace) indices = Deinterlace(indices, imgW, imgH);

                    // Apply disposal of previous frame
                    if (disposal == 2) // restore to background
                        for (int i = 0; i < canvas.Length; i++) canvas[i] = bgColor;
                    // disposal == 3 (restore to previous) is rare, skip for now

                    // Blit indices onto canvas
                    for (int row = 0; row < imgH; row++)
                    {
                        for (int col = 0; col < imgW; col++)
                        {
                            int si = row * imgW + col;
                            if (si >= indices.Length) continue;
                            int ci = indices[si];
                            if (transparent && ci == transIdx) continue;
                            int canvasIdx = (imgTop + row) * screenW + (imgLeft + col);
                            if (canvasIdx < 0 || canvasIdx >= canvas.Length) continue;
                            if (ci < 0 || ci >= ct.Length) continue;
                            canvas[canvasIdx] = ct[ci];
                        }
                    }

                    // Create texture (GIF origin is top-left; Unity is bottom-left → flip Y)
                    var tex = new Texture2D(screenW, screenH, TextureFormat.RGBA32, false);
                    var pixels = new Color32[canvas.Length];
                    for (int row = 0; row < screenH; row++)
                        Array.Copy(canvas, (screenH - 1 - row) * screenW, pixels, row * screenW, screenW);
                    tex.SetPixels32(pixels);
                    tex.Apply();

                    frames.Add(new GifFrame { Texture = tex, Delay = delay });

                    // Save canvas if next frame might want to restore to previous
                    Array.Copy(canvas, prevCanvas, canvas.Length);

                    // Reset per-frame state
                    delay       = 0.1f;
                    transparent = false;
                    disposal    = 0;
                    continue;
                }

                // Unknown block — try to skip
                if (pos < data.Length)
                    SkipSubBlocks(data, ref pos);
            }

            return frames.Count > 0 ? frames.ToArray() : null;
        }

        // ─── LZW Decompression ────────────────────────────────────────

        private static int[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
        {
            int clearCode  = 1 << minCodeSize;
            int eoiCode    = clearCode + 1;
            int codeSize   = minCodeSize + 1;
            int nextCode   = eoiCode + 1;
            int codeMask   = (1 << codeSize) - 1;

            var dict = new List<int[]>(4096);
            ResetDict(dict, clearCode);

            var result = new List<int>(pixelCount + 64);
            int bitBuf = 0, bitsLeft = 0, dataPos = 0;
            int[] prev = null;

            while (result.Count < pixelCount)
            {
                while (bitsLeft < codeSize)
                {
                    if (dataPos >= data.Length) goto done;
                    bitBuf |= data[dataPos++] << bitsLeft;
                    bitsLeft += 8;
                }
                int code = bitBuf & codeMask;
                bitBuf   >>= codeSize;
                bitsLeft  -= codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    codeMask = (1 << codeSize) - 1;
                    nextCode = eoiCode + 1;
                    ResetDict(dict, clearCode);
                    prev = null;
                    continue;
                }

                if (code == eoiCode) break;

                int[] entry;
                if (code < dict.Count && dict[code] != null)
                {
                    entry = dict[code];
                }
                else if (code == nextCode && prev != null)
                {
                    entry = new int[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else break;

                result.AddRange(entry);

                if (prev != null && nextCode < 4096)
                {
                    var ne = new int[prev.Length + 1];
                    Array.Copy(prev, ne, prev.Length);
                    ne[prev.Length] = entry[0];

                    if (nextCode < dict.Count) dict[nextCode] = ne;
                    else dict.Add(ne);
                    nextCode++;

                    if (nextCode > codeMask && codeSize < 12)
                    {
                        codeSize++;
                        codeMask = (1 << codeSize) - 1;
                    }
                }

                prev = entry;
            }

            done:
            return result.ToArray();
        }

        private static void ResetDict(List<int[]> dict, int clearCode)
        {
            dict.Clear();
            for (int i = 0; i < clearCode; i++) dict.Add(new[] { i });
            dict.Add(null); // clearCode placeholder
            dict.Add(null); // eoiCode placeholder
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private static Color32[] ReadColorTable(byte[] data, ref int pos, int count)
        {
            var t = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                t[i] = new Color32(data[pos], data[pos + 1], data[pos + 2], 255);
                pos += 3;
            }
            return t;
        }

        private static byte[] ReadSubBlocks(byte[] data, ref int pos)
        {
            var buf = new List<byte>(256);
            while (pos < data.Length)
            {
                int sz = data[pos++];
                if (sz == 0) break;
                for (int i = 0; i < sz && pos < data.Length; i++)
                    buf.Add(data[pos++]);
            }
            return buf.ToArray();
        }

        private static void SkipSubBlocks(byte[] data, ref int pos)
        {
            while (pos < data.Length)
            {
                int sz = data[pos++];
                if (sz == 0) break;
                pos += sz;
            }
        }

        private static int ReadU16(byte[] data, int pos)
            => data[pos] | (data[pos + 1] << 8);

        private static int[] Deinterlace(int[] src, int w, int h)
        {
            var dst = new int[src.Length];
            int s = 0;
            foreach (int start in new[] { 0, 4, 2, 1 })
                foreach (int step in new[] { 8, 8, 4, 2 })
                    for (int y = start; y < h; y += step)
                        for (int x = 0; x < w; x++)
                            dst[y * w + x] = src[s++];
            return dst;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Video Host
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Downloads a video to a temp file (with proper Referer/UA headers) then plays it.
    /// Progress and volume are communicated via Action callbacks — no dependency on
    /// Unity's Slider type, so any visual element can be used as a control.
    /// </summary>
    public class MOTDVideoHost : MonoBehaviour
    {
        private VideoPlayer _player;
        private RenderTexture _rt;
        private VisualElement _target;
        private Label _statusLabel;
        private bool _prepared;
        private string _videoUrl;
        private string _tempFile;

        private Action<float>  _progressSetter;
        private Action<string> _timeSetter;

        public bool IsPlaying  => _player != null && _player.isPlaying;
        public bool IsPrepared => _prepared;

        // ─── Factory ────────────────────────────────────────────────

        public static MOTDVideoHost Create(
            string url, string referer,
            VisualElement target, Label statusLabel)
        {
            var go = new GameObject("MOTD_Video");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);

            var host = go.AddComponent<MOTDVideoHost>();
            host._target      = target;
            host._statusLabel = statusLabel;
            host._videoUrl    = url;
            host.StartCoroutine(host.DownloadThenPlay(url, referer));
            return host;
        }

        /// <summary>Call after Create() to wire up progress bar and time label.</summary>
        public void ConnectControls(Action<float> progressSetter, Action<string> timeSetter)
        {
            _progressSetter = progressSetter;
            _timeSetter     = timeSetter;
        }

        /// <summary>Seek to a normalised position [0, 1].</summary>
        public void SeekTo(float normalized)
        {
            if (_player != null && _prepared && _player.length > 0)
                _player.time = normalized * _player.length;
        }

        /// <summary>Set audio volume [0, 1].</summary>
        public void SetVolume(float volume)
        {
            if (_player != null)
                _player.SetDirectAudioVolume(0, Mathf.Clamp01(volume));
        }

        // ─── Download → Play ────────────────────────────────────────

        private IEnumerator DownloadThenPlay(string url, string referer)
        {
            SetStatus("Downloading video...");

            string ext = ".mp4";
            string clean = url.Split('?')[0];
            int dot = clean.LastIndexOf('.');
            if (dot >= 0 && clean.Length - dot <= 5) ext = clean.Substring(dot);

            // Hash the full URL so two different videos can't collide on the
            // 32-bit GetHashCode space (which has plenty of overlap in practice
            // and would serve a previously-downloaded video under the new URL).
            // Truncated SHA-256 hex is collision-free for any realistic count.
            string urlHash;
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                urlHash = BitConverter.ToString(digest, 0, 8).Replace("-", "");
            }
            _tempFile = Path.Combine(Application.temporaryCachePath,
                "motd_" + urlHash + ext);

            if (!File.Exists(_tempFile))
            {
                using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
                {
                    req.downloadHandler = new DownloadHandlerFile(_tempFile);
                    req.timeout = 60;
                    req.SetRequestHeader("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    req.SetRequestHeader("Accept", "video/webm,video/mp4,video/*,*/*;q=0.8");
                    if (!string.IsNullOrEmpty(referer))
                        req.SetRequestHeader("Referer", referer);

                    yield return req.SendWebRequest();

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Plugin.LogError("Video download failed: " + req.error + " (" + url + ")");
                        SetStatus("Download failed:\n" + req.error);
                        yield break;
                    }
                }
            }

            SetStatus("Loading video...");
            PreparePlayer("file://" + _tempFile);
        }

        private void PreparePlayer(string localUrl)
        {
            _player = gameObject.AddComponent<VideoPlayer>();
            _player.source          = VideoSource.Url;
            _player.url             = localUrl;
            _player.playOnAwake     = false;
            _player.renderMode      = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.Direct;
            _player.isLooping       = true;
            _player.skipOnDrop      = true;
            _player.prepareCompleted += OnPrepared;
            _player.errorReceived    += OnError;
            _player.Prepare();
        }

        private void OnPrepared(VideoPlayer vp)
        {
            int w = (int)vp.width;  if (w <= 0) w = 640;
            int h = (int)vp.height; if (h <= 0) h = 360;

            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            _rt.Create();
            _player.targetTexture = _rt;
            _prepared = true;

            float maxW  = 700f;
            float scale = w > maxW ? maxW / w : 1f;
            _target.style.width  = w * scale;
            _target.style.height = h * scale;
            var bg = new Background();
            bg.renderTexture = _rt;
            _target.style.backgroundImage = new StyleBackground(bg);
            _target.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _target.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            _target.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);

            if (_statusLabel != null && _statusLabel.parent != null)
                _statusLabel.RemoveFromHierarchy();

            _player.Play();
            Plugin.Log("Video playing: " + w + "x" + h + " from " + _videoUrl);
        }

        private void OnError(VideoPlayer vp, string msg)
        {
            Plugin.LogError("Video error: " + msg);
            SetStatus("Video error: " + msg);
            if (_tempFile != null && File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
                _tempFile = null;
            }
        }

        private void SetStatus(string text)
        {
            if (_statusLabel == null) return;
            _statusLabel.text  = text;
            _statusLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
        }

        // ─── Per-frame update ────────────────────────────────────────

        void Update()
        {
            if (!_prepared || _player == null || _player.length <= 0) return;

            _progressSetter?.Invoke((float)(_player.time / _player.length));

            if (_timeSetter != null)
            {
                var cur = TimeSpan.FromSeconds(_player.time);
                var dur = TimeSpan.FromSeconds(_player.length);
                _timeSetter(string.Format("{0}:{1:D2} / {2}:{3:D2}",
                    (int)cur.TotalMinutes, cur.Seconds,
                    (int)dur.TotalMinutes, dur.Seconds));
            }
        }

        // ─── Controls ───────────────────────────────────────────────

        public void Play()  { if (_player != null && _prepared) _player.Play(); }
        public void Pause() { if (_player != null) _player.Pause(); }

        public void TogglePlayPause()
        {
            if (_player == null) return;
            if (_player.isPlaying) _player.Pause();
            else if (_prepared)    _player.Play();
        }

        // ─── Cleanup ────────────────────────────────────────────────

        public void Cleanup()
        {
            StopAllCoroutines();
            if (_player != null)
            {
                _player.Stop();
                _player.prepareCompleted -= OnPrepared;
                _player.errorReceived    -= OnError;
                _player.targetTexture     = null;
            }
            if (_target != null)
                _target.style.backgroundImage = new StyleBackground();
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            Destroy(gameObject);
        }

        void OnDestroy() { if (_rt != null) _rt.Release(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Web Content — HTML Element Model + Parser + Fetcher
    // ═════════════════════════════════════════════════════════════════════════

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

        public static string DeriveReferer(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var uri = new Uri(url);
                string host = uri.Host.ToLowerInvariant();
                if (host.EndsWith(".poncepuck.net") || host == "poncepuck.net") return "https://poncepuck.net/";
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

        // Hard cap on HTML size before parsing. ParseHtml runs synchronously on
        // Unity's main thread with regex backtracking that can stall the game on
        // pathological input — cap the input length so a hostile page can't
        // freeze the client. 256 KB is far more than any reasonable MOTD page;
        // truncation produces a degraded render (cut mid-tag) but never a hang.
        private const int MaxHtmlBytesForParse = 256 * 1024;

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
                string body = req.downloadHandler.text ?? "";
                if (body.Length > MaxHtmlBytesForParse)
                    body = body.Substring(0, MaxHtmlBytesForParse);
                onSuccess?.Invoke(ParseHtml(body, url));
            }
        }

        private static IEnumerator FetchImageCoroutine(string imageUrl, Action<Texture2D> onDone, string referer)
        {
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

            html = RxDel(html, @"<script[^>]*>[\s\S]*?</script>");
            html = RxDel(html, @"<style[^>]*>[\s\S]*?</style>");
            html = RxDel(html, @"<head[^>]*>[\s\S]*?</head>");
            html = RxDel(html, @"<noscript[^>]*>[\s\S]*?</noscript>");
            html = RxDel(html, @"<!--[\s\S]*?-->");

            int _formCount = 0;
            html = Regex.Replace(html, @"<form([^>]*)>([\s\S]*?)</form>", m =>
            {
                string formAttrs = m.Groups[1].Value;
                string inner     = m.Groups[2].Value;
                bool isPost = Regex.IsMatch(formAttrs,
                    @"method\s*=\s*[""']post[""']", RegexOptions.IgnoreCase);
                if (isPost) { Plugin.Log("[MOTD ParseHtml] Skipping POST form"); return ""; }

                string actionAttr = AttrVal(formAttrs, "action");
                if (!string.IsNullOrEmpty(actionAttr))
                    actionAttr = DecodeEntities(actionAttr);
                string action;

                if (string.IsNullOrEmpty(actionAttr))
                {
                    action = baseUrl;
                    Plugin.Log("[MOTD ParseHtml] GET form has no action — defaulting to baseUrl: " + baseUrl);
                }
                else
                {
                    action = ResolveUrl(actionAttr, baseUrl);
                    Plugin.Log("[MOTD ParseHtml] GET form action resolved: " + action);
                }

                var hiddenSb = new StringBuilder();
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
                    string hidden = hiddenSb.ToString().TrimStart('?');
                    action = action + "&" + hidden;
                    hiddenSb.Clear();
                }
                string fullAction = action + hiddenSb;

                var inputM = Regex.Match(inner, @"<input([^>]*)>", RegexOptions.IgnoreCase);
                while (inputM.Success)
                {
                    string typeAttr = AttrVal(inputM.Value, "type");
                    string type = string.IsNullOrEmpty(typeAttr) ? "text" : typeAttr.ToLowerInvariant();

                    if (type == "text" || type == "search" || type == "email" || type == "url")
                    {
                        string name = AttrVal(inputM.Value, "name") ?? "q";
                        string ph   = AttrVal(inputM.Value, "placeholder");

                        if (string.IsNullOrEmpty(ph))
                        {
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

                        ph = (ph ?? "Search...").Replace("\"", "'");
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

            html = RxDel(html, @"<button[^>]*>[\s\S]*?</button>");
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

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

            m = Rx(t, @"<(?:header|nav|footer)[^>]*>([\s\S]*?)</(?:header|nav|footer)>");
            if (m.Success)
            {
                string inner = m.Groups[1].Value;
                ExtractSearchBars(inner, baseUrl, out_);
                ExtractLinks(inner, baseUrl, out_);
                return;
            }

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

            m = Rx(t, @"<video[^>]*src\s*=\s*[""']([^""']+)[""'][^>]*/>");
            if (m.Success)
            {
                out_.Add(new ContentElement { Type = ContentElement.ElementType.Video, Url = ResolveUrl(m.Groups[1].Value, baseUrl) });
                return;
            }

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

            m = Rx(t, @"<figure[^>]*>([\s\S]*?)</figure>");
            if (m.Success) { ParseBlock(m.Groups[1].Value, baseUrl, out_); return; }

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

            m = Rx(t, @"<li[^>]*>([\s\S]*?)</li>");
            if (m.Success)
            {
                string txt = ToRichText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.ListItem, Text = txt });
                return;
            }

            m = Rx(t, @"<blockquote[^>]*>([\s\S]*?)</blockquote>");
            if (m.Success)
            {
                string txt = ToRichText(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Blockquote, Text = txt });
                return;
            }

            m = Rx(t, @"<pre[^>]*>([\s\S]*?)</pre>");
            if (m.Success)
            {
                string txt = StripTags(m.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(txt))
                    out_.Add(new ContentElement { Type = ContentElement.ElementType.Code, Text = txt });
                return;
            }

            m = Rx(t, @"<p[^>]*>([\s\S]*?)</p>");
            if (m.Success)
            {
                ExtractLinks(m.Groups[1].Value, baseUrl, out_,
                    defaultType: ContentElement.ElementType.Paragraph);
                return;
            }

            m = Rx(t, @"<(?:td|th|caption|figcaption|dt|dd)[^>]*>([\s\S]*?)</(?:td|th|caption|figcaption|dt|dd)>");
            if (m.Success)
            {
                ExtractLinks(m.Groups[1].Value, baseUrl, out_);
                return;
            }

            m = Rx(t, @"<img[^>]*>");
            if (m.Success)
            {
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

            if (Regex.IsMatch(t, @"<hr\s*/?>", RegexOptions.IgnoreCase))
            {
                out_.Add(new ContentElement { Type = ContentElement.ElementType.Separator });
                return;
            }

            m = Rx(t, @"<motd-search([^>]*)>");
            if (m.Success)
            {
                string tagAttrs = m.Groups[1].Value;
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

            if (last < html.Length)
            {
                string after = ToRichText(html.Substring(last));
                if (!string.IsNullOrWhiteSpace(after))
                    out_.Add(new ContentElement { Type = defaultType, Text = after });
            }
        }

        // ─── Rich Text Conversion ────────────────────────────────────

        public static string ToRichText(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            html = RxDel(html, @"<(?:script|style)[^>]*>[\s\S]*?</(?:script|style)>");
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(?:strong|b)(?:\s[^>]*)?>", "<b>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:strong|b)>", "</b>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(?:em|i)(?:\s[^>]*)?>", "<i>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:em|i)>", "</i>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<u(?:\s[^>]*)?>", "<u>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</u>", "</u>", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(?:s|strike|del)(?:\s[^>]*)?>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(?:s|strike|del)>", "", RegexOptions.IgnoreCase);

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
            html = Regex.Replace(html, @"<a[^>]*>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</a>", "", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", "");
            html = DecodeEntities(html);
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
            // Anchor to a name-boundary (start-of-tag, whitespace) so looking up
            // "src" against <img data-src="..."> doesn't match the data-src.
            var m = Regex.Match(tag,
                @"(?:^|\s|<\w+)" + Regex.Escape(attr) + @"\s*=\s*[""']([^""']+)[""']",
                RegexOptions.IgnoreCase);
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
