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

        Color _idleColor = new Color(0.55f, 0.65f, 0.85f, 1f);
        static readonly Color RootColor = new Color(1.0f, 0.55f, 0.25f, 1f);
        static readonly Color IkColor = new Color(0.3f, 0.8f, 0.85f, 1f);
        static readonly Color HoverColor = new Color(1.0f, 0.85f, 0.2f, 1f);
        static readonly Color GrabbedColor = new Color(0.3f, 1.0f, 0.4f, 1f);

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
        }

        // Makes this handle an IK effector. Both parent bones must exist on the figure.
        public bool SetIkChain(RigProfile profile, IkChain chain)
        {
            if (!figureHas(chain.Root, out _ikRoot) || !figureHas(chain.Mid, out _ikMid)) return false;
            _ikShoulder = chain.Shoulder != null && figureHas(chain.Shoulder, out var s) ? s : -1;
            _profile = profile;
            _ik = chain;
            _idleColor = IkColor;
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
                // Carry the hand/foot; the limb above solves to reach it.
                var targetPos = hand.position + hand.rotation * _offsetPos;
                AimShoulder(targetPos);
                TwoBoneIk.Solve(Figure.Bones[_ikRoot], Figure.Bones[_ikMid], Bone, targetPos,
                    _profile.PoleDirection(_ik, Figure.Go.transform), ref _bendHint);
                Bone.rotation = hand.rotation * _offsetRot;
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

        void Apply()
        {
            if (!_renderer) return;
            var c = Current == State.Grabbed ? GrabbedColor : Current == State.Hover ? HoverColor : _idleColor;
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
