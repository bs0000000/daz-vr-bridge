// A grab point on a bone. BoneHandles spawns one per grabbable bone; VrHand
// finds them by overlap and drives the bone through them.
//
// Two shapes: a sphere at the middle of the bone segment (limbs, spine, head),
// and a ring around the pivot for the root bone (hip), which sits outside the
// body so it can be reached and is visually distinct from the pelvis sphere.

using System.Collections.Generic;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BoneHandle : MonoBehaviour, IGrabbable
    {
        public enum State { Idle, Hover, Grabbed }
        public enum Kind { Sphere, Ring }

        public SceneLoader.LoadedFigure Figure;
        public int BoneIndex;
        public string BoneId;
        public Kind Shape { get; private set; }
        public Transform Bone => Figure.Bones[BoneIndex];

        public State Current { get; private set; } = State.Idle;
        public bool IsGrabbed => Current == State.Grabbed;
        public int Priority => 0;
        // Faded out entirely: its colour cannot be seen, so it need not be scanned.
        public bool Visible => _alpha > 0.01f || Current != State.Idle;

        Renderer _renderer;
        float _alpha = 1f;
        float _ringRadius;
        static PoseSync _poseSync;

        // Grab state. Spheres (aim-based FK): the bone swings about its origin so the
        // segment keeps pointing at the hand; the hand's roll about that axis twists the
        // bone. The ring (root): rigid follow, so the whole figure can be carried.
        Vector3 _dir0;          // bone origin -> hand at grab time (world)
        Quaternion _boneRot0;   // bone world rotation at grab time
        Quaternion _handRot0;   // hand world rotation at grab time
        Vector3 _offsetPos;     // ring/IK: bone position in hand space at grab time
        Quaternion _offsetRot;  // ring/IK: bone rotation relative to the hand at grab time

        // IK: set when this bone ends a chain in the rig profile. Grabbing it carries the
        // bone rigidly (like the ring) while the two bones above solve to follow.
        IkChain _ik;
        RigProfile _profile;
        int _ikRoot, _ikMid;
        int _ikShoulder = -1;       // clavicle, when the chain has one
        Quaternion _shoulderRot0;   // its rotation at grab time; the aim is re-applied to
                                    // this each frame so the contribution cannot accumulate
        Vector3 _bendHint;

        // The bones this handle drives, for the joint-limit readout.
        public int[] ControlledBones { get; private set; }
        float _overLimit;

        // Set by BoneHandles.
        public SceneLoader Loader;
        public bool ClampToLimits = true;
        public bool RollAssist = true;
        public int IkIterations = 4;
        public bool SurfaceSnap = true;
        public bool BodyCollisions = true;
        public float SnapRadius = 0.03f;
        // How much of a refused reach the clavicle absorbs. Negative means "whatever the
        // rig profile says"; the in-VR setting overrides it without writing to the
        // profile, which is cached and shared by every figure on that rig.
        public float ShoulderWeight = -1f;

        // Contact proxy. Neither a sphere nor a box works: a sphere big enough for a fist
        // floats an open palm, and a box is a loose fit whose corner hits first whenever
        // the hand meets a surface at an angle. So the real surface is used — at grab
        // time the outermost vertices of the hand (and its fingers) are taken as contact
        // samples, and each one sweeps individually.
        Vector3 _contactLocal;      // the handle offset; only the sphere fallback uses it
        Vector3[] _samplesLocal;    // contact points in the bone's frame
        float _sampleReach;         // furthest sample from the joint, for the broadphase
        Vector3 _lastEffector;
        bool _onSurface;
        Vector3 _contactPoint, _contactNormal;
        public bool OnSurface => _onSurface;
        Transform _contactDisc;
        VrHand _hand;
        bool _wasPinned;

        Color _idleColor = new Color(0.55f, 0.65f, 0.85f, 1f);
        static readonly Color RootColor = new Color(1.0f, 0.55f, 0.25f, 1f);
        static readonly Color IkColor = new Color(0.3f, 0.8f, 0.85f, 1f);
        static readonly Color HoverColor = new Color(1.0f, 0.85f, 0.2f, 1f);
        static readonly Color GrabbedColor = new Color(0.3f, 1.0f, 0.4f, 1f);
        static readonly Color OverLimitColor = new Color(1.0f, 0.25f, 0.2f, 1f);

        public void Init(SceneLoader.LoadedFigure figure, int boneIndex, float radius)
        {
            Shape = Kind.Sphere;
            Setup(figure, boneIndex);
            var b = figure.BoneJson[boneIndex];

            // Sit at the middle of the bone segment, not at the joint: that is where a
            // hand reaches for a limb, and it gives the drag a lever arm. The segment
            // (origin -> end) is figure-space; express it in the bone's own frame.
            var seg = DazSpace.Pos(b["end"]) - DazSpace.Pos(b["origin"]);
            var local = Quaternion.Inverse(DazSpace.Rot(b["orient"])) * seg;
            transform.localPosition = local * 0.5f;
            _contactLocal = transform.localPosition;

            var col = gameObject.AddComponent<SphereCollider>();
            col.radius = radius;
            col.isTrigger = true;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "vis";
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(transform, false);
            vis.transform.localScale = Vector3.one * radius * 2f; // primitive sphere scale is its diameter
            _renderer = vis.GetComponent<Renderer>();
            _renderer.sharedMaterial = OverlayMaterial();
            SetState(State.Idle);
        }

        // Ring in the figure's horizontal plane around the bone pivot (root bone).
        public void InitRing(SceneLoader.LoadedFigure figure, int boneIndex, float ringRadius, float tube)
        {
            Shape = Kind.Ring;
            _ringRadius = ringRadius;
            _idleColor = RootColor;
            Setup(figure, boneIndex);

            // The ring lies flat in figure space; undo the bone's own frame so its
            // local Y is the figure's up.
            var b = figure.BoneJson[boneIndex];
            transform.localRotation = Quaternion.Inverse(DazSpace.Rot(b["orient"]));

            // Grab zone: a necklace of trigger spheres along the ring.
            const int colliders = 16;
            for (var i = 0; i < colliders; i++)
            {
                var a = i * Mathf.PI * 2f / colliders;
                var c = new GameObject($"col{i}");
                c.transform.SetParent(transform, false);
                c.transform.localPosition = new Vector3(Mathf.Cos(a) * ringRadius, 0f, Mathf.Sin(a) * ringRadius);
                var sc = c.AddComponent<SphereCollider>();
                sc.radius = tube * 2.5f;
                sc.isTrigger = true;
            }

            var vis = new GameObject("vis");
            vis.transform.SetParent(transform, false);
            vis.AddComponent<MeshFilter>().sharedMesh = TorusMesh(ringRadius, tube);
            _renderer = vis.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = OverlayMaterial();
            SetState(State.Idle);
        }

        void Setup(SceneLoader.LoadedFigure figure, int boneIndex)
        {
            Figure = figure;
            BoneIndex = boneIndex;
            BoneId = figure.BoneJson[boneIndex].Value<string>("id");
            ControlledBones = new[] { boneIndex };
        }

        // Makes this handle an IK effector. Both parent bones must exist on the figure.
        public bool SetIkChain(RigProfile profile, IkChain chain)
        {
            if (!figureHas(chain.Root, out _ikRoot) || !figureHas(chain.Mid, out _ikMid)) return false;
            _ikShoulder = chain.Shoulder != null && figureHas(chain.Shoulder, out var s) ? s : -1;
            _profile = profile;
            _ik = chain;
            _idleColor = IkColor;
            ControlledBones = _ikShoulder >= 0
                ? new[] { _ikShoulder, _ikRoot, _ikMid, BoneIndex }
                : new[] { _ikRoot, _ikMid, BoneIndex };
            SetState(Current);
            return true;

            bool figureHas(string id, out int index) => Figure.ByName.TryGetValue(id, out index);
        }

        // Distance from a world point to the grabbable surface's center line:
        // the sphere center, or the nearest point on the ring circle.
        public float DistanceTo(Vector3 world)
        {
            if (Shape == Kind.Sphere) return Vector3.Distance(world, transform.position);
            var l = transform.InverseTransformPoint(world);
            var radial = Mathf.Sqrt(l.x * l.x + l.z * l.z) - _ringRadius;
            var local = Mathf.Sqrt(radial * radial + l.y * l.y);
            return local * transform.lossyScale.x;
        }

        public void SetState(State s)
        {
            Current = s;
            Apply();
        }

        // ---- IGrabbable

        public void SetHover(bool on)
        {
            if (IsGrabbed) return;
            SetState(on ? State.Hover : State.Idle);
        }

        public void BeginGrab(Transform hand)
        {
            _dir0 = hand.position - Bone.position;
            _boneRot0 = Bone.rotation;
            _handRot0 = hand.rotation;
            _offsetPos = Quaternion.Inverse(hand.rotation) * (Bone.position - hand.position);
            _offsetRot = Quaternion.Inverse(hand.rotation) * Bone.rotation;
            _bendHint = Vector3.zero; // the solver seeds it from the limb's current bend
            if (_ikShoulder >= 0) _shoulderRot0 = Figure.Bones[_ikShoulder].rotation;
            if (_ik != null) MeasureContactSamples();
            _lastEffector = Bone.position;
            _onSurface = false;
            _hand = hand ? hand.GetComponent<VrHand>() : null;
            SetState(State.Grabbed);
            if (!_poseSync) _poseSync = FindAnyObjectByType<PoseSync>();
            _poseSync?.SetGrabbed(Figure.Id, true);
        }

        public void UpdateGrab(Transform hand)
        {
            if (Shape == Kind.Ring)
            {
                // Carry the root: position and rotation follow the hand rigidly.
                Bone.rotation = hand.rotation * _offsetRot;
                Bone.position = hand.position + hand.rotation * _offsetPos;
                return;
            }

            if (_ik != null)
            {
                // Carry the hand/foot; the limb above solves to reach it. Props stop it:
                // a hand put on an armrest rests there instead of passing through.
                var targetRot = hand.rotation * _offsetRot;
                var desiredEffector = hand.position + hand.rotation * _offsetPos;

                // Resolve where the palm/sole may go, then put the joint back under it.
                var wasOnSurface = _onSurface;
                _lastEffector = ResolveAgainstSurfaces(desiredEffector, targetRot);
                SolveIk(_lastEffector, targetRot);
                ShowContact();
                if (_onSurface && !wasOnSurface) _hand?.Pulse(0.35f, 0.03f); // a tick on touching down
                return;
            }

            var dir = hand.position - Bone.position;
            if (_dir0.sqrMagnitude < 1e-6f || dir.sqrMagnitude < 1e-6f) return;

            var swing = Quaternion.FromToRotation(_dir0, dir);

            var delta = hand.rotation * Quaternion.Inverse(_handRot0);
            var axis = dir.normalized;
            var proj = Vector3.Dot(new Vector3(delta.x, delta.y, delta.z), axis) * axis;
            var twist = new Quaternion(proj.x, proj.y, proj.z, delta.w);
            twist = twist.x == 0f && twist.y == 0f && twist.z == 0f && twist.w == 0f ? Quaternion.identity : twist.normalized;

            Bone.rotation = twist * swing * _boneRot0;
        }

        // Collide-and-slide from where the effector was to where the controller wants it:
        // sweep a sphere, stop at the first surface, project what is left onto that
        // surface and sweep again. So a hand pushed into a couch arm settles on it and
        // then slides along it, and lifting the controller frees it immediately.
        // Measures the hand or foot as it currently is — fingers curled or flat — by
        // taking the skinned vertices that belong to this bone and its descendants and
        // keeping the outermost one in each of 26 directions. Those extremes are what
        // touches a surface first, whatever angle the hand meets it at. Once per grab.
        void MeasureContactSamples()
        {
            _samplesLocal = null;
            _sampleReach = 0f;
            if (!SurfaceSnap && !BodyCollisions) return;

            SkinnedMeshRenderer smr = null;
            foreach (var s in Figure.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (s.sharedMesh != null && (!smr || s.sharedMesh.vertexCount > smr.sharedMesh.vertexCount)) smr = s;
            if (!smr) return;

            // This bone and everything hanging off it: the palm plus its fingers.
            var mine = new bool[Figure.Bones.Length];
            for (var b = 0; b < mine.Length; b++)
                for (var p = b; p >= 0; p = Figure.ParentBone[p])
                    if (p == BoneIndex) { mine[b] = true; break; }

            var baked = new Mesh();
            smr.BakeMesh(baked, true);
            var verts = baked.vertices;
            var perVertex = smr.sharedMesh.GetBonesPerVertex();
            var weights = smr.sharedMesh.GetAllBoneWeights();

            // Bone-local, and relative to the bone origin so a sample is just an offset
            // from the effector the IK drives.
            var toLocal = Bone.worldToLocalMatrix * smr.transform.localToWorldMatrix;
            var dirs = SampleDirections();
            var best = new Vector3[dirs.Length];
            var bestDot = new float[dirs.Length];
            for (var d = 0; d < dirs.Length; d++) bestDot[d] = float.NegativeInfinity;

            var found = 0;
            var wi = 0;
            for (var v = 0; v < perVertex.Length && v < verts.Length; v++)
            {
                var take = false;
                for (var k = 0; k < perVertex[v]; k++, wi++)
                {
                    var bw = weights[wi];
                    if (bw.weight >= 0.5f && bw.boneIndex < mine.Length && mine[bw.boneIndex]) take = true;
                }
                if (!take) continue;
                var p = toLocal.MultiplyPoint3x4(verts[v]);
                if (p.sqrMagnitude > 0.25f) continue; // 50 cm from the joint: not this hand
                found++;
                for (var d = 0; d < dirs.Length; d++)
                {
                    var dot = Vector3.Dot(p, dirs[d]);
                    if (dot > bestDot[d]) { bestDot[d] = dot; best[d] = p; }
                }
            }
            perVertex.Dispose();
            weights.Dispose();
            Destroy(baked);
            if (found < 8) return;

            // Distinct extremes only; several directions often pick the same fingertip.
            var unique = new List<Vector3>(dirs.Length);
            for (var d = 0; d < dirs.Length; d++)
            {
                if (bestDot[d] == float.NegativeInfinity) continue;
                var dup = false;
                foreach (var u in unique) if ((u - best[d]).sqrMagnitude < 4e-4f) { dup = true; break; }
                if (!dup) unique.Add(best[d]);
            }
            if (unique.Count < 4) return;
            _samplesLocal = unique.ToArray();
            foreach (var s in _samplesLocal) _sampleReach = Mathf.Max(_sampleReach, s.magnitude);
        }

        // 6 face + 12 edge + 8 corner directions of a cube.
        static Vector3[] SampleDirections()
        {
            if (_sampleDirs != null) return _sampleDirs;
            var list = new List<Vector3>(26);
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        if (x == 0 && y == 0 && z == 0) continue;
                        list.Add(new Vector3(x, y, z).normalized);
                    }
            _sampleDirs = list.ToArray();
            return _sampleDirs;
        }
        static Vector3[] _sampleDirs;

        // Sweeps every contact sample and lets the first one to touch stop the whole hand,
        // then slides the rest of the motion along that surface. Only motion *into* a
        // surface is blocked (dot(dir, normal) < 0), so a hand already resting on
        // something is always free to lift off — no "already stuck" special case, which
        // is what used to switch snapping off silently.
        Vector3 ResolveAgainstSurfaces(Vector3 desired, Quaternion orientation)
        {
            var t0 = BridgeProfiler.Begin();
            try { return ResolveAgainstSurfacesInner(desired, orientation); }
            finally { BridgeProfiler.End("surface sweep", t0); }
        }

        Vector3 ResolveAgainstSurfacesInner(Vector3 desired, Quaternion orientation)
        {
            _onSurface = false;
            if (!SurfaceSnap && !BodyCollisions) return desired;

            var motion = desired - _lastEffector;
            var remaining = motion.magnitude;
            if (remaining < 1e-5f) return _lastEffector;

            var samples = _samplesLocal;
            var dir = motion / remaining;
            var pos = _lastEffector;
            const float skin = 0.002f;
            var radius = samples != null ? 0.005f : Mathf.Max(0.005f, SnapRadius);

            // Broadphase: one sweep of a sphere enclosing the whole hand. A hand moving
            // through open air is the common case, and it now costs one cast instead of
            // one per contact sample.
            if (samples != null)
            {
                var propsMayHit = SurfaceSnap && Physics.SphereCast(pos, _sampleReach + radius, dir,
                    out _, remaining, ~0, QueryTriggerInteraction.Ignore);
                var bodyMayHit = BodyCollisions && BodyCollisionRig.MayHit(pos, dir, remaining,
                    _sampleReach + radius, Figure, _ikRoot, _ikMid, BoneIndex, _ikShoulder);
                if (!propsMayHit && !bodyMayHit) return desired;
            }

            for (var i = 0; i < 3 && remaining > 1e-5f; i++)
            {
                var hitDist = remaining;
                var hitNormal = Vector3.zero;
                var hitPoint = Vector3.zero;
                var blocked = false;

                var count = samples?.Length ?? 1;
                for (var s = 0; s < count; s++)
                {
                    var origin = samples != null ? pos + orientation * samples[s] : pos + orientation * _contactLocal;
                    if (SurfaceSnap && Physics.SphereCast(origin, radius, dir, out var hit,
                        remaining, ~0, QueryTriggerInteraction.Ignore)
                        && Vector3.Dot(dir, hit.normal) < 0f && hit.distance < hitDist)
                    {
                        hitDist = hit.distance;
                        hitNormal = hit.normal;
                        hitPoint = hit.point;
                        blocked = true;
                    }
                    if (BodyCollisions && BodyCollisionRig.Sweep(origin, dir, hitDist, radius,
                        Figure, _ikRoot, _ikMid, BoneIndex, _ikShoulder,
                        out var bodyDistance, out var bodyPoint, out var bodyNormal))
                    {
                        hitDist = bodyDistance;
                        hitNormal = bodyNormal;
                        hitPoint = bodyPoint;
                        blocked = true;
                    }
                }

                if (!blocked)
                {
                    pos += dir * remaining;
                    break;
                }

                _onSurface = true;
                _contactPoint = hitPoint;
                _contactNormal = hitNormal;

                var travelled = Mathf.Max(0f, hitDist - skin);
                pos += dir * travelled;
                remaining -= travelled;

                var slide = Vector3.ProjectOnPlane(dir * remaining, hitNormal);
                remaining = slide.magnitude;
                if (remaining < 1e-5f) break;
                dir = slide / remaining;
            }
            return pos;
        }

        // A cyan disc lying on the surface at the contact point: resting on something
        // and running out of joint range feel the same through a controller, so they
        // need to look different. Red handle = joint at its limit. Disc = touching.
        void ShowContact()
        {
            if (!_onSurface)
            {
                if (_contactDisc) _contactDisc.gameObject.SetActive(false);
                return;
            }

            if (!_contactDisc)
            {
                var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                disc.name = "contact";
                Destroy(disc.GetComponent<Collider>());
                disc.transform.localScale = new Vector3(0.12f, 0.0015f, 0.12f);
                var r = disc.GetComponent<Renderer>();
                r.sharedMaterial = OverlayMaterial();
                var block = new MaterialPropertyBlock();
                var c = new Color(0.2f, 0.9f, 1f, 0.75f);
                block.SetColor("_BaseColor", c);
                block.SetColor("_Color", c);
                r.SetPropertyBlock(block);
                _contactDisc = disc.transform;
            }

            _contactDisc.gameObject.SetActive(true);
            _contactDisc.position = _contactPoint + _contactNormal * 0.002f;
            _contactDisc.up = _contactNormal; // the cylinder's axis is its Y
        }

        // The limb solve.
        //
        // Without limits it is one analytic two-bone solve. With them, Daz's own joint
        // ranges are enforced while dragging instead of only on commit, and the reach the
        // limits refuse is handed to the clavicle, which is what a body actually does:
        // the upper arm alone runs out of range long before the arm runs out of reach.
        void SolveIk(Vector3 targetPos, Quaternion targetRot)
        {
            var t0 = BridgeProfiler.Begin();
            try { SolveIkInner(targetPos, targetRot); }
            finally { BridgeProfiler.End("ik solve", t0); }
        }

        void SolveIkInner(Vector3 targetPos, Quaternion targetRot)
        {
            var root = Figure.Bones[_ikRoot];
            var mid = Figure.Bones[_ikMid];
            var shoulder = _ikShoulder >= 0 ? Figure.Bones[_ikShoulder] : null;
            var pole = _profile.PoleDirection(_ik, Figure.Go.transform);
            var clamping = ClampToLimits && Loader != null;

            if (shoulder)
            {
                AimShoulder(targetPos);
                if (clamping) DazEuler.ClampToLimits(Loader, Figure, _ikShoulder);
            }

            var iterations = clamping ? Mathf.Max(1, IkIterations) : 1;
            for (var i = 0; i < iterations; i++)
            {
                TwoBoneIk.Solve(root, mid, Bone, targetPos, pole, ref _bendHint);
                if (!clamping) break;

                var moved = DazEuler.ClampToLimits(Loader, Figure, _ikRoot)
                          + DazEuler.ClampToLimits(Loader, Figure, _ikMid);
                if (moved <= 0.01f) break; // the solve was already legal: exact and done

                // The clamps left the hand short of the target. Swing the clavicle to
                // close the gap (a CCD step), within its own limits, and solve again.
                if (shoulder && i < iterations - 1)
                {
                    var from = Bone.position - shoulder.position;
                    var to = targetPos - shoulder.position;
                    if (from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f)
                        shoulder.rotation = Quaternion.FromToRotation(from, to) * shoulder.rotation;
                    DazEuler.ClampToLimits(Loader, Figure, _ikShoulder);
                }
            }

            Bone.rotation = targetRot;

            // The wrist alone cannot twist far (Daz gives l_hand z +/-70..80); in a real
            // arm most of that twist is forearm pronation. Rolling the forearm about the
            // elbow-to-wrist axis moves no joint position, so the solve above still holds.
            if (clamping && RollAssist && _ik.RollAssist) RollMidBone(targetRot);

            if (clamping)
            {
                DazEuler.ClampToLimits(Loader, Figure, _ikMid);
                DazEuler.ClampToLimits(Loader, Figure, BoneIndex);
            }
        }

        // Picks the roll that leaves the least total limit violation on the middle and end
        // bones: a coarse sweep, then a refinement. The cost is smooth in the roll angle.
        void RollMidBone(Quaternion targetRot)
        {
            var t0 = BridgeProfiler.Begin();
            try { RollMidBoneInner(targetRot); }
            finally { BridgeProfiler.End("roll sweep", t0); }
        }

        void RollMidBoneInner(Quaternion targetRot)
        {
            var mid = Figure.Bones[_ikMid];
            var axis = Bone.position - mid.position;
            if (axis.sqrMagnitude < 1e-8f) return;
            axis.Normalize();

            var baseRot = mid.rotation;
            var best = 0f;
            var bestCost = RollCost(0f, baseRot, axis, targetRot);

            if (bestCost > 0.01f)
            {
                var max = _ik.RollMaxDeg;
                for (var roll = -max; roll <= max; roll += 15f)
                {
                    var cost = RollCost(roll, baseRot, axis, targetRot);
                    if (cost < bestCost - 0.01f) { bestCost = cost; best = roll; }
                }
                var coarse = best;
                for (var d = -12f; d <= 12f; d += 3f)
                {
                    var cost = RollCost(coarse + d, baseRot, axis, targetRot);
                    if (cost < bestCost - 0.01f) { bestCost = cost; best = coarse + d; }
                }
            }

            RollCost(best, baseRot, axis, targetRot); // leave the winner applied
        }

        float RollCost(float roll, Quaternion baseRot, Vector3 axis, Quaternion targetRot)
        {
            Figure.Bones[_ikMid].rotation = Quaternion.AngleAxis(roll, axis) * baseRot;
            Bone.rotation = targetRot;
            var end = DazEuler.Check(Loader, Figure, BoneIndex);
            var mid = DazEuler.Check(Loader, Figure, _ikMid);
            return (end.Valid ? end.Worst : 0f) + (mid.Valid ? mid.Worst : 0f);
        }

        // Rotates the clavicle a fraction of the way toward the target before the
        // two-bone solve: reaching across or down is shoulder-girdle motion in a real
        // body, and without it the upper arm alone has to exceed its Daz limits.
        void AimShoulder(Vector3 targetPos)
        {
            if (_ikShoulder < 0) return;
            var sh = Figure.Bones[_ikShoulder];

            sh.rotation = _shoulderRot0; // start from the grab-time pose, never from last frame
            var from = Bone.position - sh.position;
            var to = targetPos - sh.position;
            if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f) return;

            var weight = ShoulderWeight >= 0f ? ShoulderWeight : _ik.ShoulderWeight;
            var partial = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(from, to), weight);
            partial.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) { angle = 360f - angle; axis = -axis; }
            if (angle > _ik.ShoulderMaxDeg) partial = Quaternion.AngleAxis(_ik.ShoulderMaxDeg, axis);

            sh.rotation = partial * _shoulderRot0;
        }

        // The disc lives at the scene root (it follows a surface, not the bone), so it
        // has to be cleaned up by hand when the scene is rebuilt.
        void OnDestroy()
        {
            if (_contactDisc) Destroy(_contactDisc.gameObject);
        }

        public void EndGrab(Transform hand)
        {
            _onSurface = false;
            _wasPinned = false;
            _hand = null;
            if (_contactDisc) _contactDisc.gameObject.SetActive(false);
            SetState(State.Idle);
            _poseSync?.SetGrabbed(Figure.Id, false);
            _poseSync?.Commit(Figure, _ik != null ? $"VR pose: {_ik.Name}" : $"VR pose: {BoneId}");
        }

        // 0 = hidden, 1 = fully visible. Hovered/grabbed handles ignore it.
        public void SetVisibility(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            // The fade changes continuously as a hand moves; only repaint on a visible step.
            if (Mathf.Abs(alpha - _alpha) < 0.02f && (alpha > 0.01f) == (_alpha > 0.01f)) return;
            _alpha = alpha;
            Apply();
        }

        // 0..1: how pinned against a Daz joint limit the worst bone this handle drives is.
        public void SetOverLimit(float pinned)
        {
            // Buzz once on running out of range, so it can be felt rather than read.
            var nowPinned = pinned > 0.6f;
            if (nowPinned && !_wasPinned && Current == State.Grabbed) _hand?.Pulse(0.7f, 0.06f);
            _wasPinned = nowPinned;

            if (Mathf.Approximately(_overLimit, pinned)) return;
            _overLimit = pinned;
            Apply();
        }

        // One block reused for every handle: allocating a MaterialPropertyBlock per call
        // meant ~26 allocations a frame once the distance fade was continuously changing.
        static MaterialPropertyBlock _block;
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Apply()
        {
            if (!_renderer) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            var c = Current == State.Grabbed ? GrabbedColor : Current == State.Hover ? HoverColor : _idleColor;
            // At a joint limit: this bone has run out of range and is why the limb stopped
            // following. (Without clamping it is also past the limit and Daz will correct it.)
            if (_overLimit > 0f) c = Color.Lerp(c, OverLimitColor, 0.35f + 0.5f * Mathf.Clamp01(_overLimit));
            var a = Current == State.Idle ? _alpha : 1f;
            _renderer.enabled = a > 0.01f;
            if (!_renderer.enabled) return;
            c.a = a;
            _block.Clear();
            _block.SetColor(BaseColorId, c);
            _block.SetColor(ColorId, c);
            _renderer.SetPropertyBlock(_block);
        }

        static Material _overlayMaterial;
        // Unlit, alpha, drawn through everything. Shared by handles and controllers.
        public static Material OverlayMaterial()
        {
            if (_overlayMaterial) return _overlayMaterial;
            var shader = Shader.Find("DazVrBridge/HandleOverlay")
                      ?? Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Unlit/Color");
            _overlayMaterial = new Material(shader) { name = "BridgeOverlay" };
            return _overlayMaterial;
        }

        static Mesh _torus;
        static Mesh TorusMesh(float radius, float tube)
        {
            if (_torus) return _torus;
            const int segs = 40, sides = 12;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var tris = new List<int>();
            for (var i = 0; i <= segs; i++)
            {
                var a = i * Mathf.PI * 2f / segs;
                var center = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                for (var j = 0; j <= sides; j++)
                {
                    var b = j * Mathf.PI * 2f / sides;
                    var n = center * Mathf.Cos(b) + Vector3.up * Mathf.Sin(b);
                    verts.Add(center * radius + n * tube);
                    norms.Add(n);
                }
            }
            for (var i = 0; i < segs; i++)
                for (var j = 0; j < sides; j++)
                {
                    var a = i * (sides + 1) + j;
                    var b = a + sides + 1;
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                }
            _torus = new Mesh { name = "HandleRing" };
            _torus.SetVertices(verts);
            _torus.SetNormals(norms);
            _torus.SetTriangles(tris, 0);
            _torus.RecalculateBounds();
            return _torus;
        }
    }
}
