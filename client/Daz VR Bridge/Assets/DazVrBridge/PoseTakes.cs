// Takes: the whole scene's pose, kept so it can be come back to.
//
// The photoshoot loop. Mimic the pose, press a button, move on -- capture costs nothing,
// because every bone's world rotation is already what pose.commit sends and a take is a
// copy of data the client is holding anyway. Then, at the end, walk back through what
// you caught, recall the ones worth keeping, and export those to Daz as pose presets.
//
// Selection decides the scope, and getting that wrong was this feature's first mistake.
// Every take used to be the whole scene, always -- so catching one character you had just
// posed swept up four you had not, and putting that one back moved the others. Now:
//
//   a character is selected   save THAT character, restore a take onto it alone
//   nothing is selected       choose who goes in, all of them by default
//
// A whole-scene take still carries the props, because a hand resting on a chair is only a
// pose while the chair is where it was. A one-character take does not: a chair is not part
// of anybody's pose.
//
// They live on disk now, filed under the Daz scene they were taken in, because a
// photoshoot that ends when the headset comes off is a photoshoot nobody can use. A take
// only ever comes back into the scene it belongs to, and a figure whose bone count has
// changed since is skipped rather than mangled.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class PoseTakes : MonoBehaviour
    {
        /// Quick slots on the wheel, which are simply the most recent takes: a flick is
        /// for the one you just caught, not for choosing among twenty.
        public const int SlotCount = 3;
        /// Past this the oldest goes. A number, rather than filling a disk quietly.
        public const int MaxTakes = 24;

        internal sealed class FigurePose
        {
            public int[] Order;             // bone indices, parents first
            public Quaternion[] Rotation;   // world, per bone index
            public Vector3 RootPosition;
            public int Root = -1;
        }

        public sealed class Take
        {
            public string Name = "";
            public string Scene = "";       // the Daz scene it was taken in
            public long Stamp;              // unix seconds
            /// Who this take is of, when it is of one character. Empty means the scene.
            public string Subject = "";
            internal readonly Dictionary<string, FigurePose> Figures = new Dictionary<string, FigurePose>();
            internal readonly Dictionary<string, (Vector3 pos, Quaternion rot)> Nodes =
                new Dictionary<string, (Vector3, Quaternion)>();

            public int FigureCount => Figures.Count;
            public IEnumerable<string> FigureIds => Figures.Keys;

            public string When
            {
                get
                {
                    var local = DateTimeOffset.FromUnixTimeSeconds(Stamp).ToLocalTime();
                    return local.ToString("HH:mm");
                }
            }

            /// What it is of, for the list: a name where it is one character, a count
            /// where it is several. "3 figures" and "Nadine" are both answers; "a take"
            /// is not.
            public string Of => !string.IsNullOrEmpty(Subject)
                ? Subject
                : FigureCount + (FigureCount == 1 ? " figure" : " figures");

            public bool Holds(string figureId) => Figures.ContainsKey(figureId);
        }

        readonly List<Take> _takes = new List<Take>();
        SceneLoader _loader;
        PoseSync _poses;
        NodeSync _nodes;
        string _scene = "";

        /// Newest last.
        public IReadOnlyList<Take> Takes => _takes;
        public int Count => _takes.Count;

        void Start()
        {
            _loader = FindAnyObjectByType<SceneLoader>();
            _poses = FindAnyObjectByType<PoseSync>();
            _nodes = FindAnyObjectByType<NodeSync>();
            if (_loader) _loader.SceneBuilt += OnSceneBuilt;
            OnSceneBuilt();
        }

        void OnDestroy()
        {
            if (_loader) _loader.SceneBuilt -= OnSceneBuilt;
        }

        // A rebuild may be the same scene reloaded or a different one entirely. The
        // takes that belong to whatever is now open are read back from disk; the rest
        // stay on disk, where they will be waiting if that scene is opened again.
        void OnSceneBuilt()
        {
            _scene = _loader ? _loader.ScenePath : "";
            _takes.Clear();
            Load();
        }

        public bool Filled(int slot) => slot >= 0 && slot < SlotCount && slot < _takes.Count;
        public bool CanCapture => _loader && _loader.Figures.Count > 0 && !_loader.Busy;

        /// The wheel's slots count back from the newest: slot 0 is the last take made.
        public int SlotToIndex(int slot) => _takes.Count - 1 - slot;

        /// Stores a pose. `only` names the figures to include; null or empty means every
        /// figure in the scene, and then the props they are standing on as well.
        public int Capture(ICollection<string> only = null)
        {
            if (!CanCapture) return -1;
            var whole = only == null || only.Count == 0;
            var take = new Take
            {
                Scene = _scene,
                Stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            take.Name = take.When;

            foreach (var fig in _loader.Figures.Values)
            {
                if (!whole && !only.Contains(fig.Id)) continue;
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
                if (take.Figures.Count == 1) take.Subject = fig.Label;
            }
            if (take.Figures.Count == 0) return -1;
            if (take.Figures.Count > 1) take.Subject = "";

            // Props, but only for a take of the whole scene. A hand resting on a chair is
            // only a pose while the chair is where it was -- and a chair is not part of
            // one character's pose, so a take of one character leaves it alone.
            if (whole)
            {
                foreach (var pair in _loader.Nodes)
                {
                    var t = pair.Value.Go ? pair.Value.Go.transform : null;
                    if (t) take.Nodes[pair.Key] = (t.position, t.rotation);
                }
            }

            _takes.Add(take);
            while (_takes.Count > MaxTakes) _takes.RemoveAt(0);
            Save();
            return _takes.Count - 1;
        }

        /// Puts things back the way that take had them, and tells Daz. `onlyFigure`
        /// restores one character out of a take that holds several and leaves the rest
        /// -- and the props -- exactly where they are.
        public bool Recall(int index, string onlyFigure = null)
        {
            if (index < 0 || index >= _takes.Count || !_loader || _loader.Busy) return false;
            var take = _takes[index];
            var one = !string.IsNullOrEmpty(onlyFigure);
            if (one && !take.Holds(onlyFigure)) return false;

            foreach (var pair in take.Figures)
            {
                if (one && pair.Key != onlyFigure) continue;
                if (!_loader.Figures.TryGetValue(pair.Key, out var fig)) continue;
                var pose = pair.Value;
                // A figure that has been re-rigged since is not this figure any more.
                if (pose.Rotation.Length != fig.Bones.Length) continue;

                // Parents first. Rotating a parent carries its children, so a child set
                // before its parent is immediately undone by it.
                foreach (var i in pose.Order) fig.Bones[i].rotation = pose.Rotation[i];
                if (pose.Root >= 0 && pose.Root < fig.Bones.Length) fig.Bones[pose.Root].position = pose.RootPosition;

                _poses?.Commit(fig, $"VR take {take.Name}");
            }

            if (one) return true;   // props are not part of one character's pose

            foreach (var pair in take.Nodes)
            {
                if (!_loader.Nodes.TryGetValue(pair.Key, out var node) || !node.Go) continue;
                node.Go.transform.SetPositionAndRotation(pair.Value.pos, pair.Value.rot);
                _nodes?.Commit(node, $"VR take {take.Name}");
            }
            return true;
        }

        /// How many takes hold this figure. What the figure's own panel needs to know
        /// before offering to restore one onto it.
        public int HoldingAny(string figureId)
        {
            if (string.IsNullOrEmpty(figureId)) return 0;
            var count = 0;
            foreach (var take in _takes) if (take.Holds(figureId)) count++;
            return count;
        }

        public void Delete(int index)
        {
            if (index < 0 || index >= _takes.Count) return;
            _takes.RemoveAt(index);
            Save();
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
            Array.Sort(depth, order);
            return order;
        }

        // ---- disk
        //
        // One file for everything, filed by scene. Hand-rolled rather than JsonUtility:
        // the shape is dictionaries of arrays of quaternions, which Unity's serializer
        // does not do, and Newtonsoft is already here for the protocol.

        string Path => System.IO.Path.Combine(Application.persistentDataPath, "takes.json");

        void Save()
        {
            try
            {
                var all = ReadFile();
                all[Key(_scene)] = Write(_takes);
                File.WriteAllText(Path, all.ToString(Formatting.None));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DazVrBridge] Could not save takes: {e.Message}");
            }
        }

        void Load()
        {
            try
            {
                var all = ReadFile();
                if (all[Key(_scene)] is JArray stored) Read(stored, _takes);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DazVrBridge] Could not load takes: {e.Message}");
            }
        }

        JObject ReadFile()
        {
            if (!File.Exists(Path)) return new JObject();
            var text = File.ReadAllText(Path);
            return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
        }

        // The scene's path is the key. An unsaved scene has none, and its takes are kept
        // under a name of their own rather than being mixed in with a real scene's.
        static string Key(string scene) => string.IsNullOrEmpty(scene) ? "(unsaved)" : scene;

        static JArray Write(List<Take> takes)
        {
            var array = new JArray();
            foreach (var take in takes)
            {
                var figures = new JObject();
                foreach (var pair in take.Figures)
                {
                    var pose = pair.Value;
                    var rotation = new JArray();
                    foreach (var q in pose.Rotation) { rotation.Add(q.x); rotation.Add(q.y); rotation.Add(q.z); rotation.Add(q.w); }
                    var order = new JArray();
                    foreach (var i in pose.Order) order.Add(i);
                    figures[pair.Key] = new JObject
                    {
                        ["order"] = order,
                        ["rot"] = rotation,
                        ["root"] = pose.Root,
                        ["rootPos"] = new JArray(pose.RootPosition.x, pose.RootPosition.y, pose.RootPosition.z),
                    };
                }

                var nodes = new JObject();
                foreach (var pair in take.Nodes)
                    nodes[pair.Key] = new JArray(
                        pair.Value.pos.x, pair.Value.pos.y, pair.Value.pos.z,
                        pair.Value.rot.x, pair.Value.rot.y, pair.Value.rot.z, pair.Value.rot.w);

                array.Add(new JObject
                {
                    ["name"] = take.Name,
                    ["subject"] = take.Subject,
                    ["stamp"] = take.Stamp,
                    ["figures"] = figures,
                    ["nodes"] = nodes,
                });
            }
            return array;
        }

        void Read(JArray stored, List<Take> into)
        {
            foreach (var entry in stored)
            {
                if (!(entry is JObject o)) continue;
                var take = new Take
                {
                    Name = o.Value<string>("name") ?? "",
                    Subject = o.Value<string>("subject") ?? "",
                    Stamp = o.Value<long?>("stamp") ?? 0,
                    Scene = _scene,
                };

                if (o["figures"] is JObject figures)
                {
                    foreach (var pair in figures)
                    {
                        if (!(pair.Value is JObject f)) continue;
                        var rotation = (JArray)f["rot"];
                        var order = (JArray)f["order"];
                        if (rotation == null || order == null || rotation.Count != order.Count * 4) continue;

                        var pose = new FigurePose
                        {
                            Rotation = new Quaternion[order.Count],
                            Order = new int[order.Count],
                            Root = f.Value<int?>("root") ?? -1,
                        };
                        for (var i = 0; i < order.Count; i++)
                        {
                            pose.Order[i] = (int)order[i];
                            pose.Rotation[i] = new Quaternion(
                                (float)rotation[i * 4], (float)rotation[i * 4 + 1],
                                (float)rotation[i * 4 + 2], (float)rotation[i * 4 + 3]);
                        }
                        if (f["rootPos"] is JArray p && p.Count == 3)
                            pose.RootPosition = new Vector3((float)p[0], (float)p[1], (float)p[2]);
                        take.Figures[pair.Key] = pose;
                    }
                }

                if (o["nodes"] is JObject nodes)
                {
                    foreach (var pair in nodes)
                    {
                        if (!(pair.Value is JArray v) || v.Count != 7) continue;
                        take.Nodes[pair.Key] = (
                            new Vector3((float)v[0], (float)v[1], (float)v[2]),
                            new Quaternion((float)v[3], (float)v[4], (float)v[5], (float)v[6]));
                    }
                }

                into.Add(take);
            }
        }
    }
}
