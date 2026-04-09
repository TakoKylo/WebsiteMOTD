using System;
using System.Text;
using Steamworks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace WebsiteMOTD
{
    public class Plugin : IPuckMod
    {
        public static string MOD_NAME = "WebsiteMOTD";
        public static string MOD_VERSION = "1.0.0";

        /// <summary>
        /// The URL shown in the MOTD overlay when clients connect.
        /// Server sends this to every joining client.
        /// </summary>
        public static string MOTD_URL = "https://poncepuck.net/rules/";

        private static string MESSAGE_CHANNEL = "motd-webpage";

        private static bool _isSetup = false;

        public bool OnEnable()
        {
            Log("Enabling v" + MOD_VERSION + "...");
            try
            {
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

            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                // Not connected yet — subscribe to listen for when we do connect
                Log("NetworkManager not available yet, deferring setup.");
                // We'll get called again via OnClientConnected
            }

            // Hook connection events so we can (re)initialize messaging each session
            if (nm != null)
            {
                nm.OnClientConnectedCallback += OnClientConnected;
                nm.OnClientDisconnectCallback += OnClientDisconnected;

                // Already connected? Initialize immediately
                if (nm.IsConnectedClient || nm.IsServer)
                {
                    InitializeMessaging();
                }
            }
        }

        private void Teardown()
        {
            _isSetup = false;
            _messagingInitialized = false;

            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                nm.OnClientConnectedCallback -= OnClientConnected;
                nm.OnClientDisconnectCallback -= OnClientDisconnected;

                try
                {
                    nm.CustomMessagingManager?.UnregisterNamedMessageHandler(MESSAGE_CHANNEL);
                }
                catch { }
            }

            if (!IsDedicatedServer())
            {
                MOTDUI.Hide();
            }

            MOTDWebContent.Cleanup();
        }

        // ─── Connection callbacks ────────────────────────────────────

        private void OnClientConnected(ulong clientId)
        {
            Log("Client " + clientId + " connected.");
            InitializeMessaging();

            // SERVER: send the MOTD URL to the newly connected client
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                SendMOTDToClient(clientId);
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && clientId == nm.LocalClientId)
            {
                Log("Local client disconnected, cleaning up.");
                _messagingInitialized = false;

                if (!IsDedicatedServer())
                {
                    MOTDUI.Hide();
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

            nm.CustomMessagingManager.RegisterNamedMessageHandler(MESSAGE_CHANNEL, OnMessageReceived);
            _messagingInitialized = true;
            Log("Messaging initialized on channel '" + MESSAGE_CHANNEL + "'.");
        }

        /// <summary>
        /// SERVER → CLIENT: send the MOTD URL.
        /// </summary>
        private void SendMOTDToClient(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm?.CustomMessagingManager == null) return;

            byte[] urlBytes = Encoding.UTF8.GetBytes(MOTD_URL);
            int size = sizeof(ushort) + urlBytes.Length;

            using (var writer = new FastBufferWriter(size, Allocator.Temp))
            {
                ushort len = (ushort)urlBytes.Length;
                writer.WriteValueSafe(len);
                writer.WriteBytesSafe(urlBytes);

                nm.CustomMessagingManager.SendNamedMessage(
                    MESSAGE_CHANNEL,
                    clientId,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced
                );
            }

            Log("Sent MOTD URL to client " + clientId + ".");
        }

        /// <summary>
        /// CLIENT: received the MOTD URL from the server → show the overlay.
        /// </summary>
        private static void OnMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                reader.ReadValueSafe(out ushort len);
                byte[] urlBytes = new byte[len];
                reader.ReadBytesSafe(ref urlBytes, len);

                string url = Encoding.UTF8.GetString(urlBytes);
                Log("Received MOTD URL from server: " + url);

                if (!IsDedicatedServer())
                {
                    MOTDUI.Show(url);
                }
            }
            catch (Exception ex)
            {
                LogError("Failed to process MOTD message: " + ex);
            }
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
    }
}
