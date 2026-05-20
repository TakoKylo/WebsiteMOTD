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

        // Monotonically increasing per-item ID assigned on the server when an item
        // becomes _current. Used to dedupe video-ended signals (a single video can be
        // reported "ended" by multiple sources — overlay WebView, world screen, every
        // client — and we must advance exactly once per item). URL alone can't dedupe
        // because two players can legitimately queue the same URL back-to-back.
        public long Seq;
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
        private static long _itemSeqCounter;       // server-side monotonic source for QueueItem.Seq
        private static long _endedForSeq;          // deduplicates video-ended signals (per-item, not per-URL)

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

        private void Setup()
        {
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

            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                Log("NetworkManager not available yet, deferring setup.");
                return;
            }

            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;

            if (nm.IsConnectedClient || nm.IsServer)
                InitializeMessaging();
        }

        private void Teardown()
        {
            _isSetup = false;
            _messagingInitialized = false;
            _queue.Clear();
            _current = null;
            _lastLoadedWorldUrl = null;
            _endedForSeq = 0;
            _itemSeqCounter = 0;
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
                MOTDUI.Hide();
                MOTDWorldScreen.DestroyScreens();
            }

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
                    if (screensEnabled)
                    {
                        MOTDWorldScreen.SpawnScreens();
                        LoadCurrentOnWorldScreens();
                    }
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
            if (!_serverQueueEnabled) return;
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

        /// <summary>Toggle vote-skip on the currently playing item.</summary>
        public static void ToggleVoteSkip()
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            if (nm.IsServer)
                ServerHandleVote(nm.LocalClientId);
            else
                SendScreenMsg("vote");
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
        /// without waiting on votes. No-op for non-owners.
        /// </summary>
        public static void OwnerVetoCurrent()
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            if (!IsLocalOwnerOfCurrent()) return;

            if (nm.IsServer)
                ServerOwnerVeto(nm.LocalClientId);
            else
                SendScreenMsg("veto");
        }

        /// <summary>Remove one of your queued items.</summary>
        public static void RemoveFromQueue(int index)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            if (nm.IsServer)
                ServerRemoveItem(nm.LocalClientId, index);
            else
                SendScreenMsg("remove:" + index);
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
        /// Server advances the queue immediately; clients send a signal to the server.
        /// On a listen server the overlay WebView and the world-screen WebView can both
        /// fire this for the same video — and remote clients may pile on with their own
        /// "ended" messages. Dedupe by the current item's Seq.
        /// </summary>
        public static void VideoEnded()
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;
            if (nm.IsServer)
            {
                ServerTryAdvanceForEnded();
            }
            else
            {
                SendScreenMsg("ended");
            }
        }

        /// <summary>Server-side dedupe gate for ended-driven advances. Returns whether the queue advanced.</summary>
        private static bool ServerTryAdvanceForEnded()
        {
            long seq = _current != null ? _current.Seq : 0;
            if (seq == 0 || seq == _endedForSeq) return false;
            _endedForSeq = seq;
            Log("Video ended (seq=" + seq + ") — advancing queue.");
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
            foreach (ulong clientId in nm.ConnectedClientsIds)
                SendScreenMsgToInternal(clientId, msgBytes);
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
                    else if (msg == "vote")
                    {
                        ServerHandleVote(senderClientId);
                    }
                    else if (msg == "veto")
                    {
                        ServerOwnerVeto(senderClientId);
                    }
                    else if (msg == "ended")
                    {
                        // Dedupe routes through ServerTryAdvanceForEnded — same gate the
                        // server-local VideoEnded path uses, so listen-server hosts don't
                        // double-advance when both their own webview and a remote client
                        // report the same video ending.
                        if (ServerTryAdvanceForEnded())
                            Log("Client " + senderClientId + " reported video ended.");
                    }
                    else if (msg.StartsWith("remove:"))
                    {
                        if (int.TryParse(msg.Substring(7), out int idx))
                            ServerRemoveItem(senderClientId, idx);
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

        private static void ServerAddItem(ulong clientId, string username, string url)
        {
            // Source-of-truth for whether the queue is enabled is _serverQueueEnabled;
            // see SendMOTDToClient for why we don't read ServerConfig directly here.
            if (!_serverQueueEnabled) return;
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            // The allowlist still flows through ServerConfig — its class default already
            // contains youtube/twitch/etc., so listen servers get a sensible allowlist
            // without needing the file. Dedicated admins can override via JSON.
            if (!ServerConfig.IsQueueUrlAllowed(url))
            {
                string allowed = string.Join(", ", ServerConfig.QueueAllowedSites);
                string msg = "Queue only accepts URLs from: " + allowed;
                Log("Rejected queue URL from client " + clientId + " (" + url + ") — not in allowlist.");

                // On a listen server the rejected client IS the host, so
                // routing through the network layer would bounce to the
                // server-side message branch and drop the err. Short-circuit.
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
                return;
            }

            var item = new QueueItem
            {
                ClientId = clientId,
                Username = username ?? ("Player " + clientId),
                Url = url
            };

            if (_current == null)
            {
                item.Seq = ++_itemSeqCounter;
                _current = item;
                Log("Now playing (seq=" + item.Seq + "): " + username + " → " + url);
                LoadCurrentOnWorldScreens();
            }
            else
            {
                _queue.Add(item);
                Log("Queued: " + username + " → " + url + " (pos " + _queue.Count + ")");
            }

            ServerBroadcastQueueState();
        }

        private static void ServerHandleVote(ulong clientId)
        {
            if (_current == null) return;

            if (_current.VoteSkippers.Contains(clientId))
                _current.VoteSkippers.Remove(clientId);
            else
                _current.VoteSkippers.Add(clientId);

            ServerBroadcastQueueState();
            ServerCheckVoteSkip();
        }

        /// <summary>
        /// Owner-only skip on the server side. The sender must be the player who
        /// queued the current item — otherwise the request is ignored.
        /// </summary>
        private static void ServerOwnerVeto(ulong clientId)
        {
            if (_current == null) return;
            if (_current.ClientId != clientId) return;
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

        private static void ServerRemoveItem(ulong clientId, int index)
        {
            if (index < 0 || index >= _queue.Count) return;
            // Only the owner can remove their own items
            if (_queue[index].ClientId != clientId) return;

            _queue.RemoveAt(index);
            ServerBroadcastQueueState();
        }

        private static void ServerPlayNext()
        {
            if (_queue.Count == 0)
            {
                _current = null;
                Log("Queue empty — falling back to MOTD URL.");
            }
            else
            {
                _current = _queue[0];
                _queue.RemoveAt(0);
                _current.Seq = ++_itemSeqCounter;
                Log("Now playing (seq=" + _current.Seq + "): " + _current.Username + " → " + _current.Url);
            }
            // NOTE: _endedForSeq is intentionally NOT cleared here. It tracks the last
            // item we advanced past, so late "ended" signals for the previous item are
            // ignored and don't double-advance through the new _current.
            LoadCurrentOnWorldScreens();
            ServerBroadcastQueueState();
        }

        private static void LoadCurrentOnWorldScreens()
        {
            // Dedicated servers have no renderer — skip entirely
            if (IsDedicatedServer()) return;

            // Server disabled level screens for this lobby
            if (!_serverScreensEnabled) return;

            MOTDWorldScreen.SpawnScreens();
            string url = _current != null ? _current.Url : MOTD_URL;

            // Only reload if the URL actually changed — otherwise every queue
            // broadcast (e.g. vote toggles) would restart the current video.
            if (url == _lastLoadedWorldUrl) return;
            string prevWorldUrl = _lastLoadedWorldUrl;
            _lastLoadedWorldUrl = url;
            MOTDWorldScreen.LoadOnAllScreens(url);

            // Also navigate the overlay WebView, but ONLY if the user is currently
            // viewing the page that the world screens were just showing (or has
            // nothing loaded yet). Otherwise an active browse gets yanked back to
            // the queue URL every time someone votes or a video advances.
            if (MOTDUI.IsVisible && MOTDUI.ShouldFollowWorldScreen(prevWorldUrl))
                MOTDUI.NavigateTo(url, addToHistory: false);
        }

        private static void ServerBroadcastQueueState()
        {
            string serialized = SerializeQueueState();
            BroadcastScreenMsg("state:" + serialized);

            // Also update our own cached state (server needs it for UI + world screen loading)
            ParseQueueState(serialized);
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
        //   cur|clientId|username|url|voter1,voter2,...|seq
        //   qu|clientId|username|url
        //   qu|clientId|username|url
        //
        // "cur" line omitted when nothing is playing.
        // URLs and usernames are base64-encoded to avoid delimiter collisions.
        // The trailing seq field is server-internal but ships in the broadcast so
        // the server can re-parse its own state without losing the dedupe key.

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
                sb.Append('|').Append(_current.Seq).Append('\n');
            }
            foreach (var item in _queue)
            {
                sb.Append("qu|").Append(item.ClientId).Append('|')
                  .Append(B64(item.Username)).Append('|')
                  .Append(B64(item.Url)).Append('\n');
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
                    if (parts.Length >= 6 && long.TryParse(parts[5], out long seq))
                        item.Seq = seq;
                    _current = item;
                }
                else if (kind == "qu")
                {
                    _queue.Add(new QueueItem
                    {
                        ClientId = cid,
                        Username = username,
                        Url = url
                    });
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
}
