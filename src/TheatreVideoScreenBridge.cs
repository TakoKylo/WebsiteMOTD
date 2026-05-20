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
    /// Public surface mirrors the OpenWorld API:
    ///   TryClaim(owner) → GameObject (null if unavailable or already claimed)
    ///   Release(owner)
    ///   IsAvailable, IsClaimed, CurrentOwner, Screen, ScreenRenderer
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

        /// <summary>True if OpenWorldPracticeMod's TheatreVideoScreen class is reachable.</summary>
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

        /// <summary>Release our claim so OpenWorld can resume its showcase video.</summary>
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

        /// <summary>The Renderer the OpenWorld mod considers "the screen" — preferred over GetComponent lookups.</summary>
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

            // The OpenWorld mod's type name isn't pinned by us — try a couple of
            // reasonable variants. AppDomain scan lets us pick it up regardless of
            // load order without a hard reference to its assembly.
            string[] candidates =
            {
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
            _tryClaim = _type.GetMethod("TryClaim", flags, null, new[] { typeof(string) }, null);
            _release = _type.GetMethod("Release", flags, null, new[] { typeof(string) }, null);
            _isAvailable = _type.GetProperty("IsAvailable", flags);
            _isClaimed = _type.GetProperty("IsClaimed", flags);
            _currentOwner = _type.GetProperty("CurrentOwner", flags);
            _screen = _type.GetProperty("Screen", flags);
            _screenRenderer = _type.GetProperty("ScreenRenderer", flags);

            Plugin.Log("TheatreVideoScreen API resolved from " + _type.Assembly.GetName().Name + ".");
        }
    }
}
