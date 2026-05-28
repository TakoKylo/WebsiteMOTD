using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace WebsiteMOTD
{
    // ────────────────────────────────────────────────────────────────
    //  Shared path helpers
    // ────────────────────────────────────────────────────────────────

    internal static class ConfigPaths
    {
        /// <summary>
        /// &lt;puck-root&gt;/config/ — derived from Application.dataPath, which
        /// always points at &lt;gameRoot&gt;/Puck_Data on both clients and
        /// dedicated servers (regardless of whether the mod DLL lives in
        /// Plugins/&lt;mod&gt;/ or steamapps/workshop/content/&lt;app&gt;/&lt;id&gt;/).
        /// Mirrors the pattern CompetitiveAdjustments uses, which is known
        /// to land in the right place on Workshop-installed servers.
        /// </summary>
        public static string ConfigDir()
        {
            string gameRoot = Application.dataPath;
            if (gameRoot.EndsWith("Puck_Data"))
                gameRoot = Directory.GetParent(gameRoot).FullName;

            string configDir = Path.Combine(gameRoot, "config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            return configDir;
        }

        /// <summary>Mod DLL directory — used as a legacy-migration source.</summary>
        public static string DllDir()
        {
            return Path.GetDirectoryName(typeof(ConfigPaths).Assembly.Location) ?? "";
        }

        /// <summary>
        /// Candidate legacy locations to migrate from, in priority order.
        /// Only includes &quot;next to DLL&quot; — the old &lt;dll&gt;/../../config/
        /// path is intentionally excluded because on Workshop-installed
        /// servers it resolved to steamapps/workshop/content/config/, and
        /// migrating from a wrong path would just propagate stale data.
        /// </summary>
        public static IEnumerable<string> LegacyDirs()
        {
            yield return DllDir();
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Browse-shortcut link (server-defined, synced to clients)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A browse-row button parsed from its <c>"Name|URL"</c> string form.
    /// Stored as plain strings in config/over the wire because JsonUtility does
    /// not reliably (de)serialize List&lt;custom-class&gt; — but it handles
    /// List&lt;string&gt; fine (same as queue_allowed_sites).
    /// </summary>
    public struct BrowseLink
    {
        public string Name;
        public string Url;

        /// <summary>Parse "Name|URL" (split on the first pipe). No pipe = URL only.</summary>
        public static BrowseLink Parse(string entry)
        {
            string e = (entry ?? "").Trim();
            int bar = e.IndexOf('|');
            if (bar < 0) return new BrowseLink { Name = e, Url = e };
            return new BrowseLink
            {
                Name = e.Substring(0, bar).Trim(),
                Url  = e.Substring(bar + 1).Trim(),
            };
        }
    }

    /// <summary>JsonUtility can't (de)serialize a bare List, so wrap it.</summary>
    [Serializable]
    public class BrowseLinksWrapper
    {
        public List<string> links = new List<string>();
    }

    // ────────────────────────────────────────────────────────────────
    //  Server-only config
    // ────────────────────────────────────────────────────────────────

    [Serializable]
    public class ServerConfigData
    {
        public bool screens_enabled = false;
        public bool queue_enabled = false;
        public string motd_url = "https://poncepuck.net/motd/";

        // Buttons shown in the overlay's "Browse:" row. Server-defined and synced
        // to every client. Each entry is "Name|URL". Seeded with YouTube + Twitch;
        // edit/add/remove freely. (List<string>, not List<object>, because
        // JsonUtility silently fails to parse arrays of custom classes.)
        public List<string> browse_links = ServerConfig.DefaultBrowseLinks();

        // Hard cap on how long a single queue item is allowed to remain
        // "current" before the server force-advances. Defends against the
        // dedicated-server-has-no-WebView case where the server depends on
        // clients reporting "ended" — if every client has disconnected,
        // crashed, or had its ad-block JS misfire, the queue would otherwise
        // stall on that item forever. 0 disables the cap.
        public int max_item_seconds = 1800;

        // Host allowlist for the shared queue. Domain is matched against
        // the URL's host; subdomains are accepted (e.g. "youtube.com"
        // also allows "m.youtube.com"). An empty list disables the
        // restriction entirely, which is useful for private lobbies.
        public List<string> queue_allowed_sites = new List<string>
        {
            "youtube.com",
            "youtu.be",
            "twitch.tv",
            "kick.com",
        };
    }

    /// <summary>
    /// Hard settings for dedicated servers. Persisted to
    ///   &lt;puck-root&gt;/config/ServerMOTD.json
    /// and only ever created/read on dedicated servers — clients never
    /// touch this file. Admins can change the MOTD URL here without
    /// recompiling the mod.
    /// </summary>
    public static class ServerConfig
    {
        public static bool ScreensEnabled => _data.screens_enabled;
        public static bool QueueEnabled   => _data.queue_enabled;
        public static string MotdUrl      => _data.motd_url;

        /// <summary>Browse-row buttons ("Name|URL" entries), defaults if cleared.</summary>
        public static List<string> BrowseLinks =>
            (_data.browse_links != null && _data.browse_links.Count > 0)
                ? _data.browse_links
                : DefaultBrowseLinks();

        /// <summary>The seed/fallback browse buttons (YouTube + Twitch).</summary>
        public static List<string> DefaultBrowseLinks() => new List<string>
        {
            "YouTube|https://www.youtube.com/",
            "Twitch|https://www.twitch.tv/directory",
        };
        public static int MaxItemSeconds  => _data.max_item_seconds;

        public static IReadOnlyList<string> QueueAllowedSites =>
            _data.queue_allowed_sites ?? new List<string>();

        /// <summary>Whether a URL's host is permitted in the shared queue.</summary>
        public static bool IsQueueUrlAllowed(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            var list = _data.queue_allowed_sites;
            if (list == null || list.Count == 0) return true; // no restriction

            string host;
            try
            {
                var uri = new Uri(url);
                host = uri.Host.ToLowerInvariant();
            }
            catch { return false; }

            foreach (string raw in list)
            {
                string a = (raw ?? "").Trim().ToLowerInvariant();
                if (a.Length == 0) continue;
                if (host == a) return true;
                if (host.EndsWith("." + a)) return true; // subdomain match
            }
            return false;
        }

        private static ServerConfigData _data = new ServerConfigData();
        private static bool _loaded;

        /// <summary>
        /// Load <c>config/ServerMOTD.json</c>, creating it with defaults on first
        /// run. The caller decides WHEN this runs: dedicated servers call it at
        /// setup; listen-server hosts call it via
        /// <see cref="Plugin.ApplyHostServerConfigOnce"/> when hosting starts.
        /// Pure clients never call it, so they never get a stray ServerMOTD.json.
        ///
        /// <paramref name="hostDefaults"/> only affects first-run file creation:
        /// when true (listen host), screens+queue are seeded ON to preserve the
        /// prior listen-server behaviour; when false (dedicated), they stay OFF
        /// (opt-in). An existing file is always honoured as-is regardless.
        /// </summary>
        public static void Load(bool hostDefaults = false)
        {
            if (_loaded) return;
            _loaded = true;

            string configDir = ConfigPaths.ConfigDir();
            string dllDir    = ConfigPaths.DllDir();
            string jsonPath  = Path.Combine(configDir, "ServerMOTD.json");
            string iniPath   = Path.Combine(dllDir,    "server_config.ini");

            // Migrate from older filenames (server_config.json) and older
            // locations (next to the mod DLL). New canonical name is
            // ServerMOTD.json under <puck-root>/config/.
            if (!File.Exists(jsonPath))
            {
                string[] candidates = new[]
                {
                    Path.Combine(configDir, "server_config.json"),
                    Path.Combine(dllDir,    "ServerMOTD.json"),
                    Path.Combine(dllDir,    "server_config.json"),
                };
                foreach (string candidate in candidates)
                {
                    if (candidate == jsonPath) continue;
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        File.Copy(candidate, jsonPath, false);
                        Plugin.Log("Migrated " + Path.GetFileName(candidate) + " → " + jsonPath);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogError("Failed to migrate " + candidate + ": " + ex.Message);
                    }
                }
            }

            if (File.Exists(jsonPath))
            {
                try
                {
                    string raw = File.ReadAllText(jsonPath);
                    var parsed = JsonUtility.FromJson<ServerConfigData>(raw);
                    if (parsed != null) _data = parsed;
                    if (string.IsNullOrWhiteSpace(_data.motd_url))
                        _data.motd_url = "https://poncepuck.net/motd/";
                    if (_data.browse_links == null || _data.browse_links.Count == 0)
                        _data.browse_links = DefaultBrowseLinks();
                    Plugin.Log("Server config loaded from " + jsonPath
                               + " (screens=" + _data.screens_enabled
                               + ", queue=" + _data.queue_enabled
                               + ", browse_links=" + _data.browse_links.Count
                               + ", motd_url=" + _data.motd_url + ").");

                    // Schema backfill: a file written by an older build won't have
                    // newer fields (e.g. browse_links). Rewrite it so the full
                    // current schema — with sensible defaults — is present for the
                    // admin to edit. Parsed values (incl. manual edits) are kept.
                    if (!raw.Contains("browse_links"))
                    {
                        Save(jsonPath);
                        Plugin.Log("Backfilled new fields into ServerMOTD.json.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Failed to parse ServerMOTD.json: " + ex.Message);
                }
                return;
            }

            // First run: seed defaults, then let a legacy .ini override if present.
            // Listen hosts seed screens+queue ON (preserving prior behaviour);
            // dedicated servers keep the class-default OFF.
            if (hostDefaults)
            {
                _data.screens_enabled = true;
                _data.queue_enabled = true;
            }
            if (File.Exists(iniPath))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(iniPath))
                    {
                        string l = line.Trim();
                        if (l.StartsWith("#") || !l.Contains("=")) continue;
                        int eq = l.IndexOf('=');
                        string key = l.Substring(0, eq).Trim().ToLowerInvariant();
                        string val = l.Substring(eq + 1).Trim();
                        switch (key)
                        {
                            case "screens_enabled":
                                _data.screens_enabled = !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                                break;
                            case "queue_enabled":
                                _data.queue_enabled = !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                                break;
                        }
                    }
                    Plugin.Log("Migrated legacy server_config.ini → " + jsonPath + ".");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Failed to migrate server_config.ini: " + ex.Message);
                }
            }

            Save(jsonPath);
        }

        /// <summary>
        /// Force a fresh read from disk. Used when a listen host (re)starts a
        /// lobby so edits made between sessions apply without a game restart.
        /// </summary>
        public static void Reload(bool hostDefaults = false)
        {
            _loaded = false;
            Load(hostDefaults);
        }

        private static void Save(string path)
        {
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(_data, true));
                Plugin.Log("Wrote " + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Plugin.LogError("Failed to write ServerMOTD.json: " + ex.Message);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Client-only config
    // ────────────────────────────────────────────────────────────────

    [Serializable]
    public class ClientConfigData
    {
        public float volume = 0.5f;
        public bool muted = false;
        public bool screens_disabled = false;
        public float zoom = 1.0f;
        public List<string> trusted_sites = new List<string>();
    }

    /// <summary>
    /// Per-user UI settings plus the trusted-sites allowlist, stored in a
    /// single file at &lt;puck-root&gt;/config/ClientMOTD.json. Never created
    /// on dedicated servers. Replaces the old motd_settings.ini +
    /// trusted_sites.txt pair; legacy files are migrated on first run.
    /// </summary>
    public static class ClientConfig
    {
        public static float Volume
        {
            get { EnsureLoaded(); return _data.volume; }
            set { EnsureLoaded(); _data.volume = value; Save(); }
        }

        public static bool Muted
        {
            get { EnsureLoaded(); return _data.muted; }
            set { EnsureLoaded(); _data.muted = value; Save(); }
        }

        public static bool ScreensDisabled
        {
            get { EnsureLoaded(); return _data.screens_disabled; }
            set { EnsureLoaded(); _data.screens_disabled = value; Save(); }
        }

        public static float Zoom
        {
            get { EnsureLoaded(); return _data.zoom; }
            set { EnsureLoaded(); _data.zoom = value; Save(); }
        }

        public static bool IsTrusted(string domain)
        {
            EnsureLoaded();
            return _trustedSet.Contains(domain);
        }

        public static void AddTrusted(string domain)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(domain)) return;
            if (_trustedSet.Add(domain))
            {
                _data.trusted_sites.Add(domain);
                Save();
            }
        }

        public static IReadOnlyCollection<string> TrustedSites
        {
            get { EnsureLoaded(); return _data.trusted_sites; }
        }

        /// <summary>
        /// Persist all four UI settings in a single disk write. Cheaper than
        /// setting each property individually (each setter would Save() on its own).
        /// </summary>
        public static void SaveSettings(float volume, bool muted, bool screensDisabled, float zoom)
        {
            EnsureLoaded();
            _data.volume           = Mathf.Clamp01(volume);
            _data.muted            = muted;
            _data.screens_disabled = screensDisabled;
            _data.zoom             = Mathf.Clamp(zoom, 0.5f, 2.0f);
            Save();
        }

        private static ClientConfigData _data = new ClientConfigData();
        private static HashSet<string> _trustedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _path;
        private static bool _loaded;

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            // No client config on dedicated servers.
            if (Plugin.IsDedicatedServer()) return;

            string configDir = ConfigPaths.ConfigDir();
            string dllDir    = ConfigPaths.DllDir();
            _path            = Path.Combine(configDir, "ClientMOTD.json");
            string iniPath   = Path.Combine(dllDir,    "motd_settings.ini");
            string trustPath = Path.Combine(dllDir,    "trusted_sites.txt");

            // Migrate from older filenames (client_config.json) and older
            // locations (next to the mod DLL). New canonical name is
            // ClientMOTD.json under <puck-root>/config/.
            if (!File.Exists(_path))
            {
                string[] candidates = new[]
                {
                    Path.Combine(configDir, "client_config.json"),
                    Path.Combine(dllDir,    "ClientMOTD.json"),
                    Path.Combine(dllDir,    "client_config.json"),
                };
                foreach (string candidate in candidates)
                {
                    if (candidate == _path) continue;
                    if (!File.Exists(candidate)) continue;
                    try
                    {
                        File.Copy(candidate, _path, false);
                        Plugin.Log("Migrated " + Path.GetFileName(candidate) + " → " + _path);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogError("Failed to migrate " + candidate + ": " + ex.Message);
                    }
                }
            }

            if (File.Exists(_path))
            {
                try
                {
                    string raw = File.ReadAllText(_path);
                    var parsed = JsonUtility.FromJson<ClientConfigData>(raw);
                    if (parsed != null) _data = parsed;
                    _data.volume = Mathf.Clamp01(_data.volume);
                    _data.zoom   = Mathf.Clamp(_data.zoom, 0.5f, 2.0f);
                    if (_data.trusted_sites == null)
                        _data.trusted_sites = new List<string>();
                    Plugin.Log("Client config loaded (" + _data.trusted_sites.Count + " trusted sites).");
                }
                catch (Exception ex)
                {
                    Plugin.LogError("Failed to parse ClientMOTD.json: " + ex.Message);
                }
            }
            else
            {
                MigrateLegacyIni(iniPath);
                MigrateLegacyTrusted(trustPath);
                Save(); // materialize the consolidated JSON right away
            }

            _trustedSet = new HashSet<string>(_data.trusted_sites ?? new List<string>(),
                                              StringComparer.OrdinalIgnoreCase);
        }

        private static void MigrateLegacyIni(string iniPath)
        {
            if (!File.Exists(iniPath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(iniPath))
                {
                    string l = line.Trim();
                    if (l.StartsWith("#") || !l.Contains("=")) continue;
                    int eq = l.IndexOf('=');
                    string key = l.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = l.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "volume":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                                _data.volume = Mathf.Clamp01(v);
                            break;
                        case "muted":
                            _data.muted = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "screens_disabled":
                            _data.screens_disabled = val.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "zoom":
                            if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                                _data.zoom = Mathf.Clamp(z, 0.5f, 2.0f);
                            break;
                    }
                }
                Plugin.Log("Migrated legacy motd_settings.ini → ClientMOTD.json.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("Failed to migrate motd_settings.ini: " + ex.Message);
            }
        }

        private static void MigrateLegacyTrusted(string trustPath)
        {
            if (!File.Exists(trustPath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(trustPath))
                {
                    string d = line.Trim();
                    if (string.IsNullOrEmpty(d) || d.StartsWith("#")) continue;
                    if (!_data.trusted_sites.Contains(d))
                        _data.trusted_sites.Add(d);
                }
                Plugin.Log("Migrated " + _data.trusted_sites.Count + " trusted sites → ClientMOTD.json.");
            }
            catch (Exception ex)
            {
                Plugin.LogError("Failed to migrate trusted_sites.txt: " + ex.Message);
            }
        }

        private static void Save()
        {
            if (Plugin.IsDedicatedServer()) return;
            if (_path == null)
                _path = Path.Combine(ConfigPaths.ConfigDir(), "ClientMOTD.json");
            try
            {
                File.WriteAllText(_path, JsonUtility.ToJson(_data, true));
            }
            catch (Exception ex)
            {
                Plugin.LogError("Failed to write ClientMOTD.json: " + ex.Message);
            }
        }
    }
}
