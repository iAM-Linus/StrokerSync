using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrokerSync.MotionSources
{
    /// <summary>
    /// Tracks the absolute movement of a single body part (Pelvis, Abdomen, etc.) 
    /// along a specific axis and normalizes it to a 0.0-1.0 stroke range.
    /// Perfect for solo scenes (cowgirl bouncing, masturbation) without penetration.
    /// </summary>
    public class SoloSource : IMotionSource
    {
        private StrokerSync _plugin;
        private SuperController Controller => SuperController.singleton;

        // --- State ---
        private Atom _targetAtom;
        private Rigidbody _cachedBodyPart;
        private float _minTracker;
        private float _maxTracker;
        private float _prevProj;
        private float _prevProjTime;

        // --- Settings Storables ---
        public JSONStorableBool Enabled { get; private set; }
        private JSONStorableStringChooser _atomChooser;
        private JSONStorableStringChooser _bodyPartChooser;
        private JSONStorableStringChooser _axisChooser;
        private JSONStorableBool _invertMotion;
        private JSONStorableFloat _minAmplitude;
        private JSONStorableFloat _adaptationSpeed;
        private JSONStorableString _liveDebugDisplay;

        // --- UI Cleanup ---
        private readonly List<Action> _uiCleanup = new List<Action>();

        public void OnInitStorables(StrokerSync plugin)
        {
            _plugin = plugin;

            Enabled = new JSONStorableBool("solo_Enabled", false);
            plugin.RegisterBool(Enabled);

            _atomChooser = new JSONStorableStringChooser("solo_Atom", new List<string> { "None" }, "None", "Target Character", OnAtomChanged);
            plugin.RegisterStringChooser(_atomChooser);

            var parts = new List<string> { "pelvis", "abdomen", "chest", "head", "LabiaTrigger", "VaginaTrigger", "Gen1Hard" };
            _bodyPartChooser = new JSONStorableStringChooser("solo_BodyPart", parts, "pelvis", "Body Part", OnPartChanged);
            plugin.RegisterStringChooser(_bodyPartChooser);

            var axes = new List<string> {
                "World Y (Up/Down)", "World Z (Forward/Back)", "World X (Left/Right)",
                "Local Y (Up/Down)", "Local Z (Forward/Back)", "Local X (Left/Right)"
            };
            _axisChooser = new JSONStorableStringChooser("solo_Axis", axes, "World Y (Up/Down)", "Motion Axis");
            plugin.RegisterStringChooser(_axisChooser);

            _invertMotion = new JSONStorableBool("solo_Invert", false);
            plugin.RegisterBool(_invertMotion);

            // Prevents micro-jitters from turning into full 100% strokes
            _minAmplitude = new JSONStorableFloat("solo_MinAmplitude", 0.08f, 0.02f, 0.30f, false);
            plugin.RegisterFloat(_minAmplitude);

            // How fast the min/max window chases the current position if the character changes posture
            _adaptationSpeed = new JSONStorableFloat("solo_AdaptationSpeed", 0.15f, 0.01f, 1.0f, false);
            plugin.RegisterFloat(_adaptationSpeed);

            _liveDebugDisplay = new JSONStorableString("solo_LiveDebug", "Tracking: OFF");
            plugin.RegisterString(_liveDebugDisplay);
        }

        public void OnInit(StrokerSync plugin) { _plugin = plugin; }
        public void InitLogic(StrokerSync plugin) { _plugin = plugin; }

        public bool OnUpdate(ref float outPos, ref float outVelocity)
        {
            if (!Enabled.val) return false;

            if (_targetAtom == null || _cachedBodyPart == null)
            {
                if (Time.frameCount % 60 == 0) RefreshCaches();
                if (_cachedBodyPart == null) return false;
            }

            Transform t = _cachedBodyPart.transform;
            Vector3 pos = t.position;
            float proj = 0f;

            switch (_axisChooser.val)
            {
                case "World Y (Up/Down)": proj = pos.y; break;
                case "World Z (Forward/Back)": proj = pos.z; break;
                case "World X (Left/Right)": proj = pos.x; break;
                case "Local Y (Up/Down)": proj = Vector3.Dot(pos, t.up); break;
                case "Local Z (Forward/Back)": proj = Vector3.Dot(pos, t.forward); break;
                case "Local X (Left/Right)": proj = Vector3.Dot(pos, t.right); break;
            }

            // Adaptive Window Tracking
            _minTracker = Mathf.Min(_minTracker, proj);
            _maxTracker = Mathf.Max(_maxTracker, proj);

            // Decay the min/max slowly towards current position to adapt to posture changes
            float decay = Time.deltaTime * _adaptationSpeed.val;
            _minTracker = Mathf.Lerp(_minTracker, proj, decay);
            _maxTracker = Mathf.Lerp(_maxTracker, proj, decay);

            // Normalize
            float range = Mathf.Max(_maxTracker - _minTracker, _minAmplitude.val);
            float normalized = Mathf.Clamp01((proj - _minTracker) / range);

            if (_invertMotion.val) normalized = 1f - normalized;

            // Velocity calculation
            float now = Time.time;
            float dt = now - _prevProjTime;
            float velocity = 0f;
            if (dt > 0.001f)
                velocity = Mathf.Clamp01(Mathf.Abs(proj - _prevProj) / dt / 2.0f);

            _prevProj = proj;
            _prevProjTime = now;

            if (Time.frameCount % 10 == 0)
                _liveDebugDisplay.val = $"Raw: {proj:F3}m | Min: {_minTracker:F3}m | Max: {_maxTracker:F3}m\nOut: {normalized:F2}";

            outPos = normalized;
            outVelocity = velocity;
            return true;
        }

        public float? PredictPosition(float deltaSeconds) { return null; }
        public void OnSimulatorUpdate(float prevPos, float newPos, float deltaTime) { }

        public void OnDestroy(StrokerSync plugin) { DestroyUI(); }

        public void OnSceneLoaded(StrokerSync plugin)
        {
            _targetAtom = null;
            _cachedBodyPart = null;
            _minTracker = float.MaxValue;
            _maxTracker = float.MinValue;
            if (_atomChooser != null) _atomChooser.valNoCallback = "None";
            plugin.StartCoroutine(DelayedRepopulate());
        }

        private void RefreshCaches()
        {
            _targetAtom = Controller.GetAtomByUid(_atomChooser.val);
            if (_targetAtom == null || !_targetAtom.on) return;

            foreach (var rb in _targetAtom.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb.name == _bodyPartChooser.val)
                {
                    _cachedBodyPart = rb;
                    _minTracker = float.MaxValue;
                    _maxTracker = float.MinValue;
                    SuperController.LogMessage($"StrokerSync: Solo tracking attached to {_cachedBodyPart.name}");
                    return;
                }
            }
        }

        private void OnAtomChanged(string s) { RefreshCaches(); }
        private void OnPartChanged(string s) { RefreshCaches(); }

        private System.Collections.IEnumerator DelayedRepopulate()
        {
            yield return new UnityEngine.WaitForSeconds(1.5f);
            PopulateAtomChooser();
        }

        private void PopulateAtomChooser()
        {
            if (Controller == null) return;
            var choices = new List<string> { "None" };
            foreach (var a in Controller.GetAtoms())
                if (a.on && a.type == "Person") choices.Add(a.uid);
            _atomChooser.choices = choices;
        }

        public Action BuildSoloUI(StrokerSync plugin)
        {
            DestroyUI();

            var toggle = plugin.CreateToggle(Enabled);
            toggle.label = "Enable Solo Tracking (Overrides Penetration)";
            _uiCleanup.Add(() => plugin.RemoveToggle(toggle));

            var spacer = plugin.CreateSpacer();
            _uiCleanup.Add(() => plugin.RemoveSpacer(spacer));

            var atomPopup = plugin.CreateScrollablePopup(_atomChooser);
            atomPopup.popup.onOpenPopupHandlers += PopulateAtomChooser;
            _uiCleanup.Add(() => plugin.RemovePopup(atomPopup));

            var partPopup = plugin.CreateScrollablePopup(_bodyPartChooser);
            _uiCleanup.Add(() => plugin.RemovePopup(partPopup));

            var axisPopup = plugin.CreateScrollablePopup(_axisChooser);
            _uiCleanup.Add(() => plugin.RemovePopup(axisPopup));

            var invToggle = plugin.CreateToggle(_invertMotion);
            invToggle.label = "Invert Motion Direction";
            _uiCleanup.Add(() => plugin.RemoveToggle(invToggle));

            var spacer2 = plugin.CreateSpacer();
            _uiCleanup.Add(() => plugin.RemoveSpacer(spacer2));

            var minAmpSlider = plugin.CreateSlider(_minAmplitude);
            minAmpSlider.label = "Minimum Stroke Amplitude (m)";
            _uiCleanup.Add(() => plugin.RemoveSlider(minAmpSlider));

            var adaptSlider = plugin.CreateSlider(_adaptationSpeed);
            adaptSlider.label = "Posture Adaptation Speed";
            _uiCleanup.Add(() => plugin.RemoveSlider(adaptSlider));

            var debug = plugin.CreateTextField(_liveDebugDisplay);
            debug.height = 60f;
            _uiCleanup.Add(() => plugin.RemoveTextField(debug));

            return DestroyUI;
        }

        private void DestroyUI()
        {
            foreach (var a in _uiCleanup) try { a(); } catch { }
            _uiCleanup.Clear();
        }
    }
}