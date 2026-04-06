using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;

namespace StrokerSync
{
    /// <summary>
    /// Manages connection to Intiface Central and device lifecycle.
    /// Auto-reconnects on connection loss.
    /// </summary>
    public class IntifaceConnectionManager
    {
        private ButtplugClient _client;
        private MonoBehaviour _coroutineRunner;

        // Device tracking
        private int _handyDeviceIndex = -1;
        private string _handyDeviceName;
        private List<DeviceInfo> _connectedDevices = new List<DeviceInfo>();

        // Connection state
        public bool IsConnected => _client != null && _client.IsConnected;
        public bool HasDevice => _handyDeviceIndex >= 0;
        public string DeviceName => _handyDeviceName ?? "None";
        public bool HasAnyVibrator { get {
            foreach (var d in _connectedDevices) if (d.HasVibrate) return true;
            return false;
        }}
        public string ServerName => _client?.ServerName ?? "Not connected";
        public List<DeviceInfo> ConnectedDevices => _connectedDevices;

        // Auto-reconnect
        private string _lastServerUrl;
        private bool _autoReconnectEnabled;
        private bool _isReconnecting;
        private float _reconnectTimer;
        private int _reconnectAttempts;
        private const float RECONNECT_DELAY_BASE = 2f;  // Start at 2 seconds
        private const float RECONNECT_DELAY_MAX = 30f;   // Cap at 30 seconds
        private const int RECONNECT_MAX_ATTEMPTS = 10;

        // Callbacks for UI updates
        public Action<string> OnStatusChanged;

        // For SendPosition (throttled version) - kept for backward compat
        private float _lastSentPosition = 0f;
        private float _lastSendTime = 0f;
        private const float MIN_SEND_INTERVAL = 0.016f;
        private const float MIN_POSITION_CHANGE = 0.003f;

        public struct DeviceInfo
        {
            public int DeviceIndex;
            public string DeviceName;
            public bool HasLinear;
            public bool HasVibrate;
            public bool IsHandy;
        }

        public IntifaceConnectionManager(MonoBehaviour coroutineRunner)
        {
            _coroutineRunner = coroutineRunner;
        }

        public void Connect(string serverUrl, Action<bool, string> callback)
        {
            _lastServerUrl = serverUrl;
            _autoReconnectEnabled = true;
            _reconnectAttempts = 0;
            _isReconnecting = false;

            _client = new ButtplugClient("StrokerSync");
            SetupClientCallbacks();

            _coroutineRunner.StartCoroutine(ConnectCoroutine(serverUrl, callback));
        }

        private void SetupClientCallbacks()
        {
            _client.OnDeviceAdded = OnDeviceAdded;
            _client.OnDeviceRemoved = OnDeviceRemoved;
            _client.OnDisconnected = () =>
            {
                bool hadDevice = _handyDeviceIndex >= 0;
                string deviceName = _handyDeviceName;

                _handyDeviceIndex = -1;
                _handyDeviceName = null;
                _connectedDevices.Clear();

                if (_autoReconnectEnabled && !_isReconnecting)
                {
                    SuperController.LogMessage($"StrokerSync: Connection lost{(hadDevice ? $" (was using {deviceName})" : "")}. Will auto-reconnect...");
                    OnStatusChanged?.Invoke("Connection lost. Reconnecting...");
                    _reconnectTimer = GetReconnectDelay();
                }
                else
                {
                    SuperController.LogMessage("StrokerSync: Disconnected from Intiface Central");
                }
            };
        }

        private IEnumerator ConnectCoroutine(string serverUrl, Action<bool, string> callback)
        {
            bool connectSuccess = false;
            string connectError = null;

            yield return _client.Connect(serverUrl, (success, error) =>
            {
                connectSuccess = success;
                connectError = error;
            });

            if (!connectSuccess)
            {
                callback?.Invoke(false, connectError ?? "Failed to connect to Intiface Central");
                yield break;
            }

            SuperController.LogMessage($"StrokerSync: Connected to {_client.ServerName}");
            _reconnectAttempts = 0; // Reset on successful connection

            yield return RefreshDeviceList();

            yield return _client.StartScanning((error) =>
            {
                if (error != null)
                    SuperController.LogMessage($"StrokerSync: Scanning note - {error}");
            });

            AutoSelectHandy();

            string statusMsg = HasDevice
                ? $"Connected to {_handyDeviceName}"
                : $"Connected to Intiface ({_connectedDevices.Count} devices). No Handy found.";

            callback?.Invoke(true, statusMsg);
        }

        /// <summary>
        /// Silent reconnect attempt (no user callback, just logs + status updates).
        /// </summary>
        private IEnumerator ReconnectCoroutine()
        {
            _isReconnecting = true;
            _reconnectAttempts++;

            SuperController.LogMessage($"StrokerSync: Reconnect attempt {_reconnectAttempts}/{RECONNECT_MAX_ATTEMPTS}...");
            OnStatusChanged?.Invoke($"Reconnecting (attempt {_reconnectAttempts})...");

            // Dispose old client to stop its receive thread and free resources.
            // Without this, the old client's background thread keeps running as a ghost.
            if (_client != null)
            {
                yield return _client.Disconnect();
                _client = null;
            }

            // Create fresh client
            _client = new ButtplugClient("StrokerSync");
            SetupClientCallbacks();

            bool connectSuccess = false;
            string connectError = null;

            yield return _client.Connect(_lastServerUrl, (success, error) =>
            {
                connectSuccess = success;
                connectError = error;
            });

            if (!connectSuccess)
            {
                SuperController.LogMessage($"StrokerSync: Reconnect failed - {connectError}");
                _isReconnecting = false;

                if (_reconnectAttempts >= RECONNECT_MAX_ATTEMPTS)
                {
                    SuperController.LogMessage("StrokerSync: Max reconnect attempts reached. Use the Connect button to try again.");
                    OnStatusChanged?.Invoke("Reconnection failed. Click Connect to retry.");
                    _autoReconnectEnabled = false;
                }
                else
                {
                    float delay = GetReconnectDelay();
                    OnStatusChanged?.Invoke($"Reconnect failed. Retrying in {delay:F0}s...");
                    _reconnectTimer = delay;
                }
                yield break;
            }

            // Reconnected successfully
            _isReconnecting = false;
            _reconnectAttempts = 0;

            SuperController.LogMessage($"StrokerSync: Reconnected to {_client.ServerName}");

            yield return RefreshDeviceList();

            yield return _client.StartScanning((error) =>
            {
                if (error != null)
                    SuperController.LogMessage($"StrokerSync: Scanning note - {error}");
            });

            AutoSelectHandy();

            string status = HasDevice
                ? $"Reconnected! Device: {_handyDeviceName}"
                : $"Reconnected. Waiting for device...";
            SuperController.LogMessage($"StrokerSync: {status}");
            OnStatusChanged?.Invoke(status);
        }

        private float GetReconnectDelay()
        {
            // Exponential backoff: 2s, 4s, 8s, 16s, 30s, 30s...
            float delay = RECONNECT_DELAY_BASE * Mathf.Pow(2f, _reconnectAttempts);
            return Mathf.Min(delay, RECONNECT_DELAY_MAX);
        }

        public IEnumerator RefreshDeviceList()
        {
            if (_client == null || !_client.IsConnected)
                yield break;

            yield return _client.RequestDeviceList((deviceList, error) =>
            {
                if (error != null)
                {
                    SuperController.LogError($"StrokerSync: Device list error - {error}");
                    return;
                }

                if (deviceList == null) return;

                _connectedDevices.Clear();
                var devices = deviceList["Devices"];
                if (devices == null || !(devices is JSONArray)) return;

                for (int i = 0; i < devices.Count; i++)
                {
                    var info = ParseDeviceInfo(devices[i]);
                    _connectedDevices.Add(info);
                    SuperController.LogMessage($"StrokerSync: Found [{info.DeviceIndex}] {info.DeviceName} " +
                        $"(Linear:{info.HasLinear}, Vibrate:{info.HasVibrate}, Handy:{info.IsHandy})");
                }
            });
        }

        public void SelectDevice(int deviceIndex)
        {
            foreach (var device in _connectedDevices)
            {
                if (device.DeviceIndex == deviceIndex)
                {
                    _handyDeviceIndex = device.DeviceIndex;
                    _handyDeviceName = device.DeviceName;
                    SuperController.LogMessage($"StrokerSync: Selected: {_handyDeviceName} (index {_handyDeviceIndex})");
                    return;
                }
            }
            SuperController.LogError($"StrokerSync: Device index {deviceIndex} not found");
        }

        public void AutoSelectHandy()
        {
            foreach (var device in _connectedDevices)
            {
                if (device.IsHandy)
                {
                    _handyDeviceIndex = device.DeviceIndex;
                    _handyDeviceName = device.DeviceName;
                    SuperController.LogMessage($"StrokerSync: Auto-selected Handy: {_handyDeviceName}");
                    return;
                }
            }

            foreach (var device in _connectedDevices)
            {
                if (device.HasLinear)
                {
                    _handyDeviceIndex = device.DeviceIndex;
                    _handyDeviceName = device.DeviceName;
                    SuperController.LogMessage($"StrokerSync: Auto-selected linear: {_handyDeviceName}");
                    return;
                }
            }
        }

        /// <summary>
        /// Send position directly with no internal throttling.
        /// The caller (StrokerSync) is responsible for rate limiting.
        /// </summary>
        public void SendPositionDirect(float position, int durationMs)
        {
            if (!HasDevice || _client == null || !_client.IsConnected)
                return;

            _client.SendLinearCmd(_handyDeviceIndex, Mathf.Clamp01(position), Mathf.Max(durationMs, 10));
            _lastSentPosition = position;
        }

        /// <summary>
        /// Send position with internal throttling (for callers without own rate limiting).
        /// </summary>
        public void SendPosition(float position, int durationMs)
        {
            if (!HasDevice || _client == null || !_client.IsConnected)
                return;

            float now = Time.time;
            if ((now - _lastSendTime) < MIN_SEND_INTERVAL)
                return;

            float clampedPos = Mathf.Clamp01(position);
            if (Mathf.Abs(clampedPos - _lastSentPosition) < MIN_POSITION_CHANGE)
                return;

            _client.SendLinearCmd(_handyDeviceIndex, clampedPos, Mathf.Max(durationMs, 10));
            _lastSentPosition = clampedPos;
            _lastSendTime = now;
        }

        public void SendPositionWithVelocity(float position, float velocity)
        {
            if (!HasDevice) return;

            float distance = Mathf.Abs(position - _lastSentPosition);
            float duration = LaunchUtils.PredictMoveDuration(distance, Mathf.Max(velocity, 0.05f));
            int durationMs = Mathf.Clamp((int)(duration * 1000f), 20, 5000);

            SendPosition(position, durationMs);
        }

        public void SendVibrate(float speed)
        {
            if (!HasDevice || _client == null || !_client.IsConnected) return;
            _client.SendVibrateCmd(_handyDeviceIndex, speed);
        }

        /// <summary>
        /// Broadcasts a vibration command to every connected device that reports
        /// vibration capability. intensity is clamped 0–1.
        /// </summary>
        public void SendVibrateAll(float intensity)
        {
            if (_client == null || !_client.IsConnected) return;
            float clamped = Mathf.Clamp01(intensity);
            foreach (var device in _connectedDevices)
                if (device.HasVibrate)
                    _client.SendVibrateCmd(device.DeviceIndex, clamped);
        }

        public void StopDevice()
        {
            if (_client != null && _client.IsConnected && _handyDeviceIndex >= 0)
                _client.SendStopDeviceCmd(_handyDeviceIndex);
        }

        public void Update()
        {
            if (_client != null && _client.IsConnected)
            {
                // ProcessIncomingMessages now also handles Buttplug protocol pings
                _client.ProcessIncomingMessages();
            }

            // Auto-reconnect timer
            if (_autoReconnectEnabled && !_isReconnecting && !IsConnected && _reconnectTimer > 0f)
            {
                _reconnectTimer -= Time.deltaTime;
                if (_reconnectTimer <= 0f)
                {
                    _coroutineRunner.StartCoroutine(ReconnectCoroutine());
                }
            }
        }

        public List<string> GetDeviceChoices()
        {
            var choices = new List<string> { "None" };
            foreach (var device in _connectedDevices)
                choices.Add($"[{device.DeviceIndex}] {device.DeviceName}");
            return choices;
        }

        public void Destroy()
        {
            _autoReconnectEnabled = false; // Don't reconnect after intentional disconnect

            if (_client != null)
            {
                StopDevice();
                SendVibrateAll(0f); // Ensure all vibrators stop
                _coroutineRunner.StartCoroutine(_client.Disconnect());
            }
            _handyDeviceIndex = -1;
            _connectedDevices.Clear();
        }

        #region Private Methods

        private DeviceInfo ParseDeviceInfo(JSONNode device)
        {
            var info = new DeviceInfo
            {
                DeviceIndex = device["DeviceIndex"].AsInt,
                DeviceName = device["DeviceName"].Value,
                HasLinear = false,
                HasVibrate = false,
                IsHandy = false
            };

            string nameLower = info.DeviceName.ToLowerInvariant();
            info.IsHandy = nameLower.Contains("handy") || nameLower.Contains("ohd_");

            var messages = device["DeviceMessages"];
            if (messages != null)
            {
                if (messages["LinearCmd"] != null)
                    info.HasLinear = true;
                if (messages["ScalarCmd"] != null)
                {
                    var scalarActuators = messages["ScalarCmd"];
                    if (scalarActuators is JSONArray)
                    {
                        for (int j = 0; j < scalarActuators.Count; j++)
                        {
                            if (scalarActuators[j]["ActuatorType"]?.Value == "Vibrate")
                                info.HasVibrate = true;
                        }
                    }
                    else
                    {
                        info.HasVibrate = true;
                    }
                }
            }

            return info;
        }

        private void OnDeviceAdded(JSONNode device)
        {
            var info = ParseDeviceInfo(device);
            _connectedDevices.Add(info);
            SuperController.LogMessage($"StrokerSync: Device added: [{info.DeviceIndex}] {info.DeviceName}");

            if (_handyDeviceIndex < 0 && (info.IsHandy || info.HasLinear))
            {
                _handyDeviceIndex = info.DeviceIndex;
                _handyDeviceName = info.DeviceName;
                SuperController.LogMessage($"StrokerSync: Auto-selected: {info.DeviceName}");
            }
        }

        private void OnDeviceRemoved(JSONNode device)
        {
            int removedIndex = device["DeviceIndex"].AsInt;
            _connectedDevices.RemoveAll(d => d.DeviceIndex == removedIndex);

            if (removedIndex == _handyDeviceIndex)
            {
                SuperController.LogMessage($"StrokerSync: Device disconnected: {_handyDeviceName}");
                _handyDeviceIndex = -1;
                _handyDeviceName = null;
                AutoSelectHandy();
            }
        }

        #endregion
    }
}