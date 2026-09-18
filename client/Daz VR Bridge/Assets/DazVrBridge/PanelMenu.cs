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
using TMPro;
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
        int _take = -1;                       // the take whose page is open, -1 for the list
        VrPanel.Row _armed;                   // a Delete waiting for its second press
        float _armedUntil;
        bool _aiming;
        LineRenderer _beam;
        GameObject _spot;
        TextMeshPro _caption;

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
            if (!hand || !hand.IsTracked || hand.HoldingSomething || VrHand.UiBlocked) { StopAiming(); return; }

            // Hold A to aim, release to open. A quick press is the same gesture with the
            // holding left out, so it needs no rule of its own: the press aims for one
            // frame and the release on the frame after fires at what that frame found.
            //
            // The aiming is the point. Reaching for something tells you what you have by
            // touching it; pointing across a room tells you nothing at all unless what
            // you are pointing at says its name back.
            if (hand.PrimaryPressed)
            {
                if (_panel.IsOpen) { Disarm(); _panel.Close(); return; }
                _aiming = true;
                Aim(hand);
                return;
            }
            if (!_aiming) return;
            if (hand.PrimaryHeld) { Aim(hand); return; }

            StopAiming();
            OpenFor(hand);
        }

        // ---- aiming

        void Aim(VrHand hand)
        {
            _node = null; _figure = null; _bone = null;
            var point = Pick(hand);
            hand.AimRay(out var ray);

            var scale = _rig ? _rig.Scale : 1f;
            var beam = Beam();
            beam.SetPosition(0, ray.origin + ray.direction * (0.03f * scale));
            beam.SetPosition(1, point);
            beam.widthMultiplier = 0.004f * scale;
            beam.gameObject.SetActive(true);

            var found = _figure != null || _node != null;
            var spot = Spot();
            spot.transform.position = point;
            spot.transform.localScale = Vector3.one * ((found ? 0.03f : 0.015f) * scale);
            spot.SetActive(true);

            var caption = Caption();
            var text = _node != null ? _node.Label
                     : _figure != null ? _figure.Label
                     : "Session settings";
            if (caption.text != text) caption.text = text;
            var head = Camera.main;
            var at = point + Vector3.up * (0.07f * scale);
            if (head)
                caption.transform.SetPositionAndRotation(
                    at, Quaternion.LookRotation(at - head.transform.position, Vector3.up));
            caption.transform.localScale = Vector3.one * scale;
            caption.gameObject.SetActive(true);
        }

        void StopAiming()
        {
            _aiming = false;
            if (_beam) _beam.gameObject.SetActive(false);
            if (_spot) _spot.SetActive(false);
            if (_caption) _caption.gameObject.SetActive(false);
        }

        LineRenderer Beam()
        {
            if (_beam) return _beam;
            var go = new GameObject("panel aim");
            _beam = go.AddComponent<LineRenderer>();
            _beam.useWorldSpace = true;
            _beam.positionCount = 2;
            _beam.sharedMaterial = BoneHandle.OverlayMaterial();
            _beam.numCapVertices = 2;
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.startColor = _beam.endColor = new Color(1f, 0.85f, 0.35f, 0.75f);
            return _beam;
        }

        GameObject Spot()
        {
            if (_spot) return _spot;
            _spot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _spot.name = "panel aim spot";
            Destroy(_spot.GetComponent<Collider>());
            var r = _spot.GetComponent<Renderer>();
            r.sharedMaterial = BoneHandle.OverlayMaterial();
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(1f, 0.85f, 0.35f, 0.95f));
            r.SetPropertyBlock(block);
            return _spot;
        }

        TextMeshPro Caption()
        {
            if (_caption) return _caption;
            var go = new GameObject("panel aim label");
            _caption = go.AddComponent<TextMeshPro>();
            _caption.rectTransform.sizeDelta = new Vector2(0.5f, 0.05f);
            _caption.alignment = TextAlignmentOptions.Center;
            _caption.textWrappingMode = TextWrappingModes.NoWrap;
            _caption.enableAutoSizing = true;
            _caption.fontSizeMin = 0.001f;
            _caption.fontSizeMax = 300f;
            _caption.color = Color.white;
            var font = _caption.fontMaterial;
            var zTest = Shader.PropertyToID("_ZTestMode");
            if (font.HasProperty(zTest)) font.SetFloat(zTest, (float)UnityEngine.Rendering.CompareFunction.Always);
            font.renderQueue = 4001;

            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "ground";
            Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(go.transform, false);
            back.transform.localPosition = new Vector3(0f, 0f, 0.002f);
            back.transform.localScale = new Vector3(0.52f, 0.062f, 1f);
            var backRenderer = back.GetComponent<Renderer>();
            backRenderer.sharedMaterial = BoneHandle.OverlayMaterial();
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(0.05f, 0.07f, 0.10f, 0.78f));
            backRenderer.SetPropertyBlock(block);
            return _caption;
        }

        void OnDestroy()
        {
            if (_beam) Destroy(_beam.gameObject);
            if (_spot) Destroy(_spot);
            if (_caption) Destroy(_caption.gameObject);
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
            // Whatever the last aiming frame settled on -- which is what the label under
            // the cursor was showing. Picking again here would let a flick during the
            // release open a panel about something the aim never named.
            var at = Anchor();

            // A bone opens its FIGURE's panel. Pointing at someone's forearm is how you
            // point at a person -- there is nothing on this panel that is about one bone,
            // and until there is, making people aim at a joint to reach a character is
            // asking them to know how the rig is put together to use the tool.
            //
            // The bone is still remembered, for one thing: Select in Daz selects the
            // bone that was actually under the ray, which is more use at the desk than
            // selecting the figure and more precise than anything else here needs.
            if (_node != null)
            {
                _panel.Open("node:" + _node.Id, _node.Label, NodeTabs(), at, hand.side);
                _desk?.Selected(_node.Id, _bone);
            }
            else if (_figure != null)
            {
                _panel.Open("figure:" + _figure.Id, _figure.Label, FigureTabs(), at, hand.side);
                _desk?.Selected(_figure.Id, _bone);
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
                NodeTag tagged = null;
                for (var i = 0; i < n; i++)
                {
                    if (Hits[i].distance >= best) continue;
                    // A grab handle where there is one, and the node itself where there
                    // is not: a prop too big to pick up is still a thing you can ask
                    // about, and being told nothing at all is the worst answer there is.
                    var g = Hits[i].collider.GetComponentInParent<IGrabbable>();
                    var tag = g != null ? null : Hits[i].collider.GetComponentInParent<NodeTag>();
                    if (g == null && tag == null) continue;
                    best = Hits[i].distance;
                    found = g;
                    tagged = tag;
                }
                if (Take(found)) return Anchor();
                if (tagged != null && tagged.Node != null)
                {
                    _node = tagged.Node;
                    return Anchor();
                }
                // Nothing grabbable under the ray: the spot lands on the first solid
                // thing it meets, or out at arm's reach when it meets nothing at all.
                if (Physics.Raycast(ray, out var wall, range)) return wall.point;
                return ray.origin + ray.direction * (0.9f * (_rig ? _rig.Scale : 1f));
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

        List<VrPanel.Tab> FigureTabs()
        {
            return One("Figure", () => new List<VrPanel.Row>
            {
                Note("Pointing at", () => _bone),
                Button("Resync the pose", () => _desk?.Resync(_figure), () => Ready),
                Button("Select in Daz", () => _desk?.Selected(_figure.Id, _bone), () => Ready),
                Button("Go to it", () => GoTo(Anchor())),
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
                // Which bone the ray was on. Only shown when it means something, and it
                // is what Select in Daz below will reach for.
                if (_figure != null && !string.IsNullOrEmpty(_bone))
                    rows.Add(Note("Pointing at", () => _bone));

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

                // A figure's truth is its pose, not the one transform under its root.
                if (_figure != null)
                    rows.Add(Button("Resync the pose", () => _desk?.Resync(_figure), () => Ready));
                else
                    rows.Add(Button("Resync from Daz", () => _desk?.Resync(_node), () => Ready));
                rows.Add(Button("Select in Daz", () => _desk?.Selected(_node.Id, _bone), () => Ready));
                rows.Add(Button("Go to it", () => GoTo(Anchor())));

                // Why this will not move, and the switch that changes it. Props above
                // the size limit arrive fixed so that reaching for a cup cannot drag the
                // room it is in; that is a default, not a verdict.
                if (_node.Type == "prop")
                {
                    if (string.IsNullOrEmpty(_node.Json?.Value<string>("mesh_skipped")))
                        rows.Add(new VrPanel.Row
                        {
                            Label = "Movable",
                            Kind = VrPanel.Kind.Toggle,
                            Get = () => SceneLoader.IsMovable(_node),
                            Set = on => SceneLoader.SetMovable(_node, on),
                        });
                    else
                        rows.Add(Note("Cannot move", () => _node.Json.Value<string>("mesh_skipped")));
                }

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
                new VrPanel.Tab { Label = "Takes", Build = TakeRows },
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
            var rows = new List<VrPanel.Row>();

            // Dialling from inside the headset. The settings themselves stay on the
            // monitor -- an address and six digits get typed, and typing in VR is
            // miserable -- but pressing Connect with what is already saved is one
            // button, and taking the headset off to press it is absurd.
            if (!Ready)
                rows.Add(Button("Connect to Daz", () => _session?.Reconnect(), () => _session,
                    () => _session ? _session.host + ":" + _session.port : ""));

            rows.AddRange(new[]
            {
                Toggle("Draft (nothing reaches Daz)", () => PoseSync.Draft, v => _poses?.SetDraft(v)),
                Slider("Haptics", 0f, 1f, 100f, "%", () => VrHand.HapticGain, v => VrHand.HapticGain = v, () => true),
                Button("Life size", LifeSize, () => _rig, () => (_rig ? _rig.Scale : 1f).ToString("0.00") + "x"),
                Button("Resync everything", () => _desk?.ResyncAll(), () => Ready && !DeskSync.Rendering),
                Button("Capture a take", Capture, () => _takes && _takes.CanCapture),
            });


            return rows;
        }

        // ---- takes
        //
        // The list, or one take's page. Two levels rather than one row per action per
        // take: a take has four things you might do to it, and a row can only do one.

        void Capture()
        {
            var made = _takes ? _takes.Capture() : -1;
            if (made >= 0) _panel?.Rebuild();
        }

        List<VrPanel.Row> TakeRows()
        {
            if (!_takes) return new List<VrPanel.Row>();
            if (_take >= 0 && _take < _takes.Count) return TakePage(_takes.Takes[_take]);
            _take = -1;

            var rows = new List<VrPanel.Row>
            {
                Button("Capture a take", Capture, () => _takes.CanCapture),
            };
            if (_takes.Count == 0)
            {
                rows.Add(Note("Nothing captured yet", () => "they last past this session"));
                return rows;
            }

            // Newest first: the one you just caught is the one you are looking for.
            var shown = 0;
            for (var i = _takes.Count - 1; i >= 0 && shown < 8; i--, shown++)
            {
                var index = i;
                var take = _takes.Takes[i];
                rows.Add(Button(take.Name,
                    () => { _take = index; _panel.Rebuild(); },
                    null,
                    () => take.FigureCount + (take.FigureCount == 1 ? " figure" : " figures")));
            }
            return rows;
        }

        List<VrPanel.Row> TakePage(PoseTakes.Take take)
        {
            var rows = new List<VrPanel.Row>
            {
                Note("Taken at", () => take.When),
                Note("Holds", () => take.FigureCount + (take.FigureCount == 1 ? " figure" : " figures")),
                Button("Recall it", () => _takes.Recall(_take), () => _takes && !DeskSync.Rendering),
                // Recall first, then export: the plugin writes whatever pose the figure
                // is wearing, and both messages go down the same ordered connection, so
                // what lands in the library is what this take holds.
                Button("Save as a Daz pose", () => Export(take), () => Ready && !DeskSync.Rendering,
                    () => DeskSync.LastExport),
            };

            var remove = new VrPanel.Row { Label = "Delete this take", Kind = VrPanel.Kind.Button, Danger = true };
            remove.Run = () =>
            {
                if (_armed == remove)
                {
                    _takes.Delete(_take);
                    Disarm();
                    _take = -1;
                    _panel.Rebuild();
                    return;
                }
                Disarm();
                _armed = remove;
                _armedUntil = Time.time + 5f;
                remove.Label = "Delete - press again";
            };
            rows.Add(remove);
            rows.Add(Button("Back to the takes", () => { _take = -1; _panel.Rebuild(); }));
            return rows;
        }

        // One preset per figure, because a pose preset applies to whatever is selected
        // when it is loaded, and a file holding five characters' poses could not.
        void Export(PoseTakes.Take take)
        {
            if (!_takes.Recall(_take)) return;
            var many = take.FigureCount > 1;
            foreach (var id in take.FigureIds)
            {
                var label = _loader && _loader.Figures.TryGetValue(id, out var figure) ? figure.Label : id;
                _desk?.ExportPose(id, many ? take.Name + " - " + label : take.Name);
            }
        }

        int Hidden()
        {
            if (!_loader) return 0;
            var count = 0;
            foreach (var node in _loader.Nodes.Values)
                if (!SceneLoader.IsNodeVisible(node)) count++;
            return count;
        }

        void ShowHidden()
        {
            if (!_loader) return;
            foreach (var node in _loader.Nodes.Values)
                if (!SceneLoader.IsNodeVisible(node)) _desk?.SetVisible(node, true);
            _panel?.Rebuild();
        }

        List<VrPanel.Row> SceneRows()
        {
            var rows = new List<VrPanel.Row>
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

            // What became of this scene's props. Only when it has something to report:
            // "12 props, 12 movable" is not news, and the tab has a ten-row ceiling.
            if (_loader && _loader.PropsWorthMentioning)
                rows.Add(Note("Props", () => _loader.PropSummary));

            // Hiding takes an object's colliders with it, which also takes away the only
            // way to point at it again. This is the way back, and it is only here when
            // there is something to come back from.
            var hidden = Hidden();
            if (hidden > 0)
                rows.Add(Button("Show what is hidden", ShowHidden, () => Ready,
                    () => hidden + (hidden == 1 ? " object" : " objects")));

            return rows;
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
