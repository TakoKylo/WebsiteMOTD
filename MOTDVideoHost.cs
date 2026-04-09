using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using UnityEngine.Video;

namespace WebsiteMOTD
{
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

        // Callbacks set after Create() by ConnectControls()
        private Action<float>  _progressSetter; // value 0-1, no seek
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

        // ─── Download → Play ─────────────────────────────────────────

        private IEnumerator DownloadThenPlay(string url, string referer)
        {
            SetStatus("Downloading video...");

            string ext = ".mp4";
            string clean = url.Split('?')[0];
            int dot = clean.LastIndexOf('.');
            if (dot >= 0 && clean.Length - dot <= 5) ext = clean.Substring(dot);

            _tempFile = Path.Combine(Application.temporaryCachePath,
                "motd_" + Mathf.Abs(url.GetHashCode()) + ext);

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
}
