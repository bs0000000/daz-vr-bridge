# client

Unity 6 LTS project: `Daz VR Bridge/`. Open it from Unity Hub (Add → pick that folder).
The bridge scripts live in `Daz VR Bridge/Assets/DazVrBridge/`.

## How it was set up (Phase 0)

1. **Unity Hub → New project → 3D (URP)**, Unity 6 LTS, location: this `client/` folder.
2. **Window → Package Manager**:
   - *XR Plugin Management* → after install, Project Settings → XR Plug-in Management →
     tick **OpenXR** (Windows tab). In the OpenXR settings add an interaction profile for
     your controllers and make sure SteamVR is your active OpenXR runtime.
   - *XR Interaction Toolkit* (needed from Phase 2; harmless now).
   - **Newtonsoft Json** — add by name: `com.unity.nuget.newtonsoft-json`.
     The bridge scripts use it for message headers.
3. Scene: add an `XR Origin (VR)` (GameObject → XR), then a **Canvas** set to *World Space*,
   scaled to ~0.001, placed ~1.5 m in front of the origin, with a **TextMeshPro - Text**
   child. Drop `BridgeHud` on any object, assign the TMP text, set host/port/pairing code.
4. In Daz Studio 6: Window → Panes (Tabs) → **VR Bridge** → Start. Copy the address and
   the six-digit code into the HUD component if Unity runs on another machine.
5. Press Play. The HUD shows the scene name, node count and Daz version, and follows
   File → Open / New in Daz Studio.

## Scene setup (Phase 1b)

One GameObject (call it `Bridge`) with **BridgeSession** (host/port/pairing code) and
**SceneLoader** (bake options; leave `textures` at `none` for now). The world-space text
gets **BridgeHud** with the session and loader assigned (it also finds them by itself).
On Play the loader requests the scene, pulls assets it has not cached, and builds a
`DazScene` object: one child per Daz node, figures with a `skeleton` hierarchy at bind
pose and a `mesh` child carrying the SkinnedMeshRenderer; followers under their figure.
Assets are cached in `%USERPROFILE%\AppData\LocalLow\<company>\<product>\bridge-cache`.

## Round trip without a headset (Phase 2a)

Add **PoseSync** to the `Bridge` object. In Play mode with the Game view focused:

| Key | Action |
|---|---|
| **T** | Self-test: Daz sends its pose, the client echoes it, Daz checks every Euler control came back within 0.01°. Result on the HUD. |
| **C** | Commit: every bone whose rotation changed since the last Daz state is sent as one undo step ("VR pose"). Rotate a bone under `DazScene/<figure>/skeleton` with the Scene-view gizmo first. |
| **R** | Re-request the scene. |

Pose a bone in Daz Studio (Posing tab or the viewport tool) and the headset follows within ~100 ms.

## Grabbing bones in VR (Phase 2b)

No XR Interaction Toolkit rig needed. Two components:

- **VrRig** on `XR Origin (VR)`: spawns a Left/Right hand under the Camera Offset at
  runtime, tracked via the Input System's `<XRController>` layout (OpenXR must have an
  interaction profile for your controllers enabled — Project Settings → XR Plug-in
  Management → OpenXR → Interaction Profiles).
- **BoneHandles** on the `Bridge` object: after each load, a small sphere at every
  grabbable bone from the figure's rig profile (`Resources/RigProfiles/<rig>.json`, kept
  in sync with `/profiles`).

Handles sit at the middle of each bone segment and draw *through* the body (so the spine
handles are visible), fading in as a controller comes within 30 cm and fully visible
under 10 cm (`BoneHandles` → Show/Full Distance). Three kinds, by color:

| Handle | Color | Grab behaviour |
|---|---|---|
| Bone | blue | FK: the bone swings to follow your hand, children come along |
| Hand / foot | teal | **IK**: carried rigidly while the limb above solves to reach it |
| Root (hip) | orange ring at the waist | carries the whole figure, position and rotation |

**IK** (`BoneHandles` → Ik Enabled) comes from the rig profile's chains marked `"ik": true`
— for Genesis 9 the two arms and two legs. Grab a hand and move it: her hand goes where
your hand goes and the elbow and shoulder solve analytically to follow. The bend plane you
posed is preserved, so the elbow stays where you put it; when the limb is straight (no
plane to preserve) the profile's `pole` decides — elbows back, knees front. Out of reach,
the limb straightens and points at your hand. The joint can never invert.

Arm chains also name a `shoulder` (the clavicle), which takes a fraction of the reach
(`shoulder_weight`, capped by `shoulder_max_deg`) before the two-bone solve — reaching
across the body or down to an armrest is shoulder-girdle motion in a real body, and
without it the upper arm alone has to swing past its Daz limits, which Daz then corrects
hard on commit.

**Joint limits.** Daz enforces per-bone rotation limits, and the upper arm's
(`y[-110, 40]`) runs out long before the arm runs out of reach. Two features, both on
`BoneHandles`:

- **Clamp To Limits** (default on) keeps every joint inside its Daz range *while you drag*
  and hands the reach the limits refuse to the clavicle: solve, clamp, swing the clavicle
  to close the gap, solve again (Ik Iterations, default 4). It stops the moment a solve
  comes back legal, so poses inside the limits behave exactly as before. What you see is
  what Daz will keep. Turn it off for the old free solve.
- **Show Limits** (default on) turns a handle **red** when a bone it drives is *at* its
  limit — which, with clamping on, is what "the hand stopped following my controller"
  actually means — and lists them on the HUD, `l_upperarm y 40° at [-110, 40]`. With
  clamping off the same display reads `past` instead, flagging what Daz will correct.

Arm chains also set `roll_assist`: the wrist alone can only twist `z[-70, 80]`, so the
solver rolls the forearm about the elbow-to-wrist axis (which moves no joint, leaving the
IK solution intact) to find the roll that leaves the least total violation on the forearm
and hand. That is forearm pronation, which is where a real wrist's twist comes from; Daz
drives the forearm twist bones from it on commit.

The Euler decomposition behind all of this is documented in `protocol/PROTOCOL.md` and
verified to 0.00002° against Daz's own values, with the rebuild round-tripping at 6e-8.

Controllers also draw through the body. Hover a handle (yellow), squeeze the
**trigger** (green) and move your hand: the bone swings about its joint to keep pointing at
your hand, so dragging the forearm drags the forearm; roll the controller to twist the
bone about its axis. Children follow as a chain (this is FK — placing a hand somewhere
with the elbow solving itself is IK, Phase 4). Let go and the figure is committed to Daz
as one undo step named after the bone; Daz's answering `pose.state` snaps anything its
joint limits clamped. Face, twist and finger bones have no handles by design.

## Moving yourself and the figure (Phase 4, first part)

**World grab** is on the *other* button (grip, when trigger grabs bones). Hold it on one
hand and drag to move yourself through the scene; hold it on both hands and pull them
apart or together to scale, or turn them about each other to rotate. It moves and scales
the **XR rig**, not the Daz scene, so every Daz coordinate stays 1:1: at rig scale 3 you are
a giant surveying the set and one physical step covers three meters; at 0.3 you are small
enough to work on fingers. Reach and handle reveal distances scale with the rig. Limits and
toggles are on `VrRig` (Min/Max Scale, Two Hand Scale/Rotate).

The waist **ring** now carries the figure rigidly (position and rotation) — grab it and walk
her to the couch. The commit sends the hip's world position along with the rotations, and
Daz applies it as the hip translation, one undo step.

## Props, cameras, lights (Phase 3)

Add **NodeSync** to the `Bridge` object. Then:

- **Cameras** appear as a small dark body with a wireframe frustum and a live
  picture-in-picture panel floating above it, rendering the VR scene through the Daz lens
  (focal length, frame width, aspect). Grab the body and carry it; release commits
  `camera.set` (one undo step in Daz). Change the focal length in Daz and the panel follows.
- **Props** up to 2 m (`SceneLoader` → Max Grabbable Prop Size) can be grabbed anywhere on
  their bounds and moved; environments larger than that stay put. A prop parented to a
  bone in Daz (something held in a hand) rides on that bone.
- **Lights** show as a small emitter with a direction line (spot/distant) or a sphere
  (point), light the clay preview roughly, and can be moved the same way.

Objects follow the hand rigidly; bones use the swing/twist drag. Moving a node at the desk
updates VR within ~100 ms (`node.state`).

Keep `Bridge` (BridgeSession, SceneLoader, PoseSync, NodeSync, BoneHandles) as a plain object at
the scene root — **never under the XR Origin or the Canvas**. `DazScene` is parented to the
SceneLoader's object, so under the XR Origin the world grab would move and scale the Daz
scene along with you (nothing appears to happen), and the Canvas is scaled 0.001. Place
`Bridge` at the origin; use the world grab to bring the scene to you.

Controllers that leave the headset's view dim rather than vanish: the hand keeps its last
valid pose (the runtime extrapolates from the IMU) and stays usable. The HUD marks such a
hand "(imu)".

## Files

| File | Role |
|---|---|
| `BridgeFrame.cs` | Frame encode/decode. Mirror of `plugin/bridge_protocol.*`. |
| `BridgeClient.cs` | One connection (control or bulk). Background reader, main-thread `Pump()`. |
| `BridgeSession.cs` | Owns the control + bulk connections for the app; others subscribe to its frames. |
| `BridgeHud.cs` | Status text: connection, open scene, load progress. |
| `DazSpace.cs` | The only place that knows Daz units/handedness. cm→m, Z mirror, quaternion map. |
| `DzmChunks.cs` | Parsers for the `DZM1` mesh and `DZS1` skin chunks. |
| `AssetCache.cs` | Content-addressed disk cache with SHA-1 verification. |
| `SceneLoader.cs` | manifest → GameObjects; skeletons at bind pose, skinned meshes, materials; `ApplyWorldPose` / `DazWorldRotOf` for the pose round trip. |
| `PoseSync.cs` | `pose.state` in, `pose.commit` out, self-test; keyboard triggers for editor testing. |
| `RigProfile.cs` | Loads `Resources/RigProfiles/<rig>.json`: grabbable/hidden bones, IK chains, facing direction, mirror prefixes. |
| `TwoBoneIk.cs` | Analytic two-bone solver (law of cosines) with bend-plane preservation. |
| `DazEuler.cs` | A bone's pose as Daz's X/Y/Z rotation values, for live joint-limit checks. |
| `BoneHandle.cs` / `BoneHandles.cs` | Grab spheres on grabbable bones, spawned per figure after load. |
| `VrHand.cs` / `VrRig.cs` | Tracked controllers from the Input System; hover/trigger grab of any `IGrabbable`. |
| `IGrabbable.cs` / `NodeHandle.cs` | The grab contract; rigid grab-and-move for props, cameras, lights. |
| `NodeSync.cs` | `node.state` in, `node.transform` / `camera.set` out. |
| `CameraView.cs` / `LightGizmo.cs` | Camera body + frustum + picture-in-picture; light emitter gizmo + Unity light. |
