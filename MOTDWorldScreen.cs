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
        private static extern ulong _CWebViewPlugin_BitmapGeneration(IntPtr instance);
        [DllImport("WebView")]
        private static extern bool _CWebViewPlugin_IsInitialized(IntPtr instance);

        // ─── Shared state (single webview for all screens) ──────────
        private static IntPtr _sharedWebView = IntPtr.Zero;
        private static Texture2D _sharedTexture;
        private static byte[] _sharedTextureBuffer;
        private static ulong _sharedLastGen;
        private static bool _staticInitDone;
        private static string _currentUrl;

        private const int TEX_WIDTH = 1280;
        private const int TEX_HEIGHT = 720;

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
            _currentUrl = url;
            Plugin.Log("WorldScreen loading: " + url);
            _CWebViewPlugin_LoadURL(_sharedWebView, url);
        }

        public static void DestroyScreens()
        {
            if (_screenA != null) { _screenA.Cleanup(); _screenA = null; }
            if (_screenB != null) { _screenB.Cleanup(); _screenB = null; }
            DestroySharedWebView();
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
                var audio = go.AddComponent<AudioSource>();
                audio.spatialBlend = 1f;
                audio.minDistance = 5f;
                audio.maxDistance = 40f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                audio.playOnAwake = false;

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

            if (!_staticInitDone)
            {
                bool isDX11 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;
                _CWebViewPlugin_InitStatic(false, isDX11);
                _staticInitDone = true;
            }

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
            _currentUrl = null;
        }

        // ─── Per-frame update ───────────────────────────────────────

        void Update()
        {
            // Only the primary screen drives the shared webview
            if (_isPrimary && _sharedWebView != IntPtr.Zero)
            {
                bool refresh = (Time.frameCount % 6 == 0);
                _CWebViewPlugin_Update(_sharedWebView, refresh, 1);

                if (refresh)
                {
                    ulong gen = _CWebViewPlugin_BitmapGeneration(_sharedWebView);
                    if (gen != _sharedLastGen)
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

                            _sharedLastGen = gen;
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

    }
}
