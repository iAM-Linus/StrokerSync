using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Detects cunnilingus (mouth-to-vulva contact) and produces a vibration
    /// intensity signal — no stroker movement is involved.
    ///
    /// DETECTION:
    ///   Each frame the giver's lowerJaw (or head as fallback) is checked for
    ///   proximity to the SHARED clitoral zone centre (supplied by FingerSource).
    ///   This is the same position the clitoral indicator sphere sits at, so the
    ///   user positions one sphere and it governs both finger and oral detection.
    ///   When the giver's mouth is within _oralRadius, OralIntensity is non-null.
    ///
    /// INTENSITY SOURCE (user-selectable):
    ///   "Giver Head"    — movement speed of the giver's jaw/head.
    ///                     Tongue/head bobbing → intensity.
    ///   "Receiver Hips" — movement speed of the receiver's pelvis.
    ///                     Hip grinding/thrusting → intensity.
    ///   "Blend"         — average of both (default).
    ///
    /// A configurable base intensity ensures the vibrator is never completely
    /// silent when contact is active but both parties are stationary.
    ///
    /// ATOM SELECTION:
    ///   Both giver and receiver can be set to *Auto*.
    ///   Auto-receiver: first Person atom with a LabiaTrigger rigidbody.
    ///   Auto-giver:    first Person atom that is NOT the receiver.
    /// </summary>
    public class OralSource
    {
        // =====================================================================
        // CONSTANTS
        // =====================================================================

        private const string GIVER_AUTO    = "*Auto*";
        private const string RECEIVER_AUTO = "*Auto*";

        private const string MODE_HEAD  = "Giver Head";
        private const string MODE_HIPS  = "Receiver Hips";
        private const string MODE_BLEND = "Blend";

        private const float AUTO_SELECT_INTERVAL      = 0.5f;
        private const float CACHE_VALIDATION_INTERVAL = 2.0f;

        // =====================================================================
        // FIELDS
        // =====================================================================

        private StrokerSync _plugin;
        private SuperController Controller => SuperController.singleton;

        // Atom choosers
        private JSONStorableStringChooser _giverChooser;
        private JSONStorableStringChooser _receiverChooser;

        // Resolved atoms
        private Atom _giverAtom;
        private Atom _receiverAtom;
        private Atom _cachedGiverForAtom;
        private Atom _cachedReceiverForAtom;

        // Cached transforms
        private Transform _giverMouth;       // lowerJaw or head rigidbody on giver
        private Transform _receiverLabia;    // LabiaTrigger on receiver — zone centre
        private Transform _receiverPelvis;   // pelvis/hip — zone offset + hip speed

        // Timers
        private float _autoSelectTimer;
        private float _cacheValidationTimer;

        // Movement tracking
        private Vector3 _prevGiverMouthPos;
        private float   _prevGiverMouthTime;
        private Vector3 _prevReceiverPelvisPos;
        private float   _prevReceiverPelvisTime;

        // Edge-detection for log messages
        private bool _wasActive;

        // Settings storables
        private JSONStorableFloat          _oralRadius;
        private JSONStorableFloat          _sensitivity;
        private JSONStorableFloat          _baseIntensity;
        private JSONStorableStringChooser  _intensityMode;

        // UI cleanup
        private readonly List<Action> _uiCleanup = new List<Action>();

        // =====================================================================
        // PUBLIC API
        // =====================================================================

        /// <summary>
        /// Non-null when the giver's mouth is inside the contact zone.
        /// Intensity 0–1, combining base level and movement speed.
        /// </summary>
        public float? OralIntensity { get; private set; }

        // =====================================================================
        // INIT
        // =====================================================================

        public void OnInitStorables(StrokerSync plugin)
        {
            _plugin = plugin;

            _giverChooser = new JSONStorableStringChooser(
                "oral_Giver",
                new List<string> { GIVER_AUTO },
                GIVER_AUTO,
                "Giver (licker)",
                (string _) => InvalidateCache());
            plugin.RegisterStringChooser(_giverChooser);

            _receiverChooser = new JSONStorableStringChooser(
                "oral_Receiver",
                new List<string> { RECEIVER_AUTO },
                RECEIVER_AUTO,
                "Receiver",
                (string _) => InvalidateCache());
            plugin.RegisterStringChooser(_receiverChooser);

            // Contact sphere radius in metres. A head is larger than a fingertip
            // so the default is wider than the clitoral finger zone radius.
            // The zone CENTRE is shared with the clitoral indicator sphere —
            // position that sphere to tune oral detection as well.
            _oralRadius = new JSONStorableFloat(
                "oral_Radius", 0.09f, 0.03f, 0.25f, false);
            plugin.RegisterFloat(_oralRadius);

            // Speed-to-intensity gain.
            _sensitivity = new JSONStorableFloat(
                "oral_Sensitivity", 3.0f, 0.5f, 15.0f, false);
            plugin.RegisterFloat(_sensitivity);

            // Minimum intensity even when both parties are completely still.
            _baseIntensity = new JSONStorableFloat(
                "oral_BaseIntensity", 0.3f, 0.0f, 1.0f, false);
            plugin.RegisterFloat(_baseIntensity);

            _intensityMode = new JSONStorableStringChooser(
                "oral_IntensityMode",
                new List<string> { MODE_HEAD, MODE_HIPS, MODE_BLEND },
                MODE_BLEND,
                "Intensity Source");
            plugin.RegisterStringChooser(_intensityMode);
        }

        // =====================================================================
        // UPDATE (call every frame)
        // =====================================================================

        /// <param name="sharedZonePos">
        /// World-space zone centre from FingerSource.ClitoralZonePosition —
        /// the same position the clitoral indicator sphere sits at.
        /// Pass null when the receiver atom is not yet resolved.
        /// </param>
        public void Update(Vector3? sharedZonePos)
        {
            // Auto-select atoms periodically
            _autoSelectTimer -= Time.deltaTime;
            if (_autoSelectTimer <= 0f)
            {
                _autoSelectTimer = AUTO_SELECT_INTERVAL;
                AutoSelectAtoms();
            }

            // Validate caches periodically
            _cacheValidationTimer -= Time.deltaTime;
            if (_cacheValidationTimer <= 0f)
            {
                _cacheValidationTimer = CACHE_VALIDATION_INTERVAL;
                ValidateCache();
            }

            // Zone centre comes from FingerSource — if it's null the receiver
            // atom hasn't been resolved yet; nothing to detect against.
            if (!sharedZonePos.HasValue)
            {
                OralIntensity = null;
                return;
            }

            if (_giverAtom == null)
            {
                OralIntensity = null;
                return;
            }

            // Build giver cache on first use or after atom change.
            // Receiver cache is only needed for hip-speed tracking; zone
            // position is now supplied externally.
            if (!EnsureGiverCache())
            {
                OralIntensity = null;
                return;
            }
            EnsureReceiverCache(); // optional — used for hip movement only

            // Proximity check against the shared clitoral zone centre
            Vector3 zonePos = sharedZonePos.Value;
            float dist   = Vector3.Distance(_giverMouth.position, zonePos);
            bool  inZone = dist < _oralRadius.val;

            if (!inZone)
            {
                if (_wasActive)
                {
                    _wasActive = false;
                    // DEBUG: SuperController.LogMessage("StrokerSync [Oral]: Contact ended.");
                }
                OralIntensity = null;
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                // DEBUG: SuperController.LogMessage("StrokerSync [Oral]: Contact started.");
            }

            // ── Movement speeds ───────────────────────────────────────────────

            float now = Time.time;
            float dt;

            float headSpeed = 0f;
            dt = now - _prevGiverMouthTime;
            if (dt > 0.001f && _prevGiverMouthTime > 0f)
                headSpeed = Vector3.Distance(_giverMouth.position, _prevGiverMouthPos) / dt;
            _prevGiverMouthPos  = _giverMouth.position;
            _prevGiverMouthTime = now;

            float hipSpeed = 0f;
            if (_receiverPelvis != null)
            {
                dt = now - _prevReceiverPelvisTime;
                if (dt > 0.001f && _prevReceiverPelvisTime > 0f)
                    hipSpeed = Vector3.Distance(_receiverPelvis.position, _prevReceiverPelvisPos) / dt;
                _prevReceiverPelvisPos  = _receiverPelvis.position;
                _prevReceiverPelvisTime = now;
            }

            // ── Blend based on mode ───────────────────────────────────────────

            float moveSpeed;
            string mode = _intensityMode.val;
            if      (mode == MODE_HEAD) moveSpeed = headSpeed;
            else if (mode == MODE_HIPS) moveSpeed = hipSpeed;
            else                        moveSpeed = (headSpeed + hipSpeed) * 0.5f;

            float baseI      = _baseIntensity.val;
            float speedBonus = Mathf.Clamp01(moveSpeed * _sensitivity.val);
            OralIntensity = Mathf.Clamp01(baseI + (1f - baseI) * speedBonus);
        }

        // =====================================================================
        // SCENE LOAD
        // =====================================================================

        public void OnSceneLoaded()
        {
            InvalidateCache();
        }

        // =====================================================================
        // UI
        // =====================================================================

        public Action CreateUI(StrokerSync plugin)
        {
            var giverPopup = plugin.CreateScrollablePopup(_giverChooser, true);
            giverPopup.popup.onOpenPopupHandlers += PopulateGiverChooser;
            _uiCleanup.Add(() => plugin.RemovePopup(giverPopup));

            var receiverPopup = plugin.CreateScrollablePopup(_receiverChooser, true);
            receiverPopup.popup.onOpenPopupHandlers += PopulateReceiverChooser;
            _uiCleanup.Add(() => plugin.RemovePopup(receiverPopup));

            var modePopup = plugin.CreateScrollablePopup(_intensityMode, true);
            modePopup.label = "Intensity Source";
            _uiCleanup.Add(() => plugin.RemovePopup(modePopup));

            var baseSlider = plugin.CreateSlider(_baseIntensity, true);
            baseSlider.label = "Base Intensity";
            _uiCleanup.Add(() => plugin.RemoveSlider(baseSlider));

            var sensSlider = plugin.CreateSlider(_sensitivity, true);
            sensSlider.label = "Sensitivity";
            _uiCleanup.Add(() => plugin.RemoveSlider(sensSlider));

            var radiusSlider = plugin.CreateSlider(_oralRadius, true);
            radiusSlider.label = "Contact Radius (m)";
            _uiCleanup.Add(() => plugin.RemoveSlider(radiusSlider));

            return DestroyUI;
        }

        private void DestroyUI()
        {
            foreach (var a in _uiCleanup) try { a(); } catch { }
            _uiCleanup.Clear();
        }

        // =====================================================================
        // ATOM AUTO-SELECT
        // =====================================================================

        private void AutoSelectAtoms()
        {
            if (Controller == null) return;

            var allPersonUids    = new List<string> { GIVER_AUTO };
            var femalePersonUids = new List<string> { RECEIVER_AUTO };

            Atom autoReceiver = null;
            Atom autoGiver    = null;

            foreach (var atom in Controller.GetAtoms())
            {
                if (atom == null || !atom.on || atom.type != "Person") continue;
                allPersonUids.Add(atom.uid);

                if (HasLabiaTrigger(atom))
                {
                    femalePersonUids.Add(atom.uid);
                    if (autoReceiver == null) autoReceiver = atom;
                }
            }

            // Giver: first Person that is not the receiver
            foreach (var atom in Controller.GetAtoms())
            {
                if (atom == null || !atom.on || atom.type != "Person") continue;
                if (atom == autoReceiver) continue;
                autoGiver = atom;
                break;
            }

            _giverChooser.choices    = allPersonUids;
            _receiverChooser.choices = femalePersonUids;

            // Apply auto-selections
            if (_receiverChooser.val == RECEIVER_AUTO)
            {
                if (_receiverAtom != autoReceiver)
                {
                    _receiverAtom = autoReceiver;
                    _cachedReceiverForAtom = null;
                }
            }
            else
            {
                var selected = Controller.GetAtomByUid(_receiverChooser.val);
                if (_receiverAtom != selected)
                {
                    _receiverAtom = selected;
                    _cachedReceiverForAtom = null;
                }
            }

            if (_giverChooser.val == GIVER_AUTO)
            {
                if (_giverAtom != autoGiver)
                {
                    _giverAtom = autoGiver;
                    _cachedGiverForAtom = null;
                }
            }
            else
            {
                var selected = Controller.GetAtomByUid(_giverChooser.val);
                if (_giverAtom != selected)
                {
                    _giverAtom = selected;
                    _cachedGiverForAtom = null;
                }
            }
        }

        private void PopulateGiverChooser()
        {
            if (Controller == null) return;
            var choices = new List<string> { GIVER_AUTO };
            foreach (var a in Controller.GetAtoms())
            {
                if (a == null || !a.on || a.type != "Person") continue;
                choices.Add(a.uid);
            }
            _giverChooser.choices = choices;
        }

        private void PopulateReceiverChooser()
        {
            if (Controller == null) return;
            var choices = new List<string> { RECEIVER_AUTO };
            foreach (var a in Controller.GetAtoms())
            {
                if (a == null || !a.on || a.type != "Person") continue;
                if (HasLabiaTrigger(a)) choices.Add(a.uid);
            }
            _receiverChooser.choices = choices;
        }

        private static bool HasLabiaTrigger(Atom atom)
        {
            foreach (var rb in atom.GetComponentsInChildren<Rigidbody>(true))
                if (rb.name == "LabiaTrigger") return true;
            return false;
        }

        // =====================================================================
        // COMPONENT CACHING
        // =====================================================================

        private bool EnsureGiverCache()
        {
            if (_cachedGiverForAtom == _giverAtom && _giverMouth != null)
                return true;

            _cachedGiverForAtom = _giverAtom;
            _giverMouth = null;

            // Prefer lowerJaw (closest to lips); fall back to head
            foreach (var rb in _giverAtom.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.name == "lowerJaw") { _giverMouth = rb.transform; break; }
                if (rb.name == "head")       _giverMouth = rb.transform; // keep looking for lowerJaw
            }

            if (_giverMouth != null)
                SuperController.LogMessage(
                    $"StrokerSync [Oral]: Giver mouth bone '{_giverMouth.name}' on '{_giverAtom.uid}'.");
            else
                SuperController.LogMessage(
                    $"StrokerSync [Oral]: No mouth bone found on '{_giverAtom.uid}' — oral detection disabled.");

            return _giverMouth != null;
        }

        private bool EnsureReceiverCache()
        {
            if (_cachedReceiverForAtom == _receiverAtom
                && (_receiverLabia != null || _receiverPelvis != null))
                return true;

            _cachedReceiverForAtom = _receiverAtom;
            _receiverLabia  = null;
            _receiverPelvis = null;

            foreach (var rb in _receiverAtom.GetComponentsInChildren<Rigidbody>(true))
            {
                string n = rb.name;
                if (n == "LabiaTrigger")          _receiverLabia  = rb.transform;
                if (n == "pelvis" || n == "hip")  _receiverPelvis = rb.transform;
            }

            SuperController.LogMessage(
                $"StrokerSync [Oral]: Receiver '{_receiverAtom.uid}': " +
                $"labia={(_receiverLabia != null)}, pelvis={(_receiverPelvis != null)}.");

            return _receiverLabia != null || _receiverPelvis != null;
        }

        private void ValidateCache()
        {
            if (_cachedGiverForAtom != null
                && (_giverMouth == null || _giverMouth.gameObject == null))
            {
                _cachedGiverForAtom = null;
                _giverMouth = null;
            }

            if (_cachedReceiverForAtom != null
                && _receiverLabia != null
                && _receiverLabia.gameObject == null)
            {
                _cachedReceiverForAtom = null;
                _receiverLabia  = null;
                _receiverPelvis = null;
            }
        }

        private void InvalidateCache()
        {
            _cachedGiverForAtom    = null;
            _cachedReceiverForAtom = null;
            _giverAtom             = null;
            _receiverAtom          = null;
            _giverMouth            = null;
            _receiverLabia         = null;
            _receiverPelvis        = null;
            _wasActive             = false;
            OralIntensity          = null;
            _prevGiverMouthTime    = 0f;
            _prevReceiverPelvisTime = 0f;
        }
    }
}
