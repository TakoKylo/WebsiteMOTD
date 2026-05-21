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
        public const string OwnerId = "WebsiteMOTD";

        private static bool _resolveAttempted;
        private static Type _type;
        private static MethodInfo _tryClaim;
        private static MethodInfo _release;
        private static PropertyInfo _isAvailable;
        private static PropertyInfo _isClaimed;
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
        /// a single consumer (MOTDWorldScreen). Safe to call repeatedly; idempotent
        /// when the same handler is already wired. No-ops if OWP isn't loaded or its
        /// API doesn't expose a ClaimChanged event of the expected shape.
        /// </summary>
        public static void SubscribeClaimChanged(Action<string, GameObject> handler)
        {
            Resolve();
            if (_type == null || _claimChangedEvent == null) return;

            // Already subscribed with this exact handler — no-op.
            if (_onClaimChanged == handler && _eventDelegateInstance != null) return;

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
            _resolveAttempted = false;
            _type = null;
            _tryClaim = null;
            _release = null;
            _isAvailable = null;
            _isClaimed = null;
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
            if (_resolveAttempted) return;
            _resolveAttempted = true;

            // Per the current OWP docs the canonical type name is
            // MyPuckMod.TheatreVideoScreen. Older builds may have shipped under
            // OpenWorldPracticeMod.* or an unnamespaced TheatreVideoScreen — try
            // those as fallbacks so we don't break if the mod gets re-namespaced.
            string[] candidates =
            {
                "MyPuckMod.TheatreVideoScreen",
                "OpenWorldPracticeMod.TheatreVideoScreen",
                "TheatreVideoScreen",
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
            if (_type == null) return;

            var flags = BindingFlags.Public | BindingFlags.Static;
            _tryClaim       = _type.GetMethod("TryClaim", flags, null, new[] { typeof(string) }, null);
            _release        = _type.GetMethod("Release",  flags, null, new[] { typeof(string) }, null);
            _isAvailable    = _type.GetProperty("IsAvailable",    flags);
            _isClaimed      = _type.GetProperty("IsClaimed",      flags);
            _currentOwner   = _type.GetProperty("CurrentOwner",   flags);
            _screen         = _type.GetProperty("Screen",         flags);
            _screenRenderer = _type.GetProperty("ScreenRenderer", flags);
            _claimChangedEvent = _type.GetEvent("ClaimChanged", flags);

            Plugin.Log("TheatreVideoScreen API resolved from " + _type.Assembly.GetName().Name
                       + " (event=" + (_claimChangedEvent != null ? "yes" : "no") + ").");
        }
    }
}
