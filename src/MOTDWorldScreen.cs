using System;
using System.Globalization;
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

        // 1080p texture for the in-world screens. 720p was visibly soft up close
        // (the quads are 10×5.625 world units — players can stand 2-3m away).
        private const int TEX_WIDTH = 1920;
        private const int TEX_HEIGHT = 1080;

        // Refresh strategy: stay at full speed whenever the bitmap is actually
        // changing (video playback, scrolling, animations) OR a page just loaded.
        // Only throttle when content has been static for a while. This keeps video
        // playback smooth indefinitely instead of dropping to 7.5fps after 8s.
        private const float LoadActiveSec = 3.0f;        // grace window after load
        private const int   StaticChecksForIdle = 30;    // ~0.5s @ 60fps of no change → throttle
        private const int   IdleFrameInterval = 8;       // ~7.5fps when truly idle
        private const int   NoBitmapGenFrameInterval = 4; // fallback when DLL lacks BitmapGeneration
        // Cap active-mode bitmap captures. The BitmapGeneration check gates
        // texture uploads, so the cap controls only how often we ASK for a
        // fresh frame. 60Hz was visibly judder-y on 120/144Hz monitors during
        // video playback: a 60Hz sample of 60Hz content drops frames whenever
        // sample alignment slips. 120Hz keeps the screens smooth on common
        // high-refresh setups while still leaving headroom on the STA thread
        // (each capture cycle gets ~8ms before the next request).
        private const float MinCaptureIntervalSec = 1f / 120f;
        private static int  _consecutiveStaticChecks;
        private static float _lastCaptureUT;

        // ─── Proximity audio ────────────────────────────────────────
        // WebView2 plays audio directly to the Windows mixer — we can't route it
        // through Unity's audio system without modifying the native plugin. As a
        // close approximation, we sample the listener's distance to each screen
        // every few frames and multiply the global WebView volume by a logarithmic
        // attenuation curve. Two-screen contributions are summed (clamped to 1)
        // so the player hears both when standing between them.
        private const float ProxMinDistance = 5f;
        private const float ProxMaxDistance = 60f;
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

        // ─── OpenWorld "TheatreVideoScreen" claim ───────────────────
        // OpenWorldPracticeMod ships a screen prefab and exposes a TryClaim/Release
        // API plus a ClaimChanged event for cooperative ownership — see
        // TheatreVideoScreenBridge. While we hold the claim, the OWP mod stops its
        // showcase video and lets us drive the screen's material. We bind our shared
        // WebView texture across _MainTex / _BaseMap / _EmissionMap so it lights up
        // regardless of shader pipeline (URP-Lit, Unlit, Standard, etc).
        //
        // Re-attach handling is event-driven: OWP destroys + recreates the screen
        // GameObject across open-world teardown/re-entry, so we subscribe to
        // ClaimChanged and re-bind to the fresh GameObject when it fires.
        private static GameObject _theatreScreen;
        private static Renderer _theatreRenderer;

        // Driver-only screen: spawned when no A/B screens exist but the theatre
        // screen is claimed, so the WebView still gets pumped. No MeshRenderer —
        // it exists purely to drive Update() on the shared webview.
        private static MOTDWorldScreen _driver;

        /// <summary>True if we currently hold the OpenWorld theatre screen claim.</summary>
        public static bool HasTheatreScreen => _theatreScreen != null && _theatreRenderer != null;

        // ─── Static API ─────────────────────────────────────────────

        /// <summary>
        /// Try to claim the OpenWorld theatre screen if it's available and we don't
        /// already hold it. Subscribes to the ClaimChanged event on first call so
        /// later teardown/re-attach events automatically re-bind the texture without
        /// per-frame polling. Safe and cheap to call repeatedly.
        /// </summary>
        public static GameObject EnsureTheatreClaim()
        {
            if (!TheatreVideoScreenBridge.ApiPresent) return null;

            // Subscribe first so we don't miss a ClaimChanged that fires between
            // TryClaim and the event hookup. The bridge's subscribe is idempotent
            // when called with the same handler.
            TheatreVideoScreenBridge.SubscribeClaimChanged(OnTheatreClaimChanged);

            // Drop stale cache if the screen prefab vanished (e.g. open-world torn
            // down). The ClaimChanged event will re-notify us when it returns.
            if (!TheatreVideoScreenBridge.IsAvailable)
            {
                if (_theatreScreen != null)
                {
                    _theatreScreen = null;
                    _theatreRenderer = null;
                }
                return null;
            }

            // Already claimed by us — nothing to do.
            if (_theatreScreen != null && TheatreVideoScreenBridge.IsClaimedByUs)
                return _theatreScreen;

            // Either we never claimed, or another mod displaced us — try (re)claim.
            GameObject go = TheatreVideoScreenBridge.TryClaim();
            if (go == null)
            {
                _theatreScreen = null;
                _theatreRenderer = null;
                return null;
            }

            BindToTheatreScreen(go);
            Plugin.Log("TheatreVideoScreen claimed — driving with WebView texture.");
            return go;
        }

        /// <summary>
        /// Cache the screen GameObject + resolve its renderer. Centralised so the
        /// event-driven re-attach path and the initial-claim path stay in sync.
        /// </summary>
        private static void BindToTheatreScreen(GameObject go)
        {
            _theatreScreen = go;
            _theatreRenderer = TheatreVideoScreenBridge.GetScreenRenderer()
                ?? (go != null ? (go.GetComponent<Renderer>() ?? go.GetComponentInChildren<Renderer>()) : null);

            if (go != null && _theatreRenderer == null)
                Plugin.LogError("TheatreVideoScreen claimed but no Renderer found on the screen GameObject.");
        }

        /// <summary>
        /// ClaimChanged callback from the OWP bridge. Three cases:
        ///   • newOwner == our id: we (still) own it — could be a fresh claim ACK or
        ///     a re-attach after open-world teardown. Re-bind to the (possibly new) GO.
        ///   • newOwner == null: screen released to default. If queue content is live,
        ///     try to retake; otherwise stay out.
        ///   • newOwner == something else: another mod claimed it. Drop our refs so we
        ///     don't stomp their content.
        /// </summary>
        private static void OnTheatreClaimChanged(string newOwner, GameObject screen)
        {
            if (newOwner == TheatreVideoScreenBridge.OwnerId)
            {
                BindToTheatreScreen(screen);
                Plugin.Log("TheatreVideoScreen re-attached (we still own it) — re-bound to new GameObject.");
            }
            else if (string.IsNullOrEmpty(newOwner))
            {
                // Screen available & unclaimed. Retake if we're actively driving content.
                _theatreScreen = null;
                _theatreRenderer = null;
                if (_sharedWebView != IntPtr.Zero)
                    EnsureTheatreClaim();
            }
            else
            {
                Plugin.Log("TheatreVideoScreen taken by '" + newOwner + "' — releasing local refs.");
                _theatreScreen = null;
                _theatreRenderer = null;
            }
        }

        /// <summary>Release the theatre claim so OpenWorld can resume its own video.</summary>
        public static void ReleaseTheatreClaim()
        {
            // Unsubscribe BEFORE Release so the bridge doesn't bounce our own
            // release back to OnTheatreClaimChanged and immediately re-claim.
            TheatreVideoScreenBridge.UnsubscribeClaimChanged();

            if (_theatreScreen == null && !TheatreVideoScreenBridge.IsClaimedByUs) return;
            _theatreScreen = null;
            _theatreRenderer = null;
            TheatreVideoScreenBridge.Release();
        }

        /// <summary>
        /// Bind <paramref name="tex"/> to the standard texture slots on
        /// <paramref name="mat"/>. Sets all of _MainTex/_BaseMap/_EmissionMap that
        /// exist so the texture shows up regardless of shader pipeline (URP-Lit
        /// uses _BaseMap, built-in uses _MainTex, emissive shaders use _EmissionMap).
        ///
        /// Also applies a vertical flip via the texture transform: WebView2's bitmap
        /// is top-down (row 0 = top) but Unity texture sampling assumes bottom-up
        /// (row 0 = bottom). Our own world quads compensate via flipped mesh UVs
        /// (see <see cref="CreateQuadMesh"/>), but we can't modify the OWP screen's
        /// mesh — scale (1,-1) + offset (0,1) on the material does the same thing
        /// per-shader-property without touching the renderer's geometry.
        /// </summary>
        private static readonly Vector2 FlipScale = new Vector2(1f, -1f);
        private static readonly Vector2 FlipOffset = new Vector2(0f, 1f);

        private static void BindTextureToScreenMaterial(Material mat, Texture tex)
        {
            if (mat == null || tex == null) return;
            bool boundAny = false;
            boundAny |= BindSlot(mat, "_MainTex",     tex);
            boundAny |= BindSlot(mat, "_BaseMap",     tex);
            boundAny |= BindSlot(mat, "_EmissionMap", tex);

            // Fallback for shaders that name their main texture something else
            // (e.g. a custom theater-screen shader using "_Texture" or "_Albedo").
            // Material.mainTexture writes to whichever property the shader marks
            // as [MainTexture] (or the default _MainTex). Same for the scale/offset.
            if (!boundAny)
            {
                try
                {
                    if (mat.mainTexture != tex) mat.mainTexture = tex;
                    mat.mainTextureScale = FlipScale;
                    mat.mainTextureOffset = FlipOffset;
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Theatre material mainTexture fallback failed: " + ex.Message);
                }
            }
        }

        private static bool BindSlot(Material mat, string prop, Texture tex)
        {
            if (!mat.HasProperty(prop)) return false;
            if (mat.GetTexture(prop) != tex)
                mat.SetTexture(prop, tex);
            // Set every frame — cheap matrix update, defends against the OWP mod
            // resetting material state (e.g., after a teardown/re-attach we may
            // get a fresh material with default 1,0 / 0,0 transform).
            mat.SetTextureScale(prop, FlipScale);
            mat.SetTextureOffset(prop, FlipOffset);
            return true;
        }

        /// <summary>
        /// Spawn an invisible "driver" GameObject that pumps the shared WebView when
        /// the regular A/B screens are disabled by config but external mirrors exist.
        /// No-op if the regular screens are already spawned or a driver already exists.
        /// </summary>
        public static void EnsureDriver()
        {
            if (_screenA != null || _driver != null) return;

            if (!MOTDWebView.PreloadNativeDLL())
            {
                Plugin.LogError("WorldScreen: WebView.dll not loaded, cannot start driver.");
                return;
            }

            _cachedListener = null;
            _positionalMultiplier = 1f;

            var go = new GameObject("MOTD_WorldScreen_Driver");
            go.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);

            _driver = go.AddComponent<MOTDWorldScreen>();
            _driver._renderer = null;
            _driver._isPrimary = true;

            InitSharedWebView(go.name);
            Plugin.Log("WorldScreen driver started (regular screens off, theater mirrors active).");
        }

        public static void SpawnScreens()
        {
            if (_screenA != null) return;

            if (!MOTDWebView.PreloadNativeDLL())
            {
                Plugin.LogError("WorldScreen: WebView.dll not loaded, cannot spawn.");
                return;
            }

            // If a driver was already running (theater-only mode), hand off cleanly:
            // the new screenA takes over the primary role and the existing shared
            // WebView is reused. We null out _driver before Destroy so its OnDestroy
            // doesn't tear down the WebView we're about to keep using.
            if (_driver != null)
            {
                var oldDriver = _driver;
                _driver = null;
                oldDriver._isPrimary = false;
                Destroy(oldDriver.gameObject);
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

            // Apply the user's "screens off" preference immediately. Without this
            // the screens are visible on every spawn until the player opens the
            // overlay at least once (because the screens-disabled flag was only
            // applied from MOTDUI.Show). Volume is handled via InjectWorldVolumeJS
            // on CallOnLoaded, which reads MOTDUI.EffectiveVolume — already loaded
            // eagerly by Plugin.Setup, so no extra wiring needed here.
            if (MOTDUI.ScreensDisabled)
                SetScreensVisible(false);

            Plugin.Log("World screens spawned at " + posA + " and " + posB);
        }

        /// <summary>
        /// Load a URL on the shared world screen webview. Uses a raw LoadURL call
        /// to match the behavior of the UI webview (which handles YouTube/Twitch fine).
        ///
        /// <paramref name="itemId"/> is the queue item id this load represents — it
        /// gets injected into the page as window.__motdItemId so the videoEnded JS
        /// hook can bind the event to a specific item, letting the server drop
        /// stale messages from videos that have already been skipped past.
        ///
        /// <paramref name="startOffsetSec"/> seeks into the video by appending a
        /// platform-appropriate time param (YouTube &amp;t=, Twitch VOD &amp;time=,
        /// direct media #t=). Used by the mid-join sync path so a player joining
        /// 30 s into a video doesn't restart it from 0:00. 0 means start at the
        /// beginning.
        /// </summary>
        public static void LoadOnAllScreens(string url, long itemId = 0, int startOffsetSec = 0)
        {
            if (_sharedWebView == IntPtr.Zero) return;
            string embedUrl = MOTDUI.ConvertToEmbedUrl(url, startOffsetSec);
            _lastLoadTime = Time.unscaledTime;
            _pendingItemId = itemId;
            Plugin.Log("WorldScreen loading (id=" + itemId + ", t=" + startOffsetSec + "s): " + embedUrl);
            _CWebViewPlugin_LoadURL(_sharedWebView, embedUrl);
        }

        // Item id captured at LoadOnAllScreens time. Injected as __motdItemId at
        // CallOnLoaded. Used by the videoEnded JS hook to tag its "ended" message
        // so the server can validate it against _current.Id before advancing.
        private static long _pendingItemId;

        // ─── Drift correction ───────────────────────────────────────
        // Server broadcasts a periodic "tick:<id>:<elapsedSec>" with the current
        // item's authoritative elapsed time. On receipt we ask the WebView for
        // its actual video.currentTime via EvaluateJS (the answer comes back on
        // the message queue as "videoTime:<id>:<currentTime>:<playbackRate>"),
        // then seek forward if the local position has fallen behind the server
        // by more than DriftThresholdSec. We only ever seek FORWARD — if the
        // local playback is ahead of the server (rare; usually means we got
        // lucky with buffering while the server's _currentStartedAtUT was
        // pessimistic), seeking backward would be more jarring than the drift.
        private const float DriftThresholdSec = 5f;
        private const double DriftSeekCooldownSec = 30.0;
        private static long   _lastTickItemId;
        private static int    _lastTickElapsedSec;
        private static double _lastTickRxUT;
        private static double _lastDriftSeekUT;

        /// <summary>
        /// Server heartbeat receiver: kick a drift check for the world-screen
        /// WebView. The actual position read is async (EvaluateJS injects a
        /// callback that lands on the message queue in <see cref="Update"/>),
        /// so this method only records the latest server-reported position and
        /// fires the query — the comparison happens in the message-loop branch
        /// for "videoTime:".
        ///
        /// No-op when the WebView hasn't loaded the item the tick references —
        /// the load is still in flight or we've already advanced past this item
        /// locally (rare but possible if a state message lapped the tick).
        /// </summary>
        public static void HandleServerTick(long itemId, int elapsedSec)
        {
            if (_sharedWebView == IntPtr.Zero) return;
            if (_pendingItemId != itemId) return;

            _lastTickItemId = itemId;
            _lastTickElapsedSec = elapsedSec;
            _lastTickRxUT = Time.unscaledTimeAsDouble;

            // Read currentTime and playbackRate; ad-fast-forward (rate>3) is
            // checked downstream so we don't seek mid-ad. Limit the lookup to
            // the main player container — hover-preview <video>s on YouTube's
            // sidebar would otherwise dominate the picker.
            _CWebViewPlugin_EvaluateJS(_sharedWebView,
                "(function(){" +
                "var mp=document.querySelector('#movie_player,.html5-video-player');" +
                "var v=mp?mp.querySelector('video'):document.querySelector('video');" +
                "if(!v||!isFinite(v.duration))return;" +
                "if(window.Unity&&typeof window.Unity.call==='function')" +
                "window.Unity.call('videoTime:" + itemId + ":'+v.currentTime+':'+v.playbackRate);" +
                "})();");
        }

        /// <summary>
        /// Apply drift correction once the WebView has reported its actual
        /// playback position. Only seeks FORWARD when local lags server beyond
        /// the threshold, throttled by <see cref="DriftSeekCooldownSec"/>.
        /// </summary>
        private static void TryCorrectDrift(long itemId, float localTime, float playbackRate)
        {
            if (_sharedWebView == IntPtr.Zero) return;
            if (itemId != _lastTickItemId) return;
            // AdBlock JS sets rate=16 to fast-forward ads — that's the signal
            // we're not in the main video yet; don't seek into a future of an
            // ad-stream that isn't ours.
            if (playbackRate > 3f) return;

            double serverNow = _lastTickElapsedSec + (Time.unscaledTimeAsDouble - _lastTickRxUT);
            double drift = serverNow - localTime; // positive = local is behind
            if (drift < DriftThresholdSec) return;
            if (Time.unscaledTimeAsDouble - _lastDriftSeekUT < DriftSeekCooldownSec) return;

            _lastDriftSeekUT = Time.unscaledTimeAsDouble;
            // Tiny forward bias to account for the round-trip of the seek itself.
            double targetSec = serverNow + 0.5;
            Plugin.Log("Drift correction (item " + itemId + "): local=" + localTime.ToString("0.0")
                + "s server=" + serverNow.ToString("0.0") + "s → seek " + targetSec.ToString("0.0") + "s");
            _CWebViewPlugin_EvaluateJS(_sharedWebView,
                "(function(){" +
                "var mp=document.querySelector('#movie_player,.html5-video-player');" +
                "var v=mp?mp.querySelector('video'):document.querySelector('video');" +
                "if(v&&isFinite(v.duration)){try{v.currentTime=" + targetSec.ToString("0.00", CultureInfo.InvariantCulture) + ";}catch(e){}}" +
                "})();");
        }

        public static void DestroyScreens()
        {
            // Release the OpenWorld claim BEFORE destroying the webview/screens
            // so the OpenWorld mod can swap back to its showcase video while its
            // GameObject is still alive.
            ReleaseTheatreClaim();

            if (_screenA != null) { _screenA.Cleanup(); _screenA = null; }
            if (_screenB != null) { _screenB.Cleanup(); _screenB = null; }
            if (_driver != null) { _driver.Cleanup(); _driver = null; }
            DestroySharedWebView();
            // The AudioListener may belong to a player/camera that gets destroyed on
            // scene change. Drop the reference so the next SpawnScreens re-resolves.
            _cachedListener = null;
            _positionalMultiplier = 1f;

            // Drop drift / load tracking state too. Without this, a plugin
            // disable/enable cycle can leave _lastTickItemId pointing at an id
            // from the previous session. Server-side _nextItemId restarts at 0
            // on Teardown, so a future item could collide with the stale id and
            // trigger a phantom drift seek immediately on load.
            _pendingItemId       = 0;
            _lastTickItemId      = 0;
            _lastTickElapsedSec  = 0;
            _lastTickRxUT        = 0d;
            _lastDriftSeekUT     = 0d;
            _lastLoadTime        = 0f;
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

            // Load bundled extensions (e.g. uBlock Origin) into the shared
            // profile. Profile-bound + once-per-process, so this is a no-op
            // when the overlay WebView already triggered the load.
            MOTDWebView.TryLoadBundledExtensions(_sharedWebView);
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
            _consecutiveStaticChecks = 0;
        }

        // ─── Per-frame update ───────────────────────────────────────

        // How often to retry the OpenWorld theatre claim when we don't currently
        // hold it. OWP's ClaimChanged event isn't fired on the *initial* theatre
        // spawn (per its contract — only on re-creations), so a player who joins
        // a regular hockey lobby and later enters open-world otherwise never gets
        // notified. Cheap reflective call; one probe per second is free.
        private const int TheatreReclaimFrameInterval = 60;

        void Update()
        {
            // Only the primary screen drives the shared webview
            if (_isPrimary && _sharedWebView != IntPtr.Zero)
            {
                // Distance-based volume — sampled on a throttle so we don't
                // bombard the WebView with EvaluateJS calls every frame.
                if (Time.frameCount % ProxUpdateFrameInterval == 0)
                    UpdatePositionalVolume();

                // Late-spawn theatre catcher: if we don't currently hold the
                // OWP theatre claim (either OWP hasn't spawned it yet, or we
                // missed the ClaimChanged event), retry once a second. Stops
                // automatically as soon as the claim succeeds — the
                // _theatreScreen check skips the work after that.
                if (_theatreScreen == null
                    && TheatreVideoScreenBridge.ApiPresent
                    && Time.frameCount % TheatreReclaimFrameInterval == 0)
                {
                    EnsureTheatreClaim();
                }


                // Pump message queue (always, even when not refreshing bitmap)
                for (;;)
                {
                    string msg = _CWebViewPlugin_GetMessage(_sharedWebView);
                    if (msg == null) break;
                    if (msg.StartsWith("ExtensionLoaded:"))
                    {
                        Plugin.Log("WorldScreen browser extension loaded: " + msg.Substring(16));
                        continue;
                    }
                    if (msg.StartsWith("ExtensionError:"))
                    {
                        Plugin.LogError("WorldScreen browser extension load failed: " + msg.Substring(15)
                            + " (ad-block falling back to JS-only).");
                        continue;
                    }
                    if (msg.StartsWith("CallOnLoaded:"))
                    {
                        _lastLoadTime = Time.unscaledTime;
                        _consecutiveStaticChecks = 0;
                        // Bind page to the queue item id BEFORE the media hook
                        // installs — the hook reads __motdItemId when firing ended.
                        _CWebViewPlugin_EvaluateJS(_sharedWebView,
                            "window.__motdItemId=" + _pendingItemId + ";");
                        InjectWorldAdBlockJS();
                        InjectWorldVolumeJS();
                        InjectWorldVideoEndedJS();
                    }
                    else if (msg.StartsWith("CallFromJS:"))
                    {
                        string body = msg.Substring(11);
                        if (body.StartsWith("videoEnded:"))
                        {
                            if (long.TryParse(body.Substring(11), out long reportedId))
                                Plugin.VideoEnded(reportedId);
                        }
                        else if (body.StartsWith("videoTime:"))
                        {
                            // videoTime:<itemId>:<currentTime>:<playbackRate>
                            // Async response to the HandleServerTick EvaluateJS;
                            // routed into the drift-correction decision.
                            var parts = body.Substring(10).Split(':');
                            if (parts.Length >= 2
                                && long.TryParse(parts[0], out long vId)
                                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float vTime))
                            {
                                float rate = 1f;
                                if (parts.Length >= 3)
                                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
                                TryCorrectDrift(vId, vTime, rate);
                            }
                        }
                    }
                }

                // Adaptive refresh: full-speed during the post-load grace window OR
                // whenever the page is actively producing new bitmaps (video, anim,
                // scrolling). Only throttle once content has been static for a beat.
                float idleSec = Time.unscaledTime - _lastLoadTime;
                bool nearLoad = idleSec < LoadActiveSec;
                bool pageActive = _consecutiveStaticChecks < StaticChecksForIdle;

                bool shouldRefresh;
                if (!_bitmapGenSupported)
                    shouldRefresh = (Time.frameCount % NoBitmapGenFrameInterval == 0);
                else if (nearLoad || pageActive)
                    shouldRefresh = true;
                else
                    shouldRefresh = (Time.frameCount % IdleFrameInterval == 0);

                // Real-time rate cap. Frame-count throttling doesn't help here
                // because the rate it produces varies with Unity's FPS, while
                // WebView2 paints at a fixed cadence (≈ vsync of its own DComp
                // surface). Pin the capture rate so we don't outpace it.
                if (shouldRefresh && Time.unscaledTime - _lastCaptureUT < MinCaptureIntervalSec)
                    shouldRefresh = false;

                _CWebViewPlugin_Update(_sharedWebView, shouldRefresh, 1);
                if (shouldRefresh) _lastCaptureUT = Time.unscaledTime;

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
                            if (bitmapChanged)
                            {
                                _sharedLastGen = gen;
                                _consecutiveStaticChecks = 0; // page is moving — stay fast
                            }
                            else if (_consecutiveStaticChecks < int.MaxValue)
                            {
                                _consecutiveStaticChecks++;   // count toward idle decision
                            }
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

            // Primary also drives the OpenWorld theatre screen: keep the shared
            // texture bound to its material every frame. (Re)claiming is event-
            // driven via TheatreVideoScreenBridge.ClaimChanged — see
            // OnTheatreClaimChanged — so no per-frame polling is needed. Theatre
            // screen runs regardless of server/client screens settings, that's
            // the whole point of the cooperation with OpenWorldPracticeMod.
            if (_isPrimary
                && _sharedTexture != null
                && _theatreRenderer != null
                && _theatreRenderer.material != null)
            {
                BindTextureToScreenMaterial(_theatreRenderer.material, _sharedTexture);
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
        /// Every active screen — the two arena screens AND the OWP theatre screen
        /// when we've claimed it — contributes an attenuation value; the sum is
        /// clamped to 1.0. Standing between any two of them gives full volume from
        /// both "speakers". Treating the theatre screen as just another source
        /// matches the audio feel of the arena screens and lets a player in the
        /// open-world hub hear queue audio without it staying at full volume from
        /// across the map.
        /// </summary>
        private static void UpdatePositionalVolume()
        {
            if (_sharedWebView == IntPtr.Zero) return;
            if (_screenA == null && _screenB == null && _theatreScreen == null) return;

            var listener = GetListenerTransform();
            if (listener == null) return;
            Vector3 lp = listener.position;

            float attenA = _screenA != null
                ? LogAttenuate(Vector3.Distance(lp, _screenA.transform.position)) : 0f;
            float attenB = _screenB != null
                ? LogAttenuate(Vector3.Distance(lp, _screenB.transform.position)) : 0f;
            float attenT = _theatreScreen != null
                ? LogAttenuate(Vector3.Distance(lp, _theatreScreen.transform.position)) : 0f;

            float combined = Mathf.Clamp01(attenA + attenB + attenT);
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
                "'html,body{overflow:hidden!important;background:#000!important;margin:0!important;padding:0!important}'" +
                "+'#masthead-container,tp-yt-app-header-layout,ytd-mini-guide-renderer{display:none!important}'" +
                "+'ytd-watch-flexy #secondary,#secondary{display:none!important}'" +
                "+'ytd-watch-metadata,#below-the-fold,ytd-comments,ytd-item-section-renderer{display:none!important}'" +
                "+'ytd-watch-flexy[is-two-columns_] #primary{max-width:100%!important;margin:0!important;padding:0!important}'" +
                "+'#ytd-player,#movie_player{position:fixed!important;top:0!important;left:0!important;width:100vw!important;height:100vh!important;max-height:none!important;z-index:9999!important}'" +
                // The outer player only sizes the chrome — without forcing the inner
                // container + video element, YouTube's aspect-ratio JS leaves the
                // actual frame letterboxed inside a maximized player box. Force the
                // container to fill, then absolute-position the <video> at 100%/100%
                // with object-fit:contain so the picture itself stretches to the
                // viewport (no inline transform/margin/top from YT's layout pass).
                "+'#movie_player .html5-video-container{position:absolute!important;top:0!important;left:0!important;width:100%!important;height:100%!important;transform:none!important}'" +
                "+'#movie_player video.html5-main-video,#movie_player .video-stream{position:absolute!important;top:0!important;left:0!important;width:100%!important;height:100%!important;max-width:none!important;max-height:none!important;margin:0!important;transform:none!important;object-fit:contain!important}';" +
                "(document.head||document.documentElement).appendChild(s);" +
                // YouTube's resize handler reads the player rect on a ResizeObserver
                // and rewrites the <video> inline style; nudge it once so the inline
                // styles get cleared and our !important rules win on the first paint.
                "try{window.dispatchEvent(new Event('resize'));}catch(_e){}" +
                // Kill autoplay-next on the world-screen WebView too. Without this,
                // when a queued video ends YouTube auto-rolls into a recommendation
                // and our notifyEnded check ("any video still playing?" → bail)
                // never fires, so the server's queue stalls until the per-item
                // deadline. See MOTDUI.InjectMediaHelperJS for the full rationale
                // (defensive aria-label check, observer auto-disconnect).
                "var _ytObs=null,_ytKilled=false;" +
                "function killYtAutoplay(){" +
                "if(_ytKilled)return;" +
                "try{var t=document.querySelector('.ytp-autonav-toggle-button');" +
                "if(!t)return;" +
                "var label=(t.getAttribute('aria-label')||'').toLowerCase();" +
                "if(label.indexOf('autoplay')===-1)return;" +
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
                "}" +
                // Event-listener based ended detection — fires before YouTube changes src.
                // Message includes window.__motdItemId so the server can validate it's
                // still the active queue item before advancing.
                "var _sent=false;" +
                "function notifyEnded(){" +
                "var id=window.__motdItemId||0;" +
                "if(id===0)return;" +
                // Ad → main transition can fire 'ended' on the ad video; by the time
                // this delayed notify runs, the main video is already playing in the
                // same element. Recheck — but ONLY inside the main player container,
                // because hover-preview loops on YouTube's recommended-videos rail are
                // also <video> elements that report !paused && !ended, and would
                // otherwise cause us to reset the guard and never notify the real end.
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
                "function onEnded(e){" +
                "var v=e.target;" +
                "if(!v||!isFinite(v.duration)||v.duration<=5)return;" +
                // Our AdBlock JS sets playbackRate=16 to fast-forward ads. When the
                // ad reaches its end, that elevated rate is still in effect — a clean
                // signal that this 'ended' belongs to an ad we sped through, not the
                // queued video the player actually wanted to watch.
                "if(v.playbackRate>3)return;" +
                // Fallback: YouTube ad markers on the player container catch ads
                // that finished naturally (without the rate-bump) before the
                // .ad-showing class was removed in the same tick.
                "var p=v.closest&&v.closest('.html5-video-player');" +
                "if(p&&(p.classList.contains('ad-showing')||p.classList.contains('ad-interrupting')))return;" +
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
