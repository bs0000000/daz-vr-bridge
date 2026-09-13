// Put this on the XR Origin. Spawns a left and right VrHand under the Camera
// Offset (the same parent as the Main Camera) so controller poses and head
// pose share one origin, forces floor-level tracking, and implements the
// world grab: hold the world button (grip by default) on one hand to drag
// yourself through the scene, on both hands to scale and turn as well.
//
// The RIG is what moves and scales, never the Daz scene: Daz coordinates stay
// 1:1 for every sync message. A rig scale of 3 makes you a giant surveying
// the set; 0.3 makes you small enough to pose fingers.

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

        [Header("World grab (the other button)")]
        public bool worldGrabEnabled = true;
        public bool twoHandScale = true;
        public bool twoHandRotate = true;
        public float minScale = 0.1f;
        public float maxScale = 10f;

        public VrHand Left { get; private set; }
        public VrHand Right { get; private set; }
        public float Scale => transform.localScale.x;

        bool _grabbing;
        bool _wasTwo;
        Vector3 _prevL, _prevR;

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

        // Runs after the hands have read their tracking for this frame.
        void LateUpdate()
        {
            if (!worldGrabEnabled || !Left || !Right) { _grabbing = false; return; }

            var l = Left.WorldGrab && !Left.HoldingSomething;
            var r = Right.WorldGrab && !Right.HoldingSomething;
            var two = l && r;
            if (!l && !r) { _grabbing = false; return; }

            var curL = Left.transform.position;
            var curR = Right.transform.position;

            // (Re)anchor whenever the set of grabbing hands changes.
            if (!_grabbing || two != _wasTwo)
            {
                _grabbing = true;
                _wasTwo = two;
                _prevL = curL; _prevR = curR;
                return;
            }

            if (two)
            {
                var prevMid = (_prevL + _prevR) * 0.5f;
                var curMid = (curL + curR) * 0.5f;

                // 1. Drag: keep the midpoint under the hands.
                transform.position -= curMid - prevMid;

                // 2. Turn: hands rotated about the vertical axis -> counter-rotate the rig about the midpoint.
                if (twoHandRotate)
                {
                    var prevDir = Flat(_prevR - _prevL);
                    var curDir = Flat(curR - curL);
                    if (prevDir.sqrMagnitude > 1e-6f && curDir.sqrMagnitude > 1e-6f)
                    {
                        var delta = Vector3.SignedAngle(prevDir, curDir, Vector3.up);
                        transform.RotateAround(prevMid, Vector3.up, -delta);
                    }
                }

                // 3. Scale: hands apart -> world grows -> rig shrinks, about the midpoint.
                if (twoHandScale)
                {
                    var prevDist = Vector3.Distance(_prevL, _prevR);
                    var curDist = Vector3.Distance(curL, curR);
                    if (prevDist > 1e-4f && curDist > 1e-4f)
                    {
                        var factor = prevDist / curDist;
                        var newScale = Mathf.Clamp(Scale * factor, minScale, maxScale);
                        factor = newScale / Scale;
                        transform.position = prevMid + (transform.position - prevMid) * factor;
                        transform.localScale = Vector3.one * newScale;
                    }
                }
            }
            else
            {
                // One hand: drag only.
                var cur = l ? curL : curR;
                var prev = l ? _prevL : _prevR;
                transform.position -= cur - prev;
            }

            // The hands' world positions are back where they were by construction;
            // re-read them so tracking noise does not accumulate.
            _prevL = Left.transform.position;
            _prevR = Right.transform.position;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
