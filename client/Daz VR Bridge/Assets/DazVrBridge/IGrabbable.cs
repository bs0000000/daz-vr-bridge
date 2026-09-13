// Anything a VrHand can hold: bone handles (swing a bone) and node handles
// (move a prop, camera or light). The hand only knows this contract.

using UnityEngine;

namespace DazVrBridge
{
    public interface IGrabbable
    {
        // Distance from a world point to the grabbable surface; the hand picks the smallest.
        float DistanceTo(Vector3 world);
        bool IsGrabbed { get; }
        void SetHover(bool on);
        void BeginGrab(Transform hand);
        void UpdateGrab(Transform hand);
        void EndGrab(Transform hand);
    }
}
