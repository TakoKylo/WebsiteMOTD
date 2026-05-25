using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace WebsiteMOTD
{
    /// <summary>
    /// A single item in the shared world-screen queue.
    /// </summary>
    public class QueueItem
    {
        public ulong ClientId;
        public string Username;
        public string Url;
        public List<ulong> VoteSkippers = new List<ulong>();

        /// <summary>
        /// Monotonically increasing, server-assigned identifier. Stable for the
        /// lifetime of the item (assigned at add, never changes). Used as the
        /// dedupe key for client→server remove requests so they target a specific
        /// item even if its queue position has shifted in between.
        /// </summary>
        public long Id;

        /// <summary>
        /// Server-broadcast: seconds elapsed since this item started playing as
        /// the current video. Filled at serialization time; on clients, written
        /// during state parse so the initial-load path can seek to the right
        /// position (mid-join sync). Only meaningful for the "cur" item, and
        /// only at the moment the broadcast was emitted.
        /// </summary>
        public float StartOffsetSec;
    }

    public class Plugin : IPuckPlugin
    {
        public static string MOD_NAME = "WebsiteMOTD";
        public static string MOD_VERSION = "1.1.2";

        private Harmony _harmony;

        /// <summary>
        /// The URL shown in the MOTD overlay when clients connect.
        /// On a dedicated server this is overwritten from ServerConfig
        /// at setup time; on clients the server pushes the value via
        /// the MOTD message and it's updated then.
        /// </summary>
        public static string MOTD_URL = "https://poncepuck.net/motd/";

        private static string MESSAGE_CHANNEL = "motd-webpage";
        private static string SCREEN_CHANNEL = "motd-screen";

        // ─── Queue State (server-authoritative, mirrored on clients) ───
        // Server holds the source of truth in _queue / _current.
        // Clients receive periodic broadcasts and cache the same list for UI display.
        private static readonly List<QueueItem> _queue = new List<QueueItem>();
        private static QueueItem _current;
        private static string _lastLoadedWorldUrl; // prevents reloading on every state broadcast
        private static long _nextItemId;           // server-side monotonic source for QueueItem.Id
        // unscaledTimeAsDouble — *not* float. After ~24-48 h of process uptime the
        // float form loses sub-second precision, breaking the ended-debounce gate
        // and the per-client add cooldown on long-running dedicated servers.
        private static double _lastAdvanceTimeUT;  // last ServerPlayNext — debounces "ended"
        // Time.unscaledTimeAsDouble when _current became active. Used to broadcast an
        // elapsed offset so mid-join clients can seek into the video where it is
        // actually playing, instead of starting from 0:00. Reset on every queue
        // advance (and the very first ServerAddItem). Server-only.
        private static double _currentStartedAtUT;

        // ─── Server-side queue limits (defensive against spam/DoS) ─────
        private const int MaxQueueLength = 50;          // total items across all players
        private const int MaxPerPlayer = 5;             // including the currently-playing one
        private const int MaxUrlLength = 2000;          // characters
        private const float AddCooldownSeconds = 1f;    // per-client add throttle
        // "Ended" debounce: a single video end fires multiple "ended" messages across
        // peers (overlay + world-screen + every remote client). All arrive within ~2s
        // of each other. Any non-ended advance (vote-skip, owner-veto, etc.) resets
        // this timer too, so we never accidentally skip two videos for one event.
        private const float EndedDebounceSeconds = 3f;

        // Per-client add cooldown timestamps (server-only). Double-precision for
        // long-running dedicated servers — see _lastAdvanceTimeUT comment.
        private static readonly Dictionary<ulong, double> _lastAddTimeUT = new Dictionary<ulong, double>();

        // Per-client throttle for the cheaper non-add actions (vote/remove/veto/ended).
        // Without this a misbehaving client can spam vote toggles thousands of times
        // per second; each toggle triggers ServerBroadcastVoteUpdate → broadcasts to
        // every peer, which is a viable amplification DoS even with a single client.
        // Tighter window than AddCooldownSeconds because these are normal-flow actions
        // (a player clicking "skip" or removing items) — 4 per second per client is
        // plenty for real use, well below abuse rate.
        private const float ActionCooldownSeconds = 0.25f;
        private static readonly Dictionary<ulong, double> _lastActionTimeUT = new Dictionary<ulong, double>();

        /// <summary>
        /// Returns true if the client may perform a non-add queue action right now,
        /// updating the per-client timestamp. Returns false if the cooldown is active —
        /// caller should drop the message silently (no err: roundtrip, which would
        /// itself amplify a spam attempt).
        /// </summary>
        private static bool ServerAllowAction(ulong clientId)
        {
            double nowUT = Time.unscaledTimeAsDouble;
            if (_lastActionTimeUT.TryGetValue(clientId, out double lastUT)
                && nowUT - lastUT < ActionCooldownSeconds)
                return false;
            _lastActionTimeUT[clientId] = nowUT;
            return true;
        }

        // Server config flags received by clients (default: all enabled)
        private static bool _serverScreensEnabled = true;
        private static bool _serverQueueEnabled = true;

        /// <summary>Whether the server allows the queue system (readable by UI).</summary>
        public static bool IsQueueEnabled => _serverQueueEnabled;

        /// <summary>Read-only snapshot for the UI.</summary>
        public static IReadOnlyList<QueueItem> Queue => _queue;
        public static QueueItem Current => _current;

        /// <summary>Fires whenever the queue state changes (client or server).</summary>
        public static event Action OnQueueChanged;

        private static bool _isSetup = false;

        public bool OnEnable()
        {
            Log("Enabling v" + MOD_VERSION + "...");
            try
            {
                _harmony = new Harmony("WebsiteMOTD");
                _harmony.PatchAll();
                Log("Harmony patches applied.");

                Setup();
                Log("Enabled!");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to enable: " + ex);
                return false;
            }
        }

        public bool OnDisable()
        {
            try
            {
                Log("Disabling...");
                _harmony?.UnpatchSelf();
                Teardown();
                Log("Disabled!");
                return true;
            }
            catch (Exception ex)
            {
                LogError("Failed to disable: " + ex);
                return false;
            }
        }

        // ─── Setup / Teardown ────────────────────────────────────────

        // Set once when the network half of Setup has wired callbacks. Used as a
        // guard for the retry path so we don't double-register if Setup runs
        // again after NetworkManager becomes available.
        private static bool _networkSetupDone;

        // Spawned only when Setup runs while NetworkManager isn't available yet.
        // Polls each frame and finishes the network half once NM appears.
        private static MOTDSetupRetrier _setupRetrier;

        // Plugin instance pointer for the retrier — needed because Setup is
        // an instance method but the retrier is a separate MonoBehaviour.
        private static Plugin _activeInstance;

        // Server-only periodic work pump (drift heartbeat + per-item deadline
        // safety net). Lives as long as the plugin is enabled; self-checks
        // IsServer each Update so it's a no-op on pure clients.
        private static MOTDServerTicker _serverTicker;
        // Wall-clock-ish next-fire times. Doubles so they don't lose precision
        // on long-running dedicated servers (same reason _lastAdvanceTimeUT is
        // a double — Time.unscaledTime as float degrades past ~24-48 h uptime).
        private const float ServerTickIntervalSec = 8f;

        private void Setup()
        {
            _activeInstance = this;
            if (_isSetup) return;
            _isSetup = true;

            ServerConfig.Load();
            // Server applies its own config locally
            if (IsDedicatedServer())
            {
                _serverScreensEnabled = ServerConfig.ScreensEnabled;
                _serverQueueEnabled   = ServerConfig.QueueEnabled;
                MOTD_URL              = ServerConfig.MotdUrl;
            }
            else
            {
                // Load client config eagerly. Without this, screens spawned by
                // LoadCurrentOnWorldScreens (triggered by the MOTD/state message
                // on connect, before any user interaction) would ignore the saved
                // screens_disabled / volume / muted preferences until the user
                // first opens the overlay.
                MOTDUI.EnsureSettingsLoaded();
                // F2 toggles the overlay. Spawn the poller on clients only —
                // dedicated servers don't render UI. The poller itself is
                // cross-platform; on Linux pressing F2 routes through MOTDUI.Show
                // which falls back to the Steam overlay rather than touching the
                // Windows-only WebView native plugin.
                MOTDUI.EnableHotkeys();
            }

            TrySetupNetwork();
        }

        /// <summary>
        /// Idempotent network-setup step. Registers connection callbacks and the
        /// initial messaging handler once NetworkManager is available. If NM is
        /// still null, spawns a one-shot poller that retries each frame — so a
        /// plugin enabled before Unity's network bootstrap completes still
        /// finishes wiring up on its own instead of staying half-initialized.
        /// </summary>
        internal static void TrySetupNetwork()
        {
            if (_networkSetupDone) return;
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                EnsureSetupRetrier();
                return;
            }

            nm.OnClientConnectedCallback    += _activeInstance.OnClientConnected;
            nm.OnClientDisconnectCallback   += _activeInstance.OnClientDisconnected;

            if (nm.IsConnectedClient || nm.IsServer)
                _activeInstance.InitializeMessaging();

            // Ticker drives the drift-heartbeat broadcast and the per-item
            // max-duration safety net. Cheap to keep running on clients (it
            // self-checks IsServer every Update and bails), so we spawn it
            // unconditionally to cover the listen-server case where the local
            // peer transitions into being the server after creating a lobby.
            EnsureServerTicker();

            _networkSetupDone = true;
            DestroySetupRetrier();
            Log("Network setup complete.");
        }

        private static void EnsureSetupRetrier()
        {
            if (_setupRetrier != null) return;
            try
            {
                var go = new GameObject("MOTD_SetupRetrier");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _setupRetrier = go.AddComponent<MOTDSetupRetrier>();
                Log("NetworkManager not available yet — retrier spawned, will complete setup when it appears.");
            }
            catch (Exception ex) { LogError("EnsureSetupRetrier failed: " + ex.Message); }
        }

        private static void DestroySetupRetrier()
        {
            if (_setupRetrier == null) return;
            try { UnityEngine.Object.Destroy(_setupRetrier.gameObject); }
            catch (Exception ex) { LogError("DestroySetupRetrier failed: " + ex.Message); }
            _setupRetrier = null;
        }

        private static void EnsureServerTicker()
        {
            if (_serverTicker != null) return;
            try
            {
                var go = new GameObject("MOTD_ServerTicker");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _serverTicker = go.AddComponent<MOTDServerTicker>();
            }
            catch (Exception ex) { LogError("EnsureServerTicker failed: " + ex.Message); }
        }

        private static void DestroyServerTicker()
        {
            if (_serverTicker == null) return;
            try { UnityEngine.Object.Destroy(_serverTicker.gameObject); }
            catch (Exception ex) { LogError("DestroyServerTicker failed: " + ex.Message); }
            _serverTicker = null;
        }

        private void Teardown()
        {
            _isSetup = false;
            _networkSetupDone = false;
            _messagingInitialized = false;
            DestroySetupRetrier();
            DestroyServerTicker();
            _queue.Clear();
            _current = null;
            _lastLoadedWorldUrl = null;
            _nextItemId = 0;
            _lastAdvanceTimeUT = 0f;
            _currentStartedAtUT = 0f;
            _lastAddTimeUT.Clear();
            _lastActionTimeUT.Clear();
            _serverScreensEnabled = true;
            _serverQueueEnabled = true;

            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnClientConnectedCallback -= OnClientConnected;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;

                try
                {
                    nm.CustomMessagingManager?.UnregisterNamedMessageHandler(MESSAGE_CHANNEL);
                    nm.CustomMessagingManager?.UnregisterNamedMessageHandler(SCREEN_CHANNEL);
                }
                catch { }
            }

            if (!IsDedicatedServer())
            {
                MOTDUI.DisableHotkeys();
                MOTDUI.Hide();
                MOTDWorldScreen.DestroyScreens();
            }

            // Drop the reflective OWP bridge cache so a re-enable cycle re-resolves
            // it cleanly (defends against OWP being reloaded between sessions).
            TheatreVideoScreenBridge.ResetCachedState();

            MOTDWebContent.Cleanup();
            OnQueueChanged?.Invoke();
        }

        // ─── Connection callbacks ────────────────────────────────────

        private void OnClientConnected(ulong clientId)
        {
            Log("Client " + clientId + " connected.");
            InitializeMessaging();

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                SendMOTDToClient(clientId);
                // Send current queue state to the newcomer
                ServerSendQueueState(clientId);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;

            // SERVER: remove disconnected client's items and votes
            if (nm != null && nm.IsServer)
            {
                bool changed = false;

                // Drop their cooldown entries so a reconnected client doesn't
                // get blocked by their previous session's timestamps.
                _lastAddTimeUT.Remove(clientId);
                _lastActionTimeUT.Remove(clientId);

                // Remove their queued items
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    if (_queue[i].ClientId == clientId)
                    {
                        _queue.RemoveAt(i);
                        changed = true;
                    }
                }

                // Remove their vote from current
                if (_current != null && _current.VoteSkippers.Remove(clientId))
                    changed = true;

                // If current item belongs to the disconnected client, advance
                if (_current != null && _current.ClientId == clientId)
                {
                    Log("Current item owner (client " + clientId + ") disconnected, advancing.");
                    ServerPlayNext();
                    changed = true;
                }

                if (changed)
                {
                    ServerBroadcastQueueState();
                    ServerCheckVoteSkip();
                }
            }

            if (nm != null && clientId == nm.LocalClientId)
            {
                Log("Local client disconnected, cleaning up.");
                _messagingInitialized = false;
                _queue.Clear();
                _current = null;
                _lastLoadedWorldUrl = null;
                OnQueueChanged?.Invoke();

                if (!IsDedicatedServer())
                {
                    MOTDUI.Hide();
                    // Hand the OpenWorld theatre back to OWP's showcase video
                    // — we're no longer receiving queue state, so holding the
                    // claim would just leave the theatre stuck on the last
                    // frame of whatever was playing when we dropped.
                    MOTDWorldScreen.ReleaseTheatreClaim();
                }
            }
        }

        // ─── Messaging ──────────────────────────────────────────────

        private static bool _messagingInitialized = false;

        private void InitializeMessaging()
        {
            if (_messagingInitialized) return;

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null)
            {
                LogError("Cannot initialize messaging: CustomMessagingManager is null.");
                return;
            }

            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MESSAGE_CHANNEL); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(SCREEN_CHANNEL); } catch { }

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MESSAGE_CHANNEL, OnMessageReceived);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(SCREEN_CHANNEL, OnScreenMessage);
            _messagingInitialized = true;
            Log("Messaging initialized.");
        }

        /// <summary>SERVER → CLIENT: send the MOTD URL + server config flags.</summary>
        private void SendMOTDToClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            // Read from the cached statics rather than ServerConfig directly.
            // ServerConfig.Load() only runs on dedicated servers — on a listen-server
            // host its _data is the class default (screens_enabled=false, queue_enabled=false)
            // so reading from it would tell the host's own client "queue disabled" and
            // disable the queue feature on listen lobbies entirely.
            // Setup() seeds the statics from ServerConfig on dedicated; on listen
            // servers they keep the class-level true defaults.
            byte flags = 0;
            if (_serverScreensEnabled) flags |= 0x01;
            if (_serverQueueEnabled)   flags |= 0x02;

            byte[] urlBytes = Encoding.UTF8.GetBytes(MOTD_URL);
            int size = sizeof(uint) + urlBytes.Length + 1;

            // uint length prefix (not ushort): the per-message ushort cap was
            // 65535 bytes, which is fine for a single URL but breaks once the
            // same framing is used by the queue-state channel where a full
            // queue easily exceeds 64 KiB. uint here matches the screen
            // channel framing so both ends stay consistent.
            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((uint)urlBytes.Length);
                writer.WriteBytesSafe(urlBytes);
                writer.WriteValueSafe(flags);

                nm.CustomMessagingManager.SendNamedMessage(
                    MESSAGE_CHANNEL, clientId, writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }

            Log("Sent MOTD URL to client " + clientId + " (screens=" + _serverScreensEnabled + ", queue=" + _serverQueueEnabled + ").");
        }

        /// <summary>CLIENT: received the MOTD URL + server config from the server.</summary>
        private static void OnMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out uint len);
                byte[] urlBytes = new byte[len];
                reader.ReadBytesSafe(ref urlBytes, (int)len);

                // Read server config flags (backwards-compatible: if missing, defaults to all-enabled)
                bool screensEnabled = true;
                bool queueEnabled = true;
                try
                {
                    reader.ReadValueSafe(out byte flags);
                    screensEnabled = (flags & 0x01) != 0;
                    queueEnabled   = (flags & 0x02) != 0;
                }
                catch { }

                // Apply server's config on the client side
                _serverScreensEnabled = screensEnabled;
                _serverQueueEnabled = queueEnabled;

                string url = Encoding.UTF8.GetString(urlBytes);
                Log("Received MOTD URL: " + url + " (screens=" + screensEnabled + ", queue=" + queueEnabled + ")");

                // Cache the server's URL so /web (no arg) opens the right page.
                if (!string.IsNullOrWhiteSpace(url))
                    MOTD_URL = url;

                if (!IsDedicatedServer())
                {
                    MOTDUI.Show(url);
                    // Always call LoadCurrentOnWorldScreens — it handles both the
                    // server-enabled path and the theatre-claim path internally, so
                    // OpenWorld's TheatreVideoScreen still gets content when the
                    // server has level screens disabled.
                    LoadCurrentOnWorldScreens();
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to process MOTD message: " + ex);
            }
        }

        // ─── Public Queue API (called from UI / chat commands) ─────

        /// <summary>Add a URL to the shared queue on behalf of the local player.</summary>
        public static void AddToQueue(string url)
        {
            // Surface a visible error when the queue is server-disabled — otherwise
            // /q chat commands and the queue-panel button would silently no-op. Chat
            // is the universal fallback since the panel itself is hidden when the
            // server has the queue off.
            if (!_serverQueueEnabled)
            {
                if (!IsDedicatedServer())
                {
                    LocalChat("The server has the queue disabled.");
                    MOTDUI.ShowQueueError("The server has the queue disabled.");
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            if (nm.IsServer)
            {
                ServerAddItem(nm.LocalClientId, GetLocalUsername(), url);
            }
            else
            {
                // Include the local username (base64 to avoid the '|' delimiter
                // colliding with usernames that contain it). The server still
                // prefers PlayerManager's view, but uses this as a fallback when
                // the player object hasn't been populated yet — without it, a
                // /q issued during the first frames after join would bake the
                // generic "Player <id>" fallback into the queue permanently.
                SendScreenMsg("add:" + B64(GetLocalUsername()) + "|" + url);
            }
        }

        /// <summary>
        /// Toggle vote-skip on the currently playing item. The current item's id is
        /// captured at click time and bundled in the message; the server validates
        /// the id still matches _current.Id when the message arrives, so a vote
        /// can't accidentally target a different item that advanced into "current"
        /// during the network round-trip.
        /// </summary>
        public static void ToggleVoteSkip()
        {
            if (_current == null) return;
            long targetId = _current.Id;

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            if (nm.IsServer)
                ServerHandleVote(nm.LocalClientId, targetId);
            else
                SendScreenMsg("vote:" + targetId);
        }

        /// <summary>True if the local client queued the currently playing item.</summary>
        public static bool IsLocalOwnerOfCurrent()
        {
            if (_current == null) return false;
            var nm = NetworkManager.Singleton;
            if (nm == null) return false;
            return _current.ClientId == nm.LocalClientId;
        }

        /// <summary>
        /// Owner-only skip: the player who queued the current item can drop it
        /// without waiting on votes. No-op for non-owners. Item id captured at
        /// click time and validated server-side to prevent a stale veto from
        /// dropping a different item that advanced during the round-trip.
        /// </summary>
        public static void OwnerVetoCurrent()
        {
            if (_current == null) return;
            long targetId = _current.Id;

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            if (!IsLocalOwnerOfCurrent()) return;

            if (nm.IsServer)
                ServerOwnerVeto(nm.LocalClientId, targetId);
            else
                SendScreenMsg("veto:" + targetId);
        }

        /// <summary>
        /// Admin-only force-skip: server admins (per Puck's AdminLevel) can
        /// advance the queue past any item without votes or owner consent.
        /// Same id-validation pattern as owner-veto so a stale fskip doesn't
        /// drop a different item that advanced during the round-trip.
        /// No-op when the local player isn't an admin — UI hides the button
        /// in that case too, but the server enforces independently.
        /// </summary>
        public static void AdminForceSkip()
        {
            if (_current == null) return;
            long targetId = _current.Id;

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            if (!IsLocalClientAdmin()) return;

            if (nm.IsServer)
                ServerAdminForceSkip(nm.LocalClientId, targetId);
            else
                SendScreenMsg("fskip:" + targetId);
        }

        /// <summary>True if the local player has Puck admin (AdminLevel &gt; 0).</summary>
        public static bool IsLocalClientAdmin()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null) return false;
                return IsClientAdmin(nm.LocalClientId);
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolve a connected client's admin status via PlayerManager. Reads the
        /// AdminLevel NetworkVariable which is server-authoritative and synced
        /// to all clients, so this returns the right answer on both sides.
        /// </summary>
        private static bool IsClientAdmin(ulong clientId)
        {
            try
            {
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                var player = pm?.GetPlayerByClientId(clientId);
                if (player == null) return false;
                return player.AdminLevel.Value > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Remove one of your queued items by its server-assigned id (see
        /// <see cref="QueueItem.Id"/>). Id is stable across the item's lifetime,
        /// so this targets the right item even if the queue has shifted between
        /// when the UI rendered and when the request arrives.
        /// </summary>
        public static void RemoveFromQueue(long itemId)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            if (nm.IsServer)
                ServerRemoveItem(nm.LocalClientId, itemId);
            else
                SendScreenMsg("remove:" + itemId);
        }

        public static bool HasLocalVotedSkip()
        {
            if (_current == null) return false;
            var nm = NetworkManager.Singleton;
            if (nm == null) return false;
            return _current.VoteSkippers.Contains(nm.LocalClientId);
        }

        public static int GetVoteSkipThreshold()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null) return 1;
            // Majority of connected clients (server counts too)
            int total = nm.ConnectedClientsIds.Count;
            return Mathf.Max(1, (total / 2) + 1);
        }

        /// <summary>
        /// Called when the local client's WebView detects that the current video ended.
        /// <paramref name="reportedItemId"/> is the queue item id the page was bound to
        /// at load time (see __motdItemId injection). Server validates the id matches
        /// _current.Id before advancing — so a stale "ended" message from a video that
        /// was already skipped past can't double-advance the queue, AND a random video
        /// in the overlay (id=0) doesn't affect the queue at all.
        /// </summary>
        public static void VideoEnded(long reportedItemId)
        {
            if (reportedItemId == 0) return; // not a queue item — drop locally too

            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            if (nm.IsServer)
                ServerTryAdvanceForEnded(reportedItemId);
            else
                SendScreenMsg("ended:" + reportedItemId);
        }

        /// <summary>
        /// Server-side gate for ended-driven advances. Two layers of dedupe:
        ///   • Id match — the message reports which item the video belonged to;
        ///     if it's not the active _current, it's a stale event from a past
        ///     item that we've already advanced past. Drop.
        ///   • Time debounce — multiple peers report ended for the SAME video
        ///     within a couple seconds of each other; the time gate coalesces
        ///     them into a single ServerPlayNext call.
        /// Returns whether the queue advanced.
        /// </summary>
        private static bool ServerTryAdvanceForEnded(long reportedItemId)
        {
            if (_current == null) return false;
            if (_current.Id != reportedItemId)
            {
                // Stale: this "ended" is for an item we already moved past.
                return false;
            }
            double elapsed = Time.unscaledTimeAsDouble - _lastAdvanceTimeUT;
            if (elapsed < EndedDebounceSeconds)
            {
                // Same video, multiple peers reporting — coalesce.
                return false;
            }
            Log("Video ended (id=" + _current.Id + ") — advancing queue.");
            ServerPlayNext();
            return true;
        }

        // ─── Screen message transport ───────────────────────────────

        private static void SendScreenMsg(string msg)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            using (var writer = new FastBufferWriter(sizeof(uint) + msgBytes.Length, Allocator.Temp))
            {
                writer.WriteValueSafe((uint)msgBytes.Length);
                writer.WriteBytesSafe(msgBytes);
                nm.CustomMessagingManager.SendNamedMessage(
                    SCREEN_CHANNEL, NetworkManager.ServerClientId,
                    writer, NetworkDelivery.ReliableSequenced);
            }
        }

        private static void BroadcastScreenMsg(string msg)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            ulong localId = nm.LocalClientId;
            foreach (ulong clientId in nm.ConnectedClientsIds)
            {
                // Skip the local client — the host already has the source-of-truth
                // state and gets notified directly via OnQueueChanged. Sending
                // through Netcode just bounces back to ourselves, allocates a
                // FastBufferWriter for nothing, and the receive handler ignores
                // it anyway (the "state:" branch only runs on non-server clients).
                if (clientId == localId) continue;
                SendScreenMsgToInternal(clientId, msgBytes);
            }
        }

        private static void SendScreenMsgTo(ulong clientId, string msg)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            byte[] msgBytes = Encoding.UTF8.GetBytes(msg);
            SendScreenMsgToInternal(clientId, msgBytes);
        }

        private static void SendScreenMsgToInternal(ulong clientId, byte[] msgBytes)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            // uint length prefix: a fully populated queue (MaxQueueLength items
            // × ~2 KB URLs, base64-bloated) easily exceeds 64 KiB. The old
            // ushort cast silently wrapped the length on the wire, leaving
            // receivers parsing a truncated state and silently diverging from
            // the server's authoritative view. uint avoids that ceiling.
            using (var writer = new FastBufferWriter(sizeof(uint) + msgBytes.Length, Allocator.Temp))
            {
                writer.WriteValueSafe((uint)msgBytes.Length);
                writer.WriteBytesSafe(msgBytes);
                nm.CustomMessagingManager.SendNamedMessage(
                    SCREEN_CHANNEL, clientId,
                    writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void OnScreenMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out uint len);
                byte[] msgBytes = new byte[len];
                reader.ReadBytesSafe(ref msgBytes, (int)len);
                string msg = Encoding.UTF8.GetString(msgBytes);

                var nm = NetworkManager.Singleton;
                if (nm == null) return;
                if (nm.IsServer)
                {
                    // Client → Server requests. The non-add actions go through a
                    // shared per-client cooldown so a misbehaving client can't burn
                    // server CPU + broadcast bandwidth toggling votes in a tight
                    // loop. Adds have their own (slower) cooldown checked in
                    // ServerAddItem so the URL-length / allowlist checks still get
                    // to surface their err: feedback to legitimate users.
                    if ((msg.StartsWith("vote:") || msg.StartsWith("veto:")
                         || msg.StartsWith("remove:") || msg.StartsWith("ended:")
                         || msg.StartsWith("fskip:"))
                        && !ServerAllowAction(senderClientId))
                        return;

                    if (msg.StartsWith("add:"))
                    {
                        // Wire formats:
                        //   add:<url>                          (legacy)
                        //   add:<b64-username>|<url>           (current)
                        // PlayerManager is the source of truth for usernames,
                        // but on a freshly-joined client it can briefly return
                        // the "Player <id>" placeholder — in that window the
                        // client-provided name is the only good source.
                        string payload = msg.Substring(4);
                        int pipe = payload.IndexOf('|');
                        string clientProvidedName = null;
                        string url;
                        if (pipe > 0)
                        {
                            clientProvidedName = FromB64(payload.Substring(0, pipe));
                            url = payload.Substring(pipe + 1);
                        }
                        else
                        {
                            url = payload;
                        }
                        string username = GetPlayerName(senderClientId);
                        if ((string.IsNullOrEmpty(username)
                                || username == ("Player " + senderClientId))
                            && !string.IsNullOrEmpty(clientProvidedName))
                        {
                            // Cap to a sane length so a malicious client can't bloat
                            // the queue state with a huge "username".
                            username = clientProvidedName.Length > 32
                                ? clientProvidedName.Substring(0, 32)
                                : clientProvidedName;
                        }
                        ServerAddItem(senderClientId, username, url);
                    }
                    else if (msg.StartsWith("vote:"))
                    {
                        if (long.TryParse(msg.Substring(5), out long voteId))
                            ServerHandleVote(senderClientId, voteId);
                    }
                    else if (msg.StartsWith("veto:"))
                    {
                        if (long.TryParse(msg.Substring(5), out long vetoId))
                            ServerOwnerVeto(senderClientId, vetoId);
                    }
                    else if (msg.StartsWith("fskip:"))
                    {
                        if (long.TryParse(msg.Substring(6), out long fskipId))
                            ServerAdminForceSkip(senderClientId, fskipId);
                    }
                    else if (msg.StartsWith("ended:"))
                    {
                        if (long.TryParse(msg.Substring(6), out long endedId)
                            && ServerTryAdvanceForEnded(endedId))
                        {
                            Log("Client " + senderClientId + " reported video ended (id=" + endedId + ").");
                        }
                    }
                    else if (msg.StartsWith("remove:"))
                    {
                        if (long.TryParse(msg.Substring(7), out long itemId))
                            ServerRemoveItem(senderClientId, itemId);
                    }
                }
                else
                {
                    // Server → Client broadcasts
                    if (msg.StartsWith("state:"))
                    {
                        ParseQueueState(msg.Substring(6));
                        LoadCurrentOnWorldScreens();
                        OnQueueChanged?.Invoke();
                    }
                    else if (msg.StartsWith("tick:"))
                    {
                        // tick:<itemId>:<elapsedSec> — heartbeat for drift sync.
                        // The world screen consults its WebView's actual playback
                        // position and seeks forward if the gap exceeds threshold.
                        string body = msg.Substring(5);
                        int colon = body.IndexOf(':');
                        if (colon > 0
                            && long.TryParse(body.Substring(0, colon), out long tickId)
                            && int.TryParse(body.Substring(colon + 1), out int tickElapsedSec))
                        {
                            MOTDWorldScreen.HandleServerTick(tickId, tickElapsedSec);
                        }
                    }
                    else if (msg.StartsWith("votes:"))
                    {
                        // votes:<itemId>:<voterId,voterId,...> — apply vote delta
                        // in-place. Ignore if it targets a different item than what
                        // we have as current (stale, or we missed a state update —
                        // the next full state broadcast will reconcile).
                        string body = msg.Substring(6);
                        int colon = body.IndexOf(':');
                        if (colon >= 0
                            && long.TryParse(body.Substring(0, colon), out long voteItemId)
                            && _current != null && _current.Id == voteItemId)
                        {
                            _current.VoteSkippers.Clear();
                            string votersCsv = body.Substring(colon + 1);
                            if (!string.IsNullOrEmpty(votersCsv))
                            {
                                foreach (string v in votersCsv.Split(','))
                                    if (ulong.TryParse(v, out ulong voter))
                                        _current.VoteSkippers.Add(voter);
                            }
                            OnQueueChanged?.Invoke();
                        }
                    }
                    else if (msg.StartsWith("err:"))
                    {
                        string err = msg.Substring(4);
                        LocalChat(err);
                        MOTDUI.ShowQueueError(err);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Screen message error: " + ex);
            }
        }

        // ─── Server-side queue logic ────────────────────────────────

        /// <summary>
        /// Surface a queue-rejection error to the client who tried the action.
        /// Short-circuits to local chat + UI for the host (where routing back through
        /// the messaging layer would land on the server branch and drop the err).
        /// </summary>
        private static void SendQueueError(ulong clientId, string msg)
        {
            Log("Queue error (client " + clientId + "): " + msg);
            var nm = NetworkManager.Singleton;
            if (nm != null && clientId == nm.LocalClientId)
            {
                LocalChat(msg);
                if (!IsDedicatedServer()) MOTDUI.ShowQueueError(msg);
            }
            else
            {
                SendScreenMsgTo(clientId, "err:" + msg);
            }
        }

        private static int CountItemsByClient(ulong clientId)
        {
            int n = 0;
            if (_current != null && _current.ClientId == clientId) n++;
            foreach (var q in _queue) if (q.ClientId == clientId) n++;
            return n;
        }

        private static void ServerAddItem(ulong clientId, string username, string url)
        {
            // Source-of-truth for whether the queue is enabled is _serverQueueEnabled;
            // see SendMOTDToClient for why we don't read ServerConfig directly here.
            if (!_serverQueueEnabled)
            {
                SendQueueError(clientId, "The server has the queue disabled.");
                return;
            }
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();

            // URL length cap — defends against accidental paste of giant blobs and
            // intentional bloat that would balloon the state broadcast.
            if (url.Length > MaxUrlLength)
            {
                SendQueueError(clientId, "URL is too long (max " + MaxUrlLength + " characters).");
                return;
            }

            // Per-client add cooldown — light spam protection. Applied before the
            // (slightly more expensive) allowlist check so spammers get throttled
            // even when they're hammering with rejected URLs.
            double nowUT = Time.unscaledTimeAsDouble;
            if (_lastAddTimeUT.TryGetValue(clientId, out double lastUT)
                && nowUT - lastUT < AddCooldownSeconds)
            {
                SendQueueError(clientId, "You're adding too fast — wait a moment.");
                return;
            }

            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            // The allowlist still flows through ServerConfig — its class default already
            // contains youtube/twitch/etc., so listen servers get a sensible allowlist
            // without needing the file. Dedicated admins can override via JSON.
            if (!ServerConfig.IsQueueUrlAllowed(url))
            {
                string allowed = string.Join(", ", ServerConfig.QueueAllowedSites);
                SendQueueError(clientId, "Queue only accepts URLs from: " + allowed);
                return;
            }

            // Global queue cap.
            if (_queue.Count >= MaxQueueLength)
            {
                SendQueueError(clientId, "Queue is full (" + MaxQueueLength + " items max).");
                return;
            }

            // Per-player cap (counts the currently-playing item too — otherwise a
            // player could permanently park one of their videos as _current and still
            // queue MaxPerPlayer more).
            if (CountItemsByClient(clientId) >= MaxPerPlayer)
            {
                SendQueueError(clientId, "You already have " + MaxPerPlayer + " items in the queue.");
                return;
            }

            // All validation passed — commit the add and record the cooldown timestamp.
            _lastAddTimeUT[clientId] = nowUT;

            var item = new QueueItem
            {
                ClientId = clientId,
                Username = username ?? ("Player " + clientId),
                Url = url,
                Id = ++_nextItemId,
            };

            if (_current == null)
            {
                _current = item;
                _currentStartedAtUT = Time.unscaledTimeAsDouble;
                Log("Now playing (id=" + item.Id + "): " + username + " → " + url);
                LoadCurrentOnWorldScreens();
            }
            else
            {
                _queue.Add(item);
                Log("Queued (id=" + item.Id + "): " + username + " → " + url + " (pos " + _queue.Count + ")");
            }

            ServerBroadcastQueueState();
        }

        private static void ServerHandleVote(ulong clientId, long targetItemId)
        {
            if (_current == null) return;
            // Stale: client clicked vote when item A was current, but item B is
            // now current. The user voted on what they saw, not what's playing now —
            // drop the vote rather than misapply it to a different item.
            if (_current.Id != targetItemId) return;

            if (_current.VoteSkippers.Contains(clientId))
                _current.VoteSkippers.Remove(clientId);
            else
                _current.VoteSkippers.Add(clientId);

            // Send only the vote delta — orders of magnitude smaller than a full
            // state broadcast. ServerCheckVoteSkip may still trigger a real
            // advance (which DOES broadcast full state), so the threshold UX
            // stays intact.
            ServerBroadcastVoteUpdate();
            ServerCheckVoteSkip();
        }

        /// <summary>
        /// Owner-only skip on the server side. Validates both that the sender is the
        /// owner of the current item AND that the item id matches what the client
        /// clicked on — guards against a stale veto skipping the wrong item.
        /// </summary>
        private static void ServerOwnerVeto(ulong clientId, long targetItemId)
        {
            if (_current == null) return;
            if (_current.Id != targetItemId) return;     // stale — drop
            if (_current.ClientId != clientId) return;   // not the owner — drop
            Log("Owner " + clientId + " vetoed their own queued item — advancing.");
            ServerPlayNext();
        }

        /// <summary>
        /// Admin-only force-skip on the server side. Bypasses both the owner check
        /// and the vote threshold. Validates that the sender is actually an admin
        /// (we don't trust the message itself — a non-admin client could send the
        /// fskip wire format and we'd refuse) and that the item id matches what
        /// the admin clicked on.
        /// </summary>
        private static void ServerAdminForceSkip(ulong clientId, long targetItemId)
        {
            if (_current == null) return;
            if (_current.Id != targetItemId) return;     // stale — drop
            if (!IsClientAdmin(clientId))
            {
                SendQueueError(clientId, "Force-skip requires admin.");
                return;
            }
            Log("Admin " + clientId + " force-skipped item " + targetItemId + " — advancing.");
            ServerPlayNext();
        }

        private static void ServerCheckVoteSkip()
        {
            if (_current == null) return;

            int needed = GetVoteSkipThreshold();
            if (_current.VoteSkippers.Count >= needed)
            {
                Log("Vote skip passed (" + _current.VoteSkippers.Count + "/" + needed + ").");
                ServerPlayNext();
            }
        }

        /// <summary>
        /// Remove a queued item by its server-assigned <see cref="QueueItem.Id"/>.
        /// Indexes can shift between client→server roundtrips (e.g. when another
        /// player's item plays out), so removing by index would race; the id-based
        /// lookup always targets the intended item.
        /// </summary>
        private static void ServerRemoveItem(ulong clientId, long itemId)
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                var item = _queue[i];
                if (item.Id != itemId) continue;
                if (item.ClientId != clientId) return; // not the owner — silently ignore
                _queue.RemoveAt(i);
                ServerBroadcastQueueState();
                return;
            }
            // Item not found — already played out or removed; no-op.
        }

        private static void ServerPlayNext()
        {
            // Every advance bumps the debounce timer so late "ended" signals for the
            // outgoing video can't accidentally skip the new one too. See
            // ServerTryAdvanceForEnded for the dedupe gate.
            _lastAdvanceTimeUT = Time.unscaledTimeAsDouble;
            // Reset the mid-join start clock too — the next broadcast will report
            // ~0s elapsed, so a player joining right now starts the new video from
            // the beginning rather than seeking based on the previous item's clock.
            _currentStartedAtUT = Time.unscaledTimeAsDouble;

            if (_queue.Count == 0)
            {
                _current = null;
                Log("Queue empty — falling back to MOTD URL.");
            }
            else
            {
                _current = _queue[0];
                _queue.RemoveAt(0);
                Log("Now playing (id=" + _current.Id + "): " + _current.Username + " → " + _current.Url);
            }
            LoadCurrentOnWorldScreens();
            ServerBroadcastQueueState();
        }

        private static void LoadCurrentOnWorldScreens()
        {
            // Dedicated servers have no renderer — skip entirely
            if (IsDedicatedServer()) return;

            // Theatre claim policy: ONLY hold the OpenWorld theatre while we
            // have actual queue content to display. The A/B level screens fall
            // back to showing the MOTD URL when nothing's queued — that's fine
            // for a passive background, but stealing the theatre to show an
            // idle MOTD page wrecks OWP's showcase experience (its default
            // video stops, the WorkingSpeakers prefab goes silent because no
            // VideoPlayer is producing audio samples).
            //
            // Transition into a queue item → claim. Transition out (queue
            // empties, last video ended) → release so OWP's StartDefaultVideo
            // path resumes. Re-claim happens automatically on the next add.
            bool hasQueueContent = _current != null;
            if (hasQueueContent)
                MOTDWorldScreen.EnsureTheatreClaim();
            else
                MOTDWorldScreen.ReleaseTheatreClaim();

            bool hasTheatre = MOTDWorldScreen.HasTheatreScreen;

            if (_serverScreensEnabled)
            {
                // Normal path: spawn our own A/B screens. They handle both
                // the idle MOTD-URL and the active queue-item case.
                MOTDWorldScreen.SpawnScreens();
            }
            else if (hasQueueContent && (hasTheatre || TheatreVideoScreenBridge.ApiPresent))
            {
                // Server disabled level screens AND we have content to push.
                // Spawn a headless driver so:
                //   1. The WebView is ready to pump content to the theatre.
                //   2. The driver's per-frame Update keeps polling the OWP
                //      bridge for a claim. OWP doesn't fire ClaimChanged on
                //      its FIRST attach when there's no surviving claim
                //      (see TheatreVideoScreen.AttachToFound), so the
                //      polling is the only signal we get that the screen
                //      became available.
                MOTDWorldScreen.EnsureDriver();
            }
            else
            {
                // No regular screens AND (no queue content OR no OWP) —
                // nothing to do; let OWP keep its showcase running.
                return;
            }

            string url = _current != null ? _current.Url : MOTD_URL;

            // Only reload if the URL actually changed — otherwise every queue
            // broadcast (e.g. vote toggles) would restart the current video.
            if (url == _lastLoadedWorldUrl) return;
            string prevWorldUrl = _lastLoadedWorldUrl;
            _lastLoadedWorldUrl = url;
            long worldItemId = _current != null ? _current.Id : 0;
            // Apply the server's elapsed-time hint on the load that actually
            // navigates. Returning broadcasts for the same URL short-circuit
            // above, so each client only ever seeks once per item — first time
            // they see this URL as current, which is the mid-join case.
            int startOffsetSec = _current != null ? Mathf.Max(0, Mathf.FloorToInt(_current.StartOffsetSec)) : 0;
            MOTDWorldScreen.LoadOnAllScreens(url, worldItemId, startOffsetSec);

            // Also navigate the overlay WebView, but ONLY if the user is currently
            // viewing the page that the world screens were just showing (or has
            // nothing loaded yet). Otherwise an active browse gets yanked back to
            // the queue URL every time someone votes or a video advances.
            //
            // Pass the queue item id so the overlay binds videoEnded events to this
            // specific item. Without that, any random video the player browses
            // ending could advance the queue, and stale "ended" messages from past
            // items could double-skip.
            if (MOTDUI.IsVisible && MOTDUI.ShouldFollowWorldScreen(prevWorldUrl))
            {
                long itemId = _current != null ? _current.Id : 0;
                MOTDUI.NavigateTo(url, addToHistory: false, queueItemId: itemId, startOffsetSec: startOffsetSec);
            }
        }

        private static void ServerBroadcastQueueState()
        {
            string serialized = SerializeQueueState();
            BroadcastScreenMsg("state:" + serialized);

            // The server is the source of truth — _queue/_current already reflect
            // what we just serialized. The old code re-parsed our own broadcast,
            // tearing down and rebuilding the same QueueItem objects with the same
            // data; pure waste. Just notify subscribers.
            OnQueueChanged?.Invoke();
        }

        private static void ServerSendQueueState(ulong targetClient)
        {
            string serialized = SerializeQueueState();
            SendScreenMsgTo(targetClient, "state:" + serialized);
        }

        /// <summary>
        /// Lightweight periodic heartbeat used by the drift-correction path.
        /// Carries only the current item id and elapsed seconds since it started
        /// playing — clients use this to detect when their WebView playback has
        /// fallen behind the server's authoritative clock and seek forward.
        /// Cheaper than re-broadcasting the full queue state every few seconds.
        /// </summary>
        internal static void ServerSendTick()
        {
            if (_current == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            int elapsedSec = (int)System.Math.Max(0d, Time.unscaledTimeAsDouble - _currentStartedAtUT);
            BroadcastScreenMsg("tick:" + _current.Id + ":" + elapsedSec);
        }

        /// <summary>
        /// Server-side safety net: if the current item has been "playing" longer
        /// than ServerConfig.MaxItemSeconds, force-advance. Dedicated servers can't
        /// observe video ends themselves (no WebView, no graphics) so they depend
        /// on clients reporting "ended" — and if every client has disconnected,
        /// crashed, or had its ad-block JS misfire, the queue would otherwise
        /// stall on that item forever. Set max_item_seconds to 0 in ServerMOTD.json
        /// to disable.
        /// </summary>
        internal static void ServerCheckItemDeadline()
        {
            if (_current == null) return;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            int maxSec = ServerConfig.MaxItemSeconds;
            if (maxSec <= 0) return;
            if (Time.unscaledTimeAsDouble - _currentStartedAtUT < maxSec) return;
            Log("Item " + _current.Id + " exceeded max duration (" + maxSec + "s) — auto-advancing.");
            ServerPlayNext();
        }

        /// <summary>
        /// Compact delta broadcast for the common vote-toggle case. The full
        /// state serializer would re-emit the entire queue (potentially ~100 KB
        /// with a long, max-URL queue) just to update one voter list; this
        /// sends only the affected item's id and the new voter set. Clients
        /// apply it in-place. Mid-join clients still get the full state via
        /// ServerSendQueueState — this is purely an optimization for the steady
        /// state.
        /// </summary>
        private static void ServerBroadcastVoteUpdate()
        {
            if (_current == null) return;
            var sb = new StringBuilder();
            sb.Append("votes:").Append(_current.Id).Append(':');
            for (int i = 0; i < _current.VoteSkippers.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(_current.VoteSkippers[i]);
            }
            BroadcastScreenMsg(sb.ToString());
            OnQueueChanged?.Invoke();
        }

        // ─── Queue state serialization ──────────────────────────────
        //
        // Format (line-delimited):
        //   sv|b64(motd_url)|flagsByte
        //   cur|clientId|username|url|voter1,voter2,...|id|elapsedSec
        //   qu|clientId|username|url|id
        //   qu|clientId|username|url|id
        //
        // The "sv" header carries the server's MOTD URL and config flags so the
        // world-screen sync path no longer races the MOTD-channel message at
        // connect time — a client receiving state first has everything it needs
        // to render the right URL with the right flags, without waiting on the
        // separate MOTD message to arrive.
        //
        // "cur" line omitted when nothing is playing.
        // URLs and usernames are base64-encoded to avoid delimiter collisions.
        // The id field ships in the broadcast so clients can send remove-by-id
        // requests that target a specific item even after the queue shifts. The
        // trailing elapsedSec lets late-joining clients seek into the video where
        // it is actually playing — older clients that don't know about the field
        // ignore it (Length-checked parse), so the wire stays backwards-compat.

        private static string SerializeQueueState()
        {
            var sb = new StringBuilder();

            // Header line: server config (MOTD url + flags). Always emitted so
            // both the initial state on join and every subsequent broadcast keep
            // clients aligned on the URL/flags without a separate message.
            byte flags = 0;
            if (_serverScreensEnabled) flags |= 0x01;
            if (_serverQueueEnabled)   flags |= 0x02;
            sb.Append("sv|").Append(B64(MOTD_URL ?? "")).Append('|').Append(flags).Append('\n');

            if (_current != null)
            {
                sb.Append("cur|").Append(_current.ClientId).Append('|')
                  .Append(B64(_current.Username)).Append('|')
                  .Append(B64(_current.Url)).Append('|');
                for (int i = 0; i < _current.VoteSkippers.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(_current.VoteSkippers[i]);
                }
                int elapsedSec = (int)System.Math.Max(0d, Time.unscaledTimeAsDouble - _currentStartedAtUT);
                sb.Append('|').Append(_current.Id).Append('|').Append(elapsedSec).Append('\n');
            }
            foreach (var item in _queue)
            {
                sb.Append("qu|").Append(item.ClientId).Append('|')
                  .Append(B64(item.Username)).Append('|')
                  .Append(B64(item.Url)).Append('|')
                  .Append(item.Id).Append('\n');
            }
            return sb.ToString();
        }

        private static void ParseQueueState(string data)
        {
            _queue.Clear();
            _current = null;
            if (string.IsNullOrEmpty(data)) return;

            foreach (string line in data.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                string[] parts = line.Split('|');
                if (parts.Length < 2) continue;

                string kind = parts[0];

                // Server header — handled BEFORE the cur/qu shape check because
                // it has a different layout (parts[1] is a b64 URL, not a ulong
                // client id). Carries MOTD URL + screens/queue enable flags so
                // the world-screen path doesn't need the separate MOTD message.
                if (kind == "sv")
                {
                    if (parts.Length >= 3)
                    {
                        string svUrl = FromB64(parts[1]);
                        if (!string.IsNullOrWhiteSpace(svUrl)) MOTD_URL = svUrl;
                        if (byte.TryParse(parts[2], out byte svFlags))
                        {
                            _serverScreensEnabled = (svFlags & 0x01) != 0;
                            _serverQueueEnabled   = (svFlags & 0x02) != 0;
                        }
                    }
                    continue;
                }

                if (parts.Length < 4) continue;
                if (!ulong.TryParse(parts[1], out ulong cid)) continue;
                string username = FromB64(parts[2]);
                string url = FromB64(parts[3]);

                if (kind == "cur")
                {
                    var item = new QueueItem
                    {
                        ClientId = cid,
                        Username = username,
                        Url = url
                    };
                    if (parts.Length >= 5 && !string.IsNullOrEmpty(parts[4]))
                    {
                        foreach (string v in parts[4].Split(','))
                            if (ulong.TryParse(v, out ulong voter))
                                item.VoteSkippers.Add(voter);
                    }
                    if (parts.Length >= 6 && long.TryParse(parts[5], out long id))
                        item.Id = id;
                    // Optional trailing field added later; older servers omit it
                    // and we just leave StartOffsetSec at 0 (= start from beginning).
                    if (parts.Length >= 7 && int.TryParse(parts[6], out int elapsedSec))
                        item.StartOffsetSec = elapsedSec;
                    _current = item;
                }
                else if (kind == "qu")
                {
                    var item = new QueueItem
                    {
                        ClientId = cid,
                        Username = username,
                        Url = url
                    };
                    if (parts.Length >= 5 && long.TryParse(parts[4], out long id))
                        item.Id = id;
                    _queue.Add(item);
                }
            }
        }

        private static string B64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
        }

        private static string FromB64(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return s; }
        }

        // ─── Helpers ─────────────────────────────────────────────────

        public static bool IsDedicatedServer()
        {
            return SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }

        public static void Log(string message)
        {
            Debug.Log("[" + MOD_NAME + "] " + message);
        }

        public static void LogError(string message)
        {
            Debug.LogError("[" + MOD_NAME + "] " + message);
        }

        /// <summary>Show a system message in the in-game chat UI.</summary>
        public static void LocalChat(string text)
        {
            try
            {
                var cm = ChatManager.Instance;
                if (cm != null)
                {
                    ChatMessage chatMsg = new ChatMessage
                    {
                        SteamID = null,
                        Username = null,
                        Team = null,
                        // ChatMessage.Content is FixedString512Bytes (508 usable
                        // UTF-8 bytes); the implicit string conversion throws on
                        // overflow. Server-pushed err: strings are untrusted and
                        // could exceed that — clamp before assignment.
                        Content = ClampForChatContent("[MOTD] " + (text ?? "")),
                        Timestamp = Utils.GetTimestamp(),
                        IsQuickChat = false,
                        IsTeamChat = false,
                        IsSystem = true
                    };
                    cm.AddChatMessage(chatMsg);
                    return;
                }
                Log("[LocalChat] ChatManager.Instance is null");
            }
            catch (Exception ex)
            {
                LogError("[LocalChat] Error: " + ex);
            }

            Log(text);
        }

        // ChatMessage.Content fits 508 UTF-8 bytes; leave headroom for the ellipsis
        // we append on truncation. Truncates on a UTF-8 codepoint boundary so we
        // don't hand FixedString512Bytes a malformed continuation byte.
        private const int MaxChatBodyBytes = 500;
        private static string ClampForChatContent(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length <= MaxChatBodyBytes) return s;
            int cut = MaxChatBodyBytes;
            // Back up off any UTF-8 continuation byte (10xxxxxx) so we land on a
            // start byte; preserves codepoint integrity.
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;
            return Encoding.UTF8.GetString(bytes, 0, cut) + "…";
        }

        public static string GetLocalUsername()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null) return "Player";
                return GetPlayerName(nm.LocalClientId);
            }
            catch { return "Player"; }
        }

        public static string GetPlayerName(ulong clientId)
        {
            try
            {
                var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (pm != null)
                {
                    var player = pm.GetPlayerByClientId(clientId);
                    if (player != null && player.Username.Value.Length > 0)
                        return player.Username.Value.ToString();
                }
            }
            catch { }
            return "Player " + clientId;
        }
    }

    /// <summary>
    /// Forces <see cref="UIState.IsMouseRequired"/> to true while the MOTD overlay is open.
    /// UIManager.CheckMouseRequirement recomputes the flag from its own UIView list whenever
    /// any game UI's visibility/focus changes, so without this postfix opening or closing
    /// a stock view (chat, scoreboard, etc.) while MOTD is up would re-enable movement
    /// input — the player's stick/keys would feed through the page underneath.
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "CheckMouseRequirement")]
    public static class CheckMouseRequirementPatch
    {
        public static void Postfix()
        {
            if (!MOTDUI.IsAnyOverlayVisible) return;
            if (GlobalStateManager.UIState.IsMouseRequired) return;
            GlobalStateManager.SetUIState(new System.Collections.Generic.Dictionary<string, object>
            {
                { "isMouseRequired", true }
            });
        }
    }

    // ─── Input gating while overlay is open ─────────────────────────
    //
    // IsMouseRequired covers PlayerInput (movement, blade, dash, jump). It does NOT
    // gate the UI-level inputs that fire chat, scoreboard, or pause — those are
    // bound to their own actions and only check IsInteracting or game phase. The
    // MOTD overlay isn't a UIView, so it doesn't contribute to IsInteracting, and
    // those actions fire right through.
    //
    // We can't easily fake a UIView entry, so instead we patch each handler with a
    // prefix that no-ops while the overlay is up. Pause is included so ESC closes
    // the overlay without also toggling the pause menu underneath.

    [HarmonyPatch(typeof(UIManager), "OnAllChatActionPerformed")]
    public static class BlockAllChatPatch
    {
        public static bool Prefix() => !MOTDUI.IsAnyOverlayVisible;
    }

    [HarmonyPatch(typeof(UIManager), "OnTeamChatActionPerformed")]
    public static class BlockTeamChatPatch
    {
        public static bool Prefix() => !MOTDUI.IsAnyOverlayVisible;
    }

    [HarmonyPatch(typeof(UIManager), "OnScoreboardActionStarted")]
    public static class BlockScoreboardStartedPatch
    {
        public static bool Prefix() => !MOTDUI.IsAnyOverlayVisible;
    }

    [HarmonyPatch(typeof(UIManager), "OnScoreboardActionCanceled")]
    public static class BlockScoreboardCanceledPatch
    {
        // Cancellation also needs gating: if the scoreboard never opened (we blocked
        // Started), it shouldn't try to close either. Otherwise harmless, but cleaner.
        public static bool Prefix() => !MOTDUI.IsAnyOverlayVisible;
    }

    [HarmonyPatch(typeof(UIManager), "OnPauseActionPerformed")]
    public static class BlockPausePatch
    {
        public static bool Prefix() => !MOTDUI.IsAnyOverlayVisible;
    }

    /// <summary>
    /// Client-side chat command interceptor.
    /// /web, /motd, /browser   → open the URL in the browser overlay.
    /// /q, /queue               → add the URL to the shared screen queue.
    /// </summary>
    [HarmonyPatch(typeof(ChatManager), "Client_SendChatMessage")]
    public static class ChatCommandPatch
    {
        public static bool Prefix(string content)
        {
            if (string.IsNullOrEmpty(content)) return true;
            string msg = content.Trim();
            if (!msg.StartsWith("/")) return true;

            string[] parts = msg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts[0].ToLowerInvariant();
            string arg = parts.Length > 1 ? parts[1].Trim() : "";

            if (cmd == "/web" || cmd == "/motd" || cmd == "/browser")
            {
                MOTDUI.Show(string.IsNullOrEmpty(arg) ? Plugin.MOTD_URL : arg);
                return false;
            }

            if (cmd == "/q" || cmd == "/queue")
            {
                if (string.IsNullOrEmpty(arg))
                {
                    // No arg: just open the queue UI so they can see / manage it.
                    MOTDUI.Show(Plugin.MOTD_URL);
                }
                else
                {
                    Plugin.AddToQueue(arg);
                }
                return false;
            }

            if (cmd == "/fskip" || cmd == "/forceskip")
            {
                // Admin-only. Server enforces the admin check regardless of what
                // the client sends, so this is just a friendlier UX path than
                // hunting for the button. Surfaces a chat hint when used by a
                // non-admin or with nothing currently queued so they know why
                // nothing happened (silent no-ops on chat commands are confusing).
                if (!Plugin.IsLocalClientAdmin())
                {
                    Plugin.LocalChat("Force-skip requires admin.");
                    return false;
                }
                if (Plugin.Current == null)
                {
                    Plugin.LocalChat("Nothing is currently playing.");
                    return false;
                }
                Plugin.AdminForceSkip();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// One-shot retrier: spawned by <see cref="Plugin.Setup"/> only when
    /// <c>NetworkManager.Singleton</c> isn't available yet at enable time.
    /// Polls every frame and calls <see cref="Plugin.TrySetupNetwork"/> until
    /// the network half of setup actually completes — then self-destructs.
    ///
    /// Without this, a plugin enabled before Unity's network bootstrap finished
    /// would stay half-initialized forever (the original code logged "deferring
    /// setup" but never retried).
    /// </summary>
    internal class MOTDSetupRetrier : MonoBehaviour
    {
        void Update()
        {
            try { Plugin.TrySetupNetwork(); }
            catch (Exception ex) { Plugin.LogError("SetupRetrier: " + ex.Message); }
        }
    }

    /// <summary>
    /// Server-side periodic pump:
    ///   • Per-frame: <see cref="Plugin.ServerCheckItemDeadline"/> — force-advances
    ///     items that have outlived <c>max_item_seconds</c>. Defends against the
    ///     dedicated-server-has-no-WebView case where the server depends entirely
    ///     on clients reporting "ended" — if zero clients are connected (or all
    ///     are blocking the URL), the queue would otherwise stall forever.
    ///   • Every <see cref="Plugin.ServerTickIntervalSec"/>: broadcasts a lightweight
    ///     <c>tick:</c> with the current item's elapsed time so clients can detect
    ///     and correct playback drift without restarting the video.
    /// Self-checks <c>IsServer</c> each frame so it's a free no-op on pure clients.
    /// </summary>
    internal class MOTDServerTicker : MonoBehaviour
    {
        private double _nextTickUT;

        void Update()
        {
            try
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm == null || !nm.IsServer) return;

                Plugin.ServerCheckItemDeadline();

                double now = Time.unscaledTimeAsDouble;
                if (now >= _nextTickUT)
                {
                    _nextTickUT = now + 8.0; // mirrors Plugin.ServerTickIntervalSec
                    Plugin.ServerSendTick();
                }
            }
            catch (Exception ex) { Plugin.LogError("ServerTicker: " + ex.Message); }
        }
    }
}
