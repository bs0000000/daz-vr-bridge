// Put this on the XR Origin. Spawns a left and right VrHand under the Camera
// Offset (the same parent as the Main Camera) so controller poses and head
// pose share one origin, and forces floor-level tracking so SteamVR's floor
// origin is not stacked on top of the Camera Y Offset.

using Unity.XR.CoreUtils;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class VrRig : MonoBehaviour
    {
        public Transform cameraOffset; // defaults to Main Camera's parent
        public VrHand.GrabButton grabButton = VrHand.GrabButton.Trigger;
        public float grabRadius = 0.06f;
        [Tooltip("SteamVR tracks floor-relative; anything else adds the Camera Y Offset on top and you stand 1 m too tall.")]
        public bool forceFloorTracking = true;

        public VrHand Left { get; private set; }
        public VrHand Right { get; private set; }

        void Start()
        {
            if (forceFloorTracking)
            {
                var origin = GetComponent<XROrigin>();
                if (origin) origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            }

            if (!cameraOffset)
            {
                var cam = Camera.main;
                cameraOffset = cam ? cam.transform.parent : transform;
            }
            Left = Spawn(VrHand.Side.Left);
            Right = Spawn(VrHand.Side.Right);

            Debug.Log($"[DazVrBridge] XR controllers: {VrHand.DescribeDevices()}");
        }

        VrHand Spawn(VrHand.Side side)
        {
            var go = new GameObject(side == VrHand.Side.Left ? "Left Hand" : "Right Hand");
            go.SetActive(false); // let Awake see the settings below
            go.transform.SetParent(cameraOffset, false);
            var hand = go.AddComponent<VrHand>();
            hand.side = side;
            hand.grabButton = grabButton;
            hand.grabRadius = grabRadius;
            go.SetActive(true);
            return hand;
        }
    }
}
