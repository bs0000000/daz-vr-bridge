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
        public float handleRadius = 0.035f;
        public bool showHandles = true;

        public readonly List<BoneHandle> All = new List<BoneHandle>();

        void Start()
        {
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            loader.SceneBuilt += Rebuild;
            if (loader.Figures.Count > 0) Rebuild();
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
                    h.Init(fig, i, handleRadius);
                    go.transform.Find("vis").gameObject.SetActive(showHandles);
                    All.Add(h);
                    count++;
                }
                Debug.Log($"[DazVrBridge] {fig.Label}: {count} bone handles ({profile.Rig})");
            }
        }
    }
}
