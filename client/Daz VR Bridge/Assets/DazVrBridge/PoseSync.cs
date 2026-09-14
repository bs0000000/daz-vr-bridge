// Phase 2a: the round trip, without any VR interaction yet.
//
//   pose.state  (Daz -> here)  re-poses the figure's bones from Daz world transforms
//   pose.commit (here -> Daz)  sends bones whose rotation differs from the last Daz
//                              state, as Daz world rotations; Daz applies them as one
//                              undo step and answers with a pose.state
//   self-test                  Daz sends its current pose, we echo it back, Daz checks
//                              its Euler controls came back within 0.01 degrees
//
// Editor test loop (Play mode, Game view focused):
//   rotate a bone under DazScene/<figure>/skeleton with the Scene gizmo, press C
//   press T to run the self-test on the first figure
//   press R to re-request the scene

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace DazVrBridge
{
    public sealed class PoseSync : MonoBehaviour
    {
        public BridgeSession session;
        public SceneLoader loader;

        [Tooltip("Minimum rotation change (degrees) for a bone to be included in a commit.")]
        public float commitThresholdDeg = 0.05f;

        public string LastSelfTest { get; private set; } = "";

        // Per figure: each bone's rotation/position as last confirmed by Daz (pose.state or manifest).
        readonly Dictionary<string, Quaternion[]> _known = new Dictionary<string, Quaternion[]>();
        readonly Dictionary<string, Vector3[]> _knownPos = new Dictionary<string, Vector3[]>();
        [Tooltip("Minimum position change (meters) for a bone's translation to be committed (root moves).")]
        public float commitPosThreshold = 0.002f;

        // Figures with a bone currently held in VR: incoming pose.state is dropped for
        // them so Daz's last confirmation cannot fight the hand. The commit on release
        // produces a fresh pose.state anyway.
        readonly HashSet<string> _grabbed = new HashSet<string>();

        public void SetGrabbed(string figureId, bool on)
        {
            if (on) _grabbed.Add(figureId); else _grabbed.Remove(figureId);
        }

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            session.ControlFrame += OnFrame;
            loader.SceneBuilt += SnapshotAll;
        }

        void Update()
        {
            if (!KeyDown()) return;
        }

        bool KeyDown()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return false;
            if (kb.cKey.wasPressedThisFrame) { CommitAll(); return true; }
            if (kb.tKey.wasPressedThisFrame) { SelfTest(); return true; }
            if (kb.rKey.wasPressedThisFrame) { loader.RequestScene(); return true; }
            if (kb.dKey.wasPressedThisFrame) { DebugBoneOffsets(); return true; }
            if (kb.pKey.wasPressedThisFrame) { ReportCost(); return true; }
#else
            if (Input.GetKeyDown(KeyCode.C)) { CommitAll(); return true; }
            if (Input.GetKeyDown(KeyCode.T)) { SelfTest(); return true; }
            if (Input.GetKeyDown(KeyCode.R)) { loader.RequestScene(); return true; }
            if (Input.GetKeyDown(KeyCode.D)) { DebugBoneOffsets(); return true; }
            if (Input.GetKeyDown(KeyCode.P)) { ReportCost(); return true; }
#endif
            return false;
        }

        void OnFrame(BridgeFrame f)
        {
            switch (f.Type)
            {
                case "pose.state":
                    OnPoseState(f.Header);
                    break;
                case "selftest.result":
                    LastSelfTest = f.Header.Value<bool>("pass")
                        ? $"self-test PASS: {f.Header.Value<int>("bones")} bones, max {f.Header.Value<double>("max_error_deg"):F4}°"
                        : $"self-test FAIL: max {f.Header.Value<double>("max_error_deg"):F3}° at {f.Header.Value<string>("worst")} {f.Header.Value<string>("error")}";
                    Debug.Log($"[DazVrBridge] {LastSelfTest}");
                    break;
            }
        }

        void OnPoseState(JObject h)
        {
            var id = h.Value<string>("figure");
            if (!loader.Figures.TryGetValue(id, out var fig)) return;
            if (_grabbed.Contains(id)) return;

            loader.ApplyWorldPose(fig, (JArray)h["bones"]);
            Snapshot(fig);

            if (h.Value<bool?>("selftest") == true)
            {
                // Echo every bone straight back; Daz compares its Euler controls.
                Send(fig, AllBones(fig), "self-test", selfTest: true);
            }
        }

        // ---- commit

        [ContextMenu("Commit all figures")]
        public void CommitAll()
        {
            foreach (var fig in loader.Figures.Values) Commit(fig);
        }

        public void Commit(SceneLoader.LoadedFigure fig, string label = "VR pose")
        {
            var changed = new List<int>();
            if (_known.TryGetValue(fig.Id, out var known) && _knownPos.TryGetValue(fig.Id, out var knownPos))
            {
                for (var i = 0; i < fig.Bones.Length; i++)
                    if (Quaternion.Angle(known[i], fig.Bones[i].rotation) > commitThresholdDeg
                        || Vector3.Distance(knownPos[i], fig.Bones[i].position) > commitPosThreshold) changed.Add(i);
            }
            else
            {
                for (var i = 0; i < fig.Bones.Length; i++) changed.Add(i);
            }

            if (changed.Count == 0)
            {
                Debug.Log($"[DazVrBridge] {fig.Label}: nothing to commit");
                return;
            }
            Send(fig, changed, label, selfTest: false);
            Debug.Log($"[DazVrBridge] {fig.Label}: committed {changed.Count} bones");
        }

        void Send(SceneLoader.LoadedFigure fig, List<int> boneIndices, string label, bool selfTest)
        {
            var bones = new JArray();
            _knownPos.TryGetValue(fig.Id, out var knownPos);
            foreach (var i in boneIndices)
            {
                var q = loader.DazWorldRotOf(fig, i);
                var entry = new JArray(fig.BoneJson[i].Value<string>("id"), q[0], q[1], q[2], q[3]);
                // A bone that moved (the root, carried by its ring) also sends its world position.
                if (knownPos == null || Vector3.Distance(knownPos[i], fig.Bones[i].position) > commitPosThreshold)
                {
                    var p = DazSpace.ToDazPos(loader.Root.InverseTransformPoint(fig.Bones[i].position));
                    entry.Add(p[0]); entry.Add(p[1]); entry.Add(p[2]);
                }
                bones.Add(entry);
            }
            var h = new JObject
            {
                ["t"] = "pose.commit",
                ["figure"] = fig.Id,
                ["bones"] = bones,
                ["label"] = label,
            };
            if (selfTest) h["selftest"] = true;
            session.SendControl(h);
        }

        // ---- self-test

        [ContextMenu("Self-test first figure")]
        public void SelfTest()
        {
            foreach (var fig in loader.Figures.Values)
            {
                LastSelfTest = "self-test running…";
                session.SendControl(new JObject { ["t"] = "selftest.begin", ["figure"] = fig.Id });
                return;
            }
            Debug.LogWarning("[DazVrBridge] no figure loaded");
        }

        // ---- diagnostics

        // P: how much of each frame the bridge itself accounts for, then start a fresh
        // window. Anything unaccounted for is Unity, XR submission, or waiting on the
        // headset's refresh — which is what caps the frame rate once a headset is live.
        [ContextMenu("Report bridge cost")]
        public void ReportCost()
        {
            Debug.Log(BridgeProfiler.Report());
            BridgeProfiler.Reset();
        }

        // D key: for a few bones, distance between the live bone position and the
        // centroid of the skinned mesh's vertices that belong (>= 0.9) to that bone.
        // Should be a few cm (joint center vs. flesh); tens of cm means the mesh and
        // the pivots disagree.
        [ContextMenu("Debug bone offsets")]
        public void DebugBoneOffsets()
        {
            foreach (var fig in loader.Figures.Values)
            {
                // The body: followers (eyes, lashes) are also under the figure; take the biggest.
                SkinnedMeshRenderer smr = null;
                foreach (var s in fig.Go.GetComponentsInChildren<SkinnedMeshRenderer>())
                    if (!smr || s.sharedMesh.vertexCount > smr.sharedMesh.vertexCount) smr = s;
                if (!smr) continue;
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                var verts = baked.vertices;
                var l2w = smr.transform.localToWorldMatrix;
                var perVertex = smr.sharedMesh.GetBonesPerVertex();
                var weights = smr.sharedMesh.GetAllBoneWeights();

                var sums = new Dictionary<int, (Vector3 sum, int n)>();
                var wi = 0;
                for (var v = 0; v < perVertex.Length; v++)
                {
                    for (var k = 0; k < perVertex[v]; k++, wi++)
                    {
                        var bw = weights[wi];
                        if (bw.weight < 0.9f) continue;
                        sums.TryGetValue(bw.boneIndex, out var acc);
                        sums[bw.boneIndex] = (acc.sum + l2w.MultiplyPoint3x4(verts[v]), acc.n + 1);
                    }
                }
                perVertex.Dispose(); weights.Dispose();

                var sb = new System.Text.StringBuilder($"[DazVrBridge] bone offsets for {fig.Label} (root scale {loader.Root.lossyScale.y:F3}):\n");
                foreach (var id in new[] { "hip", "head", "l_eye", "l_upperarm", "l_forearm", "l_hand", "r_hand", "l_thigh", "l_foot" })
                {
                    if (!fig.ByName.TryGetValue(id, out var i) || !sums.TryGetValue(i, out var acc) || acc.n == 0) { sb.Append($"  {id}: no vertices\n"); continue; }
                    var c = acc.sum / acc.n;
                    var p = fig.Bones[i].position;
                    sb.Append($"  {id,-11} n={acc.n,5}  bone={p:F3}  centroid={c:F3}  |d|={(c - p).magnitude * 100f:F1} cm  dy={(c - p).y * 100f:F1} cm\n");
                }
                // Cameras: where this figure's head and eye land in each camera's frame.
                foreach (var view in FindObjectsByType<CameraView>())
                {
                    sb.Append($"  camera {view.name}: {view.Describe()}\n");
                    foreach (var id in new[] { "head", "l_eye" })
                    {
                        if (!fig.ByName.TryGetValue(id, out var i)) continue;
                        var p = view.FramePoint(fig.Bones[i].position);
                        sb.Append($"    {id,-6} in frame: x={p.x:F2} y={p.y:F2} (0.5,0.5 = center)  depth={p.z:F2} m\n");
                    }
                }

                Debug.Log(sb.ToString());
                LastSelfTest = sb.ToString();
            }
        }

        // ---- bookkeeping

        static List<int> AllBones(SceneLoader.LoadedFigure fig)
        {
            var all = new List<int>(fig.Bones.Length);
            for (var i = 0; i < fig.Bones.Length; i++) all.Add(i);
            return all;
        }

        void SnapshotAll()
        {
            _known.Clear();
            _knownPos.Clear();
            foreach (var fig in loader.Figures.Values) Snapshot(fig);
        }

        void Snapshot(SceneLoader.LoadedFigure fig)
        {
            var q = new Quaternion[fig.Bones.Length];
            var p = new Vector3[fig.Bones.Length];
            for (var i = 0; i < q.Length; i++) { q[i] = fig.Bones[i].rotation; p[i] = fig.Bones[i].position; }
            _known[fig.Id] = q;
            _knownPos[fig.Id] = p;
        }
    }
}
