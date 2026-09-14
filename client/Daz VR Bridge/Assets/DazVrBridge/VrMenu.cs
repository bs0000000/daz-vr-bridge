// Runtime menu: right B opens, point with the right controller and use trigger.
// Analytic canvas hit testing avoids EventSystem/XR Interaction Toolkit setup.
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

namespace DazVrBridge
{
    [DefaultExecutionOrder(-50)]
    public sealed class VrMenu : MonoBehaviour
    {
        const string Prefix = "DazVrBridge.Menu.";
        const int Count = 12;
        readonly Text[] _labels = new Text[Count];
        readonly Image[] _rows = new Image[Count];
        Canvas _canvas;
        Text _status;
        LineRenderer _ray;
        Material _rayMaterial;
        VrRig _rig;
        BoneHandles _handles;
        SceneLoader _loader;
        EditSync _edits;
        BridgeSession _session;
        bool _open;
        int _hover = -1;
        float _refresh;
        Font _font;

        void Start()
        {
            _rig = FindAnyObjectByType<VrRig>();
            _handles = FindAnyObjectByType<BoneHandles>();
            _loader = FindAnyObjectByType<SceneLoader>();
            _edits = FindAnyObjectByType<EditSync>();
            _session = FindAnyObjectByType<BridgeSession>();
            LoadSettings();
            Build();
        }

        void Update()
        {
            bool toggle = (_rig && _rig.Right && _rig.Right.SecondaryPressed)
                || (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame);
            if (toggle && !VrHand.AnyHolding) SetOpen(!_open);
            if (_open && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) SetOpen(false);
        }

        void SetOpen(bool value)
        {
            if (!_canvas) return;
            if (value)
            {
                var cam = Camera.main;
                if (!cam) return;
                float scale = _rig ? _rig.Scale : 1f;
                _canvas.transform.SetPositionAndRotation(cam.transform.position + cam.transform.forward * (0.65f * scale), cam.transform.rotation);
                _canvas.transform.localScale = Vector3.one * (0.001f * scale);
            }
            _open = value;
            VrHand.UiBlocked = value;
            _canvas.gameObject.SetActive(value);
            _ray.gameObject.SetActive(value);
            _hover = -1;
            Refresh();
        }

        void LateUpdate()
        {
            if (!_open) return;
            var hand = _rig ? _rig.Right : null;
            int hit = -1;
            if (hand && hand.IsTracked)
            {
                var origin = hand.transform.position;
                var direction = hand.transform.forward;
                var plane = new Plane(_canvas.transform.forward, _canvas.transform.position);
                var end = origin + direction * ((_rig ? _rig.Scale : 1f) * 2f);
                if (plane.Raycast(new Ray(origin, direction), out var distance) && distance < ((_rig ? _rig.Scale : 1f) * 2f))
                {
                    end = origin + direction * distance;
                    var local = _canvas.transform.InverseTransformPoint(end);
                    int row = Mathf.FloorToInt((260f - local.y) / 44f);
                    if (Mathf.Abs(local.x) <= 240f && row >= 0 && row < Count) hit = row;
                }
                _ray.enabled = true;
                _ray.startWidth = _ray.endWidth = 0.0015f * (_rig ? _rig.Scale : 1f);
                _ray.SetPosition(0, origin); _ray.SetPosition(1, end);
                if (hit != _hover && hit >= 0) hand.Pulse(0.15f, 0.015f);
                if (hit >= 0 && hand.TriggerPressed && Available(hit))
                {
                    Activate(hit); hand.Pulse(0.35f, 0.025f); Refresh();
                }
            }
            else _ray.enabled = false;
            if (hit != _hover) { _hover = hit; Refresh(); }
            if (Time.unscaledTime >= _refresh) { _refresh = Time.unscaledTime + 0.2f; Refresh(); }
        }

        bool Available(int row)
        {
            if (row == 0) return _edits && _edits.CanUndo && !_edits.Pending && !(_loader && _loader.Busy);
            if (row == 1) return _edits && _edits.CanRedo && !_edits.Pending && !(_loader && _loader.Busy);
            if (row == 2) return _loader && !_loader.Busy && _session && _session.ControlReady;
            if (row >= 3 && row <= 8) return _handles;
            if (row == 10) return _rig;
            return true;
        }

        void Activate(int row)
        {
            switch (row)
            {
                case 0: _edits.Undo(); return;
                case 1: _edits.Redo(); return;
                case 2: _loader.RequestScene(); return;
                case 3: _handles.surfaceSnap = !_handles.surfaceSnap; break;
                case 4: _handles.clampToLimits = !_handles.clampToLimits; break;
                case 5: _handles.rollAssist = !_handles.rollAssist; break;
                case 6: _handles.showLimitGizmo = !_handles.showLimitGizmo; break;
                case 7: _handles.showLimitText = !_handles.showLimitText; break;
                case 8: _handles.showDistance = _handles.showDistance < 0.39f ? 0.45f : _handles.showDistance < 0.59f ? 0.6f : 0.3f; break;
                case 9: VrHand.HapticGain = VrHand.HapticGain > 0.75f ? 0f : VrHand.HapticGain < 0.25f ? 0.5f : 1f; break;
                case 10:
                    var cam = Camera.main;
                    if (cam)
                    {
                        var pivot = cam.transform.position;
                        float factor = 1f / _rig.Scale;
                        _rig.transform.position = pivot + (_rig.transform.position - pivot) * factor;
                        _rig.transform.localScale = Vector3.one;
                        SetOpen(true);
                    }
                    return;
                case 11: SetOpen(false); return;
            }
            if (_handles) _handles.ApplySettings();
            SaveSettings();
        }

        static string Toggle(string name, bool value) => name + (value ? "   ON" : "   OFF");
        void Refresh()
        {
            if (!_canvas || !_open) return;
            string[] captions = {
                EditLabel("Undo", _edits ? _edits.UndoCaption : ""),
                EditLabel("Redo", _edits ? _edits.RedoCaption : ""),
                "Refresh scene",
                Toggle("Surface snap", _handles && _handles.surfaceSnap),
                Toggle("Joint clamping", _handles && _handles.clampToLimits),
                Toggle("Forearm roll assist", _handles && _handles.rollAssist),
                Toggle("Limit rods", _handles && _handles.showLimitGizmo),
                Toggle("Limit text", _handles && _handles.showLimitText),
                "Handle reveal   " + (_handles ? Mathf.RoundToInt(_handles.showDistance * 100f) : 0) + " cm",
                "Haptics   " + Mathf.RoundToInt(VrHand.HapticGain * 100f) + "%",
                "Return to life size", "Close (right B)"
            };
            for (int i = 0; i < Count; i++)
            {
                if (_labels[i].text != captions[i]) _labels[i].text = captions[i];
                _labels[i].color = Available(i) ? Color.white : Color.gray;
                _rows[i].color = i == _hover && Available(i) ? new Color(0.05f, 0.4f, 0.5f) : new Color(0.10f, 0.14f, 0.20f);
            }
            string status = !_session || !_session.ControlReady ? "Disconnected" : _loader && _loader.Busy ? _loader.Status : "Right trigger: select | left B/A: undo/redo";
            if (_status.text != status) _status.text = status;
        }

        static string EditLabel(string action, string caption)
        {
            if (string.IsNullOrEmpty(caption)) return action + " (Daz history)";
            if (caption.Length > 28) caption = caption.Substring(0, 27) + "…";
            return action + "   " + caption;
        }

        void Build()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("VR session menu", typeof(RectTransform), typeof(Canvas));
            go.layer = 5;
            _canvas = go.GetComponent<Canvas>(); _canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)go.transform).sizeDelta = new Vector2(520f, 690f);
            var panel = go.AddComponent<Image>(); panel.color = new Color(0.035f, 0.055f, 0.09f, 0.98f); panel.raycastTarget = false;
            Label(go.transform, "Session & settings", new Vector2(0, 307), new Vector2(480, 48), 26);
            for (int i = 0; i < Count; i++)
            {
                var row = new GameObject("Option " + i, typeof(RectTransform), typeof(Image));
                row.layer = 5; row.transform.SetParent(go.transform, false);
                var rect = (RectTransform)row.transform; rect.sizeDelta = new Vector2(480, 40); rect.anchoredPosition = new Vector2(0, 238 - i * 44);
                _rows[i] = row.GetComponent<Image>(); _rows[i].raycastTarget = false;
                _labels[i] = Label(row.transform, "", Vector2.zero, new Vector2(455, 40), 21);
            }
            _status = Label(go.transform, "", new Vector2(0, -304), new Vector2(480, 50), 17);
            var rayGo = new GameObject("Menu pointer"); rayGo.layer = 5; _ray = rayGo.AddComponent<LineRenderer>();
            _rayMaterial = new Material(BoneHandle.OverlayMaterial());
            _ray.sharedMaterial = _rayMaterial; _ray.positionCount = 2;
            _ray.startColor = _ray.endColor = Color.cyan;
            _ray.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _ray.receiveShadows = false;
            SetOpen(false);
        }

        Text Label(Transform parent, string value, Vector2 position, Vector2 size, int fontSize)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text)); go.layer = 5; go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.sizeDelta = size; rect.anchoredPosition = position;
            var text = go.GetComponent<Text>(); text.font = _font; text.fontSize = fontSize; text.text = value;
            text.alignment = TextAnchor.MiddleLeft; text.color = Color.white; text.raycastTarget = false;
            return text;
        }

        void LoadSettings()
        {
            if (_handles)
            {
                _handles.surfaceSnap = Read("snap", _handles.surfaceSnap);
                _handles.clampToLimits = Read("clamp", _handles.clampToLimits);
                _handles.rollAssist = Read("roll", _handles.rollAssist);
                _handles.showLimitGizmo = Read("rods", _handles.showLimitGizmo);
                _handles.showLimitText = Read("text", _handles.showLimitText);
                _handles.showDistance = Mathf.Clamp(PlayerPrefs.GetFloat(Prefix + "reveal", _handles.showDistance), 0.15f, 0.6f);
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
                PlayerPrefs.SetInt(Prefix + "clamp", _handles.clampToLimits ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "roll", _handles.rollAssist ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "rods", _handles.showLimitGizmo ? 1 : 0);
                PlayerPrefs.SetInt(Prefix + "text", _handles.showLimitText ? 1 : 0);
                PlayerPrefs.SetFloat(Prefix + "reveal", _handles.showDistance);
            }
            PlayerPrefs.SetFloat(Prefix + "haptics", VrHand.HapticGain);
            PlayerPrefs.Save();
        }
        void OnDisable() { if (_canvas) SetOpen(false); else VrHand.UiBlocked = false; }
        void OnDestroy()
        {
            VrHand.UiBlocked = false;
            if (_canvas) Destroy(_canvas.gameObject);
            if (_ray) Destroy(_ray.gameObject);
            if (_rayMaterial) Destroy(_rayMaterial);
        }
    }
}
