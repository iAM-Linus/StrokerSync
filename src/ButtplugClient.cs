using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace StrokerSync
{
    /// <summary>
    /// WebSocket client for the Buttplug protocol (spec v3) via Intiface Central.
    /// Sends Buttplug Ping messages when MaxPingTime > 0 to prevent server disconnects.
    /// </summary>
    public class ButtplugClient
    {
        private const int BUTTPLUG_MESSAGE_VERSION = 3;

        private TinyWebSocket _webSocket;
        private string _serverUrl;
        private string _clientName;

        private int _nextMessageId = 1;

        private readonly Dictionary<int, Action<JSONNode>> _pendingCallbacks = new Dictionary<int, Action<JSONNode>>();
        private readonly Dictionary<int, float> _callbackTimestamps = new Dictionary<int, float>();
        private readonly object _callbackLock = new object();
        private float _callbackCleanupTimer;
        private const float CALLBACK_TTL = 10f;           // Expire callbacks after 10 seconds
        private const float CALLBACK_CLEANUP_INTERVAL = 5f; // Sweep every 5 seconds

        private readonly Queue<JSONNode> _incomingMessages = new Queue<JSONNode>();
        private readonly object _incomingLock = new object();

        // Buttplug protocol ping
        private float _pingInterval;
        private float _pingTimer;

        // Sampled error logging for high-frequency commands (#16)
        private int _linearCmdCount;
        private int _linearCmdErrorCount;
        private const int LINEAR_CMD_SAMPLE_INTERVAL = 100; // Log every Nth command's error response

        public Action<JSONNode> OnDeviceAdded;
        public Action<JSONNode> OnDeviceRemoved;
        public Action<string> OnError;
        public Action OnDisconnected;

        public bool IsConnected { get; private set; }
        public string ServerName { get; private set; }
        public int MaxPingTime { get; private set; }

        public ButtplugClient(string clientName = "StrokerSync")
        {
            _clientName = clientName;
        }

        public IEnumerator Connect(string serverUrl, Action<bool, string> callback)
        {
            _serverUrl = serverUrl;
            IsConnected = false;

            _webSocket = new TinyWebSocket(_serverUrl);

            _webSocket.OnMessage = (data) =>
            {
                try
                {
                    var json = JSON.Parse(data);
                    if (json != null && json is JSONArray)
                    {
                        for (int i = 0; i < json.Count; i++)
                        {
                            lock (_incomingLock)
                            {
                                _incomingMessages.Enqueue(json[i]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SuperController.LogError("StrokerSync: JSON parse error - " + ex.Message);
                }
            };

            _webSocket.OnClose = () =>
            {
                bool wasConnected = IsConnected;
                IsConnected = false;
                if (wasConnected)
                {
                    lock (_incomingLock)
                    {
                        _incomingMessages.Enqueue(null);
                    }
                }
            };

            _webSocket.OnError = (errMsg) =>
            {
                SuperController.LogError("StrokerSync: WebSocket error - " + errMsg);
            };

            bool connectionAttempted = false;
            string connectionError = null;

            Thread connectThread = new Thread(() =>
            {
                try { _webSocket.Connect(); }
                catch (Exception ex) { connectionError = ex.Message; }
                connectionAttempted = true;
            });
            connectThread.IsBackground = true;
            connectThread.Start();

            float timeout = 5f;
            while (!connectionAttempted && timeout > 0)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (!_webSocket.IsOpen)
            {
                callback?.Invoke(false, "Failed to connect to Intiface Central. " + (connectionError ?? "Unknown error."));
                yield break;
            }

            yield return PerformHandshake(callback);
        }

        private IEnumerator PerformHandshake(Action<bool, string> callback)
        {
            int msgId = GetNextId();
            string handshakeJson = "[{\"RequestServerInfo\":{\"Id\":" + msgId + ",\"ClientName\":\"" + _clientName + "\",\"MessageVersion\":" + BUTTPLUG_MESSAGE_VERSION + "}}]";

            bool gotResponse = false;
            bool success = false;
            string error = null;

            RegisterCallback(msgId, (response) =>
            {
                if (response["ServerInfo"] != null)
                {
                    var serverInfo = response["ServerInfo"];
                    ServerName = serverInfo["ServerName"].Value;
                    MaxPingTime = serverInfo["MaxPingTime"].AsInt;
                    IsConnected = true;
                    success = true;

                    if (MaxPingTime > 0)
                    {
                        _pingInterval = (MaxPingTime / 1000f) * 0.4f;
                        _pingTimer = _pingInterval;
                        SuperController.LogMessage($"StrokerSync: Server ping required every {MaxPingTime}ms, sending every {_pingInterval:F1}s");
                    }
                    else
                    {
                        _pingInterval = 0f;
                    }
                }
                else if (response["Error"] != null)
                {
                    error = response["Error"]["ErrorMessage"].Value;
                }
                gotResponse = true;
            });

            SendMessage(handshakeJson);

            float timeout = 5f;
            while (!gotResponse && timeout > 0)
            {
                timeout -= Time.deltaTime;
                ProcessIncomingMessages();
                yield return null;
            }

            if (!gotResponse)
                error = "Handshake timeout";

            callback?.Invoke(success, error);
        }

        /// <summary>
        /// Call from Update() every frame. Handles incoming messages and keepalive pings.
        /// </summary>
        public void ProcessIncomingMessages()
        {
            if (_pingInterval > 0 && IsConnected)
            {
                _pingTimer -= Time.deltaTime;
                if (_pingTimer <= 0f)
                {
                    _pingTimer = _pingInterval;
                    SendButtplugPing();
                }
            }

            // Periodic sweep: remove callbacks that have been waiting longer than TTL.
            // Prevents unbounded memory growth if the server never responds to a message.
            _callbackCleanupTimer -= Time.deltaTime;
            if (_callbackCleanupTimer <= 0f)
            {
                _callbackCleanupTimer = CALLBACK_CLEANUP_INTERVAL;
                CleanupStaleCallbacks();
            }

            lock (_incomingLock)
            {
                while (_incomingMessages.Count > 0)
                {
                    var message = _incomingMessages.Dequeue();
                    if (message == null)
                    {
                        OnDisconnected?.Invoke();
                        continue;
                    }
                    HandleMessage(message);
                }
            }
        }

        private void CleanupStaleCallbacks()
        {
            float now = Time.time;
            List<int> expired = null;

            lock (_callbackLock)
            {
                foreach (var kvp in _callbackTimestamps)
                {
                    if (now - kvp.Value > CALLBACK_TTL)
                    {
                        if (expired == null) expired = new List<int>();
                        expired.Add(kvp.Key);
                    }
                }
                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                    {
                        _pendingCallbacks.Remove(expired[i]);
                        _callbackTimestamps.Remove(expired[i]);
                    }
                }
            }

            if (expired != null && expired.Count > 0)
                SuperController.LogMessage($"StrokerSync: Cleaned up {expired.Count} expired callback(s)");
        }

        private void SendButtplugPing()
        {
            if (!IsConnected || _webSocket == null || !_webSocket.IsOpen) return;

            int msgId = GetNextId();
            string json = "[{\"Ping\":{\"Id\":" + msgId + "}}]";

            RegisterCallback(msgId, (response) =>
            {
                if (response["Error"] != null)
                    SuperController.LogError("StrokerSync: Ping error - " + response["Error"]["ErrorMessage"].Value);
            });

            SendMessage(json);
        }

        public IEnumerator StartScanning(Action<string> callback = null)
        {
            int msgId = GetNextId();
            string json = "[{\"StartScanning\":{\"Id\":" + msgId + "}}]";
            bool gotResponse = false;
            string error = null;
            RegisterCallback(msgId, (r) => { if (r["Error"] != null) error = r["Error"]["ErrorMessage"].Value; gotResponse = true; });
            SendMessage(json);
            float timeout = 5f;
            while (!gotResponse && timeout > 0) { timeout -= Time.deltaTime; ProcessIncomingMessages(); yield return null; }
            callback?.Invoke(error);
        }

        public IEnumerator StopScanning(Action<string> callback = null)
        {
            int msgId = GetNextId();
            string json = "[{\"StopScanning\":{\"Id\":" + msgId + "}}]";
            bool gotResponse = false;
            string error = null;
            RegisterCallback(msgId, (r) => { if (r["Error"] != null) error = r["Error"]["ErrorMessage"].Value; gotResponse = true; });
            SendMessage(json);
            float timeout = 3f;
            while (!gotResponse && timeout > 0) { timeout -= Time.deltaTime; ProcessIncomingMessages(); yield return null; }
            callback?.Invoke(error);
        }

        public IEnumerator RequestDeviceList(Action<JSONNode, string> callback)
        {
            int msgId = GetNextId();
            string json = "[{\"RequestDeviceList\":{\"Id\":" + msgId + "}}]";
            bool gotResponse = false;
            JSONNode deviceList = null;
            string error = null;
            RegisterCallback(msgId, (r) => { if (r["DeviceList"] != null) deviceList = r["DeviceList"]; else if (r["Error"] != null) error = r["Error"]["ErrorMessage"].Value; gotResponse = true; });
            SendMessage(json);
            float timeout = 5f;
            while (!gotResponse && timeout > 0) { timeout -= Time.deltaTime; ProcessIncomingMessages(); yield return null; }
            if (!gotResponse) error = "Device list request timeout";
            callback?.Invoke(deviceList, error);
        }

        public void SendLinearCmd(int deviceIndex, float position, int durationMs)
        {
            if (!IsConnected || _webSocket == null || !_webSocket.IsOpen) return;
            int msgId = GetNextId();
            string posStr = Mathf.Clamp01(position).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string json = "[{\"LinearCmd\":{\"Id\":" + msgId + ",\"DeviceIndex\":" + deviceIndex + ",\"Vectors\":[{\"Index\":0,\"Duration\":" + durationMs + ",\"Position\":" + posStr + "}]}}]";

            // Sample every Nth command to track errors without flooding callbacks.
            // Most commands are fire-and-forget; this catches persistent device errors.
            _linearCmdCount++;
            if (_linearCmdCount % LINEAR_CMD_SAMPLE_INTERVAL == 0)
            {
                RegisterCallback(msgId, (response) =>
                {
                    if (response["Error"] != null)
                    {
                        _linearCmdErrorCount++;
                        SuperController.LogMessage($"StrokerSync: LinearCmd error ({_linearCmdErrorCount} errors in {_linearCmdCount} cmds): " +
                            response["Error"]["ErrorMessage"].Value);
                    }
                });
            }

            SendMessage(json);
        }

        public void SendVibrateCmd(int deviceIndex, float speed)
        {
            if (!IsConnected || _webSocket == null || !_webSocket.IsOpen) return;
            int msgId = GetNextId();
            string speedStr = Mathf.Clamp01(speed).ToString(System.Globalization.CultureInfo.InvariantCulture);
            string json = "[{\"ScalarCmd\":{\"Id\":" + msgId + ",\"DeviceIndex\":" + deviceIndex + ",\"Scalars\":[{\"Index\":0,\"Scalar\":" + speedStr + ",\"ActuatorType\":\"Vibrate\"}]}}]";
            SendMessage(json);
        }

        public void SendStopDeviceCmd(int deviceIndex)
        {
            if (!IsConnected || _webSocket == null || !_webSocket.IsOpen) return;
            int msgId = GetNextId();
            SendMessage("[{\"StopDeviceCmd\":{\"Id\":" + msgId + ",\"DeviceIndex\":" + deviceIndex + "}}]");
        }

        public void SendStopAllDevices()
        {
            if (!IsConnected || _webSocket == null || !_webSocket.IsOpen) return;
            int msgId = GetNextId();
            SendMessage("[{\"StopAllDevices\":{\"Id\":" + msgId + "}}]");
        }

        public IEnumerator Disconnect()
        {
            IsConnected = false;
            _pingInterval = 0f;
            if (_webSocket != null && _webSocket.IsOpen)
            {
                SendStopAllDevices();
                yield return new WaitForSeconds(0.1f);
                _webSocket.Close();
            }
            _webSocket = null;
            lock (_callbackLock) { _pendingCallbacks.Clear(); _callbackTimestamps.Clear(); }
            lock (_incomingLock) _incomingMessages.Clear();
        }

        private int GetNextId() { return Interlocked.Increment(ref _nextMessageId); }

        private void RegisterCallback(int id, Action<JSONNode> callback)
        {
            lock (_callbackLock)
            {
                _pendingCallbacks[id] = callback;
                _callbackTimestamps[id] = Time.time;
            }
        }

        private void SendMessage(string json)
        {
            if (_webSocket == null || !_webSocket.IsOpen) return;
            try { _webSocket.Send(json); }
            catch (Exception ex) { SuperController.LogError("StrokerSync: Send error - " + ex.Message); }
        }

        private void HandleMessage(JSONNode message)
        {
            var messageClass = message as JSONClass;
            if (messageClass == null) return;

            foreach (KeyValuePair<string, JSONNode> kvp in messageClass)
            {
                var key = kvp.Key;
                var body = kvp.Value;
                int id = body["Id"].AsInt;

                switch (key)
                {
                    case "ServerInfo":
                    case "Ok":
                    case "Error":
                    case "DeviceList":
                    case "ScanningFinished":
                        Action<JSONNode> callback = null;
                        lock (_callbackLock)
                        {
                            if (_pendingCallbacks.TryGetValue(id, out callback))
                            {
                                _pendingCallbacks.Remove(id);
                                _callbackTimestamps.Remove(id);
                            }
                        }
                        callback?.Invoke(message);
                        break;
                    case "DeviceAdded":
                        OnDeviceAdded?.Invoke(body);
                        break;
                    case "DeviceRemoved":
                        OnDeviceRemoved?.Invoke(body);
                        break;
                }
            }
        }
    }
}