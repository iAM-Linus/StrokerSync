using System;
using UnityEngine;
using UnityEngine.UI;

namespace StrokerSync
{
    /// <summary>
    /// Lightweight world-space overlay panel for quick access to the most
    /// commonly adjusted controls while a scene is playing.
    /// No external dependencies — pure Unity UI with built-in font.
    /// Parented to Camera.main using the same FOV-compensated depth formula
    /// as DockedUI so it sits correctly at any monitor FOV.
    /// </summary>
    internal class OverlayPanel
    {
        private readonly MVRScript _plugin;
        private readonly Action _onReDetect;

        private GameObject _root;
        private Canvas _canvas;
        private Font _font;

        // Pen Min / Pen Max sliders + their value readout texts
        private Slider _penMinSlider;
        private Text   _penMinValText;
        private Slider _penMaxSlider;
        private Text   _penMaxValText;

        // Send Rate / Duration Pad stepper readouts
        private Text _sendRateText;
        private Text _durationPadText;

        // Rolling calibration window + rate sliders + readouts
        private Slider _rollingWindowSlider;
        private Text   _rollingWindowValText;
        private Slider _rollingContractSlider;
        private Text   _rollingContractValText;

        // Toggle buttons (image + label ref for live colour/text refresh)
        private Image _rollingCalBtnImage;
        private Text  _rollingCalBtnText;
        private Image _fullStrokeBtnImage;
        private Text  _fullStrokeBtnText;

        // Cached storable references
        private JSONStorableFloat _penMin;
        private JSONStorableFloat _penMax;
        private JSONStorableFloat _sendRate;
        private JSONStorableFloat _durationPad;
        private JSONStorableFloat _rollingWindowSecs;
        private JSONStorableFloat _rollingContractRate;
        private JSONStorableBool  _rollingCal;
        private JSONStorableBool  _fullStrokeMode;

        private float _refreshTimer;
        private bool  _refreshing;           // true while RefreshDisplays syncs sliders
        private const float REFRESH_INTERVAL = 0.1f; // 10 Hz

        // Panel pixel dimensions (scale 0.001 → 1 pixel ≈ 1 mm in world space)
        private const float W   = 292f;
        private const float H   = 402f;
        private const float ROW = 36f;
        private const float PAD = 5f;

        public bool IsVisible => _root != null && _root.activeSelf;

        public OverlayPanel(MVRScript plugin, Action onReDetect)
        {
            _plugin     = plugin;
            _onReDetect = onReDetect;
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        public void Build()
        {
            _penMin              = _plugin.GetFloatJSONParam("maleFemale_PenRangeMin");
            _penMax              = _plugin.GetFloatJSONParam("maleFemale_PenRangeMax");
            _sendRate            = _plugin.GetFloatJSONParam("sendRateHz");
            _durationPad         = _plugin.GetFloatJSONParam("deviceSmoothnessMs");
            _rollingCal          = _plugin.GetBoolJSONParam("maleFemale_RollingCal");
            _fullStrokeMode      = _plugin.GetBoolJSONParam("maleFemale_FullStrokeMode");
            _rollingWindowSecs   = _plugin.GetFloatJSONParam("maleFemale_RollingWindowSecs");
            _rollingContractRate = _plugin.GetFloatJSONParam("maleFemale_RollingContractRate");

            _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            BuildCanvas();
            RefreshDisplays();
        }

        public void Destroy()
        {
            if (_canvas != null)
            {
                SuperController.singleton.RemoveCanvas(_canvas);
                _canvas = null;
            }
            if (_root != null)
            {
                GameObject.Destroy(_root);
                _root = null;
            }
        }

        public void SetVisible(bool visible)
        {
            if (_root != null)
                _root.SetActive(visible);
        }

        /// <summary>Call from StrokerSync.Update() each frame.</summary>
        public void Update()
        {
            if (!IsVisible) return;
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer > 0f) return;
            _refreshTimer = REFRESH_INTERVAL;
            RefreshDisplays();
        }

        // =====================================================================
        // Canvas construction
        // =====================================================================

        private void BuildCanvas()
        {
            _root = new GameObject("StrokerSyncOverlay");
            _root.transform.SetParent(_plugin.transform, false);

            _canvas = _root.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.WorldSpace;
            _canvas.pixelPerfect = false;
            SuperController.singleton.AddCanvas(_canvas);

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.scaleFactor          = 80f;
            scaler.dynamicPixelsPerUnit = 1f;
            _root.AddComponent<GraphicRaycaster>();

            if (Camera.main != null)
                _canvas.worldCamera = Camera.main;

            float s = 0.001f * SuperController.singleton.worldScale;
            _root.transform.localScale = new Vector3(s, s, s);

            var canvasRT = _root.GetComponent<RectTransform>();
            canvasRT.sizeDelta = new Vector2(W, H);

            float fov = SuperController.singleton.monitorCameraFOV;
            float z   = 2f / ((fov / 40f - 1f) * 1.2f + 1f);

            if (Camera.main != null)
            {
                _root.transform.SetParent(Camera.main.transform, false);
                _root.transform.localPosition    = new Vector3(1.08f, 0.5f, z);
                _root.transform.localEulerAngles = Vector3.zero;
            }

            var bg = MakePanel(_root.transform, "BG", W, H, 0f, 0f,
                               new Color(0.07f, 0.07f, 0.07f, 0.94f));

            float y = H / 2f - 18f;

            // ----- Title bar -----
            MakeLabel(bg, "Title", "StrokerSync  Quick Controls",
                      W - 44f, 28f, -(W / 2f - (W - 44f) / 2f - 5f), y,
                      13, TextAnchor.MiddleLeft, new Color(0.58f, 0.82f, 1f));
            MakeButton(bg, "Close", "✕", 28f, 28f, W / 2f - 18f, y,
                       new Color(0.45f, 0.12f, 0.12f, 1f), () => SetVisible(false));
            y -= ROW - 2f;

            MakeDivider(bg, y + PAD); y -= PAD;

            // ----- Re-detect toy button -----
            MakeButton(bg, "ReDetect", "Re-detect Toy Tip & Length",
                       W - 16f, 30f, 0f, y,
                       new Color(0.12f, 0.30f, 0.55f, 1f),
                       () => _onReDetect?.Invoke());
            y -= ROW;

            MakeDivider(bg, y + PAD); y -= PAD;

            // ----- Pen Min / Max sliders -----
            Slider s1; Text t1;
            MakeSliderRow(bg, "Pen Min", _penMin, "F2", y, out s1, out t1);
            _penMinSlider = s1; _penMinValText = t1;
            y -= ROW;

            Slider s2; Text t2;
            MakeSliderRow(bg, "Pen Max", _penMax, "F2", y, out s2, out t2);
            _penMaxSlider = s2; _penMaxValText = t2;
            y -= ROW;

            // ----- Send Rate / Duration Pad steppers -----
            _sendRateText    = MakeControlRow(bg, "Send Rate", _sendRate,    1f, "F0", y); y -= ROW;
            _durationPadText = MakeControlRow(bg, "Dur. Pad",  _durationPad, 5f, "F0", y); y -= ROW;

            MakeDivider(bg, y + PAD); y -= PAD;

            // ----- Rolling Calibration toggles -----
            MakeToggleRow(bg, "Rolling Cal",  _rollingCal,     y,
                          out _rollingCalBtnImage,  out _rollingCalBtnText);  y -= ROW;

            MakeToggleRow(bg, "Full Stroke",  _fullStrokeMode, y,
                          out _fullStrokeBtnImage,  out _fullStrokeBtnText);  y -= ROW;

            // ----- Cal Window / Cal Rate sliders -----
            Slider s3; Text t3;
            MakeSliderRow(bg, "Cal Window", _rollingWindowSecs,   "F0", y, out s3, out t3);
            _rollingWindowSlider = s3; _rollingWindowValText = t3;
            y -= ROW;

            Slider s4; Text t4;
            MakeSliderRow(bg, "Cal Rate",   _rollingContractRate, "F2", y, out s4, out t4);
            _rollingContractSlider = s4; _rollingContractValText = t4;
        }

        // =====================================================================
        // Row builders
        // =====================================================================

        /// <summary>
        /// Label + slider + value-readout row.
        /// Returns Slider and Text via out parameters.
        /// </summary>
        private void MakeSliderRow(GameObject bg, string label,
                                    JSONStorableFloat storable, string fmt, float y,
                                    out Slider sliderOut, out Text valTextOut)
        {
            const float LW = 80f, VW = 38f, RH = 28f;
            float sliderW = W - 16f - LW - 8f - VW - 4f;
            float lx      = -W / 2f + 8f + LW / 2f;
            float sliderX = lx + LW / 2f + 8f + sliderW / 2f;
            float valX    = sliderX + sliderW / 2f + 4f + VW / 2f;

            MakeLabel(bg, label + "L", label, LW, RH, lx, y);

            var valText = MakeLabel(bg, label + "V", "—", VW, RH, valX, y,
                                    12, TextAnchor.MiddleRight);
            valTextOut = valText;

            var sliderGO = new GameObject(label + "Slider");
            sliderGO.transform.SetParent(bg.transform, false);
            var sliderRT = sliderGO.AddComponent<RectTransform>();
            sliderRT.anchorMin        = sliderRT.anchorMax = new Vector2(0.5f, 0.5f);
            sliderRT.sizeDelta        = new Vector2(sliderW, RH);
            sliderRT.anchoredPosition = new Vector2(sliderX, y);

            // Track
            var trackGO = new GameObject("Track");
            trackGO.transform.SetParent(sliderGO.transform, false);
            trackGO.AddComponent<Image>().color = new Color(0.18f, 0.18f, 0.18f, 1f);
            var trackRT = trackGO.GetComponent<RectTransform>();
            trackRT.anchorMin = new Vector2(0f, 0.3f);
            trackRT.anchorMax = new Vector2(1f, 0.7f);
            trackRT.offsetMin = trackRT.offsetMax = Vector2.zero;

            // Fill area
            var fillAreaGO = new GameObject("Fill Area");
            fillAreaGO.transform.SetParent(sliderGO.transform, false);
            var fillAreaRT = fillAreaGO.AddComponent<RectTransform>();
            fillAreaRT.anchorMin = new Vector2(0f, 0.3f);
            fillAreaRT.anchorMax = new Vector2(1f, 0.7f);
            fillAreaRT.offsetMin = new Vector2(5f, 0f);
            fillAreaRT.offsetMax = new Vector2(-5f, 0f);

            var fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            fillGO.AddComponent<Image>().color = new Color(0.24f, 0.52f, 0.82f, 1f);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = new Vector2(0f, 1f);
            fillRT.offsetMin = fillRT.offsetMax = Vector2.zero;

            // Handle
            var handleAreaGO = new GameObject("Handle Slide Area");
            handleAreaGO.transform.SetParent(sliderGO.transform, false);
            var handleAreaRT = handleAreaGO.AddComponent<RectTransform>();
            handleAreaRT.anchorMin = Vector2.zero;
            handleAreaRT.anchorMax = Vector2.one;
            handleAreaRT.offsetMin = new Vector2(8f, 0f);
            handleAreaRT.offsetMax = new Vector2(-8f, 0f);

            var handleGO = new GameObject("Handle");
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var handleImg = handleGO.AddComponent<Image>();
            handleImg.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            handleGO.GetComponent<RectTransform>().sizeDelta = new Vector2(14f, RH - 4f);

            var slider = sliderGO.AddComponent<Slider>();
            slider.fillRect      = fillRT;
            slider.handleRect    = handleGO.GetComponent<RectTransform>();
            slider.targetGraphic = handleImg;
            slider.direction     = Slider.Direction.LeftToRight;
            slider.minValue      = storable?.min ?? 0f;
            slider.maxValue      = storable?.max ?? 1f;
            slider.value         = storable?.val ?? 0f;

            valText.text = (storable?.val ?? 0f).ToString(fmt);

            string capturedFmt = fmt;
            slider.onValueChanged.AddListener(v =>
            {
                if (_refreshing) return;
                if (storable != null) storable.val = v;
                valText.text = v.ToString(capturedFmt);
            });

            sliderOut = slider;
        }

        /// <summary>Label + ◄ value ► row. Returns the value Text.</summary>
        private Text MakeControlRow(GameObject bg, string label,
                                     JSONStorableFloat storable, float step,
                                     string fmt, float y)
        {
            const float LW = 104f, BW = 28f, VW = 56f, RH = 28f;
            float lx   = -W / 2f + 8f + LW / 2f;
            float decX = lx + LW / 2f + 4f + BW / 2f;
            float valX = decX + BW / 2f + 2f + VW / 2f;
            float incX = valX + VW / 2f + 2f + BW / 2f;

            MakeLabel(bg, label + "L", label, LW, RH, lx, y);

            MakeButton(bg, label + "-", "◄", BW, RH, decX, y,
                       new Color(0.20f, 0.20f, 0.20f, 1f), () =>
                       {
                           if (storable != null)
                               storable.val = Mathf.Clamp(storable.val - step, storable.min, storable.max);
                       });

            var valText = MakeLabel(bg, label + "V", "—", VW, RH, valX, y,
                                    13, TextAnchor.MiddleCenter);

            MakeButton(bg, label + "+", "►", BW, RH, incX, y,
                       new Color(0.20f, 0.20f, 0.20f, 1f), () =>
                       {
                           if (storable != null)
                               storable.val = Mathf.Clamp(storable.val + step, storable.min, storable.max);
                       });

            return valText;
        }

        /// <summary>
        /// Label + ON/OFF toggle button row.
        /// Returns the button Image and Text so RefreshDisplays can sync them.
        /// </summary>
        private void MakeToggleRow(GameObject bg, string label,
                                    JSONStorableBool storable, float y,
                                    out Image outBtnImage, out Text outBtnText)
        {
            const float LW = 130f, BW = 80f, RH = 28f;
            float lx = -W / 2f + 8f + LW / 2f;
            float bx = lx + LW / 2f + 8f + BW / 2f;

            MakeLabel(bg, label + "L", label, LW, RH, lx, y);

            bool initOn  = storable?.val ?? false;
            Color initCol = initOn ? new Color(0.15f, 0.55f, 0.20f, 1f)
                                   : new Color(0.45f, 0.15f, 0.15f, 1f);

            var btnGO = MakeButtonGO(bg, label + "B", initOn ? "ON" : "OFF",
                                     BW, RH, bx, y, initCol);

            outBtnImage = btnGO.GetComponent<Image>();
            outBtnText  = btnGO.GetComponentInChildren<Text>();

            var btn = btnGO.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.AddListener(() =>
                {
                    if (storable == null) return;
                    storable.val = !storable.val;
                    RefreshDisplays();
                });
            }
        }

        // =====================================================================
        // Display refresh
        // =====================================================================

        private void RefreshDisplays()
        {
            // Sliders: guard with _refreshing so value assignment doesn't
            // write back to the storable (Unity 5.6 has no SetValueWithoutNotify).
            _refreshing = true;

            if (_penMinSlider != null && _penMin != null)
            {
                _penMinSlider.value = _penMin.val;
                if (_penMinValText != null) _penMinValText.text = _penMin.val.ToString("F2");
            }
            if (_penMaxSlider != null && _penMax != null)
            {
                _penMaxSlider.value = _penMax.val;
                if (_penMaxValText != null) _penMaxValText.text = _penMax.val.ToString("F2");
            }
            if (_rollingWindowSlider != null && _rollingWindowSecs != null)
            {
                _rollingWindowSlider.value = _rollingWindowSecs.val;
                if (_rollingWindowValText != null)
                    _rollingWindowValText.text = _rollingWindowSecs.val.ToString("F0");
            }
            if (_rollingContractSlider != null && _rollingContractRate != null)
            {
                _rollingContractSlider.value = _rollingContractRate.val;
                if (_rollingContractValText != null)
                    _rollingContractValText.text = _rollingContractRate.val.ToString("F2");
            }

            _refreshing = false;

            // Steppers
            if (_sendRateText != null && _sendRate != null)
                _sendRateText.text = _sendRate.val.ToString("F0");
            if (_durationPadText != null && _durationPad != null)
                _durationPadText.text = _durationPad.val.ToString("F0");

            // Toggle buttons
            RefreshToggle(_rollingCalBtnImage,  _rollingCalBtnText,  _rollingCal);
            RefreshToggle(_fullStrokeBtnImage,  _fullStrokeBtnText,  _fullStrokeMode);
        }

        private void RefreshToggle(Image btnImage, Text btnText, JSONStorableBool storable)
        {
            if (storable == null || btnImage == null || btnText == null) return;
            bool on = storable.val;
            btnImage.color = on ? new Color(0.15f, 0.55f, 0.20f, 1f)
                                : new Color(0.45f, 0.15f, 0.15f, 1f);
            btnText.text = on ? "ON" : "OFF";
        }

        // =====================================================================
        // Primitive UI helpers
        // =====================================================================

        private GameObject MakePanel(Transform parent, string name,
                                      float w, float h, float x, float y, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>().color = c;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
            return go;
        }

        private Text MakeLabel(GameObject parent, string name, string text,
                                float w, float h, float x, float y,
                                int fontSize = 13,
                                TextAnchor anchor = TextAnchor.MiddleLeft,
                                Color? col = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var txt = go.AddComponent<Text>();
            txt.text      = text;
            txt.font      = _font;
            txt.fontSize  = fontSize;
            txt.color     = col ?? Color.white;
            txt.alignment = anchor;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, y);
            return txt;
        }

        private GameObject MakeButtonGO(GameObject parent, string name, string label,
                                         float w, float h, float x, float y,
                                         Color bgColor, Action onClick = null)
        {
            var go = MakePanel(parent.transform, name, w, h, x, y, bgColor);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            var cb = btn.colors;
            cb.normalColor      = bgColor;
            cb.highlightedColor = new Color(Mathf.Min(bgColor.r * 1.25f, 1f),
                                            Mathf.Min(bgColor.g * 1.25f, 1f),
                                            Mathf.Min(bgColor.b * 1.25f, 1f), 1f);
            cb.pressedColor     = new Color(bgColor.r * 0.75f,
                                            bgColor.g * 0.75f,
                                            bgColor.b * 0.75f, 1f);
            btn.colors = cb;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            var tgo = new GameObject("T");
            tgo.transform.SetParent(go.transform, false);
            var txt = tgo.AddComponent<Text>();
            txt.text      = label;
            txt.font      = _font;
            txt.fontSize  = 12;
            txt.color     = Color.white;
            txt.alignment = TextAnchor.MiddleCenter;
            var trt = tgo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(3f, 2f);
            trt.offsetMax = new Vector2(-3f, -2f);
            return go;
        }

        private void MakeButton(GameObject parent, string name, string label,
                                 float w, float h, float x, float y,
                                 Color bgColor, Action onClick)
        {
            MakeButtonGO(parent, name, label, w, h, x, y, bgColor, onClick);
        }

        private void MakeDivider(GameObject bg, float y)
        {
            MakePanel(bg.transform, "Div" + y.GetHashCode(),
                      W - 14f, 1f, 0f, y, new Color(0.28f, 0.28f, 0.28f, 1f));
        }
    }
}
