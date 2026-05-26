using System;
using System.Reflection;
using UnityEngine;

namespace WebsiteMOTD
{
    /// <summary>
    /// Reflective bridge to OpenWorldPracticeMod's TheatreVideoScreen claim API.
    /// All operations no-op cleanly when that mod isn't loaded, so this is safe to
    /// call unconditionally. Avoids a hard assembly reference between the two mods.
    ///
    /// Public surface mirrors the OWP API:
    ///   TryClaim(owner) → GameObject (null if unavailable / already claimed)
    ///   Release(owner)
    ///   IsAvailable, IsClaimed, CurrentOwner, Screen, ScreenRenderer
    ///   ClaimChanged event (subscribe via SubscribeClaimChanged)
    ///
    /// Why event-driven, not polling: per OWP's own integration notes, the screen
    /// GameObject is destroyed and recreated across open-world teardown/re-entry.
    /// Polling caches a stale GameObject through that transition; subscribing to
    /// ClaimChanged is the only way to know when we need to re-bind to the new GO.
    /// </summary>
    internal static class TheatreVideoScreenBridge
    {
        // Identifier passed to OWP's TryClaim/Release. Mirrored from Plugin.MOD_NAME
        // so a rename can't silently desync the two. static readonly (not const)
        // because const fields can't initialize from a non-const source.
        public static readonly string OwnerId = Plugin.MOD_NAME;

        // Timestamp of the last attempted Resolve when _type was still null.
        // Lets us retry periodically without iterating every loaded assembly on
        // every property access — important because OWP can load AFTER MOTD
        // (the previous one-shot _resolveAttempted flag missed that case
        // permanently until a plugin disable/enable cycle).
        private const float ResolveRetryBackoffSec = 5f;
        private static float _lastResolveAttemptUT;

        private static Type _type;
        private static MethodInfo _tryClaim;
        private static MethodInfo _release;
        private static PropertyInfo _isAvailable;
        private static PropertyInfo _currentOwner;
        private static PropertyInfo _screen;
        private static PropertyInfo _screenRenderer;
        private static EventInfo _claimChangedEvent;

        // Active subscriber (single consumer — MOTDWorldScreen). The bridge owns
        // the strongly-typed delegate instance it added to the event, so we can
        // remove it later via the same reference.
        private static Action<string, GameObject> _onClaimChanged;
        private static Delegate _eventDelegateInstance;

        /// <summary>True if OWP's TheatreVideoScreen class is reachable in any loaded assembly.</summary>
        public static bool ApiPresent { get { Resolve(); return _type != null; } }

        /// <summary>True if the theatre screen prefab is currently spawned in the scene.</summary>
        public static bool IsAvailable { get { Resolve(); return SafeBool(_isAvailable); } }

        /// <summary>True if the current claim belongs to us.</summary>
        public static bool IsClaimedByUs
        {
            get
            {
                Resolve();
                if (_currentOwner == null) return false;
                try { return (_currentOwner.GetValue(null) as string) == OwnerId; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Try to claim the theatre screen for this mod. Returns the screen GameObject
        /// on success (caller can drive the renderer however it likes), null otherwise.
        /// </summary>
        public static GameObject TryClaim()
        {
            Resolve();
            if (_tryClaim == null) return null;
            try
            {
                return _tryClaim.Invoke(null, new object[] { OwnerId }) as GameObject;
            }
            catch (Exception ex)
            {
                Plugin.LogError("TheatreVideoScreen.TryClaim threw: " + ex.Message);
                return null;
            }
        }

        /// <summary>Release our claim so OWP can resume its showcase video.</summary>
        public static void Release()
        {
            Resolve();
            if (_release == null) return;
            try
            {
                _release.Invoke(null, new object[] { OwnerId });
            }
            catch (Exception ex)
            {
                Plugin.LogError("TheatreVideoScreen.Release threw: " + ex.Message);
            }
        }

        /// <summary>The Renderer OWP considers "the screen" — preferred over GetComponent lookups.</summary>
        public static Renderer GetScreenRenderer()
        {
            Resolve();
            if (_screenRenderer == null) return null;
            try { return _screenRenderer.GetValue(null) as Renderer; }
            catch { return null; }
        }

        /// <summary>Currently-active screen GameObject (may be set even when not claimed by us).</summary>
        public static GameObject GetScreen()
        {
            Resolve();
            if (_screen == null) return null;
            try { return _screen.GetValue(null) as GameObject; }
            catch { return null; }
        }

        /// <summary>
        /// Subscribe to ClaimChanged. Replaces any prior subscriber — the bridge tracks
        /// a single consumer (MOTDWorldScreen). Safe to call repeatedly. No-ops if OWP
        /// isn't loaded or its API doesn't expose a ClaimChanged event of the expected
        /// shape.
        ///
        /// Always does Unsubscribe → Subscribe rather than early-returning when the
        /// handler matches the cached one. The early-return optimisation broke the
        /// "OWP disabled and re-enabled while MOTD stays alive" case: OWP's
        /// ResetSubscribers clears its event invocation list, but our cached
        /// _eventDelegateInstance reference stayed non-null, so we'd believe we
        /// were still subscribed when we weren't. The double-call cost is a
        /// no-op RemoveEventHandler plus one AddEventHandler — both O(N) in
        /// subscribers, N≤2 in practice, and called at most once per second from
        /// EnsureTheatreClaim's polling path.
        /// </summary>
        public static void SubscribeClaimChanged(Action<string, GameObject> handler)
        {
            Resolve();
            if (_type == null || _claimChangedEvent == null) return;

            UnsubscribeClaimChanged();
            _onClaimChanged = handler;

            try
            {
                // Build a delegate matching the event's handler type that targets our
                // static dispatcher. CreateDelegate verifies signature compatibility —
                // if OWP changed the event shape (e.g. to a single-arg flavor), this
                // throws and we log + bail rather than crashing.
                var dispatcher = typeof(TheatreVideoScreenBridge).GetMethod(
                    nameof(DispatchClaimChanged),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (dispatcher == null) return;

                _eventDelegateInstance = Delegate.CreateDelegate(
                    _claimChangedEvent.EventHandlerType, null, dispatcher);
                _claimChangedEvent.AddEventHandler(null, _eventDelegateInstance);
            }
            catch (Exception ex)
            {
                Plugin.LogError("Subscribe to TheatreVideoScreen.ClaimChanged failed: " + ex.Message);
                _eventDelegateInstance = null;
                _onClaimChanged = null;
            }
        }

        /// <summary>Remove our handler from ClaimChanged. Safe to call when not subscribed.</summary>
        public static void UnsubscribeClaimChanged()
        {
            if (_eventDelegateInstance == null) return;
            try
            {
                _claimChangedEvent?.RemoveEventHandler(null, _eventDelegateInstance);
            }
            catch (Exception ex)
            {
                Plugin.LogError("Unsubscribe TheatreVideoScreen.ClaimChanged failed: " + ex.Message);
            }
            _eventDelegateInstance = null;
            _onClaimChanged = null;
        }

        /// <summary>
        /// Drop ALL cached reflection state so the next call re-resolves the API.
        /// Called from Plugin.Teardown so a plugin disable/enable cycle re-discovers
        /// OWP — defensive against the case where OWP itself got reloaded with a
        /// fresh assembly between our sessions (our cached MethodInfo would point at
        /// the old assembly and Invoke would throw cryptically).
        /// </summary>
        public static void ResetCachedState()
        {
            UnsubscribeClaimChanged();
            _lastResolveAttemptUT = 0f;
            _type = null;
            _tryClaim = null;
            _release = null;
            _isAvailable = null;
            _currentOwner = null;
            _screen = null;
            _screenRenderer = null;
            _claimChangedEvent = null;
        }

        // Reflective event target — invoked by OWP and forwards to the user callback.
        // Kept static + non-public so CreateDelegate can hit it without a target instance.
        private static void DispatchClaimChanged(string newOwner, GameObject screen)
        {
            try { _onClaimChanged?.Invoke(newOwner, screen); }
            catch (Exception ex) { Plugin.LogError("TheatreVideoScreen.ClaimChanged handler threw: " + ex.Message); }
        }

        private static bool SafeBool(PropertyInfo p)
        {
            if (p == null) return false;
            try { return (bool)p.GetValue(null); }
            catch { return false; }
        }

        private static void Resolve()
        {
            // Already resolved — fast path, no work.
            if (_type != null) return;

            // Recent unsuccessful attempt — back off so we don't iterate every
            // loaded assembly on every property access. The retry window catches
            // late-loading OWP (e.g. user toggles the mod on after MOTD is
            // already running) without thrashing the reflection layer between.
            float now = Time.unscaledTime;
            if (_lastResolveAttemptUT > 0f && now - _lastResolveAttemptUT < ResolveRetryBackoffSec)
                return;
            _lastResolveAttemptUT = now;

            // Canonical OWP namespace as of current builds. The legacy
            // "OpenWorldPracticeMod.*" fallback covers older mod versions that
            // shipped before the rename. Bare "TheatreVideoScreen" used to be
            // here too as a last-ditch fallback, but with OWP's namespace stable
            // for several releases now the risk of binding to an unrelated
            // global-namespace class outweighs the benefit.
            string[] candidates =
            {
                "MyPuckMod.TheatreVideoScreen",
                "OpenWorldPracticeMod.TheatreVideoScreen",
            };

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var name in candidates)
                    {
                        var t = asm.GetType(name, false);
                        if (t != null) { _type = t; break; }
                    }
                    if (_type != null) break;
                }
                catch { }
            }
            if (_type == null) return;  // backoff timestamp guards the retry

            var flags = BindingFlags.Public | BindingFlags.Static;
            _tryClaim       = _type.GetMethod("TryClaim", flags, null, new[] { typeof(string) }, null);
            _release        = _type.GetMethod("Release",  flags, null, new[] { typeof(string) }, null);
            _isAvailable    = _type.GetProperty("IsAvailable",    flags);
            _currentOwner   = _type.GetProperty("CurrentOwner",   flags);
            _screen         = _type.GetProperty("Screen",         flags);
            _screenRenderer = _type.GetProperty("ScreenRenderer", flags);
            _claimChangedEvent = _type.GetEvent("ClaimChanged", flags);

            Plugin.Log("TheatreVideoScreen API resolved from " + _type.Assembly.GetName().Name
                       + " (event=" + (_claimChangedEvent != null ? "yes" : "no") + ").");
        }
    }
}
