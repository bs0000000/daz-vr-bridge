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
        [Tooltip("Hands and feet drag the whole limb (two-bone IK). Off: every handle is plain FK.")]
        public bool ikEnabled = true;
        [Tooltip("Keep every joint inside its Daz limits while dragging, handing the refused reach to the clavicle. Off: the old free solve, with Daz correcting on commit.")]
        public bool clampToLimits = true;
        [Range(1, 8)]
        [Tooltip("Passes of solve-then-clamp. More = closer to the target when limits bind.")]
        public int ikIterations = 4;

        [Header("Visibility by controller distance")]
        [Tooltip("Handles farther than this from every controller are hidden.")]
        public float showDistance = 0.30f;
        [Tooltip("Handles closer than this are fully opaque; in between they fade.")]
        public float fullDistance = 0.10f;
        [Tooltip("Opacity when no controller is tracked (desktop testing).")]
        [Range(0f, 1f)] public float alphaWithoutHands = 0.35f;

        [Header("Joint limits")]
        [Tooltip("Turn a handle red when a bone it drives is past a Daz joint limit, and list them on the HUD.")]
        public bool showLimits = true;

        public readonly List<BoneHandle> All = new List<BoneHandle>();
        // Bones currently past a Daz limit, worst first: "l_upperarm y +14.2°".
        public string LimitText { get; private set; } = "";
        VrRig _rig;
        readonly List<(string text, float over)> _violations = new List<(string, float)>();

        void Start()
        {
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            loader.SceneBuilt += Rebuild;
            if (loader.Figures.Count > 0) Rebuild();
        }

        void Update()
        {
            if (All.Count == 0) return;
            if (showLimits) UpdateLimits();
            if (!showHandles) return;
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();

            var hands = 0;
            Vector3 l = default, r = default;
            if (_rig && _rig.Left && _rig.Left.IsTracked) { l = _rig.Left.transform.position; hands++; }
            if (_rig && _rig.Right && _rig.Right.IsTracked) { r = _rig.Right.transform.position; hands++; }

            // Reveal distances are physical (arm's reach); scale them with the rig.
            var s = _rig ? _rig.Scale : 1f;
            foreach (var h in All)
            {
                if (!h) continue;
                if (hands == 0) { h.SetVisibility(alphaWithoutHands); continue; }
                var d = float.MaxValue;
                if (_rig.Left && _rig.Left.IsTracked) d = Mathf.Min(d, h.DistanceTo(l));
                if (_rig.Right && _rig.Right.IsTracked) d = Mathf.Min(d, h.DistanceTo(r));
                h.SetVisibility(1f - Mathf.InverseLerp(fullDistance * s, showDistance * s, d));
            }
        }

        // Daz clamps to its joint limits on commit, which is what makes a pose "snap back"
        // when you let go. Showing the violation while you drag makes that predictable.
        void UpdateLimits()
        {
            _violations.Clear();
            foreach (var h in All)
            {
                if (!h || h.ControlledBones == null) continue;
                var worst = 0f;
                foreach (var bone in h.ControlledBones)
                {
                    var status = DazEuler.Check(loader, h.Figure, bone);
                    if (!status.Valid || status.Worst <= 0.5f) continue;
                    if (status.Worst > worst) worst = status.Worst;
                    var axis = status.WorstAxis;
                    var value = axis == "x" ? status.Euler.x : axis == "y" ? status.Euler.y : status.Euler.z;
                    var min = axis == "x" ? h.Figure.LimitMin[bone].x : axis == "y" ? h.Figure.LimitMin[bone].y : h.Figure.LimitMin[bone].z;
                    var max = axis == "x" ? h.Figure.LimitMax[bone].x : axis == "y" ? h.Figure.LimitMax[bone].y : h.Figure.LimitMax[bone].z;
                    var id = h.Figure.BoneJson[bone].Value<string>("id");
                    _violations.Add(($"{id} {axis} {value:F0}° outside [{min:F0}, {max:F0}]", status.Worst));
                }
                h.SetOverLimit(worst);
            }

            if (_violations.Count == 0) { LimitText = ""; return; }
            _violations.Sort((a, b) => b.over.CompareTo(a.over));
            var lines = new List<string>();
            for (var i = 0; i < _violations.Count && i < 4; i++) lines.Add(_violations[i].text);
            if (_violations.Count > 4) lines.Add($"+{_violations.Count - 4} more");
            LimitText = "past Daz limits:\n" + string.Join("\n", lines);
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
                var ik = 0;
                for (var i = 0; i < fig.Bones.Length; i++)
                {
                    var id = fig.BoneJson[i].Value<string>("id");
                    if (!profile.IsGrabbable(id)) continue;

                    var go = new GameObject($"handle:{id}");
                    go.transform.SetParent(fig.Bones[i], false);
                    var h = go.AddComponent<BoneHandle>();
                    h.Loader = loader;
                    h.ClampToLimits = clampToLimits;
                    h.IkIterations = ikIterations;
                    if (id == profile.Root) h.InitRing(fig, i, rootRingRadius, rootRingTube);
                    else h.Init(fig, i, handleRadius);
                    if (ikEnabled && profile.IkByEndBone.TryGetValue(id, out var chain) && h.SetIkChain(profile, chain)) ik++;
                    go.transform.Find("vis").gameObject.SetActive(showHandles);
                    All.Add(h);
                    count++;
                }
                Debug.Log($"[DazVrBridge] {fig.Label}: {count} bone handles, {ik} IK effectors ({profile.Rig})");
            }
        }
    }
}
