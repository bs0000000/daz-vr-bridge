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

        [Tooltip("Let arm chains roll the forearm to recover wrist twist (profile: roll_assist).")]
        public bool rollAssist = true;

        [Header("Surfaces")]
        [Tooltip("Hands and feet stop at prop surfaces instead of passing through them.")]
        public bool surfaceSnap = true;
        [Tooltip("Hands and feet stop at lightweight body proxies on this and nearby figures.")]
        public bool bodyCollisions = true;
        [Tooltip("Half-thickness of the hand or foot, in meters. The sweep is centred on the middle of the bone, so this is how far that point stops from a surface.")]
        public float snapRadius = 0.03f;

        [Header("Joint limits")]
        [Tooltip("Turn a handle red when a bone it drives is at or past a Daz joint limit.")]
        public bool showLimits = true;
        [Tooltip("Draw a rod through the pinned bone along the rotation that ran out: along the bone means twist, across it means bend. Faster to read than words.")]
        public bool showLimitGizmo = true;
        [Tooltip("Also spell it out on the HUD. Off by default: reading while posing is a nuisance.")]
        public bool showLimitText;

        public readonly List<BoneHandle> All = new List<BoneHandle>();
        // Bones currently at a Daz limit, most pinned first: "l_upperarm y 40° at [-110, 40]".
        public string LimitText { get; private set; } = "";
        VrRig _rig;

        // Per handle, the last limit reading. The handle in your hand is re-checked every
        // frame; the rest ride a 15 Hz sweep, since they only drive the display.
        float[] _pinned;
        string[] _pinnedText;
        float _nextFullScan;
        readonly List<(string text, float rank)> _violations = new List<(string, float)>();

        // One rod, shown on whichever held bone has run out of range. A clamped joint sits
        // exactly on its limit, so the raw test flickers on and off every frame — it needs
        // hysteresis to switch, a minimum time on screen, and smoothing so it does not
        // jump between bones faster than it can be read.
        Transform _limitRod;
        bool _rodWanted, _rodOn;
        Vector3 _rodPos, _rodDir;
        float _rodHideAfter;

        void Start()
        {
            if (!loader) loader = FindAnyObjectByType<SceneLoader>();
            loader.SceneBuilt += Rebuild;
            if (loader.Figures.Count > 0) Rebuild();
        }

        void Update()
        {
            BridgeProfiler.EndFrame();
            if (All.Count == 0) return;

            // Visibility first: the limit scan uses it to skip handles you cannot see,
            // which with several figures in a scene is nearly all of them.
            if (showHandles)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Bridge.HandleFade");
                var tv = BridgeProfiler.Begin();
                UpdateVisibility();
                BridgeProfiler.End("handle fade", tv);
                UnityEngine.Profiling.Profiler.EndSample();
            }

            if (showLimits)
            {
                UnityEngine.Profiling.Profiler.BeginSample("Bridge.LimitScan");
                var tl = BridgeProfiler.Begin();
                UpdateLimits();
                BridgeProfiler.End("limit scan", tl);
                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        void UpdateVisibility()
        {
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
            if (_pinned == null || _pinned.Length != All.Count)
            {
                _pinned = new float[All.Count];
                _pinnedText = new string[All.Count];
            }

            var full = Time.time >= _nextFullScan;
            if (full) _nextFullScan = Time.time + 0.066f;
            _rodWanted = false;

            for (var i = 0; i < All.Count; i++)
            {
                var h = All[i];
                if (!h || h.ControlledBones == null) continue;
                // The held handle matters every frame; everything else is just the display,
                // and a handle faded out of sight has no display to update.
                if (h.Current == BoneHandle.State.Idle && (!full || !h.Visible))
                {
                    if (_pinned[i] != 0f) { _pinned[i] = 0f; _pinnedText[i] = null; h.SetOverLimit(0f); }
                    continue;
                }

                var held = h.Current == BoneHandle.State.Grabbed;

                // Nothing in this loop may allocate: it runs for every bone of every
                // handle and was the client's largest source of garbage, which showed up
                // as 8 ms GC spikes rather than as frame time.
                var pinned = 0f;
                var pinnedBone = -1;
                var pinnedAxis = 0;
                var pinnedStatus = default(LimitStatus);
                foreach (var bone in h.ControlledBones)
                {
                    var status = DazEuler.Check(loader, h.Figure, bone);
                    if (!status.Valid) continue;
                    var p = status.Pinned();
                    if (p <= 0f || p <= pinned) continue;

                    // A handle you are not holding only reddens when it is genuinely past
                    // a limit (clamping off). Merely resting against one is normal.
                    if (!held && status.Worst <= 0.5f) continue;

                    pinned = p;
                    pinnedBone = bone;
                    var axisName = status.WorstAxis;
                    pinnedAxis = axisName == "x" ? 0 : axisName == "y" ? 1 : 2;
                    pinnedStatus = status;
                }

                _pinned[i] = pinned;
                h.SetOverLimit(pinned);

                if (!held || pinnedBone < 0) { _pinnedText[i] = null; continue; }

                // Hysteresis: it takes a firm pin to appear and a clear release to go.
                if (pinned > (_rodOn ? 0.3f : 0.75f))
                {
                    _rodWanted = true;
                    _rodPos = h.Figure.Bones[pinnedBone].position;
                    _rodDir = DazEuler.AxisWorld(h.Figure, pinnedBone, pinnedAxis);
                }

                // Only built when it will actually be shown: the string, and the axis
                // naming behind it, are the expensive part.
                _pinnedText[i] = showLimitText ? Describe(h.Figure, pinnedBone, pinnedAxis, pinnedStatus) : null;
            }

            UpdateLimitRod();

            if (!showLimitText) { LimitText = ""; return; }
            _violations.Clear();
            for (var i = 0; i < All.Count; i++)
                if (_pinnedText[i] != null) _violations.Add((_pinnedText[i], _pinned[i]));

            if (_violations.Count == 0) { LimitText = ""; return; }
            _violations.Sort((a, b) => b.rank.CompareTo(a.rank));
            var lines = new List<string>();
            for (var i = 0; i < _violations.Count && i < 4; i++) lines.Add(_violations[i].text);
            if (_violations.Count > 4) lines.Add($"+{_violations.Count - 4} more");
            LimitText = "at Daz limits:\n" + string.Join("\n", lines);
        }

        string Describe(SceneLoader.LoadedFigure fig, int bone, int axis, LimitStatus status)
        {
            var value = axis == 0 ? status.Euler.x : axis == 1 ? status.Euler.y : status.Euler.z;
            var min = axis == 0 ? fig.LimitMin[bone].x : axis == 1 ? fig.LimitMin[bone].y : fig.LimitMin[bone].z;
            var max = axis == 0 ? fig.LimitMax[bone].x : axis == 1 ? fig.LimitMax[bone].y : fig.LimitMax[bone].z;
            var forward = RigProfile.Load(fig.Rig).ForwardOf(fig.Go.transform);
            var kind = DazEuler.AxisKind(fig, bone, axis, forward);
            var word = status.Worst > 0.5f ? "past" : "at";
            return $"{fig.BoneId[bone]} {kind} {value:F0}° {word} [{min:F0}, {max:F0}]";
        }

        // A rod through the bone along the rotation axis that ran out: lying along the
        // bone reads as twist, across it as bend. Which joint and which motion, without
        // words.
        void UpdateLimitRod()
        {
            // Stays up for a beat after the pin clears, so a joint you brush against does
            // not produce a flash you cannot read.
            if (_rodWanted) _rodHideAfter = Time.time + 0.5f;
            _rodOn = showLimitGizmo && Time.time < _rodHideAfter;

            if (!_rodOn)
            {
                if (_limitRod) _limitRod.gameObject.SetActive(false);
                return;
            }

            if (!_limitRod)
            {
                var rod = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                rod.name = "limit axis";
                Destroy(rod.GetComponent<Collider>());
                rod.transform.localScale = new Vector3(0.008f, 0.075f, 0.008f); // cylinders are 2 units tall
                var r = rod.GetComponent<Renderer>();
                r.sharedMaterial = BoneHandle.OverlayMaterial();
                var block = new MaterialPropertyBlock();
                var c = new Color(1f, 0.25f, 0.2f, 0.9f);
                block.SetColor("_BaseColor", c);
                block.SetColor("_Color", c);
                r.SetPropertyBlock(block);
                _limitRod = rod.transform;
            }

            var appearing = !_limitRod.gameObject.activeSelf;
            _limitRod.gameObject.SetActive(true);
            // Ease between bones rather than teleporting, so the eye can follow it.
            var t = appearing ? 1f : 1f - Mathf.Exp(-18f * Time.deltaTime);
            _limitRod.position = Vector3.Lerp(_limitRod.position, _rodPos, t);
            _limitRod.up = Vector3.Slerp(_limitRod.up, _rodDir, t); // the cylinder's axis is its Y
        }

        void OnDestroy()
        {
            if (_limitRod) Destroy(_limitRod.gameObject);
            if (loader) loader.SceneBuilt -= Rebuild;
        }

        public void ApplySettings()
        {
            foreach (var h in All)
            {
                h.ClampToLimits = clampToLimits; h.SurfaceSnap = surfaceSnap;
                h.BodyCollisions = bodyCollisions;
                h.RollAssist = rollAssist; h.IkIterations = ikIterations;
            }
        }

        public void Rebuild()
        {
            All.Clear(); // old handles died with the old DazScene
            _pinned = null;
            LimitText = "";
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
                    h.RollAssist = rollAssist;
                    h.SurfaceSnap = surfaceSnap;
                    h.BodyCollisions = bodyCollisions;
                    h.SnapRadius = snapRadius;
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
