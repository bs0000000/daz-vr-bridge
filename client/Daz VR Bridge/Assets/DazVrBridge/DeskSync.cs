// The desk's side of the session: what Daz is looking at, and what it is busy with.
//
//   select        (here -> Daz)  mirror a VR grab into Daz's own selection, so whoever is
//                                watching the monitor sees what the headset is holding
//   pose.request  (here -> Daz)  one figure's current pose, on demand
//   node.request  (here -> Daz)  one prop, camera or light's current transform
//   render.begin  (here -> Daz)  render the camera being held
//   render.state  (Daz -> here)  a render started or finished; Daz refuses edits while
//                                one runs, so the bridge drops into draft for its duration
//
// Resync exists for the moments the watcher cannot cover: coming back from a draft
// session, or reconnecting. Asking for one figure is cheaper than re-baking a scene by
// several orders of magnitude, and on a thousand-prop set that difference is the feature.

using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class DeskSync : MonoBehaviour
    {
        public BridgeSession session;
        public SceneLoader loader;

        [Tooltip("Select in Daz whatever is grabbed in VR, so the monitor follows the headset.")]
        public bool mirrorSelection = true;
        [Tooltip("Ignore repeat selections closer together than this (seconds).")]
        public float selectGuard = 0.15f;

        /// True while Daz is rendering. It refuses scene edits throughout, so posing has
        /// to stop being sent rather than be sent and refused.
        public static bool Rendering { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { Rendering = false; }

        string _lastSelected = "";
        float _nextSelect;
        bool _draftedForRender;
        PoseSync _poses;

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            _poses = FindAnyObjectByType<PoseSync>();
            session.ControlFrame += OnFrame;
        }

        void OnDestroy()
        {
            if (session) session.ControlFrame -= OnFrame;
        }

        // ---- selection

        /// Called when a handle is taken. `bone` is empty for props, cameras and lights.
        public void Selected(string nodeId, string bone)
        {
            if (!mirrorSelection || string.IsNullOrEmpty(nodeId)) return;
            if (!session || !session.ControlReady) return;
            var key = nodeId + "/" + bone;
            if (key == _lastSelected && Time.time < _nextSelect) return;
            _lastSelected = key;
            _nextSelect = Time.time + selectGuard;

            var h = new JObject { ["t"] = "select", ["node"] = nodeId };
            if (!string.IsNullOrEmpty(bone)) h["bone"] = bone;
            session.SendControl(h);
        }

        // ---- resync

        /// Ask Daz for one figure's pose. The reply is an ordinary pose.state, so PoseSync
        /// applies it through the path it already has.
        public void Resync(SceneLoader.LoadedFigure figure)
        {
            if (figure == null || !session || !session.ControlReady) return;
            session.SendControl(new JObject { ["t"] = "pose.request", ["figure"] = figure.Id });
        }

        public void Resync(SceneLoader.LoadedNode node)
        {
            if (node == null || !session || !session.ControlReady) return;
            session.SendControl(new JObject { ["t"] = "node.request", ["node"] = node.Id });
        }

        /// Everything, one message per figure and node. Still far cheaper than a re-bake:
        /// no geometry moves, only transforms.
        public void ResyncAll()
        {
            if (!loader) return;
            foreach (var figure in loader.Figures.Values) Resync(figure);
            foreach (var node in loader.Nodes.Values) Resync(node);
        }

        // ---- render

        public void Render(string cameraId)
        {
            if (!session || !session.ControlReady || Rendering) return;
            var h = new JObject { ["t"] = "render.begin" };
            if (!string.IsNullOrEmpty(cameraId)) h["camera"] = cameraId;
            session.SendControl(h);
        }

        /// The camera a hand is holding, if either is holding one; otherwise the first in
        /// the scene. Rendering the camera you are pointing is the whole gesture.
        public string CameraToRender()
        {
            if (!loader) return null;
            foreach (var handle in FindObjectsByType<NodeHandle>())
            {
                if (handle.IsGrabbed && handle.Node != null && handle.Node.Type == "camera") return handle.Node.Id;
            }
            foreach (var node in loader.Nodes.Values)
                if (node.Type == "camera") return node.Id;
            return null;
        }

        void OnFrame(BridgeFrame f)
        {
            if (f.Type != "render.state") return;
            var rendering = f.Header.Value<bool?>("rendering") ?? false;
            if (rendering == Rendering) return;
            Rendering = rendering;

            // Daz will not accept an edit while it renders, so rather than let commits be
            // refused one at a time, the bridge drafts for the duration and sends the lot
            // when the render is done. Anyone already drafting is left alone: leaving
            // draft is their decision, not a render's.
            if (!_poses) _poses = FindAnyObjectByType<PoseSync>();
            if (!_poses) return;
            if (rendering)
            {
                _draftedForRender = !PoseSync.Draft;
                if (_draftedForRender) _poses.SetDraft(true);
            }
            else if (_draftedForRender)
            {
                _draftedForRender = false;
                _poses.SetDraft(false);
            }
        }
    }
}
