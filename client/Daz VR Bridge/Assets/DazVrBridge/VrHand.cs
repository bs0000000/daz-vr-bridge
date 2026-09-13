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

        public bool IsTracked { get; private set; }
        public string DeviceName { get; private set; } = "";
        // The other button (grip when trigger grabs bones): held to grab the world.
        public bool WorldGrab => IsTracked && _world.IsPressed();
        public bool HoldingSomething => _grabbed != null;

        InputAction _position, _rotation, _grab, _world, _isTracked;
        InputAction _monTrigger, _monGrip, _monPrimary, _monSecondary; // button monitor for the HUD
        GameObject _vis;

        IGrabbable _hover;
        IGrabbable _grabbed;

        readonly Collider[] _overlap = new Collider[32];

        void Awake()
        {
            var hand = side == Side.Left ? "LeftHand" : "RightHand";
            _position = new InputAction($"{hand}/position", binding: $"<XRController>{{{hand}}}/devicePosition");
            _rotation = new InputAction($"{hand}/rotation", binding: $"<XRController>{{{hand}}}/deviceRotation");
            _isTracked = new InputAction($"{hand}/isTracked", InputActionType.Button, $"<XRController>{{{hand}}}/isTracked");
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
            var r = _vis.GetComponent<Renderer>();
            r.sharedMaterial = BoneHandle.OverlayMaterial();
            var block = new MaterialPropertyBlock();
            var c = side == Side.Left ? new Color(0.85f, 0.9f, 1f, 0.9f) : new Color(1f, 0.9f, 0.85f, 0.9f);
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            r.SetPropertyBlock(block);
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

        InputAction[] AllActions() => new[] { _position, _rotation, _grab, _world, _isTracked, _monTrigger, _monGrip, _monPrimary, _monSecondary };

        void OnEnable()
        {
            foreach (var a in AllActions()) a.Enable();
        }

        void OnDisable()
        {
            foreach (var a in AllActions()) a.Disable();
        }

        // For the HUD: which of the four buttons this controller currently reports pressed.
        public string ButtonMonitor()
        {
            if (!IsTracked) return "-";
            return $"trig={(_monTrigger.IsPressed() ? 1 : 0)} grip={(_monGrip.IsPressed() ? 1 : 0)} A/X={(_monPrimary.IsPressed() ? 1 : 0)} B/Y={(_monSecondary.IsPressed() ? 1 : 0)}";
        }

        void Update()
        {
            var device = _position.activeControl?.device;
            DeviceName = device != null ? device.displayName : "";
            IsTracked = device != null && _isTracked.IsPressed();
            _vis.SetActive(IsTracked);

            if (IsTracked)
            {
                // Pose relative to the Camera Offset this hand is parented under.
                transform.localPosition = _position.ReadValue<Vector3>();
                var rot = _rotation.ReadValue<Quaternion>();
                if (rot.x != 0f || rot.y != 0f || rot.z != 0f || rot.w != 0f) transform.localRotation = rot;
            }

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
