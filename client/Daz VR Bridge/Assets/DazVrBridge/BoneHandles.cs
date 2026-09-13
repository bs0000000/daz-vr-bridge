// Spawns a BoneHandle at every grabbable bone of every loaded figure, per the
// figure's rig profile. Handles are children of the bone, so they follow poses.

using System.Collections.Generic;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class BoneHandles : MonoBehaviour
    {
        public SceneLoader loader;
        [Tooltip("Grab sphere radius in meters.")]
        public float handleRadius = 0.04f;
        [Tooltip("Root bone (hip) handle: ring radius and tube thickness in meters.")]
        public float rootRingRadius = 0.22f;
        public float rootRingTube = 0.012f;
        public bool showHandles = true;

        [Header("Visibility by controller distance")]
        [Tooltip("Handles farther than this from every controller are hidden.")]
        public float showDistance = 0.30f;
        [Tooltip("Handles closer than this are fully opaque; in between they fade.")]
        public float fullDistance = 0.10f;
        [Tooltip("Opacity when no controller is tracked (desktop testing).")]
        [Range(0f, 1f)] public float alphaWithoutHands = 0.35f;

        public readonly List<BoneHandle> All = new List<BoneHandle>();
        VrRig _rig;

        void Start()
        {
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            loader.SceneBuilt += Rebuild;
            if (loader.Figures.Count > 0) Rebuild();
        }

        void Update()
        {
            if (!showHandles || All.Count == 0) return;
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();

            var hands = 0;
            Vector3 l = default, r = default;
            if (_rig && _rig.Left && _rig.Left.IsTracked) { l = _rig.Left.transform.position; hands++; }
            if (_rig && _rig.Right && _rig.Right.IsTracked) { r = _rig.Right.transform.position; hands++; }

            foreach (var h in All)
            {
                if (!h) continue;
                if (hands == 0) { h.SetVisibility(alphaWithoutHands); continue; }
                var d = float.MaxValue;
                if (_rig.Left && _rig.Left.IsTracked) d = Mathf.Min(d, h.DistanceTo(l));
                if (_rig.Right && _rig.Right.IsTracked) d = Mathf.Min(d, h.DistanceTo(r));
                h.SetVisibility(1f - Mathf.InverseLerp(fullDistance, showDistance, d));
            }
        }

        void OnDestroy()
        {
            if (loader) loader.SceneBuilt -= Rebuild;
        }

        public void Rebuild()
        {
            All.Clear(); // old handles died with the old DazScene
            foreach (var fig in loader.Figures.Values)
            {
                var profile = RigProfile.Load(fig.Rig);
                var count = 0;
                for (var i = 0; i < fig.Bones.Length; i++)
                {
                    var id = fig.BoneJson[i].Value<string>("id");
                    if (!profile.IsGrabbable(id)) continue;

                    var go = new GameObject($"handle:{id}");
                    go.transform.SetParent(fig.Bones[i], false);
                    var h = go.AddComponent<BoneHandle>();
                    if (id == profile.Root) h.InitRing(fig, i, rootRingRadius, rootRingTube);
                    else h.Init(fig, i, handleRadius);
                    go.transform.Find("vis").gameObject.SetActive(showHandles);
                    All.Add(h);
                    count++;
                }
                Debug.Log($"[DazVrBridge] {fig.Label}: {count} bone handles ({profile.Rig})");
            }
        }
    }
}
