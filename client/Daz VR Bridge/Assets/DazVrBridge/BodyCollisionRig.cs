// Lightweight collision proxies for posed figures.
//
// Only the IK handle in a controller ever asks this registry for a cast. There are no
// animated MeshColliders and no physics bodies to update, so the idle cost is zero and
// several figures stay practical.
//
// Each proxy is a tapered, elliptical tube around one bone, measured from the figure's
// own skinned vertices. Both of those words were paid for:
//
//   elliptical -- a torso is about twice as wide as it is deep, so a round capsule must
//                 either let hands sink into the ribs or stop them proud of the chest.
//   tapered    -- and a single cross-section per bone is no better, because the
//                 spine1-to-spine3 segment runs from the waist to the upper chest. One
//                 radius for the whole span is the chest's, and a hand laid on the belly
//                 then stops a hand's width out. The cross-section is now sampled at
//                 six stations along each bone and interpolated between them.
//
// The surface is a measured table rather than a formula, so the ray test marches the
// implicit field and bisects onto the crossing instead of solving in closed form. That
// costs a few dozen dot products for the one hand that is being dragged.
//
// BodyCollisionRig.ShowShapes draws the result as wireframe rings -- the fastest way to
// settle whether a hand stops early because the shape is wrong or because something
// else is.

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BodyCollisionRig : MonoBehaviour
    {
        // Cross-sections measured along each bone. Six is enough to separate a waist from
        // a chest and a hip from a knee without turning the measurement noisy.
        const int Stations = 6;
        // A high quantile rather than the extreme: one stray vertex from a fitted garment
        // should not inflate a whole limb.
        const float RadiusQuantile = 0.82f;
        const int MinPerStation = 12;

        sealed class Proxy
        {
            public int A, B;
            public int Owner;
            public Vector3 LocalStart;      // segment start in bone-local space (usually the origin)
            public Vector3 LocalEnd;        // segment end in bone-local space, when B < 0
            public Vector3 LocalU;          // one cross-section axis, bone-local; the other
                                            // is derived, so the pair rotates with the pose
            public readonly float[] Ru = new float[Stations];
            public readonly float[] Rv = new float[Stations];
            // Where the cross-section actually sits relative to the bone. A spine runs down
            // the back of a torso, not its middle, so a tube centred on the bone with
            // symmetric radii hangs out behind the body and falls short in front.
            public readonly float[] Cu = new float[Stations];
            public readonly float[] Cv = new float[Stations];
            public float RadiusAxis;        // the end caps
            public float Bound;             // largest radius anywhere, for the broadphase
            public bool Measured;

            public void SetUniform(float r)
            {
                for (var i = 0; i < Stations; i++) { Ru[i] = r; Rv[i] = r; Cu[i] = 0f; Cv[i] = 0f; }
                RadiusAxis = r;
                Bound = r;
            }

            public void Rebound()
            {
                // Has to cover the offset as well as the radius, or the broadphase rejects
                // rays that would have hit the far side of an off-centre section.
                Bound = RadiusAxis;
                for (var i = 0; i < Stations; i++)
                    Bound = Mathf.Max(Bound, Mathf.Max(Mathf.Abs(Cu[i]) + Ru[i], Mathf.Abs(Cv[i]) + Rv[i]));
            }

            // The cross-section a fraction t of the way along the bone.
            public void Radii(float t, out float ru, out float rv, out float cu, out float cv)
            {
                var s = Mathf.Clamp01(t) * (Stations - 1);
                var i = Mathf.Min((int)s, Stations - 2);
                var f = s - i;
                ru = Mathf.Lerp(Ru[i], Ru[i + 1], f);
                rv = Mathf.Lerp(Rv[i], Rv[i + 1], f);
                cu = Mathf.Lerp(Cu[i], Cu[i + 1], f);
                cv = Mathf.Lerp(Cv[i], Cv[i + 1], f);
            }
        }

        // The ray test's working set: one proxy resolved into world space.
        struct Shape
        {
            public Proxy P;
            public Vector3 A, U, V, W;
            public float Length, K, Extra;
        }

        /// Draw every proxy as wireframe rings. A diagnostic, off by default.
        public static bool ShowShapes;

        static readonly List<BodyCollisionRig> Active = new List<BodyCollisionRig>();
        readonly List<Proxy> _proxies = new List<Proxy>(16);
        SceneLoader.LoadedFigure _figure;
        int _centerBone = -1;
        float _boundsRadius;
        // Radii are measured in world units, so they have to follow the world grab's scale
        // afterwards. Bone positions already do.
        float _measuredScale = 1f;
        string _bodyMesh = "none";   // which mesh the profile came from, for the load report

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

            var torso = _proxies.Count > 1 ? _proxies[1] : null;
            var report = $"[DazVrBridge] {figure.Label}: {_proxies.Count} body proxies, {measured} measured " +
                $"from {_bodyMesh} (scale {scale:F2})";
            if (torso != null)
                report += $"; torso width x depth along the spine: " +
                    $"{torso.Ru[0] * 200f:F0}x{torso.Rv[0] * 200f:F0} -> " +
                    $"{torso.Ru[Stations / 2] * 200f:F0}x{torso.Rv[Stations / 2] * 200f:F0} -> " +
                    $"{torso.Ru[Stations - 1] * 200f:F0}x{torso.Rv[Stations - 1] * 200f:F0} cm";
            Debug.Log(report);
        }

        // ---- measurement

        // Every vertex is assigned to the proxy whose flesh it belongs to, binned by how
        // far along that bone it sits, and projected onto the two axes across the bone.
        // The spread in each axis, per bin, is the profile. Once per figure, at load.
        // The body mesh, chosen by how much of the skeleton it touches. Vertex count is
        // the wrong question: an HD garment or a hair prop can easily out-resolve the
        // body, and a mesh whose skin asset never arrived carries no bone weights at all,
        // so picking the biggest one silently measured nothing on three figures out of
        // four. A body reaches the whole rig; hair reaches the head.
        SkinnedMeshRenderer PickBodyMesh(out int bonesTouched, out int vertices)
        {
            SkinnedMeshRenderer best = null;
            bonesTouched = 0;
            vertices = 0;
            var seen = new HashSet<int>();
            foreach (var s in _figure.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (s.sharedMesh == null) continue;
                var per = s.sharedMesh.GetBonesPerVertex();
                if (per.Length == 0) { per.Dispose(); continue; }   // no skin: nothing to learn
                var w = s.sharedMesh.GetAllBoneWeights();
                seen.Clear();
                for (var i = 0; i < w.Length; i++) if (w[i].weight > 0.05f) seen.Add(w[i].boneIndex);
                per.Dispose();
                w.Dispose();
                if (seen.Count <= bonesTouched) continue;
                bonesTouched = seen.Count;
                vertices = s.sharedMesh.vertexCount;
                best = s;
            }
            return best;
        }

        // Every vertex is assigned to the proxy whose flesh it belongs to, then projected
        // onto the two axes across that bone and binned by how far along it sits. The
        // spread in each axis, per bin, is the profile. Once per figure, at load.
        int MeasureFromMesh()
        {
            if (_proxies.Count == 0) return 0;

            var smr = PickBodyMesh(out var bonesTouched, out var vertexCount);
            _bodyMesh = smr ? $"{vertexCount} verts over {bonesTouched} bones" : "none with bone weights";
            if (!smr)
            {
                Debug.LogWarning($"[DazVrBridge] {_figure.Label}: no skinned mesh carries bone weights, " +
                    "so contact shapes fall back to guesses. Was the skin asset requested?");
                return 0;
            }

            // A bone belongs to the nearest proxy at or above it: spine2's flesh is part of
            // the spine1-to-spine3 tube, a finger's is part of the hand's.
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

            // Kept per vertex rather than binned straight away: an end proxy cannot be
            // binned until its own extent is known, and that comes from these.
            var alongs = new List<float>[count];
            var across = new List<float>[count];
            var through = new List<float>[count];
            for (var p = 0; p < count; p++)
            {
                alongs[p] = new List<float>(1024);
                across[p] = new List<float>(1024);
                through[p] = new List<float>(1024);
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
                // Whichever bone pulls hardest on this vertex, however weak that is. An
                // earlier "more than half" test threw away most of a mesh skinned across
                // eight influences.
                var dominant = -1;
                var best = 0f;
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
                var radial = rel - axisW[p] * along;
                alongs[p].Add(along);
                across[p].Add(Vector3.Dot(radial, uW[p]));
                through[p].Add(Vector3.Dot(radial, vW[p]));
            }
            perVertex.Dispose();
            weights.Dispose();
            Destroy(baked);

            var measured = 0;
            var starved = new List<string>();
            var ru = new float[Stations];
            var rv = new float[Stations];
            var cu = new float[Stations];
            var cv = new float[Stations];
            var have = new bool[Stations];
            var binU = new List<float>[Stations];
            var binV = new List<float>[Stations];
            for (var s = 0; s < Stations; s++) { binU[s] = new List<float>(256); binV[s] = new List<float>(256); }
            var overhang = new List<float>(256);
            var sorted = new List<float>(1024);

            for (var p = 0; p < count; p++)
            {
                var proxy = _proxies[p];
                if (alongs[p].Count < MinPerStation * 2) { starved.Add(BoneId(proxy.A)); continue; }

                float lo = 0f, hi = lengthW[p];
                if (proxy.B < 0)
                {
                    // An end proxy -- head, hand, foot -- has no second bone to say where
                    // the flesh stops, and its own segment is far shorter than that flesh:
                    // the head bone runs roughly jaw to eye level while the skull carries
                    // on past both ends. Measured against that stub, everything above and
                    // below collapsed into one spherical cap, which is why a head read as
                    // a stack of discs across the face. Take the extent from the vertices.
                    sorted.Clear();
                    sorted.AddRange(alongs[p]);
                    sorted.Sort();
                    lo = sorted[Mathf.Clamp(Mathf.RoundToInt((sorted.Count - 1) * 0.01f), 0, sorted.Count - 1)];
                    hi = sorted[Mathf.Clamp(Mathf.RoundToInt((sorted.Count - 1) * 0.99f), 0, sorted.Count - 1)];
                    if (hi - lo < 1e-4f) { lo = 0f; hi = Mathf.Max(1e-3f, lengthW[p]); }
                }
                var span = Mathf.Max(1e-4f, hi - lo);

                for (var s = 0; s < Stations; s++) { binU[s].Clear(); binV[s].Clear(); }
                overhang.Clear();
                for (var i = 0; i < alongs[p].Count; i++)
                {
                    var t = (alongs[p][i] - lo) / span;
                    if (t < 0f) { overhang.Add(-t * span); continue; }
                    if (t > 1f) { overhang.Add((t - 1f) * span); continue; }
                    var station = Mathf.Clamp(Mathf.RoundToInt(t * (Stations - 1)), 0, Stations - 1);
                    binU[station].Add(across[p][i]);
                    binV[station].Add(through[p][i]);
                }

                var any = false;
                for (var s = 0; s < Stations; s++)
                {
                    have[s] = binU[s].Count >= MinPerStation;
                    if (!have[s]) continue;
                    // Both edges of the section, so it can be centred as well as sized. The
                    // quantiles are trimmed rather than taken at the extremes, which keeps
                    // a stray vertex from dragging one side outwards.
                    Extent(binU[s], out cu[s], out ru[s]);
                    Extent(binV[s], out cv[s], out rv[s]);
                    any |= ru[s] > 1e-4f && rv[s] > 1e-4f;
                }
                if (!any) { starved.Add(BoneId(proxy.A)); continue; }

                // A station the mesh was too thin to fill borrows its nearest filled
                // neighbour, so a gap never collapses the tube to nothing.
                FillGaps(ru, have);
                FillGaps(rv, have);
                FillGaps(cu, have);
                FillGaps(cv, have);
                // One smoothing pass: the quantile is noisy bin to bin, and a body is not.
                Smooth(ru);
                Smooth(rv);
                Smooth(cu);
                Smooth(cv);

                for (var s = 0; s < Stations; s++)
                {
                    proxy.Ru[s] = ru[s]; proxy.Rv[s] = rv[s];
                    proxy.Cu[s] = cu[s]; proxy.Cv[s] = cv[s];
                }
                // The caps round off the ends rather than leaving a flat disc.
                proxy.RadiusAxis = Mathf.Max(Quantile(overhang, RadiusQuantile),
                    0.35f * (ru[Stations - 1] + rv[Stations - 1]));

                if (proxy.B < 0)
                {
                    // Move the segment onto what was actually measured. The direction is
                    // unchanged, so the cross-section frame stays where it was.
                    var dir = proxy.LocalEnd.sqrMagnitude > 1e-10f ? proxy.LocalEnd.normalized : Vector3.up;
                    var boneScale = Mathf.Max(1e-4f, _figure.Bones[proxy.A].lossyScale.x);
                    proxy.LocalStart = dir * (lo / boneScale);
                    proxy.LocalEnd = dir * (hi / boneScale);
                }

                proxy.Rebound();
                proxy.Measured = true;
                measured++;
            }

            if (starved.Count > 0)
                Debug.LogWarning($"[DazVrBridge] {_figure.Label}: {starved.Count} contact proxies had too few " +
                    $"vertices and kept their guessed size ({string.Join(", ", starved)}).");
            return measured;
        }

        string BoneId(int bone) => _figure.BoneJson[bone].Value<string>("id");


        static void FillGaps(float[] values, bool[] have)
        {
            for (var i = 0; i < Stations; i++)
            {
                if (have[i]) continue;
                var nearest = -1;
                for (var d = 1; d < Stations; d++)
                {
                    if (i - d >= 0 && have[i - d]) { nearest = i - d; break; }
                    if (i + d < Stations && have[i + d]) { nearest = i + d; break; }
                }
                if (nearest >= 0) values[i] = values[nearest];
            }
        }

        static void Smooth(float[] values)
        {
            var copy = new float[Stations];
            System.Array.Copy(values, copy, Stations);
            for (var i = 0; i < Stations; i++)
            {
                var a = copy[Mathf.Max(0, i - 1)];
                var b = copy[i];
                var c = copy[Mathf.Min(Stations - 1, i + 1)];
                values[i] = (a + 2f * b + c) * 0.25f;
            }
        }

        // The trimmed span of a set of signed offsets, as a centre and a half-width.
        static void Extent(List<float> values, out float center, out float radius)
        {
            values.Sort();
            var last = values.Count - 1;
            var lo = values[Mathf.Clamp(Mathf.RoundToInt(last * (1f - RadiusQuantile)), 0, last)];
            var hi = values[Mathf.Clamp(Mathf.RoundToInt(last * RadiusQuantile), 0, last)];
            center = (lo + hi) * 0.5f;
            radius = Mathf.Max(1e-4f, (hi - lo) * 0.5f);
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
            var proxy = new Proxy { A = ai, B = bi, Owner = ai, LocalU = SeedAxis(ai, SegmentLocal(ai, bi)) };
            proxy.SetUniform(radius);
            _proxies.Add(proxy);
        }

        void AddEnd(string bone, float radius)
        {
            if (!TryBone(bone, out var i)) return;
            var end = RestEndLocal(i);
            var proxy = new Proxy { A = i, B = -1, Owner = i, LocalEnd = end, LocalU = SeedAxis(i, end) };
            proxy.SetUniform(radius);
            _proxies.Add(proxy);
        }

        static readonly Vector3[] SeedPreference = { Vector3.right, Vector3.forward, Vector3.up };

        // The cross-section axes have to be the body's own -- width across, depth front to
        // back -- not just any pair perpendicular to the bone. The measurement records the
        // extent along each axis and then treats the pair as an ellipse, and an extent is
        // a bounding box: measured on a frame skewed to the body, a torso comes out as a
        // box rotated off its own axes, which bulges out past the skin on the diagonal.
        Vector3 SeedAxis(int bone, Vector3 segmentLocal)
        {
            var n = segmentLocal.sqrMagnitude > 1e-10f ? segmentLocal.normalized : Vector3.up;
            var toLocal = Quaternion.Inverse(_figure.OrientUnity[bone]);
            // Figure right, then forward, then up: the first that is not nearly along the
            // bone, so the projection is never degenerate.
            foreach (var axis in SeedPreference)
            {
                var candidate = Vector3.ProjectOnPlane(toLocal * axis, n);
                if (candidate.sqrMagnitude > 0.25f) return candidate.normalized;
            }
            var fallback = Vector3.ProjectOnPlane(Vector3.right, n);
            return fallback.sqrMagnitude > 1e-8f ? fallback.normalized : Vector3.forward;
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
            a = bone.TransformPoint(proxy.LocalStart);
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

        Shape ShapeOf(Proxy proxy, float extra)
        {
            Ends(proxy, out var a, out var b);
            Frame(proxy, a, b, out var u, out var v, out var w);
            return new Shape
            {
                P = proxy, A = a, U = u, V = v, W = w,
                Length = Vector3.Distance(a, b), K = RadiusScale, Extra = extra,
            };
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

                    // Cheap reject first: the ray against a round capsule at this proxy's
                    // widest radius. Only what survives is worth marching.
                    rig.Ends(proxy, out var a, out var b);
                    var center = (a + b) * 0.5f;
                    var bound = Vector3.Distance(a, b) * 0.5f + proxy.Bound * k + sampleRadius;
                    if ((ClosestOnSegment(origin, origin + direction * hitDistance, center) - center).sqrMagnitude > bound * bound) continue;

                    var shape = rig.ShapeOf(proxy, sampleRadius);
                    if (!March(shape, origin, direction, hitDistance, out var t, out var normal)) continue;
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

        // ---- the implicit surface

        // Negative inside the tube, zero on its skin. The cross-section is an ellipse that
        // changes along the bone, and past either end the axial term rounds it off into a
        // cap rather than leaving a flat disc.
        static float Field(in Shape s, Vector3 p)
        {
            var rel = p - s.A;
            var along = Vector3.Dot(rel, s.W);
            var t = s.Length > 1e-6f ? Mathf.Clamp01(along / s.Length) : 0f;
            s.P.Radii(t, out var ru, out var rv, out var cu, out var cv);
            ru = Mathf.Max(1e-5f, ru * s.K + s.Extra);
            rv = Mathf.Max(1e-5f, rv * s.K + s.Extra);
            var raxis = Mathf.Max(1e-5f, s.P.RadiusAxis * s.K + s.Extra);

            var du = (Vector3.Dot(rel, s.U) - cu * s.K) / ru;
            var dv = (Vector3.Dot(rel, s.V) - cv * s.K) / rv;
            var dw = along < 0f ? along : along > s.Length ? along - s.Length : 0f;
            dw /= raxis;
            return du * du + dv * dv + dw * dw - 1f;
        }

        static Vector3 Gradient(in Shape s, Vector3 p)
        {
            const float h = 0.002f;
            var gu = Field(s, p + s.U * h) - Field(s, p - s.U * h);
            var gv = Field(s, p + s.V * h) - Field(s, p - s.V * h);
            var gw = Field(s, p + s.W * h) - Field(s, p - s.W * h);
            return s.U * gu + s.V * gv + s.W * gw;
        }

        // Step along the ray until the field goes negative, then bisect onto the crossing.
        // The step is a fraction of the thinnest thing a body has, so nothing thin enough
        // to matter is stepped over.
        static bool March(in Shape s, Vector3 origin, Vector3 direction, float maxDistance,
            out float distance, out Vector3 normal)
        {
            distance = 0f;
            normal = Vector3.zero;

            if (Field(s, origin) <= 0f)
            {
                // Already inside: always free to leave, never free to push deeper. Without
                // this a hand that ends up inside an arm would be locked there.
                normal = Gradient(s, origin);
                if (normal.sqrMagnitude < 1e-12f) return false;
                normal.Normalize();
                return Vector3.Dot(direction, normal) < 0f;
            }

            var step = 0.004f * Mathf.Max(0.25f, s.K);
            var steps = Mathf.Clamp(Mathf.CeilToInt(maxDistance / step), 4, 64);
            step = maxDistance / steps;

            var previous = 0f;
            for (var i = 1; i <= steps; i++)
            {
                var t = i * step;
                if (Field(s, origin + direction * t) > 0f) { previous = t; continue; }

                var lo = previous;
                var hi = t;
                for (var b = 0; b < 6; b++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (Field(s, origin + direction * mid) > 0f) lo = mid; else hi = mid;
                }
                normal = Gradient(s, origin + direction * lo);
                if (normal.sqrMagnitude < 1e-12f) return false;
                normal.Normalize();
                distance = lo;
                return true;
            }
            return false;
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 point)
        {
            var ab = b - a;
            var denominator = Vector3.Dot(ab, ab);
            if (denominator < 1e-10f) return a;
            return a + ab * Mathf.Clamp01(Vector3.Dot(point - a, ab) / denominator);
        }

        // ---- diagnostic view

        const int RingSegments = 14;
        MeshFilter _wireFilter;
        MeshRenderer _wireRenderer;
        Mesh _wire;
        readonly List<Vector3> _wireVerts = new List<Vector3>(2048);
        readonly List<int> _wireLines = new List<int>(4096);

        void LateUpdate()
        {
            if (!ShowShapes)
            {
                if (_wireRenderer && _wireRenderer.enabled) _wireRenderer.enabled = false;
                return;
            }
            if (_figure == null || _proxies.Count == 0) return;
            if (!_wire) BuildWire();
            _wireRenderer.enabled = true;

            _wireVerts.Clear();
            _wireLines.Clear();
            var k = RadiusScale;
            foreach (var proxy in _proxies)
            {
                var s = ShapeOf(proxy, 0f);
                for (var station = 0; station < Stations; station++)
                {
                    var t = station / (float)(Stations - 1);
                    proxy.Radii(t, out var ru, out var rv, out var cu, out var cv);
                    ru *= k; rv *= k;
                    var center = s.A + s.W * (t * s.Length) + s.U * (cu * k) + s.V * (cv * k);
                    var first = _wireVerts.Count;
                    for (var i = 0; i < RingSegments; i++)
                    {
                        var angle = i * (2f * Mathf.PI / RingSegments);
                        _wireVerts.Add(center + s.U * (Mathf.Cos(angle) * ru) + s.V * (Mathf.Sin(angle) * rv));
                        _wireLines.Add(first + i);
                        _wireLines.Add(first + (i + 1) % RingSegments);
                    }
                }
            }

            _wire.Clear();
            _wire.SetVertices(_wireVerts);
            _wire.SetIndices(_wireLines, MeshTopology.Lines, 0);
            _wire.RecalculateBounds();
        }

        void BuildWire()
        {
            // Deliberately unparented: the ring vertices are computed in world space, and
            // a parent with the figure's scale would apply that scale a second time.
            var go = new GameObject("contact shapes");
            _wireFilter = go.AddComponent<MeshFilter>();
            _wireRenderer = go.AddComponent<MeshRenderer>();
            _wire = new Mesh { name = "contact shapes", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _wire.MarkDynamic();
            _wireFilter.sharedMesh = _wire;
            _wireRenderer.sharedMaterial = BoneHandle.OverlayMaterial();
            _wireRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _wireRenderer.receiveShadows = false;
        }

        void OnDestroy()
        {
            if (_wireFilter) Destroy(_wireFilter.gameObject);
            if (_wire) Destroy(_wire);
        }
    }
}
