// Props, cameras and lights both ways.
//
//   node.state     (Daz -> here)  re-places a node from its Daz world transform
//   node.transform (here -> Daz)  a moved prop/light; camera.set for cameras (+ focal)
//                                 Daz applies it as one undo step and answers with node.state

using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class NodeSync : MonoBehaviour
    {
        public BridgeSession session;
        public SceneLoader loader;

        readonly System.Collections.Generic.HashSet<string> _grabbed = new System.Collections.Generic.HashSet<string>();

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            session.ControlFrame += OnFrame;
        }

        public void SetGrabbed(string nodeId, bool on)
        {
            if (on) _grabbed.Add(nodeId); else _grabbed.Remove(nodeId);
        }

        void OnFrame(BridgeFrame f)
        {
            if (f.Type != "node.state") return;
            var id = f.Header.Value<string>("node");
            if (_grabbed.Contains(id)) return;
            if (!loader.Nodes.TryGetValue(id, out var node)) return;
            loader.ApplyNodeState(node, f.Header);
        }

        public void Commit(SceneLoader.LoadedNode node, string label)
        {
            var (pos, rot) = loader.DazWorldTransformOf(node.Go.transform);
            var isCamera = node.Type == "camera";
            var h = new JObject
            {
                ["t"] = isCamera ? "camera.set" : "node.transform",
                [isCamera ? "camera" : "node"] = node.Id,
                ["pos"] = new JArray(pos[0], pos[1], pos[2]),
                ["rot"] = new JArray(rot[0], rot[1], rot[2], rot[3]),
                ["commit"] = true,
                ["label"] = label,
            };
            if (isCamera)
            {
                var view = node.Go.GetComponent<CameraView>();
                if (view) h["focal_mm"] = view.FocalMm;
            }
            session.SendControl(h);
            Debug.Log($"[DazVrBridge] {node.Label}: committed transform");
        }
    }
}
