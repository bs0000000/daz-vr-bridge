// Lightweight collision proxies for posed figures.
//
// The proxies are analytic capsules read directly from the current bone positions.
// There are no animated MeshColliders and no physics bodies to update: only the IK
// handle in a controller asks this registry for casts. This keeps the idle cost at zero
// and makes several figures practical.
//
// Each proxy is an ELLIPTICAL capsule -- two radii across the bone, not one -- and the
// radii are measured from the figure's own skinned vertices rather than guessed from
// bone spacing. Both changes exist for the same reason: a torso is about twice as wide
// as it is deep, so a round capsule has to choose between letting hands sink into the
// ribs and stopping them centimetres off the chest. A guessed radius did the latter.
// The measurement is the same trick a hand already uses on itself before a grab.

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
            public Vector3 LocalEnd;    // segment end in bone-local space, when B < 0
            public Vector3 LocalU;      // one cross-section axis, bone-local; the other is
                                        // derived from it, so the pair rotates with the pose
            public float RadiusU, RadiusV, RadiusAxis;
            public float Bound;         // largest of the three, for the broadphase
            public bool Measured;

            public void SetRadii(float u, float v, float axis)
            {
                RadiusU = u; RadiusV = v; RadiusAxis = axis;
                Bound = Mathf.Max(u, Mathf.Max(v, axis));
            }
        }

        // A high quantile of the cross-section rather than the extreme: one stray vertex
        // from a fitted garment should not inflate a whole limb.
        const float RadiusQuantile = 0.82f;
        const int MinVertsToMeasure = 24;

        static readonly List<BodyCollisionRig> Active = new List<BodyCollisionRig>();
        readonly List<Proxy> _proxies = new List<Proxy>(16);
        SceneLoader.LoadedFigure _figure;
        int _centerBone = -1;
        float _boundsRadius;
        // Radii are measured in world units, so they have to follow the world grab's scale
        // afterwards. Bone positions already do.
        float _measuredScale = 1f;

        public bool CollisionEnabled = true;
        public SceneLoader.LoadedFigure Figure => _figure;
        public int ProxyCount => _proxies.Count;

        float RadiusScale
        {
            get
            {
                if (_figure == null || !_figure.Go || _measuredScale <= 1e-6f) return 1f;
                return _figure.Go.transform.lossyScale.x / _measuredScale;
            }
        }

        void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        void OnDisable() { Active.Remove(this); }

        public void Init(SceneLoader.LoadedFigure figure)
        {
            _figure = figure;
            _proxies.Clear();

            var scale = BodyScale();
            _measuredScale = figure.Go.transform.lossyScale.x;
            if (!TryBone("hip", out _centerBone)) _centerBone = -1;
            _boundsRadius = 1.15f * scale;

            // Starting guesses. Every one is replaced by a measurement when the mesh has
            // enough vertices for that proxy; they survive only as the fallback for a
            // figure that arrived without a body mesh.
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

            var measured = MeasureFromMesh();

            var torso = _proxies.Count > 2 ? _proxies[2] : null;
            Debug.Log($"[DazVrBridge] {figure.Label}: {_proxies.Count} body collision proxies, " +
                $"{measured} measured from the mesh (scale {scale:F2}" +
                (torso != null ? $", chest {torso.RadiusU * 200f:F0} x {torso.RadiusV * 200f:F0} cm" : "") + ")");
        }

        // ---- measurement

        // Every vertex is assigned to the proxy whose flesh it belongs to, then projected
        // onto the two axes across that proxy's bone. The spread in each axis, separately,
        // is the ellipse. Once per figure, at load.
        int MeasureFromMesh()
        {
            if (_proxies.Count == 0) return 0;

            SkinnedMeshRenderer smr = null;
            foreach (var s in _figure.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (s.sharedMesh != null && (!smr || s.sharedMesh.vertexCount > smr.sharedMesh.vertexCount)) smr = s;
            if (!smr) return 0;

            // A bone belongs to the nearest proxy at or above it: spine2's flesh is part of
            // the spine1-to-spine3 capsule, a finger's is part of the hand's.
            var owners = new Dictionary<int, int>(_proxies.Count);
            for (var p = 0; p < _proxies.Count; p++) owners[_proxies[p].Owner] = p;
            var proxyOf = new int[_figure.Bones.Length];
            for (var b = 0; b < proxyOf.Length; b++)
            {
                proxyOf[b] = -1;
                for (var p = b; p >= 0; p = _figure.ParentBone[p])
                    if (owners.TryGetValue(p, out var found)) { proxyOf[b] = found; break; }
            }

            var count = _proxies.Count;
            var originW = new Vector3[count];
            var axisW = new Vector3[count];
            var uW = new Vector3[count];
            var vW = new Vector3[count];
            var lengthW = new float[count];
            for (var p = 0; p < count; p++)
            {
                Ends(_proxies[p], out var a, out var b);
                Frame(_proxies[p], a, b, out uW[p], out vW[p], out axisW[p]);
                originW[p] = a;
                lengthW[p] = Vector3.Distance(a, b);
            }

            var across = new List<float>[count];
            var through = new List<float>[count];
            var overhang = new List<float>[count];
            for (var p = 0; p < count; p++)
            {
                across[p] = new List<float>(512);
                through[p] = new List<float>(512);
                overhang[p] = new List<float>(64);
            }

            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var verts = baked.vertices;
            var perVertex = smr.sharedMesh.GetBonesPerVertex();
            var weights = smr.sharedMesh.GetAllBoneWeights();
            var toWorld = smr.transform.localToWorldMatrix;

            var wi = 0;
            for (var v = 0; v < perVertex.Length && v < verts.Length; v++)
            {
                var dominant = -1;
                var best = 0.5f;
                for (var k = 0; k < perVertex[v]; k++, wi++)
                {
                    var bw = weights[wi];
                    if (bw.weight > best) { best = bw.weight; dominant = bw.boneIndex; }
                }
                if (dominant < 0 || dominant >= proxyOf.Length) continue;
                var p = proxyOf[dominant];
                if (p < 0) continue;

                var rel = toWorld.MultiplyPoint3x4(verts[v]) - originW[p];
                var along = Vector3.Dot(rel, axisW[p]);
                if (along < 0f) overhang[p].Add(-along);
                else if (along > lengthW[p]) overhang[p].Add(along - lengthW[p]);
                else
                {
                    var radial = rel - axisW[p] * along;
                    across[p].Add(Mathf.Abs(Vector3.Dot(radial, uW[p])));
                    through[p].Add(Mathf.Abs(Vector3.Dot(radial, vW[p])));
                }
            }
            perVertex.Dispose();
            weights.Dispose();
            Destroy(baked);

            var measured = 0;
            for (var p = 0; p < count; p++)
            {
                if (across[p].Count < MinVertsToMeasure) continue;
                var ru = Quantile(across[p], RadiusQuantile);
                var rv = Quantile(through[p], RadiusQuantile);
                if (ru < 1e-4f || rv < 1e-4f) continue;
                // The caps: how far flesh reaches past the joint, which is the whole point
                // for the skull and the heel. Never smaller than the cross-section, or the
                // capsule would end in a disc.
                var cap = Mathf.Max(Quantile(overhang[p], RadiusQuantile), 0.3f * (ru + rv));
                _proxies[p].SetRadii(ru, rv, cap);
                _proxies[p].Measured = true;
                measured++;
            }
            return measured;
        }

        static float Quantile(List<float> values, float q)
        {
            if (values.Count == 0) return 0f;
            values.Sort();
            var i = Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * q), 0, values.Count - 1);
            return values[i];
        }

        // ---- construction

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
            var proxy = new Proxy { A = ai, B = bi, Owner = ai, LocalU = SeedAxis(SegmentLocal(ai, bi)) };
            proxy.SetRadii(radius, radius, radius);
            _proxies.Add(proxy);
        }

        void AddEnd(string bone, float radius)
        {
            if (!TryBone(bone, out var i)) return;
            var end = RestEndLocal(i);
            var proxy = new Proxy { A = i, B = -1, Owner = i, LocalEnd = end, LocalU = SeedAxis(end) };
            proxy.SetRadii(radius, radius, radius);
            _proxies.Add(proxy);
        }

        // Some direction across the bone. Which one does not matter -- both radii are
        // measured along whatever pair this produces -- but it has to be stable and not
        // nearly parallel to the bone, or the frame flips as the figure moves.
        static Vector3 SeedAxis(Vector3 segmentLocal)
        {
            var n = segmentLocal.sqrMagnitude > 1e-10f ? segmentLocal.normalized : Vector3.up;
            var seed = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            var u = Vector3.ProjectOnPlane(seed, n);
            return u.sqrMagnitude > 1e-8f ? u.normalized : Vector3.forward;
        }

        Vector3 SegmentLocal(int a, int b)
            => Quaternion.Inverse(_figure.OrientUnity[a])
                * (DazSpace.Pos(_figure.BoneJson[b]["origin"]) - DazSpace.Pos(_figure.BoneJson[a]["origin"]));

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

        // The live cross-section frame: the bone's seed axis carried by the pose, made
        // perpendicular to the segment as it currently lies.
        void Frame(Proxy proxy, Vector3 a, Vector3 b, out Vector3 u, out Vector3 v, out Vector3 w)
        {
            w = b - a;
            var length = w.magnitude;
            w = length > 1e-6f ? w / length : _figure.Bones[proxy.A].up;
            u = Vector3.ProjectOnPlane(_figure.Bones[proxy.A].rotation * proxy.LocalU, w);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.ProjectOnPlane(Vector3.right, w);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.ProjectOnPlane(Vector3.up, w);
            u = u.normalized;
            v = Vector3.Cross(w, u);
        }

        static bool Excluded(int owner, int a, int b, int c, int d)
            => owner == a || owner == b || owner == c || owner == d;

        // ---- queries

        public static bool MayHit(Vector3 start, Vector3 direction, float distance, float reach,
            SceneLoader.LoadedFigure movingFigure, int ex0, int ex1, int ex2, int ex3)
        {
            var end = start + direction * distance;
            foreach (var rig in Active)
            {
                if (!rig || !rig.CollisionEnabled || rig._figure == null) continue;
                if (!rig.NearSweep(start, end, reach)) continue;
                var k = rig.RadiusScale;
                foreach (var proxy in rig._proxies)
                {
                    if (rig._figure == movingFigure && Excluded(proxy.Owner, ex0, ex1, ex2, ex3)) continue;
                    rig.Ends(proxy, out var a, out var b);
                    var center = (a + b) * 0.5f;
                    var bound = Vector3.Distance(a, b) * 0.5f + proxy.Bound * k + reach;
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
                var k = rig.RadiusScale;
                foreach (var proxy in rig._proxies)
                {
                    if (rig._figure == movingFigure && Excluded(proxy.Owner, ex0, ex1, ex2, ex3)) continue;
                    rig.Ends(proxy, out var a, out var b);
                    rig.Frame(proxy, a, b, out var u, out var v, out var w);
                    if (!RayEllipticalCapsule(origin, direction, hitDistance, a, b, u, v, w,
                        proxy.RadiusU * k + sampleRadius,
                        proxy.RadiusV * k + sampleRadius,
                        proxy.RadiusAxis * k + sampleRadius,
                        out var t, out var normal)) continue;
                    if (Vector3.Dot(direction, normal) >= 0f) continue;
                    hitDistance = t;
                    hitNormal = normal;
                    // The sample's centre at contact, pulled back onto the skin it touched.
                    hitPoint = origin + direction * t - normal * sampleRadius;
                    found = true;
                }
            }
            return found;
        }

        bool NearSweep(Vector3 start, Vector3 end, float padding)
        {
            if (_centerBone < 0) return true;
            var center = _figure.Bones[_centerBone].position;
            var radius = _boundsRadius * RadiusScale + padding;
            return (ClosestOnSegment(start, end, center) - center).sqrMagnitude <= radius * radius;
        }

        // ---- geometry

        // Ray against an elliptical capsule, by warping space until the capsule is round.
        // The warp is diagonal in the capsule's own frame, so a point goes in as
        // (dot/ru, dot/rv, dot/rw) and a normal comes back out the same way: for a
        // diagonal map the inverse transpose is the map itself. The warped direction is
        // renormalised because RayCapsule's quadratic assumes a unit direction, and the
        // distance is scaled back afterwards.
        static bool RayEllipticalCapsule(Vector3 origin, Vector3 direction, float maxDistance,
            Vector3 a, Vector3 b, Vector3 u, Vector3 v, Vector3 w,
            float ru, float rv, float rw, out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;
            if (ru < 1e-6f || rv < 1e-6f || rw < 1e-6f) return false;

            var rel = origin - a;
            var o = new Vector3(Vector3.Dot(rel, u) / ru, Vector3.Dot(rel, v) / rv, Vector3.Dot(rel, w) / rw);
            var d = new Vector3(Vector3.Dot(direction, u) / ru, Vector3.Dot(direction, v) / rv, Vector3.Dot(direction, w) / rw);
            var scale = d.magnitude;
            if (scale < 1e-9f) return false;
            d /= scale;

            var end = new Vector3(0f, 0f, Vector3.Distance(a, b) / rw);
            if (!RayCapsule(o, d, maxDistance * scale, Vector3.zero, end, 1f, out var t, out var n)) return false;

            distance = t / scale;
            if (distance > maxDistance) return false;
            var back = u * (n.x / ru) + v * (n.y / rv) + w * (n.z / rw);
            if (back.sqrMagnitude < 1e-12f) return false;
            normal = back.normalized;
            return true;
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
