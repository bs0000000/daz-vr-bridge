// Analytic two-bone IK: rotate root and mid so the end bone's origin lands on a
// target. Exact (law of cosines), no iteration, and the bend angle is always in
// [0, pi] so a knee or elbow can never invert — the only freedom is which way the
// joint points, which is the bend plane.
//
// Bend plane, in order of preference:
//   1. the chain's current plane, so a limb keeps the bend the user posed
//   2. the hint carried across frames of a drag, so passing through "straight"
//      (where the current plane is undefined) does not flip the joint
//   3. the rig profile's pole direction (elbows back, knees front)

using UnityEngine;

namespace DazVrBridge
{
    public static class TwoBoneIk
    {
        public static void Solve(Transform root, Transform mid, Transform end,
            Vector3 target, Vector3 poleDir, ref Vector3 bendHint)
        {
            var a = root.position;
            var b = mid.position;
            var c = end.position;

            var lab = Vector3.Distance(a, b);
            var lcb = Vector3.Distance(b, c);
            if (lab < 1e-6f || lcb < 1e-6f) return;

            var at = target - a;
            var lat = at.magnitude;
            if (lat < 1e-6f) return;
            var dir = at / lat;

            // Out of reach: clamp so the law of cosines stays real; the limb ends up
            // straight and pointing at the target.
            lat = Mathf.Clamp(lat, Mathf.Abs(lab - lcb) + 1e-4f, lab + lcb - 1e-4f);

            var bend = Perp(b - a, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Perp(bendHint, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Perp(poleDir, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Perp(Vector3.up, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Perp(Vector3.right, dir);
            bend.Normalize();
            bendHint = bend;

            // Where the middle joint has to sit for both segments to reach.
            var cosRoot = Mathf.Clamp((lat * lat + lab * lab - lcb * lcb) / (2f * lat * lab), -1f, 1f);
            var sinRoot = Mathf.Sqrt(Mathf.Max(0f, 1f - cosRoot * cosRoot));
            var newB = a + dir * (lab * cosRoot) + bend * (lab * sinRoot);

            // Aim root at the new joint, then the mid at the target. Each rotation is a
            // world-space pre-multiply; children follow, so positions are re-read after.
            root.rotation = Quaternion.FromToRotation(b - a, newB - a) * root.rotation;
            mid.rotation = Quaternion.FromToRotation(end.position - mid.position, target - mid.position) * mid.rotation;
        }

        // Component of v perpendicular to a unit direction.
        static Vector3 Perp(Vector3 v, Vector3 unitDir)
        {
            return v - unitDir * Vector3.Dot(v, unitDir);
        }
    }
}
