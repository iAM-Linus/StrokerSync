using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Combined penetration source — runs MaleFemaleSource (penis / toy) and
    /// FingerSource (finger penetration + clitoral stimulation) simultaneously.
    ///
    /// PENETRATION MERGE RULE:
    ///   If both sources detect penetration in the same frame, take the signal
    ///   with the lower outPos (0 = device fully down = fully inserted).
    ///   This keeps the device stroke in sync with whichever is deepest.
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
        }

        public void OnInit(StrokerSync plugin)
        {
            // Logic only — UI is built by StrokerSync's tab system
            _maleFemale.InitLogic(plugin);
            _finger.InitLogic(plugin);
        }

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            float mfPos = 0f, mfVel = 0f;
            float fPos  = 0f, fVel  = 0f;

            bool mfActive = _maleFemale.OnUpdate(ref mfPos, ref mfVel);
            bool fActive  = _finger.OnUpdate(ref fPos,  ref fVel);

            if (!mfActive && !fActive)
                return false;

            if (mfActive && fActive)
            {
                // Both penetrating: take whichever is inserted deeper (lower position)
                if (mfPos <= fPos)
                { outPos = mfPos; outVelocity = mfVel; }
                else
                { outPos = fPos;  outVelocity = fVel;  }
            }
            else if (mfActive)
            { outPos = mfPos; outVelocity = mfVel; }
            else
            { outPos = fPos;  outVelocity = fVel;  }

            return true;
        }

        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime)
        {
            _maleFemale.OnSimulatorUpdate(prevPos, newPos, deltaTime);
        }

        public void OnDestroy(StrokerSync plugin)
        {
            // Non-UI teardown only — UI is removed by the tab-rebuild cleanup list
            _finger.DestroyLogic(plugin);
        }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            _maleFemale.OnSceneLoaded(plugin);
            _finger.OnSceneLoaded(plugin);
        }

        // =====================================================================
        // TAB UI — called by StrokerSync when building each tab
        // =====================================================================

        /// <summary>
        /// Builds left-column detection/selection/range UI for the Stroker page.
        /// Calibration controls and the signal display are created separately by
        /// BuildStrokerTab so they can be placed precisely in the right column.
        /// </summary>
        public Action BuildMaleFemaleUI(StrokerSync plugin)
        {
            _maleFemale.CreateDetectionUI(plugin);
            return () => _maleFemale.DestroyUI();
        }

        /// <summary>
        /// Builds UI for finger / dildo penetration tracking (Penetration page, left column).
        /// </summary>
        public Action BuildFingerPenetrationUI(StrokerSync plugin)
        {

            _finger.CreatePenetrationUI(plugin);

            return () =>
            {
                _finger.DestroyPenetrationUI();
            };
        }

        /// <summary>Legacy combined UI — use BuildMaleFemaleUI + BuildFingerPenetrationUI instead.</summary>
        public Action BuildStrokerUI(StrokerSync plugin)
        {
            var mfCleanup = BuildMaleFemaleUI(plugin);
            var sep = plugin.CreateSpacer();
            var penCleanup = BuildFingerPenetrationUI(plugin);
            return () => { mfCleanup(); plugin.RemoveSpacer(sep); penCleanup(); };
        }

        /// <summary>
        /// Builds clitoral UI for the Vibration tab (left column).
        /// The returned cleanup action removes all created elements.
        /// </summary>
        public Action BuildVibrationUI(StrokerSync plugin)
        {
            var sep    = plugin.CreateSpacer();

            _finger.CreateClitoralUI(plugin);

            return () =>
            {
                plugin.RemoveSpacer(sep);
                _finger.DestroyClitoralUI();
            };
        }
    }
}
