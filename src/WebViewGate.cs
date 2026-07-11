using System;
using System.IO;
using Microsoft.Win32;
using UnityEngine;
using UnityEngine.Rendering;

namespace WebsiteMOTD
{
    /// <summary>
    /// Single decision point for whether the native WebView2 plugin may be used
    /// this session, and the crash-recovery machinery behind it.
    ///
    /// A hard native crash inside WebView.dll (access violation, GPU-driver fault,
    /// WebView2 broker crash) kills the whole Unity process instantly — managed
    /// try/catch can't intercept it. So instead of "catching" the crash we make it
    /// self-healing across launches, plus refuse to start on environments that are
    /// known not to support the native path. Everything here degrades to the Steam
    /// overlay browser (see <see cref="MOTDUI"/>), which works everywhere.
    ///
    /// Layers, all folded into <see cref="Allowed"/>:
    ///   • Platform     — Windows client only (never a dedicated/headless server),
    ///                    on a Direct3D11 device. A non-D3D11 graphics API can crash
    ///                    the native texture interop, so it's treated like Linux/macOS:
    ///                    no in-game browser, silent Steam-overlay fallback.
    ///   • Environment  — WebView2 Runtime present (registry). Fail-open: only
    ///                    blocks when we POSITIVELY confirm it's missing, so a
    ///                    working install is never disabled on a false negative.
    ///   • Crash safe-mode — a sentinel file is written before native bring-up and
    ///                    deleted on clean shutdown. If it's still there next launch,
    ///                    the last session died with the WebView live → disable this
    ///                    session and retry next launch; after a second crash, disable
    ///                    persistently until the user re-enables. One clean session
    ///                    resets the counter.
    ///   • Manual switch — <c>webview_enabled</c> in ClientMOTD.json, toggled live via
    ///                    <c>/motd webview on|off</c>.
    /// </summary>
    internal static class WebViewGate
    {
        // WebView2 Evergreen Runtime registers its version ("pv") under this client id.
        private const string WebView2ClientKey =
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        // The native WebView's texture interop is built for Direct3D11. Any other
        // graphics API (DX12 / Vulkan / OpenGL) can crash the native path, so a
        // non-D3D11 device is treated exactly like an unsupported platform
        // (Linux/macOS): no in-game browser, silent Steam-overlay fallback.

        // After this many consecutive crash-detections, stop auto-retrying and
        // persist the WebView off until the user re-enables it.
        private const int MaxCrashesBeforePersistentDisable = 2;

        private static bool _initialized;
        private static bool _platformOk;
        private static string _envReason;          // non-null => an environment precondition failed
        private static bool _safeModeThisSession;  // a prior crash disabled us for this run
        private static string _safeModeReason;

        private static bool _flagWritten;              // sentinel written this session (idempotent)
        private static bool _initStartedThisSession;   // native bring-up was attempted this run
        private static bool _notified;                 // one chat notice per session
        private static bool _userForcedOn;             // /motd webview on overrides a pre-flight env block

        /// <summary>
        /// Master gate. True only when every layer agrees the native WebView may run.
        /// Consulted at each native chokepoint (<see cref="MOTDWebView.PreloadNativeDLL"/>,
        /// <see cref="MOTDUI"/> overlay, world screens). Cheap; safe to call often.
        /// </summary>
        public static bool Allowed
        {
            get
            {
                if (!_initialized) Initialize();
                return _platformOk
                    && (_envReason == null || _userForcedOn)
                    && !_safeModeThisSession
                    && ClientConfig.WebViewEnabled;
            }
        }

        /// <summary>Human-readable reason the WebView is disabled, or null when allowed.</summary>
        public static string DisabledReason
        {
            get
            {
                if (!_initialized) Initialize();
                if (!_platformOk) return "the in-game browser isn't supported here";
                if (_envReason != null && !_userForcedOn) return _envReason;
                if (_safeModeThisSession) return _safeModeReason;
                if (!ClientConfig.WebViewEnabled)
                    return "you turned the in-game browser off (type /motd webview on to re-enable)";
                return null;
            }
        }

        /// <summary>
        /// One-time evaluation: platform + environment checks, prior-crash detection,
        /// and a hook to clear the sentinel on a clean quit. Called from
        /// <see cref="Plugin.Setup"/> on clients; also lazily by <see cref="Allowed"/>.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _notified = false;
            _userForcedOn = false;

            if (Plugin.IsDedicatedServer())
            {
                _platformOk = false;
                _envReason = "dedicated server (no renderer)";
                return;
            }

            _platformOk = MOTDWebView.IsSupportedPlatform();

            // Non-D3D11 graphics APIs (DX12 / Vulkan / OpenGL) can crash the native
            // WebView's D3D11 texture interop, so fold the check into _platformOk:
            // a non-D3D11 device is treated exactly like an unsupported platform —
            // no in-game browser, silent Steam-overlay fallback, no chat nag. Done
            // BEFORE the runtime check so we don't touch the registry pointlessly.
            if (_platformOk)
            {
                var gfx = SystemInfo.graphicsDeviceType;
                if (gfx != GraphicsDeviceType.Direct3D11)
                {
                    _platformOk = false;
                    Plugin.Log("WebViewGate: graphics device is " + gfx
                        + " (not Direct3D11) — treating like an unsupported platform; pages open in the Steam overlay.");
                }
            }

            if (_platformOk)
            {
                // Missing WebView2 Runtime is a deterministic failure on a subset of
                // machines (Win10 / stripped installs). Block only on a positive
                // "confirmed missing" (a bundled fixed-version runtime is detected
                // first, so a working install is never disabled); /motd webview on
                // can still force a retry.
                if (WebView2RuntimeConfirmedMissing())
                {
                    _envReason = "the Microsoft Edge WebView2 Runtime isn't installed — "
                        + "install it to enable the in-game browser (pages open in the Steam overlay meanwhile)";
                }
            }

            DetectPriorCrash();

            // Clear the sentinel on a normal quit so the next launch starts clean.
            // (A hard crash never reaches this — which is exactly how we detect it.)
            Application.quitting += MarkCleanShutdown;

            Plugin.Log("WebViewGate: allowed=" + Allowed
                + (Allowed ? "" : " (" + DisabledReason + ")")
                + " [platformOk=" + _platformOk
                + ", env=" + (_envReason ?? "ok")
                + ", safeMode=" + _safeModeThisSession
                + ", configEnabled=" + ClientConfig.WebViewEnabled
                + ", crashCount=" + ClientConfig.WebViewCrashCount + "].");
        }

        /// <summary>
        /// Write the crash sentinel immediately before native bring-up. Idempotent —
        /// only the first native chokepoint of the session actually writes. If the
        /// process dies before <see cref="MarkCleanShutdown"/> runs, the sentinel
        /// survives and the next launch treats it as a crash.
        /// </summary>
        public static void MarkNativeInitStarting()
        {
            _initStartedThisSession = true;
            if (_flagWritten) return;
            _flagWritten = true;
            try { File.WriteAllText(FlagPath(), "1"); }
            catch (Exception ex) { Plugin.LogError("WebViewGate: couldn't write crash sentinel: " + ex.Message); }
        }

        /// <summary>
        /// Clean-shutdown handler (Unity <c>Application.quitting</c> and plugin
        /// teardown): delete the sentinel and, if the WebView actually ran this
        /// session, reset the crash counter so one good session recovers.
        /// </summary>
        public static void MarkCleanShutdown()
        {
            try { if (File.Exists(FlagPath())) File.Delete(FlagPath()); }
            catch (Exception ex) { Plugin.LogError("WebViewGate: couldn't clear crash sentinel: " + ex.Message); }
            _flagWritten = false;

            if (_initStartedThisSession && ClientConfig.WebViewCrashCount != 0)
                ClientConfig.WebViewCrashCount = 0;
        }

        /// <summary>
        /// Plugin disable path: treat as a clean shutdown, drop the quit hook, and
        /// reset so a re-enable re-evaluates from scratch.
        /// </summary>
        public static void OnPluginTeardown()
        {
            if (!_initialized) return;
            MarkCleanShutdown();
            Application.quitting -= MarkCleanShutdown;
            _initialized = false;
            _initStartedThisSession = false;
            _notified = false;
            _userForcedOn = false;
        }

        /// <summary>
        /// Surface the disabled reason in chat exactly once per session. Called when
        /// the player actively tries to open a page and on connect. No-op when the
        /// WebView is available or when there's nothing actionable (unsupported
        /// platform).
        /// </summary>
        public static void NotifyDisabledOnce(bool onConnect = false)
        {
            if (_notified || Allowed || !_platformOk) return;
            // On join, only surface the "surprising" reasons — a crash safe-mode or a
            // missing runtime. Don't nag players who deliberately turned it off; they'll
            // still get told (with re-enable steps) if they actively try to open a page.
            if (onConnect && _envReason == null && !_safeModeThisSession) return;
            _notified = true;
            Plugin.LocalChat(DisabledReason);
        }

        /// <summary>
        /// Handle the <c>/motd webview [on|off]</c> chat sub-command. Empty/other =
        /// report status.
        /// </summary>
        public static void HandleChatToggle(string sub)
        {
            sub = (sub ?? "").Trim().ToLowerInvariant();

            if (sub == "off" || sub == "disable" || sub == "0")
            {
                ClientConfig.WebViewEnabled = false;
                _safeModeThisSession = false; // explicit user choice, not a crash state
                _userForcedOn = false;
                // Route teardown through the gate path so the world-screen load state
                // (_lastLoadedWorldUrl) is reset too — otherwise a later re-enable
                // would spawn a fresh WebView but skip the reload and show blank.
                try { Plugin.RefreshWorldScreens(); } catch { }
                try { MOTDUI.Hide(); } catch { }
                Plugin.LocalChat("In-game browser turned OFF — pages will open in the Steam overlay. Type /motd webview on to re-enable.");
                return;
            }

            if (sub == "on" || sub == "enable" || sub == "1")
            {
                ClientConfig.WebViewEnabled = true;
                ClientConfig.WebViewCrashCount = 0; // manual re-enable = fresh start
                _safeModeThisSession = false;
                _userForcedOn = true;               // override a pre-flight env block if the user insists
                _notified = false;
                MOTDWebView.ResetPreloadForRetry();
                try { Plugin.RefreshWorldScreens(); } catch { }
                Plugin.LocalChat(Allowed
                    ? "In-game browser turned ON. Open a page (F2 or /web) to use it."
                    : "In-game browser enabled, but still unavailable: " + DisabledReason);
                return;
            }

            Plugin.LocalChat(Allowed
                ? "In-game browser: ON."
                : "In-game browser: OFF — " + DisabledReason);
        }

        // ─── Internals ──────────────────────────────────────────────

        private static string FlagPath()
        {
            return Path.Combine(ConfigPaths.ConfigDir(), "MOTD_webview_active.flag");
        }

        /// <summary>
        /// On startup, if the sentinel from a prior session survived, the last run
        /// crashed with the WebView live. Bump the counter, enter safe-mode for this
        /// session, and after repeated crashes persist the WebView off.
        /// </summary>
        private static void DetectPriorCrash()
        {
            string flag = FlagPath();
            bool crashed;
            try { crashed = File.Exists(flag); }
            catch { crashed = false; }
            if (!crashed) return;

            try { File.Delete(flag); } catch { } // consume it

            int count = ClientConfig.WebViewCrashCount + 1;
            ClientConfig.WebViewCrashCount = count;
            _safeModeThisSession = true;

            if (count >= MaxCrashesBeforePersistentDisable)
            {
                ClientConfig.WebViewEnabled = false; // persist off until manual re-enable
                _safeModeReason = "MOTD turned the in-game browser off after repeated crashes ("
                    + count + "). Pages open in the Steam overlay. Type /motd webview on to try again.";
            }
            else
            {
                _safeModeReason = "MOTD disabled the in-game browser because the game crashed last session. "
                    + "It'll retry next launch — or type /motd webview on to re-enable now.";
            }
            Plugin.LogError("WebViewGate: prior-session crash detected (count=" + count + ") — " + _safeModeReason);
        }

        /// <summary>
        /// True only when we can POSITIVELY confirm no WebView2 Runtime is installed.
        /// Checks the machine and per-user registrations across both registry views.
        /// Any read failure returns false (fail-open) so a working install is never
        /// disabled by a false negative.
        /// </summary>
        private static bool WebView2RuntimeConfirmedMissing()
        {
            try
            {
                // A fixed-version runtime shipped beside the mod doesn't register in
                // the registry. If one is present, never report "missing" — otherwise
                // we'd disable a perfectly working WebView.
                if (FixedRuntimePresent()) return false;

                if (HasRuntimeVersion(RegistryHive.LocalMachine, RegistryView.Registry32)) return false;
                if (HasRuntimeVersion(RegistryHive.LocalMachine, RegistryView.Registry64)) return false;
                if (HasRuntimeVersion(RegistryHive.CurrentUser, RegistryView.Registry32)) return false;
                if (HasRuntimeVersion(RegistryHive.CurrentUser, RegistryView.Registry64)) return false;
                return true; // read every location, none carried a valid version
            }
            catch
            {
                return false; // uncertain → don't block
            }
        }

        /// <summary>
        /// True if a fixed-version WebView2 runtime is bundled next to the mod
        /// (it ships <c>msedgewebview2.exe</c> in a runtime folder). Checked shallowly
        /// — the DLL dir + native\x64 and their immediate subdirectories — so the
        /// bundled uBlock extension tree isn't walked.
        /// </summary>
        private static bool FixedRuntimePresent()
        {
            string dllDir = Path.GetDirectoryName(typeof(WebViewGate).Assembly.Location) ?? "";
            foreach (var root in new[] { Path.Combine(dllDir, "native", "x64"), dllDir })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                if (File.Exists(Path.Combine(root, "msedgewebview2.exe"))) return true;
                foreach (var sub in Directory.GetDirectories(root))
                    if (File.Exists(Path.Combine(sub, "msedgewebview2.exe"))) return true;
            }
            return false;
        }

        private static bool HasRuntimeVersion(RegistryHive hive, RegistryView view)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, view))
            using (var key = baseKey.OpenSubKey(WebView2ClientKey))
            {
                if (key == null) return false;
                string pv = key.GetValue("pv") as string;
                return !string.IsNullOrEmpty(pv) && pv != "0.0.0.0";
            }
        }
    }
}
