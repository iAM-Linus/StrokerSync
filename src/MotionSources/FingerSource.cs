using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Finger-based motion source — detects fingertip penetration of the vaginal
    /// trigger zone and clitoral / labial stimulation.
    ///
    /// SIGNAL PATH (penetration):
    ///   Fingertip position projected onto vaginal axis
    ///   → Normalized depth (0 = not inserted, 1 = fully inserted)
    ///   → Hysteresis
    ///   → Invert (1.0 = device at top / finger out, 0.0 = device at bottom / finger in)
    ///   → LinearCmd via StrokerSync
    ///
    /// SIGNAL PATH (clitoral):
    ///   Per-frame proximity check: is any fingertip within _clitoralRadius of the
    ///   clitoral zone centre (LabiaTrigger + pelvis-relative offset)?
    ///   The zone is a kinematic GO repositioned via MovePosition each frame; its
    ///   visual indicator sphere lets you see and tune the position in the viewport.
    ///   (Unity OnTriggerEnter is NOT used — finger bone rigidbodies carry no collider
    ///   geometry so trigger callbacks never fire for them.)
    ///   Movement speed of the closest finger in zone → clitoral intensity (0–1)
    ///   → Exposed via ClitoralIntensity property
    ///   → StrokerSync reads this and calls SendVibrateAll() when vibration mode != Off
    ///
    /// FINGER BONE NAMES: L_Finger_Index_C, L_Finger_Middle_C (left)
    ///                    R_Finger_Index_C, R_Finger_Middle_C (right)
    ///   (_C = distal phalanx / fingertip; found on CoreControl, [CameraRig], Person atoms)
    /// </summary>
    public class FingerSource : IMotionSource
    {
        // =====================================================================
        // NESTED: Clitoral Trigger Zone
        // =====================================================================

        /// <summary>
        /// MonoBehaviour attached to the dynamically-created clitoral trigger sphere.
        /// Tracks which finger rigidbodies are currently inside it using Unity physics
        /// OnTriggerEnter / OnTriggerExit callbacks. Thread-safe because Unity only
        /// calls these from the main thread during the physics step.
        /// </summary>
        private class ClitoralTriggerZone : MonoBehaviour
        {
            private HashSet<string> _fingerNames;

            // Track by rigidbody instance ID to handle multi-collider fingers correctly.
            private readonly HashSet<int> _activeRbIds = new HashSet<int>();

            public bool      IsActive            => _activeRbIds.Count > 0;
            public Transform ActiveFingerTransform { get; private set; }

            public void Initialize(string[] fingerNames)
            {
                _fingerNames = new HashSet<string>(fingerNames);
            }

            private void OnTriggerEnter(Collider other)
            {
                Rigidbody rb = other.attachedRigidbody;
                if (rb == null || _fingerNames == null) return;
                if (!_fingerNames.Contains(rb.name)) return;

                _activeRbIds.Add(rb.GetInstanceID());
                ActiveFingerTransform = rb.transform;
            }

            private void OnTriggerExit(Collider other)
            {
                Rigidbody rb = other.attachedRigidbody;
                if (rb == null || _fingerNames == null) return;
                if (!_fingerNames.Contains(rb.name)) return;

                _activeRbIds.Remove(rb.GetInstanceID());

                if (_activeRbIds.Count == 0)
                {
                    ActiveFingerTransform = null;
                }
                else
                {
                    // Keep last known; will be replaced on next Enter if needed.
                    // (Multiple fingers can be in the zone simultaneously.)
                }
            }

            /// <summary>Called when the source is destroyed or scene reloads.</summary>
            public void Reset()
            {
                _activeRbIds.Clear();
                ActiveFingerTransform = null;
            }
        }

        // =====================================================================
        // CONSTANTS
        // =====================================================================

        private const string FEMALE_AUTO = "*Auto*";

        // Fingertip rigidbody names — two naming conventions exist in VaM:
        //
        // Person atom bones (male/female characters in animations):
        //   lIndex3, lMid3  (left)   rIndex3, rMid3  (right)
        //   VaM's UI shows these as "[rb] IIndex3" / "[rb] IMid3" — the [rb] prefix
        //   is just a UI label; the actual Rigidbody.name has no prefix.
        //
        // VR controller bones (CoreControl / [CameraRig] — first-person VR hands):
        //   L_Finger_Index_C, L_Finger_Middle_C  (left)
        //   R_Finger_Index_C, R_Finger_Middle_C  (right)
        //
        // Both sets are included so the source works for both animated scenes and VR.
        private static readonly string[] FINGER_RB_NAMES =
        {
            // Person atom (character / animation)
            "lIndex3",
            "lMid3",
            "rIndex3",
            "rMid3",
            // VR controller (CoreControl / CameraRig)
            "L_Finger_Index_C",
            "L_Finger_Middle_C",
            "R_Finger_Index_C",
            "R_Finger_Middle_C",
        };

        // Maximum lateral distance from vaginal axis to count as "penetrating" (m).
        private const float LATERAL_MAX_DIST = 0.05f;

        // Maximum distance from LabiaTrigger to search for a penetrating finger (m).
        // Larger than the clitoral trigger radius so fingers reaching from any angle
        // are caught; axial projection handles actual depth.
        private const float PENETRATION_SEARCH_RADIUS = 0.15f;

        // Hysteresis thresholds (fraction of effective finger depth).
        private const float PENETRATION_ON_TH  = 0.05f;
        private const float PENETRATION_OFF_TH = 0.01f;

        private const float AUTO_SELECT_INTERVAL      = 0.5f;
        private const float FINGER_CACHE_INTERVAL     = 2.0f;
        private const float CACHE_VALIDATION_INTERVAL = 2.0f;

        // =====================================================================
        // FIELDS
        // =====================================================================

        private StrokerSync _plugin;
        private SuperController Controller => SuperController.singleton;

        // --- Female Atom ---
        private Atom _femaleAtom;
        private Atom _cachedForAtom;
        private JSONStorableStringChooser _femaleChooser;
        private float _autoSelectTimer;
        private float _cacheValidationTimer;

        // --- Cached Female Components ---
        private Rigidbody _cachedLabiaTrigger;
        private Rigidbody _cachedVaginaTrigger;
        private Transform _cachedPelvis;

        // --- Clitoral Trigger (dynamic Unity GameObject) ---
        private GameObject          _clitoralTriggerGO;
        private Rigidbody           _clitoralTriggerRb;
        private ClitoralTriggerZone _clitoralTriggerZone;
        private GameObject          _clitoralIndicatorGO;      // Small center-dot sphere for positioning
        private GameObject          _clitoralOuterIndicatorGO; // Radius-boundary sphere (scales with slider)

        // --- Cached Finger Transforms (for penetration detection) ---
        private readonly List<Transform> _fingerTips = new List<Transform>();
        private float _fingerCacheTimer;

        // --- Penetration State ---
        private bool  _isFingering;
        private float _prevDepth;
        private float _prevDepthTime;
        private bool  _wasFingeringLastFrame;

        // --- Clitoral State ---
        private Vector3 _prevClitoralFingerPos;
        private float   _prevClitoralFingerPosTime;
        private bool    _clitoralWasActive;  // Rising/falling edge logging
        private float   _clitoralLogTimer;   // Throttle per-second intensity log

        /// <summary>
        /// Non-null when a finger is inside the clitoral trigger zone and not penetrating.
        /// Intensity 0–1 based on finger movement speed × sensitivity.
        /// StrokerSync reads this to drive vibrators independently of Handy position.
        /// </summary>
        public float? ClitoralIntensity { get; private set; }

        // --- Settings ---
        private JSONStorableFloat _maxFingerDepth;         // Insertable finger depth (m)
        private JSONStorableFloat _clitoralSensitivity;  // Speed → intensity gain
        private JSONStorableFloat _clitoralBaseIntensity; // Minimum intensity when finger is in zone
        private JSONStorableFloat _clitoralOffsetFwd;    // Trigger offset along pelvis.forward (m)
        private JSONStorableFloat _clitoralOffsetUp;     // Trigger offset along pelvis.up (m)
        private JSONStorableFloat _clitoralRadius;       // Sphere trigger radius (m)
        private JSONStorableBool  _showIndicator;        // Show visible sphere for positioning

        // --- UI Cleanup (split by tab so each tab rebuilds only its section) ---
        private readonly List<Action> _penetrationUICleanup = new List<Action>();
        private readonly List<Action> _clitoralUICleanup    = new List<Action>();

        // =====================================================================
        // IMOTIONSOURCE — INIT
        // =====================================================================

        public void OnInitStorables(StrokerSync plugin)
        {
            _plugin = plugin;

            _femaleChooser = new JSONStorableStringChooser(
                "finger_Female",
                new List<string> { FEMALE_AUTO },
                FEMALE_AUTO,
                "Select Female",
                FemaleChooserCallback);
            plugin.RegisterStringChooser(_femaleChooser);

            // 0.08 m = roughly how far an index finger can be inserted distal+mid phalanx.
            _maxFingerDepth = new JSONStorableFloat(
                "finger_MaxDepth", 0.08f, 0.02f, 0.20f, false);
            plugin.RegisterFloat(_maxFingerDepth);

            _clitoralSensitivity = new JSONStorableFloat(
                "finger_ClitoralSensitivity", 3.0f, 0.5f, 15.0f, false);
            plugin.RegisterFloat(_clitoralSensitivity);

            // Minimum vibration intensity when a finger is inside the clitoral zone,
            // even if the finger is stationary.  Default 0.3 = 30% base + speed bonus.
            _clitoralBaseIntensity = new JSONStorableFloat(
                "finger_ClitoralBaseIntensity", 0.3f, 0.0f, 1.0f, false);
            plugin.RegisterFloat(_clitoralBaseIntensity);

            // Clitoris is typically ~2–3 cm anterior (pelvis.forward) and ~1 cm
            // superior (pelvis.up) from the vaginal opening.  Adjust per character.
            _clitoralOffsetFwd = new JSONStorableFloat(
                "finger_ClitoralOffsetFwd", 0.025f, -0.05f, 0.10f, false);
            plugin.RegisterFloat(_clitoralOffsetFwd);

            _clitoralOffsetUp = new JSONStorableFloat(
                "finger_ClitoralOffsetUp", 0.01f, -0.05f, 0.05f, false);
            plugin.RegisterFloat(_clitoralOffsetUp);

            _clitoralRadius = new JSONStorableFloat(
                "finger_ClitoralRadius", 0.035f, 0.01f, 0.08f, false);
            _clitoralRadius.setCallbackFunction = (float val) =>
            {
                // Keep physics collider in sync with slider
                if (_clitoralTriggerGO != null)
                {
                    var col = _clitoralTriggerGO.GetComponent<SphereCollider>();
                    if (col != null) col.radius = val;
                }
                // Scale outer indicator to show the new boundary
                if (_clitoralOuterIndicatorGO != null)
                    _clitoralOuterIndicatorGO.transform.localScale = Vector3.one * (val * 2f);
            };
            plugin.RegisterFloat(_clitoralRadius);

            _showIndicator = new JSONStorableBool(
                "finger_ShowClitoralIndicator", true,
                (bool show) =>
                {
                    if (_clitoralIndicatorGO != null)
                        _clitoralIndicatorGO.SetActive(show);
                    if (_clitoralOuterIndicatorGO != null)
                        _clitoralOuterIndicatorGO.SetActive(show);
                });
            plugin.RegisterBool(_showIndicator);
        }

        public void OnInit(StrokerSync plugin)
        {
            InitLogic(plugin);
        }

        /// <summary>
        /// Initialise logic (atom caching, finger scanning) without creating UI.
        /// Called by CombinedSource; UI is built by the tab system.
        /// </summary>
        public void InitLogic(StrokerSync plugin)
        {
            _plugin = plugin;
            PopulateFemaleChooser();
            CacheFingerTips();
        }

        // =====================================================================
        // IMOTIONSOURCE — UPDATE
        // =====================================================================

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            ClitoralIntensity = null;

            // Periodic refresh: finger transforms
            _fingerCacheTimer -= Time.deltaTime;
            if (_fingerCacheTimer <= 0f)
            {
                _fingerCacheTimer = FINGER_CACHE_INTERVAL;
                CacheFingerTips();
            }

            // Periodic refresh: validate female component references
            _cacheValidationTimer -= Time.deltaTime;
            if (_cacheValidationTimer <= 0f)
            {
                _cacheValidationTimer = CACHE_VALIDATION_INTERVAL;
                ValidateFemaleCache();
            }

            // Female auto-selection
            if (_femaleChooser != null && _femaleChooser.val == FEMALE_AUTO)
            {
                _autoSelectTimer -= Time.deltaTime;
                if (_autoSelectTimer <= 0f)
                {
                    _autoSelectTimer = AUTO_SELECT_INTERVAL;
                    RunAutoSelect();
                }
            }

            // Ensure female components are cached
            if (_femaleAtom != null && _cachedForAtom != _femaleAtom)
                CacheFemaleComponents(_femaleAtom);

            if (_cachedLabiaTrigger == null)
                return false;

            Vector3 labiaPos   = _cachedLabiaTrigger.transform.position;
            Vector3 vaginalDir = ComputeVaginalDirection(labiaPos);

            // ----------------------------------------------------------------
            // Compute the clitoral zone world position ONCE and reuse it for
            // both MovePosition and the proximity check below.
            // IMPORTANT: MovePosition on a kinematic Rigidbody does NOT update
            // transform.position until the next physics step, so reading
            // _clitoralTriggerGO.transform.position in the same frame would
            // give a stale value.  Using the pre-computed vector avoids that.
            // ----------------------------------------------------------------
            Vector3 clitoralZonePos = (_cachedPelvis != null)
                ? labiaPos
                    + _cachedPelvis.forward * _clitoralOffsetFwd.val
                    + _cachedPelvis.up      * _clitoralOffsetUp.val
                : labiaPos;

            if (_clitoralTriggerRb != null)
                _clitoralTriggerRb.MovePosition(clitoralZonePos);

            // ----------------------------------------------------------------
            // PENETRATION DETECTION
            // Search all cached finger tips within PENETRATION_SEARCH_RADIUS of
            // the labia for one that is on the vaginal axis.
            // ----------------------------------------------------------------
            Transform bestFinger = null;
            float     bestDist   = PENETRATION_SEARCH_RADIUS;

            foreach (var tip in _fingerTips)
            {
                if (tip == null || tip.gameObject == null) continue;
                float d = Vector3.Distance(tip.position, labiaPos);
                if (d < bestDist)
                {
                    bestDist   = d;
                    bestFinger = tip;
                }
            }

            float rawDepth = 0f;
            if (bestFinger != null)
            {
                Vector3 fingerOffset   = bestFinger.position - labiaPos;
                float   depthAlongAxis = Vector3.Dot(fingerOffset, vaginalDir);
                Vector3 axisPoint      = labiaPos + vaginalDir * Mathf.Max(0f, depthAlongAxis);
                float   lateralDist    = Vector3.Distance(bestFinger.position, axisPoint);
                bool    onAxis         = lateralDist < LATERAL_MAX_DIST && depthAlongAxis > 0f;

                float effectiveDepth = Mathf.Max(_maxFingerDepth.val, 0.02f);
                if (onAxis)
                    rawDepth = Mathf.Clamp01(depthAlongAxis / effectiveDepth);
            }

            // Hysteresis
            bool nowPenetrating;
            if (_isFingering)
                nowPenetrating = rawDepth > PENETRATION_OFF_TH;
            else
                nowPenetrating = rawDepth > PENETRATION_ON_TH;

            // ----------------------------------------------------------------
            // CASE: Finger penetrating
            // ----------------------------------------------------------------
            if (nowPenetrating)
            {
                _isFingering           = true;
                _wasFingeringLastFrame = true;
                ClitoralIntensity      = null;

                float now = Time.time;
                float dt  = now - _prevDepthTime;
                float vel = 0f;
                if (dt > 0.001f)
                    vel = Mathf.Clamp01(Mathf.Abs(rawDepth - _prevDepth) / dt / 2.0f);
                _prevDepth     = rawDepth;
                _prevDepthTime = now;

                outPos      = 1f - rawDepth; // Invert: 0=fully in, 1=fully out
                outVelocity = vel;
                return true;
            }

            // ----------------------------------------------------------------
            // Transition: was penetrating, just crossed below the off-threshold.
            // Emit one terminal "device to top" command then fall through.
            // ----------------------------------------------------------------
            if (_isFingering)
            {
                _isFingering           = false;
                _wasFingeringLastFrame = false;
                outPos      = 1.0f;
                outVelocity = 0f;
                return true;
            }

            // ----------------------------------------------------------------
            // CASE: Clitoral zone — proximity check against the trigger GO's
            // world position + radius.  We use direct distance math rather than
            // Unity physics triggers because the finger BONE rigidbodies
            // (lIndex3 etc.) are IK control bodies with no collider geometry,
            // so OnTriggerEnter never fires for them.  The trigger GO still
            // exists for its visual indicator sphere; the physics component is
            // unused and harmless.
            // ----------------------------------------------------------------
            bool      clitoralActive = false;
            Transform clitoralFinger = null;

            {
                float radius      = _clitoralRadius != null ? _clitoralRadius.val : 0.035f;
                float closestDist = radius;

                foreach (var tip in _fingerTips)
                {
                    if (tip == null || tip.gameObject == null) continue;
                    float d = Vector3.Distance(tip.position, clitoralZonePos);
                    if (d < closestDist)
                    {
                        closestDist    = d;
                        clitoralFinger = tip;
                        clitoralActive = true;
                    }
                }
            }

            if (!clitoralActive)
            {
                if (_clitoralWasActive)
                {
                    _clitoralWasActive = false;
                    SuperController.LogMessage("StrokerSync [Finger]: Clitoral contact ended.");
                }
                if (_wasFingeringLastFrame)
                {
                    _wasFingeringLastFrame = false;
                    outPos      = 1.0f;
                    outVelocity = 0f;
                    return true;
                }
                return false;
            }

            if (!_clitoralWasActive)
            {
                _clitoralWasActive = true;
                SuperController.LogMessage("StrokerSync [Finger]: Clitoral contact started.");
            }

            // ----------------------------------------------------------------
            // CASE: Finger inside the clitoral zone (not penetrating)
            // ----------------------------------------------------------------
            _wasFingeringLastFrame = false;
            if (clitoralFinger != null && clitoralFinger.gameObject != null)
            {
                float nowTime  = Time.time;
                float deltaT   = nowTime - _prevClitoralFingerPosTime;
                float moveSpeed = 0f;
                if (deltaT > 0.001f && _prevClitoralFingerPosTime > 0f)
                    moveSpeed = Vector3.Distance(clitoralFinger.position, _prevClitoralFingerPos) / deltaT;
                _prevClitoralFingerPos    = clitoralFinger.position;
                _prevClitoralFingerPosTime = nowTime;

                float baseIntensity = _clitoralBaseIntensity != null ? _clitoralBaseIntensity.val : 0.3f;
                float speedBonus    = Mathf.Clamp01(moveSpeed * _clitoralSensitivity.val);
                // Base ensures a stationary finger still vibrates; speed scales up to 1.0.
                ClitoralIntensity = Mathf.Clamp01(baseIntensity + (1f - baseIntensity) * speedBonus);

                _clitoralLogTimer -= Time.deltaTime;
                if (_clitoralLogTimer <= 0f)
                {
                    _clitoralLogTimer = 1f;
                    SuperController.LogMessage(
                        $"StrokerSync [Finger]: Clitoral intensity={ClitoralIntensity:F2} " +
                        $"(base={baseIntensity:F2} speed={moveSpeed:F3} bonus={speedBonus:F2})");
                }
            }
            else
            {
                ClitoralIntensity = 0f;
            }

            // Don't move the Handy. StrokerSync reads ClitoralIntensity and
            // drives vibrators independently (see UpdateMotionSource in StrokerSync.cs).
            return false;
        }

        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime) { }

        public void OnDestroy(StrokerSync plugin)
        {
            DestroyLogic(plugin);
            DestroyPenetrationUI();
            DestroyClitoralUI();
        }

        /// <summary>Non-UI teardown: destroy the clitoral trigger GameObject.</summary>
        public void DestroyLogic(StrokerSync plugin)
        {
            DestroyClitoralTrigger();
        }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            DestroyClitoralTrigger();

            _femaleAtom          = null;
            _cachedForAtom       = null;
            _cachedLabiaTrigger  = null;
            _cachedVaginaTrigger = null;
            _cachedPelvis        = null;
            _fingerTips.Clear();

            // Reset chooser to Auto and schedule a list refresh so new scene atoms appear.
            if (_femaleChooser != null)
            {
                _femaleChooser.valNoCallback = FEMALE_AUTO;
                _femaleChooser.choices = new System.Collections.Generic.List<string> { FEMALE_AUTO };
            }
            plugin.StartCoroutine(DelayedRepopulate());

            _isFingering               = false;
            _wasFingeringLastFrame     = false;
            _clitoralWasActive         = false;
            _clitoralLogTimer          = 0f;
            ClitoralIntensity          = null;
            _prevDepth                 = 0f;
            _prevDepthTime             = 0f;
            _prevClitoralFingerPos     = Vector3.zero;
            _prevClitoralFingerPosTime = 0f;
        }

        // =====================================================================
        // CLITORAL TRIGGER MANAGEMENT
        // =====================================================================

        /// <summary>
        /// Creates a kinematic sphere trigger at the default pelvis-relative position.
        /// Called after CacheFemaleComponents succeeds so we know the physics layer.
        /// The trigger is NOT parented to the character — instead it is repositioned
        /// each frame via Rigidbody.MovePosition so physics stays in sync.
        /// </summary>
        private void CreateClitoralTrigger()
        {
            DestroyClitoralTrigger();

            if (_cachedLabiaTrigger == null) return;

            _clitoralTriggerGO = new GameObject("StrokerSync_ClitoralTrigger");

            // Use the same physics layer as LabiaTrigger so finger colliders interact.
            _clitoralTriggerGO.layer = _cachedLabiaTrigger.gameObject.layer;

            // Kinematic Rigidbody: required for OnTriggerEnter/Exit to fire reliably.
            _clitoralTriggerRb             = _clitoralTriggerGO.AddComponent<Rigidbody>();
            _clitoralTriggerRb.isKinematic = true;

            // Sphere trigger collider.
            var col       = _clitoralTriggerGO.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius    = _clitoralRadius != null ? _clitoralRadius.val : 0.035f;

            // Trigger behaviour.
            _clitoralTriggerZone = _clitoralTriggerGO.AddComponent<ClitoralTriggerZone>();
            _clitoralTriggerZone.Initialize(FINGER_RB_NAMES);

            // Visual indicators: two spheres parented to the trigger so they move with it.
            // Inner: small opaque center dot — shows where the zone is anchored.
            // Outer: full-radius semi-transparent shell — shows the detection boundary.
            // CreatePrimitive auto-adds a MeshCollider; destroy it immediately so it
            // doesn't interfere with physics. Only the mesh renderer is needed.
            bool showNow = _showIndicator != null && _showIndicator.val;
            float diameter = col.radius * 2f;

            // ── Inner sphere (center dot, fixed ~1 cm diameter) ────────────────
            _clitoralIndicatorGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _clitoralIndicatorGO.name = "StrokerSync_ClitoralIndicator";
            _clitoralIndicatorGO.transform.SetParent(_clitoralTriggerGO.transform, false);
            _clitoralIndicatorGO.transform.localScale = Vector3.one * 0.012f;
            var meshColInner = _clitoralIndicatorGO.GetComponent<Collider>();
            if (meshColInner != null) UnityEngine.Object.Destroy(meshColInner);
            var rendInner = _clitoralIndicatorGO.GetComponent<Renderer>();
            if (rendInner != null)
            {
                var mat = rendInner.material;
                mat.color = new Color(1f, 0.3f, 0.7f, 0.85f); // bright pink, mostly opaque
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3000;
            }
            _clitoralIndicatorGO.SetActive(showNow);

            // ── Outer sphere (radius boundary, scales with slider) ─────────────
            _clitoralOuterIndicatorGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _clitoralOuterIndicatorGO.name = "StrokerSync_ClitoralOuterIndicator";
            _clitoralOuterIndicatorGO.transform.SetParent(_clitoralTriggerGO.transform, false);
            _clitoralOuterIndicatorGO.transform.localScale = Vector3.one * diameter;
            var meshColOuter = _clitoralOuterIndicatorGO.GetComponent<Collider>();
            if (meshColOuter != null) UnityEngine.Object.Destroy(meshColOuter);
            var rendOuter = _clitoralOuterIndicatorGO.GetComponent<Renderer>();
            if (rendOuter != null)
            {
                var mat = rendOuter.material;
                mat.color = new Color(1f, 0.65f, 0.85f, 0.12f); // light pink, very transparent
                mat.SetFloat("_Mode", 3f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                mat.renderQueue = 3001; // render on top of inner sphere
            }
            _clitoralOuterIndicatorGO.SetActive(showNow);

            // Set initial world position
            if (_cachedPelvis != null)
            {
                Vector3 initPos = _cachedLabiaTrigger.transform.position
                    + _cachedPelvis.forward * (_clitoralOffsetFwd != null ? _clitoralOffsetFwd.val : 0.025f)
                    + _cachedPelvis.up      * (_clitoralOffsetUp  != null ? _clitoralOffsetUp.val  : 0.01f);
                _clitoralTriggerGO.transform.position = initPos;
            }
            else
            {
                _clitoralTriggerGO.transform.position = _cachedLabiaTrigger.transform.position;
            }
        }

        private void DestroyClitoralTrigger()
        {
            if (_clitoralTriggerZone != null)
            {
                _clitoralTriggerZone.Reset();
                _clitoralTriggerZone = null;
            }
            if (_clitoralTriggerGO != null)
            {
                UnityEngine.Object.Destroy(_clitoralTriggerGO);
                _clitoralTriggerGO       = null;
                _clitoralTriggerRb       = null;
                _clitoralIndicatorGO     = null;
                _clitoralOuterIndicatorGO = null;
            }
        }

        // =====================================================================
        // VAGINAL DIRECTION
        // =====================================================================

        private Vector3 ComputeVaginalDirection(Vector3 labiaPos)
        {
            if (_cachedVaginaTrigger == null)
                return Vector3.forward;

            Vector3 raw = (_cachedVaginaTrigger.transform.position - labiaPos).normalized;

            if (_cachedPelvis == null)
                return raw;

            float   dist  = Vector3.Distance(labiaPos, _cachedVaginaTrigger.transform.position);
            float   blend = Mathf.Clamp01((dist - 0.005f) / 0.02f);

            Vector3 pelvisUp    = _cachedPelvis.up;
            Vector3 pelvisRight = _cachedPelvis.right;
            float   dotUp       = Mathf.Abs(Vector3.Dot(raw, pelvisUp));
            float   dotRight    = Mathf.Abs(Vector3.Dot(raw, pelvisRight));
            Vector3 pelvisRef   = (dotUp > dotRight) ? pelvisUp : pelvisRight;

            return Vector3.Slerp(pelvisRef, raw, blend).normalized;
        }

        // =====================================================================
        // FINGER TIP CACHING
        // =====================================================================

        private void CacheFingerTips()
        {
            _fingerTips.Clear();
            if (Controller == null) return;

            var nameSet = new HashSet<string>(FINGER_RB_NAMES);

            foreach (var atom in Controller.GetAtoms())
            {
                if (!atom.on) continue;
                foreach (var rb in atom.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (nameSet.Contains(rb.name))
                        _fingerTips.Add(rb.transform);
                }
            }

            if (_fingerTips.Count == 0)
                SuperController.LogMessage(
                    "StrokerSync [Finger]: No fingertip rigidbodies found. " +
                    "Expected: " + string.Join(", ", FINGER_RB_NAMES));
        }

        // =====================================================================
        // FEMALE ATOM CACHING
        // =====================================================================

        private void CacheFemaleComponents(Atom atom)
        {
            _cachedForAtom       = atom;
            _cachedLabiaTrigger  = null;
            _cachedVaginaTrigger = null;
            _cachedPelvis        = null;

            foreach (var rb in atom.GetComponentsInChildren<Rigidbody>(true))
            {
                string n = rb.name;
                if      (n == "LabiaTrigger")          _cachedLabiaTrigger  = rb;
                else if (n == "VaginaTrigger")         _cachedVaginaTrigger = rb;
                else if (n == "pelvis" || n == "hip")  _cachedPelvis        = rb.transform;
            }

            SuperController.LogMessage(
                $"StrokerSync [Finger]: Cached '{atom.uid}': " +
                $"labia={(_cachedLabiaTrigger != null)}, " +
                $"vagina={(_cachedVaginaTrigger != null)}, " +
                $"pelvis={(_cachedPelvis != null)}");

            // Recreate the clitoral trigger on the new atom's physics layer.
            CreateClitoralTrigger();
        }

        private void ValidateFemaleCache()
        {
            if (_femaleAtom == null) return;

            bool invalid =
                (_cachedLabiaTrigger  != null && _cachedLabiaTrigger.gameObject  == null) ||
                (_cachedVaginaTrigger != null && _cachedVaginaTrigger.gameObject == null);

            if (invalid)
            {
                _cachedForAtom = null;
                SuperController.LogMessage("StrokerSync [Finger]: Female cache stale — re-caching.");
            }
        }

        private void PopulateFemaleChooser()
        {
            if (_femaleChooser == null || Controller == null) return;
            var choices = new List<string> { FEMALE_AUTO };
            foreach (var a in Controller.GetAtoms())
            {
                if (!a.on || a.type != "Person") continue;
                if (HasLabiaTrigger(a)) choices.Add(a.uid);
            }
            _femaleChooser.choices = choices;
        }

        private System.Collections.IEnumerator DelayedRepopulate()
        {
            yield return new UnityEngine.WaitForSeconds(1.5f);
            PopulateFemaleChooser();
            CacheFingerTips();
        }

        // =====================================================================
        // AUTO-SELECT
        // =====================================================================

        private void RunAutoSelect()
        {
            if (Controller == null) return;

            Vector3 refPos = Vector3.zero;
            bool    hasRef = false;
            foreach (var tip in _fingerTips)
            {
                if (tip != null && tip.gameObject != null) { refPos = tip.position; hasRef = true; break; }
            }

            Atom  best        = null;
            float bestDistSqr = float.MaxValue;

            foreach (var a in Controller.GetAtoms())
            {
                if (!a.on || a.type != "Person") continue;
                if (!HasLabiaTrigger(a)) continue;

                Vector3 atomPos  = a.mainController != null ? a.mainController.transform.position : Vector3.zero;
                float   d        = hasRef ? (atomPos - refPos).sqrMagnitude : 0f;

                if (d < bestDistSqr) { bestDistSqr = d; best = a; }
            }

            if (best != null && best != _femaleAtom)
            {
                _femaleAtom = best;
                CacheFemaleComponents(best);
                SuperController.LogMessage($"StrokerSync [Finger]: Auto-selected '{best.uid}'.");
            }
        }

        private bool HasLabiaTrigger(Atom a)
        {
            foreach (var rb in a.GetComponentsInChildren<Rigidbody>(true))
                if (rb.name == "LabiaTrigger") return true;
            return false;
        }

        private void FemaleChooserCallback(string s)
        {
            DestroyClitoralTrigger();
            _cachedForAtom       = null;
            _cachedLabiaTrigger  = null;
            _cachedVaginaTrigger = null;
            _cachedPelvis        = null;

            if (string.IsNullOrEmpty(s) || s == FEMALE_AUTO)
                _femaleAtom = null;
            else
            {
                _femaleAtom = Controller != null ? Controller.GetAtomByUid(s) : null;
                if (_femaleAtom != null) CacheFemaleComponents(_femaleAtom);
            }
        }

        // =====================================================================
        // UI
        // =====================================================================

        // =====================================================================
        // PUBLIC UI BUILDERS — called by CombinedSource for the tab system
        // =====================================================================

        /// <summary>Creates all finger UI (penetration + clitoral). For standalone use.</summary>
        public void CreateUI(StrokerSync plugin)
        {
            CreatePenetrationUI(plugin);
            CreateClitoralUI(plugin);
        }

        /// <summary>Creates the Finger Penetration section (goes in Stroker tab).</summary>
        public void CreatePenetrationUI(StrokerSync plugin)
        {
            DestroyPenetrationUI();

            var spacer = plugin.CreateSpacer(true);
            spacer.height = 40f;
                _penetrationUICleanup.Add(() => plugin.RemoveSpacer(spacer));


            var header = plugin.CreateTextField(
                new JSONStorableString("fingerPenInfo",
                    "Finger Penetration:\n" +
                    "Tracks fingertip rigidbodies inside the vaginal\n" +
                    "opening and drives the Handy stroke depth.\n" +
                    "Supports fingers, dildos, and VR controller hands."), true);
            header.height = 240f;

            _penetrationUICleanup.Add(() => plugin.RemoveTextField(header));

            var femalePopup = plugin.CreateScrollablePopup(_femaleChooser);
            femalePopup.label = "Clit Stim Target";
            femalePopup.popup.onOpenPopupHandlers += () => PopulateFemaleChooser();
            _penetrationUICleanup.Add(() => plugin.RemovePopup(femalePopup));

            var depthSlider = plugin.CreateSlider(_maxFingerDepth);
            depthSlider.label = "Finger Depth (m)";
            _penetrationUICleanup.Add(() => plugin.RemoveSlider(depthSlider));

            var dumpBtn = plugin.CreateButton("Dump Person RB Names → VaM Log");
            dumpBtn.button.onClick.AddListener(DumpAllRigidbodyNames);
            _penetrationUICleanup.Add(() => plugin.RemoveButton(dumpBtn));
        }

        public void DestroyPenetrationUI()
        {
            foreach (var a in _penetrationUICleanup) try { a(); } catch { }
            _penetrationUICleanup.Clear();
        }

        /// <summary>Creates the Clitoral Zone section (goes in Vibration tab).</summary>
        public void CreateClitoralUI(StrokerSync plugin)
        {
            DestroyClitoralUI();

            // Target chooser — placed first so it's obvious who the zone tracks.
            var targetPopup = plugin.CreateScrollablePopup(_femaleChooser, true);
            targetPopup.label = "Clit Stim Target";
            targetPopup.popup.onOpenPopupHandlers += () => PopulateFemaleChooser();
            _clitoralUICleanup.Add(() => plugin.RemovePopup(targetPopup));

            var header = plugin.CreateTextField(
                new JSONStorableString("fingerClitInfo",
                    "Clitoral Zone:\n" +
                    "A sphere (pink indicator) placed near the vaginal\n" +
                    "opening. Fingers inside it drive vibrators.\n" +
                    "Adjust offset/radius to align with the character."), true);
            header.height = 120f;
            _clitoralUICleanup.Add(() => plugin.RemoveTextField(header));

            var sensitivitySlider = plugin.CreateSlider(_clitoralSensitivity, true);
            sensitivitySlider.label = "Clitoral Sensitivity (speed bonus)";
            _clitoralUICleanup.Add(() => plugin.RemoveSlider(sensitivitySlider));

            var baseIntensitySlider = plugin.CreateSlider(_clitoralBaseIntensity, true);
            baseIntensitySlider.label = "Clitoral Base Intensity (at rest)";
            _clitoralUICleanup.Add(() => plugin.RemoveSlider(baseIntensitySlider));

            var fwdSlider = plugin.CreateSlider(_clitoralOffsetFwd, true);
            fwdSlider.label = "Clitoral Offset Forward (m)";
            _clitoralUICleanup.Add(() => plugin.RemoveSlider(fwdSlider));

            var upSlider = plugin.CreateSlider(_clitoralOffsetUp, true);
            upSlider.label = "Clitoral Offset Up (m)";
            _clitoralUICleanup.Add(() => plugin.RemoveSlider(upSlider));

            var radiusSlider = plugin.CreateSlider(_clitoralRadius, true);
            radiusSlider.label = "Clitoral Trigger Radius (m)";
            _clitoralUICleanup.Add(() => plugin.RemoveSlider(radiusSlider));

            var indicatorToggle = plugin.CreateToggle(_showIndicator, true);
            indicatorToggle.label = "Show Clitoral Trigger Sphere";
            _clitoralUICleanup.Add(() => plugin.RemoveToggle(indicatorToggle));
        }

        public void DestroyClitoralUI()
        {
            foreach (var a in _clitoralUICleanup) try { a(); } catch { }
            _clitoralUICleanup.Clear();
        }

        /// <summary>
        /// Logs all rigidbody names from Person-type atoms only.
        /// Use to identify correct finger bone names if defaults don't match.
        /// </summary>
        private void DumpAllRigidbodyNames()
        {
            if (Controller == null) return;

            SuperController.LogMessage("=== StrokerSync [Finger]: RB NAME DUMP START (Person atoms only) ===");

            foreach (var atom in Controller.GetAtoms())
            {
                if (!atom.on || atom.type != "Person") continue;

                var rbs = atom.GetComponentsInChildren<Rigidbody>(true);
                if (rbs == null || rbs.Length == 0) continue;

                SuperController.LogMessage(
                    $"--- Atom: '{atom.uid}' ({rbs.Length} rigidbodies) ---");

                var sb    = new System.Text.StringBuilder();
                int count = 0;
                foreach (var rb in rbs)
                {
                    if (count > 0) sb.Append(", ");
                    sb.Append(rb.name);
                    count++;
                    if (count % 20 == 0)
                    {
                        SuperController.LogMessage(sb.ToString());
                        sb.Length = 0;
                        count     = 0;
                    }
                }
                if (sb.Length > 0)
                    SuperController.LogMessage(sb.ToString());
            }

            SuperController.LogMessage("=== StrokerSync [Finger]: RB NAME DUMP END ===");
        }
    }
}
