// Takes: the whole scene's pose, kept so it can be come back to.
//
// The photoshoot loop, reduced to the part worth proving first. Capture costs nothing
// -- every bone's world rotation is already what pose.commit sends, so a take is a copy
// of data the client is holding anyway -- and recall is that copy applied back. What is
// deliberately missing is review, thumbnails and export: those are the expensive half,
// and they are only worth building once taking and recalling has proved that posing
// freely and curating afterwards is actually how this wants to be used.
//
// A take is scene-level, not per-figure. Multi-figure is the point of this tool, and a
// pose of one character out of five is not a pose of the scene -- it would recall an
// arm into a chair that has since moved.
//
// Takes live for the session only. Nothing is written to disk, because the format that
// deserves to be written is the one after this has been used for a week.

using System.Collections.Generic;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class PoseTakes : MonoBehaviour
    {
        // Three, which is what is left of the session page after Draft, Take, Buttons,
        // Render and Back -- and they sit as one arc rather than scattered, so which slot
        // is which is a position rather than a number to read.
        public const int SlotCount = 3;

        sealed class FigurePose
        {
            public int[] Order;             // bone indices, parents first
            public Quaternion[] Rotation;   // world, per bone index
            public Vector3 RootPosition;
            public int Root = -1;
        }

        sealed class Take
        {
            public readonly Dictionary<string, FigurePose> Figures = new Dictionary<string, FigurePose>();
            public readonly Dictionary<string, (Vector3 pos, Quaternion rot)> Nodes =
                new Dictionary<string, (Vector3, Quaternion)>();
            public bool Filled;
        }

        readonly Take[] _takes = new Take[SlotCount];
        SceneLoader _loader;
        PoseSync _poses;
        NodeSync _nodes;

        public int Next { get; private set; }

        void Start()
        {
            _loader = FindAnyObjectByType<SceneLoader>();
            _poses = FindAnyObjectByType<PoseSync>();
            _nodes = FindAnyObjectByType<NodeSync>();
            for (var i = 0; i < SlotCount; i++) _takes[i] = new Take();
            if (_loader) _loader.SceneBuilt += Clear;
        }

        void OnDestroy()
        {
            if (_loader) _loader.SceneBuilt -= Clear;
        }

        // A rebuild replaces every figure and node, so the ids a take refers to may not
        // mean the same thing any more. Better to lose the takes than to recall a pose
        // onto whatever now happens to share an id.
        void Clear()
        {
            foreach (var take in _takes) { take.Figures.Clear(); take.Nodes.Clear(); take.Filled = false; }
            Next = 0;
        }

        public bool Filled(int slot) => slot >= 0 && slot < SlotCount && _takes[slot].Filled;

        public bool CanCapture => _loader && _loader.Figures.Count > 0 && !_loader.Busy;

        /// Stores the scene into the next slot, wrapping round when they are all used.
        public int Capture()
        {
            if (!CanCapture) return -1;
            var slot = Next;
            var take = _takes[slot];
            take.Figures.Clear();
            take.Nodes.Clear();

            foreach (var fig in _loader.Figures.Values)
            {
                var pose = new FigurePose
                {
                    Rotation = new Quaternion[fig.Bones.Length],
                    Order = DepthOrder(fig),
                };
                for (var i = 0; i < fig.Bones.Length; i++)
                {
                    pose.Rotation[i] = fig.Bones[i].rotation;
                    if (fig.ParentBone[i] < 0 && pose.Root < 0) pose.Root = i;
                }
                if (pose.Root >= 0) pose.RootPosition = fig.Bones[pose.Root].position;
                take.Figures[fig.Id] = pose;
            }

            // Props too: a hand resting on a chair is only a pose while the chair is
            // where it was.
            foreach (var pair in _loader.Nodes)
            {
                var t = pair.Value.Go ? pair.Value.Go.transform : null;
                if (t) take.Nodes[pair.Key] = (t.position, t.rotation);
            }

            take.Filled = true;
            Next = (slot + 1) % SlotCount;
            return slot;
        }

        /// Puts the scene back the way that take had it, and tells Daz.
        public bool Recall(int slot)
        {
            if (!Filled(slot) || !_loader || _loader.Busy) return false;
            var take = _takes[slot];

            foreach (var pair in take.Figures)
            {
                if (!_loader.Figures.TryGetValue(pair.Key, out var fig)) continue;
                var pose = pair.Value;
                if (pose.Rotation.Length != fig.Bones.Length) continue;

                // Parents first. Rotating a parent carries its children, so a child set
                // before its parent is immediately undone by it.
                foreach (var i in pose.Order) fig.Bones[i].rotation = pose.Rotation[i];
                if (pose.Root >= 0) fig.Bones[pose.Root].position = pose.RootPosition;

                _poses?.Commit(fig, $"VR take {slot + 1}");
            }

            foreach (var pair in take.Nodes)
            {
                if (!_loader.Nodes.TryGetValue(pair.Key, out var node) || !node.Go) continue;
                node.Go.transform.SetPositionAndRotation(pair.Value.pos, pair.Value.rot);
                _nodes?.Commit(node, $"VR take {slot + 1}");
            }
            return true;
        }

        // Bone indices sorted shallowest first, computed once per capture rather than
        // trusted from the manifest's order.
        static int[] DepthOrder(SceneLoader.LoadedFigure fig)
        {
            var order = new int[fig.Bones.Length];
            var depth = new int[fig.Bones.Length];
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
                var d = 0;
                for (var p = fig.ParentBone[i]; p >= 0 && d < order.Length; p = fig.ParentBone[p]) d++;
                depth[i] = d;
            }
            System.Array.Sort(depth, order);
            return order;
        }
    }
}
