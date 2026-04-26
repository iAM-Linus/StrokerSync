using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Male + Female motion source - tracks penis penetration depth.
    ///
    /// SIGNAL PATH:
    ///   Raw penetration depth (0-1)
    ///   → Range mapping (penRangeMin/Max → 0-1)
    ///   → Conditional spike rejection (median only on outliers, zero latency otherwise)
    ///   → Optional asymmetric EMA (fast attack on "in", softer "out")
    ///   → Invert (1.0 = out, 0.0 = in)
    ///   → Stroke zone mapping (in StrokerSync.cs)
    ///   → LinearCmd(position, predictive duration)
    ///
    /// KEY INSIGHT: The Handy's firmware already does smooth interpolation via
    /// the LinearCmd duration parameter. Software-side smoothing should ONLY remove
    /// noise, never shape the signal. Heavy smoothing (speed limiters, strong EMA)
    /// compresses stroke amplitude and makes the device barely move.
    /// </summary>
    public class MaleFemaleSource : IMotionSource
    {
        private const string FEMALE_AUTO = "*Auto*";
        private const int PENIS_LENGTH_AVERAGES_COUNT = 60;

        private StrokerSync _plugin;
        private SuperController Controller => SuperController.singleton;

        // --- Male Tracking (Cached) ---
        private Atom _maleAtom;
        private bool _isObjectPenetrator;           // True when male atom is a non-Person object (dildo/toy)
        private List<Transform> _cachedPenisTransforms;
        private CapsuleCollider _cachedTipCollider;
        private float _penisLength;
        private Vector3 _penisBase;
        private Vector3 _penisTip;
        private Vector3 _penisDirection;
        private float _penisRadius;

        // Penis length averaging
        private float[] _penisLengthAverages;
        private int _penisLengthAverageIndex;
        private float _runningLengthSum;

        // --- Female Tracking (Cached) ---
        private Atom _femaleAtom;
        private Atom _cachedForAtom;          // Which atom CacheFemaleComponents last ran for
        private Rigidbody _cachedLabiaTrigger;
        private Rigidbody _cachedVaginaTrigger;
        private Rigidbody _cachedAnusTrigger;
        private Transform _cachedMouthTarget;   // Lip/tongue bone (near mouth opening, not jaw joint)
        private Transform _cachedPelvis;  // Pelvis bone for stabilizing vaginal canal direction

        // --- Hand Tracking (Cached) ---
        // Finger bones (lMid1/rMid1 = middle finger base = center of grip).
        // "lHand"/"rHand" are WRIST bones, too far from the shaft for grip tracking.
        private Transform _cachedReceiverLGrip;
        private Transform _cachedReceiverRGrip;
        private Transform _cachedMaleLGrip;
        private Transform _cachedMaleRGrip;

        private Vector3 _targetPosition;
        private Vector3 _targetDirection;
        private float _targetDepth;

        // --- Noise Filtering ---
        // Conditional spike rejection: only engages median when a sample
        // deviates from the trend beyond SPIKE_THRESHOLD. During smooth
        // motion the raw sample passes through with zero latency.
        private float _medianBuf0, _medianBuf1, _medianBuf2;
        private int _medianCount;
        private float _prevFilterOutput;
        private const float SPIKE_THRESHOLD = 0.12f; // Position-units; typical physics spike is 0.15+

        // Penetration hysteresis: prevents on/off flickering near threshold
        private bool _isPenetrating;
        private const float PENETRATION_OFF_THRESHOLD = 0.005f;  // Stop sending below this
        private const float PENETRATION_ON_THRESHOLD = 0.02f;    // Resume sending above this

        // Optional light EMA for remaining high-frequency noise
        private float _emaState;
        private bool _filterInitialized;

        // Previous position for velocity output
        private float _prevPosition;
        private float _prevPositionTime;

        // --- Penetration Range Mapping ---
        private JSONStorableFloat _penRangeMin;
        private JSONStorableFloat _penRangeMax;
        private JSONStorableBool _fullStrokeMode;   // when true, rolling cal tracks range bidirectionally
        private JSONStorableString _penRangeDisplay;

        // Auto-calibration state (manual button)
        private bool _autoCalibrating;
        private float _autoCalTimer;
        private float _autoCalMin;
        private float _autoCalMax;
        private const float AUTO_CAL_DURATION = 8.0f;
        private UIDynamicButton _autoCalButton;

        // Auto-calibrate on scene load
        private JSONStorableBool _autoCalOnLoad;
        private JSONStorableFloat _autoCalDelay;

        // --- Rolling Calibration ---
        // Continuously adapts the penetration range without user intervention.
        // Expand: instant when a new extreme is observed.
        // Contract: slow decay toward recently-observed range over time.
        private JSONStorableBool _rollingCalEnabled;
        private float _rollingWindowMin;          // Min observed in current window
        private float _rollingWindowMax;          // Max observed in current window
        private float _rollingWindowTimer;        // Time remaining in current window
        private bool _rollingWindowHasData;       // Did we see any penetration this window?
        private JSONStorableFloat _rollingWindowSecs;      // How far back to look (storable)
        private JSONStorableFloat _rollingContractRate;    // Blend factor per window (storable)
        private const float ROLLING_EXPAND_MARGIN = 0.02f; // Small margin added on instant expand
        private const float ROLLING_MIN_RANGE = 0.05f;     // Never contract range below this width

        // --- Auto-Select ---
        private float _autoSelectTimer;
        private const float AUTO_SELECT_INTERVAL = 0.5f;

        // --- Cache Validation ---
        // Periodically verify cached component references are still alive.
        // Atom reloads (clothing changes, appearance presets) can silently
        // destroy rigidbodies, leaving stale references that return wrong data.
        private float _cacheValidationTimer;
        private const float CACHE_VALIDATION_INTERVAL = 2.0f;

        // --- UI ---
        private JSONStorableStringChooser _maleChooser;
        private JSONStorableStringChooser _femaleChooser;
        // Object/toy penetrator settings.
        // _toyAtomChooser selects the toy; when set to anything other than "None"
        // it takes over as the penetrator and the male Person chooser is ignored.
        private JSONStorableStringChooser _toyAtomChooser; // The toy/object atom to use as penetrator
        private JSONStorableStringChooser _toyAxis;        // Which local axis points base→tip
        private JSONStorableFloat _toyLength;              // Length of the penetrating portion (meters)
        private JSONStorableStringChooser _targetChooser;
        private JSONStorableFloat _referenceLengthScale;
        private JSONStorableFloat _referenceRadiusScale;
        private JSONStorableFloat _noiseFilter;       // Light EMA strength (0=off, 0.3=moderate)
        // Kept registered for backward compat with saved scenes, but no longer used:
        private JSONStorableFloat _maxStrokeSpeed;

        // UI element cleanup — all elements created in CreateUI are tracked here
        // so they can be removed when switching to a different motion source.
        private readonly List<Action> _uiCleanup = new List<Action>();

        // Collider names - static, allocated once
        private static readonly HashSet<string> PENIS_COLLIDER_NAMES = new HashSet<string>
        {
            "AutoColliderGen1Hard",
            "AutoColliderGen2Hard",
            "AutoColliderGen3aHard",
            "AutoColliderGen3bHard"
        };

        public void OnInit(StrokerSync plugin)
        {
            InitLogic(plugin);
            CreateUI(plugin);
        }

        /// <summary>
        /// Initialise tracking logic without creating any UI elements.
        /// Called by CombinedSource so it can manage UI via the tab system.
        /// </summary>
        public void InitLogic(StrokerSync plugin)
        {
            _plugin = plugin;
            plugin.StartCoroutine(DelayedInit());
        }

        /// <summary>Remove all UI elements created by CreateUI / CreateDetectionUI.</summary>
        public void DestroyUI()
        {
            foreach (var cleanup in _uiCleanup)
                try { cleanup(); } catch { }
            _uiCleanup.Clear();
        }

        // ── Storable accessors for tab-based UI layout ─────────────────────────
        public JSONStorableString PenRangeDisplay => _penRangeDisplay;
        public JSONStorableFloat NoiseFilter => _noiseFilter;
        public JSONStorableBool AutoCalOnLoad => _autoCalOnLoad;
        public JSONStorableFloat AutoCalDelay => _autoCalDelay;
        public JSONStorableBool RollingCal => _rollingCalEnabled;
        public JSONStorableFloat RollingWindowSecs => _rollingWindowSecs;
        public JSONStorableFloat RollingContractRate => _rollingContractRate;

        public void OnInitStorables(StrokerSync plugin)
        {
            _plugin = plugin;

            _maleChooser = new JSONStorableStringChooser(
                "maleFemale_Male", new List<string>(), "", "Select Male",
                (JSONStorableStringChooser.SetStringCallback)MaleChooserCallback);
            plugin.RegisterStringChooser(_maleChooser);

            _femaleChooser = new JSONStorableStringChooser(
                "maleFemale_Female", new List<string>(), FEMALE_AUTO, "Select Female",
                (JSONStorableStringChooser.SetStringCallback)FemaleChooserCallback);
            plugin.RegisterStringChooser(_femaleChooser);

            var targets = new List<string> { "Auto", "Vagina", "Anus", "Mouth", "Hand" };
            _targetChooser = new JSONStorableStringChooser(
                "maleFemale_Target", targets, "Auto", "Target Orifice",
                (JSONStorableStringChooser.SetStringCallback)null);
            plugin.RegisterStringChooser(_targetChooser);

            _referenceLengthScale = new JSONStorableFloat("maleFemale_ReferenceLengthScale", 1.0f, 0.1f, 3.0f, false);
            plugin.RegisterFloat(_referenceLengthScale);

            _referenceRadiusScale = new JSONStorableFloat("maleFemale_ReferenceRadiusScale", 1.0f, 0.1f, 10.0f, false);
            plugin.RegisterFloat(_referenceRadiusScale);

            // Noise filter: light EMA applied AFTER the median filter.
            // 0.0 = off (median only), 0.3 = moderate extra smoothing.
            // NOTE: This replaces the old "Smoothing" slider (ID kept for save compat).
            // The old default of 0.5 was way too heavy and crushed amplitude.
            _noiseFilter = new JSONStorableFloat("maleFemale_Smoothing", 0.05f, 0.0f, 0.4f, false);
            plugin.RegisterFloat(_noiseFilter);

            // Backward compat: keep registered so saved scenes don't error.
            // No longer used in signal path — device smoothness controls feel now.
            _maxStrokeSpeed = new JSONStorableFloat("maleFemale_MaxStrokeSpeed", 3.0f, 0.5f, 10.0f, false);
            plugin.RegisterFloat(_maxStrokeSpeed);

            // Penetration range mapping
            _penRangeMin = new JSONStorableFloat("maleFemale_PenRangeMin", 0.0f, 0.0f, 1.0f, false);
            plugin.RegisterFloat(_penRangeMin);

            _penRangeMax = new JSONStorableFloat("maleFemale_PenRangeMax", 1.0f, 0.0f, 1.0f, false);
            plugin.RegisterFloat(_penRangeMax);

            // Full Stroke Mode: when enabled alongside rolling calibration, the window-end
            // contraction runs unconditionally in BOTH directions (min moves up AND down,
            // max moves down AND up) so the range always converges to the actual movement
            // amplitude — giving full device strokes even for partial animations.
            _fullStrokeMode = new JSONStorableBool("maleFemale_FullStrokeMode", false);
            plugin.RegisterBool(_fullStrokeMode);

            _penRangeDisplay = new JSONStorableString("maleFemale_PenRangeDisplay", "Raw: --- | Mapped: --- | Out: ---");
            plugin.RegisterString(_penRangeDisplay);

            // Auto-calibrate on scene load
            _autoCalOnLoad = new JSONStorableBool("maleFemale_AutoCalOnLoad", false);
            plugin.RegisterBool(_autoCalOnLoad);

            // Delay before auto-calibration starts (seconds).
            // Most scenes need time for the animation to start before calibrating.
            _autoCalDelay = new JSONStorableFloat("maleFemale_AutoCalDelay", 10f, 3f, 30f, false);
            plugin.RegisterFloat(_autoCalDelay);

            // Rolling calibration: continuously adapts range during play
            _rollingCalEnabled = new JSONStorableBool("maleFemale_RollingCal", true);
            plugin.RegisterBool(_rollingCalEnabled);

            // How long each rolling-cal window is in seconds.
            // Shorter = faster adaptation but noisier; longer = smoother but slower to catch changes.
            _rollingWindowSecs = new JSONStorableFloat("maleFemale_RollingWindowSecs", 8f, 1f, 60f, false);
            plugin.RegisterFloat(_rollingWindowSecs);

            // How aggressively the stored range contracts toward the observed range each window.
            // Higher = snappier but may overshoot on noisy data.
            _rollingContractRate = new JSONStorableFloat("maleFemale_RollingContractRate", 0.30f, 0.05f, 0.95f, false);
            plugin.RegisterFloat(_rollingContractRate);

            // Object/toy penetrator settings.
            // When _toyAtomChooser is not "None", that object overrides the male Person
            // chooser as the penetrator. The main controller of the object is the BASE.
            _toyAtomChooser = new JSONStorableStringChooser(
                "maleFemale_ToyAtom", new List<string> { "None" }, "None",
                "Toy / Object Penetrator",
                ToyAtomChooserCallback);
            plugin.RegisterStringChooser(_toyAtomChooser);

            var axisList = new List<string> { "+Y (Up)", "+Z (Forward)", "-Z (Back)", "+X (Right)", "-X (Left)", "-Y (Down)" };
            _toyAxis = new JSONStorableStringChooser("maleFemale_ToyAxis", axisList, "+Z (Forward)", null);
            plugin.RegisterStringChooser(_toyAxis);

            // Default 0.18 m — typical insertable length of a mid-size toy.
            _toyLength = new JSONStorableFloat("maleFemale_ToyLength", 0.18f, 0.03f, 0.45f, false);
            plugin.RegisterFloat(_toyLength);
        }

        // Debug logging (throttled)
        private float _debugLogTimer;
        private const float DEBUG_LOG_INTERVAL = 3.0f;
        private int _debugFramesSinceMotion;

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            // 0. Periodic cache validation — detect stale references from atom reloads
            _cacheValidationTimer -= Time.deltaTime;
            if (_cacheValidationTimer <= 0f)
            {
                _cacheValidationTimer = CACHE_VALIDATION_INTERVAL;
                ValidateCachedComponents();
            }

            // 1. Update penis geometry (cached transforms - no GC)
            if (!UpdateMalePhysics())
            {
                if (_maleAtom == null)
                    LogDebugThrottled($"MaleFemale: _maleAtom is NULL. Chooser val='{_maleChooser.val}', choices={_maleChooser.choices.Count}");
                else if (!_maleAtom.on)
                    LogDebugThrottled($"MaleFemale: Male atom '{_maleAtom.uid}' is OFF");
                else
                    LogDebugThrottled($"MaleFemale: Penis colliders not found on '{_maleAtom.uid}'. Cache={((_cachedPenisTransforms != null) ? _cachedPenisTransforms.Count.ToString() : "null")}/4");
                return false;
            }

            // 2. Handle receiver auto-selection on a timer
            if (_femaleChooser.val == FEMALE_AUTO)
            {
                _autoSelectTimer -= Time.deltaTime;
                if (_autoSelectTimer <= 0f)
                {
                    _autoSelectTimer = AUTO_SELECT_INTERVAL;
                    RunAutoSelect();
                }
            }

            // 3. Update target position (cached rigidbodies - no GC)
            if (!UpdateFemaleTarget())
            {
                if (_femaleAtom == null)
                    LogDebugThrottled("MaleFemale: _femaleAtom is NULL (auto-select hasn't found anyone yet)");
                else
                    LogDebugThrottled($"MaleFemale: Target tracking failed on '{_femaleAtom.uid}'. " +
                        $"labia={(_cachedLabiaTrigger != null)}, vagina={(_cachedVaginaTrigger != null)}, " +
                        $"anus={(_cachedAnusTrigger != null)}, jaw={(_cachedMouthTarget != null)}, mode='{_targetChooser.val}'");
                return false;
            }

            // 4. Calculate raw penetration depth (0 = not touching, 1 = fully inserted)
            float rawDepth = CalculatePenetrationDepth();

            // 4b. Auto-calibration: track min/max over time (manual button)
            if (_autoCalibrating)
            {
                UpdateAutoCalibration(rawDepth);
            }

            // 4b2. Rolling calibration: continuously adapt range during play
            if (_rollingCalEnabled != null && _rollingCalEnabled.val && !_autoCalibrating)
            {
                UpdateRollingCalibration(rawDepth);
            }

            // 4c. Remap raw depth through the penetration range
            float rangeMin = _penRangeMin.val;
            float rangeMax = _penRangeMax.val;
            float mappedDepth;
            if (rangeMax > rangeMin + 0.001f)
                mappedDepth = Mathf.Clamp01((rawDepth - rangeMin) / (rangeMax - rangeMin));
            else
                mappedDepth = rawDepth;

            // 5. Filter: median (spike removal) + optional light EMA (noise reduction)
            //    NO speed limiter — that's what was crushing the amplitude.
            //    The Handy's LinearCmd duration handles physical smoothness.
            float filtered = ApplyNoiseFilter(mappedDepth);

            // Update live display (throttled)
            if (Time.frameCount % 10 == 0)
            {
                float displayOut = 1f - filtered; // Show what the device actually gets
                _penRangeDisplay.val = $"Raw: {rawDepth:F2} | Mapped: {mappedDepth:F2} | Out: {displayOut:F2}";
            }

            // Hysteresis: require a higher threshold to START sending than to STOP.
            // Prevents on/off flickering when the tip barely touches the target.
            if (!_isPenetrating)
            {
                if (filtered < PENETRATION_ON_THRESHOLD)
                {
                    _debugFramesSinceMotion++;
                    if (_debugFramesSinceMotion >= 300)
                    {
                        _debugFramesSinceMotion = 0;
                        LogDebugThrottled($"MaleFemale: No penetration for 5s. Tip-target dist: {Vector3.Distance(_penisTip, _targetPosition):F3}m, rawDepth={rawDepth:F3}");
                    }
                    return false;
                }
                _isPenetrating = true;
            }
            else
            {
                if (filtered < PENETRATION_OFF_THRESHOLD)
                {
                    // Off-edge: emit one "fully withdrawn" command so the device returns
                    // to base position instead of freezing at the last commanded position.
                    // On subsequent frames _isPenetrating is already false so the
                    // if (!_isPenetrating) branch fires and returns false as normal.
                    _isPenetrating = false;
                    _debugFramesSinceMotion++;
                    outPos = 1.0f;   // 1.0 = device at top = penis fully out
                    outVelocity = 0f;
                    return true;
                }
            }
            _debugFramesSinceMotion = 0;

            // 6. Velocity (informational — duration is set by deviceSmoothnessMs)
            float now = Time.time;
            float dt = now - _prevPositionTime;
            float velocity = 0f;
            if (dt > 0.001f)
                velocity = Mathf.Clamp01(Mathf.Abs(filtered - _prevPosition) / dt / 3.0f);
            _prevPosition = filtered;
            _prevPositionTime = now;

            // Invert: 1.0 = device at top (penis out), 0.0 = device at bottom (penis in)
            outPos = 1f - filtered;
            outVelocity = velocity;
            return true;
        }

        public float? PredictPosition(float deltaSeconds) { return null; }
        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime) { }
        public void OnDestroy(StrokerSync plugin)
        {
            DestroyUI();
        }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            _plugin = plugin;

            // Clear all cached atom references — they're from the old scene
            _maleAtom = null;
            _femaleAtom = null;
            _cachedForAtom = null;
            _isObjectPenetrator = false;
            if (_toyAtomChooser != null) _toyAtomChooser.valNoCallback = "None";

            // Clear all cached components
            _cachedPenisTransforms = null;
            _cachedTipCollider = null;
            _penisLengthAverages = null;
            _runningLengthSum = 0f;

            _cachedLabiaTrigger = null;
            _cachedVaginaTrigger = null;
            _cachedAnusTrigger = null;
            _cachedMouthTarget = null;
            _cachedPelvis = null;
            _cachedReceiverLGrip = null;
            _cachedReceiverRGrip = null;
            _cachedMaleLGrip = null;
            _cachedMaleRGrip = null;

            // Reset filter state
            _filterInitialized = false;
            _isPenetrating = false;
            _prevPosition = 0f;
            _prevPositionTime = 0f;

            // Reset rolling calibration
            _rollingWindowHasData = false;
            _rollingWindowTimer = 0f;

            // Reset auto-calibration
            _autoCalibrating = false;

            // Reset chooser values
            _maleChooser.valNoCallback = "";
            _femaleChooser.valNoCallback = FEMALE_AUTO;

            SuperController.LogMessage("StrokerSync: Scene loaded — cleared all cached references. Re-scanning...");

            // Re-scan for atoms in the new scene after a short delay
            plugin.StartCoroutine(DelayedInit());
        }

        #region NOISE FILTERING

        /// <summary>
        /// Two-stage noise filter optimized for minimum latency:
        ///
        /// Stage 1 - Conditional spike rejection:
        ///   Compares each new sample against the previous output.
        ///   If the deviation is below SPIKE_THRESHOLD, the raw sample passes
        ///   through with ZERO latency (no median delay).
        ///   Only when a spike is detected does the median filter engage,
        ///   adding 1 frame of latency for just that sample.
        ///
        /// Stage 2 - Asymmetric EMA (optional):
        ///   Smoothing is direction-dependent for better haptic feel:
        ///   - Increasing depth (penetration "in"): 4x less smoothing → snappy attack
        ///   - Decreasing depth (withdrawal "out"): full smoothing → softer release
        ///   This matches the perceptual expectation where onset matters most.
        /// </summary>
        private float ApplyNoiseFilter(float raw)
        {
            // Initialize
            if (!_filterInitialized)
            {
                _medianBuf0 = _medianBuf1 = _medianBuf2 = raw;
                _emaState = raw;
                _prevFilterOutput = raw;
                _medianCount = 0;
                _filterInitialized = true;
                return raw;
            }

            // Always maintain the median buffer for spike detection
            _medianBuf2 = _medianBuf1;
            _medianBuf1 = _medianBuf0;
            _medianBuf0 = raw;
            _medianCount++;

            // Conditional spike rejection:
            // During smooth motion, pass raw through directly (zero latency).
            // Only engage the median filter when a spike is detected.
            float filtered;
            float deviation = Mathf.Abs(raw - _prevFilterOutput);

            if (deviation < SPIKE_THRESHOLD || _medianCount < 2)
            {
                // Smooth motion: zero-latency passthrough
                filtered = raw;
            }
            else
            {
                // Possible spike: use median to reject it
                float a = _medianBuf0, b = _medianBuf1, c = _medianBuf2;
                float lo = Mathf.Min(a, Mathf.Min(b, c));
                float hi = Mathf.Max(a, Mathf.Max(b, c));
                filtered = a + b + c - lo - hi;
            }

            // Asymmetric EMA post-filter
            float alpha = _noiseFilter.val;
            if (alpha > 0.001f)
            {
                // Direction-dependent smoothing:
                // Increasing depth (going in) → less smoothing (snappy attack)
                // Decreasing depth (pulling out) → more smoothing (softer release)
                float effectiveAlpha = (filtered > _emaState) ? alpha * 0.25f : alpha;
                _emaState = Mathf.Lerp(filtered, _emaState, effectiveAlpha);
                _prevFilterOutput = _emaState;
                return _emaState;
            }
            else
            {
                _emaState = filtered;
                _prevFilterOutput = filtered;
                return filtered;
            }
        }

        #endregion

        #region AUTO-CALIBRATION

        private void UpdateAutoCalibration(float rawDepth)
        {
            _autoCalTimer -= Time.deltaTime;
            if (rawDepth > 0.005f)
            {
                _autoCalMin = Mathf.Min(_autoCalMin, rawDepth);
                _autoCalMax = Mathf.Max(_autoCalMax, rawDepth);
            }
            if (_autoCalTimer <= 0f)
            {
                _autoCalibrating = false;
                if (_autoCalMax > _autoCalMin + 0.02f)
                {
                    float margin = (_autoCalMax - _autoCalMin) * 0.05f;
                    _penRangeMin.val = Mathf.Max(0f, _autoCalMin - margin);
                    _penRangeMax.val = Mathf.Min(1f, _autoCalMax + margin);
                    SuperController.LogMessage($"StrokerSync: Auto-calibrated range: {_penRangeMin.val:F2} - {_penRangeMax.val:F2}");
                }
                else
                {
                    SuperController.LogMessage("StrokerSync: Auto-calibration failed - not enough movement. Try again with more motion.");
                }
                if (_autoCalButton != null)
                    _autoCalButton.label = "Auto-Calibrate Range (8s)";
            }
            else if (_autoCalButton != null)
            {
                _autoCalButton.label = $"Calibrating... {_autoCalTimer:F0}s (move in/out!)";
            }
        }

        #endregion

        #region ROLLING CALIBRATION

        /// <summary>
        /// Continuously adapts the penetration range during play:
        ///
        /// EXPAND (instant): If rawDepth exceeds current range, expand immediately
        /// with a small margin. The user feels full range instantly on deeper strokes.
        ///
        /// CONTRACT (slow): Every ROLLING_WINDOW_SECS, compare the stored range
        /// to what was actually observed. If the stored range is wider, blend it
        /// toward the observed range by ROLLING_CONTRACT_RATE. This adapts to
        /// position changes over ~30-60 seconds without disrupting the experience.
        ///
        /// GUARD: Contraction only happens when penetration was active during the
        /// window. Pauses (position changes, cutscenes) freeze the range.
        /// </summary>
        private void UpdateRollingCalibration(float rawDepth)
        {
            bool isActive = rawDepth > 0.01f;

            // --- Instant expand ---
            if (isActive)
            {
                bool expanded = false;
                if (rawDepth < _penRangeMin.val)
                {
                    _penRangeMin.val = Mathf.Max(0f, rawDepth - ROLLING_EXPAND_MARGIN);
                    expanded = true;
                }
                if (rawDepth > _penRangeMax.val)
                {
                    _penRangeMax.val = Mathf.Min(1f, rawDepth + ROLLING_EXPAND_MARGIN);
                    expanded = true;
                }
                if (expanded)
                {
                    // SuperController.LogMessage($"StrokerSync: Rolling cal expanded range: {_penRangeMin.val:F2} - {_penRangeMax.val:F2}");
                }
            }

            // --- Track observed range within current window ---
            if (isActive)
            {
                if (!_rollingWindowHasData)
                {
                    _rollingWindowMin = rawDepth;
                    _rollingWindowMax = rawDepth;
                    _rollingWindowHasData = true;
                }
                else
                {
                    _rollingWindowMin = Mathf.Min(_rollingWindowMin, rawDepth);
                    _rollingWindowMax = Mathf.Max(_rollingWindowMax, rawDepth);
                }
            }

            // --- End of window: slow contraction ---
            _rollingWindowTimer -= Time.deltaTime;
            if (_rollingWindowTimer <= 0f)
            {
                _rollingWindowTimer = _rollingWindowSecs.val;

                if (_rollingWindowHasData)
                {
                    float observedMin = _rollingWindowMin;
                    float observedMax = _rollingWindowMax;

                    float newMin = _penRangeMin.val;
                    float newMax = _penRangeMax.val;

                    if (_fullStrokeMode != null && _fullStrokeMode.val)
                    {
                        // Full Stroke Mode: converge range to observed amplitude in BOTH
                        // directions — min chases observedMin (up or down), max chases
                        // observedMax (up or down).  This means a few windows of any
                        // consistent animation tightly fit the device range to actual movement.
                        newMin = Mathf.Lerp(_penRangeMin.val, observedMin - ROLLING_EXPAND_MARGIN, _rollingContractRate.val);
                        newMax = Mathf.Lerp(_penRangeMax.val, observedMax + ROLLING_EXPAND_MARGIN, _rollingContractRate.val);
                    }
                    else
                    {
                        // Normal mode: only contract (never widen via window)
                        if (observedMin > _penRangeMin.val)
                            newMin = Mathf.Lerp(_penRangeMin.val, observedMin - ROLLING_EXPAND_MARGIN, _rollingContractRate.val);

                        if (observedMax < _penRangeMax.val)
                            newMax = Mathf.Lerp(_penRangeMax.val, observedMax + ROLLING_EXPAND_MARGIN, _rollingContractRate.val);
                    }

                    // Safety: never contract to less than ROLLING_MIN_RANGE
                    if (newMax - newMin >= ROLLING_MIN_RANGE)
                    {
                        _penRangeMin.val = Mathf.Max(0f, newMin);
                        _penRangeMax.val = Mathf.Min(1f, newMax);
                    }
                }

                // Reset window
                _rollingWindowHasData = false;
            }
        }

        #endregion

        #region PENIS TRACKING (cached - runs every frame but no allocations)

        private bool UpdateMalePhysics()
        {
            // --- Object/toy path ---
            // If a toy atom is selected, it takes over as the penetrator entirely.
            // The male Person chooser is ignored when a toy is active.
            if (_toyAtomChooser != null && _toyAtomChooser.val != "None")
            {
                Atom toyAtom = Controller.GetAtomByUid(_toyAtomChooser.val);
                if (toyAtom == null || !toyAtom.on) return false;
                var ctrl = toyAtom.mainController;
                if (ctrl == null) return false;
                _penisBase = ctrl.transform.position;
                _penisDirection = GetToyAxis(ctrl.transform);
                _penisLength = (_toyLength != null) ? _toyLength.val : 0.18f;
                _penisTip = _penisBase + _penisDirection * _penisLength;
                _penisRadius = 0.02f;
                return true;
            }

            // --- Person atom path ---
            if (_maleAtom == null || !_maleAtom.on)
                return false;

            if (_cachedPenisTransforms == null || _cachedPenisTransforms.Count != 4 ||
                _cachedPenisTransforms[0] == null)
            {
                CacheMaleComponents();
                if (_cachedPenisTransforms == null || _cachedPenisTransforms.Count != 4)
                    return false;
            }

            float rawLength = 0f;
            for (int i = 0; i < 3; i++)
            {
                rawLength += Vector3.Distance(
                    _cachedPenisTransforms[i].position,
                    _cachedPenisTransforms[i + 1].position);
            }

            Transform origin = _cachedPenisTransforms[0];
            Vector3 originUp = origin.up;
            float seg0Len = (_cachedPenisTransforms[1].position - origin.position).magnitude;
            Vector3 baseOffset = -originUp * (seg0Len * 0.15f);

            _penisBase = origin.position + baseOffset + baseOffset.normalized * 0.015f;
            _penisDirection = originUp;

            rawLength += baseOffset.magnitude + 0.015f;
            if (_cachedTipCollider != null)
                rawLength += _cachedTipCollider.radius;

            _penisLength = UpdateRollingAverage(rawLength);
            _penisTip = _penisBase + _penisDirection * _penisLength;

            _penisRadius = 0.025f;
            if (_cachedPenisTransforms.Count > 1)
            {
                var baseCollider = _cachedPenisTransforms[1].GetComponent<CapsuleCollider>();
                if (baseCollider != null)
                    _penisRadius = Mathf.Max(baseCollider.radius, 0.016f);
            }

            return true;
        }

        private void CacheMaleComponents()
        {
            _cachedPenisTransforms = null;
            _cachedTipCollider = null;
            _penisLengthAverages = null;
            _runningLengthSum = 0f;
            _isObjectPenetrator = false;

            if (_maleAtom == null) return;

            // Non-Person atoms (dildos, sex toys, custom objects) don't have the
            // standard penis collider chain. Use main controller + configured axis/length.
            if (_maleAtom.type != "Person")
            {
                _isObjectPenetrator = true;
                SuperController.LogMessage($"StrokerSync: Object penetrator '{_maleAtom.uid}' (type={_maleAtom.type}). " +
                    $"Using axis={(_toyAxis != null ? _toyAxis.val : "+Y (Up)")}, length={(_toyLength != null ? _toyLength.val : 0.18f):F2}m");
                return;
            }

            var transforms = _maleAtom.GetComponentsInChildren<Collider>()
                .Where(c => c.enabled && PENIS_COLLIDER_NAMES.Contains(c.name))
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.transform)
                .ToList();

            if (transforms.Count == 4)
            {
                _cachedPenisTransforms = transforms;
                _cachedTipCollider = transforms[3].GetComponent<CapsuleCollider>();
                SuperController.LogMessage($"StrokerSync: Cached {transforms.Count} penis colliders on '{_maleAtom.uid}'");
            }
            else
            {
                var allColliderNames = _maleAtom.GetComponentsInChildren<Collider>()
                    .Where(c => c.name.Contains("Gen") && c.name.Contains("Hard"))
                    .Select(c => $"{c.name}(enabled={c.enabled})")
                    .ToList();
                SuperController.LogMessage($"StrokerSync: Penis cache FAILED on '{_maleAtom.uid}'. " +
                    $"Found {transforms.Count}/4. GenHard colliders: [{string.Join(", ", allColliderNames.ToArray())}]");
            }

            // Cache finger bones on the male atom for self-touch (masturbation).
            // lMid1/rMid1 = middle finger base joint = center of grip.
            _cachedMaleLGrip = null;
            _cachedMaleRGrip = null;
            foreach (var t in _maleAtom.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "lMid1") _cachedMaleLGrip = t;
                else if (t.name == "rMid1") _cachedMaleRGrip = t;
                if (_cachedMaleLGrip != null && _cachedMaleRGrip != null) break;
            }
        }

        /// <summary>
        /// Returns the local-space direction vector for an object penetrator based on
        /// the user's chosen axis. Defaults to +Y (up) which matches most VaM toys.
        /// The toy's main controller should be placed at the BASE (handle end).
        /// </summary>
        private Vector3 GetToyAxis(Transform t)
        {
            string axis = (_toyAxis != null) ? _toyAxis.val : "+Y (Up)";
            switch (axis)
            {
                case "+Z (Forward)": return t.forward;
                case "-Z (Back)": return -t.forward;
                case "+X (Right)": return t.right;
                case "-X (Left)": return -t.right;
                case "-Y (Down)": return -t.up;
                default: return t.up;   // "+Y (Up)"
            }
        }

        #endregion

        #region CACHE VALIDATION

        /// <summary>
        /// Verify cached component references are still alive. Atom reloads
        /// (clothing changes, appearance preset loads, etc.) can destroy
        /// rigidbodies and transforms without notification. If any reference
        /// is stale (destroyed Unity object), force a full re-cache.
        /// </summary>
        private void ValidateCachedComponents()
        {
            // Validate male components
            if (_cachedPenisTransforms != null)
            {
                bool stale = false;
                for (int i = 0; i < _cachedPenisTransforms.Count; i++)
                {
                    if (_cachedPenisTransforms[i] == null)
                    {
                        stale = true;
                        break;
                    }
                }
                if (stale)
                {
                    SuperController.LogMessage("StrokerSync: Male cache invalidated (component destroyed). Re-caching...");
                    _cachedPenisTransforms = null;
                    _cachedTipCollider = null;
                    _cachedMaleLGrip = null;
                    _cachedMaleRGrip = null;
                }
            }

            // Validate male hand refs separately (can go stale independently)
            if (_cachedMaleLGrip != null && _cachedMaleLGrip.gameObject == null) _cachedMaleLGrip = null;
            if (_cachedMaleRGrip != null && _cachedMaleRGrip.gameObject == null) _cachedMaleRGrip = null;

            // Validate female components — check if ANY cached ref went null
            // while others survived (partial invalidation)
            if (_femaleAtom != null)
            {
                bool hadAny = _cachedLabiaTrigger != null || _cachedVaginaTrigger != null ||
                              _cachedAnusTrigger != null || _cachedMouthTarget != null || _cachedPelvis != null ||
                              _cachedReceiverLGrip != null || _cachedReceiverRGrip != null;
                bool anyDestroyed = false;

                // Unity overloads == null for destroyed objects
                if (_cachedLabiaTrigger != null && _cachedLabiaTrigger.gameObject == null) anyDestroyed = true;
                if (_cachedVaginaTrigger != null && _cachedVaginaTrigger.gameObject == null) anyDestroyed = true;
                if (_cachedAnusTrigger != null && _cachedAnusTrigger.gameObject == null) anyDestroyed = true;
                if (_cachedMouthTarget != null && _cachedMouthTarget.gameObject == null) anyDestroyed = true;
                if (_cachedPelvis != null && _cachedPelvis.gameObject == null) anyDestroyed = true;
                if (_cachedReceiverLGrip != null && _cachedReceiverLGrip.gameObject == null) anyDestroyed = true;
                if (_cachedReceiverRGrip != null && _cachedReceiverRGrip.gameObject == null) anyDestroyed = true;

                if (hadAny && anyDestroyed)
                {
                    SuperController.LogMessage($"StrokerSync: Female cache invalidated on '{_femaleAtom.uid}'. Re-caching...");
                    CacheFemaleComponents(_femaleAtom);
                }
            }
        }

        #endregion

        #region FEMALE/TARGET TRACKING (cached)

        private bool UpdateFemaleTarget()
        {
            string mode = _targetChooser.val;

            // Hand tracking works with male atom's own hands too (masturbation),
            // so it doesn't require a receiver atom to be selected.
            if (mode == "Hand")
                return SetTargetHand();

            if (_femaleAtom == null || !_femaleAtom.on)
                return false;

            // Re-cache only when the atom changes (or on first selection for this atom).
            // The previous per-mode check caused CacheFemaleComponents to run EVERY FRAME
            // for male/futa receivers where vaginal triggers are absent — because
            // _cachedLabiaTrigger is always null on those atoms, keeping needsRecache true.
            // That meant GetComponentsInChildren<Rigidbody>(true) fired 60+ times per second,
            // which triggers VaM's internal breast-physics initialization on non-female atoms
            // and floods the log with breast-morph errors.
            // ValidateCachedComponents() already forces a re-cache on stale references,
            // so a single pass per atom is sufficient.
            if (_cachedForAtom != _femaleAtom)
                CacheFemaleComponents(_femaleAtom);

            if (mode == "Auto")
                return AutoSelectTarget();
            if (mode == "Vagina")
                return SetTargetVagina();
            if (mode == "Anus")
                return SetTargetAnus();
            if (mode == "Mouth")
                return SetTargetMouth();

            return false;
        }

        /// <summary>
        /// Compute vaginal canal direction by blending the labia→vagina trigger
        /// vector with the pelvis bone's up axis (which approximates the "into body"
        /// direction). The pelvis reference stabilizes tracking during extreme poses
        /// where the trigger positions alone give a poor canal axis estimate.
        /// Falls back to trigger-only direction if no pelvis bone was cached.
        /// </summary>
        private Vector3 GetVaginalDirection()
        {
            Vector3 triggerDir = (_cachedVaginaTrigger.transform.position - _cachedLabiaTrigger.transform.position).normalized;

            if (_cachedPelvis == null)
                return triggerDir;

            // Pelvis up in VAM generally points cranially (head-ward) through the body.
            // Blend 70% trigger direction + 30% pelvis up for pose stability.
            Vector3 pelvisUp = _cachedPelvis.up;
            Vector3 blended = (triggerDir * 0.7f + pelvisUp * 0.3f).normalized;
            return blended;
        }

        private bool AutoSelectTarget()
        {
            float bestDist = float.MaxValue;
            bool found = false;

            if (_cachedLabiaTrigger != null && _cachedVaginaTrigger != null)
            {
                float d = (_penisTip - _cachedLabiaTrigger.transform.position).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    _targetPosition = _cachedLabiaTrigger.transform.position;
                    _targetDirection = GetVaginalDirection();
                    _targetDepth = 0.24f;
                    found = true;
                }
            }

            if (_cachedAnusTrigger != null)
            {
                float d = (_penisTip - _cachedAnusTrigger.transform.position).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    _targetPosition = _cachedAnusTrigger.transform.position;
                    _targetDirection = -_cachedAnusTrigger.transform.up;
                    _targetDepth = 0.20f;
                    found = true;
                }
            }

            if (_cachedMouthTarget != null)
            {
                Vector3 mouthPos = GetMouthPosition();
                float d = (_penisTip - mouthPos).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    _targetPosition = mouthPos;
                    _targetDirection = _cachedMouthTarget.name.Contains("lowerJaw")
                        ? _cachedMouthTarget.forward : _penisDirection;
                    _targetDepth = 0.15f;
                    found = true;
                }
            }

            // Check hands (all cached: male self-touch + receiver handjob).
            // Only auto-select a hand if it's actually gripping the shaft.
            Transform[] hands = { _cachedMaleLGrip, _cachedMaleRGrip, _cachedReceiverLGrip, _cachedReceiverRGrip };
            float maxGripDistSqr = 0.06f * 0.06f;
            for (int i = 0; i < hands.Length; i++)
            {
                if (hands[i] == null) continue;

                Vector3 handPos = hands[i].position;

                // Verify the hand is actually on the shaft (lateral distance check)
                Vector3 baseToHand = handPos - _penisBase;
                float proj = Vector3.Dot(baseToHand, _penisDirection);
                if (proj < -0.02f || proj > _penisLength + 0.02f) continue;

                float projClamped = Mathf.Clamp(proj, 0f, _penisLength);
                Vector3 onAxis = _penisBase + _penisDirection * projClamped;
                if ((onAxis - handPos).sqrMagnitude > maxGripDistSqr) continue;

                float d = (handPos - _penisTip).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    _targetPosition = handPos;
                    _targetDirection = _penisDirection;
                    _targetDepth = _penisLength;
                    found = true;
                }
            }

            return found;
        }

        private bool SetTargetVagina()
        {
            if (_cachedLabiaTrigger == null || _cachedVaginaTrigger == null) return false;
            _targetPosition = _cachedLabiaTrigger.transform.position;
            _targetDirection = GetVaginalDirection();
            _targetDepth = 0.24f;
            return true;
        }

        private bool SetTargetAnus()
        {
            if (_cachedAnusTrigger == null) return false;
            _targetPosition = _cachedAnusTrigger.transform.position;
            _targetDirection = -_cachedAnusTrigger.transform.up;
            _targetDepth = 0.20f;
            return true;
        }

        private bool SetTargetMouth()
        {
            if (_cachedMouthTarget == null) return false;
            _targetPosition = GetMouthPosition();
            _targetDirection = _cachedMouthTarget.name.Contains("lowerJaw")
                ? _cachedMouthTarget.forward : _penisDirection;
            _targetDepth = 0.15f;
            return true;
        }

        /// <summary>
        /// Get the effective mouth opening position. tongue02/LipLowerMiddle are
        /// already at the mouth. lowerJaw (fallback) is at the jaw joint near the
        /// ear, so we offset it 9cm forward along the jaw's axis.
        /// </summary>
        private Vector3 GetMouthPosition()
        {
            if (_cachedMouthTarget.name.Contains("lowerJaw"))
                return _cachedMouthTarget.position + _cachedMouthTarget.forward * 0.09f;
            return _cachedMouthTarget.position;
        }

        /// <summary>
        /// Find the closest hand (from any cached atom) that is near the penis shaft.
        /// Works for both masturbation (male's own hands) and handjobs (receiver's hands).
        /// The existing CalculatePenetrationDepth handles converting hand position
        /// on the shaft into a stroke position value.
        /// </summary>
        private bool SetTargetHand()
        {
            Transform bestHand = null;
            float bestDistSqr = float.MaxValue;

            // Maximum distance from penis axis to consider a hand as "gripping"
            float maxGripDist = 0.06f; // ~6cm — finger bones are close to the shaft when gripping
            float maxGripDistSqr = maxGripDist * maxGripDist;

            // Check all cached hands: male (self-touch) + receiver (handjob)
            Transform[] hands = { _cachedMaleLGrip, _cachedMaleRGrip, _cachedReceiverLGrip, _cachedReceiverRGrip };
            for (int i = 0; i < hands.Length; i++)
            {
                if (hands[i] == null) continue;

                Vector3 handPos = hands[i].position;

                // Project hand onto penis axis to check lateral distance
                Vector3 baseToHand = handPos - _penisBase;
                float projection = Vector3.Dot(baseToHand, _penisDirection);

                // Hand must be along the shaft (not behind base or past tip)
                if (projection < -0.02f || projection > _penisLength + 0.02f)
                    continue;

                float projClamped = Mathf.Clamp(projection, 0f, _penisLength);
                Vector3 closestOnAxis = _penisBase + _penisDirection * projClamped;
                float lateralDistSqr = (closestOnAxis - handPos).sqrMagnitude;

                // Must be within grip distance of the shaft
                if (lateralDistSqr > maxGripDistSqr)
                    continue;

                // Pick the closest hand
                float distSqr = (handPos - _penisTip).sqrMagnitude;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestHand = hands[i];
                }
            }

            if (bestHand == null)
                return false;

            _targetPosition = bestHand.position;
            _targetDirection = _penisDirection; // Not used by depth calc, but keep consistent
            _targetDepth = _penisLength;
            return true;
        }

        private void CacheFemaleComponents(Atom atom)
        {
            _cachedLabiaTrigger = null;
            _cachedVaginaTrigger = null;
            _cachedAnusTrigger = null;
            _cachedMouthTarget = null;
            _cachedPelvis = null;
            _cachedReceiverLGrip = null;
            _cachedReceiverRGrip = null;

            if (atom == null) return;

            // IMPORTANT: Use (true) to include inactive GameObjects.
            // On futa models, vaginal trigger objects may be deactivated
            // when penis morphs are enabled, but the rigidbodies still exist
            // and still get physics updates from VAM's internal systems.
            foreach (var rb in atom.GetComponentsInChildren<Rigidbody>(true))
            {
                string n = rb.name;
                if (n == "LabiaTrigger") _cachedLabiaTrigger = rb;
                else if (n == "VaginaTrigger") _cachedVaginaTrigger = rb;
                else if (n == "_JointAr" || n == "_JointAl") _cachedAnusTrigger = rb;
                else if (n == "pelvis" || n == "hip") _cachedPelvis = rb.transform;
                // MouthTrigger is a physics trigger rigidbody placed exactly at the
                // mouth opening — same system as LabiaTrigger. More reliable than
                // tongue/lip bones because it sits at a fixed anatomical reference
                // point and is unaffected by tongue animation or mouth-open expressions.
                else if ((n == "MouthTrigger" || n == "mouthTrigger" || n == "LipTrigger")
                         && _cachedMouthTarget == null)
                    _cachedMouthTarget = rb.transform;
            }

            // Search Transforms for mouth and finger bones.
            // Rigidbody search won't find these — they're skeletal transforms.
            // Do mouth in priority order: tongue02 is best (sits at opening),
            // then lip bones, then jaw as last resort.
            // Single-pass with if/else if was broken: whichever bone appeared first
            // in transform order won, regardless of intended priority.
            var allTransforms = atom.GetComponentsInChildren<Transform>(true);
            foreach (var t in allTransforms)
            {
                string n = t.name;
                if (n == "lMid1") _cachedReceiverLGrip = t;
                else if (n == "rMid1") _cachedReceiverRGrip = t;
            }

            // Mouth: try bones in descending preference order.
            // LipLowerMiddle sits right at the mouth opening (zero offset) — the correct
            // reference for depth calculation. tongue02 is 4-5cm INSIDE the mouth, which
            // creates a dead zone and compresses the depth scale significantly.
            string[] mouthBoneNames = { "LipLowerMiddle", "lipLowerMiddle", "tongue02", "MouthCenter", "lipsLowerInner" };
            foreach (string boneName in mouthBoneNames)
            {
                foreach (var t in allTransforms)
                {
                    if (t.name == boneName)
                    {
                        _cachedMouthTarget = t;
                        break;
                    }
                }
                if (_cachedMouthTarget != null) break;
            }

            // Last-resort fallback: lowerJaw (position will be offset in GetMouthPosition)
            if (_cachedMouthTarget == null)
            {
                foreach (var t in allTransforms)
                {
                    if (t.name.Contains("lowerJaw"))
                    {
                        _cachedMouthTarget = t;
                        break;
                    }
                }
            }

            // Record which atom we just cached so UpdateFemaleTarget doesn't re-cache
            // on the next frame when a component is legitimately absent (e.g. male receiver).
            _cachedForAtom = atom;

            // Diagnostic: if we failed to find triggers, log what rigidbodies DO exist.
            // Use includeInactive=false here — we've already done the inactive scan above,
            // and a second includeInactive=true call on non-female atoms can trigger VaM's
            // breast-physics initialization again unnecessarily.
            if (_cachedLabiaTrigger == null && _cachedVaginaTrigger == null && _cachedAnusTrigger == null)
            {
                var rbNames = new List<string>();
                foreach (var rb in atom.GetComponentsInChildren<Rigidbody>(false))
                {
                    string n = rb.name;
                    if (n.Contains("Trigger") || n.Contains("Joint") || n.Contains("anus") ||
                        n.Contains("Anus") || n.Contains("Vagin") || n.Contains("Labi") ||
                        n.Contains("Mouth") || n.Contains("mouth") || n.Contains("Lip") ||
                        n.Contains("Gen") || n.Contains("Pelvi") || n.Contains("Hip"))
                    {
                        rbNames.Add($"{n}(active={rb.gameObject.activeInHierarchy})");
                    }
                }
                SuperController.LogMessage($"StrokerSync: No vagina/anus triggers found on '{atom.uid}'. " +
                    $"Relevant rigidbodies: [{string.Join(", ", rbNames.ToArray())}]");
            }
            else
            {
                SuperController.LogMessage($"StrokerSync: Cached receiver on '{atom.uid}': " +
                    $"labia={(_cachedLabiaTrigger != null)}, vagina={(_cachedVaginaTrigger != null)}, " +
                    $"anus={(_cachedAnusTrigger != null)}, mouth={(_cachedMouthTarget != null)}({_cachedMouthTarget?.name}), " +
                    $"pelvis={(_cachedPelvis != null)}, " +
                    $"lGrip={(_cachedReceiverLGrip != null)}({_cachedReceiverLGrip?.name}), " +
                    $"rGrip={(_cachedReceiverRGrip != null)}({_cachedReceiverRGrip?.name})");
            }
        }

        #endregion

        #region PENETRATION DEPTH CALCULATION

        private float CalculatePenetrationDepth()
        {
            float scaledLength = _penisLength * _referenceLengthScale.val;

            float maxProximity = 0.08f * _referenceRadiusScale.val;
            float tipToTargetSqr = (_penisTip - _targetPosition).sqrMagnitude;
            float baseToTargetSqr = (_penisBase - _targetPosition).sqrMagnitude;

            float maxDistSqr = (scaledLength + maxProximity) * (scaledLength + maxProximity);
            if (tipToTargetSqr > maxDistSqr && baseToTargetSqr > maxDistSqr)
                return 0f;

            Vector3 baseToTarget = _targetPosition - _penisBase;
            float projectionOnPenis = Vector3.Dot(baseToTarget, _penisDirection);

            float projClamped = Mathf.Clamp(projectionOnPenis, 0f, scaledLength);
            Vector3 closestPointOnAxis = _penisBase + _penisDirection * projClamped;
            float lateralDistSqr = (closestPointOnAxis - _targetPosition).sqrMagnitude;

            if (lateralDistSqr > maxProximity * maxProximity)
                return 0f;

            // Normalize by the SMALLER of penis length and target cavity depth.
            // For vagina/anus: targetDepth (24cm/20cm) > typical scaledLength, so penis
            //   length wins — full insertion maps to 1.0. (Existing behaviour, unchanged.)
            // For mouth: targetDepth (15cm) < typical penis length, so mouth depth wins —
            //   inserting 15cm fills the scale to 1.0 instead of staying stuck near 0.
            float effectiveDepth = Mathf.Min(scaledLength, _targetDepth);
            if (effectiveDepth < 0.001f) effectiveDepth = scaledLength; // safety guard
            float penetrationAmount = scaledLength - projectionOnPenis;
            float strokePosition = Mathf.Clamp01(penetrationAmount / effectiveDepth);
            return strokePosition;
        }

        #endregion

        #region UTILITIES

        private void LogDebugThrottled(string msg)
        {
            _debugLogTimer -= Time.deltaTime;
            if (_debugLogTimer <= 0f)
            {
                _debugLogTimer = DEBUG_LOG_INTERVAL;
                SuperController.LogMessage("StrokerSync: " + msg);
            }
        }

        private float UpdateRollingAverage(float newVal)
        {
            if (_penisLengthAverages == null)
            {
                _penisLengthAverages = new float[PENIS_LENGTH_AVERAGES_COUNT];
                for (int i = 0; i < PENIS_LENGTH_AVERAGES_COUNT; i++)
                    _penisLengthAverages[i] = newVal;
                _runningLengthSum = newVal * PENIS_LENGTH_AVERAGES_COUNT;
                _penisLengthAverageIndex = 0;
            }

            _runningLengthSum -= _penisLengthAverages[_penisLengthAverageIndex];
            _runningLengthSum += newVal;
            _penisLengthAverages[_penisLengthAverageIndex] = newVal;
            _penisLengthAverageIndex = (_penisLengthAverageIndex + 1) % PENIS_LENGTH_AVERAGES_COUNT;

            return _runningLengthSum / PENIS_LENGTH_AVERAGES_COUNT;
        }

        #endregion

        #region AUTO-SELECT (runs on timer, not every frame)

        private void RunAutoSelect()
        {
            Atom bestReceiver = null;
            float bestDistSqr = float.MaxValue;
            int receiversFound = 0;

            foreach (var a in Controller.GetAtoms())
            {
                if (a.type != "Person" || !a.on || a == _maleAtom) continue;

                // Check for receiver anatomy — include inactive GameObjects
                // (futa models may have inactive vaginal trigger objects)
                bool hasReceiver = false;
                foreach (var rb in a.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb.name == "LabiaTrigger" || rb.name == "_JointAr" || rb.name == "_JointAl")
                    {
                        hasReceiver = true;
                        break;
                    }
                }
                // Fallback: accept jaw (mouth) as a receiver target
                if (!hasReceiver)
                {
                    foreach (var t in a.GetComponentsInChildren<Transform>(true))
                    {
                        if (t.name.Contains("lowerJaw"))
                        {
                            hasReceiver = true;
                            break;
                        }
                    }
                }
                if (!hasReceiver) continue;

                receiversFound++;

                Rigidbody refBone = null;
                foreach (var rb in a.linkableRigidbodies)
                {
                    if (rb.name == "hip" || rb.name == "pelvis" || rb.name == "abdomen")
                    {
                        refBone = rb;
                        break;
                    }
                }

                float distSqr;
                if (refBone != null)
                    distSqr = (_penisTip - refBone.transform.position).sqrMagnitude;
                else
                    distSqr = (_penisTip - a.mainController.transform.position).sqrMagnitude;

                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestReceiver = a;
                }
            }

            if (bestReceiver != null && bestReceiver != _femaleAtom)
            {
                _femaleAtom = bestReceiver;
                CacheFemaleComponents(bestReceiver);
                SuperController.LogMessage($"StrokerSync: Auto-selected receiver '{bestReceiver.uid}' (dist={Mathf.Sqrt(bestDistSqr):F2}m, " +
                    $"labia={(_cachedLabiaTrigger != null)}, vagina={(_cachedVaginaTrigger != null)}, " +
                    $"anus={(_cachedAnusTrigger != null)}, jaw={(_cachedMouthTarget != null)})");
            }
            else if (bestReceiver == null && _femaleAtom == null)
            {
                LogDebugThrottled($"MaleFemale: AutoSelect found {receiversFound} receiver atom(s) but none selected. " +
                    (receiversFound == 0 ? "No Person atoms with receiver anatomy in scene." : "All rejected (same as penetrator?)."));
            }
        }

        #endregion

        #region ATOM LIST / UI

        private static bool HasPenisAnatomy(Atom a)
        {
            foreach (var c in a.GetComponentsInChildren<Collider>())
            {
                if (c.enabled && PENIS_COLLIDER_NAMES.Contains(c.name))
                    return true;
            }
            return false;
        }

        private static bool HasReceiverAnatomy(Atom a)
        {
            // Include inactive GameObjects — futa models may have
            // deactivated vaginal trigger objects
            foreach (var rb in a.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.name == "LabiaTrigger" || rb.name == "VaginaTrigger" ||
                    rb.name == "_JointAr" || rb.name == "_JointAl")
                    return true;
            }
            // All Person atoms have a jaw — accept as receiver (mouth target)
            foreach (var t in a.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.Contains("lowerJaw"))
                    return true;
            }
            return false;
        }

        private void FindMales()
        {
            var current = _maleChooser.val;
            var uids = new List<string> { "None" };

            foreach (var a in Controller.GetAtoms())
            {
                if (a.type != "Person" || !a.on) continue;
                if (HasPenisAnatomy(a))
                    uids.Add(a.uid);
            }

            _maleChooser.choices = uids;

            if (!uids.Contains(current))
            {
                if (_plugin.containingAtom.type == "Person" && HasPenisAnatomy(_plugin.containingAtom))
                    current = _plugin.containingAtom.uid;

                if (!uids.Contains(current))
                    current = uids.Count > 1 ? uids[1] : "None";
            }

            MaleChooserCallback(current);
        }

        private void FindToys()
        {
            if (_toyAtomChooser == null) return;
            var current = _toyAtomChooser.val;
            var uids = new List<string> { "None" };

            foreach (var a in Controller.GetAtoms())
            {
                // Only non-Person atoms with a main controller (scene objects / custom assets)
                if (a.type == "Person" || !a.on || a.mainController == null) continue;
                uids.Add(a.uid);
            }

            _toyAtomChooser.choices = uids;
            if (!uids.Contains(current))
                _toyAtomChooser.valNoCallback = "None";
        }

        private void ToyAtomChooserCallback(string s)
        {
            if (_toyAtomChooser != null)
                _toyAtomChooser.valNoCallback = s;
            // Auto-detect tip direction and length whenever a new toy is selected.
            // The user can always override with the axis popup or re-detect button.
            if (s != "None")
                AutoDetectToyAxis();
        }

        /// <summary>
        /// Scans every collider on the selected toy atom and finds the point furthest
        /// from the main controller. That point is the tip. The world-space direction
        /// from main controller → furthest point is then snapped to the nearest local
        /// axis (+Y, -Y, +Z, -Z, +X, -X) and stored in _toyAxis. The straight-line
        /// distance is stored in _toyLength.
        ///
        /// Works best when the main controller sits at the handle/base end, which is
        /// the convention for most VaM toy assets.
        /// </summary>
        public void AutoDetectToyAxis()
        {
            if (_toyAtomChooser == null || _toyAtomChooser.val == "None") return;
            Atom toyAtom = Controller.GetAtomByUid(_toyAtomChooser.val);
            if (toyAtom == null || toyAtom.mainController == null) return;

            Transform ctrl = toyAtom.mainController.transform;
            Vector3 ctrlPos = ctrl.position;

            float maxDistSqr = -1f;
            Vector3 furthestPoint = ctrlPos;

            // Check all enabled colliders — sample all 8 corners of each AABB.
            // This catches elongated capsules and off-center meshes reliably.
            foreach (var col in toyAtom.GetComponentsInChildren<Collider>())
            {
                if (!col.enabled) continue;
                Bounds b = col.bounds;
                // 8 AABB corners
                for (int xi = 0; xi < 2; xi++)
                    for (int yi = 0; yi < 2; yi++)
                        for (int zi = 0; zi < 2; zi++)
                        {
                            Vector3 corner = new Vector3(
                                xi == 0 ? b.min.x : b.max.x,
                                yi == 0 ? b.min.y : b.max.y,
                                zi == 0 ? b.min.z : b.max.z);
                            float dSqr = (corner - ctrlPos).sqrMagnitude;
                            if (dSqr > maxDistSqr)
                            {
                                maxDistSqr = dSqr;
                                furthestPoint = corner;
                            }
                        }
            }

            if (maxDistSqr < 0.0001f)
            {
                SuperController.LogMessage($"StrokerSync: Auto-detect failed on '{toyAtom.uid}' — no colliders found.");
                return;
            }

            Vector3 worldDir = (furthestPoint - ctrlPos).normalized;
            string bestAxis = WorldDirToNearestLocalAxis(ctrl, worldDir);
            float length = Mathf.Clamp(Mathf.Sqrt(maxDistSqr), 0.03f, 0.45f);

            if (_toyAxis != null) _toyAxis.val = bestAxis;
            if (_toyLength != null) _toyLength.val = length;

            SuperController.LogMessage($"StrokerSync: Auto-detected toy axis={bestAxis}, length={length:F3}m on '{toyAtom.uid}'");
        }

        /// <summary>
        /// Returns the name of the local axis of <paramref name="t"/> that best aligns
        /// with <paramref name="worldDir"/>. Used to snap a detected world direction to
        /// one of the six canonical axis choices in the UI dropdown.
        /// </summary>
        private static string WorldDirToNearestLocalAxis(Transform t, Vector3 worldDir)
        {
            float dotUp = Vector3.Dot(worldDir, t.up);
            float dotDown = Vector3.Dot(worldDir, -t.up);
            float dotFwd = Vector3.Dot(worldDir, t.forward);
            float dotBack = Vector3.Dot(worldDir, -t.forward);
            float dotRight = Vector3.Dot(worldDir, t.right);
            float dotLeft = Vector3.Dot(worldDir, -t.right);

            float best = dotUp;
            string name = "+Y (Up)";
            if (dotDown > best) { best = dotDown; name = "-Y (Down)"; }
            if (dotFwd > best) { best = dotFwd; name = "+Z (Forward)"; }
            if (dotBack > best) { best = dotBack; name = "-Z (Back)"; }
            if (dotRight > best) { best = dotRight; name = "+X (Right)"; }
            if (dotLeft > best) { name = "-X (Left)"; }
            return name;
        }

        private void FindFemales()
        {
            var femaleOptions = new List<string> { FEMALE_AUTO, "None" };

            foreach (var a in Controller.GetAtoms())
            {
                if (a.type != "Person" || !a.on) continue;
                if (HasReceiverAnatomy(a))
                    femaleOptions.Add(a.uid);
            }

            _femaleChooser.choices = femaleOptions;

            string current = _femaleChooser.val;
            if (current != FEMALE_AUTO && !femaleOptions.Contains(current))
                FemaleChooserCallback(FEMALE_AUTO);
        }

        private void MaleChooserCallback(string s)
        {
            _maleAtom = Controller.GetAtomByUid(s);
            _cachedPenisTransforms = null;
            _maleChooser.valNoCallback = _maleAtom != null ? s : "None";
            SuperController.LogMessage($"StrokerSync: MaleChooserCallback('{s}') -> atom={((_maleAtom != null) ? _maleAtom.uid : "NULL")}");
        }

        private void FemaleChooserCallback(string s)
        {
            if (s == FEMALE_AUTO)
            {
                _femaleAtom = null;
                _cachedForAtom = null;
                // Clear ALL cached female components — not just labiatrigger —
                // so stale references from the previous selection don't survive.
                _cachedLabiaTrigger = null;
                _cachedVaginaTrigger = null;
                _cachedAnusTrigger = null;
                _cachedMouthTarget = null;
                _cachedPelvis = null;
                _cachedReceiverLGrip = null;
                _cachedReceiverRGrip = null;
            }
            else
            {
                _femaleAtom = Controller.GetAtomByUid(s);
                if (_femaleAtom != null)
                    CacheFemaleComponents(_femaleAtom);
            }
            _femaleChooser.valNoCallback = s;
        }

        /// <summary>
        /// Create the LEFT-column detection/selection/range UI.
        /// Called by CombinedSource.BuildMaleFemaleUI when operating in tab mode.
        /// Calibration controls and the signal display are built directly in
        /// BuildStrokerTab so their column position can be controlled precisely.
        /// </summary>
        public void CreateDetectionUI(StrokerSync plugin)
        {
            _uiCleanup.Clear();

            // --- Character / Toy Selection ---
            var malePopup = plugin.CreateScrollablePopup(_maleChooser);
            malePopup.label = "Penetrator (has penis)";
            malePopup.popup.onOpenPopupHandlers += () => FindMales();
            _uiCleanup.Add(() => plugin.RemovePopup(malePopup));

            var toyAtomPopup = plugin.CreateScrollablePopup(_toyAtomChooser);
            toyAtomPopup.label = "Toy/Object Penetrator (overrides male if set)";
            toyAtomPopup.popup.onOpenPopupHandlers += () => FindToys();
            _uiCleanup.Add(() => plugin.RemovePopup(toyAtomPopup));

            var femalePopup = plugin.CreateScrollablePopup(_femaleChooser);
            femalePopup.label = "Receiver (Auto = closest)";
            femalePopup.popup.onOpenPopupHandlers += () => FindFemales();
            _uiCleanup.Add(() => plugin.RemovePopup(femalePopup));

            var targetPopup = plugin.CreateScrollablePopup(_targetChooser);
            targetPopup.label = "Target Orifice";
            _uiCleanup.Add(() => plugin.RemovePopup(targetPopup));

            // --- Stroke Range ---
            var rangeMinSlider = plugin.CreateSlider(_penRangeMin);
            rangeMinSlider.label = "Pen Range Min (out position)";
            _uiCleanup.Add(() => plugin.RemoveSlider(rangeMinSlider));

            var rangeMaxSlider = plugin.CreateSlider(_penRangeMax);
            rangeMaxSlider.label = "Pen Range Max (in position)";
            _uiCleanup.Add(() => plugin.RemoveSlider(rangeMaxSlider));

            var fullStrokeToggle = plugin.CreateToggle(_fullStrokeMode);
            fullStrokeToggle.label = "Full Stroke Mode (compress range)";
            _uiCleanup.Add(() => plugin.RemoveToggle(fullStrokeToggle));

            var sp1 = plugin.CreateSpacer();
            sp1.height = 8f;
            _uiCleanup.Add(() => plugin.RemoveSpacer(sp1));

            // --- Calibration title ---
            var calTitle = plugin.CreateSpacer();
            calTitle.height = 40f;
            _uiCleanup.Add(() => plugin.RemoveSpacer(calTitle));
            var calText = calTitle.gameObject.AddComponent<UnityEngine.UI.Text>();
            calText.text = "Calibration";
            calText.fontSize = 28;
            calText.fontStyle = UnityEngine.FontStyle.Bold;
            calText.color = new Color(0.95f, 0.9f, 0.92f);
            calText.alignment = TextAnchor.MiddleLeft;
            try
            {
                var src = plugin.manager.configurableTextFieldPrefab
                    .GetComponentInChildren<UnityEngine.UI.Text>();
                if (src != null) calText.font = src.font;
            }
            catch { }

            // --- Calibration controls ---
            var lengthSlider = plugin.CreateSlider(_referenceLengthScale);
            lengthSlider.label = "Length Calibration";
            _uiCleanup.Add(() => plugin.RemoveSlider(lengthSlider));

            var radiusSlider = plugin.CreateSlider(_referenceRadiusScale);
            radiusSlider.label = "Detection Radius (default 1.0)";
            _uiCleanup.Add(() => plugin.RemoveSlider(radiusSlider));

            _autoCalButton = plugin.CreateButton("Auto-Calibrate Range (8s)");
            _autoCalButton.button.onClick.AddListener(() => StartAutoCalibration());
            _uiCleanup.Add(() => plugin.RemoveButton(_autoCalButton));

            var reDetectBtn = plugin.CreateButton("Re-detect Toy Tip & Length");
            reDetectBtn.button.onClick.AddListener(() => AutoDetectToyAxis());
            _uiCleanup.Add(() => plugin.RemoveButton(reDetectBtn));

            // Manual overrides — auto-filled by AutoDetectToyAxis, editable by hand.
            var toyAxisPopup = plugin.CreateScrollablePopup(_toyAxis);
            toyAxisPopup.label = "Toy Tip Direction (auto-detected)";
            _uiCleanup.Add(() => plugin.RemovePopup(toyAxisPopup));

            var toyLengthSlider = plugin.CreateSlider(_toyLength);
            toyLengthSlider.label = "Toy Length m (auto-detected)";
            _uiCleanup.Add(() => plugin.RemoveSlider(toyLengthSlider));
        }

        /// <summary>
        /// Legacy full-page UI (all controls in left column).
        /// Used only when MaleFemaleSource is instantiated outside the tab system.
        /// </summary>
        public void CreateUI(StrokerSync plugin)
        {
            CreateDetectionUI(plugin);

            var display = plugin.CreateTextField(_penRangeDisplay);
            display.height = 40f;
            _uiCleanup.Add(() => plugin.RemoveTextField(display));

            var noiseSlider = plugin.CreateSlider(_noiseFilter);
            noiseSlider.label = "Noise Filter (0=off, 0.2=moderate)";
            _uiCleanup.Add(() => plugin.RemoveSlider(noiseSlider));

            var sp = plugin.CreateSpacer();
            _uiCleanup.Add(() => plugin.RemoveSpacer(sp));

            var autoCalToggle = plugin.CreateToggle(_autoCalOnLoad);
            autoCalToggle.label = "Auto-Calibrate on Scene Load";
            _uiCleanup.Add(() => plugin.RemoveToggle(autoCalToggle));

            var autoCalDelaySlider = plugin.CreateSlider(_autoCalDelay);
            autoCalDelaySlider.label = "Auto-Cal Delay (seconds)";
            _uiCleanup.Add(() => plugin.RemoveSlider(autoCalDelaySlider));

            var rollingCalToggle = plugin.CreateToggle(_rollingCalEnabled);
            rollingCalToggle.label = "Rolling Calibration (continuous)";
            _uiCleanup.Add(() => plugin.RemoveToggle(rollingCalToggle));

            var rollingWindowSlider = plugin.CreateSlider(_rollingWindowSecs);
            rollingWindowSlider.label = "Rolling Cal Window (seconds)";
            _uiCleanup.Add(() => plugin.RemoveSlider(rollingWindowSlider));

            var rollingContractSlider = plugin.CreateSlider(_rollingContractRate);
            rollingContractSlider.label = "Rolling Cal Rate (per window)";
            _uiCleanup.Add(() => plugin.RemoveSlider(rollingContractSlider));
        }

        private void StartAutoCalibration()
        {
            _autoCalibrating = true;
            _autoCalTimer = AUTO_CAL_DURATION;
            _autoCalMin = 1f;
            _autoCalMax = 0f;
            if (_autoCalButton != null)
                _autoCalButton.label = $"Calibrating... {AUTO_CAL_DURATION:F0}s (move in/out!)";
            SuperController.LogMessage("StrokerSync: Auto-calibrating...");
        }

        private IEnumerator DelayedInit()
        {
            yield return new WaitForSeconds(1.0f);
            FindMales();
            FindFemales();

            // Auto-calibrate on scene load if enabled
            if (_autoCalOnLoad != null && _autoCalOnLoad.val)
            {
                float delay = (_autoCalDelay != null) ? _autoCalDelay.val : 10f;
                SuperController.LogMessage($"StrokerSync: Auto-calibration will start in {delay:F0}s...");
                yield return new WaitForSeconds(delay);
                StartAutoCalibration();
            }
        }

        #endregion
    }
}