// One tracked controller with a grab. No XR Interaction Toolkit dependency:
// pose and buttons come straight from the Input System's XRController layout,
// which OpenXR provides once an interaction profile is enabled.
//
// Grab model (FK, aim-based): while holding the grab button on a handle, the
// bone swings about its origin so that the segment keeps pointing at the
// controller (drag the forearm and it follows your hand), and rolling the
// controller twists the bone about its own axis. Children follow as a chain.
// Release commits the figure to Daz as one undo step.

using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace DazVrBridge
{
    public sealed class VrHand : MonoBehaviour
    {
        public enum Side { Left, Right }
        public enum GrabButton { Trigger, Grip, Primary, Secondary }

        public Side side;
        public GrabButton grabButton = GrabButton.Trigger;
        [Tooltip("Button that grabs the world (move/scale). Check the HUD's button monitor to see what your controller reports.")]
        public GrabButton worldButton = GrabButton.Grip;
        [Tooltip("Handles within this distance of the controller can be grabbed (meters).")]
        public float grabRadius = 0.06f;

        // Present: a controller for this hand exists and has reported a pose at least once.
        // That is what interaction keys off; losing optical tracking (isTracked=false, the
        // runtime extrapolating from the IMU) only dims the visual.
        public bool IsTracked => DevicePresent && _hasPose;
        public bool DevicePresent { get; private set; }
        public bool OpticallyTracked { get; private set; }
        public string DeviceName { get; private set; } = "";
        bool _hasPose;
        // The other button (grip when trigger grabs bones): held to grab the world.
        public bool WorldGrab => IsTracked && _world.IsPressed();
        public bool HoldingSomething => _grabbed != null;

        InputAction _position, _rotation, _grab, _world, _isTracked, _trackingState;
        InputAction _monTrigger, _monGrip, _monPrimary, _monSecondary; // button monitor for the HUD
        GameObject _vis;
        Renderer _visRenderer;
        Color _visColor;

        IGrabbable _hover;
        IGrabbable _grabbed;

        readonly Collider[] _overlap = new Collider[32];

        void Awake()
        {
            var hand = side == Side.Left ? "LeftHand" : "RightHand";
            _position = new InputAction($"{hand}/position", binding: $"<XRController>{{{hand}}}/devicePosition");
            _rotation = new InputAction($"{hand}/rotation", binding: $"<XRController>{{{hand}}}/deviceRotation");
            _isTracked = new InputAction($"{hand}/isTracked", InputActionType.Button, $"<XRController>{{{hand}}}/isTracked");
            _trackingState = new InputAction($"{hand}/trackingState", InputActionType.Value, $"<XRController>{{{hand}}}/trackingState");
            _grab = ButtonAction($"{hand}/grab", hand, grabButton);
            _world = ButtonAction($"{hand}/world", hand, worldButton);

            _monTrigger = ButtonAction($"{hand}/mon.trigger", hand, GrabButton.Trigger);
            _monGrip = ButtonAction($"{hand}/mon.grip", hand, GrabButton.Grip);
            _monPrimary = ButtonAction($"{hand}/mon.primary", hand, GrabButton.Primary);
            _monSecondary = ButtonAction($"{hand}/mon.secondary", hand, GrabButton.Secondary);

            // A visible controller: a small elongated box, shown only while tracked,
            // drawn through the body (overlay) so it never vanishes inside a limb.
            _vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _vis.name = "vis";
            Destroy(_vis.GetComponent<Collider>());
            _vis.transform.SetParent(transform, false);
            _vis.transform.localScale = new Vector3(0.03f, 0.03f, 0.10f);
            _visRenderer = _vis.GetComponent<Renderer>();
            _visRenderer.sharedMaterial = BoneHandle.OverlayMaterial();
            _visColor = side == Side.Left ? new Color(0.85f, 0.9f, 1f, 0.9f) : new Color(1f, 0.9f, 0.85f, 0.9f);
            SetVisAlpha(0.9f);
            _vis.SetActive(false);
        }

        // A button action with every binding that could mean that button on some
        // controller/runtime: digital press plus the analog axis for trigger/grip
        // (pressed past 0.5), and both common names for the face buttons.
        static InputAction ButtonAction(string name, string hand, GrabButton button)
        {
            var a = new InputAction(name, InputActionType.Button);
            var h = $"<XRController>{{{hand}}}";
            switch (button)
            {
                case GrabButton.Trigger:
                    a.AddBinding($"{h}/triggerPressed");
                    a.AddBinding($"{h}/trigger");
                    break;
                case GrabButton.Grip:
                    a.AddBinding($"{h}/gripPressed");
                    a.AddBinding($"{h}/grip");
                    a.AddBinding($"{h}/gripButton");
                    break;
                case GrabButton.Primary:
                    a.AddBinding($"{h}/primaryButton");
                    break;
                case GrabButton.Secondary:
                    a.AddBinding($"{h}/secondaryButton");
                    break;
            }
            return a;
        }

        InputAction[] AllActions() => new[] { _position, _rotation, _grab, _world, _isTracked, _trackingState, _monTrigger, _monGrip, _monPrimary, _monSecondary };

        void SetVisAlpha(float a)
        {
            if (!_visRenderer) return;
            var c = _visColor; c.a = a;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            _visRenderer.SetPropertyBlock(block);
        }

        void OnEnable()
        {
            foreach (var a in AllActions()) a.Enable();
        }

        void OnDisable()
        {
            foreach (var a in AllActions()) a.Disable();
        }

        // A short buzz. Feedback you don't have to read: a tick when a hand touches down
        // on a surface, a firmer one when a joint runs out of range.
        public void Pulse(float amplitude, float duration)
        {
            var device = _position.activeControl?.device;
            if (device is UnityEngine.InputSystem.XR.XRControllerWithRumble rumble)
                rumble.SendImpulse(Mathf.Clamp01(amplitude), duration);
        }

        // For the HUD: which of the four buttons this controller currently reports pressed.
        public string ButtonMonitor()
        {
            if (!IsTracked) return "-";
            var track = OpticallyTracked ? "" : " (imu)";
            return $"trig={(_monTrigger.IsPressed() ? 1 : 0)} grip={(_monGrip.IsPressed() ? 1 : 0)} A/X={(_monPrimary.IsPressed() ? 1 : 0)} B/Y={(_monSecondary.IsPressed() ? 1 : 0)}{track}";
        }

        void Update()
        {
            // Bound controls exist iff a controller for this hand is connected.
            var controls = _position.controls;
            DevicePresent = controls.Count > 0;
            DeviceName = DevicePresent ? controls[0].device.displayName : "";
            OpticallyTracked = DevicePresent && _isTracked.IsPressed();

            if (DevicePresent)
            {
                // Take a pose component only when the runtime flags it valid (bit 1 =
                // position, bit 2 = rotation); otherwise keep the last one rather than
                // snapping to the origin. Runtimes usually keep both valid on the IMU alone.
                var state = _trackingState.controls.Count > 0 ? (int)_trackingState.ReadValue<int>() : 3;
                if ((state & 1) != 0)
                {
                    var p = _position.ReadValue<Vector3>();
                    if (p != Vector3.zero || _hasPose) { transform.localPosition = p; _hasPose = true; }
                }
                if ((state & 2) != 0)
                {
                    var rot = _rotation.ReadValue<Quaternion>();
                    if (rot.x != 0f || rot.y != 0f || rot.z != 0f || rot.w != 0f) transform.localRotation = rot;
                }
            }

            _vis.SetActive(IsTracked);
            SetVisAlpha(OpticallyTracked ? 0.9f : 0.35f);

            if (_grabbed != null)
            {
                _grabbed.UpdateGrab(transform);
                if (!_grab.IsPressed() || !IsTracked) Release();
                return;
            }

            if (!IsTracked || WorldGrab) { ClearHover(); return; } // world grab has priority
            UpdateHover();
            if (_hover != null && _grab.WasPressedThisFrame()) Grab(_hover);
        }

        void UpdateHover()
        {
            IGrabbable best = null;
            var bestDist = float.MaxValue;
            // Reach is a physical distance: scale it with the rig so a giant still reaches.
            var reach = grabRadius * transform.lossyScale.x;
            var n = Physics.OverlapSphereNonAlloc(transform.position, reach, _overlap, ~0, QueryTriggerInteraction.Collide);
            var bestPriority = int.MaxValue;
            for (var i = 0; i < n; i++)
            {
                var g = _overlap[i].GetComponentInParent<IGrabbable>(); // ring/body colliders are children
                if (g == null || g.IsGrabbed) continue;
                var d = g.DistanceTo(transform.position);
                if (g.Priority < bestPriority || (g.Priority == bestPriority && d < bestDist))
                {
                    bestPriority = g.Priority; bestDist = d; best = g;
                }
            }
            if (best != _hover)
            {
                ClearHover();
                _hover = best;
                _hover?.SetHover(true);
            }
        }

        void ClearHover()
        {
            _hover?.SetHover(false);
            _hover = null;
        }

        void Grab(IGrabbable g)
        {
            _grabbed = g;
            _hover = null;
            g.BeginGrab(transform);
        }

        void Release()
        {
            var g = _grabbed;
            _grabbed = null;
            g.EndGrab(transform);
        }

        // For the HUD: every XR controller the Input System currently sees.
        public static string DescribeDevices()
        {
            var sb = new StringBuilder();
            foreach (var d in InputSystem.devices)
            {
                if (!(d is XRController)) continue;
                var usages = string.Join("/", d.usages);
                sb.Append(sb.Length > 0 ? ", " : "").Append(d.displayName).Append(" [").Append(usages).Append("]");
            }
            return sb.Length > 0 ? sb.ToString() : "no XR controllers";
        }
    }
}
