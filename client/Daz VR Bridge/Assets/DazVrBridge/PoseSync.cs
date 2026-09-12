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

        // Per figure: each bone's rotation as last confirmed by Daz (pose.state or manifest).
        readonly Dictionary<string, Quaternion[]> _known = new Dictionary<string, Quaternion[]>();

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
#else
            if (Input.GetKeyDown(KeyCode.C)) { CommitAll(); return true; }
            if (Input.GetKeyDown(KeyCode.T)) { SelfTest(); return true; }
            if (Input.GetKeyDown(KeyCode.R)) { loader.RequestScene(); return true; }
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
            if (_known.TryGetValue(fig.Id, out var known))
            {
                for (var i = 0; i < fig.Bones.Length; i++)
                    if (Quaternion.Angle(known[i], fig.Bones[i].rotation) > commitThresholdDeg) changed.Add(i);
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
            foreach (var i in boneIndices)
            {
                var q = loader.DazWorldRotOf(fig, i);
                bones.Add(new JArray(fig.BoneJson[i].Value<string>("id"), q[0], q[1], q[2], q[3]));
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
            foreach (var fig in loader.Figures.Values) Snapshot(fig);
        }

        void Snapshot(SceneLoader.LoadedFigure fig)
        {
            var q = new Quaternion[fig.Bones.Length];
            for (var i = 0; i < q.Length; i++) q[i] = fig.Bones[i].rotation;
            _known[fig.Id] = q;
        }
    }
}
