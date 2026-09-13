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

Handles sit at the middle of each bone segment and draw *through* the body (so the hip
and spine handles are visible), fading in as a controller comes within 30 cm and fully
visible under 10 cm (`BoneHandles` → Show/Full Distance). Hover one (yellow), squeeze the
**trigger** (green) and move your hand: the bone swings about its joint to keep pointing at
your hand, so dragging the forearm drags the forearm; roll the controller to twist the
bone about its axis. Children follow as a chain (this is FK — placing a hand somewhere
with the elbow solving itself is IK, Phase 4). Let go and the figure is committed to Daz
as one undo step named after the bone; Daz's answering `pose.state` snaps anything its
joint limits clamped. Face, twist and finger bones have no handles by design.

Keep `Bridge` (BridgeSession, SceneLoader, PoseSync, BoneHandles) as a plain object at
the scene root rather than under the Canvas — the Canvas is scaled 0.001 and `DazScene`
is parented to the SceneLoader's object. Place `Bridge` about 1.5 m in front of the
XR Origin.

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
| `RigProfile.cs` | Loads `Resources/RigProfiles/<rig>.json`: grabbable/hidden bones, chains, mirror prefixes. |
| `BoneHandle.cs` / `BoneHandles.cs` | Grab spheres on grabbable bones, spawned per figure after load. |
| `VrHand.cs` / `VrRig.cs` | Tracked controllers from the Input System; hover/grip FK grab; commit on release. |
