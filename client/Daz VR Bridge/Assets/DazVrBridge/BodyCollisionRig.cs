// Lightweight collision proxies for posed figures.
//
// The proxies are analytic capsules read directly from the current bone positions.
// There are no animated MeshColliders and no physics bodies to update: only the IK
// handle in a controller asks this registry for casts. This keeps the idle cost at zero
// and makes several figures practical.

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BodyCollisionRig : MonoBehaviour
    {
        sealed class Proxy
        {
            public int A, B;
            public int Owner;
            public Vector3 LocalEnd;
            public float Radius;
        }

        static readonly List<BodyCollisionRig> Active = new List<BodyCollisionRig>();
        readonly List<Proxy> _proxies = new List<Proxy>(16);
        SceneLoader.LoadedFigure _figure;
        int _centerBone = -1;
        float _boundsRadius;

        public bool CollisionEnabled = true;
        public SceneLoader.LoadedFigure Figure => _figure;
        public int ProxyCount => _proxies.Count;

        void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        void OnDisable() { Active.Remove(this); }

        public void Init(SceneLoader.LoadedFigure figure)
        {
            _figure = figure;
            _proxies.Clear();

            var scale = BodyScale();
            if (!TryBone("hip", out _centerBone)) _centerBone = -1;
            _boundsRadius = 1.15f * scale;
            var shoulderWidth = Distance("l_upperarm", "r_upperarm", 0.40f * scale);
            var hipWidth = Distance("l_thigh", "r_thigh", 0.20f * scale);
            var chest = Mathf.Clamp(shoulderWidth * 0.38f, 0.11f * scale, 0.20f * scale);
            var pelvis = Mathf.Clamp(hipWidth * 0.70f, 0.10f * scale, 0.18f * scale);

            Add("hip", "spine1", pelvis);
            Add("spine1", "spine3", Mathf.Lerp(pelvis, chest, 0.55f));
            Add("spine3", "neck1", chest);
            AddEnd("head", Mathf.Clamp(Length("head") * 0.48f, 0.085f * scale, 0.13f * scale));

            AddLimb("l_upperarm", "l_forearm", 0.20f, 0.045f, 0.075f, scale);
            AddLimb("r_upperarm", "r_forearm", 0.20f, 0.045f, 0.075f, scale);
            AddLimb("l_forearm", "l_hand", 0.17f, 0.035f, 0.060f, scale);
            AddLimb("r_forearm", "r_hand", 0.17f, 0.035f, 0.060f, scale);
            AddEnd("l_hand", 0.060f * scale);
            AddEnd("r_hand", 0.060f * scale);

            AddLimb("l_thigh", "l_shin", 0.20f, 0.060f, 0.105f, scale);
            AddLimb("r_thigh", "r_shin", 0.20f, 0.060f, 0.105f, scale);
            AddLimb("l_shin", "l_foot", 0.16f, 0.045f, 0.080f, scale);
            AddLimb("r_shin", "r_foot", 0.16f, 0.045f, 0.080f, scale);
            Add("l_foot", "l_toes", 0.065f * scale);
            Add("r_foot", "r_toes", 0.065f * scale);

            Debug.Log($"[DazVrBridge] {figure.Label}: {_proxies.Count} body collision proxies " +
                $"(scale {scale:F2}, torso {pelvis * 100f:F1}–{chest * 100f:F1} cm)");
        }

        float BodyScale()
        {
            if (TryBone("hip", out var hip) && TryBone("head", out var head))
                // G9 hip-to-head-origin is about 54 cm on a life-size figure. Using the
                // full standing height here would shrink every proxy by roughly a third.
                return Mathf.Clamp(Vector3.Distance(RestPosition(hip), RestPosition(head)) / 0.54f, 0.65f, 1.6f);
            return Mathf.Max(0.01f, transform.lossyScale.x);
        }

        float Distance(string a, string b, float fallback)
        {
            return TryBone(a, out var ai) && TryBone(b, out var bi)
                ? Vector3.Distance(RestPosition(ai), RestPosition(bi))
                : fallback;
        }

        Vector3 RestPosition(int i)
            => _figure.Go.transform.TransformPoint(DazSpace.Pos(_figure.BoneJson[i]["origin"]));

        float Length(string bone)
        {
            if (!TryBone(bone, out var i)) return 0f;
            return RestEndLocal(i).magnitude * _figure.Bones[i].lossyScale.x;
        }

        void AddLimb(string a, string b, float fraction, float min, float max, float scale)
        {
            var radius = Mathf.Clamp(Distance(a, b, 0f) * fraction, min * scale, max * scale);
            Add(a, b, radius);
        }

        void Add(string a, string b, float radius)
        {
            if (!TryBone(a, out var ai) || !TryBone(b, out var bi)) return;
            _proxies.Add(new Proxy { A = ai, B = bi, Owner = ai, Radius = radius });
        }

        void AddEnd(string bone, float radius)
        {
            if (!TryBone(bone, out var i)) return;
            _proxies.Add(new Proxy { A = i, B = -1, Owner = i, LocalEnd = RestEndLocal(i), Radius = radius });
        }

        bool TryBone(string id, out int index) => _figure.ByName.TryGetValue(id, out index);

        Vector3 RestEndLocal(int i)
        {
            JToken bone = _figure.BoneJson[i];
            var segment = DazSpace.Pos(bone["end"]) - DazSpace.Pos(bone["origin"]);
            return Quaternion.Inverse(_figure.OrientUnity[i]) * segment;
        }

        void Ends(Proxy proxy, out Vector3 a, out Vector3 b)
        {
            var bone = _figure.Bones[proxy.A];
            a = bone.position;
            b = proxy.B >= 0 ? _figure.Bones[proxy.B].position : bone.TransformPoint(proxy.LocalEnd);
        }

        static bool Excluded(int owner, int a, int b, int c, int d)
            => owner == a || owner == b || owner == c || owner == d;

        public static bool MayHit(Vector3 start, Vector3 direction, float distance, float reach,
            SceneLoader.LoadedFigure movingFigure, int ex0, int ex1, int ex2, int ex3)
        {
            var end = start + direction * distance;
            foreach (var rig in Active)
            {
                if (!rig || !rig.CollisionEnabled || rig._figure == null) continue;
                if (!rig.NearSweep(start, end, reach)) continue;
                foreach (var proxy in rig._proxies)
                {
                    if (rig._figure == movingFigure && Excluded(proxy.Owner, ex0, ex1, ex2, ex3)) continue;
                    rig.Ends(proxy, out var a, out var b);
                    var center = (a + b) * 0.5f;
                    var bound = Vector3.Distance(a, b) * 0.5f + proxy.Radius + reach;
                    if ((ClosestOnSegment(start, end, center) - center).sqrMagnitude <= bound * bound) return true;
                }
            }
            return false;
        }

        public static bool Sweep(Vector3 origin, Vector3 direction, float distance, float sampleRadius,
            SceneLoader.LoadedFigure movingFigure, int ex0, int ex1, int ex2, int ex3,
            out float hitDistance, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            hitDistance = distance;
            hitPoint = hitNormal = Vector3.zero;
            var found = false;
            foreach (var rig in Active)
            {
                if (!rig || !rig.CollisionEnabled || rig._figure == null) continue;
                if (!rig.NearSweep(origin, origin + direction * hitDistance, sampleRadius)) continue;
                foreach (var proxy in rig._proxies)
                {
                    if (rig._figure == movingFigure && Excluded(proxy.Owner, ex0, ex1, ex2, ex3)) continue;
                    rig.Ends(proxy, out var a, out var b);
                    if (!RayCapsule(origin, direction, hitDistance, a, b, proxy.Radius + sampleRadius,
                        out var t, out var normal)) continue;
                    if (Vector3.Dot(direction, normal) >= 0f) continue;
                    hitDistance = t;
                    hitNormal = normal;
                    var centerAtHit = origin + direction * t;
                    var axisPoint = ClosestOnSegment(a, b, centerAtHit);
                    hitPoint = axisPoint + normal * proxy.Radius;
                    found = true;
                }
            }
            return found;
        }

        bool NearSweep(Vector3 start, Vector3 end, float padding)
        {
            if (_centerBone < 0) return true;
            var center = _figure.Bones[_centerBone].position;
            var radius = _boundsRadius + padding;
            return (ClosestOnSegment(start, end, center) - center).sqrMagnitude <= radius * radius;
        }

        static bool RayCapsule(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 a, Vector3 b, float radius, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            var closest = ClosestOnSegment(a, b, origin);
            var fromAxis = origin - closest;
            var inside = fromAxis.sqrMagnitude < radius * radius;
            if (inside)
            {
                if (fromAxis.sqrMagnitude <= 1e-10f) return false;
                normal = fromAxis.normalized;
                return Vector3.Dot(direction, normal) < 0f;
            }

            var ba = b - a;
            var oa = origin - a;
            var baba = Vector3.Dot(ba, ba);
            if (baba < 1e-10f) return RaySphere(origin, direction, maxDistance, a, radius, out distance, out normal);
            var bard = Vector3.Dot(ba, direction);
            var baoa = Vector3.Dot(ba, oa);
            var rdoa = Vector3.Dot(direction, oa);
            var oaoa = Vector3.Dot(oa, oa);
            var aa = baba - bard * bard;
            var bb = baba * rdoa - baoa * bard;
            var cc = baba * oaoa - baoa * baoa - radius * radius * baba;

            var found = false;
            var best = maxDistance;
            if (Mathf.Abs(aa) > 1e-10f)
            {
                var h = bb * bb - aa * cc;
                if (h >= 0f)
                {
                    var t = (-bb - Mathf.Sqrt(h)) / aa;
                    var y = baoa + t * bard;
                    if (t >= 0f && t <= maxDistance && y > 0f && y < baba)
                    {
                        best = t;
                        var p = origin + direction * t;
                        normal = (p - (a + ba * (y / baba))).normalized;
                        found = true;
                    }
                }
            }

            if (RaySphere(origin, direction, best, a, radius, out var capDistance, out var capNormal))
            {
                best = capDistance;
                normal = capNormal;
                found = true;
            }
            if (RaySphere(origin, direction, best, b, radius, out capDistance, out capNormal))
            {
                best = capDistance;
                normal = capNormal;
                found = true;
            }
            distance = best;
            return found;
        }

        static bool RaySphere(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 center, float radius, out float distance, out Vector3 normal)
        {
            var oc = origin - center;
            var b = Vector3.Dot(direction, oc);
            var c = Vector3.Dot(oc, oc) - radius * radius;
            var h = b * b - c;
            if (h < 0f) { distance = 0f; normal = Vector3.zero; return false; }
            distance = -b - Mathf.Sqrt(h);
            if (distance < 0f || distance > maxDistance) { normal = Vector3.zero; return false; }
            normal = (origin + direction * distance - center).normalized;
            return true;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            var ab = b - a;
            var denominator = Vector3.Dot(ab, ab);
            if (denominator < 1e-10f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(point - a, ab) / denominator);
        }
    }
}
