using System;
using System.Collections.Generic;
using SimpleJSON;
using MVR.FileManagementSecure;
using UnityEngine;
using StrokerSync.MotionSources;

namespace StrokerSync
{
    /// <summary>
    /// StrokerSync - Stroker integration via Intiface Central + Bluetooth
    /// </summary>
    public class StrokerSync : MVRScript
    {
        private static StrokerSync _instance;

        // Connection
        private IntifaceConnectionManager _connectionManager;
        private bool _isInitialized;

        // Configuration storables
        private JSONStorableString _serverUrl;
        private JSONStorableStringChooser _motionSourceChooser;
        private JSONStorableStringChooser _deviceChooser;
        private JSONStorableStringChooser _vibratorChooser;
        private JSONStorableStringChooser _vibrationMode;
        private JSONStorableFloat _vibrationIntensityScale;

        // Vibration state
        private bool _vibrationActive; // True while vibrator is running; used to detect off-edge
        private JSONStorableFloat _vibrationDisplay; // Live intensity readout (display only, not persisted)

        // Quick-access overlay panel
        private OverlayPanel _overlayPanel;
        private JSONStorableBool _pauseStroker;
        private JSONStorableBool _pauseVibration;
        private JSONStorableFloat _simulatorPosition;
        private JSONStorableFloat _strokeZoneMin;
        private JSONStorableFloat _strokeZoneMax;
        private JSONStorableFloat _sendRateHz;
        private JSONStorableFloat _deviceSmoothnessMs;
        // Simulator state
        private float _simulatorTarget;
        private float _simulatorSpeed;

        // Always-active motion sources
        private readonly CombinedSource _combinedSource = new CombinedSource();

        // Tab UI system
        private JSONStorableStringChooser _tabChooser;
        private string                    _activeTab   = "Connection";
        private readonly List<Action>     _tabCleanup  = new List<Action>();

        // UI
        private UIDynamicButton _connectButton;
        private UIDynamicButton _refreshDevicesButton;
        private UIDynamicTextField _statusText;
        private JSONStorableString _statusString;

        private float _lastRawPosSent = -1f;  // Deadband tracked in source space (pre-mapping)
        private float _sendAccumulator;  // Accumulated time toward next send (accumulator pattern)
        private const float POSITION_DEADZONE = 0.005f;
        private bool _loggedFirstSend;

        // Adaptive send rate: scales Hz based on motion velocity.
        // Fast strokes get full configured rate; slow/idle movement sends less often.
        private float _adaptiveVelocity;
        private float _prevMappedPos;
        private float _prevMappedPosTime;
        private const float ADAPTIVE_MIN_HZ = 5f;              // Floor rate when nearly idle
        private const float ADAPTIVE_VELOCITY_THRESHOLD = 0.8f; // pos-units/sec for full rate (full stroke in ~1.2s)
        private const float ADAPTIVE_VELOCITY_SMOOTHING = 0.40f; // EMA retention — higher value reduces physics-spike sensitivity

        // Position extrapolation: send where the device SHOULD BE at the end of
        // the interpolation window, not where the penetration IS right now.
        // This lets the device use the full LinearCmd duration to glide smoothly
        // instead of staircase-jumping between positions.
        private float _signedVelocity;                           // Signed EMA velocity (pos-units/sec)
        private const float EXTRAP_VELOCITY_SMOOTHING = 0.38f;  // EMA retention for extrapolation velocity — reduced for faster reversal tracking
        private float _prevExtrapPos;
        private float _prevExtrapTime;
        private bool _isReversing;   // True when stroke direction just flipped

        // Stroke advance filter: skip same-direction commands that don't push the target
        // meaningfully further. The device completes its current glide uninterrupted;
        // only update when the endpoint advances enough, or reverses.
        // The threshold is DYNAMIC: it scales with the observed stroke amplitude so
        // small tip-only movements still get fine-grained updates.
        private float _lastCommandedTarget = -1f;
        private float _strokePeak   = 0.5f;   // Most recent local maximum (zero-amplitude start → fine threshold until real data arrives)
        private float _strokeValley = 0.5f;   // Most recent local minimum
        private const float STROKE_THRESHOLD_FRACTION = 0.12f; // Threshold = amplitude × this
        private const float STROKE_THRESHOLD_MIN      = 0.02f; // Floor: never coarser than 2 %
        private const float STROKE_THRESHOLD_MAX      = 0.15f; // Ceiling: never finer than 15 %

        // Settings save/load via VAM's FileManagerSecure API
        private static readonly string CONFIG_DIR = "Custom\\Scripts\\StrokerSync";
        private static readonly string CONFIG_PATH = CONFIG_DIR + "\\defaults.json";

        public override void Init()
        {
            if (_instance != null)
            {
                SuperController.LogError("StrokerSync: Only one instance allowed!");
                return;
            }

            if (containingAtom == null || containingAtom.type == "CoreControl")
            {
                SuperController.LogError("StrokerSync: Please add to an in-scene atom!");
                return;
            }

            _instance = this;

            InitStorables();
            LoadDefaults(); // Apply saved defaults before UI is created
            InitUI();
            InitConnectionManager();

            // Start logic for always-active source (no UI yet — tabs do that)
            _combinedSource.OnInit(this);

            // Build overlay after storables are all registered (MaleFemaleSource registers its own in InitStorables)
            _overlayPanel = new OverlayPanel(this, TriggerAutoDetect);
            _overlayPanel.Build();
            _overlayPanel.SetVisible(false); // Hidden until user opens it

            // Listen for scene loads so we can reset cached references (session plugin persistence)
            SuperController.singleton.onSceneLoadedHandlers += OnSceneLoaded;

            SuperController.LogMessage("StrokerSync: Plugin initialized.");
            SuperController.LogMessage("StrokerSync: Make sure Intiface Central is running with your Handy paired.");
        }

        public IntifaceConnectionManager GetConnectionManager()
        {
            return _connectionManager;
        }

        private void InitConnectionManager()
        {
            _connectionManager = new IntifaceConnectionManager(this);

            _connectionManager.OnStatusChanged = (status) =>
            {
                if (_statusString != null)
                    _statusString.val = $"Status: {status}";
                if (_connectButton != null)
                {
                    _connectButton.label = _connectionManager.IsConnected
                        ? "Disconnect"
                        : "Connect to Intiface Central";
                }
            };
        }

        private void InitStorables()
        {
            _serverUrl = new JSONStorableString("serverUrl", "ws://127.0.0.1:12345");
            RegisterString(_serverUrl);

            // Keep "motionSource" registered so old saved scenes don't error on load.
            // It is no longer used functionally — CombinedSource always runs.
            _motionSourceChooser = new JSONStorableStringChooser(
                "motionSource",
                new List<string> { "Combined" },
                "Combined", "Motion Source");
            RegisterStringChooser(_motionSourceChooser);

            _deviceChooser = new JSONStorableStringChooser(
                "device", new List<string> { "None" }, "None", "Device",
                (string name) =>
                {
                    if (name == "None" || _connectionManager == null) return;
                    if (name.StartsWith("["))
                    {
                        int endBracket = name.IndexOf(']');
                        if (endBracket > 1)
                        {
                            string indexStr = name.Substring(1, endBracket - 1);
                            int deviceIndex;
                            if (int.TryParse(indexStr, out deviceIndex))
                                _connectionManager.SelectDevice(deviceIndex);
                        }
                    }
                });
            RegisterStringChooser(_deviceChooser);

            var vibratorChoices = new List<string>
            {
                IntifaceConnectionManager.VIBRATOR_ALL,
                IntifaceConnectionManager.VIBRATOR_NONE
            };
            _vibratorChooser = new JSONStorableStringChooser(
                "vibratorDevice", vibratorChoices, IntifaceConnectionManager.VIBRATOR_ALL, "Vibrator Device",
                (string name) =>
                {
                    if (_connectionManager == null) return;
                    if (name == IntifaceConnectionManager.VIBRATOR_ALL)
                        _connectionManager.SelectVibrator(-2);
                    else if (name == IntifaceConnectionManager.VIBRATOR_NONE)
                        _connectionManager.SelectVibrator(-1);
                    else if (name.StartsWith("["))
                    {
                        int endBracket = name.IndexOf(']');
                        if (endBracket > 1)
                        {
                            string indexStr = name.Substring(1, endBracket - 1);
                            int deviceIndex;
                            if (int.TryParse(indexStr, out deviceIndex))
                                _connectionManager.SelectVibrator(deviceIndex);
                        }
                    }
                });
            RegisterStringChooser(_vibratorChooser);

            _pauseStroker = new JSONStorableBool("pauseStroker", true);
            RegisterBool(_pauseStroker);

            _pauseVibration = new JSONStorableBool("pauseVibration", true, (bool paused) =>
            {
                // When pausing, immediately stop all vibrators so they don't linger.
                if (paused && _vibrationActive && _connectionManager != null)
                {
                    _vibrationActive = false;
                    _vibrationDisplay.val = 0f;
                    _connectionManager.SendVibrateAll(0f);
                }
            });
            RegisterBool(_pauseVibration);

            _simulatorPosition = new JSONStorableFloat("simulatorPosition", 0.0f, 0.0f, 1.0f);
            RegisterFloat(_simulatorPosition);

            _strokeZoneMin = new JSONStorableFloat("strokeZoneMin", 0.0f, 0.0f, 1.0f);
            RegisterFloat(_strokeZoneMin);

            _strokeZoneMax = new JSONStorableFloat("strokeZoneMax", 1.0f, 0.0f, 1.0f);
            RegisterFloat(_strokeZoneMax);

            _sendRateHz = new JSONStorableFloat("sendRateHz", 20f, 5f, 60f);
            RegisterFloat(_sendRateHz);

            // Duration padding: added on top of the predicted send interval.
            // The base duration is computed from the adaptive send rate so the device
            // arrives at each position exactly when the next command fires.
            // This padding adds a small overlap to prevent stop-start gaps.
            _deviceSmoothnessMs = new JSONStorableFloat("deviceSmoothnessMs", 10f, 0f, 100f);
            RegisterFloat(_deviceSmoothnessMs);

            // Vibration — sent to ALL connected vibrating devices simultaneously.
            var vibModes = new List<string> { "Off", "Depth", "Velocity", "Blend" };
            _vibrationMode = new JSONStorableStringChooser("vibrationMode", vibModes, "Off", "Vibration Mode",
                (string mode) =>
                {
                    // When switching to Off, immediately stop all vibrators.
                    if (mode == "Off" && _vibrationActive && _connectionManager != null)
                    {
                        _vibrationActive = false;
                        _vibrationDisplay.val = 0f;
                        _connectionManager.SendVibrateAll(0f);
                    }
                });
            RegisterStringChooser(_vibrationMode);

            _vibrationIntensityScale = new JSONStorableFloat("vibrationIntensityScale", 1.0f, 0.0f, 1.0f);
            RegisterFloat(_vibrationIntensityScale);

            _vibrationDisplay = new JSONStorableFloat("vibrationDisplay", 0f, 0f, 1f);
            RegisterFloat(_vibrationDisplay);

            // Tab chooser must be registered so CreateScrollablePopup can bind to it.
            // It persists the last-open tab across scene loads, which is fine UX.
            _tabChooser = new JSONStorableStringChooser(
                "_uiTab",
                new List<string> { "Connection", "Stroker", "Penetration", "Vibration" },
                "Connection", "Tab");
            RegisterStringChooser(_tabChooser);

            _combinedSource.OnInitStorables(this);
        }

        // =====================================================================
        // SETTINGS SAVE / LOAD
        // =====================================================================

        /// <summary>
        /// Save current settings via VAM's FileManagerSecure API.
        /// for all new scenes. Scene-specific overrides still take priority.
        /// </summary>
        /// <summary>
        /// Save current settings via VAM's FileManagerSecure API.
        /// These become the defaults for all new scenes.
        /// </summary>
        private void SaveDefaults()
        {
            try
            {
                if (!FileManagerSecure.DirectoryExists(CONFIG_DIR))
                    FileManagerSecure.CreateDirectory(CONFIG_DIR);

                var json = new JSONClass();
                json["deviceSmoothnessMs"].AsFloat = _deviceSmoothnessMs.val;
                json["sendRateHz"].AsFloat = _sendRateHz.val;
                json["strokeZoneMin"].AsFloat = _strokeZoneMin.val;
                json["strokeZoneMax"].AsFloat = _strokeZoneMax.val;
                json["serverUrl"] = _serverUrl.val;

                json["maleFemale_Smoothing"].AsFloat = GetFloatParamValue("maleFemale_Smoothing");
                json["maleFemale_ReferenceLengthScale"].AsFloat = GetFloatParamValue("maleFemale_ReferenceLengthScale");
                json["maleFemale_ReferenceRadiusScale"].AsFloat = GetFloatParamValue("maleFemale_ReferenceRadiusScale");
                json["maleFemale_AutoCalOnLoad"] = GetBoolParamValue("maleFemale_AutoCalOnLoad") ? "true" : "false";
                json["maleFemale_AutoCalDelay"].AsFloat = GetFloatParamValue("maleFemale_AutoCalDelay");
                json["maleFemale_RollingCal"] = GetBoolParamValue("maleFemale_RollingCal") ? "true" : "false";
                json["maleFemale_FullStrokeMode"] = GetBoolParamValue("maleFemale_FullStrokeMode") ? "true" : "false";
                json["maleFemale_RollingWindowSecs"].AsFloat   = GetFloatParamValue("maleFemale_RollingWindowSecs");
                json["maleFemale_RollingContractRate"].AsFloat = GetFloatParamValue("maleFemale_RollingContractRate");

                // Toy penetrator defaults
                var toyAtomParam = GetStringChooserJSONParam("maleFemale_ToyAtom");
                if (toyAtomParam != null) json["maleFemale_ToyAtom"] = toyAtomParam.val;
                var toyAxisParam = GetStringChooserJSONParam("maleFemale_ToyAxis");
                if (toyAxisParam != null) json["maleFemale_ToyAxis"] = toyAxisParam.val;
                json["maleFemale_ToyLength"].AsFloat = GetFloatParamValue("maleFemale_ToyLength");

                // Vibration defaults
                var vibModeParam = GetStringChooserJSONParam("vibrationMode");
                if (vibModeParam != null) json["vibrationMode"] = vibModeParam.val;
                json["vibrationIntensityScale"].AsFloat = GetFloatParamValue("vibrationIntensityScale");

                // Finger / clitoral defaults
                json["finger_MaxDepth"].AsFloat            = GetFloatParamValue("finger_MaxDepth");
                json["finger_ClitoralSensitivity"].AsFloat = GetFloatParamValue("finger_ClitoralSensitivity");
                json["finger_ClitoralBaseIntensity"].AsFloat = GetFloatParamValue("finger_ClitoralBaseIntensity");
                json["finger_ClitoralOffsetFwd"].AsFloat   = GetFloatParamValue("finger_ClitoralOffsetFwd");
                json["finger_ClitoralOffsetUp"].AsFloat    = GetFloatParamValue("finger_ClitoralOffsetUp");
                json["finger_ClitoralRadius"].AsFloat      = GetFloatParamValue("finger_ClitoralRadius");
                json["finger_ShowClitoralIndicator"]       = GetBoolParamValue("finger_ShowClitoralIndicator") ? "true" : "false";

                SuperController.singleton.SaveJSON(json, CONFIG_PATH,
                    () => SuperController.LogMessage("StrokerSync: Defaults saved to " + CONFIG_PATH),
                    null, null);
            }
            catch (Exception ex)
            {
                SuperController.LogError("StrokerSync: Failed to save defaults - " + ex.Message);
            }
        }

        private void LoadDefaults()
        {
            try
            {
                if (!FileManagerSecure.FileExists(CONFIG_PATH)) return;

                var json = SuperController.singleton.LoadJSON(CONFIG_PATH);
                if (json == null) return;

                if (json["deviceSmoothnessMs"] != null) _deviceSmoothnessMs.val = json["deviceSmoothnessMs"].AsFloat;
                if (json["sendRateHz"] != null) _sendRateHz.val = json["sendRateHz"].AsFloat;
                if (json["strokeZoneMin"] != null) _strokeZoneMin.val = json["strokeZoneMin"].AsFloat;
                if (json["strokeZoneMax"] != null) _strokeZoneMax.val = json["strokeZoneMax"].AsFloat;
                if (json["serverUrl"] != null) _serverUrl.val = json["serverUrl"].Value;

                if (json["maleFemale_Smoothing"] != null) SetFloatParamValue("maleFemale_Smoothing", json["maleFemale_Smoothing"].AsFloat);
                if (json["maleFemale_ReferenceLengthScale"] != null) SetFloatParamValue("maleFemale_ReferenceLengthScale", json["maleFemale_ReferenceLengthScale"].AsFloat);
                if (json["maleFemale_ReferenceRadiusScale"] != null) SetFloatParamValue("maleFemale_ReferenceRadiusScale", json["maleFemale_ReferenceRadiusScale"].AsFloat);
                if (json["maleFemale_AutoCalOnLoad"] != null) SetBoolParamValue("maleFemale_AutoCalOnLoad", json["maleFemale_AutoCalOnLoad"].Value == "true");
                if (json["maleFemale_AutoCalDelay"] != null) SetFloatParamValue("maleFemale_AutoCalDelay", json["maleFemale_AutoCalDelay"].AsFloat);
                if (json["maleFemale_RollingCal"] != null) SetBoolParamValue("maleFemale_RollingCal", json["maleFemale_RollingCal"].Value == "true");
                if (json["maleFemale_FullStrokeMode"] != null) SetBoolParamValue("maleFemale_FullStrokeMode", json["maleFemale_FullStrokeMode"].Value == "true");
                if (json["maleFemale_RollingWindowSecs"]   != null) SetFloatParamValue("maleFemale_RollingWindowSecs",   json["maleFemale_RollingWindowSecs"].AsFloat);
                if (json["maleFemale_RollingContractRate"] != null) SetFloatParamValue("maleFemale_RollingContractRate", json["maleFemale_RollingContractRate"].AsFloat);

                // Toy penetrator defaults (ToyAtom is scene-specific so skip on load; axis+length persist as user prefs)
                if (json["maleFemale_ToyAxis"] != null)
                {
                    var toyAxisParam = GetStringChooserJSONParam("maleFemale_ToyAxis");
                    if (toyAxisParam != null) toyAxisParam.val = json["maleFemale_ToyAxis"].Value;
                }
                if (json["maleFemale_ToyLength"] != null) SetFloatParamValue("maleFemale_ToyLength", json["maleFemale_ToyLength"].AsFloat);

                // Vibration defaults
                if (json["vibrationMode"] != null)
                {
                    var vibModeParam = GetStringChooserJSONParam("vibrationMode");
                    if (vibModeParam != null) vibModeParam.val = json["vibrationMode"].Value;
                }
                if (json["vibrationIntensityScale"] != null) SetFloatParamValue("vibrationIntensityScale", json["vibrationIntensityScale"].AsFloat);

                // Finger / clitoral defaults
                if (json["finger_MaxDepth"]               != null) SetFloatParamValue("finger_MaxDepth",               json["finger_MaxDepth"].AsFloat);
                if (json["finger_ClitoralSensitivity"]    != null) SetFloatParamValue("finger_ClitoralSensitivity",    json["finger_ClitoralSensitivity"].AsFloat);
                if (json["finger_ClitoralBaseIntensity"]  != null) SetFloatParamValue("finger_ClitoralBaseIntensity",  json["finger_ClitoralBaseIntensity"].AsFloat);
                if (json["finger_ClitoralOffsetFwd"]      != null) SetFloatParamValue("finger_ClitoralOffsetFwd",      json["finger_ClitoralOffsetFwd"].AsFloat);
                if (json["finger_ClitoralOffsetUp"]       != null) SetFloatParamValue("finger_ClitoralOffsetUp",       json["finger_ClitoralOffsetUp"].AsFloat);
                if (json["finger_ClitoralRadius"]         != null) SetFloatParamValue("finger_ClitoralRadius",         json["finger_ClitoralRadius"].AsFloat);
                if (json["finger_ShowClitoralIndicator"]  != null) SetBoolParamValue("finger_ShowClitoralIndicator",   json["finger_ShowClitoralIndicator"].Value == "true");

                SuperController.LogMessage("StrokerSync: Loaded saved defaults");
            }
            catch (Exception ex)
            {
                SuperController.LogError("StrokerSync: Failed to load defaults - " + ex.Message);
            }
        }

        private float GetFloatParamValue(string name)
        {
            var p = GetFloatJSONParam(name);
            return p != null ? p.val : 0f;
        }

        private bool GetBoolParamValue(string name)
        {
            var p = GetBoolJSONParam(name);
            return p != null ? p.val : false;
        }

        private void SetFloatParamValue(string name, float val)
        {
            var p = GetFloatJSONParam(name);
            if (p != null) p.val = val;
        }

        private void SetBoolParamValue(string name, bool val)
        {
            var p = GetBoolJSONParam(name);
            if (p != null) p.val = val;
        }

        // =====================================================================
        // UI
        // =====================================================================

        private new void InitUI()
        {
            _statusString = new JSONStorableString("status",
                "Status: Not connected\n\nMake sure Intiface Central is running.");

            BuildTab("Connection");
        }

        // =====================================================================
        // TAB SYSTEM
        // =====================================================================

        private void BuildTab(string tab)
        {
            foreach (var a in _tabCleanup) try { a(); } catch { }
            _tabCleanup.Clear();
            _activeTab = tab;
            _tabChooser.valNoCallback = tab; // keep storable in sync without re-firing

            switch (tab)
            {
                case "Connection":  BuildMainView();       break;
                case "Stroker":     BuildStrokerTab();     break;
                case "Penetration": BuildPenetrationTab(); break;
                case "Vibration":   BuildVibrationTab();   break;
            }
        }

        private void BuildMainView()
        {
            // ── Left column — connection + three large navigation buttons ─────
            var header = CreateTextField(new JSONStorableString("connHeader",
                "StrokerSync — Bluetooth via Intiface Central"));
            header.height = 50f;
            _tabCleanup.Add(() => RemoveTextField(header));

            var urlField = CreateTextField(_serverUrl);
            urlField.height = 60f;
            _tabCleanup.Add(() => RemoveTextField(urlField));

            _connectButton = CreateButton("Connect to Intiface Central");
            _connectButton.button.onClick.AddListener(OnConnect);
            _tabCleanup.Add(() => RemoveButton(_connectButton));

            _statusText = CreateTextField(_statusString);
            _statusText.height = 100f;
            _tabCleanup.Add(() => RemoveTextField(_statusText));

            var sp1 = CreateSpacer(); sp1.height = 15f;
            _tabCleanup.Add(() => RemoveSpacer(sp1));

            // Navigation buttons — double height via LayoutElement
            var strokerBtn = CreateButton("Stroker Settings  ›");
            strokerBtn.button.onClick.AddListener(() => BuildTab("Stroker"));
            SetButtonHeight(strokerBtn, 100f);
            _tabCleanup.Add(() => RemoveButton(strokerBtn));

            var penBtn = CreateButton("Penetration Toy Settings  ›");
            penBtn.button.onClick.AddListener(() => BuildTab("Penetration"));
            SetButtonHeight(penBtn, 100f);
            _tabCleanup.Add(() => RemoveButton(penBtn));

            var vibBtn = CreateButton("Vibrator Settings  ›");
            vibBtn.button.onClick.AddListener(() => BuildTab("Vibration"));
            SetButtonHeight(vibBtn, 100f);
            _tabCleanup.Add(() => RemoveButton(vibBtn));

            // ── Right column ─────────────────────────────────────────────────
            var pauseStrokerToggle = CreateToggle(_pauseStroker, true);
            pauseStrokerToggle.label = "Pause Stroker";
            _tabCleanup.Add(() => RemoveToggle(pauseStrokerToggle));

            var pauseVibrationToggle = CreateToggle(_pauseVibration, true);
            pauseVibrationToggle.label = "Pause Vibration";
            _tabCleanup.Add(() => RemoveToggle(pauseVibrationToggle));

            var manualSlider = CreateSlider(_simulatorPosition, true);
            manualSlider.label = "Position (manual)";
            _tabCleanup.Add(() => RemoveSlider(manualSlider));

            var sp2 = CreateSpacer(true); sp2.height = 15f;
            _tabCleanup.Add(() => RemoveSpacer(sp2));

            var devicePopup = CreateScrollablePopup(_deviceChooser, true);
            devicePopup.popup.onOpenPopupHandlers += () =>
            {
                if (_connectionManager != null)
                    _deviceChooser.choices = _connectionManager.GetDeviceChoices();
            };
            _tabCleanup.Add(() => RemovePopup(devicePopup));

            var vibratorPopup = CreateScrollablePopup(_vibratorChooser, true);
            vibratorPopup.popup.onOpenPopupHandlers += () =>
            {
                if (_connectionManager != null)
                    _vibratorChooser.choices = _connectionManager.GetVibratorChoices();
            };
            _tabCleanup.Add(() => RemovePopup(vibratorPopup));

            _refreshDevicesButton = CreateButton("Refresh Devices", true);
            _refreshDevicesButton.button.onClick.AddListener(() =>
            {
                if (_connectionManager != null && _connectionManager.IsConnected)
                    StartCoroutine(RefreshDevicesCoroutine());
            });
            _tabCleanup.Add(() => RemoveButton(_refreshDevicesButton));

            var sp3 = CreateSpacer(true); sp3.height = 15f;
            _tabCleanup.Add(() => RemoveSpacer(sp3));

            var overlayBtn = CreateButton("Quick Panel ↗", true);
            overlayBtn.button.onClick.AddListener(() =>
            {
                if (_overlayPanel != null)
                    _overlayPanel.SetVisible(!_overlayPanel.IsVisible);
            });
            _tabCleanup.Add(() => RemoveButton(overlayBtn));

            var sp4 = CreateSpacer(true); sp4.height = 15f;
            _tabCleanup.Add(() => RemoveSpacer(sp4));

            var saveBtn = CreateButton("Save Settings as Default", true);
            saveBtn.button.onClick.AddListener(SaveDefaults);
            _tabCleanup.Add(() => RemoveButton(saveBtn));
        }

        /// <summary>Set a button to a custom height via its LayoutElement.</summary>
        private void SetButtonHeight(UIDynamicButton btn, float height)
        {
            if (btn == null) return;
            var le = btn.gameObject.GetComponent<UnityEngine.UI.LayoutElement>();
            if (le == null) le = btn.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }

        private void BuildStrokerTab()
        {
            var sec = new CollapsibleSection(this);
            _tabCleanup.Add(() => sec.RemoveAll());

            // ── Back button ───────────────────────────────────────────────────
            var backBtn = sec.CreateButton("‹ Back");
            backBtn.height = 100f;
            backBtn.buttonColor = new Color(0.2f, 0.8f, 0.2f);
            backBtn.button.onClick.AddListener(() => BuildTab("Connection"));

            // ── Left — detection / selection / range controls ─────────────────
            sec.CreateTitle("Penis / Toy Penetration");
            sec.OnRemove(_combinedSource.BuildMaleFemaleUI(this));

            // ── Left — Timeline curve learning ───────────────────────────────
            sec.CreateSpacer().height = 8f;
            sec.CreateTitle("Timeline Curve Learning");
            sec.OnRemove(_combinedSource.BuildTimelineCurveUI(this));

            // ── Right — signal display (top of right column) ──────────────────
            var rangeDisplay = sec.CreateTextField(_combinedSource.MFRangeDisplay, true);
            rangeDisplay.height = 40f;

            // ── Right — Stroke Zone ───────────────────────────────────────────
            sec.CreateTitle("Stroke Zone", true);
            sec.CreateSlider(_strokeZoneMin, true).label = "Stroke Min";
            sec.CreateSlider(_strokeZoneMax, true).label = "Stroke Max";

            // ── Right — Send Rate & Timing ────────────────────────────────────
            sec.CreateSpacer(true).height = 8f;
            sec.CreateTitle("Send Rate & Timing", true);
            sec.CreateSlider(_sendRateHz, true).label         = "Max Send Rate (Hz)";
            sec.CreateSlider(_deviceSmoothnessMs, true).label = "Duration Padding (ms)";
            sec.CreateSlider(_combinedSource.MFNoiseFilter, true).label =
                "Noise Filter (0=off, 0.2=moderate)";

            // ── Right — Auto Calibration ─────────────────────────────────────
            sec.CreateSpacer(true).height = 8f;
            sec.CreateTitle("Auto Calibration", true);
            sec.CreateToggle(_combinedSource.MFAutoCalOnLoad, true).label =
                "Auto-Calibrate on Scene Load";
            sec.CreateSlider(_combinedSource.MFAutoCalDelay, true).label =
                "Auto-Cal Delay (seconds)";
            sec.CreateToggle(_combinedSource.MFRollingCal, true).label =
                "Rolling Calibration (continuous)";
            sec.CreateSlider(_combinedSource.MFRollingWindow, true).label =
                "Rolling Cal Window (seconds)";
            sec.CreateSlider(_combinedSource.MFRollingRate, true).label =
                "Rolling Cal Rate (per window)";
        }

        private void BuildPenetrationTab()
        {
            var sec = new CollapsibleSection(this);
            _tabCleanup.Add(() => sec.RemoveAll());

            // ── Back button ───────────────────────────────────────────────────
            var backBtn = sec.CreateButton("‹ Back");
            backBtn.height = 100f;
            backBtn.buttonColor = new Color(0.2f, 0.8f, 0.2f);
            backBtn.button.onClick.AddListener(() => BuildTab("Connection"));

            // ── Left — finger / dildo penetration tracking ────────────────────
            sec.CreateTitle("Finger / Dildo Penetration Tracking");
            sec.OnRemove(_combinedSource.BuildFingerPenetrationUI(this));
        }

        private void BuildVibrationTab()
        {
            var sec = new CollapsibleSection(this);
            _tabCleanup.Add(() => sec.RemoveAll());

            // ── Back button ───────────────────────────────────────────────────
            var backBtn = sec.CreateButton("‹ Back");
            backBtn.height = 100f;
            backBtn.buttonColor = new Color(0.2f, 0.8f, 0.2f);
            backBtn.button.onClick.AddListener(() => BuildTab("Connection"));

            // ── Left — vibration mode ─────────────────────────────────────────
            sec.CreateTitle("Penetration-Linked Vibration");
            sec.CreateScrollablePopup(_vibrationMode).label  = "Vibration Mode";
            sec.CreateSlider(_vibrationIntensityScale).label = "Intensity Scale";
            var dispSlider = sec.CreateSlider(_vibrationDisplay);
            dispSlider.label = "Live Intensity";
            dispSlider.slider.interactable = false;

            // ── Right ─────────────────────────────────────────────────────────
            var saveBtn = sec.CreateButton("Save Settings as Default", true);
            saveBtn.height = 100f;
            saveBtn.button.onClick.AddListener(SaveDefaults);

            // ── Right — clitoral zone ──────────────────────────────────────────
            sec.CreateTitle("Clitoral Zone", true);
            sec.OnRemove(_combinedSource.BuildVibrationUI(this));
        }

        // =====================================================================
        // CONNECTION
        // =====================================================================

        private void OnConnect()
        {
            if (_connectionManager == null) return;

            if (_connectionManager.IsConnected)
            {
                _connectionManager.Destroy();
                _isInitialized = false;
                _statusString.val = "Status: Disconnected";
                _connectButton.label = "Connect to Intiface Central";
                _deviceChooser.choices = new List<string> { "None" };
                _vibratorChooser.choices = new List<string>
                {
                    IntifaceConnectionManager.VIBRATOR_ALL,
                    IntifaceConnectionManager.VIBRATOR_NONE
                };
                return;
            }

            _statusString.val = "Status: Connecting...";
            _connectButton.label = "Connecting...";

            _connectionManager.Connect(_serverUrl.val, (success, message) =>
            {
                if (success)
                {
                    _isInitialized = true;
                    _statusString.val = $"Status: Connected!\nServer: {_connectionManager.ServerName}\n" +
                        $"Device: {_connectionManager.DeviceName}";
                    _connectButton.label = "Disconnect";

                    _deviceChooser.choices = _connectionManager.GetDeviceChoices();
                    _vibratorChooser.choices = _connectionManager.GetVibratorChoices();
                    if (_connectionManager.HasDevice)
                    {
                        foreach (var choice in _deviceChooser.choices)
                        {
                            if (choice.Contains(_connectionManager.DeviceName))
                            {
                                _deviceChooser.SetVal(choice);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    _isInitialized = false;
                    _statusString.val = $"Status: Connection failed\n{message}\n\n" +
                        "Make sure Intiface Central is running\nand the server is started.";
                    _connectButton.label = "Connect to Intiface Central";
                }
            });
        }

        private System.Collections.IEnumerator RefreshDevicesCoroutine()
        {
            yield return _connectionManager.RefreshDeviceList();
            _deviceChooser.choices = _connectionManager.GetDeviceChoices();
            _vibratorChooser.choices = _connectionManager.GetVibratorChoices();

            _statusString.val = $"Status: Connected\nServer: {_connectionManager.ServerName}\n" +
                $"Devices: {_connectionManager.ConnectedDevices.Count}\n" +
                $"Active: {_connectionManager.DeviceName}";
        }

        // =====================================================================
        // UPDATE LOOP
        // =====================================================================

        private void Update()
        {
            if (_connectionManager != null)
                _connectionManager.Update();

            _overlayPanel?.Update();

            _isInitialized = _connectionManager != null && _connectionManager.IsConnected;

            if (!_isInitialized) return;

            UpdateMotionSource();
            UpdateSimulator();
        }

        private void UpdateMotionSource()
        {
            float pos = 0f, vel = 0f;
            bool hasPenetration = _combinedSource.OnUpdate(ref pos, ref vel);

            if (hasPenetration)
                SendDevicePosition(pos, vel);
            else
                HandleClitoralVibration();
        }

        /// <summary>
        /// When no penetration is detected, check for clitoral stimulation from
        /// FingerSource and drive vibrators accordingly.  This is independent of
        /// VibrationMode (which gates penetration-linked vibration only).
        /// </summary>
        private void HandleClitoralVibration()
        {
            float? clitIntensity = _combinedSource.ClitoralIntensity;
            bool hasClitoral = clitIntensity.HasValue;

            // Clitoral vibration respects pause but NOT VibrationMode — it is its own path.
            bool canSendClit = hasClitoral && !_pauseVibration.val
                && _connectionManager != null && _connectionManager.IsConnected;

            if (canSendClit)
            {
                float clit = clitIntensity.Value * _vibrationIntensityScale.val;
                _vibrationDisplay.val = clit;

                // Send to selected vibrator device(s).
                // Fallback: if no dedicated vibrators exist, try primary device directly
                // (the Handy 2 Pro may not advertise vibration capability but supports it).
                _connectionManager.SendVibration(clit);
                if (!_connectionManager.HasAnyVibrator && _connectionManager.HasDevice)
                    _connectionManager.SendVibrate(clit);

                _vibrationActive = clit > 0.01f;
            }
            else if (_vibrationActive)
            {
                // Clitoral contact just ended — stop all vibrators.
                _vibrationActive = false;
                _vibrationDisplay.val = 0f;
                if (_connectionManager != null)
                {
                    _connectionManager.SendVibrateAll(0f);
                    _connectionManager.SendVibrate(0f);
                }
            }
        }

        private void UpdateSimulator()
        {
            var prevPos = _simulatorPosition.val;
            var newPos = Mathf.MoveTowards(prevPos, _simulatorTarget,
                LaunchUtils.PredictDistanceTraveled(_simulatorSpeed, Time.deltaTime));
            _simulatorPosition.SetVal(newPos);

            _combinedSource.OnSimulatorUpdate(prevPos, newPos, Time.deltaTime);
        }

        private void SendDevicePosition(float pos, float velocity)
        {
            _simulatorTarget = Mathf.Clamp01(pos);
            _simulatorSpeed = Mathf.Clamp01(velocity);

            if (!_isInitialized || _pauseStroker.val || _connectionManager == null || !_connectionManager.HasDevice)
                return;

            float mappedPos = Mathf.Lerp(_strokeZoneMin.val, _strokeZoneMax.val, pos);
            mappedPos = Mathf.Clamp01(mappedPos);

            // Track velocities every frame for accurate adaptive rate and extrapolation
            float now = Time.time;
            float frameDt = now - _prevMappedPosTime;
            if (frameDt > 0.001f)
            {
                float instantVelocity = Mathf.Abs(mappedPos - _prevMappedPos) / frameDt;
                _adaptiveVelocity = Mathf.Lerp(instantVelocity, _adaptiveVelocity, ADAPTIVE_VELOCITY_SMOOTHING);
                _prevMappedPos = mappedPos;
                _prevMappedPosTime = now;
            }

            float extrapDt = now - _prevExtrapTime;
            if (extrapDt > 0.001f)
            {
                float signedInstant = (mappedPos - _prevExtrapPos) / extrapDt;

                // Detect stroke reversal: EMA velocity and instant velocity have opposite signs.
                // At turnarounds, velocity extrapolation always overshoots — suppress it entirely
                // and just send the current position so the device decelerates naturally.
                _isReversing = (_signedVelocity >  0.05f && signedInstant < -0.05f) ||
                               (_signedVelocity < -0.05f && signedInstant >  0.05f);

                // Update stroke amplitude tracking on each direction change.
                // Blend 70 % new / 30 % old to resist isolated physics spikes.
                if (_isReversing)
                {
                    if (_signedVelocity > 0f) _strokePeak   = Mathf.Lerp(_strokePeak,   mappedPos, 0.7f);
                    else                       _strokeValley = Mathf.Lerp(_strokeValley, mappedPos, 0.7f);
                }

                _signedVelocity = Mathf.Lerp(signedInstant, _signedVelocity, EXTRAP_VELOCITY_SMOOTHING);
                _prevExtrapPos = mappedPos;
                _prevExtrapTime = now;
            }

            // Deadband in source space: unaffected by stroke zone compression.
            if (Mathf.Abs(pos - _lastRawPosSent) < POSITION_DEADZONE)
                return;

            // Adaptive rate: scale between ADAPTIVE_MIN_HZ and configured max based on motion speed
            float velocityFactor = Mathf.Clamp01(_adaptiveVelocity / ADAPTIVE_VELOCITY_THRESHOLD);
            float effectiveHz = Mathf.Lerp(ADAPTIVE_MIN_HZ, _sendRateHz.val, velocityFactor);
            float sendInterval = 1f / effectiveHz;

            // Accumulator pattern: add frame time, send when budget is met.
            _sendAccumulator += Time.deltaTime;

            // Cap at 2x interval to prevent burst-sending after long stalls
            if (_sendAccumulator > sendInterval * 2f)
                _sendAccumulator = sendInterval;

            if (_sendAccumulator < sendInterval)
                return;

            _sendAccumulator -= sendInterval;

            // --- LOOK-AHEAD ---
            // Strategy A: if Timeline curve access is available, evaluate the curve
            // at (clipTime + sendInterval) for deterministic look-ahead.  This is
            // perfect at reversals — no overshoot, no reactive suppression needed.
            // Fallback: velocity-based extrapolation with reversal suppression.
            float extrapolatedPos;
            float? predicted = _combinedSource.PredictPosition(sendInterval);
            if (predicted.HasValue)
            {
                // Map predicted source-space position through stroke zone
                extrapolatedPos = Mathf.Lerp(_strokeZoneMin.val, _strokeZoneMax.val, predicted.Value);
            }
            else
            {
                // Velocity extrapolation fallback — suppress at stroke reversals
                extrapolatedPos = _isReversing
                    ? mappedPos
                    : mappedPos + _signedVelocity * sendInterval;
            }

            extrapolatedPos = Mathf.Clamp01(extrapolatedPos);

            // Duration: 1.5× the send interval so the device is always mid-movement when
            // the next command arrives. This creates smooth continuous gliding rather than
            // sprint-and-restart steps. The deliberate overlap absorbs BT latency (~20-40ms)
            // and prevents the device from having to rush to hit exact arrival times.
            int baseDurationMs = (int)(sendInterval * 1500f);
            int durationMs = Mathf.Max(baseDurationMs + (int)_deviceSmoothnessMs.val, 20);

            // --- STROKE ADVANCE FILTER (adaptive threshold) ---
            // Once the device is heading in a direction, skip commands that don't push
            // the target meaningfully further. The device completes its current glide
            // instead of being interrupted by redundant same-direction waypoints.
            // Direction reversals always bypass this filter.
            //
            // The threshold scales with the observed stroke amplitude:
            //   large stroke (0→1)  → threshold ≈ 12 % → sparse updates, no stepping
            //   small stroke (0→15%) → threshold ≈ 2 %  → fine updates, no rigid feel
            float strokeAmplitude = Mathf.Max(0f, _strokePeak - _strokeValley);
            float strokeThreshold = Mathf.Clamp(
                strokeAmplitude * STROKE_THRESHOLD_FRACTION,
                STROKE_THRESHOLD_MIN,
                STROKE_THRESHOLD_MAX);

            bool skipSend = false;
            if (!_isReversing && _lastCommandedTarget >= 0f)
            {
                float delta = extrapolatedPos - _lastCommandedTarget;
                // Use motion direction from velocity EMA; fall back to delta sign when nearly still
                float motionDir = Mathf.Abs(_signedVelocity) > 0.05f
                    ? Mathf.Sign(_signedVelocity)
                    : Mathf.Sign(delta);
                bool advancingFurther = Mathf.Sign(delta) == motionDir
                                     && Mathf.Abs(delta) >= strokeThreshold;
                if (!advancingFurther)
                    skipSend = true;
            }

            // Deadband tracker always updates so it stays current even on skipped frames
            _lastRawPosSent = pos;

            if (!skipSend)
            {
                _lastCommandedTarget = extrapolatedPos;
                _connectionManager.SendPositionDirect(extrapolatedPos, durationMs);

                if (!_loggedFirstSend)
                {
                    _loggedFirstSend = true;
                    SuperController.LogMessage($"StrokerSync: First command sent! pos={extrapolatedPos:F3} (raw={mappedPos:F3}) " +
                        $"dur={durationMs}ms rate={effectiveHz:F0}Hz");
                }

                // Simulator reflects what the device actually received
                _simulatorTarget = extrapolatedPos;
            }

            // Vibration: not gated by stroke advance filter — depth and velocity track continuously.
            // Runs at the same rate as the linear command (shared accumulator).
            if (_vibrationMode.val != "Off" && !_pauseVibration.val)
            {
                float vibIntensity = ComputeVibrationIntensity(mappedPos, _signedVelocity)
                                     * _vibrationIntensityScale.val;
                _vibrationDisplay.val = vibIntensity;
                _connectionManager.SendVibration(vibIntensity);
                if (!_connectionManager.HasAnyVibrator && _connectionManager.HasDevice)
                    _connectionManager.SendVibrate(vibIntensity);
                _vibrationActive = vibIntensity > 0.01f;
            }
            else
            {
                _vibrationDisplay.val = 0f;
            }
        }

        /// <summary>
        /// Compute vibration intensity from current stroke state.
        /// mappedPos: 0 = fully in (deep), 1 = fully out.
        /// signedVelocity: signed EMA velocity in pos-units/sec.
        /// </summary>
        private float ComputeVibrationIntensity(float mappedPos, float signedVelocity)
        {
            // Depth: higher intensity when deeper (pos near 0)
            float depthIntensity = Mathf.Clamp01(1f - mappedPos);
            // Velocity: higher intensity at faster stroke speed, normalised to full-rate threshold
            float velIntensity = Mathf.Clamp01(Mathf.Abs(signedVelocity) / ADAPTIVE_VELOCITY_THRESHOLD);

            switch (_vibrationMode.val)
            {
                case "Depth":    return depthIntensity;
                case "Velocity": return velIntensity;
                case "Blend":    return Mathf.Clamp01((depthIntensity + velIntensity) * 0.5f);
                default:         return 0f; // "Off"
            }
        }

        private void OnSceneLoaded()
        {
            SuperController.LogMessage("StrokerSync: Scene load detected — resetting scene-dependent state.");

            // Reset extrapolation state
            _signedVelocity = 0f;
            _prevExtrapPos = 0f;
            _prevExtrapTime = 0f;
            _isReversing = false;
            _lastCommandedTarget = -1f;
            _strokePeak   = 0.5f;
            _strokeValley = 0.5f;
            _adaptiveVelocity = 0f;
            _prevMappedPos = 0f;
            _prevMappedPosTime = 0f;

            // Reset vibration state
            _vibrationActive = false;
            _vibrationDisplay.val = 0f;
            if (_connectionManager != null)
                _connectionManager.SendVibrateAll(0f);

            // Reset send state
            _lastRawPosSent = -1f;
            _sendAccumulator = 0f;
            _loggedFirstSend = false;

            // Notify motion source to clear cached references
            _combinedSource.OnSceneLoaded(this);
        }

        private void TriggerAutoDetect()
        {
            _combinedSource.AutoDetectToyAxis();
        }

        private void OnDestroy()
        {
            _overlayPanel?.Destroy();
            _overlayPanel = null;

            // Unsubscribe from scene load events
            SuperController.singleton.onSceneLoadedHandlers -= OnSceneLoaded;

            // Tear down the active tab's UI elements
            foreach (var a in _tabCleanup) try { a(); } catch { }
            _tabCleanup.Clear();

            _combinedSource.OnDestroy(this);

            if (_connectionManager != null)
                _connectionManager.Destroy();

            if (_instance == this)
                _instance = null;

            SuperController.LogMessage("StrokerSync: Plugin destroyed");
        }
    }
}