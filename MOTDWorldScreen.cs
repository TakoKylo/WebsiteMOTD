using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WebsiteMOTD
{
    /// <summary>
    /// Spawns a quad in the 3D world that displays the WebView content.
    /// Two instances are placed behind each goal net.
    /// Each screen has its own WebView instance so the texture stays independent.
    /// </summary>
    public class MOTDWorldScreen : MonoBehaviour
    {
        // Reuse the same DllImports from MOTDWebView (same native DLL)
        [DllImport("WebView")]
        private static extern void _CWebViewPlugin_InitStatic(bool inEditor, bool useDirect3D11);
        [DllImport("WebView")]
        private static extern IntPtr _CWebViewPlugin_Init(string gameObject, bool transparent, bool zoom, int width, int height, string ua, bool separated);
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
        private static extern string _CWebViewPlugin_GetMessage(IntPtr instance);
        [DllImport("WebView")]
        private static extern ulong _CWebViewPlugin_BitmapGeneration(IntPtr instance);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_IsInitialized(IntPtr instance);

        private IntPtr _webView = IntPtr.Zero;
        private Texture2D _texture;
        private byte[] _textureBuffer;
        private ulong _lastGen;
        private MeshRenderer _renderer;
        private AudioSource _audio;

        private static bool _staticInitDone;

        private const int TEX_WIDTH = 1280;
        private const int TEX_HEIGHT = 720;

        // ─── Static API ─────────────────────────────────────────────

        private static MOTDWorldScreen _screenA;
        private static MOTDWorldScreen _screenB;

        /// <summary>
        /// Spawn two screens behind the goal nets. Call once after joining a game.
        /// </summary>
        public static void SpawnScreens()
        {
            if (_screenA != null) return; // already spawned

            if (!MOTDWebView.PreloadNativeDLL())
            {
                Plugin.LogError("WorldScreen: WebView.dll not loaded, cannot spawn.");
                return;
            }

            // Find goal positions by searching for GoalTrigger components in the scene
            Vector3 posA, posB;
            Quaternion rotA, rotB;
            FindGoalPositions(out posA, out rotA, out posB, out rotB);

            _screenA = CreateScreen("MOTD_WorldScreen_A", posA, rotA);
            _screenB = CreateScreen("MOTD_WorldScreen_B", posB, rotB);

            Plugin.Log("World screens spawned at " + posA + " and " + posB);
        }

        /// <summary>
        /// Load a URL on both world screens.
        /// </summary>
        public static void LoadOnAllScreens(string url)
        {
            if (_screenA != null) _screenA.LoadURL(url);
            if (_screenB != null) _screenB.LoadURL(url);
        }

        /// <summary>
        /// Destroy both world screens.
        /// </summary>
        public static void DestroyScreens()
        {
            if (_screenA != null) { _screenA.Cleanup(); _screenA = null; }
            if (_screenB != null) { _screenB.Cleanup(); _screenB = null; }
        }

        // ─── Goal Finding ───────────────────────────────────────────

        private static void FindGoalPositions(out Vector3 posA, out Quaternion rotA,
                                               out Vector3 posB, out Quaternion rotB)
        {
            // Try to find Goal objects in the scene
            // Puck has "GoalTrigger" components on the goal objects
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
                    // Try to distinguish red vs blue by name or x position
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

            // If we found goals, place screens behind them (offset along their facing direction)
            // Puck rinks typically run along the Z axis with goals at each end
            if (redGoalPos.HasValue && blueGoalPos.HasValue)
            {
                Vector3 rg = redGoalPos.Value;
                Vector3 bg = blueGoalPos.Value;

                // Direction from center to each goal
                Vector3 center = (rg + bg) * 0.5f;
                Vector3 dirA = (rg - center).normalized;
                Vector3 dirB = (bg - center).normalized;

                // Place screen 3m behind each goal, 3m up
                posA = rg + dirA * 3f + Vector3.up * 3f;
                posB = bg + dirB * 3f + Vector3.up * 3f;

                // Face toward center of rink
                rotA = Quaternion.LookRotation(-dirA, Vector3.up);
                rotB = Quaternion.LookRotation(-dirB, Vector3.up);
            }
            else
            {
                // Fallback: place at fixed positions along Z axis (typical hockey rink)
                Plugin.Log("WorldScreen: Could not find goal objects, using fallback positions.");
                posA = new Vector3(0f, 4f, -30f);
                rotA = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                posB = new Vector3(0f, 4f, 30f);
                rotB = Quaternion.LookRotation(Vector3.back, Vector3.up);
            }
        }

        // ─── Instance ───────────────────────────────────────────────

        private static MOTDWorldScreen CreateScreen(string name, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.transform.position = position;
            go.transform.rotation = rotation;

            // Create a quad mesh (6m x 3.375m = 16:9 aspect)
            var mf = go.AddComponent<MeshFilter>();
            mf.mesh = CreateQuadMesh(6f, 3.375f);

            var mr = go.AddComponent<MeshRenderer>();
            // Use an unlit material so it's visible regardless of lighting
            var mat = new Material(Shader.Find("Unlit/Texture"));
            mr.material = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Audio source for ambient web audio (if ever needed)
            var audio = go.AddComponent<AudioSource>();
            audio.spatialBlend = 1f; // full 3D
            audio.minDistance = 5f;
            audio.maxDistance = 40f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.playOnAwake = false;

            var screen = go.AddComponent<MOTDWorldScreen>();
            screen._renderer = mr;
            screen._audio = audio;
            screen.InitWebView();

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
            // Flip UVs vertically — WebView2 bitmap is top-down
            mesh.uv = new[]
            {
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            return mesh;
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
                gameObject.name, true, false,
                TEX_WIDTH, TEX_HEIGHT, "", false);

            if (_webView == IntPtr.Zero)
            {
                Plugin.LogError("WorldScreen: native init failed for " + gameObject.name);
                return;
            }

            _CWebViewPlugin_SetVisibility(_webView, true);
            _CWebViewPlugin_SetRect(_webView, TEX_WIDTH, TEX_HEIGHT);
        }

        public void LoadURL(string url)
        {
            if (_webView == IntPtr.Zero) return;
            Plugin.Log("WorldScreen loading: " + url);
            _CWebViewPlugin_LoadURL(_webView, url);
        }

        void Update()
        {
            if (_webView == IntPtr.Zero) return;

            // Pump messages (discard — world screens don't need callbacks)
            for (;;)
            {
                string s = _CWebViewPlugin_GetMessage(_webView);
                if (s == null) break;
            }

            // Refresh at ~10fps for world screens (saves perf, no interaction needed)
            bool refresh = (Time.frameCount % 6 == 0);
            _CWebViewPlugin_Update(_webView, refresh, 1);
            if (!refresh) return;

            ulong gen = _CWebViewPlugin_BitmapGeneration(_webView);
            if (gen == _lastGen) return;

            int w = _CWebViewPlugin_BitmapWidth(_webView);
            int h = _CWebViewPlugin_BitmapHeight(_webView);
            if (w <= 0 || h <= 0) return;

            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
                _texture = new Texture2D(w, h, TextureFormat.RGBA32, false, !linear);
                _texture.filterMode = FilterMode.Bilinear;
                _texture.wrapMode = TextureWrapMode.Clamp;
                _textureBuffer = new byte[w * h * 4];

                if (_renderer != null && _renderer.material != null)
                    _renderer.material.mainTexture = _texture;
            }

            if (_textureBuffer == null) return;

            var gch = GCHandle.Alloc(_textureBuffer, GCHandleType.Pinned);
            _CWebViewPlugin_Render(_webView, gch.AddrOfPinnedObject());
            gch.Free();

            _texture.LoadRawTextureData(_textureBuffer);
            _texture.Apply(false);
            _lastGen = gen;
        }

        public void Cleanup()
        {
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

            _textureBuffer = null;
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
            {
                Destroy(_texture);
                _texture = null;
            }
        }
    }
}
