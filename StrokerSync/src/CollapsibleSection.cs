using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StrokerSync
{
    /// <summary>
    /// Tracks a group of VaM UI elements and removes them all at once via RemoveAll().
    /// Supports nested child sections, arbitrary cleanup actions, and styled section titles.
    /// </summary>
    public class CollapsibleSection
    {
        private readonly MVRScript _plugin;

        private readonly List<CollapsibleSection> _children = new List<CollapsibleSection>();
        private readonly List<Action>             _actions  = new List<Action>();
        private readonly List<UIDynamicToggle>    _toggles  = new List<UIDynamicToggle>();
        private readonly List<UIDynamicSlider>    _sliders  = new List<UIDynamicSlider>();
        private readonly List<UIDynamicPopup>     _popups   = new List<UIDynamicPopup>();
        private readonly List<UIDynamicButton>    _buttons  = new List<UIDynamicButton>();
        private readonly List<UIDynamicTextField> _texts    = new List<UIDynamicTextField>();
        private readonly List<UIDynamic>          _spacers  = new List<UIDynamic>();

        public CollapsibleSection(MVRScript plugin)
        {
            _plugin = plugin;
        }

        // =====================================================================
        // CLEANUP
        // =====================================================================

        /// <summary>Remove all tracked elements and run all registered cleanup actions.</summary>
        public void RemoveAll()
        {
            foreach (var c in _children) c.RemoveAll();
            _children.Clear();

            foreach (var a in _actions) try { a(); } catch { }
            _actions.Clear();

            foreach (var t in _toggles)  _plugin.RemoveToggle(t);
            _toggles.Clear();

            foreach (var s in _sliders)  _plugin.RemoveSlider(s);
            _sliders.Clear();

            foreach (var p in _popups)
            {
                try { p.popup.visible = false; } catch { }
                _plugin.RemovePopup(p);
            }
            _popups.Clear();

            foreach (var b in _buttons)  _plugin.RemoveButton(b);
            _buttons.Clear();

            foreach (var tf in _texts)   _plugin.RemoveTextField(tf);
            _texts.Clear();

            foreach (var sp in _spacers) _plugin.RemoveSpacer(sp);
            _spacers.Clear();
        }

        /// <summary>Register an arbitrary cleanup action to run during RemoveAll.</summary>
        public void OnRemove(Action action) { _actions.Add(action); }

        /// <summary>Create a child section whose RemoveAll is called during this section's RemoveAll.</summary>
        public CollapsibleSection CreateChild()
        {
            var c = new CollapsibleSection(_plugin);
            _children.Add(c);
            return c;
        }

        // =====================================================================
        // ELEMENT CREATORS
        // =====================================================================

        public UIDynamicToggle CreateToggle(JSONStorableBool jsb, bool rightSide = false)
        {
            var e = _plugin.CreateToggle(jsb, rightSide);
            _toggles.Add(e);
            return e;
        }

        public UIDynamicSlider CreateSlider(JSONStorableFloat jsf, bool rightSide = false)
        {
            var e = _plugin.CreateSlider(jsf, rightSide);
            _sliders.Add(e);
            return e;
        }

        public UIDynamicPopup CreateScrollablePopup(JSONStorableStringChooser jss, bool rightSide = false)
        {
            var e = _plugin.CreateScrollablePopup(jss, rightSide);
            _popups.Add(e);
            return e;
        }

        public UIDynamicButton CreateButton(string label, bool rightSide = false)
        {
            var e = _plugin.CreateButton(label, rightSide);
            _buttons.Add(e);
            return e;
        }

        public UIDynamicTextField CreateTextField(JSONStorableString jss, bool rightSide = false)
        {
            var e = _plugin.CreateTextField(jss, rightSide);
            _texts.Add(e);
            return e;
        }

        public UIDynamic CreateSpacer(bool rightSide = false)
        {
            var e = _plugin.CreateSpacer(rightSide);
            _spacers.Add(e);
            return e;
        }

        // =====================================================================
        // SECTION TITLE
        // =====================================================================

        /// <summary>
        /// Create a styled section header — a spacer resized to 40px with a bold Text overlay.
        /// Uses the same font as VaM's configurable text field prefab.
        /// </summary>
        public UIDynamic CreateTitle(string text, bool rightSide = false)
        {
            var spacer = _plugin.CreateSpacer(rightSide);
            spacer.height = 40f;
            _spacers.Add(spacer);

            var t = spacer.gameObject.AddComponent<Text>();
            t.text      = text;
            t.fontSize  = 28;
            t.fontStyle = FontStyle.Bold;
            t.color     = new Color(0.95f, 0.9f, 0.92f);
            t.alignment = TextAnchor.MiddleLeft;

            // Copy font from VaM's own text field prefab
            try
            {
                var src = _plugin.manager.configurableTextFieldPrefab
                    .GetComponentInChildren<Text>();
                if (src != null) t.font = src.font;
            }
            catch { }

            return spacer;
        }
    }
}
