// Undo and redo, from inside the headset.
//
// There is no VR-side undo stack. The buttons pop Daz's own stack -- the same step
// Ctrl+Z at the desk would pop -- because every pose.commit already lands there as a
// named entry. That is the only arrangement where the two sides agree about what
// happened; a private stack in the client would drift the moment anyone touched Daz.
//
// The rollback comes back as a pose.state / node.state a moment later, through the
// normal watcher path, so nothing here has to re-pose anything by hand.
//
// Feedback is haptic, not text: one firm buzz for undo, two lighter ones for redo,
// and a single short tick when there was nothing left to undo.

using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DazVrBridge
{
    public sealed class EditSync : MonoBehaviour
    {
        public BridgeSession session;
        [Tooltip("Controllers whose face buttons trigger undo/redo. Leave empty to find them.")]
        public VrHand[] hands;

        public enum Face { None, Primary, Secondary }

        [Header("Bindings (A/X = Primary, B/Y = Secondary)")]
        [Tooltip("Face button that undoes. Only fires when that hand is not holding anything.")]
        public Face undoButton = Face.Secondary;
        public Face redoButton = Face.Primary;
        [Tooltip("Which hand the buttons are read from.")]
        public VrHand.Side bindingHand = VrHand.Side.Left;
        [Tooltip("How long the button must be held before history fires. A tap does nothing: a face button is far too easy to brush while reaching for a bone, and an accidental undo costs real work.")]
        public float holdSeconds = 0.4f;
        [Tooltip("While the button stays held, fire again this often, so several steps can be walked back in one gesture.")]
        public float repeatSeconds = 0.55f;
        [Tooltip("Ignore requests closer together than this, so a bounced button cannot undo twice (seconds).")]
        public float repeatGuard = 0.25f;

        // What Daz says is on the stack right now, for a menu to label its buttons with.
        public bool CanUndo { get; private set; }
        public bool CanRedo { get; private set; }
        public string UndoCaption { get; private set; } = "";
        public string RedoCaption { get; private set; } = "";
        // Last thing that actually happened, for the HUD.
        public string LastEdit { get; private set; } = "";

        long _pending = -1;
        SceneLoader _loader;
        public bool Pending => _pending >= 0;
        float _nextAllowed;
        float _nextHandScan;
        Face _heldFace = Face.None;
        float _nextFire;
        float _fireInterval;
        float _flashUntil;

        const int ChargeSegments = 14;
        static readonly Color ChargeUndo = new Color(1f, 0.70f, 0.25f, 0.95f);
        static readonly Color ChargeRedo = new Color(0.45f, 0.95f, 0.55f, 0.95f);
        static readonly Color ChargeDim = new Color(0.25f, 0.27f, 0.30f, 0.45f);
        static readonly int ChargeColorId = Shader.PropertyToID("_BaseColor");
        Transform _chargeRoot;
        Renderer[] _charge;
        MaterialPropertyBlock _block;

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            _loader = FindAnyObjectByType<SceneLoader>();
            session.ControlFrame += OnFrame;
            session.ControlState += OnState;
        }

        void OnDestroy()
        {
            if (session) { session.ControlFrame -= OnFrame; session.ControlState -= OnState; }
            if (_chargeRoot) Destroy(_chargeRoot.gameObject);
        }

        void OnState(BridgeClient.State state)
        {
            if (state == BridgeClient.State.Connected) return;
            _pending = -1; CanUndo = CanRedo = false;
        }

        void Update()
        {
            // VrRig spawns the controllers, so they may not exist yet at Start; look again
            // until they do.
            if ((hands == null || hands.Length == 0) && Time.time >= _nextHandScan)
            {
                _nextHandScan = Time.time + 1f;
                hands = FindObjectsByType<VrHand>();
            }
            if (hands == null) return;

            // History is a hold, not a tap. Holding past the threshold fires once and then
            // repeats, so walking back three steps is one gesture rather than three
            // presses; letting go disarms it.
            var face = Face.None;
            VrHand on = null;
            foreach (var h in hands)
            {
                if (!h || h.side != bindingHand || !h.IsTracked) continue;
                // A face button touched mid-grab is a fumble, not a command.
                if (h.HoldingSomething || h.WorldGrab || VrHand.UiBlocked) continue;
                on = h;
                if (Held(h, undoButton)) face = undoButton;
                else if (Held(h, redoButton)) face = redoButton;
                break;
            }

            if (face == Face.None) _heldFace = Face.None;
            else if (face != _heldFace)
            {
                _heldFace = face;
                _fireInterval = holdSeconds;
                _nextFire = Time.time + holdSeconds;
            }
            else if (Time.time >= _nextFire)
            {
                _fireInterval = repeatSeconds;
                _nextFire = Time.time + repeatSeconds;
                _flashUntil = Time.time + 0.2f;
                if (face == undoButton) Undo(); else Redo();
            }

            DrawCharge(on, face);

#if ENABLE_INPUT_SYSTEM
            // Desk testing without a headset.
            var kb = Keyboard.current;
            if (kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed))
            {
                if (kb.zKey.wasPressedThisFrame) Undo();
                else if (kb.yKey.wasPressedThisFrame) Redo();
            }
#endif
        }

        // A ring of segments at the hand that fills while the button is held, so the hold
        // is something you watch rather than something you have to have been told about.
        // It fills anticlockwise in amber for undo and clockwise in green for redo -- the
        // direction is the icon -- and flashes white on each step it takes.
        void DrawCharge(VrHand hand, Face face)
        {
            if (face == Face.None || !hand)
            {
                if (_chargeRoot) _chargeRoot.gameObject.SetActive(false);
                return;
            }

            if (!_chargeRoot) BuildCharge();
            var head = Camera.main;
            if (!head) return;

            var scale = hand.transform.lossyScale.x;
            // Just above the controller, so it is not buried inside the drawn stick.
            var at = hand.transform.position + head.transform.up * (0.045f * scale);
            _chargeRoot.SetPositionAndRotation(at, Quaternion.LookRotation(at - head.transform.position, head.transform.up));
            _chargeRoot.localScale = Vector3.one * scale;
            _chargeRoot.gameObject.SetActive(true);

            var progress = _fireInterval > 1e-4f
                ? Mathf.Clamp01(1f - (_nextFire - Time.time) / _fireInterval)
                : 1f;
            var flashing = Time.time < _flashUntil;
            var undo = face == undoButton;
            var lit = flashing ? ChargeSegments : Mathf.RoundToInt(progress * ChargeSegments);
            var colour = flashing ? Color.white : undo ? ChargeUndo : ChargeRedo;

            for (var i = 0; i < ChargeSegments; i++)
            {
                // Anticlockwise for undo, clockwise for redo, both starting from the top.
                var index = undo ? i : ChargeSegments - 1 - i;
                // Every segment stays drawn; an unlit one is just dim, so the ring reads
                // as a dial with a filled arc rather than as pieces appearing out of nothing.
                _block.SetColor(ChargeColorId, i < lit ? colour : ChargeDim);
                _charge[index].SetPropertyBlock(_block);
            }
        }

        void BuildCharge()
        {
            _block = new MaterialPropertyBlock();
            var root = new GameObject("undo charge");
            _chargeRoot = root.transform;
            var material = BoneHandle.OverlayMaterial();
            _charge = new Renderer[ChargeSegments];
            for (var i = 0; i < ChargeSegments; i++)
            {
                var degrees = 90f - i * (360f / ChargeSegments);
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = $"seg{i}";
                Destroy(go.GetComponent<Collider>());
                go.transform.SetParent(_chargeRoot, false);
                go.transform.localPosition = new Vector3(
                    Mathf.Cos(degrees * Mathf.Deg2Rad), Mathf.Sin(degrees * Mathf.Deg2Rad), 0f) * 0.032f;
                go.transform.localRotation = Quaternion.Euler(0f, 0f, degrees);
                go.transform.localScale = new Vector3(0.006f, 0.013f, 1f);
                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _charge[i] = r;
            }
            root.SetActive(false);
        }

        static bool Held(VrHand h, Face f)
        {
            switch (f)
            {
                case Face.Primary: return h.PrimaryHeld;
                case Face.Secondary: return h.SecondaryHeld;
                default: return false;
            }
        }

        public void Undo() => Send("edit.undo");
        public void Redo() => Send("edit.redo");

        void Send(string type)
        {
            if (!session || !session.ControlReady || Pending || Time.time < _nextAllowed
                || VrHand.AnyHolding || (_loader && _loader.Busy)) return;
            if (hands != null) foreach (var hand in hands) if (hand && hand.WorldGrab) return;
            _nextAllowed = Time.time + repeatGuard;
            _pending = session.SendControl(new JObject { ["t"] = type });
        }

        void OnFrame(BridgeFrame f)
        {
            switch (f.Type)
            {
                case "welcome":
                    if (f.Header["edit"] is JObject e) ReadState(e);
                    break;
                case "edit.state":
                    ReadState(f.Header);
                    break;
                case "error":
                    if (f.Header.Value<long?>("ref_seq") == _pending)
                    { _pending = -1; LastEdit = f.Header.Value<string>("msg") ?? "Edit failed"; }
                    break;
                case "edit.result":
                    if (f.Header.Value<long?>("ref_seq") != _pending) break;
                    _pending = -1;
                    OnResult(f.Header);
                    break;
            }
        }

        void ReadState(JObject h)
        {
            CanUndo = h.Value<bool?>("can_undo") ?? false;
            CanRedo = h.Value<bool?>("can_redo") ?? false;
            UndoCaption = h.Value<string>("undo") ?? "";
            RedoCaption = h.Value<string>("redo") ?? "";
        }

        void OnResult(JObject h)
        {
            ReadState(h);
            var ok = h.Value<bool?>("ok") ?? false;
            var action = h.Value<string>("action") ?? "undo";
            var caption = h.Value<string>("caption") ?? "";

            LastEdit = ok
                ? $"{action}: {(string.IsNullOrEmpty(caption) ? "(unnamed step)" : caption)}"
                : $"nothing to {action}";

            if (!ok) StartCoroutine(Buzz(1, 0.25f, 0.02f));          // a flat tick: nope
            else if (action == "redo") StartCoroutine(Buzz(2, 0.5f, 0.03f));
            else StartCoroutine(Buzz(1, 0.85f, 0.07f));
        }

        // Both hands, so the buzz reads as "the scene changed" rather than "that
        // controller did something".
        IEnumerator Buzz(int times, float amplitude, float duration)
        {
            for (var i = 0; i < times; i++)
            {
                if (hands != null) foreach (var h in hands) if (h) h.Pulse(amplitude, duration);
                if (i + 1 < times) yield return new WaitForSeconds(duration + 0.06f);
            }
        }
    }
}
