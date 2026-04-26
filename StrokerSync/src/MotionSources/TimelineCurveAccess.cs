using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Auto-detects VAM Timeline in the scene and records a position curve
    /// indexed by clip time for deterministic look-ahead prediction.
    ///
    /// NO System.Reflection is used — all Timeline access goes through VAM's
    /// standard JSONStorable API (GetFloatJSONParam, GetBoolJSONParam).
    /// If Timeline is not present, all methods return graceful defaults and
    /// the physics-based motion sources work exactly as before.
    ///
    /// USAGE:
    ///   The caller (CombinedSource) feeds the physics-tracked position each
    ///   frame via RecordFrame(physicsPosition).  This class reads the current
    ///   clip time from Timeline's "Scrubber" storable and records the pair.
    ///   After one full loop completes, PredictPosition() returns interpolated
    ///   look-ahead values from the recorded table.
    ///
    /// TIMELINE STORABLES USED:
    ///   "Scrubber"    — current clip time (float, range 0 to animationLength)
    ///   "Speed"       — global playback speed multiplier (float)
    ///   "Is Playing"  — playback state (bool)
    ///   "Animation"   — current clip/animation name (string chooser)
    /// </summary>
    public class TimelineCurveAccess
    {
        // =====================================================================
        // TIMELINE STORABLE REFERENCES
        // =====================================================================

        private JSONStorable      _timelineStorable;
        private JSONStorableFloat _scrubberParam;     // clip time (0..animLength)
        private JSONStorableFloat _speedParam;        // global speed
        private JSONStorableBool  _isPlayingParam;
        private JSONStorableStringChooser _animationParam;

        private bool _storableResolved;   // True once Timeline storables are cached

        // =====================================================================
        // RECORDED CURVE
        // =====================================================================

        private readonly List<float> _sampleTimes     = new List<float>();
        private readonly List<float> _samplePositions  = new List<float>();

        private bool  _isRecording;
        private bool  _curveReady;
        private float _prevClipTime;
        private float _animationLength;
        private float _recordedAnimationLength;
        private string _recordedAnimation;

        // Auto-detect throttle
        private float _scanTimer;
        private const float SCAN_INTERVAL = 2.0f;

        private const int MIN_SAMPLES = 10;

        // Drift detection — triggers re-record when live physics diverges
        // from the recorded curve for too many consecutive frames.
        private int   _driftFrames;
        private const float DRIFT_THRESHOLD   = 0.15f;   // position units (0–1)
        private const int   DRIFT_FRAME_LIMIT = 45;      // ~0.75 s at 60 fps
        private float _reRecordCooldown;                  // prevents thrashing
        private const float RE_RECORD_COOLDOWN = 2.0f;   // seconds after finalize

        // Animation-length change detection threshold
        private const float LENGTH_CHANGE_THRESHOLD = 0.05f;

        // =====================================================================
        // PUBLIC API
        // =====================================================================

        /// <summary>True when the recorded curve is available for look-ahead.</summary>
        public bool IsReady => _curveReady;

        /// <summary>True while recording the first loop.</summary>
        public bool IsRecording => _isRecording;

        /// <summary>Number of samples recorded so far.</summary>
        public int SampleCount => _sampleTimes.Count;

        /// <summary>Detected animation length in seconds, or 0.</summary>
        public float AnimationLength => _animationLength;

        /// <summary>True once a Timeline storable has been found in the scene.</summary>
        public bool TimelineDetected => _storableResolved;

        /// <summary>
        /// Manually trigger a re-record of the curve.  Can be called from a
        /// UI button at any time.
        /// </summary>
        public void ForceReRecord()
        {
            _curveReady = false;
            _isRecording = false;
            _driftFrames = 0;
            _reRecordCooldown = 0f;
            ClearRecording();
            if (_storableResolved)
                SuperController.LogMessage(
                    "StrokerSync: Manual re-record requested — recording next loop.");
        }

        /// <summary>
        /// Status string for UI display.
        /// </summary>
        public string Status
        {
            get
            {
                if (!_storableResolved)
                    return "Scanning for Timeline...";
                if (_curveReady)
                    return $"Look-ahead: ACTIVE ({_sampleTimes.Count} samples, " +
                           $"{_animationLength:F2}s loop)";
                if (_isRecording)
                    return $"Recording curve... ({_sampleTimes.Count} samples)";
                return "Timeline found — waiting for playback...";
            }
        }

        /// <summary>
        /// Call every frame.  Auto-detects Timeline if not yet found, then
        /// records the physics position indexed by clip time.
        /// When the curve is ready, performs drift detection and animation-length
        /// change detection to auto-trigger re-recording when the animation changes.
        /// </summary>
        /// <param name="physicsPosition">Current position from physics tracking (0–1).</param>
        public void Update(float physicsPosition)
        {
            // Auto-detect Timeline if not yet resolved
            if (!_storableResolved)
            {
                _scanTimer -= Time.deltaTime;
                if (_scanTimer > 0f) return;
                _scanTimer = SCAN_INTERVAL;
                ScanForTimeline();
                if (!_storableResolved) return;
            }

            // Verify storable is still alive
            if (_timelineStorable == null)
            {
                _storableResolved = false;
                return;
            }

            float clipTime = _scrubberParam.val;
            bool isPlaying = _isPlayingParam != null && _isPlayingParam.val;

            // Update animation length from Scrubber's configured max
            if (_scrubberParam.max > 0.001f)
                _animationLength = _scrubberParam.max;

            // Invalidate if animation/clip changed
            if ((_curveReady || _isRecording) && _animationParam != null)
            {
                string currentAnim = _animationParam.val;
                if (_recordedAnimation != null && currentAnim != _recordedAnimation)
                {
                    _curveReady = false;
                    _isRecording = false;
                    _driftFrames = 0;
                    _reRecordCooldown = 0f;
                    ClearRecording();
                    SuperController.LogMessage(
                        "StrokerSync: Timeline clip changed — re-recording curve.");
                }
            }

            // ── Animation-length change detection ──
            // If the clip length changed since recording, the curve is stale.
            if (_curveReady && _recordedAnimationLength > 0.001f)
            {
                float lengthDiff = Mathf.Abs(_animationLength - _recordedAnimationLength);
                if (lengthDiff > LENGTH_CHANGE_THRESHOLD)
                {
                    _curveReady = false;
                    _isRecording = false;
                    _driftFrames = 0;
                    _reRecordCooldown = 0f;
                    ClearRecording();
                    SuperController.LogMessage(
                        $"StrokerSync: Animation length changed " +
                        $"({_recordedAnimationLength:F2}s → {_animationLength:F2}s) " +
                        $"— re-recording curve.");
                }
            }

            if (!isPlaying) return;

            // ── Drift detection (only while curve is ready) ──
            if (_curveReady)
            {
                // Cooldown after finalize to let things settle
                if (_reRecordCooldown > 0f)
                {
                    _reRecordCooldown -= Time.deltaTime;
                    return;
                }

                float expected = InterpolatedLookup(clipTime);
                float error = Mathf.Abs(physicsPosition - expected);
                if (error > DRIFT_THRESHOLD)
                    _driftFrames++;
                else
                    _driftFrames = Mathf.Max(0, _driftFrames - 1); // slow decay

                if (_driftFrames >= DRIFT_FRAME_LIMIT)
                {
                    _curveReady = false;
                    _isRecording = false;
                    _driftFrames = 0;
                    ClearRecording();
                    SuperController.LogMessage(
                        "StrokerSync: Curve drift detected — re-recording.");
                }
                return;
            }

            // --- RECORDING & LOOP WRAP DETECTION ---

            // Detect if the playhead just wrapped backward (e.g. loop restarted)
            // Using 0.3f threshold to ignore engine audio sync jitter
            bool justWrapped = (_prevClipTime > 0f && clipTime < _prevClipTime - 0.3f);

            // Also treat jumping to the absolute beginning of the timeline as a wrap
            // (useful for when the user hits the stop/play reset button)
            if (clipTime < 0.05f && _prevClipTime >= 0.05f)
            {
                justWrapped = true;
            }

            if (!_isRecording)
            {
                // ONLY start recording when a loop boundary is crossed.
                // If we clear the curve mid-clip, we wait patiently for it to loop.
                // This guarantees we capture a full, complete cycle.
                if (justWrapped || (clipTime < 0.1f && _sampleTimes.Count == 0))
                {
                    _isRecording = true;
                    ClearRecording();
                    _prevClipTime = clipTime;
                    _recordedAnimation = _animationParam != null ? _animationParam.val : null;
                    AddSample(clipTime, physicsPosition);
                }
                else
                {
                    _prevClipTime = clipTime;
                }
                return;
            }

            // We are currently recording. Stop when the loop wraps.
            if (justWrapped)
            {
                if (_sampleTimes.Count >= MIN_SAMPLES)
                {
                    FinalizeRecording();
                }
                else
                {
                    ClearRecording();
                    _isRecording = false;
                }
                _prevClipTime = clipTime;
                return;
            }

            // Record forward-progressing samples
            if (clipTime > _prevClipTime + 0.0001f)
                AddSample(clipTime, physicsPosition);

            _prevClipTime = clipTime;
        }

        /// <summary>
        /// Predict the position at (currentClipTime + deltaSeconds).
        /// Returns null if the curve has not been recorded yet.
        /// </summary>
        public float? PredictPosition(float deltaSeconds)
        {
            if (!_curveReady || _scrubberParam == null) return null;

            float clipTime = _scrubberParam.val;
            float speed = _speedParam != null ? _speedParam.val : 1f;
            float futureTime = clipTime + deltaSeconds * speed;

            if (_animationLength > 0.001f)
                futureTime = ((futureTime % _animationLength) + _animationLength) % _animationLength;

            return InterpolatedLookup(futureTime);
        }

        /// <summary>
        /// Invalidate all state.  Call on scene load.
        /// </summary>
        public void Invalidate()
        {
            _curveReady = false;
            _isRecording = false;
            _storableResolved = false;
            _timelineStorable = null;
            _scrubberParam = null;
            _speedParam = null;
            _isPlayingParam = null;
            _animationParam = null;
            _scanTimer = 0f;
            _driftFrames = 0;
            _reRecordCooldown = 0f;
            _recordedAnimationLength = 0f;
            ClearRecording();
        }

        // =====================================================================
        // AUTO-DETECTION
        // =====================================================================

        private void ScanForTimeline()
        {
            var sc = SuperController.singleton;
            if (sc == null) return;

            foreach (var atom in sc.GetAtoms())
            {
                if (atom == null || !atom.on) continue;

                foreach (var id in atom.GetStorableIDs())
                {
                    if (id == null) continue;
                    if (!id.Contains("VamTimeline")) continue;

                    var storable = atom.GetStorableByID(id);
                    if (storable == null) continue;

                    // Verify it has the expected Timeline storables
                    var scrubber = storable.GetFloatJSONParam("Scrubber");
                    if (scrubber == null) continue;

                    _timelineStorable = storable;
                    _scrubberParam = scrubber;
                    _speedParam = storable.GetFloatJSONParam("Speed");
                    _isPlayingParam = storable.GetBoolJSONParam("Is Playing");

                    try { _animationParam = storable.GetStringChooserJSONParam("Animation"); }
                    catch { _animationParam = null; }

                    _storableResolved = true;

                    SuperController.LogMessage(
                        $"StrokerSync: Timeline detected on '{atom.uid}' ({id}).");
                    return;
                }
            }
        }

        // =====================================================================
        // RECORDING INTERNALS
        // =====================================================================

        private void AddSample(float time, float position)
        {
            _sampleTimes.Add(time);
            _samplePositions.Add(position);
        }

        private void ClearRecording()
        {
            _sampleTimes.Clear();
            _samplePositions.Clear();
            _prevClipTime = 0f;
        }

        private void FinalizeRecording()
        {
            _isRecording = false;
            _curveReady = true;
            _driftFrames = 0;
            _reRecordCooldown = RE_RECORD_COOLDOWN;

            if (_animationLength < 0.001f && _sampleTimes.Count > 0)
                _animationLength = _sampleTimes[_sampleTimes.Count - 1];

            // Snapshot the animation length at record time for change detection
            _recordedAnimationLength = _animationLength;

            // Wrap-around sample: copy first position at the end for smooth loop
            if (_sampleTimes.Count > 0 && _animationLength > 0.001f)
            {
                _sampleTimes.Add(_animationLength);
                _samplePositions.Add(_samplePositions[0]);
            }

            SuperController.LogMessage(
                $"StrokerSync: Timeline curve recorded — {_sampleTimes.Count} samples over " +
                $"{_animationLength:F2}s.  Deterministic look-ahead active.");
        }

        private float InterpolatedLookup(float time)
        {
            int count = _sampleTimes.Count;
            if (count == 0) return 0f;
            if (count == 1) return _samplePositions[0];

            if (time <= _sampleTimes[0])         return _samplePositions[0];
            if (time >= _sampleTimes[count - 1]) return _samplePositions[count - 1];

            int lo = 0, hi = count - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (_sampleTimes[mid] <= time) lo = mid;
                else hi = mid;
            }

            float t0 = _sampleTimes[lo];
            float t1 = _sampleTimes[hi];
            float p0 = _samplePositions[lo];
            float p1 = _samplePositions[hi];

            if (t1 - t0 < 0.0001f) return p0;

            float t = (time - t0) / (t1 - t0);
            return Mathf.Lerp(p0, p1, t);
        }
    }
}
