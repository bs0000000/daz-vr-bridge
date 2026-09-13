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
        public Vector3 Over;    // how far past its limit each axis is, degrees (0 = inside)
        public Vector3 Slack;   // degrees to the nearest limit; <= 0 means at or past it
        public bool Valid;

        public float Worst => Mathf.Max(Over.x, Mathf.Max(Over.y, Over.z));

        // How pinned the tightest axis is: 1 when it is at (or past) its limit, falling to
        // 0 by `window` degrees away. With clamping on, a joint that has run out of range
        // sits exactly at its limit rather than beyond it, so this is the signal worth
        // showing — "this joint is why the hand stopped following you".
        public float Pinned(float window = 2f)
        {
            var slack = Mathf.Min(Slack.x, Mathf.Min(Slack.y, Slack.z));
            return Mathf.Clamp01(1f - slack / Mathf.Max(0.01f, window));
        }

        public string WorstAxis
        {
            get
            {
                if (Slack.x <= Slack.y && Slack.x <= Slack.z) return "x";
                return Slack.y <= Slack.z ? "y" : "z";
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
            status.Slack = new Vector3(
                Mathf.Min(status.Euler.x - min.x, max.x - status.Euler.x),
                Mathf.Min(status.Euler.y - min.y, max.y - status.Euler.y),
                Mathf.Min(status.Euler.z - min.z, max.z - status.Euler.z));
            return status;
        }

        // Inverse of Check: the bone's Daz world rotation for given Daz X/Y/Z values.
        //   E        = R_a3(v3) R_a2(v2) R_a1(v1)
        //   q_local  = inverse(orient) (x) conj(E) (x) orient
        //   ws_child = q_local (x) ws_parent
        public static Quaternion DazWorldRotFromEuler(SceneLoader loader, SceneLoader.LoadedFigure fig, int boneIndex, Vector3 deg)
        {
            var order = fig.RotOrder[boneIndex];
            var e = AxisQuat(order[2], Component(deg, order[2]))
                  * AxisQuat(order[1], Component(deg, order[1]))
                  * AxisQuat(order[0], Component(deg, order[0]));

            var o = fig.OrientDaz[boneIndex];
            var qLocal = Conj(o) * Conj(e) * o;

            var parent = fig.ParentBone[boneIndex];
            var wsParent = parent >= 0 ? loader.DazWorldRotQ(fig, parent) : Quaternion.identity;
            return qLocal * wsParent;
        }

        // Inverse of SceneLoader.DazWorldRotQ.
        public static void SetDazWorldRot(SceneLoader loader, SceneLoader.LoadedFigure fig, int boneIndex, Quaternion dazRaw)
        {
            var wUnity = new Quaternion(dazRaw.x, dazRaw.y, -dazRaw.z, dazRaw.w);
            fig.Bones[boneIndex].rotation = loader.Root.rotation * wUnity * fig.OrientUnity[boneIndex];
        }

        // Pulls a bone back inside its Daz limits, the way Daz will on commit. Children
        // keep their local transforms, so the rest of the limb follows. Returns how many
        // degrees it had to move, 0 when the bone was already legal (or is unclamped:
        // Daz only enforces limits on properties whose "clamped" flag is set).
        public static float ClampToLimits(SceneLoader loader, SceneLoader.LoadedFigure fig, int boneIndex)
        {
            if (!fig.Clamped[boneIndex]) return 0f;
            var status = Check(loader, fig, boneIndex);
            if (!status.Valid || status.Worst <= 0.01f) return 0f;

            var min = fig.LimitMin[boneIndex];
            var max = fig.LimitMax[boneIndex];
            var clamped = new Vector3(
                Mathf.Clamp(status.Euler.x, min.x, max.x),
                Mathf.Clamp(status.Euler.y, min.y, max.y),
                Mathf.Clamp(status.Euler.z, min.z, max.z));

            SetDazWorldRot(loader, fig, boneIndex, DazWorldRotFromEuler(loader, fig, boneIndex, clamped));
            return status.Worst;
        }

        static Quaternion AxisQuat(char axis, float degrees)
        {
            var half = degrees * 0.5f * Mathf.Deg2Rad;
            var s = Mathf.Sin(half);
            var c = Mathf.Cos(half);
            switch (Axis(axis))
            {
                case 0: return new Quaternion(s, 0f, 0f, c);
                case 1: return new Quaternion(0f, s, 0f, c);
                default: return new Quaternion(0f, 0f, s, c);
            }
        }

        static float Component(Vector3 v, char axis)
        {
            switch (Axis(axis))
            {
                case 0: return v.x;
                case 1: return v.y;
                default: return v.z;
            }
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
