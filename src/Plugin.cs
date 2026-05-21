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
    }

    public class Plugin : IPuckPlugin
    {
        public static string MOD_NAME = "WebsiteMOTD";
        public static string MOD_VERSION = "1.0.0";

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
        private static float _lastAdvanceTimeUT;   // Time.unscaledTime of last ServerPlayNext — debounces "ended"

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

        // Per-client add cooldown timestamps (server-only).
        private static readonly Dictionary<ulong, float> _lastAddTimeUT = new Dictionary<ulong, float>();

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

        private void Teardown()
        {
            _isSetup = false;
            _networkSetupDone = false;
            _messagingInitialized = false;
            DestroySetupRetrier();
            _queue.Clear();
            _current = null;
            _lastLoadedWorldUrl = null;
            _nextItemId = 0;
            _lastAdvanceTimeUT = 0f;
            _lastAddTimeUT.Clear();
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

                // Drop their add cooldown entry so a reconnected client doesn't
                // get blocked by their previous session's timestamp.
                _lastAddTimeUT.Remove(clientId);

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
                    MOTDUI.Hide();
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
            int size = sizeof(ushort) + urlBytes.Length + 1;

            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                writer.WriteValueSafe((ushort)urlBytes.Length);
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
                reader.ReadValueSafe(out ushort len);
                byte[] urlBytes = new byte[len];
                reader.ReadBytesSafe(ref urlBytes, len);

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
                SendScreenMsg("add:" + url);
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
            float elapsed = Time.unscaledTime - _lastAdvanceTimeUT;
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
            using (var writer = new FastBufferWriter(sizeof(ushort) + msgBytes.Length, Allocator.Temp))
            {
                writer.WriteValueSafe((ushort)msgBytes.Length);
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

            // ushort length can hold up to 65535 bytes which is plenty for queue state
            using (var writer = new FastBufferWriter(sizeof(ushort) + msgBytes.Length, Allocator.Temp))
            {
                writer.WriteValueSafe((ushort)msgBytes.Length);
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
                reader.ReadValueSafe(out ushort len);
                byte[] msgBytes = new byte[len];
                reader.ReadBytesSafe(ref msgBytes, len);
                string msg = Encoding.UTF8.GetString(msgBytes);

                var nm = NetworkManager.Singleton;
                if (nm == null) return;
                if (nm.IsServer)
                {
                    // Client → Server requests
                    if (msg.StartsWith("add:"))
                    {
                        string url = msg.Substring(4);
                        string username = GetPlayerName(senderClientId);
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
            float nowUT = Time.unscaledTime;
            if (_lastAddTimeUT.TryGetValue(clientId, out float lastUT)
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

            ServerBroadcastQueueState();
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
            _lastAdvanceTimeUT = Time.unscaledTime;

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

            // Always try to claim the OpenWorld TheatreVideoScreen — when present,
            // it should receive the WebView texture regardless of server/client
            // screens settings (cooperative API; see TheatreVideoScreenBridge).
            MOTDWorldScreen.EnsureTheatreClaim();
            bool hasTheatre = MOTDWorldScreen.HasTheatreScreen;

            if (_serverScreensEnabled)
            {
                // Normal path: spawn our own A/B screens.
                MOTDWorldScreen.SpawnScreens();
            }
            else if (hasTheatre)
            {
                // Server disabled level screens, but the theatre screen is claimed —
                // run a headless driver so the WebView still pumps content to it.
                MOTDWorldScreen.EnsureDriver();
            }
            else
            {
                // No regular screens, no theatre claim — nothing to do.
                return;
            }

            string url = _current != null ? _current.Url : MOTD_URL;

            // Only reload if the URL actually changed — otherwise every queue
            // broadcast (e.g. vote toggles) would restart the current video.
            if (url == _lastLoadedWorldUrl) return;
            string prevWorldUrl = _lastLoadedWorldUrl;
            _lastLoadedWorldUrl = url;
            long worldItemId = _current != null ? _current.Id : 0;
            MOTDWorldScreen.LoadOnAllScreens(url, worldItemId);

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
                MOTDUI.NavigateTo(url, addToHistory: false, queueItemId: itemId);
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

        // ─── Queue state serialization ──────────────────────────────
        //
        // Format (line-delimited):
        //   cur|clientId|username|url|voter1,voter2,...|id
        //   qu|clientId|username|url|id
        //   qu|clientId|username|url|id
        //
        // "cur" line omitted when nothing is playing.
        // URLs and usernames are base64-encoded to avoid delimiter collisions.
        // The id field ships in the broadcast so clients can send remove-by-id
        // requests that target a specific item even after the queue shifts.

        private static string SerializeQueueState()
        {
            var sb = new StringBuilder();
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
                sb.Append('|').Append(_current.Id).Append('\n');
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
                if (parts.Length < 4) continue;

                string kind = parts[0];
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
                        Content = "[MOTD] " + text,
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
}
