using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    public class CombinedSource : IMotionSource
    {
        #region Sources

        private readonly MaleFemaleSource _maleFemale = new MaleFemaleSource();
        private readonly FingerSource     _finger     = new FingerSource();
        private readonly OralSource       _oral       = new OralSource();
        private readonly SoloSource       _solo       = new SoloSource();

        #endregion

        #region Timeline Curve Recording

        private JSONStorableBool _blendFingerPenetration;
        private JSONStorableBool _timelineCurveLearning;

        private readonly TimelineCurveAccess _curveAccess = new TimelineCurveAccess();
        private JSONStorableString _curveStatus;
        private JSONStorableAction _reRecordCurve;

        #endregion

        #region Properties

        public float? ClitoralIntensity => _finger.ClitoralIntensity;

        /// <summary>
        /// Non-null when the giver's mouth is inside the oral contact zone.
        /// 0–1 intensity based on head/hip movement speed.
        /// </summary>
        public float? OralIntensity => _oral.OralIntensity;

        public void AutoDetectToyAxis() => _maleFemale.AutoDetectToyAxis();

        public JSONStorableBool BlendFingerPenetration => _blendFingerPenetration;

        public JSONStorableBool TimelineCurveLearning => _timelineCurveLearning;

        #endregion

        #region MaleFemaleSource Passthroughs

        public JSONStorableString MFRangeDisplay    => _maleFemale.PenRangeDisplay;
        public JSONStorableFloat  MFNoiseFilter     => _maleFemale.NoiseFilter;
        public JSONStorableBool   MFAutoCalOnLoad   => _maleFemale.AutoCalOnLoad;
        public JSONStorableFloat  MFAutoCalDelay    => _maleFemale.AutoCalDelay;
        public JSONStorableBool   MFRollingCal      => _maleFemale.RollingCal;
        public JSONStorableFloat  MFRollingWindow   => _maleFemale.RollingWindowSecs;
        public JSONStorableFloat  MFRollingRate     => _maleFemale.RollingContractRate;

        #endregion

        #region IMotionSource Implementation

        public void OnInitStorables(StrokerSync plugin)
        {
            _maleFemale.OnInitStorables(plugin);
            _finger.OnInitStorables(plugin);
            _oral.OnInitStorables(plugin);
            _solo.OnInitStorables(plugin);

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
            _solo.InitLogic(plugin);
        }

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            // --- Physics sources (ALWAYS the primary position source) ---
            float sPos  = 0f, sVel  = 0f;
            float mfPos = 0f, mfVel = 0f;
            float fPos  = 0f, fVel  = 0f;

            // SOLO MODE PREEMPTION
            if (_solo.Enabled.val)
            {
                bool sActive = _solo.OnUpdate(ref sPos, ref sVel);

                if (!sActive) return false;

                outPos = sPos;
                outVelocity = sVel;
                return true; // Immediately output solo tracking
            }

            // Standard Tracking if Solo is OFF
            bool mfActive = _maleFemale.OnUpdate(ref mfPos, ref mfVel);
            bool fActive = _finger.OnUpdate(ref fPos, ref fVel);

            // Oral runs every frame regardless of penetration state.
            // Uses the same zone centre as the clitoral indicator sphere so
            // the user only needs to position one visual marker for both.
            _oral.Update(_finger.ClitoralZonePosition);

            if (!mfActive && !fActive)
            {
                if (_timelineCurveLearning.val)
                {
                    _curveAccess.Update(0f);
                    _curveStatus.val = _curveAccess.Status;
                }
                return false;
            }

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

            if (_timelineCurveLearning.val)
            {
                _curveAccess.Update(physicsPos);
                _curveStatus.val = _curveAccess.Status;
            }

            outPos      = physicsPos;
            outVelocity = physicsVel;
            return true;
        }

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
            _oral.OnSceneLoaded();
            _curveAccess.Invalidate();
        }

        #endregion

        #region Tab UI

        public Action BuildMaleFemaleUI(StrokerSync plugin)
        {
            _maleFemale.CreateDetectionUI(plugin);
            return () => _maleFemale.DestroyUI();
        }

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

        public Action BuildStrokerUI(StrokerSync plugin)
        {
            var mfCleanup = BuildMaleFemaleUI(plugin);
            var sep = plugin.CreateSpacer();
            var penCleanup = BuildFingerPenetrationUI(plugin);
            return () => { mfCleanup(); plugin.RemoveSpacer(sep); penCleanup(); };
        }

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
        /// Builds cunnilingus detection UI for the Vibration tab (right column).
        /// </summary>
        public Action BuildOralUI(StrokerSync plugin)
        {
            return _oral.CreateUI(plugin);
        }

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

        public Action BuildSoloUI(StrokerSync plugin)
        {
            return _solo.BuildSoloUI(plugin);
        }

        #endregion
    }
}
