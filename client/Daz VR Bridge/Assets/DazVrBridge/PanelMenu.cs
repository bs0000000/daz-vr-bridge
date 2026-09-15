// What goes on the panel, and how you get one.
//
// Press A with an empty hand and you get a board: about whatever you are pointing at
// if that is something in the scene, and about the session if it is not. Point at it,
// pull the trigger. Press A again to put it away.
//
// The split against the wheel is deliberate. The wheel is for what you reach for in
// the middle of a pose -- undo, snapping, a take -- where a flick in a learned
// direction beats reading anything. The panel is for what has to be read: which
// object this is, how big its textures are, what this button is about to delete.
// Putting either kind of thing on the other surface is what made the last round's
// settings page unfindable and its render impossible to take.
//
// Nothing here is new capability except Visible and Delete. It is the same session
// the wheel drives, with words on it.

using System.Collections.Generic;
using UnityEngine;

namespace DazVrBridge
{
    public sealed class PanelMenu : MonoBehaviour
    {
        [Tooltip("The controller whose A/X button opens the panel.")]
        public VrHand.Side panelHand = VrHand.Side.Right;
        [Tooltip("How far a pointed finger reaches for an object, in metres at life size.")]
        public float pickRange = 15f;

        VrPanel _panel;
        VrRig _rig;
        SceneLoader _loader;
        BoneHandles _handles;
        BridgeSession _session;
        DeskSync _desk;
        PoseSync _poses;
        NodeSync _nodes;
        PoseTakes _takes;
        VrMenu _wheel;

        SceneLoader.LoadedNode _node;         // what the object panel is about
        SceneLoader.LoadedFigure _figure;
        string _bone;
        VrPanel.Row _armed;                   // a Delete waiting for its second press
        float _armedUntil;

        static readonly RaycastHit[] Hits = new RaycastHit[24];
        static readonly string[] TextureChoices = { "none", "opacity", "full" };
        static readonly string[] SizeChoices = { "512", "1024", "2048", "4096" };
        static readonly int[] SizeValues = { 512, 1024, 2048, 4096 };
        static readonly string[] InfluenceChoices = { "4", "8" };
        static readonly string[] RegionChoices = { "whole scene", "1 m", "2 m", "4 m", "8 m" };
        static readonly float[] RegionValues = { 0f, 100f, 200f, 400f, 800f };

        void Start()
        {
            _rig = FindAnyObjectByType<VrRig>();
            _loader = FindAnyObjectByType<SceneLoader>();
            _handles = FindAnyObjectByType<BoneHandles>();
            _session = FindAnyObjectByType<BridgeSession>();
            _desk = FindAnyObjectByType<DeskSync>();
            _poses = FindAnyObjectByType<PoseSync>();
            _nodes = FindAnyObjectByType<NodeSync>();
            _takes = FindAnyObjectByType<PoseTakes>();
            _wheel = FindAnyObjectByType<VrMenu>();
            _panel = FindAnyObjectByType<VrPanel>();
            if (!_panel) _panel = gameObject.AddComponent<VrPanel>();
        }

        void Update()
        {
            if (!_panel) return;
            if (_armed != null && Time.time > _armedUntil) Disarm();

            var hand = HandFor(panelHand);
            if (!hand || !hand.IsTracked) return;
            if (!hand.PrimaryPressed || hand.HoldingSomething || VrHand.UiBlocked) return;

            if (_panel.IsOpen) { Disarm(); _panel.Close(); return; }
            OpenFor(hand);
        }

        /// Opens the session panel from somewhere else -- the wheel has a chip for it,
        /// because a surface nobody knows about is the failure this replaces.
        public void OpenSession()
        {
            if (!_panel) return;
            _node = null; _figure = null; _bone = null;
            var head = Camera.main;
            var at = head ? head.transform.position + head.transform.forward : Vector3.zero;
            _panel.Open("session", "Session", SessionTabs(), at, panelHand);
        }

        // ---- choosing what the panel is about

        void OpenFor(VrHand hand)
        {
            _node = null; _figure = null; _bone = null;
            var at = Pick(hand);

            if (_figure != null)
            {
                _panel.Open("bone:" + _figure.Id + "/" + _bone, _figure.Label, BoneTabs(), at, hand.side);
                _desk?.Selected(_figure.Id, _bone);
            }
            else if (_node != null)
            {
                _panel.Open("node:" + _node.Id, _node.Label, NodeTabs(), at, hand.side);
                _desk?.Selected(_node.Id, null);
            }
            else
            {
                _panel.Open("session", "Session", SessionTabs(), at, hand.side);
            }
        }

        // What the hand is on, or failing that what it points at. Reaching for something
        // works for what is in front of you; pointing works for the camera across the set.
        Vector3 Pick(VrHand hand)
        {
            if (Take(hand.Hover)) return Anchor();

            if (hand.AimRay(out var ray))
            {
                var range = pickRange * (_rig ? _rig.Scale : 1f);
                var n = Physics.RaycastNonAlloc(ray, Hits, range, ~0, QueryTriggerInteraction.Collide);
                var best = float.MaxValue;
                IGrabbable found = null;
                for (var i = 0; i < n; i++)
                {
                    var g = Hits[i].collider.GetComponentInParent<IGrabbable>();
                    if (g == null || Hits[i].distance >= best) continue;
                    best = Hits[i].distance;
                    found = g;
                }
                if (Take(found)) return Anchor();
                // Nothing under the ray: put the session panel where it was pointing.
                return ray.origin + ray.direction * (0.6f * (_rig ? _rig.Scale : 1f));
            }
            return Anchor();
        }

        bool Take(IGrabbable g)
        {
            if (g is NodeHandle nodeHandle && nodeHandle.Node != null)
            {
                _node = nodeHandle.Node;
                return true;
            }
            if (g is BoneHandle boneHandle && boneHandle.Figure != null)
            {
                _figure = boneHandle.Figure;
                _bone = boneHandle.BoneId;
                // A figure is a node too, so the whole-object rows work on it as well.
                if (_loader != null) _loader.Nodes.TryGetValue(_figure.Id, out _node);
                return true;
            }
            return false;
        }

        Vector3 Anchor()
        {
            if (_figure != null && _figure.ByName != null && _bone != null &&
                _figure.ByName.TryGetValue(_bone, out var index) && index < _figure.Bones.Length)
                return _figure.Bones[index].position;
            if (_node != null && _node.Go) return _node.Go.transform.position;
            var head = Camera.main;
            return head ? head.transform.position + head.transform.forward : Vector3.zero;
        }

        // ---- the object panel

        List<VrPanel.Tab> BoneTabs()
        {
            return One("Bone", () =>
            {
                var rows = new List<VrPanel.Row>
                {
                    Note("Bone", () => _bone),
                    Button("Resync this figure", () => _desk?.Resync(_figure), () => Ready),
                    Button("Select in Daz", () => _desk?.Selected(_figure.Id, _bone), () => Ready),
                    Button("Go to it", () => GoTo(Anchor())),
                };
                if (_node != null) rows.Add(Button("Whole figure...", () => Reopen(NodeTabs(), _node.Label)));
                return rows;
            });
        }

        List<VrPanel.Tab> NodeTabs()
        {
            return One(Title(_node), () =>
            {
                var rows = new List<VrPanel.Row>
                {
                    Note("Type", () => _node.Type),
                };

                if (_node.Type == "camera")
                {
                    // The reason this panel exists. Taking a render used to mean holding
                    // the camera steady in one hand while flicking a wheel with the other,
                    // which is not a shot, it is a wrestle. Frame it, let go, press here.
                    rows.Add(Button("Render this camera",
                        () => _desk?.Render(_node.Id),
                        () => Ready && !DeskSync.Rendering,
                        () => DeskSync.Rendering ? "rendering..." : ""));
                    rows.Add(new VrPanel.Row
                    {
                        Label = "Focal length",
                        Kind = VrPanel.Kind.Slider,
                        Min = 12f, Max = 200f, Unit = "mm",
                        Read = () => Lens ? Lens.FocalMm : 50f,
                        Write = mm => Lens?.SetLens(mm, null, null, null),
                        // Sent once, when the drag ends: a lens that commits per frame is
                        // forty undo steps for one decision.
                        Done = () => _nodes?.Commit(_node, "VR lens: " + _node.Label),
                        Enabled = () => Lens,
                    });
                }

                rows.Add(Button("Resync from Daz", () => _desk?.Resync(_node), () => Ready));
                rows.Add(Button("Select in Daz", () => _desk?.Selected(_node.Id, null), () => Ready));
                rows.Add(Button("Go to it", () => GoTo(Anchor())));
                rows.Add(new VrPanel.Row
                {
                    Label = "Visible",
                    Kind = VrPanel.Kind.Toggle,
                    Get = () => SceneLoader.IsNodeVisible(_node),
                    Set = on => _desk?.SetVisible(_node, on),
                });

                // Two presses, and only ever on something the left hand can undo.
                var delete = new VrPanel.Row { Label = "Delete", Kind = VrPanel.Kind.Button, Danger = true, Enabled = () => Ready };
                delete.Run = () =>
                {
                    if (_armed == delete) { _desk?.Delete(_node); Disarm(); _panel.Close(); return; }
                    Disarm();
                    _armed = delete;
                    _armedUntil = Time.time + 5f;
                    delete.Label = "Delete - press again";
                };
                rows.Add(delete);
                return rows;
            });
        }

        CameraView Lens => _node != null && _node.Go ? _node.Go.GetComponent<CameraView>() : null;

        void Disarm()
        {
            if (_armed != null) _armed.Label = "Delete";
            _armed = null;
        }

        void Reopen(List<VrPanel.Tab> tabs, string title)
        {
            _panel.Open(_node != null ? "node:" + _node.Id : "session", title, tabs, Anchor(), panelHand);
        }

        // Walks the rig over rather than teleporting the scene: the object ends up an
        // arm's length in front of you at the height it already had.
        void GoTo(Vector3 target)
        {
            var head = Camera.main;
            if (!head || !_rig) return;
            var forward = head.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return;
            var delta = target - forward.normalized * (0.8f * _rig.Scale) - head.transform.position;
            delta.y = 0f;
            _rig.transform.position += delta;
        }

        // ---- the session panel

        List<VrPanel.Tab> SessionTabs()
        {
            return new List<VrPanel.Tab>
            {
                new VrPanel.Tab { Label = "Contact", Build = ContactRows },
                new VrPanel.Tab { Label = "Session", Build = SessionRows },
                new VrPanel.Tab { Label = "Scene", Build = SceneRows },
                new VrPanel.Tab { Label = "Buttons", Build = ButtonRows },
            };
        }

        List<VrPanel.Row> ContactRows()
        {
            return new List<VrPanel.Row>
            {
                Toggle("Rest on surfaces", () => _handles && _handles.surfaceSnap, v => _handles.surfaceSnap = v),
                Toggle("Stop at bodies", () => _handles && _handles.bodyCollisions, v => _handles.bodyCollisions = v),
                Slider("Contact gap", 0f, 0.08f, 100f, "cm",
                    () => _handles ? _handles.snapRadius : 0f, v => _handles.snapRadius = v),
                Toggle("Joint limits", () => _handles && _handles.clampToLimits, v => _handles.clampToLimits = v),
                Toggle("Forearm roll", () => _handles && _handles.rollAssist, v => _handles.rollAssist = v),
                Toggle("Limit rods", () => _handles && _handles.showLimitGizmo, v => _handles.showLimitGizmo = v),
                // The first thing to reach for when contact feels wrong: it turns a guess
                // into a look at the surface hands are actually stopping against.
                Toggle("Show contact shapes", () => BodyCollisionRig.ShowShapes, v => BodyCollisionRig.ShowShapes = v),
                Slider("Handles appear at", 0.15f, 0.60f, 100f, "cm",
                    () => _handles ? _handles.showDistance : 0f, v => _handles.showDistance = v),
                Slider("Clavicle share", 0f, 1f, 100f, "%",
                    () => _handles ? _handles.clavicleWeight : 0f, v => _handles.clavicleWeight = v),
            };
        }

        List<VrPanel.Row> SessionRows()
        {
            var rows = new List<VrPanel.Row>
            {
                Toggle("Draft (nothing reaches Daz)", () => PoseSync.Draft, v => _poses?.SetDraft(v)),
                Slider("Haptics", 0f, 1f, 100f, "%", () => VrHand.HapticGain, v => VrHand.HapticGain = v, () => true),
                Button("Life size", LifeSize, () => _rig, () => (_rig ? _rig.Scale : 1f).ToString("0.00") + "x"),
                Button("Resync everything", () => _desk?.ResyncAll(), () => Ready && !DeskSync.Rendering),
                Button("Rebuild the scene", () => _loader?.RequestScene(), () => Ready && _loader && !_loader.Busy),
                Button("Capture a take", () => _takes?.Capture(), () => _takes && _takes.CanCapture),
            };
            for (var i = 0; i < PoseTakes.SlotCount; i++)
            {
                var slot = i;
                rows.Add(Button("Take " + (slot + 1),
                    () => _takes?.Recall(slot),
                    () => _takes && _takes.Filled(slot),
                    () => _takes && _takes.Filled(slot) ? "recall" : "empty"));
            }
            return rows;
        }

        List<VrPanel.Row> SceneRows()
        {
            return new List<VrPanel.Row>
            {
                Note("These take effect on rebuild", () => _loader && _loader.Busy ? _loader.Status : ""),
                Choice("Textures", TextureChoices,
                    () => Mathf.Max(0, System.Array.IndexOf(TextureChoices, _loader ? _loader.textures : "full")),
                    i => { if (_loader) _loader.textures = TextureChoices[i]; Remember(); }),
                Choice("Texture size", SizeChoices, () => Nearest(SizeValues, _loader ? _loader.texMax : 1024),
                    i => { if (_loader) _loader.texMax = SizeValues[i]; Remember(); }),
                Choice("Bones per vertex", InfluenceChoices, () => _loader && _loader.influences == 8 ? 1 : 0,
                    i => { if (_loader) _loader.influences = i == 0 ? 4 : 8; Remember(); }),
                Toggle("Approximate strand hair", () => _loader && _loader.hullProxies,
                    v => { if (_loader) _loader.hullProxies = v; Remember(); }),
                // A slice of a thousand-prop set instead of all of it, which is the
                // difference between five frames a second and a hundred and forty.
                Choice("Around Daz's selection", RegionChoices,
                    () => Nearest(RegionValues, _loader ? _loader.regionRadius : 0f),
                    i => { if (_loader) _loader.regionRadius = RegionValues[i]; Remember(); }),
                Button("Fetch the scene again", () => _loader?.RequestScene(), () => Ready && _loader && !_loader.Busy),
            };
        }

        // The tutorial, finally somewhere it can be found: a tab, not a toggle buried in
        // a wheel that you had to already know about to read what the wheel does.
        List<VrPanel.Row> ButtonRows()
        {
            return new List<VrPanel.Row>
            {
                Note("Trigger", () => "grab a bone or object; click here"),
                Note("Grip", () => "move the world; both hands turn and scale"),
                Note("Right A", () => "this panel, about whatever you point at"),
                Note("Right B, held", () => "the wheel, at your hand"),
                Note("Left Y, held", () => "undo, keep holding for more"),
                Note("Left X, held", () => "redo"),
                Note("A or X while holding", () => "pass through props and bodies"),
                Note("Both hands on one limb", () => "the second steers the joint"),
                Toggle("Labels beside the hands", () => BindingLabels.Show, v => BindingLabels.Show = v),
            };
        }

        void LifeSize()
        {
            var head = Camera.main;
            if (!_rig || !head) return;
            var pivot = head.transform.position;
            var factor = 1f / Mathf.Max(1e-4f, _rig.Scale);
            _rig.transform.position = pivot + (_rig.transform.position - pivot) * factor;
            _rig.transform.localScale = Vector3.one;
        }

        bool Ready => _session && _session.ControlReady;

        static int Nearest(int[] values, int want)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (Mathf.Abs(values[i] - want) < Mathf.Abs(values[best] - want)) best = i;
            return best;
        }

        static int Nearest(float[] values, float want)
        {
            var best = 0;
            for (var i = 1; i < values.Length; i++)
                if (Mathf.Abs(values[i] - want) < Mathf.Abs(values[best] - want)) best = i;
            return best;
        }

        void Remember() { StartScreen.Remember(_loader); }

        // Handle settings live in the wheel's PlayerPrefs, because they are the same
        // settings: two surfaces onto one session, not two sessions.
        void Applied()
        {
            _handles?.ApplySettings();
            _wheel?.Persist();
        }

        string Title(SceneLoader.LoadedNode node) => node == null ? "Object" : node.Label;

        List<VrPanel.Tab> One(string label, System.Func<List<VrPanel.Row>> build) =>
            new List<VrPanel.Tab> { new VrPanel.Tab { Label = label, Build = build } };

        VrHand HandFor(VrHand.Side side)
        {
            if (!_rig) _rig = FindAnyObjectByType<VrRig>();
            if (!_rig) return null;
            return side == VrHand.Side.Left ? _rig.Left : _rig.Right;
        }

        // ---- row shorthands, so the lists above read as lists

        VrPanel.Row Note(string label, System.Func<string> value) =>
            new VrPanel.Row { Label = label, Kind = VrPanel.Kind.Note, Value = value };

        VrPanel.Row Button(string label, System.Action run, System.Func<bool> enabled = null, System.Func<string> value = null) =>
            new VrPanel.Row { Label = label, Kind = VrPanel.Kind.Button, Run = run, Enabled = enabled, Value = value };

        VrPanel.Row Toggle(string label, System.Func<bool> get, System.Action<bool> set) =>
            new VrPanel.Row
            {
                Label = label, Kind = VrPanel.Kind.Toggle, Get = get,
                Set = v => { set(v); Applied(); },
            };

        VrPanel.Row Slider(string label, float min, float max, float display, string unit,
                           System.Func<float> read, System.Action<float> write, System.Func<bool> enabled = null) =>
            new VrPanel.Row
            {
                Label = label, Kind = VrPanel.Kind.Slider, Min = min, Max = max, Display = display, Unit = unit,
                Read = read, Write = v => { write(v); _handles?.ApplySettings(); },
                Done = () => _wheel?.Persist(),
                Enabled = enabled ?? (() => _handles),
            };

        VrPanel.Row Choice(string label, string[] choices, System.Func<int> index, System.Action<int> pick) =>
            new VrPanel.Row { Label = label, Kind = VrPanel.Kind.Choice, Choices = choices, Index = index, Pick = pick };
    }
}
