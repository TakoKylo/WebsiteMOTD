using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WebsiteMOTD
{
    /// <summary>
    /// Spawns quads in the 3D world that display WebView content.
    /// Two screens are placed behind each goal net, sharing a single WebView2 instance.
    /// Screen A is the primary (owns the webview + audio), Screen B mirrors the texture.
    /// </summary>
    public class MOTDWorldScreen : MonoBehaviour
    {
        // ─── Native DLL imports (same DLL as MOTDWebView) ───────────
        [DllImport("WebView")]
        private static extern IntPtr _CWebViewPlugin_Init(string gameObject, bool transparent, bool zoom, int width, int height, string ua, bool separated);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_AddScriptOnLoad(IntPtr instance, string js);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_Destroy(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SetVisibility(IntPtr instance, bool visibility);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_SetRect(IntPtr instance, int width, int height);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_LoadURL(IntPtr instance, string url);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_Update(IntPtr instance, bool refreshBitmap, int devicePixelRatio);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_BitmapWidth(IntPtr instance);
        [DllImport("WebView")]
        private static extern int _CWebViewPlugin_BitmapHeight(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_Render(IntPtr instance, IntPtr textureBuffer);
        [DllImport("WebView")]
        private static extern ulong _CWebViewPlugin_BitmapGeneration(IntPtr instance);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_IsInitialized(IntPtr instance);
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_EvaluateJS(IntPtr instance, string js);
        [DllImport("WebView")]
        private static extern string _CWebViewPlugin_GetMessage(IntPtr instance);

        // ─── Shared state (single webview for all screens) ──────────
        private static IntPtr _sharedWebView = IntPtr.Zero;
        private static Texture2D _sharedTexture;
        private static byte[] _sharedTextureBuffer;
        private static ulong _sharedLastGen;
        private static bool _bitmapGenSupported = true;
        private static float _lastLoadTime;  // Unity time of last page load (for adaptive refresh)

        private const int TEX_WIDTH = 1280;
        private const int TEX_HEIGHT = 720;

        // Adaptive refresh tiers (seconds since last page load)
        private const float LoadActiveSec = 3.0f;   // full-speed capture after page load
        private const float LoadWindDownSec = 8.0f;  // half-speed for a few more seconds

        // ─── Proximity audio ────────────────────────────────────────
        // WebView2 plays audio directly to the Windows mixer — we can't route it
        // through Unity's audio system without modifying the native plugin. As a
        // close approximation, we sample the listener's distance to each screen
        // every few frames and multiply the global WebView volume by a logarithmic
        // attenuation curve. Two-screen contributions are summed (clamped to 1)
        // so the player hears both when standing between them.
        private const float ProxMinDistance = 5f;
        private const float ProxMaxDistance = 40f;
        private const int   ProxUpdateFrameInterval = 4;     // re-evaluate every N frames
        private const float ProxVolumeEpsilon = 0.02f;       // skip JS update for tiny deltas
        private static float _globalAudioMultiplier = 0.5f;  // from MOTDUI volume slider / mute
        private static float _positionalMultiplier = 1f;     // computed from listener distance
        private static AudioListener _cachedListener;        // resolved lazily, refreshed on miss

        // ─── Per-instance ───────────────────────────────────────────
        private MeshRenderer _renderer;
        private bool _isPrimary;

        private static MOTDWorldScreen _screenA;
        private static MOTDWorldScreen _screenB;

        // ─── Static API ─────────────────────────────────────────────

        public static void SpawnScreens()
        {
            if (_screenA != null) return;

            if (!MOTDWebView.PreloadNativeDLL())
            {
                Plugin.LogError("WorldScreen: WebView.dll not loaded, cannot spawn.");
                return;
            }

            // Force a fresh listener lookup and a fresh positional sample so the first
            // EvaluateJS uses the right multiplier, not whatever was cached from a
            // previous session.
            _cachedListener = null;
            _positionalMultiplier = 1f;

            Vector3 posA, posB;
            Quaternion rotA, rotB;
            FindGoalPositions(out posA, out rotA, out posB, out rotB);

            _screenA = CreateScreen("MOTD_WorldScreen_A", posA, rotA, true);
            _screenB = CreateScreen("MOTD_WorldScreen_B", posB, rotB, false);

            Plugin.Log("World screens spawned at " + posA + " and " + posB);
        }

        /// <summary>
        /// Load a URL on the shared world screen webview. Uses a raw LoadURL call
        /// to match the behavior of the UI webview (which handles YouTube/Twitch fine).
        /// </summary>
        public static void LoadOnAllScreens(string url)
        {
            if (_sharedWebView == IntPtr.Zero) return;
            string embedUrl = MOTDUI.ConvertToEmbedUrl(url);
            _lastLoadTime = Time.unscaledTime;
            Plugin.Log("WorldScreen loading: " + embedUrl);
            _CWebViewPlugin_LoadURL(_sharedWebView, embedUrl);
        }

        public static void DestroyScreens()
        {
            if (_screenA != null) { _screenA.Cleanup(); _screenA = null; }
            if (_screenB != null) { _screenB.Cleanup(); _screenB = null; }
            DestroySharedWebView();
            // The AudioListener may belong to a player/camera that gets destroyed on
            // scene change. Drop the reference so the next SpawnScreens re-resolves.
            _cachedListener = null;
            _positionalMultiplier = 1f;
        }

        // ─── Goal Finding ───────────────────────────────────────────

        private static void FindGoalPositions(out Vector3 posA, out Quaternion rotA,
                                               out Vector3 posB, out Quaternion rotB)
        {
            var goals = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None);
            Vector3? redGoalPos = null;
            Vector3? blueGoalPos = null;

            foreach (var col in goals)
            {
                string name = col.gameObject.name.ToLowerInvariant();
                string parentName = col.transform.parent != null
                    ? col.transform.parent.name.ToLowerInvariant() : "";
                string fullPath = (parentName + "/" + name);

                if (fullPath.Contains("goal") || name.Contains("goal"))
                {
                    Vector3 p = col.transform.position;
                    if (name.Contains("red") || parentName.Contains("red") ||
                        (name.Contains("goal") && !name.Contains("blue") && p.x < 0))
                    {
                        if (redGoalPos == null) redGoalPos = p;
                    }
                    else if (name.Contains("blue") || parentName.Contains("blue") ||
                             (name.Contains("goal") && p.x > 0))
                    {
                        if (blueGoalPos == null) blueGoalPos = p;
                    }
                }
            }

            if (redGoalPos.HasValue && blueGoalPos.HasValue)
            {
                Vector3 rg = redGoalPos.Value;
                Vector3 bg = blueGoalPos.Value;

                Vector3 center = (rg + bg) * 0.5f;
                Vector3 dirA = (rg - center).normalized;
                Vector3 dirB = (bg - center).normalized;

                posA = rg + dirA * 6f + Vector3.up * 5f;
                posB = bg + dirB * 6f + Vector3.up * 5f;

                rotA = Quaternion.LookRotation(-dirA, Vector3.up);
                rotB = Quaternion.LookRotation(-dirB, Vector3.up);
            }
            else
            {
                Plugin.Log("WorldScreen: Could not find goal objects, using fallback positions.");
                posA = new Vector3(0f, 6f, -35f);
                rotA = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                posB = new Vector3(0f, 6f, 35f);
                rotB = Quaternion.LookRotation(Vector3.back, Vector3.up);
            }
        }

        // ─── Instance Creation ──────────────────────────────────────

        private static MOTDWorldScreen CreateScreen(string name, Vector3 position, Quaternion rotation, bool isPrimary)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.transform.position = position;
            go.transform.rotation = rotation;

            var mf = go.AddComponent<MeshFilter>();
            mf.mesh = CreateQuadMesh(10f, 5.625f);

            var mr = go.AddComponent<MeshRenderer>();
            Shader shader = Shader.Find("Unlit/Texture")
                         ?? Shader.Find("UI/Default")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = Color.white;
            mr.material = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            var screen = go.AddComponent<MOTDWorldScreen>();
            screen._renderer = mr;
            screen._isPrimary = isPrimary;

            if (isPrimary)
            {
                // No AudioSource: WebView2 audio plays through the Windows mixer, not
                // through Unity's audio graph, so a positional AudioSource here would
                // produce no sound. Proximity feel is achieved by sampling the
                // listener distance per frame and scaling the WebView's JS volume —
                // see UpdatePositionalVolume / ApplyVolume.
                InitSharedWebView(go.name);
            }

            return screen;
        }

        private static Mesh CreateQuadMesh(float width, float height)
        {
            float hw = width * 0.5f;
            float hh = height * 0.5f;

            var mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-hw, -hh, 0f),
                new Vector3( hw, -hh, 0f),
                new Vector3( hw,  hh, 0f),
                new Vector3(-hw,  hh, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            return mesh;
        }

        // ─── Shared WebView ─────────────────────────────────────────

        private static void InitSharedWebView(string goName)
        {
            if (_sharedWebView != IntPtr.Zero) return;

            MOTDWebView.EnsureStaticInit();

            _sharedWebView = _CWebViewPlugin_Init(
                goName, true, false,
                TEX_WIDTH, TEX_HEIGHT, "", false);

            if (_sharedWebView == IntPtr.Zero)
            {
                Plugin.LogError("WorldScreen: shared webview init failed.");
                return;
            }

            _CWebViewPlugin_SetVisibility(_sharedWebView, true);
            _CWebViewPlugin_SetRect(_sharedWebView, TEX_WIDTH, TEX_HEIGHT);

            // Volume hook runs before any page script, so freshly-created media
            // elements can't sneak through at the default 1.0 volume. Tolerate older
            // WebView.dll builds without this export — the spawn flow used to crash
            // here with EntryPointNotFoundException and no screens would appear.
            try
            {
                _CWebViewPlugin_AddScriptOnLoad(_sharedWebView, MOTDUI.PersistentVolumeHookJS);
            }
            catch (EntryPointNotFoundException)
            {
                Plugin.LogError("WorldScreen: WebView.dll is older than C# mod — _CWebViewPlugin_AddScriptOnLoad missing. Loudness bursts on page-load won't be prevented; rebuild the native plugin to fix.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("WorldScreen: AddScriptOnLoad threw: " + ex.Message);
            }
        }

        private static void DestroySharedWebView()
        {
            if (_sharedWebView != IntPtr.Zero)
            {
                var ptr = _sharedWebView;
                _sharedWebView = IntPtr.Zero;
                _CWebViewPlugin_Destroy(ptr);
            }

            if (_sharedTexture != null)
            {
                Destroy(_sharedTexture);
                _sharedTexture = null;
            }

            _sharedTextureBuffer = null;
            _sharedLastGen = 0;
        }

        // ─── Per-frame update ───────────────────────────────────────

        void Update()
        {
            // Only the primary screen drives the shared webview
            if (_isPrimary && _sharedWebView != IntPtr.Zero)
            {
                // Distance-based volume — sampled on a throttle so we don't
                // bombard the WebView with EvaluateJS calls every frame.
                if (Time.frameCount % ProxUpdateFrameInterval == 0)
                    UpdatePositionalVolume();


                // Pump message queue (always, even when not refreshing bitmap)
                for (;;)
                {
                    string msg = _CWebViewPlugin_GetMessage(_sharedWebView);
                    if (msg == null) break;
                    if (msg.StartsWith("CallOnLoaded:"))
                    {
                        _lastLoadTime = Time.unscaledTime;
                        InjectWorldAdBlockJS();
                        InjectWorldVolumeJS();
                        InjectWorldVideoEndedJS();
                    }
                    else if (msg.StartsWith("CallFromJS:"))
                    {
                        if (msg.Substring(11) == "videoEnded")
                            Plugin.VideoEnded();
                    }
                }

                // Adaptive refresh: fast after page load, then wind down to idle
                float idleSec = Time.unscaledTime - _lastLoadTime;
                bool shouldRefresh;
                if (!_bitmapGenSupported)
                    shouldRefresh = (Time.frameCount % 4 == 0);        // ~15fps without change-detection
                else if (idleSec < LoadActiveSec)
                    shouldRefresh = true;                               // full speed after load
                else if (idleSec < LoadWindDownSec)
                    shouldRefresh = (Time.frameCount % 2 == 0);        // ~30fps
                else
                    shouldRefresh = (Time.frameCount % 8 == 0);        // ~7.5fps idle

                _CWebViewPlugin_Update(_sharedWebView, shouldRefresh, 1);

                if (shouldRefresh)
                {
                    // Check if native bitmap actually changed since last upload
                    bool bitmapChanged = true;
                    if (_bitmapGenSupported)
                    {
                        try
                        {
                            ulong gen = _CWebViewPlugin_BitmapGeneration(_sharedWebView);
                            bitmapChanged = (gen != _sharedLastGen);
                            if (bitmapChanged) _sharedLastGen = gen;
                        }
                        catch (System.EntryPointNotFoundException)
                        {
                            _bitmapGenSupported = false;
                            Plugin.LogError("WorldScreen: _CWebViewPlugin_BitmapGeneration not found — falling back to unconditional refresh.");
                        }
                    }

                    if (bitmapChanged)
                    {
                        int w = _CWebViewPlugin_BitmapWidth(_sharedWebView);
                        int h = _CWebViewPlugin_BitmapHeight(_sharedWebView);
                        if (w > 0 && h > 0)
                        {
                            if (_sharedTexture == null || _sharedTexture.width != w || _sharedTexture.height != h)
                            {
                                if (_sharedTexture != null) Destroy(_sharedTexture);
                                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                                _sharedTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, !linear);
                                _sharedTexture.filterMode = FilterMode.Bilinear;
                                _sharedTexture.wrapMode = TextureWrapMode.Clamp;
                                _sharedTextureBuffer = new byte[w * h * 4];
                            }

                            if (_sharedTextureBuffer != null)
                            {
                                var gch = GCHandle.Alloc(_sharedTextureBuffer, GCHandleType.Pinned);
                                _CWebViewPlugin_Render(_sharedWebView, gch.AddrOfPinnedObject());
                                gch.Free();

                                _sharedTexture.LoadRawTextureData(_sharedTextureBuffer);
                                _sharedTexture.Apply(false);
                            }
                        }
                    }
                }
            }
            // All screens apply the shared texture to their material
            if (_sharedTexture != null && _renderer != null && _renderer.material != null
                && _renderer.material.mainTexture != _sharedTexture)
            {
                _renderer.material.mainTexture = _sharedTexture;
            }
        }

        // ─── Cleanup ────────────────────────────────────────────────

        public void Cleanup()
        {
            Destroy(gameObject);
        }

        void OnDestroy()
        {
            if (_isPrimary)
                DestroySharedWebView();
        }

        // ─── Client-side Controls ───────────────────────────────

        public static void SetScreensVisible(bool visible)
        {
            if (_screenA != null && _screenA._renderer != null)
                _screenA._renderer.enabled = visible;
            if (_screenB != null && _screenB._renderer != null)
                _screenB._renderer.enabled = visible;
        }

        /// <summary>
        /// Update the global (slider/mute) audio multiplier and re-apply.
        /// Positional attenuation is layered on top in <see cref="ApplyVolume"/>.
        /// </summary>
        public static void SetVolume(float volume)
        {
            _globalAudioMultiplier = Mathf.Clamp01(volume);
            ApplyVolume();
        }

        /// <summary>
        /// Push the current effective volume (global × positional) to the WebView's
        /// media elements via JS. Idempotent — safe to call from both the slider and
        /// the per-frame distance loop. JS includes prototype/play hooks that prevent
        /// the loudness bursts you'd otherwise hear when a new video starts.
        /// </summary>
        private static void ApplyVolume()
        {
            if (_sharedWebView == IntPtr.Zero) return;
            float v = Mathf.Clamp01(_globalAudioMultiplier * _positionalMultiplier);
            _CWebViewPlugin_EvaluateJS(_sharedWebView, MOTDUI.BuildVolumeJS(v));
        }

        /// <summary>
        /// Logarithmic distance attenuation matching Unity's stock log rolloff —
        /// 1.0 within <see cref="ProxMinDistance"/>, falling to 0 at
        /// <see cref="ProxMaxDistance"/>. Returns 0 past max so out-of-range
        /// screens don't keep contributing.
        /// </summary>
        private static float LogAttenuate(float distance)
        {
            if (distance <= ProxMinDistance) return 1f;
            if (distance >= ProxMaxDistance) return 0f;
            return Mathf.Log(ProxMaxDistance / distance) /
                   Mathf.Log(ProxMaxDistance / ProxMinDistance);
        }

        private static Transform GetListenerTransform()
        {
            // Puck spawns multiple BaseCamera instances (player, main menu, etc.), each
            // with its own AudioListener. Only one is enabled at a time — that's the
            // listener actually routing audio. FindFirstObjectByType returns whichever
            // Unity walks into first, which is usually the static main-menu listener
            // and never moves. We need to filter to enabled ones, and re-resolve when
            // the cached listener gets disabled (scene transition, camera swap).
            if (_cachedListener == null || !_cachedListener.isActiveAndEnabled)
            {
                _cachedListener = null;
                var all = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);
                foreach (var l in all)
                {
                    if (l != null && l.isActiveAndEnabled)
                    {
                        _cachedListener = l;
                        break;
                    }
                }
                // Last-ditch fallback: any listener (even disabled) is better than nothing.
                if (_cachedListener == null && all.Length > 0)
                    _cachedListener = all[0];
            }
            if (_cachedListener != null) return _cachedListener.transform;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        /// <summary>
        /// Sample listener position and update <see cref="_positionalMultiplier"/>.
        /// Both screens contribute and are summed (clamped to 1.0) so standing between
        /// the goals gives full volume from both speakers.
        /// </summary>
        private static void UpdatePositionalVolume()
        {
            if (_sharedWebView == IntPtr.Zero) return;
            if (_screenA == null && _screenB == null) return;

            var listener = GetListenerTransform();
            if (listener == null) return;
            Vector3 lp = listener.position;

            float attenA = _screenA != null
                ? LogAttenuate(Vector3.Distance(lp, _screenA.transform.position)) : 0f;
            float attenB = _screenB != null
                ? LogAttenuate(Vector3.Distance(lp, _screenB.transform.position)) : 0f;

            float combined = Mathf.Clamp01(attenA + attenB);
            if (Mathf.Abs(combined - _positionalMultiplier) < ProxVolumeEpsilon) return;
            _positionalMultiplier = combined;
            ApplyVolume();
        }

        private static void InjectWorldAdBlockJS()
        {
            if (_sharedWebView == IntPtr.Zero) return;
            _CWebViewPlugin_EvaluateJS(_sharedWebView, MOTDUI.AdBlockJS);
        }

        private static void InjectWorldVolumeJS()
        {
            SetVolume(MOTDUI.EffectiveVolume);
        }

        private static void InjectWorldVideoEndedJS()
        {
            if (_sharedWebView == IntPtr.Zero) return;
            _CWebViewPlugin_EvaluateJS(_sharedWebView,
                "(function(){" +
                "if(window.__motdMedia)return;" +
                "window.__motdMedia=true;" +
                // YouTube CSS maximise (world screens show same URLs)
                "if(location.hostname.indexOf('youtube.com')>=0){" +
                "var s=document.createElement('style');" +
                "s.textContent=" +
                "'html,body{overflow:hidden!important;background:#000!important}'" +
                "+'#masthead-container,tp-yt-app-header-layout,ytd-mini-guide-renderer{display:none!important}'" +
                "+'ytd-watch-flexy #secondary,#secondary{display:none!important}'" +
                "+'ytd-watch-metadata,#below-the-fold,ytd-comments,ytd-item-section-renderer{display:none!important}'" +
                "+'ytd-watch-flexy[is-two-columns_] #primary{max-width:100%!important}'" +
                "+'#ytd-player,#movie_player{position:fixed!important;top:0!important;left:0!important;width:100vw!important;height:100vh!important;max-height:none!important;z-index:9999!important}';" +
                "(document.head||document.documentElement).appendChild(s);" +
                "}" +
                // Event-listener based ended detection — fires before YouTube changes src
                "var _sent=false;" +
                "function notifyEnded(){" +
                "if(window.Unity&&typeof window.Unity.call==='function')" +
                "window.Unity.call('videoEnded');" +
                "}" +
                "function onEnded(e){" +
                "var v=e.target;" +
                "if(!v||v.duration<=5)return;" +
                "if(document.querySelector('.ad-showing'))return;" +
                "if(_sent)return;" +
                "_sent=true;" +
                "setTimeout(notifyEnded,1500);" +
                "}" +
                "function attach(v){" +
                "if(!v||v.__motdBound)return;" +
                "v.__motdBound=true;" +
                "v.addEventListener('ended',onEnded);" +
                "}" +
                "document.querySelectorAll('video').forEach(attach);" +
                "new MutationObserver(function(){" +
                "document.querySelectorAll('video').forEach(attach);" +
                "}).observe(document.body||document.documentElement,{childList:true,subtree:true});" +
                "})();"
            );
        }

    }
}
