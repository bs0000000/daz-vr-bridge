// The in-VR menu: a wheel around your hand, not a page to read.
//
// Hold the menu button and a ring of eight chips appears, anchored where your hand was
// when you pressed. Move the hand toward one to highlight it, release to apply. Nothing
// is aimed at and nothing is pointed with: selection is proprioception -- "down and
// left" -- so after a few uses the gesture happens without looking, which is the whole
// point of not putting a list of sentences in front of someone who is posing.
//
//   hold menu button   the wheel, anchored at the hand
//   move               highlight, one haptic tick per chip crossed
//   push further out   on a value chip, the value follows how far out you push,
//                      with a detent every 5%; it applies live so you watch it, not read it
//   tap trigger        apply without closing (flip several toggles, or open a page)
//   release            apply and close
//   release in the middle   cancel, and put back any value you were dragging
//
// Everything is drawn with the same overlay material the bone handles use, so the wheel
// is never swallowed by the figure your hand is inside.

using System;
using TMPro;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DazVrBridge
{
    [DefaultExecutionOrder(-50)]
    public sealed class VrMenu : MonoBehaviour
    {
        public enum Face { Primary, Secondary }

        [Header("Opening")]
        public VrHand.Side menuHand = VrHand.Side.Right;
        [Tooltip("Face button held to open the wheel. A/X = Primary, B/Y = Secondary.")]
        public Face menuButton = Face.Secondary;

        [Header("Wheel geometry, centimetres at life size")]
        [Tooltip("Release inside this radius to cancel.")]
        public float deadZone = 3f;
        [Tooltip("Where the chips sit.")]
        public float ringRadius = 13f;
        [Tooltip("Push past this to start dragging a value; the value reaches its maximum at the outer radius.")]
        public float valueInner = 17f;
        public float valueOuter = 32f;

        const int Slots = 8;
        const string Prefix = "DazVrBridge.Menu.";

        enum Kind { Action, Toggle, Value, Page }

        sealed class Entry
        {
            public string Label = "";
            public Kind Kind;
            public Action Run;                  // Action and Page
            public Func<bool> Get;              // Toggle
            public Action<bool> Set;            // Toggle
            public Func<float> Read;            // Value
            public Action<float> Write;         // Value
            public float Min, Max, Display = 1f;
            public string Unit = "";
            public Func<bool> Enabled;

            public bool Live => Enabled == null || Enabled();
        }

        Entry[][] _pages;
        int _page;

        const float ChipWidth = 11f;
        const float ChipHeight = 4.2f;
        const float BarWidth = 10f;

        // Chip visuals, one set reused across pages.
        Transform _root;
        readonly Renderer[] _chip = new Renderer[Slots];
        readonly Renderer[] _fill = new Renderer[Slots];
        readonly Vector3[] _at = new Vector3[Slots];
        readonly TextMeshPro[] _label = new TextMeshPro[Slots];
        TextMeshPro _readout;
        Renderer _hub;
        MaterialPropertyBlock _block;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ZTestId = Shader.PropertyToID("_ZTestMode");

        VrRig _rig;
        BoneHandles _handles;
        SceneLoader _loader;
        EditSync _edits;
        BridgeSession _session;
        VrHand _hand;

        bool _open;
        int _slot = -1;             // highlighted slot, -1 = the dead zone
        Entry _valueEntry;          // the value chip currently under the hand, if any
        float _valueOnEntry;
        int _lastDetent = -1;
        int _shownValue = int.MinValue;

        static readonly Color ChipIdle = new Color(0.07f, 0.10f, 0.15f, 0.88f);
        static readonly Color ChipOff = new Color(0.10f, 0.13f, 0.18f, 0.88f);
        static readonly Color ChipOn = new Color(0.10f, 0.30f, 0.34f, 0.92f);
        static readonly Color ChipHot = new Color(0.16f, 0.62f, 0.72f, 0.96f);
        static readonly Color ChipDead = new Color(0.08f, 0.08f, 0.09f, 0.70f);
        static readonly Color FillColor = new Color(0.45f, 0.85f, 0.95f, 0.95f);

        void Start()
        {
            _rig = FindAnyObjectByType<VrRig>();
            _handles = FindAnyObjectByType<BoneHandles>();
            _loader = FindAnyObjectByType<SceneLoader>();
            _edits = FindAnyObjectByType<EditSync>();
            _session = FindAnyObjectByType<BridgeSession>();
            BuildPages();
            LoadSettings();
            BuildVisuals();
            Close(false);
        }

        // ---- the wheel's contents
        //
        // Eight slots per page, clockwise from straight up. The arrangement is the menu's
        // real interface -- the labels only matter until you have learned it -- so page
        // one holds what you reach for mid-pose and page two the things you set once.

        void BuildPages()
        {
            var pose = new Entry[Slots];
            pose[0] = new Entry { Label = "Undo", Kind = Kind.Action, Run = () => _edits?.Undo(), Enabled = () => _edits && _edits.CanUndo && !_edits.Pending };
            pose[1] = new Entry { Label = "Redo", Kind = Kind.Action, Run = () => _edits?.Redo(), Enabled = () => _edits && _edits.CanRedo && !_edits.Pending };
            pose[2] = new Entry { Label = "Snap", Kind = Kind.Toggle, Get = () => _handles && _handles.surfaceSnap, Set = v => _handles.surfaceSnap = v, Enabled = () => _handles };
            pose[3] = new Entry { Label = "Body", Kind = Kind.Toggle, Get = () => _handles && _handles.bodyCollisions, Set = v => _handles.bodyCollisions = v, Enabled = () => _handles };
            pose[4] = new Entry { Label = "Setup", Kind = Kind.Page, Run = () => SetPage(1) };
            pose[5] = new Entry { Label = "Clamp", Kind = Kind.Toggle, Get = () => _handles && _handles.clampToLimits, Set = v => _handles.clampToLimits = v, Enabled = () => _handles };
            pose[6] = new Entry { Label = "Rods", Kind = Kind.Toggle, Get = () => _handles && _handles.showLimitGizmo, Set = v => _handles.showLimitGizmo = v, Enabled = () => _handles };
            pose[7] = new Entry { Label = "Reload", Kind = Kind.Action, Run = () => _loader?.RequestScene(), Enabled = () => _loader && !_loader.Busy && _session && _session.ControlReady };

            var setup = new Entry[Slots];
            setup[0] = new Entry
            {
                Label = "Gap", Kind = Kind.Value, Min = 0f, Max = 0.08f, Display = 100f, Unit = "cm",
                Read = () => _handles ? _handles.snapRadius : 0f, Write = v => _handles.snapRadius = v, Enabled = () => _handles,
            };
            setup[1] = new Entry
            {
                Label = "Clavicle", Kind = Kind.Value, Min = 0f, Max = 1f, Display = 100f, Unit = "%",
                Read = () => _handles ? _handles.clavicleWeight : 0f, Write = v => _handles.clavicleWeight = v, Enabled = () => _handles,
            };
            setup[2] = new Entry
            {
                Label = "Reveal", Kind = Kind.Value, Min = 0.15f, Max = 0.60f, Display = 100f, Unit = "cm",
                Read = () => _handles ? _handles.showDistance : 0f, Write = v => _handles.showDistance = v, Enabled = () => _handles,
            };
            setup[3] = new Entry
            {
                Label = "Haptics", Kind = Kind.Value, Min = 0f, Max = 1f, Display = 100f, Unit = "%",
                Read = () => VrHand.HapticGain, Write = v => VrHand.HapticGain = v,
            };
            setup[4] = new Entry { Label = "Back", Kind = Kind.Page, Run = () => SetPage(0) };
            setup[5] = new Entry { Label = "Roll", Kind = Kind.Toggle, Get = () => _handles && _handles.rollAssist, Set = v => _handles.rollAssist = v, Enabled = () => _handles };
            // Draws the invisible surface hands actually stop against. The first thing to
            // reach for when contact feels wrong, because it turns a guess into a look.
            setup[6] = new Entry { Label = "Shapes", Kind = Kind.Toggle, Get = () => BodyCollisionRig.ShowShapes, Set = v => BodyCollisionRig.ShowShapes = v };
            setup[7] = new Entry { Label = "Life size", Kind = Kind.Action, Run = LifeSize, Enabled = () => _rig };

            _pages = new[] { pose, setup };
        }

        Entry[] Page => _pages[_page];

        void SetPage(int page)
        {
            _page = Mathf.Clamp(page, 0, _pages.Length - 1);
            Anchor();               // re-centre on the hand so the next flick starts fresh
            _slot = -1;
            _valueEntry = null;
            Draw();
        }

        void LifeSize()
        {
            var head = Head();
            if (!_rig || !head) return;
            var pivot = head.position;
            var factor = 1f / Mathf.Max(1e-4f, _rig.Scale);
            _rig.transform.position = pivot + (_rig.transform.position - pivot) * factor;
            _rig.transform.localScale = Vector3.one;
        }

        // ---- gesture

        void Update()
        {
            _hand = HandFor(menuHand);
            var held = _hand && _hand.IsTracked && (menuButton == Face.Primary ? _hand.PrimaryHeld : _hand.SecondaryHeld);
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null && kb.mKey.isPressed) held = true;
#endif
            // Opening mid-grab would fight the hand that is posing.
            if (held && !_open && !VrHand.AnyHolding) Open();
            else if (!held && _open) Close(true);
            if (!_open) return;

            Track();
            Draw();
        }

        void Open()
        {
            _open = true;
            _page = 0;
            _slot = -1;
            _valueEntry = null;
            VrHand.UiBlocked = true;
            Anchor();
            _root.gameObject.SetActive(true);
        }

        void Close(bool apply)
        {
            if (_open && apply && _slot >= 0)
            {
                Activate(Page[_slot]);
                SaveSettings();
            }
            _open = false;
            _valueEntry = null;
            _slot = -1;
            VrHand.UiBlocked = false;
            if (_root) _root.gameObject.SetActive(false);
        }

        // Where the wheel sits: at the hand, turned to face the head. Placed once per
        // opening, so moving the hand moves you *within* the wheel rather than dragging it.
        void Anchor()
        {
            var head = Head();
            if (!_root || !head) return;
            var scale = _rig ? _rig.Scale : 1f;
            // Without a tracked controller (desk testing) the wheel still appears, an
            // arm's length ahead, so its layout and size can be checked; selection needs
            // a hand and stays inert.
            _root.position = _hand && _hand.IsTracked
                ? _hand.transform.position
                : head.position + head.forward * (0.45f * scale);
            _root.rotation = Quaternion.LookRotation(_root.position - head.position, head.up);
            _root.localScale = Vector3.one * (0.01f * scale);   // one wheel unit = one centimetre
        }

        void Track()
        {
            if (!_hand || !_hand.IsTracked) return;

            // In wheel units, which the root's scale already normalises back to life size.
            var local = _root.InverseTransformPoint(_hand.transform.position);
            var offset = new Vector2(local.x, local.y);
            var reach = offset.magnitude;

            var slot = -1;
            if (reach >= deadZone)
            {
                var degrees = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                slot = Mathf.RoundToInt((90f - degrees) / 45f);
                slot = ((slot % Slots) + Slots) % Slots;
            }

            if (slot != _slot)
            {
                // Sliding off a value chip keeps what you dialled in; pulling back to the
                // middle puts it back. That is the cancel gesture, and you feel it happen.
                if (_valueEntry != null && slot < 0)
                {
                    _valueEntry.Write?.Invoke(_valueOnEntry);
                    _handles?.ApplySettings();
                }
                _slot = slot;
                _valueEntry = null;
                _lastDetent = -1;
                if (slot >= 0)
                {
                    var e = Page[slot];
                    if (e != null && e.Kind == Kind.Value && e.Read != null) { _valueEntry = e; _valueOnEntry = e.Read(); }
                    _hand.Pulse(e != null && e.Live ? 0.25f : 0.1f, 0.012f);
                }
            }

            if (_slot >= 0)
            {
                var e = Page[_slot];
                if (e != null && e.Kind == Kind.Value && e.Live && reach >= valueInner)
                {
                    var t = Mathf.Clamp01(Mathf.InverseLerp(valueInner, valueOuter, reach));
                    var detent = Mathf.RoundToInt(t * 20f);
                    if (detent != _lastDetent) { _lastDetent = detent; _hand.Pulse(0.2f, 0.01f); }
                    e.Write?.Invoke(Mathf.Lerp(e.Min, e.Max, detent / 20f));
                    _handles?.ApplySettings();
                }

                // A tap of the trigger applies without closing: flip several toggles, or
                // step into the settings page, all inside one hold.
                if (_hand.TriggerPressed && e != null && e.Live && (e.Kind == Kind.Toggle || e.Kind == Kind.Page))
                {
                    Activate(e);
                    _hand.Pulse(0.45f, 0.03f);
                }
            }
        }

        void Activate(Entry e)
        {
            if (e == null || !e.Live) return;
            switch (e.Kind)
            {
                case Kind.Toggle:
                    e.Set(!e.Get());
                    _handles?.ApplySettings();
                    SaveSettings();
                    break;
                case Kind.Value:
                    SaveSettings();
                    break;
                default:
                    e.Run?.Invoke();
                    break;
            }
        }

        // ---- drawing

        void Draw()
        {
            var page = Page;
            for (var i = 0; i < Slots; i++)
            {
                var e = page[i];
                var hot = i == _slot;
                var live = e != null && e.Live;

                Color colour;
                if (!live) colour = ChipDead;
                else if (hot) colour = ChipHot;
                else if (e.Kind == Kind.Toggle) colour = e.Get() ? ChipOn : ChipOff;
                else colour = ChipIdle;
                Tint(_chip[i], colour);

                var fill = 0f;
                if (e != null && e.Kind == Kind.Value && e.Read != null && e.Max > e.Min)
                    fill = Mathf.Clamp01(Mathf.InverseLerp(e.Min, e.Max, e.Read()));
                var bar = _fill[i].transform;
                bar.gameObject.SetActive(fill > 0.001f);
                // Grows from the chip's left edge rather than its centre.
                bar.localScale = new Vector3(BarWidth * fill, 0.5f, 1f);
                bar.localPosition = _at[i] + new Vector3(BarWidth * (fill - 1f) * 0.5f, -1.4f, -0.05f);

                var caption = e == null ? "" : e.Label;
                if (_label[i].text != caption) _label[i].text = caption;
                _label[i].color = live ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            }

            Tint(_hub, _slot < 0 ? ChipHot : ChipDead);

            // The one number worth showing, and only while you are actually setting it.
            var sel = _slot >= 0 ? page[_slot] : null;
            if (sel != null && sel.Kind == Kind.Value && sel.Read != null)
            {
                _readout.gameObject.SetActive(true);
                // Only rebuilt when the number actually changes: this runs every frame the
                // wheel is open, and string building per frame is what the client spent two
                // rounds getting rid of.
                var shown = Mathf.RoundToInt(sel.Read() * sel.Display);
                if (shown != _shownValue) { _shownValue = shown; _readout.text = shown + sel.Unit; }
            }
            else { _readout.gameObject.SetActive(false); _shownValue = int.MinValue; }
        }

        void Tint(Renderer r, Color c)
        {
            if (!r) return;
            _block.SetColor(BaseColorId, c);
            r.SetPropertyBlock(_block);
        }

        void BuildVisuals()
        {
            _block = new MaterialPropertyBlock();
            var root = new GameObject("VR wheel");
            _root = root.transform;

            var material = BoneHandle.OverlayMaterial();

            _hub = Quad(_root, "hub", new Vector3(0f, 0f, 0.05f), new Vector3(deadZone * 1.4f, deadZone * 1.4f, 1f), material);

            for (var i = 0; i < Slots; i++)
            {
                var degrees = 90f - i * 45f;
                var at = new Vector3(Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad), 0f) * ringRadius;

                _at[i] = at;
                _chip[i] = Quad(_root, $"chip{i}", at, new Vector3(ChipWidth, ChipHeight, 1f), material);
                // A sibling of the chip, not a child: the chip's own scale is the quad's
                // size, so a child would inherit it and the bar's width would mean nothing.
                _fill[i] = Quad(_root, $"fill{i}", at, new Vector3(BarWidth, 0.5f, 1f), material);
                Tint(_fill[i], FillColor);

                _label[i] = Text(_root, $"label{i}", at + new Vector3(0f, 0.45f, -0.05f), ChipWidth - 1f, ChipHeight - 1.6f);
            }

            _readout = Text(_root, "readout", new Vector3(0f, 0f, -0.05f), deadZone * 2.6f, deadZone * 1.4f);
            _readout.fontStyle = FontStyles.Bold;
        }

        Renderer Quad(Transform parent, string name, Vector3 at, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        // TextMeshPro rather than the legacy bitmap font: this is signed-distance-field
        // text, which is what keeps a 2 cm label readable a hand's length from the eye.
        // Auto-sizing fits the word to its chip, so no label can ever overrun one.
        TextMeshPro Text(Transform parent, string name, Vector3 at, float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = new Vector2(width, height);
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.enableAutoSizing = true;
            // TextMeshPro's font size is not in the same units as the RectTransform, so a
            // hand-picked maximum is a guess -- and the first guess here clamped every
            // label to something unreadable. Give auto-sizing a range wide enough that it
            // is never the binding constraint: it then simply fills the chip, whatever the
            // unit relationship turns out to be.
            t.fontSizeMin = 0.1f;
            t.fontSizeMax = 300f;
            t.color = Color.white;
            // Drawn over the figure, like everything else on the wheel. fontMaterial (not
            // sharedMaterial) gives this label its own instance, so the change cannot leak
            // into every other piece of TMP text in the scene.
            var font = t.fontMaterial;
            if (font.HasProperty(ZTestId)) font.SetFloat(ZTestId, (float)UnityEngine.Rendering.CompareFunction.Always);
            font.renderQueue = 4000;
            return t;
        }

        Transform Head()
        {
            var cam = Camera.main;
            return cam ? cam.transform : null;
        }

        VrHand HandFor(VrHand.Side side)
        {
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();
            if (!_rig) return null;
            return side == VrHand.Side.Left ? _rig.Left : _rig.Right;
        }

        // ---- persistence

        void LoadSettings()
        {
            if (_handles)
            {
                _handles.surfaceSnap = Read("snap", _handles.surfaceSnap);
                _handles.bodyCollisions = Read("body", _handles.bodyCollisions);
                _handles.clampToLimits = Read("clamp", _handles.clampToLimits);
                _handles.rollAssist = Read("roll", _handles.rollAssist);
                _handles.showLimitGizmo = Read("rods", _handles.showLimitGizmo);
                _handles.showLimitText = Read("text", _handles.showLimitText);
                _handles.showDistance = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "reveal", _handles.showDistance), 0.15f, 0.6f);
                _handles.snapRadius = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "gap", _handles.snapRadius), 0f, 0.08f);
                _handles.clavicleWeight = Mathf.Clamp01(PlayerPrefs.GetFloat(Prefix + "clavicle", _handles.clavicleWeight));
                _handles.ApplySettings();
            }
            VrHand.HapticGain = Mathf.Clamp01(PlayerPrefs.GetFloat(Prefix + "haptics", 1f));
        }

        static bool Read(string key, bool fallback) => PlayerPrefs.GetInt(Prefix + key, fallback ? 1 : 0) != 0;

        void SaveSettings()
        {
            if (_handles)
            {
                PlayerPrefs.SetInt(Prefix + "snap", _handles.surfaceSnap ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "body", _handles.bodyCollisions ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "clamp", _handles.clampToLimits ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "roll", _handles.rollAssist ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "rods", _handles.showLimitGizmo ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "text", _handles.showLimitText ? 1 : 0);
                PlayerPrefs.SetFloat(Prefix + "reveal", _handles.showDistance);
                PlayerPrefs.SetFloat(Prefix + "gap", _handles.snapRadius);
                PlayerPrefs.SetFloat(Prefix + "clavicle", _handles.clavicleWeight);
            }
            PlayerPrefs.SetFloat(Prefix + "haptics", VrHand.HapticGain);
            PlayerPrefs.Save();
        }

        void OnDisable() { if (_open) Close(false); else VrHand.UiBlocked = false; }

        void OnDestroy()
        {
            VrHand.UiBlocked = false;
            if (_root) Destroy(_root.gameObject);
        }
    }
}
