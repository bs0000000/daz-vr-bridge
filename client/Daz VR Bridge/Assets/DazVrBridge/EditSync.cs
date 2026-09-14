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
            foreach (var h in hands)
            {
                if (!h || h.side != bindingHand || !h.IsTracked) continue;
                // A face button touched mid-grab is a fumble, not a command.
                if (h.HoldingSomething || h.WorldGrab || VrHand.UiBlocked) continue;
                if (Held(h, undoButton)) face = undoButton;
                else if (Held(h, redoButton)) face = redoButton;
                break;
            }

            if (face == Face.None) _heldFace = Face.None;
            else if (face != _heldFace) { _heldFace = face; _nextFire = Time.time + holdSeconds; }
            else if (Time.time >= _nextFire)
            {
                _nextFire = Time.time + repeatSeconds;
                if (face == undoButton) Undo(); else Redo();
            }

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
