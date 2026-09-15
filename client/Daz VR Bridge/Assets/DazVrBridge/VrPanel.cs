// A board you point at: named rows at arm's length, for everything the wheel is bad at.
//
// The wheel is proprioception -- eight fixed directions, learned once, used without
// looking -- and it is right for what you reach for mid-pose. It is wrong for anything
// that has to be READ, and the last round proved that twice: a page of settings nobody
// could find, and a render you had to take while holding the camera steady in one hand
// and flicking a wheel with the other. An object's name, a texture size, what this
// button is about to delete: those want words in rows, and rows want a pointer.
//
// So: a board in the air, a ray from the controller's AIM pose (not its grip pose --
// the handle points down and to the left of where you think you are pointing), and the
// trigger to click. It hangs from its top edge, so switching tab never moves the title
// under your cursor, and it comes back in front of you if you turn away or walk off,
// because a panel you have to go and look for is worse than no panel at all.
//
// This file knows nothing about Daz. It draws rows and reports clicks; PanelMenu says
// what the rows are.

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace DazVrBridge
{
    // Ahead of VrHand (0), so a hand under the cursor has already been told that its
    // trigger belongs to the panel this frame, before it looks for something to grab.
    [DefaultExecutionOrder(-45)]
    public sealed class VrPanel : MonoBehaviour
    {
        public enum Kind { Button, Toggle, Slider, Choice, Note }

        public sealed class Row
        {
            public string Label = "";
            public Kind Kind;
            public Action Run;                      // Button
            public Func<bool> Get; public Action<bool> Set;          // Toggle
            public Func<float> Read; public Action<float> Write;     // Slider
            public Action Done;                     // Slider: called when the drag ends
            public float Min, Max, Display = 1f;
            public string Unit = "";
            public string[] Choices; public Func<int> Index; public Action<int> Pick;
            public Func<string> Value;              // Note, or an override readout
            public Func<bool> Enabled;
            public bool Danger;                     // delete and friends, tinted red
            public bool Live => Enabled == null || Enabled();
        }

        public sealed class Tab
        {
            public string Label = "";
            public Func<List<Row>> Build;
        }

        public bool IsOpen { get; private set; }
        /// What the panel is about, so the owner can tell whether to reopen or close.
        public string Subject { get; private set; } = "";

        // Panel units are centimetres at life size, the same convention as the wheel.
        const float W = 30f;
        const float Pad = 1.2f;
        const float TitleH = 5.2f;
        const float TabH = 4.4f;
        const float RowH = 3.6f;
        const float RowStep = 4.0f;
        const int MaxRows = 9;
        const int MaxTabs = 4;
        const float TrackRight = W * 0.5f - 1.4f;
        const float TrackLeft = -W * 0.5f + 14.6f;

        static readonly Color Back = new Color(0.05f, 0.07f, 0.10f, 0.94f);
        static readonly Color RowIdle = new Color(0.10f, 0.13f, 0.18f, 0.92f);
        static readonly Color RowOn = new Color(0.10f, 0.30f, 0.34f, 0.94f);
        static readonly Color RowHot = new Color(0.16f, 0.62f, 0.72f, 0.96f);
        static readonly Color RowDead = new Color(0.08f, 0.09f, 0.11f, 0.80f);
        static readonly Color RowRed = new Color(0.45f, 0.13f, 0.14f, 0.94f);
        static readonly Color TabOn = new Color(0.16f, 0.45f, 0.52f, 0.95f);
        static readonly Color Fill = new Color(0.45f, 0.85f, 0.95f, 0.95f);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        readonly List<Tab> _tabs = new List<Tab>();
        readonly List<Row> _rows = new List<Row>();
        int _tab;

        Transform _root;
        Renderer _back, _cursor;
        TextMeshPro _title;
        readonly Renderer[] _tabChip = new Renderer[MaxTabs];
        readonly TextMeshPro[] _tabText = new TextMeshPro[MaxTabs];
        readonly Renderer[] _rowChip = new Renderer[MaxRows];
        readonly Renderer[] _rowFill = new Renderer[MaxRows];
        readonly TextMeshPro[] _rowLabel = new TextMeshPro[MaxRows];
        readonly TextMeshPro[] _rowValue = new TextMeshPro[MaxRows];
        readonly string[] _shownValue = new string[MaxRows];
        LineRenderer _beam;
        MaterialPropertyBlock _block;

        VrRig _rig;
        VrHand _pointer;
        VrHand.Side _side = VrHand.Side.Right;
        int _hover = -1;                // -1 nothing, 0..n rows, -2..-5 tabs
        Row _drag;
        int _lastDetent = -1;
        float _height = 20f;
        float _settle;                  // seconds left of the glide to a new anchor
        Vector3 _wantPos;
        Quaternion _wantRot;

        void Awake()
        {
            _block = new MaterialPropertyBlock();
            _rig = FindAnyObjectByType<VrRig>();
            BuildVisuals();
            _root.gameObject.SetActive(false);
        }

        // ---- opening

        /// Shows `tabs` titled `title`. `lookAt` is what the panel is about -- the panel
        /// is placed between you and it, off to the side of the hand that asked, so the
        /// thing you are about to change stays in view.
        public void Open(string subject, string title, List<Tab> tabs, Vector3 lookAt, VrHand.Side side)
        {
            _tabs.Clear();
            _tabs.AddRange(tabs);
            Subject = subject;
            _side = side;
            _tab = 0;
            _hover = -1;
            _drag = null;
            IsOpen = true;
            _title.text = title;
            _root.gameObject.SetActive(true);
            Rebuild();
            Anchor(lookAt, true);
        }

        public void Close()
        {
            IsOpen = false;
            _drag = null;
            _hover = -1;
            Subject = "";
            Unblock();
            if (_root) _root.gameObject.SetActive(false);
        }

        /// Re-asks the current tab for its rows. Cheap, and the only way a panel whose
        /// contents depend on state (a camera gains a Render row) stays honest.
        public void Rebuild()
        {
            _rows.Clear();
            if (_tab >= 0 && _tab < _tabs.Count && _tabs[_tab].Build != null)
            {
                var built = _tabs[_tab].Build();
                if (built != null)
                    for (var i = 0; i < built.Count && i < MaxRows; i++) _rows.Add(built[i]);
            }
            _height = Pad + TitleH + (_tabs.Count > 1 ? TabH : 0f) + 0.6f + _rows.Count * RowStep + Pad;
            Layout();
            for (var i = 0; i < MaxRows; i++) _shownValue[i] = null;
        }

        // ---- where it sits

        void Anchor(Vector3 lookAt, bool immediate)
        {
            var head = Camera.main;
            if (!head) return;
            var scale = _rig ? _rig.Scale : 1f;

            var flat = lookAt - head.transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
            // Off to the side of the hand that opened it, so the subject stays visible
            // past the edge of the board rather than behind it.
            var turn = _side == VrHand.Side.Right ? 20f : -20f;
            var dir = (Quaternion.AngleAxis(turn, Vector3.up) * flat).normalized;

            _wantPos = head.transform.position + dir * (0.62f * scale) + Vector3.up * (0.14f * scale);
            // Levelled with world up, never the head's roll: this one is read, not felt.
            _wantRot = Quaternion.LookRotation(dir, Vector3.up);
            if (immediate)
            {
                _root.SetPositionAndRotation(_wantPos, _wantRot);
                _settle = 0f;
            }
            else _settle = 0.25f;
            _root.localScale = Vector3.one * (0.01f * scale);
        }

        // Turn away or walk off and the panel comes to you, once, smoothly. Anything
        // that tracked the head continuously would be unreadable to point at.
        void Follow()
        {
            var head = Camera.main;
            if (!head) return;
            var scale = _rig ? _rig.Scale : 1f;
            var toPanel = _root.position - head.transform.position;
            var far = toPanel.magnitude > 1.6f * scale || toPanel.magnitude < 0.25f * scale;
            var behind = Vector3.Angle(head.transform.forward, toPanel) > 65f;
            if ((far || behind) && _settle <= 0f && _drag == null)
                Anchor(head.transform.position + head.transform.forward * (0.62f * scale), false);

            if (_settle > 0f)
            {
                _settle -= Time.deltaTime;
                var t = 1f - Mathf.Clamp01(_settle / 0.25f);
                var k = t * t * (3f - 2f * t);
                _root.SetPositionAndRotation(
                    Vector3.Lerp(_root.position, _wantPos, k),
                    Quaternion.Slerp(_root.rotation, _wantRot, k));
                _root.localScale = Vector3.one * (0.01f * scale);
            }
        }

        // ---- pointing

        void Update()
        {
            if (!IsOpen) return;
            Follow();
            Point();
            Draw();
        }

        void Point()
        {
            // The wheel owns both hands while it is open; two pointers fighting over one
            // trigger is how you delete something you were only looking at.
            if (VrHand.UiBlocked) { Unblock(); _hover = -1; ShowBeam(false); return; }

            var hand = Choose(out var local);
            if (hand == null)
            {
                Unblock();
                if (_hover != -1) { _hover = -1; }
                _drag = null;
                ShowBeam(false);
                return;
            }

            _pointer = hand;
            hand.PointerBlocked = true;
            var other = Other(hand);
            if (other) other.PointerBlocked = false;

            var target = Which(local);
            if (target != _hover)
            {
                _hover = target;
                if (target != -1) hand.Pulse(Live(target) ? 0.22f : 0.08f, 0.012f);
            }

            if (_drag != null)
            {
                if (!hand.TriggerHeld) { EndDrag(); }
                else Slide(local.x);
            }
            else if (hand.TriggerPressed && _hover != -1)
            {
                Activate(_hover, local.x, hand);
            }

            ShowBeam(true, hand, local);
        }

        // The hand doing the pointing: whichever is mid-drag, else whichever hits the
        // board, preferring the one that opened it.
        VrHand Choose(out Vector2 local)
        {
            local = Vector2.zero;
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();
            if (!_rig) return null;
            var first = _side == VrHand.Side.Right ? _rig.Right : _rig.Left;
            var second = _side == VrHand.Side.Right ? _rig.Left : _rig.Right;

            if (_drag != null && _pointer && Hit(_pointer, out local)) return _pointer;
            if (Hit(first, out local)) return first;
            if (Hit(second, out local)) return second;
            return null;
        }

        bool Hit(VrHand hand, out Vector2 local)
        {
            local = Vector2.zero;
            // A hand that is holding a bone is posing, not pointing.
            if (!hand || !hand.IsTracked || hand.HoldingSomething) return false;
            if (!hand.AimRay(out var ray)) return false;
            var plane = new Plane(_root.forward, _root.position);
            if (!plane.Raycast(ray, out var enter) || enter <= 0f) return false;
            var p = _root.InverseTransformPoint(ray.GetPoint(enter));
            local = new Vector2(p.x, p.y);
            // A little slack at the edges: the board is a target, not a precision task.
            return Mathf.Abs(local.x) <= W * 0.5f + 1f && local.y <= 1f && local.y >= -_height - 1f;
        }

        // Row index, or -2-n for tab n, or -1 for the title bar and the gaps.
        int Which(Vector2 local)
        {
            var top = -Pad - TitleH;
            if (_tabs.Count > 1)
            {
                if (local.y <= top && local.y >= top - TabH)
                {
                    var width = (W - 2f * Pad) / _tabs.Count;
                    var index = Mathf.FloorToInt((local.x + W * 0.5f - Pad) / width);
                    if (index >= 0 && index < _tabs.Count) return -2 - index;
                    return -1;
                }
                top -= TabH;
            }
            top -= 0.6f;
            var row = Mathf.FloorToInt((top - local.y) / RowStep);
            if (row < 0 || row >= _rows.Count) return -1;
            // Inside the chip, not the gap under it.
            if (top - local.y - row * RowStep > RowH) return -1;
            // A note is text, not a target: no highlight, no tick, nothing to press.
            if (_rows[row].Kind == Kind.Note) return -1;
            return row;
        }

        bool Live(int target)
        {
            if (target <= -2) return true;
            return target >= 0 && target < _rows.Count && _rows[target].Live;
        }

        void Activate(int target, float x, VrHand hand)
        {
            if (target <= -2)
            {
                var tab = -2 - target;
                if (tab != _tab) { _tab = tab; Rebuild(); }
                hand.Pulse(0.35f, 0.02f);
                return;
            }
            var row = _rows[target];
            if (!row.Live) { hand.Pulse(0.1f, 0.03f); return; }
            switch (row.Kind)
            {
                case Kind.Button:
                    row.Run?.Invoke();
                    hand.Pulse(0.5f, 0.035f);
                    break;
                case Kind.Toggle:
                    row.Set?.Invoke(!row.Get());
                    hand.Pulse(0.4f, 0.025f);
                    break;
                case Kind.Choice:
                    // Left half steps back, right half steps on: the readout is drawn
                    // with arrows either side of it so that is visible rather than lore.
                    if (row.Choices != null && row.Choices.Length > 0 && row.Index != null)
                    {
                        var n = row.Choices.Length;
                        var step = x < (TrackLeft + TrackRight) * 0.5f ? n - 1 : 1;
                        row.Pick?.Invoke((row.Index() + step) % n);
                    }
                    hand.Pulse(0.4f, 0.025f);
                    break;
                case Kind.Slider:
                    _drag = row;
                    _lastDetent = -1;
                    Slide(x);
                    break;
            }
        }

        void Slide(float x)
        {
            if (_drag == null || _drag.Write == null) return;
            var t = Mathf.Clamp01(Mathf.InverseLerp(TrackLeft, TrackRight, x));
            var detent = Mathf.RoundToInt(t * 40f);
            if (detent != _lastDetent)
            {
                _lastDetent = detent;
                _pointer?.Pulse(0.15f, 0.008f);
            }
            _drag.Write(Mathf.Lerp(_drag.Min, _drag.Max, detent / 40f));
        }

        void EndDrag()
        {
            var row = _drag;
            _drag = null;
            row?.Done?.Invoke();
        }

        VrHand Other(VrHand hand)
        {
            if (!_rig) return null;
            return hand == _rig.Left ? _rig.Right : _rig.Left;
        }

        void Unblock()
        {
            if (!_rig) return;
            if (_rig.Left) _rig.Left.PointerBlocked = false;
            if (_rig.Right) _rig.Right.PointerBlocked = false;
        }

        // ---- drawing

        void Draw()
        {
            for (var i = 0; i < MaxTabs; i++)
            {
                var on = i < _tabs.Count && _tabs.Count > 1;
                _tabChip[i].gameObject.SetActive(on);
                _tabText[i].gameObject.SetActive(on);
                if (!on) continue;
                var hot = _hover == -2 - i;
                Tint(_tabChip[i], i == _tab ? TabOn : hot ? RowHot : RowIdle);
                if (_tabText[i].text != _tabs[i].Label) _tabText[i].text = _tabs[i].Label;
            }

            for (var i = 0; i < MaxRows; i++)
            {
                var on = i < _rows.Count;
                _rowChip[i].gameObject.SetActive(on);
                _rowLabel[i].gameObject.SetActive(on);
                _rowValue[i].gameObject.SetActive(on);
                if (!on) { _rowFill[i].gameObject.SetActive(false); continue; }

                var row = _rows[i];
                var live = row.Live;
                Color colour;
                if (row.Kind == Kind.Note) colour = new Color(0.07f, 0.09f, 0.12f, 0.55f);
                else if (!live) colour = RowDead;
                else if (_hover == i) colour = RowHot;
                else if (row.Danger) colour = RowRed;
                else if (row.Kind == Kind.Toggle && row.Get != null) colour = row.Get() ? RowOn : RowIdle;
                else colour = RowIdle;
                Tint(_rowChip[i], colour);

                if (_rowLabel[i].text != row.Label) _rowLabel[i].text = row.Label;
                _rowLabel[i].color = live ? Color.white : new Color(1f, 1f, 1f, 0.4f);

                var text = Readout(row);
                if (_shownValue[i] != text) { _shownValue[i] = text; _rowValue[i].text = text; }

                var fill = 0f;
                if (row.Kind == Kind.Slider && row.Read != null && row.Max > row.Min)
                    fill = Mathf.Clamp01(Mathf.InverseLerp(row.Min, row.Max, row.Read()));
                var bar = _rowFill[i];
                bar.gameObject.SetActive(fill > 0.001f);
                if (fill > 0.001f)
                {
                    var width = (TrackRight - TrackLeft) * fill;
                    var y = RowCentre(i) - RowH * 0.5f + 0.45f;
                    bar.transform.localPosition = new Vector3(TrackLeft + width * 0.5f, y, -0.05f);
                    bar.transform.localScale = new Vector3(width, 0.4f, 1f);
                }
            }
        }

        string Readout(Row row)
        {
            if (row.Value != null) return row.Value();
            switch (row.Kind)
            {
                case Kind.Toggle: return row.Get != null && row.Get() ? "on" : "off";
                case Kind.Slider:
                    if (row.Read == null) return "";
                    return Mathf.RoundToInt(row.Read() * row.Display) + row.Unit;
                case Kind.Choice:
                    if (row.Choices == null || row.Index == null) return "";
                    var i = Mathf.Clamp(row.Index(), 0, row.Choices.Length - 1);
                    return "‹  " + row.Choices[i] + "  ›";
                default: return "";
            }
        }

        void ShowBeam(bool on, VrHand hand = null, Vector2 local = default)
        {
            _beam.gameObject.SetActive(on);
            _cursor.gameObject.SetActive(on);
            if (!on || !hand) return;
            if (!hand.AimRay(out var ray)) return;
            var at = _root.TransformPoint(new Vector3(local.x, local.y, -0.1f));
            _beam.SetPosition(0, ray.origin + ray.direction * (0.03f * (_rig ? _rig.Scale : 1f)));
            _beam.SetPosition(1, at);
            _beam.widthMultiplier = 0.004f * (_rig ? _rig.Scale : 1f);
            _cursor.transform.localPosition = new Vector3(local.x, local.y, -0.15f);
        }

        void Tint(Renderer r, Color c)
        {
            if (!r) return;
            _block.SetColor(BaseColorId, c);
            r.SetPropertyBlock(_block);
        }

        float _rowTop;

        float RowCentre(int i) => _rowTop - i * RowStep - RowH * 0.5f;

        // ---- geometry

        void Layout()
        {
            var tabs = _tabs.Count > 1 ? TabH : 0f;
            _back.transform.localPosition = new Vector3(0f, -_height * 0.5f, 0.1f);
            _back.transform.localScale = new Vector3(W + 1.6f, _height, 1f);

            _title.transform.localPosition = new Vector3(0f, -Pad - TitleH * 0.5f, -0.05f);

            var tabWidth = (W - 2f * Pad) / Mathf.Max(1, _tabs.Count);
            for (var i = 0; i < MaxTabs; i++)
            {
                var x = -W * 0.5f + Pad + tabWidth * (i + 0.5f);
                var y = -Pad - TitleH - TabH * 0.5f;
                _tabChip[i].transform.localPosition = new Vector3(x, y, 0f);
                _tabChip[i].transform.localScale = new Vector3(Mathf.Max(1f, tabWidth - 0.6f), TabH - 0.8f, 1f);
                _tabText[i].transform.localPosition = new Vector3(x, y, -0.05f);
                _tabText[i].rectTransform.sizeDelta = new Vector2(tabWidth - 1.4f, 1.7f);
            }

            _rowTop = -Pad - TitleH - tabs - 0.6f;
            for (var i = 0; i < MaxRows; i++)
            {
                var y = RowCentre(i);
                _rowChip[i].transform.localPosition = new Vector3(0f, y, 0f);
                _rowChip[i].transform.localScale = new Vector3(W, RowH, 1f);
                _rowLabel[i].transform.localPosition = new Vector3(-W * 0.5f + 1.4f + 6.8f, y, -0.05f);
                _rowValue[i].transform.localPosition = new Vector3(TrackRight - 6.2f, y, -0.05f);
            }
        }

        void BuildVisuals()
        {
            var go = new GameObject("VR panel");
            _root = go.transform;
            var material = BoneHandle.OverlayMaterial();

            _back = Quad(_root, "back", material);
            Tint(_back, Back);

            _title = Text(_root, "title", W - 2f * Pad, 2.4f, TextAlignmentOptions.Left);
            _title.fontStyle = FontStyles.Bold;
            _title.transform.localPosition = new Vector3(0f, 0f, -0.05f);
            _title.rectTransform.sizeDelta = new Vector2(W - 2f * Pad, 2.4f);
            _title.alignment = TextAlignmentOptions.Center;

            for (var i = 0; i < MaxTabs; i++)
            {
                _tabChip[i] = Quad(_root, "tab" + i, material);
                _tabText[i] = Text(_root, "tabText" + i, 6f, 1.7f, TextAlignmentOptions.Center);
            }

            for (var i = 0; i < MaxRows; i++)
            {
                _rowChip[i] = Quad(_root, "row" + i, material);
                _rowFill[i] = Quad(_root, "fill" + i, material);
                Tint(_rowFill[i], Fill);
                _rowLabel[i] = Text(_root, "label" + i, 13f, 1.7f, TextAlignmentOptions.Left);
                _rowValue[i] = Text(_root, "value" + i, 12f, 1.7f, TextAlignmentOptions.Right);
                _rowValue[i].color = new Color(0.72f, 0.88f, 0.94f);
            }

            _cursor = Quad(_root, "cursor", material);
            _cursor.transform.localScale = new Vector3(0.7f, 0.7f, 1f);
            Tint(_cursor, new Color(1f, 0.95f, 0.6f, 0.95f));

            var beamGo = new GameObject("beam");
            beamGo.transform.SetParent(_root, false);
            _beam = beamGo.AddComponent<LineRenderer>();
            _beam.useWorldSpace = true;
            _beam.positionCount = 2;
            _beam.sharedMaterial = material;
            _beam.numCapVertices = 2;
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.startColor = _beam.endColor = new Color(0.55f, 0.9f, 1f, 0.55f);

            Layout();
        }

        Renderer Quad(Transform parent, string name, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        // Auto-sized within a rect whose HEIGHT is the text height we want. TextMeshPro's
        // font size is not in RectTransform units and guessing a maximum is what made the
        // first wheel unreadable; giving auto-sizing a range it can never bind against and
        // a correctly sized box means the box decides, whatever the unit relationship is.
        TextMeshPro Text(Transform parent, string name, float width, float height, TextAlignmentOptions align)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = new Vector2(width, height);
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.enableAutoSizing = true;
            t.fontSizeMin = 0.1f;
            t.fontSizeMax = 300f;
            t.color = Color.white;
            var font = t.fontMaterial;
            var zTest = Shader.PropertyToID("_ZTestMode");
            if (font.HasProperty(zTest)) font.SetFloat(zTest, (float)UnityEngine.Rendering.CompareFunction.Always);
            font.renderQueue = 4001;
            return t;
        }

        void OnDisable() { if (IsOpen) Close(); }

        void OnDestroy()
        {
            Unblock();
            if (_root) Destroy(_root.gameObject);
        }
    }
}
