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
        [Tooltip("Apply each bone's current q_local on top of bind pose. Unverified until Phase 2.")]
        public bool applyCurrentPose = false;
        public Material clayMaterial; // optional; instances get the base color

        public string Status { get; private set; } = "";
        public bool Busy { get; private set; }

        JObject _manifest;
        readonly Dictionary<string, byte[]> _assets = new Dictionary<string, byte[]>();
        readonly HashSet<string> _pending = new HashSet<string>();
        GameObject _root;
        bool _requested;

        void Start()
        {
            if (!session) session = FindAnyObjectByType<BridgeSession>();
            session.ControlFrame += OnControlFrame;
            session.BulkFrame += OnBulkFrame;
            session.ControlState += OnControlState;
            session.BulkState += OnBulkState;
        }

        void OnControlState(BridgeClient.State s)
        {
            if (s == BridgeClient.State.Connected && !_requested) RequestScene();
            if (s != BridgeClient.State.Connected) _requested = false;
        }

        void OnBulkState(BridgeClient.State s)
        {
            if (s == BridgeClient.State.Connected) RequestMissingAssets();
        }

        public void RequestScene()
        {
            _requested = true;
            Busy = true;
            Status = "requesting scene…";
            session.SendControl(new JObject
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
                case "scene.manifest":
                    OnManifest((JObject)f.Header["manifest"]);
                    break;
                case "scene.changed":
                    var reason = f.Header.Value<string>("reason");
                    if (reason == "loaded" || reason == "cleared") RequestScene();
                    break;
            }
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

            if (_pending.Count == 0) Build();
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
                Debug.LogWarning($"[DazVrBridge] bulk: {f.Header.Value<string>("code")}: {f.Header.Value<string>("msg")}");
                return;
            }
            if (f.Type != "asset.data") return;

            var hash = f.Header.Value<string>("hash");
            if (!AssetCache.Put(hash, f.Payload)) return;
            _assets[hash] = f.Payload;
            _pending.Remove(hash);
            Status = $"fetching… {_pending.Count} left";

            if (_pending.Count == 0 && _manifest != null) Build();
        }

        // ------------------------------------------------------------------
        // building

        struct Figure
        {
            public GameObject Go;
            public Transform[] Bones;
            public Matrix4x4[] BindPoses;
        }

        void Build()
        {
            if (_root) Destroy(_root);

            // The whole Daz scene lives under this component's GameObject, so moving,
            // rotating or scaling that object places the scene relative to the XR rig.
            _root = new GameObject("DazScene");
            _root.transform.SetParent(transform, false);

            var byId = new Dictionary<string, GameObject>();
            var figures = new Dictionary<string, Figure>();
            var nodes = (JArray)_manifest["nodes"];

            // Pass 1: node objects at their Daz world transforms, expressed relative to the root.
            foreach (JObject n in nodes)
            {
                var go = new GameObject(n.Value<string>("label"));
                go.transform.SetParent(_root.transform, false);
                var t = n["transform"];
                go.transform.localPosition = DazSpace.Pos(t["pos"]);
                go.transform.localRotation = DazSpace.Rot(t["rot"]);
                go.transform.localScale = DazSpace.Scale(t["scale"]);
                byId[n.Value<string>("id")] = go;
            }

            // Pass 2: hierarchy. Transforms are already absolute, so keep world placement.
            foreach (JObject n in nodes)
            {
                var parent = n.Value<string>("parent");
                if (parent != null && byId.TryGetValue(parent, out var pgo))
                    byId[n.Value<string>("id")].transform.SetParent(pgo.transform, true);
            }

            // Pass 3: figures (skeleton + skinned mesh), then followers, then props.
            foreach (JObject n in nodes)
                if (n.Value<string>("type") == "figure")
                    figures[n.Value<string>("id")] = BuildFigure(n, byId[n.Value<string>("id")]);

            foreach (JObject n in nodes)
            {
                var type = n.Value<string>("type");
                var go = byId[n.Value<string>("id")];
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
                }
            }

            Busy = false;
            Status = $"loaded {nodes.Count} nodes, {figures.Count} figures";
            Debug.Log($"[DazVrBridge] {Status}");
        }

        Figure BuildFigure(JObject n, GameObject go)
        {
            var bones = (JArray)n["skeleton"]["bones"];
            var fig = new Figure { Go = go, Bones = new Transform[bones.Count], BindPoses = new Matrix4x4[bones.Count] };
            var byName = new Dictionary<string, int>();
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

            if (applyCurrentPose)
            {
                for (var i = 0; i < bones.Count; i++)
                {
                    var q = bones[i]["q_local"];
                    if (q != null) fig.Bones[i].localRotation = fig.Bones[i].localRotation * DazSpace.Rot(q);
                }
            }

            AddSkinnedMesh(n, go, fig);
            return fig;
        }

        void AddSkinnedMesh(JObject n, GameObject go, Figure fig)
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
        }

        static Mesh BuildUnityMesh(DzmMesh src, string name)
        {
            var mesh = new Mesh { name = name };
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
