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
        public float SnapRadius = 0.05f;

        // Where the effector actually ended up last frame, which is where the next
        // frame's sweep starts. Surfaces are resolved by moving from there, not by
        // testing the controller's position, so the hand slides instead of sticking.
        Vector3 _lastEffector;
        bool _onSurface;
        public bool OnSurface => _onSurface;

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
            _lastEffector = Bone.position;
            _onSurface = false;
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
                var desired = hand.position + hand.rotation * _offsetPos;
                _lastEffector = ResolveAgainstSurfaces(desired);
                SolveIk(_lastEffector, hand.rotation * _offsetRot);
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
        Vector3 ResolveAgainstSurfaces(Vector3 desired)
        {
            _onSurface = false;
            if (!SurfaceSnap || SnapRadius <= 0f) return desired;

            var motion = desired - _lastEffector;
            var remaining = motion.magnitude;
            if (remaining < 1e-5f) return desired;

            // Already intersecting something (a prop moved onto the hand, or the pose
            // started inside one): do not fight it, or the hand would never get out.
            if (Physics.CheckSphere(_lastEffector, SnapRadius, ~0, QueryTriggerInteraction.Ignore))
                return desired;

            var dir = motion / remaining;
            var pos = _lastEffector;
            const float skin = 0.001f;

            for (var i = 0; i < 3 && remaining > 1e-5f; i++)
            {
                if (!Physics.SphereCast(pos, SnapRadius, dir, out var hit, remaining, ~0, QueryTriggerInteraction.Ignore))
                {
                    pos += dir * remaining;
                    break;
                }

                _onSurface = true;
                var travelled = Mathf.Max(0f, hit.distance - skin);
                pos += dir * travelled;
                remaining -= travelled;

                var slide = Vector3.ProjectOnPlane(dir * remaining, hit.normal);
                remaining = slide.magnitude;
                if (remaining < 1e-5f) break;
                dir = slide / remaining;
            }
            return pos;
        }

        // The limb solve.
        //
        // Without limits it is one analytic two-bone solve. With them, Daz's own joint
        // ranges are enforced while dragging instead of only on commit, and the reach the
        // limits refuse is handed to the clavicle, which is what a body actually does:
        // the upper arm alone runs out of range long before the arm runs out of reach.
        void SolveIk(Vector3 targetPos, Quaternion targetRot)
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

            var partial = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(from, to), _ik.ShoulderWeight);
            partial.ToAngleAxis(out var angle, out var axis);
            if (angle > 180f) { angle = 360f - angle; axis = -axis; }
            if (angle > _ik.ShoulderMaxDeg) partial = Quaternion.AngleAxis(_ik.ShoulderMaxDeg, axis);

            sh.rotation = partial * _shoulderRot0;
        }

        public void EndGrab(Transform hand)
        {
            SetState(State.Idle);
            _poseSync?.SetGrabbed(Figure.Id, false);
            _poseSync?.Commit(Figure, _ik != null ? $"VR pose: {_ik.Name}" : $"VR pose: {BoneId}");
        }

        // 0 = hidden, 1 = fully visible. Hovered/grabbed handles ignore it.
        public void SetVisibility(float alpha)
        {
            _alpha = Mathf.Clamp01(alpha);
            Apply();
        }

        // 0..1: how pinned against a Daz joint limit the worst bone this handle drives is.
        public void SetOverLimit(float pinned)
        {
            if (Mathf.Approximately(_overLimit, pinned)) return;
            _overLimit = pinned;
            Apply();
        }

        void Apply()
        {
            if (!_renderer) return;
            var c = Current == State.Grabbed ? GrabbedColor : Current == State.Hover ? HoverColor : _idleColor;
            // At a joint limit: this bone has run out of range and is why the limb stopped
            // following. (Without clamping it is also past the limit and Daz will correct it.)
            if (_overLimit > 0f) c = Color.Lerp(c, OverLimitColor, 0.35f + 0.5f * Mathf.Clamp01(_overLimit));
            var a = Current == State.Idle ? _alpha : 1f;
            _renderer.enabled = a > 0.01f;
            if (!_renderer.enabled) return;
            c.a = a;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", c);
            block.SetColor("_Color", c);
            _renderer.SetPropertyBlock(block);
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
