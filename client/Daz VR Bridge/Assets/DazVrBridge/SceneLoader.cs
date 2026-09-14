// Turns a scene.manifest plus its assets into GameObjects.
//
// Flow: control connected -> scene.request -> scene.manifest -> diff hashes
// against the disk cache -> asset.request on bulk -> asset.data -> build.
//
// Figures get a bone hierarchy at BIND pose built from the manifest's
// figure-space origin/orient, a SkinnedMeshRenderer with bindposes derived
// from that hierarchy, and followers share the figure's bones. Applying the
// current pose from q_local is behind a toggle until Phase 2's self-test
// settles the exact convention.

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace DazVrBridge
{
    public sealed class SceneLoader : MonoBehaviour
    {
        public BridgeSession session;

        [Header("Bake options sent to the plugin")]
        public string textures = "none";
        [Range(4, 8)] public int influences = 4;

        [Header("Display")]
        [Tooltip("Pose bones from each bone's Daz world transform (ws), so the figure stands as it does in Daz Studio.")]
        public bool applyCurrentPose = true;
        public Material clayMaterial; // optional; instances get the base color

        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }

        JObject _manifest;
        readonly Dictionary<string, byte[]> _assets = new Dictionary<string, byte[]>();
        readonly HashSet<string> _pending = new HashSet<string>();
        GameObject _root;
        readonly List<UnityEngine.Object> _ownedResources = new List<UnityEngine.Object>();
        bool _requested;

        // Loaded figures by Daz node id, for PoseSync and (later) the posing UX.
        public sealed class LoadedFigure
        {
            public string Id;
            public string Label;
            public string Rig;                      // "genesis9", "genesis8", … from the manifest
            public GameObject Go;
            public Transform[] Bones;
            public JArray BoneJson;                 // manifest skeleton.bones, same order as Bones
            public Dictionary<string, int> ByName;
            public Matrix4x4[] BindPoses;

            // Per-bone manifest data, parsed once: the limit check runs every frame.
            public Quaternion[] OrientUnity;        // orient, mirrored into Unity space
            public Quaternion[] OrientDaz;          // orient, raw Daz components
            public string[] RotOrder;               // "XYZ", "YZX", …
            public Vector3[] LimitMin, LimitMax;    // degrees, per axis
            public bool[] Clamped;
            public int[] ParentBone;                // -1 when the parent is not a bone
            public string[] BoneId;
            public Vector3[] SegLocal;              // unit vector along the bone, in its own frame
            public int[] AxisI, AxisJ, AxisK;       // Euler decomposition axes (reversed order)
            public float[] AxisParity;
        }
        public readonly Dictionary<string, LoadedFigure> Figures = new Dictionary<string, LoadedFigure>();

        // Every manifest node by id (figures included), for NodeSync and the handles.
        public sealed class LoadedNode
        {
            public string Id;
            public string Label;
            public string Type;     // figure | follower | prop | camera | light | null
            public GameObject Go;
            public JObject Json;
        }
        public readonly Dictionary<string, LoadedNode> Nodes = new Dictionary<string, LoadedNode>();

        [Tooltip("Props larger than this (meters, longest side) get no grab handle: environments stay put.")]
        public float maxGrabbablePropSize = 2.0f;
        [Tooltip("Give props a mesh collider so hands and feet can rest on their surfaces. Costs a little load time per prop.")]
        public bool propMeshColliders = true;

        [Tooltip("Consulted before rebuilding after a desk-side node change, so a rebuild never lands mid-grab.")]
        public PoseSync poseSync;
        bool _nodesDirty;
        bool _buildReady;
        long _requestSeq = -1;
        float _retryAfter;

        public Transform Root => _root ? _root.transform : null;
        public event System.Action SceneBuilt;

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            if (!poseSync) poseSync = FindAnyObjectByType<PoseSync>();
            session.ControlFrame += OnControlFrame;
            session.BulkFrame += OnBulkFrame;
            session.ControlState += OnControlState;
            session.BulkState += OnBulkState;
        }

        void OnDestroy()
        {
            if (session)
            {
                session.ControlFrame -= OnControlFrame; session.BulkFrame -= OnBulkFrame;
                session.ControlState -= OnControlState; session.BulkState -= OnBulkState;
            }
            ClearScene();
        }

        void ClearScene()
        {
            if (_root) { _root.SetActive(false); Destroy(_root); }
            foreach (var resource in _ownedResources) if (resource) Destroy(resource);
            _ownedResources.Clear();
        }

        void OnControlState(BridgeClient.State s)
        {
            if (s == BridgeClient.State.Connected && !_requested) RequestScene();
            if (s != BridgeClient.State.Connected)
            { _requested = false; Busy = false; _buildReady = false; _pending.Clear(); _requestSeq = -1; }
        }

        void OnBulkState(BridgeClient.State s)
        {
            if (s == BridgeClient.State.Connected) RequestMissingAssets();
        }

        public void RequestScene()
        {
            if (!session.ControlReady || Busy || VrHand.AnyHolding) { _nodesDirty = true; return; }
            _nodesDirty = false;
            _requested = true;
            Busy = true;
            Status = "requesting scene…";
            _requestSeq = session.SendControl(new JObject
            {
                ["t"] = "scene.request",
                ["textures"] = textures,
                ["influences"] = influences,
                ["meshes"] = true,
            });
        }

        void OnControlFrame(BridgeFrame f)
        {
            switch (f.Type)
            {
                case "error":
                    if (f.Header.Value<long?>("ref_seq") == _requestSeq)
                    { Busy = false; Status = f.Header.Value<string>("msg");
                      _nodesDirty = f.Header.Value<string>("code") == "busy"; _retryAfter = Time.unscaledTime + 1f; }
                    break;
                case "scene.manifest":
                    if (f.Header.Value<long?>("ref_seq") != _requestSeq || !Busy) break;
                    OnManifest((JObject)f.Header["manifest"]);
                    break;
                case "scene.changed":
                    OnSceneChanged(f.Header.Value<string>("reason"));
                    break;
            }
        }

        // A prop added or deleted at the desk arrives as reason "nodes". Rebuilding
        // discards whatever is posed but uncommitted in VR, so it waits until no hand is
        // holding anything -- which is also when the user is most likely looking at the
        // desk. Meshes that did not change come straight from the asset cache, so the
        // rebuild is mostly manifest.
        void OnSceneChanged(string reason)
        {
            switch (reason)
            {
                case "loaded":
                case "cleared":
                    _nodesDirty = true;
                    break;
                case "nodes":
                    _nodesDirty = true;
                    break;
            }
        }

        void Update()
        {
            if (_buildReady && !VrHand.AnyHolding) { _buildReady = false; Build(); }
            if (!_nodesDirty || Busy || !session.ControlReady || Time.unscaledTime < _retryAfter || VrHand.AnyHolding) return;
            if (poseSync && poseSync.AnyGrabbed) return;
            _nodesDirty = false;
            RequestScene();
        }

        void OnManifest(JObject manifest)
        {
            _manifest = manifest;
            _assets.Clear();
            _pending.Clear();

            foreach (var a in manifest["assets"])
            {
                var hash = a.Value<string>("hash");
                if (AssetCache.TryGet(hash, out var bytes)) _assets[hash] = bytes;
                else _pending.Add(hash);
            }

            var nodes = ((JArray)manifest["nodes"]).Count;
            Status = $"manifest: {nodes} nodes, {_assets.Count} cached, {_pending.Count} to fetch";
            Debug.Log($"[DazVrBridge] {Status}");

            if (_pending.Count == 0) _buildReady = true;
            else RequestMissingAssets();
        }

        void RequestMissingAssets()
        {
            if (_pending.Count == 0 || !session.BulkReady) return;
            var hashes = new JArray();
            foreach (var h in _pending) hashes.Add(h);
            session.SendBulk(new JObject
            {
                ["t"] = "asset.request",
                ["hashes"] = hashes,
            });
            Status = $"fetching {_pending.Count} assets…";
        }

        void OnBulkFrame(BridgeFrame f)
        {
            if (f.Type == "error")
            {
                Busy = false; _buildReady = false; Status = "Asset transfer failed; refresh to retry";
                Debug.LogWarning($"[DazVrBridge] bulk: {f.Header.Value<string>("code")}: {f.Header.Value<string>("msg")}");
                return;
            }
            if (f.Type != "asset.data") return;

            var hash = f.Header.Value<string>("hash");
            if (!Busy || !_pending.Contains(hash)) return;
            if (!AssetCache.Put(hash, f.Payload)) { Busy = false; Status = "Asset verification failed; refresh to retry"; return; }
            _assets[hash] = f.Payload;
            _pending.Remove(hash);
            Status = $"fetching… {_pending.Count} left";

            if (_pending.Count == 0 && _manifest != null) _buildReady = true;
        }

        // ------------------------------------------------------------------
        // building

        void Build()
        {
            ClearScene();
            Figures.Clear();
            Nodes.Clear();

            // The whole Daz scene lives under this component's GameObject, so moving,
            // rotating or scaling that object places the scene relative to the XR rig.
            _root = new GameObject("DazScene");
            _root.transform.SetParent(transform, false);

            var byId = new Dictionary<string, GameObject>();
            var figures = Figures;
            var nodes = (JArray)_manifest["nodes"];

            // Pass 1: node objects at their Daz world transforms, expressed relative to the root.
            // Node rotations are Daz WORLD rotations (Daz quaternion sense) -> RotFromDazWorld.
            foreach (JObject n in nodes)
            {
                var id = n.Value<string>("id");
                var go = new GameObject(n.Value<string>("label"));
                go.transform.SetParent(_root.transform, false);
                var t = n["transform"];
                go.transform.localPosition = DazSpace.Pos(t["pos"]);
                go.transform.localRotation = DazSpace.RotFromDazWorld(t["rot"]);
                go.transform.localScale = DazSpace.Scale(t["scale"]);
                byId[id] = go;
                Nodes[id] = new LoadedNode { Id = id, Label = n.Value<string>("label"), Type = n.Value<string>("type"), Go = go, Json = n };
            }

            // Pass 2: hierarchy. Transforms are already absolute, so keep world placement.
            foreach (JObject n in nodes)
            {
                var parent = n.Value<string>("parent");
                if (parent != null && byId.TryGetValue(parent, out var pgo))
                    byId[n.Value<string>("id")].transform.SetParent(pgo.transform, true);
            }

            // Pass 3: figures (skeleton + skinned mesh), then followers, props, cameras, lights.
            foreach (JObject n in nodes)
                if (n.Value<string>("type") == "figure")
                    figures[n.Value<string>("id")] = BuildFigure(n, byId[n.Value<string>("id")]);

            foreach (JObject n in nodes)
            {
                var type = n.Value<string>("type");
                var id = n.Value<string>("id");
                var go = byId[id];
                if (type == "follower")
                {
                    var target = n.Value<string>("follower_of");
                    if (target != null && figures.TryGetValue(target, out var fig))
                    {
                        // A fitted follower lives in its figure's space; ignore its own transform.
                        go.transform.SetParent(fig.Go.transform, false);
                        go.transform.localPosition = Vector3.zero;
                        go.transform.localRotation = Quaternion.identity;
                        go.transform.localScale = Vector3.one;
                        AddSkinnedMesh(n, go, fig);
                    }
                }
                else if (type == "prop")
                {
                    AddStaticMesh(n, go);
                    var r = go.GetComponent<Renderer>();
                    if (r)
                    {
                        var size = r.bounds.size;
                        if (Mathf.Max(size.x, size.y, size.z) <= maxGrabbablePropSize)
                        {
                            var col = go.AddComponent<BoxCollider>();
                            col.isTrigger = true;
                            go.AddComponent<NodeHandle>().Init(Nodes[id], col);
                        }
                    }
                }
                else if (type == "camera")
                {
                    var view = go.AddComponent<CameraView>();
                    view.Init(n.Value<float?>("focal_mm") ?? 65f, n.Value<float?>("frame_width_mm") ?? 36f, n.Value<float?>("aspect") ?? 1.777f, n.Value<float?>("fov"));
                    CameraView.OrientFromDaz(go.transform, _root.transform, n, n["transform"]?["pos"]);
                    go.AddComponent<NodeHandle>().Init(Nodes[id], go.transform.Find("body").GetComponent<Collider>());
                }
                else if (type == "light")
                {
                    go.AddComponent<LightGizmo>().Init(n.Value<string>("kind") ?? "light", n.Value<float?>("intensity") ?? 1f);
                    go.AddComponent<NodeHandle>().Init(Nodes[id], go.transform.Find("body").GetComponent<Collider>());
                }

                // A prop parented to a bone (held in a hand) follows that bone.
                var parentBone = n.Value<string>("parent_bone");
                var parentNode = n.Value<string>("parent");
                if (parentBone != null && parentNode != null && figures.TryGetValue(parentNode, out var pf) && pf.ByName.TryGetValue(parentBone, out var bi))
                    go.transform.SetParent(pf.Bones[bi], true);
            }

            // Analytic capsules follow the skeleton and are queried only while a hand or
            // foot IK handle is moving. They add no animated physics or idle-frame work.
            foreach (var figure in figures.Values)
                figure.Go.AddComponent<BodyCollisionRig>().Init(figure);

            Busy = false;
            Status = $"loaded {nodes.Count} nodes, {figures.Count} figures";
            Debug.Log($"[DazVrBridge] {Status}");
            SceneBuilt?.Invoke();
        }

        LoadedFigure BuildFigure(JObject n, GameObject go)
        {
            var bones = (JArray)n["skeleton"]["bones"];
            var byName = new Dictionary<string, int>();
            var fig = new LoadedFigure
            {
                Id = n.Value<string>("id"),
                Label = n.Value<string>("label"),
                Rig = n.Value<string>("rig"),
                Go = go,
                Bones = new Transform[bones.Count],
                BoneJson = bones,
                ByName = byName,
                BindPoses = new Matrix4x4[bones.Count],
            };
            var skel = new GameObject("skeleton").transform;
            skel.SetParent(go.transform, false);

            // Bind pose: every bone at its figure-space origin with its absolute
            // orientation frame. Parents are set with worldPositionStays so the
            // local transform becomes (rest local), and a pose is then
            // localRotation = restLocal * q_pose.
            for (var i = 0; i < bones.Count; i++)
            {
                var b = (JObject)bones[i];
                var bt = new GameObject(b.Value<string>("id")).transform;
                bt.SetParent(skel, false);
                bt.localPosition = DazSpace.Pos(b["origin"]);
                bt.localRotation = DazSpace.Rot(b["orient"]);
                fig.Bones[i] = bt;
                byName[b.Value<string>("id")] = i;
            }
            for (var i = 0; i < bones.Count; i++)
            {
                var parent = bones[i].Value<string>("parent");
                if (parent != null && byName.TryGetValue(parent, out var pi))
                    fig.Bones[i].SetParent(fig.Bones[pi], true);
            }
            for (var i = 0; i < bones.Count; i++)
                fig.BindPoses[i] = fig.Bones[i].worldToLocalMatrix * go.transform.localToWorldMatrix;

            CacheBoneData(fig, bones);

            if (applyCurrentPose) ApplyWorldPose(fig, bones);

            AddSkinnedMesh(n, go, fig);
            return fig;
        }

        // Daz skinning is  p' = ws.pos + S * W * (p - origin)  with W the bone's world
        // rotation (Hamilton sense = conj of Daz's ws.rot). With bindposes taken from the
        // bind hierarchy above, that is reproduced by giving each bone the world transform
        //   position = root * Pos(ws.pos)
        //   rotation = root.rotation * W_unity * Rot(orient)
        // (the orient factor matches the bind hierarchy and cancels inside the skinning).
        // Parents are assigned before children so every world assignment is final.
        //
        // `wsBones` is any array of { id, ws:{pos,rot} } — the manifest's skeleton.bones
        // or a pose.state payload. Bones missing from it keep their current transform.
        public void ApplyWorldPose(LoadedFigure fig, JArray wsBones)
        {
            var target = new Dictionary<int, JToken>();
            foreach (JObject b in wsBones)
            {
                var ws = b["ws"];
                if (ws != null && fig.ByName.TryGetValue(b.Value<string>("id"), out var i)) target[i] = ws;
            }

            var order = new List<int>(target.Keys);
            order.Sort((a, b) => BoneDepth(fig, a).CompareTo(BoneDepth(fig, b)));

            var root = _root.transform;
            foreach (var i in order)
            {
                var ws = target[i];
                var bone = fig.Bones[i];
                bone.position = root.TransformPoint(DazSpace.Pos(ws["pos"]));
                bone.rotation = root.rotation * DazSpace.RotFromDazWorld(ws["rot"]) * DazSpace.Rot(fig.BoneJson[i]["orient"]);
            }
        }

        static void CacheBoneData(LoadedFigure fig, JArray bones)
        {
            var n = bones.Count;
            fig.OrientUnity = new Quaternion[n];
            fig.OrientDaz = new Quaternion[n];
            fig.RotOrder = new string[n];
            fig.LimitMin = new Vector3[n];
            fig.LimitMax = new Vector3[n];
            fig.Clamped = new bool[n];
            fig.ParentBone = new int[n];
            fig.BoneId = new string[n];
            fig.SegLocal = new Vector3[n];
            fig.AxisI = new int[n];
            fig.AxisJ = new int[n];
            fig.AxisK = new int[n];
            fig.AxisParity = new float[n];

            for (var i = 0; i < n; i++)
            {
                var b = (JObject)bones[i];
                var o = b["orient"];
                fig.OrientUnity[i] = DazSpace.Rot(o);
                fig.OrientDaz[i] = new Quaternion(o[0].Value<float>(), o[1].Value<float>(), o[2].Value<float>(), o[3].Value<float>());
                fig.RotOrder[i] = b.Value<string>("rot_order") ?? "XYZ";
                fig.Clamped[i] = b.Value<bool?>("clamped") ?? false;

                var lim = b["limits_deg"];
                if (lim != null)
                {
                    fig.LimitMin[i] = new Vector3(lim["x"][0].Value<float>(), lim["y"][0].Value<float>(), lim["z"][0].Value<float>());
                    fig.LimitMax[i] = new Vector3(lim["x"][1].Value<float>(), lim["y"][1].Value<float>(), lim["z"][1].Value<float>());
                }
                else
                {
                    fig.LimitMin[i] = new Vector3(-180f, -180f, -180f);
                    fig.LimitMax[i] = new Vector3(180f, 180f, 180f);
                }

                var parent = b.Value<string>("parent");
                fig.ParentBone[i] = parent != null && fig.ByName.TryGetValue(parent, out var pi) ? pi : -1;

                DazEuler.DecomposeAxes(fig.RotOrder[i], out fig.AxisI[i], out fig.AxisJ[i], out fig.AxisK[i], out fig.AxisParity[i]);

                fig.BoneId[i] = b.Value<string>("id");
                var segFigure = DazSpace.Pos(b["end"]) - DazSpace.Pos(b["origin"]);
                fig.SegLocal[i] = segFigure.sqrMagnitude > 1e-8f
                    ? (Quaternion.Inverse(fig.OrientUnity[i]) * segFigure).normalized
                    : Vector3.zero;
            }
        }

        // Inverse of the rotation half of ApplyWorldPose: a Unity bone's current world
        // rotation expressed as Daz's ws.rot (Daz quaternion sense, Daz world space).
        // The Quaternion is a raw 4-component container here, not a Unity rotation.
        public Quaternion DazWorldRotQ(LoadedFigure fig, int boneIndex)
        {
            var root = _root.transform;
            var wUnity = Quaternion.Inverse(root.rotation) * fig.Bones[boneIndex].rotation
                       * Quaternion.Inverse(fig.OrientUnity[boneIndex]);
            // RotFromDazWorld is (x, y, -z, w) and is its own inverse.
            return new Quaternion(wUnity.x, wUnity.y, -wUnity.z, wUnity.w);
        }

        public float[] DazWorldRotOf(LoadedFigure fig, int boneIndex)
        {
            var q = DazWorldRotQ(fig, boneIndex);
            return new[] { q.x, q.y, q.z, q.w };
        }

        // node.state -> place a node from its Daz world transform (and lens for cameras).
        public void ApplyNodeState(LoadedNode node, JObject header)
        {
            var transform = (JObject)header["transform"];
            var root = _root.transform;
            node.Go.transform.position = root.TransformPoint(DazSpace.Pos(transform["pos"]));
            node.Go.transform.rotation = root.rotation * DazSpace.RotFromDazWorld(transform["rot"]);
            if (node.Go.transform.parent == root) node.Go.transform.localScale = DazSpace.Scale(transform["scale"]);

            var view = node.Go.GetComponent<CameraView>();
            if (view && header["focal_mm"] != null)
            {
                view.SetLens(header.Value<float>("focal_mm"), header.Value<float?>("frame_width_mm"), header.Value<float?>("aspect"), header.Value<float?>("fov"));
                CameraView.OrientFromDaz(node.Go.transform, root, header, transform["pos"]);
            }
        }

        // A node's current world transform as Daz world pos (cm) and rot (Daz sense).
        public (float[] pos, float[] rot) DazWorldTransformOf(Transform t)
        {
            var root = _root.transform;
            var pos = DazSpace.ToDazPos(root.InverseTransformPoint(t.position));
            var q = Quaternion.Inverse(root.rotation) * t.rotation;
            return (pos, new[] { q.x, q.y, -q.z, q.w }); // inverse of RotFromDazWorld
        }

        public static int BoneDepth(LoadedFigure fig, int i)
        {
            var d = 0;
            for (var p = fig.BoneJson[i].Value<string>("parent"); p != null && fig.ByName.TryGetValue(p, out var pi); p = fig.BoneJson[pi].Value<string>("parent"))
                d++;
            return d;
        }

        void AddSkinnedMesh(JObject n, GameObject go, LoadedFigure fig)
        {
            var meshHash = n.Value<string>("mesh");
            var skinHash = n.Value<string>("skin");
            if (meshHash == null || !_assets.TryGetValue(meshHash, out var meshBytes)) return;

            var src = DzmMesh.Parse(meshBytes);
            var mesh = BuildUnityMesh(src, n.Value<string>("label"));
            mesh.bindposes = fig.BindPoses;

            if (skinHash != null && _assets.TryGetValue(skinHash, out var skinBytes))
                ApplySkin(mesh, DzsSkin.Parse(skinBytes));

            var host = new GameObject("mesh");
            host.transform.SetParent(go.transform, false);
            var smr = host.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = fig.Bones;
            smr.rootBone = fig.Bones.Length > 0 ? fig.Bones[0] : null;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterials = BuildMaterials(n, src.Groups);
        }

        void AddStaticMesh(JObject n, GameObject go)
        {
            var meshHash = n.Value<string>("mesh");
            if (meshHash == null || !_assets.TryGetValue(meshHash, out var meshBytes)) return;

            var src = DzmMesh.Parse(meshBytes);
            var mesh = BuildUnityMesh(src, n.Value<string>("label"));
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = BuildMaterials(n, src.Groups);

            // The real surface, for resting hands and feet on. Non-trigger, so the
            // grab colliders (which are triggers) stay out of these queries.
            if (propMeshColliders)
            {
                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mesh;
                col.convex = false;
            }
        }

        Mesh BuildUnityMesh(DzmMesh src, string name)
        {
            var mesh = new Mesh { name = name };
            _ownedResources.Add(mesh);
            if (src.VertexCount > 65000) mesh.indexFormat = IndexFormat.UInt32;

            var verts = new Vector3[src.VertexCount];
            for (var i = 0; i < verts.Length; i++)
                verts[i] = DazSpace.Pos(src.Positions[i * 3], src.Positions[i * 3 + 1], src.Positions[i * 3 + 2]);
            mesh.vertices = verts;

            if (src.Uvs != null)
            {
                var uvs = new Vector2[src.VertexCount];
                for (var i = 0; i < uvs.Length; i++) uvs[i] = new Vector2(src.Uvs[i * 2], src.Uvs[i * 2 + 1]);
                mesh.uv = uvs;
            }

            // One submesh per material group. Winding flips with the handedness.
            mesh.subMeshCount = src.Groups.Count;
            for (var g = 0; g < src.Groups.Count; g++)
            {
                var grp = src.Groups[g];
                var idx = new int[grp.Count * 3];
                for (var t = 0; t < grp.Count; t++)
                {
                    var s = (grp.Start + t) * 3;
                    idx[t * 3] = src.Triangles[s];
                    idx[t * 3 + 1] = src.Triangles[s + 2];
                    idx[t * 3 + 2] = src.Triangles[s + 1];
                }
                mesh.SetTriangles(idx, g, false);
            }

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static void ApplySkin(Mesh mesh, DzsSkin skin)
        {
            var vertCount = mesh.vertexCount;
            var perVertex = new NativeArray<byte>(vertCount, Allocator.Temp);
            var weights = new List<BoneWeight1>(vertCount * skin.Influences);

            for (var v = 0; v < vertCount; v++)
            {
                byte count = 0;
                for (var i = 0; i < skin.Influences; i++)
                {
                    var w = skin.Weights[v * skin.Influences + i];
                    if (w <= 0f) continue;
                    weights.Add(new BoneWeight1 { boneIndex = skin.Bones[v * skin.Influences + i], weight = w });
                    count++;
                }
                if (count == 0)
                {
                    // Unity requires at least one influence per vertex.
                    weights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                    count = 1;
                }
                perVertex[v] = count;
            }

            var arr = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(perVertex, arr);
            arr.Dispose();
            perVertex.Dispose();
        }

        // One material per submesh (= per material group), colored with the
        // Daz material's base color constant. Textures come in a later phase.
        Material[] BuildMaterials(JObject n, List<DzmMesh.Group> groups)
        {
            var mats = new Material[groups.Count];
            JArray defs = null;
            var matHash = n.Value<string>("materials");
            if (matHash != null && _assets.TryGetValue(matHash, out var bytes))
                defs = (JArray)JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes))["materials"];

            for (var i = 0; i < groups.Count; i++)
            {
                var m = clayMaterial ? new Material(clayMaterial) : new Material(DefaultShader());
                _ownedResources.Add(m);
                var color = new Color(0.7f, 0.7f, 0.7f);
                var matIndex = groups[i].Material;
                if (defs != null && matIndex < defs.Count)
                {
                    var c = defs[matIndex]["base_color"];
                    color = new Color(c[0].Value<float>(), c[1].Value<float>(), c[2].Value<float>());
                    m.name = defs[matIndex].Value<string>("name");
                }
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                else if (m.HasProperty("_Color")) m.SetColor("_Color", color);
                mats[i] = m;
            }
            return mats;
        }

        static Shader DefaultShader()
        {
            return Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        }
    }
}
