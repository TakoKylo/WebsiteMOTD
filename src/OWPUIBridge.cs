using System;
using System.Reflection;
using UnityEngine;

namespace WebsiteMOTD
{
    /// <summary>
    /// Reflective bridge to OpenWorldPracticeMod's public OWPUI cross-mod hook,
    /// specifically <c>MyPuckMod.UI.OWPUI.RegisterFooterButton(string, Action)</c>.
    /// Lets MOTD surface its entry point as a button in OWP's welcome panel
    /// instead of auto-opening its own overlay on join — one welcome surface
    /// for the user, not two stacked overlays.
    ///
    /// Mirrors the same boundary discipline as <see cref="TheatreVideoScreenBridge"/>:
    /// no hard assembly reference to OWP, lazy resolve with backoff, idempotent
    /// registration so plugin reload doesn't stack duplicates.
    ///
    /// Why a separate type from TheatreVideoScreenBridge: each reflective bridge
    /// targets a distinct OWP API surface with its own lifecycle. Mixing them
    /// would make the existing bridge's retry-and-cache logic harder to reason
    /// about and would couple unrelated failure modes (a missing RegisterFooterButton
    /// shouldn't poison the theatre-screen cache, and vice versa).
    /// </summary>
    internal static class OWPUIBridge
    {
        private const float ResolveRetryBackoffSec = 5f;
        private static float _lastResolveAttemptUT;

        private static Type _owpUiType;
        private static MethodInfo _registerFooterButton;

        // The handler we registered (if any), kept so plugin teardown can ask
        // OWP to forget it. OWPUI doesn't currently expose an Unregister API,
        // but storing this lets us add one without changing call sites.
        private static Action _registeredHandler;

        /// <summary>
        /// True if OWP's public OWPUI type is reachable in any loaded assembly.
        /// Used by <see cref="Plugin.OnMessageReceived"/> to suppress MOTD's
        /// auto-open-on-join when OWP is present — the welcome button takes
        /// over as the user-driven entry point.
        /// </summary>
        public static bool ApiPresent { get { Resolve(); return _owpUiType != null; } }

        /// <summary>
        /// Register a "MOTD" button into OWP's welcome-panel footer. Idempotent:
        /// OWPUI deduplicates on (text, handler) so re-calls during the same
        /// process are no-ops, and we drop the call entirely if a previous
        /// invocation already succeeded with the same handler reference.
        /// No-op when OWP isn't loaded.
        /// </summary>
        public static void TryRegisterWelcomeButton(Action openMotd)
        {
            if (openMotd == null) return;
            if (_registeredHandler == openMotd && _registerFooterButton != null) return;
            Resolve();
            if (_registerFooterButton == null) return;
            try
            {
                _registerFooterButton.Invoke(null, new object[] { "MOTD", openMotd });
                _registeredHandler = openMotd;
                Plugin.Log("Registered MOTD welcome-panel button with OWP.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("OWPUI.RegisterFooterButton threw: " + ex.Message);
            }
        }

        /// <summary>
        /// Drop cached reflection state so the next call re-resolves. Called from
        /// Plugin.Teardown so a plugin disable/enable cycle re-discovers OWP if
        /// it was reloaded into a fresh assembly identity in the meantime.
        /// </summary>
        public static void ResetCachedState()
        {
            _lastResolveAttemptUT = 0f;
            _owpUiType = null;
            _registerFooterButton = null;
            _registeredHandler = null;
        }

        private static void Resolve()
        {
            if (_owpUiType != null) return;

            float now = Time.unscaledTime;
            if (_lastResolveAttemptUT > 0f && now - _lastResolveAttemptUT < ResolveRetryBackoffSec)
                return;
            _lastResolveAttemptUT = now;

            // Same canonical namespace as TheatreVideoScreenBridge — OWP UI
            // lives under MyPuckMod.UI, distinct from the gameplay types under
            // MyPuckMod. No legacy "OpenWorldPracticeMod.*" fallback here:
            // RegisterFooterButton is new in this OWP release, so older builds
            // wouldn't have it under either namespace.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("MyPuckMod.UI.OWPUI", false);
                    if (t != null) { _owpUiType = t; break; }
                }
                catch { }
            }
            if (_owpUiType == null) return;

            // Explicit (string, Action) signature so a future overload doesn't
            // resolve ambiguously. Returns null gracefully on older OWP builds
            // that ship the type but not the hook — callers no-op in that case.
            _registerFooterButton = _owpUiType.GetMethod(
                "RegisterFooterButton",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(Action) },
                null);

            Plugin.Log("OWPUI integration resolved from " + _owpUiType.Assembly.GetName().Name
                + " (RegisterFooterButton=" + (_registerFooterButton != null ? "yes" : "no") + ").");
        }
    }
}
