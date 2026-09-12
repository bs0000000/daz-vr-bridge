// Put this on the XR Origin. Spawns a left and right VrHand under the Camera
// Offset (the same parent as the Main Camera) so controller poses and head
// pose share one origin. Nothing else to set up in the scene.

using UnityEngine;

namespace DazVrBridge
{
    public sealed class VrRig : MonoBehaviour
    {
        public Transform cameraOffset; // defaults to Main Camera's parent
        public float grabRadius = 0.06f;

        void Start()
        {
            if (!cameraOffset)
            {
                var cam = Camera.main;
                cameraOffset = cam ? cam.transform.parent : transform;
            }
            Spawn(VrHand.Side.Left);
            Spawn(VrHand.Side.Right);
        }

        void Spawn(VrHand.Side side)
        {
            var go = new GameObject(side == VrHand.Side.Left ? "Left Hand" : "Right Hand");
            go.transform.SetParent(cameraOffset, false);
            var hand = go.AddComponent<VrHand>();
            hand.side = side;
            hand.grabRadius = grabRadius;
        }
    }
}
