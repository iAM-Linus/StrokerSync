using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Motion source that reads a float parameter from any atom/storable.
    /// Primary use case: VAM Timeline float param animations.
    ///
    /// WORKFLOW:
    ///   1. In Timeline, create a Float Param curve (0.0-1.0) = stroke position
    ///      (or any plugin/storable that exposes a float representing position)
    ///   2. In StrokerSync, select "Timeline / Float Param" motion source
    ///   3. Pick the Atom, Receiver (storable), and Parameter name
    ///   4. StrokerSync reads that float every frame as the stroke position
    ///
    /// The float value is expected to be in the 0.0-1.0 range where:
    ///   0.0 = device at bottom (fully "in")
    ///   1.0 = device at top (fully "out")
    /// An "Invert" toggle is provided for curves authored in the opposite direction.
    ///
    /// TIMELINE CURVE LEARNING:
    ///   When the resolved storable is a VamTimeline.AtomPlugin instance, the source
    ///   reads clip time from Timeline's "Scrubber" storable and records position
    ///   samples over the first animation loop.  Once a complete loop is captured,
    ///   PredictPosition() provides deterministic look-ahead by interpolated lookup
    ///   at (clipTime + delta) — eliminating velocity extrapolation overshoot.
    ///   No Timeline source code is compiled into this plugin; all access is via
    ///   VAM's standard JSONStorable API.
    /// </summary>
    public class TimelineSource : IMotionSource
    {
        private StrokerSync _plugin;
        private SuperController Controller => SuperController.singleton;

        // --- Resolved parameter reference ---
        private Atom _resolvedAtom;
        private JSONStorable _resolvedStorable;
        private JSONStorableFloat _resolvedParam;
        private bool _isResolved;

        // --- Timeline curve access (storable-based, optional) ---
        private readonly TimelineCurveAccess _curveAccess = new TimelineCurveAccess();
        private bool _isTimelineStorable;   // True when _resolvedStorable is a Timeline plugin

        /// <summary>True when the resolved storable is a Timeline plugin AND the recorded curve is ready.</summary>
        public bool HasCurveAccess => _curveAccess.IsReady;

        /// <summary>True when Timeline is detected and the curve is being recorded.</summary>
        public bool IsRecordingCurve => _curveAccess.IsRecording;

        // Velocity tracking
        private float _prevPosition;
        private float _prevPositionTime;

        // UI refresh throttle
        private float _refreshTimer;
        private const float REFRESH_INTERVAL = 0.5f;

        // --- Storables ---
        private JSONStorableStringChooser _atomChooser;
        private JSONStorableStringChooser _storableChooser;
        private JSONStorableStringChooser _paramChooser;
        private JSONStorableBool _invertParam;
        private JSONStorableString _statusDisplay;

        // UI element cleanup
        private readonly List<Action> _uiCleanup = new List<Action>();

        // Track last resolved IDs to detect changes
        private string _lastAtomId;
        private string _lastStorableId;
        private string _lastParamId;

        public void OnInitStorables(StrokerSync plugin)
        {
            _plugin = plugin;

            _atomChooser = new JSONStorableStringChooser(
                "timeline_Atom", new List<string>(), "", "Source Atom",
                (JSONStorableStringChooser.SetStringCallback)OnAtomChanged);
            plugin.RegisterStringChooser(_atomChooser);

            _storableChooser = new JSONStorableStringChooser(
                "timeline_Storable", new List<string>(), "", "Storable (Plugin)",
                (JSONStorableStringChooser.SetStringCallback)OnStorableChanged);
            plugin.RegisterStringChooser(_storableChooser);

            _paramChooser = new JSONStorableStringChooser(
                "timeline_Param", new List<string>(), "", "Float Parameter",
                (JSONStorableStringChooser.SetStringCallback)OnParamChanged);
            plugin.RegisterStringChooser(_paramChooser);

            _invertParam = new JSONStorableBool("timeline_Invert", false);
            plugin.RegisterBool(_invertParam);

            _statusDisplay = new JSONStorableString("timeline_Status", "Select an atom, storable, and float parameter.");
            plugin.RegisterString(_statusDisplay);
        }

        public void OnInit(StrokerSync plugin)
        {
            _plugin = plugin;
            // UI is created on-demand by the tab system (CreateUI / DestroyUI).
            // Try to resolve from saved values immediately so the source is ready.
            TryResolve();
        }

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            // Lazy re-resolve if selection changed or reference died
            if (!_isResolved || _resolvedParam == null)
            {
                _refreshTimer -= Time.deltaTime;
                if (_refreshTimer <= 0f)
                {
                    _refreshTimer = REFRESH_INTERVAL;
                    TryResolve();
                }
                if (!_isResolved)
                    return false;
            }

            // Check if the storable/atom is still alive
            if (_resolvedAtom == null || !_resolvedAtom.on || _resolvedStorable == null)
            {
                _isResolved = false;
                return false;
            }

            // If this is a Timeline storable, keep trying to resolve storables
            if (_isTimelineStorable)
                _curveAccess.TryReResolve(_resolvedStorable);

            // Read the float value
            float raw = _resolvedParam.val;

            // Clamp to expected range
            raw = Mathf.Clamp01(raw);

            if (_invertParam.val)
                raw = 1f - raw;

            // Feed the position to the curve recorder (uses clip time from Scrubber)
            if (_isTimelineStorable)
            {
                bool wasRecording = _curveAccess.IsRecording;
                _curveAccess.RecordFrame(raw);

                // Update status when recording completes
                if (wasRecording && _curveAccess.IsReady)
                    UpdateCurveStatus();
            }

            // Velocity
            float now = Time.time;
            float dt = now - _prevPositionTime;
            float velocity = 0f;
            if (dt > 0.001f)
                velocity = Mathf.Clamp01(Mathf.Abs(raw - _prevPosition) / dt / 3.0f);
            _prevPosition = raw;
            _prevPositionTime = now;

            outPos = raw;
            outVelocity = velocity;
            return true;
        }

        /// <summary>
        /// Deterministic look-ahead: evaluate the recorded position curve at
        /// (clipTime + deltaSeconds).  Returns null if the curve has not been
        /// recorded yet (caller falls back to velocity extrapolation).
        /// </summary>
        public float? PredictPosition(float deltaSeconds)
        {
            if (!_isTimelineStorable || !_curveAccess.IsReady)
                return null;

            float? predicted = _curveAccess.Evaluate(deltaSeconds);
            if (!predicted.HasValue)
                return null;

            float val = Mathf.Clamp01(predicted.Value);
            if (_invertParam.val)
                val = 1f - val;
            return val;
        }

        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime) { }
        public void OnDestroy(StrokerSync plugin)
        {
            foreach (var cleanup in _uiCleanup)
            {
                try { cleanup(); } catch { }
            }
            _uiCleanup.Clear();
        }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            // Clear resolved references — atoms from old scene are destroyed
            _resolvedAtom = null;
            _resolvedStorable = null;
            _resolvedParam = null;
            _isResolved = false;
            _isTimelineStorable = false;
            _refreshTimer = 0f;
            _curveAccess.Invalidate();

            // Keep chooser values (user may load a scene with same atom names),
            // but trigger a re-resolve attempt
            if (_statusDisplay != null)
                _statusDisplay.val = "Scene loaded — re-resolving...";
        }

        // =====================================================================
        // RESOLUTION
        // =====================================================================

        private void TryResolve()
        {
            _isResolved = false;
            _resolvedAtom = null;
            _resolvedStorable = null;
            _resolvedParam = null;
            _isTimelineStorable = false;
            _curveAccess.Invalidate();

            string atomId = _atomChooser.val;
            string storableId = _storableChooser.val;
            string paramId = _paramChooser.val;

            if (string.IsNullOrEmpty(atomId) || string.IsNullOrEmpty(storableId) || string.IsNullOrEmpty(paramId))
            {
                UpdateStatus("Select an atom, storable, and float parameter.");
                return;
            }

            _resolvedAtom = Controller.GetAtomByUid(atomId);
            if (_resolvedAtom == null)
            {
                UpdateStatus($"Atom '{atomId}' not found.");
                return;
            }

            _resolvedStorable = _resolvedAtom.GetStorableByID(storableId);
            if (_resolvedStorable == null)
            {
                UpdateStatus($"Storable '{storableId}' not found on '{atomId}'.");
                return;
            }

            _resolvedParam = _resolvedStorable.GetFloatJSONParam(paramId);
            if (_resolvedParam == null)
            {
                UpdateStatus($"Float param '{paramId}' not found on '{storableId}'.");
                return;
            }

            _isResolved = true;

            // Detect if this is a Timeline plugin by checking the type name.
            // This avoids any compile-time dependency on Timeline.
            _isTimelineStorable = IsTimelinePlugin(_resolvedStorable);

            if (_isTimelineStorable)
            {
                _curveAccess.Resolve(_resolvedStorable);
                UpdateCurveStatus();
            }
            else
            {
                UpdateStatus($"Reading: {atomId} / {storableId} / {paramId}\nValue: {_resolvedParam.val:F3}");
            }
        }

        private void UpdateCurveStatus()
        {
            string atomId = _atomChooser.val;
            string storableId = _storableChooser.val;
            string paramId = _paramChooser.val;

            if (_curveAccess.IsReady)
            {
                var len = _curveAccess.GetAnimationLength();
                string lenStr = len.HasValue ? $"{len.Value:F2}s" : "?";
                UpdateStatus($"Timeline curve: {atomId} / {paramId}\n" +
                             $"Loop: {lenStr} — {_curveAccess.SampleCount} samples — Look-ahead: ACTIVE");
            }
            else if (_curveAccess.IsRecording)
            {
                UpdateStatus($"Timeline detected: {atomId} / {paramId}\n" +
                             $"Recording curve... ({_curveAccess.SampleCount} samples)");
            }
            else
            {
                UpdateStatus($"Timeline detected: {atomId} / {storableId} / {paramId}\n" +
                             "Waiting for playback to start...");
            }
        }

        /// <summary>
        /// Check if a JSONStorable is a VamTimeline.AtomPlugin by examining its
        /// runtime type name.  No compile-time dependency required.
        /// </summary>
        private static bool IsTimelinePlugin(JSONStorable storable)
        {
            if (storable == null) return false;

            // Check storable ID first (always safe, no reflection)
            string id = storable.storeId;
            if (!string.IsNullOrEmpty(id) && id.Contains("VamTimeline"))
                return true;

            // Fallback: check runtime type name (System.Object.GetType — not reflection)
            try
            {
                string typeName = storable.GetType().FullName;
                return typeName != null && typeName.Contains("VamTimeline");
            }
            catch
            {
                return false;
            }
        }

        private void UpdateStatus(string msg)
        {
            if (_statusDisplay != null)
                _statusDisplay.val = msg;
        }

        // =====================================================================
        // CHOOSER CALLBACKS
        // =====================================================================

        private void OnAtomChanged(string atomId)
        {
            _isResolved = false;
            _isTimelineStorable = false;
            _curveAccess.Invalidate();
            _storableChooser.valNoCallback = "";
            _paramChooser.valNoCallback = "";
            _storableChooser.choices = new List<string>();
            _paramChooser.choices = new List<string>();

            if (!string.IsNullOrEmpty(atomId))
                RefreshStorables(atomId);
        }

        private void OnStorableChanged(string storableId)
        {
            _isResolved = false;
            _isTimelineStorable = false;
            _curveAccess.Invalidate();
            _paramChooser.valNoCallback = "";
            _paramChooser.choices = new List<string>();

            string atomId = _atomChooser.val;
            if (!string.IsNullOrEmpty(atomId) && !string.IsNullOrEmpty(storableId))
                RefreshParams(atomId, storableId);
        }

        private void OnParamChanged(string paramId)
        {
            _isResolved = false;
            _isTimelineStorable = false;
            _curveAccess.Invalidate();
            TryResolve();
        }

        // =====================================================================
        // LIST POPULATION
        // =====================================================================

        private void RefreshAtoms()
        {
            var choices = new List<string>();
            foreach (var atom in Controller.GetAtoms())
            {
                if (atom.on)
                    choices.Add(atom.uid);
            }
            choices.Sort();
            _atomChooser.choices = choices;
        }

        private void RefreshStorables(string atomId)
        {
            var atom = Controller.GetAtomByUid(atomId);
            if (atom == null) return;

            var choices = new List<string>();
            var timelineChoices = new List<string>();

            foreach (var id in atom.GetStorableIDs())
            {
                // Prioritize Timeline storables at the top
                if (id.Contains("VamTimeline") || id.Contains("Timeline"))
                    timelineChoices.Add(id);
                else
                    choices.Add(id);
            }

            // Timeline storables first, then everything else
            timelineChoices.AddRange(choices);
            _storableChooser.choices = timelineChoices;
        }

        private void RefreshParams(string atomId, string storableId)
        {
            var atom = Controller.GetAtomByUid(atomId);
            if (atom == null) return;

            var storable = atom.GetStorableByID(storableId);
            if (storable == null) return;

            var choices = new List<string>();
            foreach (var paramName in storable.GetFloatParamNames())
            {
                choices.Add(paramName);
            }
            _paramChooser.choices = choices;
        }

        // =====================================================================
        // UI
        // =====================================================================

        public void DestroyUI()
        {
            foreach (var a in _uiCleanup) try { a(); } catch { }
            _uiCleanup.Clear();
        }

        public void CreateUI(StrokerSync plugin, bool rightSide = false)
        {
            _uiCleanup.Clear();

            var statusField = plugin.CreateTextField(_statusDisplay, rightSide);
            statusField.height = 70f;
            _uiCleanup.Add(() => plugin.RemoveTextField(statusField));

            // Atom chooser — refresh on open
            var atomPopup = plugin.CreateScrollablePopup(_atomChooser, rightSide);
            atomPopup.label = "Source Atom";
            atomPopup.popup.onOpenPopupHandlers += () => RefreshAtoms();
            _uiCleanup.Add(() => plugin.RemovePopup(atomPopup));

            // Storable chooser — refresh on open
            var storablePopup = plugin.CreateScrollablePopup(_storableChooser, rightSide);
            storablePopup.label = "Storable (Timeline plugin, etc.)";
            storablePopup.popup.onOpenPopupHandlers += () =>
            {
                if (!string.IsNullOrEmpty(_atomChooser.val))
                    RefreshStorables(_atomChooser.val);
            };
            _uiCleanup.Add(() => plugin.RemovePopup(storablePopup));

            // Param chooser — refresh on open
            var paramPopup = plugin.CreateScrollablePopup(_paramChooser, rightSide);
            paramPopup.label = "Float Parameter (0-1 = stroke pos)";
            paramPopup.popup.onOpenPopupHandlers += () =>
            {
                if (!string.IsNullOrEmpty(_atomChooser.val) && !string.IsNullOrEmpty(_storableChooser.val))
                    RefreshParams(_atomChooser.val, _storableChooser.val);
            };
            _uiCleanup.Add(() => plugin.RemovePopup(paramPopup));

            var sp = plugin.CreateSpacer(rightSide);
            _uiCleanup.Add(() => plugin.RemoveSpacer(sp));

            var invertToggle = plugin.CreateToggle(_invertParam, rightSide);
            invertToggle.label = "Invert (flip 0↔1)";
            _uiCleanup.Add(() => plugin.RemoveToggle(invertToggle));
        }
    }
}
