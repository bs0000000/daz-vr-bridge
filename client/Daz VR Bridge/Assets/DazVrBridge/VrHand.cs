// One tracked controller with a grab. No XR Interaction Toolkit dependency:
// pose and buttons come straight from the Input System's XRController layout,
// which OpenXR provides once an interaction profile is enabled.
//
// Grab model (FK): while holding the grab button near a handle, the bone's
// world rotation follows the controller's rotation, pivoting at the bone
// origin. Release commits the figure to Daz as one undo step.

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
        public PoseSync poseSync;

        public bool IsTracked { get; private set; }
        public string DeviceName { get; private set; } = "";

        InputAction _position, _rotation, _grab, _isTracked;
        GameObject _vis;

        BoneHandle _hover;
        BoneHandle _grabbed;
        Quaternion _grabOffset; // bone.rotation = controller.rotation * offset

        readonly Collider[] _overlap = new Collider[32];

        void Awake()
        {
            var hand = side == Side.Left ? "LeftHand" : "RightHand";
            _position = new InputAction($"{hand}/position", binding: $"<XRController>{{{hand}}}/devicePosition");
            _rotation = new InputAction($"{hand}/rotation", binding: $"<XRController>{{{hand}}}/deviceRotation");
            _isTracked = new InputAction($"{hand}/isTracked", InputActionType.Button, $"<XRController>{{{hand}}}/isTracked");
            var button = grabButton == GrabButton.Trigger ? "triggerPressed" : "gripPressed";
            _grab = new InputAction($"{hand}/grab", InputActionType.Button, $"<XRController>{{{hand}}}/{button}");

            // A visible controller: a small elongated box, shown only while tracked.
            _vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _vis.name = "vis";
            Destroy(_vis.GetComponent<Collider>());
            _vis.transform.SetParent(transform, false);
            _vis.transform.localScale = new Vector3(0.03f, 0.03f, 0.10f);
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

        void Start()
        {
            if (!poseSync) poseSync = FindAnyObjectByType<PoseSync>();
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

            if (_grabbed)
            {
                _grabbed.Bone.rotation = transform.rotation * _grabOffset;
                if (!_grab.IsPressed() || !IsTracked) Release();
                return;
            }

            if (!IsTracked) { ClearHover(); return; }
            UpdateHover();
            if (_hover && _grab.WasPressedThisFrame()) Grab(_hover);
        }

        void UpdateHover()
        {
            BoneHandle best = null;
            var bestDist = float.MaxValue;
            var n = Physics.OverlapSphereNonAlloc(transform.position, grabRadius, _overlap, ~0, QueryTriggerInteraction.Collide);
            for (var i = 0; i < n; i++)
            {
                var h = _overlap[i].GetComponent<BoneHandle>();
                if (!h || h.Current == BoneHandle.State.Grabbed) continue;
                var d = Vector3.Distance(transform.position, h.transform.position);
                if (d < bestDist) { bestDist = d; best = h; }
            }
            if (best != _hover)
            {
                ClearHover();
                _hover = best;
                if (_hover) _hover.SetState(BoneHandle.State.Hover);
            }
        }

        void ClearHover()
        {
            if (_hover && _hover.Current == BoneHandle.State.Hover) _hover.SetState(BoneHandle.State.Idle);
            _hover = null;
        }

        void Grab(BoneHandle h)
        {
            _grabbed = h;
            _hover = null;
            _grabOffset = Quaternion.Inverse(transform.rotation) * h.Bone.rotation;
            h.SetState(BoneHandle.State.Grabbed);
            poseSync?.SetGrabbed(h.Figure.Id, true);
        }

        void Release()
        {
            var h = _grabbed;
            _grabbed = null;
            h.SetState(BoneHandle.State.Idle);
            poseSync?.SetGrabbed(h.Figure.Id, false);
            poseSync?.Commit(h.Figure, $"VR pose: {h.BoneId}");
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
