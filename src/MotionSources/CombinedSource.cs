using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Combined penetration source — runs MaleFemaleSource (penis / toy) and
    /// FingerSource (finger penetration + clitoral stimulation) simultaneously,
    /// with optional Timeline Curve Learning for deterministic look-ahead.
    ///
    /// PENETRATION MERGE RULE:
    ///   If both sources detect penetration in the same frame, take the signal
    ///   with the lower outPos (0 = device fully down = fully inserted).
    ///   This keeps the device stroke in sync with whichever is deepest.
    ///
    /// TIMELINE CURVE LEARNING:
    ///   When enabled, TimelineCurveAccess auto-detects VAM Timeline in the
    ///   scene, reads clip time from the "Scrubber" storable, and records the
    ///   physics-tracked position over one full animation loop.  After the loop
    ///   completes, PredictPosition() returns interpolated look-ahead values
    ///   from the recorded table — eliminating velocity extrapolation overshoot.
    ///   The physics sources ALWAYS remain the primary position source.
    ///
    /// CLITORAL:
    ///   FingerSource.ClitoralIntensity is always exposed via this class regardless
    ///   of which penetration signal is active. StrokerSync reads it to drive
    ///   vibrators independently of the Handy stroke.
    /// </summary>
    public class CombinedSource : IMotionSource
    {
        private readonly MaleFemaleSource _maleFemale = new MaleFemaleSource();
        private readonly FingerSource     _finger     = new FingerSource();

        private JSONStorableBool _blendFingerPenetration;
        private JSONStorableBool _timelineCurveLearning;

        // Timeline curve recording — auto-detects Timeline, records physics
        // position indexed by clip time, provides look-ahead after one loop.
        private readonly TimelineCurveAccess _curveAccess = new TimelineCurveAccess();
        private JSONStorableString _curveStatus;
        private JSONStorableAction _reRecordCurve;

        // =====================================================================
        // PROPERTIES
        // =====================================================================

        /// <summary>
        /// Non-null when a finger is in the clitoral zone (not penetrating).
        /// 0–1 intensity combining base level and movement speed.
        /// </summary>
        public float? ClitoralIntensity => _finger.ClitoralIntensity;

        /// <summary>
        /// Forward AutoDetectToyAxis from MaleFemaleSource for the overlay panel.
        /// </summary>
        public void AutoDetectToyAxis() => _maleFemale.AutoDetectToyAxis();

        /// <summary>
        /// When true and both MaleFemale and Finger sources are active,
        /// the output is an average of both signals instead of picking
        /// the deeper one.
        /// </summary>
        public JSONStorableBool BlendFingerPenetration => _blendFingerPenetration;

        /// <summary>
        /// When true, Timeline auto-detection and curve recording are active.
        /// </summary>
        public JSONStorableBool TimelineCurveLearning => _timelineCurveLearning;

        // ── MaleFemaleSource storable pass-throughs (used by BuildStrokerTab) ─
        public JSONStorableString MFRangeDisplay    => _maleFemale.PenRangeDisplay;
        public JSONStorableFloat  MFNoiseFilter     => _maleFemale.NoiseFilter;
        public JSONStorableBool   MFAutoCalOnLoad   => _maleFemale.AutoCalOnLoad;
        public JSONStorableFloat  MFAutoCalDelay    => _maleFemale.AutoCalDelay;
        public JSONStorableBool   MFRollingCal      => _maleFemale.RollingCal;
        public JSONStorableFloat  MFRollingWindow   => _maleFemale.RollingWindowSecs;
        public JSONStorableFloat  MFRollingRate     => _maleFemale.RollingContractRate;

        // =====================================================================
        // IMOTIONSOURCE
        // =====================================================================

        public void OnInitStorables(StrokerSync plugin)
        {
            _maleFemale.OnInitStorables(plugin);
            _finger.OnInitStorables(plugin);

            _blendFingerPenetration = new JSONStorableBool("combined_BlendFingerPenetration", false);
            plugin.RegisterBool(_blendFingerPenetration);

            _timelineCurveLearning = new JSONStorableBool("combined_TimelineCurveLearning", false);
            plugin.RegisterBool(_timelineCurveLearning);

            _curveStatus = new JSONStorableString("combined_CurveStatus", "");
            plugin.RegisterString(_curveStatus);

            _reRecordCurve = new JSONStorableAction("combined_ReRecordCurve",
                () => _curveAccess.ForceReRecord());
        }

        public void OnInit(StrokerSync plugin)
        {
            _maleFemale.InitLogic(plugin);
            _finger.InitLogic(plugin);
        }

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            // --- Physics sources (ALWAYS the primary position source) ---
            float mfPos = 0f, mfVel = 0f;
            float fPos  = 0f, fVel  = 0f;

            bool mfActive = _maleFemale.OnUpdate(ref mfPos, ref mfVel);
            bool fActive  = _finger.OnUpdate(ref fPos,  ref fVel);

            if (!mfActive && !fActive)
            {
                // No physics data — still feed zero to curve recorder if enabled
                if (_timelineCurveLearning.val)
                {
                    _curveAccess.Update(0f);
                    _curveStatus.val = _curveAccess.Status;
                }
                return false;
            }

            // Merge physics signals
            float physicsPos, physicsVel;

            if (mfActive && fActive)
            {
                if (_blendFingerPenetration.val)
                {
                    physicsPos = (mfPos + fPos) * 0.5f;
                    physicsVel = (mfVel + fVel) * 0.5f;
                }
                else
                {
                    if (mfPos <= fPos)
                    { physicsPos = mfPos; physicsVel = mfVel; }
                    else
                    { physicsPos = fPos;  physicsVel = fVel;  }
                }
            }
            else if (mfActive)
            { physicsPos = mfPos; physicsVel = mfVel; }
            else
            { physicsPos = fPos;  physicsVel = fVel;  }

            // --- Timeline curve recording ---
            // Feed the physics position to the recorder every frame.
            // TimelineCurveAccess reads clip time from Timeline's Scrubber storable
            // and pairs it with this physics position.
            if (_timelineCurveLearning.val)
            {
                _curveAccess.Update(physicsPos);
                _curveStatus.val = _curveAccess.Status;
            }

            outPos      = physicsPos;
            outVelocity = physicsVel;
            return true;
        }

        /// <summary>
        /// Deterministic look-ahead via the recorded Timeline curve.
        /// Returns null when curve learning is off or curve not yet recorded,
        /// causing the caller to fall back to velocity extrapolation.
        /// </summary>
        public float? PredictPosition(float deltaSeconds)
        {
            if (!_timelineCurveLearning.val || !_curveAccess.IsReady)
                return null;
            return _curveAccess.PredictPosition(deltaSeconds);
        }

        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime)
        {
            _maleFemale.OnSimulatorUpdate(prevPos, newPos, deltaTime);
        }

        public void OnDestroy(StrokerSync plugin)
        {
            _finger.DestroyLogic(plugin);
        }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            _maleFemale.OnSceneLoaded(plugin);
            _finger.OnSceneLoaded(plugin);
            _curveAccess.Invalidate();
        }

        // =====================================================================
        // TAB UI — called by StrokerSync when building each tab
        // =====================================================================

        /// <summary>
        /// Builds left-column detection/selection/range UI for the Stroker page.
        /// </summary>
        public Action BuildMaleFemaleUI(StrokerSync plugin)
        {
            _maleFemale.CreateDetectionUI(plugin);
            return () => _maleFemale.DestroyUI();
        }

        /// <summary>
        /// Builds UI for finger / dildo penetration tracking (Penetration page).
        /// </summary>
        public Action BuildFingerPenetrationUI(StrokerSync plugin)
        {
            var blendToggle = plugin.CreateToggle(_blendFingerPenetration);
            blendToggle.label = "Blend Finger + Penetration";

            _finger.CreatePenetrationUI(plugin);

            return () =>
            {
                plugin.RemoveToggle(blendToggle);
                _finger.DestroyPenetrationUI();
            };
        }

        /// <summary>Legacy combined UI.</summary>
        public Action BuildStrokerUI(StrokerSync plugin)
        {
            var mfCleanup = BuildMaleFemaleUI(plugin);
            var sep = plugin.CreateSpacer();
            var penCleanup = BuildFingerPenetrationUI(plugin);
            return () => { mfCleanup(); plugin.RemoveSpacer(sep); penCleanup(); };
        }

        /// <summary>
        /// Builds clitoral UI for the Vibration tab (left column).
        /// </summary>
        public Action BuildVibrationUI(StrokerSync plugin)
        {
            var sep = plugin.CreateSpacer();
            _finger.CreateClitoralUI(plugin);

            return () =>
            {
                plugin.RemoveSpacer(sep);
                _finger.DestroyClitoralUI();
            };
        }

        /// <summary>
        /// Builds UI for Timeline Curve Learning — toggle, status, and re-record button.
        /// </summary>
        public Action BuildTimelineCurveUI(StrokerSync plugin)
        {
            var toggle = plugin.CreateToggle(_timelineCurveLearning);
            toggle.label = "Timeline Curve Learning";

            var statusField = plugin.CreateTextField(_curveStatus);
            statusField.height = 50f;

            var reRecordBtn = plugin.CreateButton("Re-record Curve");
            reRecordBtn.button.onClick.AddListener(() => _curveAccess.ForceReRecord());

            return () =>
            {
                plugin.RemoveToggle(toggle);
                plugin.RemoveTextField(statusField);
                plugin.RemoveButton(reRecordBtn);
            };
        }
    }
}
