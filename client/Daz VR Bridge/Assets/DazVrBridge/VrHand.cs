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
        public enum GrabButton { Trigger, Grip }

        public Side side;
        public GrabButton grabButton = GrabButton.Trigger;
        [Tooltip("Handles within this distance of the controller can be grabbed (meters).")]
        public float grabRadius = 0.06f;

        public bool IsTracked { get; private set; }
        public string DeviceName { get; private set; } = "";

        InputAction _position, _rotation, _grab, _isTracked;
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
            var button = grabButton == GrabButton.Trigger ? "triggerPressed" : "gripPressed";
            _grab = new InputAction($"{hand}/grab", InputActionType.Button, $"<XRController>{{{hand}}}/{button}");

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

        void OnEnable()
        {
            _position.Enable(); _rotation.Enable(); _grab.Enable(); _isTracked.Enable();
        }

        void OnDisable()
        {
            _position.Disable(); _rotation.Disable(); _grab.Disable(); _isTracked.Disable();
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

            if (!IsTracked) { ClearHover(); return; }
            UpdateHover();
            if (_hover != null && _grab.WasPressedThisFrame()) Grab(_hover);
        }

        void UpdateHover()
        {
            IGrabbable best = null;
            var bestDist = float.MaxValue;
            var n = Physics.OverlapSphereNonAlloc(transform.position, grabRadius, _overlap, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < n; i++)
            {
                var g = _overlap[i].GetComponentInParent<IGrabbable>(); // ring/body colliders are children
                if (g == null || g.IsGrabbed) continue;
                var d = g.DistanceTo(transform.position);
                if (d < bestDist) { bestDist = d; best = g; }
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
