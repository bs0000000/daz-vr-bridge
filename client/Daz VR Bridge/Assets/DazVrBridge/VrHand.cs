// One tracked controller with a grab. No XR Interaction Toolkit dependency:
// pose and grip come straight from the Input System's XRController layout,
// which OpenXR provides once an interaction profile is enabled.
//
// Grab model (FK): while gripping near a handle, the bone's world rotation
// follows the controller's rotation, pivoting at the bone origin. Release
// commits the figure to Daz as one undo step.

using UnityEngine;
using UnityEngine.InputSystem;

namespace DazVrBridge
{
    public sealed class VrHand : MonoBehaviour
    {
        public enum Side { Left, Right }

        public Side side;
        [Tooltip("Handles within this distance of the controller can be grabbed (meters).")]
        public float grabRadius = 0.06f;
        public PoseSync poseSync;

        InputAction _position, _rotation, _grip, _trigger;
        bool _tracked;

        BoneHandle _hover;
        BoneHandle _grabbed;
        Quaternion _grabOffset; // bone.rotation = controller.rotation * offset

        readonly Collider[] _overlap = new Collider[32];

        void Awake()
        {
            var hand = side == Side.Left ? "LeftHand" : "RightHand";
            _position = new InputAction($"{hand}/position", binding: $"<XRController>{{{hand}}}/devicePosition");
            _rotation = new InputAction($"{hand}/rotation", binding: $"<XRController>{{{hand}}}/deviceRotation");
            _grip = new InputAction($"{hand}/grip", InputActionType.Button, $"<XRController>{{{hand}}}/gripPressed");
            _trigger = new InputAction($"{hand}/trigger", InputActionType.Button, $"<XRController>{{{hand}}}/triggerPressed");

            // A visible controller: a small elongated box.
            var vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vis.name = "vis";
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(transform, false);
            vis.transform.localScale = new Vector3(0.03f, 0.03f, 0.10f);
        }

        void OnEnable()
        {
            _position.Enable(); _rotation.Enable(); _grip.Enable(); _trigger.Enable();
        }

        void OnDisable()
        {
            _position.Disable(); _rotation.Disable(); _grip.Disable(); _trigger.Disable();
        }

        void Start()
        {
            if (!poseSync) poseSync = FindAnyObjectByType<PoseSync>();
        }

        void Update()
        {
            // Pose relative to the Camera Offset this hand is parented under.
            var pos = _position.ReadValue<Vector3>();
            var rot = _rotation.ReadValue<Quaternion>();
            _tracked = pos != Vector3.zero || rot != default;
            if (_tracked)
            {
                transform.localPosition = pos;
                transform.localRotation = rot.Equals(default) ? Quaternion.identity : rot;
            }

            if (_grabbed)
            {
                _grabbed.Bone.rotation = transform.rotation * _grabOffset;
                if (!_grip.IsPressed()) Release();
                return;
            }

            UpdateHover();
            if (_hover && _grip.WasPressedThisFrame()) Grab(_hover);
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
                if (_hover && _hover.Current == BoneHandle.State.Hover) _hover.SetState(BoneHandle.State.Idle);
                _hover = best;
                if (_hover) _hover.SetState(BoneHandle.State.Hover);
            }
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
    }
}
