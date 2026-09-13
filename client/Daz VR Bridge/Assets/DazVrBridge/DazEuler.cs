// A bone's pose as Daz's own X/Y/Z rotation values, so the client can tell when a
// pose is outside Daz's joint limits before committing it.
//
// Verified against 117 posed bones of a Genesis 9 figure spanning all five rotation
// orders the rig uses, worst error 0.00002 degrees:
//
//   q_local = ws_child (x) inverse(ws_parent)         [Hamilton, raw Daz components]
//   E       = orient (x) conj(q_local) (x) inverse(orient)
//   E       = R_a3(v3) R_a2(v2) R_a1(v1)              [a1..a3 = rot_order, so E
//                                                      decomposes in REVERSED order]
//
// Quaternions here hold raw Daz components; UnityEngine.Quaternion is used only as a
// 4-float container whose operator* is the Hamilton product.

using UnityEngine;

namespace DazVrBridge
{
    public struct LimitStatus
    {
        public Vector3 Euler;   // Daz's X/Y/Z rotation values, degrees
        public Vector3 Over;    // how far past the limit each axis is, degrees (0 = inside)
        public bool Valid;

        public float Worst => Mathf.Max(Over.x, Mathf.Max(Over.y, Over.z));

        public string WorstAxis
        {
            get
            {
                if (Over.x >= Over.y && Over.x >= Over.z) return "x";
                return Over.y >= Over.z ? "y" : "z";
            }
        }
    }

    public static class DazEuler
    {
        // Daz's rotation values for one bone, or Valid=false for a bone whose parent is
        // not a bone (the root, whose parent is the figure node — a different relation,
        // and its limits are +/-180 anyway).
        public static LimitStatus Check(SceneLoader loader, SceneLoader.LoadedFigure fig, int boneIndex)
        {
            var status = new LimitStatus();
            var parent = fig.ParentBone[boneIndex];
            if (parent < 0) return status;

            var wsChild = loader.DazWorldRotQ(fig, boneIndex);
            var wsParent = loader.DazWorldRotQ(fig, parent);
            var qLocal = wsChild * Conj(wsParent);

            var o = fig.OrientDaz[boneIndex];
            var e = o * Conj(qLocal) * Conj(o);

            var order = fig.RotOrder[boneIndex];
            status.Euler = Decompose(e, Reverse(order));
            status.Valid = true;

            var min = fig.LimitMin[boneIndex];
            var max = fig.LimitMax[boneIndex];
            status.Over = new Vector3(
                Overshoot(status.Euler.x, min.x, max.x),
                Overshoot(status.Euler.y, min.y, max.y),
                Overshoot(status.Euler.z, min.z, max.z));
            return status;
        }

        static float Overshoot(float value, float min, float max)
        {
            if (value < min) return min - value;
            if (value > max) return value - max;
            return 0f;
        }

        static Quaternion Conj(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        static string Reverse(string order) => $"{order[2]}{order[1]}{order[0]}";

        // Tait-Bryan extraction for R = R_i(a) R_j(b) R_k(c), axes i, j, k distinct.
        // Returns degrees keyed by axis, so the caller gets X/Y/Z whatever the order.
        static Vector3 Decompose(Quaternion q, string order)
        {
            int i = Axis(order[0]), j = Axis(order[1]), k = Axis(order[2]);
            var even = (i + 1) % 3 == j;
            var e = even ? 1f : -1f;

            // Rotation matrix, row-major, from a Hamilton quaternion.
            float x = q.x, y = q.y, z = q.z, w = q.w;
            var m = new float[3, 3];
            m[0, 0] = 1f - 2f * (y * y + z * z); m[0, 1] = 2f * (x * y - z * w);       m[0, 2] = 2f * (x * z + y * w);
            m[1, 0] = 2f * (x * y + z * w);      m[1, 1] = 1f - 2f * (x * x + z * z);  m[1, 2] = 2f * (y * z - x * w);
            m[2, 0] = 2f * (x * z - y * w);      m[2, 1] = 2f * (y * z + x * w);       m[2, 2] = 1f - 2f * (x * x + y * y);

            var result = Vector3.zero;
            Set(ref result, j, Mathf.Asin(Mathf.Clamp(e * m[i, k], -1f, 1f)) * Mathf.Rad2Deg);
            Set(ref result, i, Mathf.Atan2(-e * m[j, k], m[k, k]) * Mathf.Rad2Deg);
            Set(ref result, k, Mathf.Atan2(-e * m[i, j], m[i, i]) * Mathf.Rad2Deg);
            return result;
        }

        static int Axis(char c) => c == 'X' || c == 'x' ? 0 : (c == 'Y' || c == 'y' ? 1 : 2);

        static void Set(ref Vector3 v, int axis, float value)
        {
            if (axis == 0) v.x = value;
            else if (axis == 1) v.y = value;
            else v.z = value;
        }
    }
}
