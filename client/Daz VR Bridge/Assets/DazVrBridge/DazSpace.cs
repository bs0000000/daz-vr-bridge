// The one place that knows Daz Studio's conventions.
//
//   Daz:   centimeters, right-handed, +Y up
//   Unity: meters,      left-handed,  +Y up
//
// Conversion is a Z mirror plus a scale. Under a Z mirror a rotation's
// quaternion maps (x, y, z, w) -> (-x, -y, z, w), and triangle winding flips.
// Everything else in the client works purely in Unity space.

using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public static class DazSpace
    {
        public const float CmToM = 0.01f;

        public static Vector3 Pos(float xCm, float yCm, float zCm)
        {
            return new Vector3(xCm * CmToM, yCm * CmToM, -zCm * CmToM);
        }

        public static Vector3 Pos(JToken arr)
        {
            return Pos(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
        }

        public static Vector3 Scale(JToken arr)
        {
            return new Vector3(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
        }

        public static Quaternion Rot(float x, float y, float z, float w)
        {
            return new Quaternion(-x, -y, z, w);
        }

        public static Quaternion Rot(JToken arr)
        {
            return Rot(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>(), arr[3].Value<float>());
        }

        // Back to Daz for anything we send (Phase 2).
        public static float[] ToDazPos(Vector3 p)
        {
            return new[] { p.x / CmToM, p.y / CmToM, -p.z / CmToM };
        }

        public static float[] ToDazRot(Quaternion q)
        {
            return new[] { -q.x, -q.y, q.z, q.w };
        }
    }
}
