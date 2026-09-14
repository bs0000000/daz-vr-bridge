# daz-vr-bridge

Pose whole Daz Studio 6 scenes in SteamVR, from the same PC or another one on the LAN.
Daz Studio stays the source of truth; the headset gets a lightweight copy of the scene
and sends poses back as undo steps.

Design doc: https://claude.ai/code/artifact/bd7f02aa-d311-43f6-9e9a-a20a8f9e15b9

```
plugin/     Daz Studio 6 plugin (C++/Qt 6). Pane + TCP server. Built from the DS6 SDK tree.
client/     Unity 6 project (OpenXR → SteamVR). Socket client, scene loader, posing UX.
protocol/   PROTOCOL.md — the wire format and message catalog both sides code against.
profiles/   Rig profiles (IK chains, twist bones, grabbable/hidden bones) per Genesis generation.
```

## Building the plugin

The plugin is not a standalone CMake project; it is compiled inside the Daz Studio 6.x SDK's
CMake tree so it inherits the `dzcore` target, Qt setup and compiler flags.

1. Open the SDK root folder (`…\Daz Studio 6.25+ BETA SDK`) in Visual Studio 2022 (File → Open → Folder).
2. `CMakeSettings.json` there needs `DAZ_STUDIO_EXE_DIR` and `Qt6_DIR` (see the design doc).
3. Point the SDK at this repo — either create `<SDK>\My Plugins\CMakeLists.txt` containing
   `add_subdirectory("<repo>/plugin" dazvrbridge)`, or append that line to the SDK's root
   `CMakeLists.txt`.
4. Build → Build All. `dsp_dazvrbridge.dll` lands in `<DAZStudio6>\plugins\`.
5. Launch Daz Studio 6 → Window → Panes (Tabs) → **VR Bridge** → Start.

## Phase status

- [x] **0 · Wire** — plugin pane + TCP server; hello/welcome, ping/pong, scene.changed. Client HUD shows the open scene.
- [x] **1a · Manifest** — node graph + full skeletons (rotation orders, limits, figure-space rest frames). Real Genesis 9 rig profile.
- [x] **1b · Meshes** — zero-pose bake, content-addressed mesh/skin/material assets over the bulk connection, skinned figures in Unity posed from Daz world transforms.
- [x] **2a · Round trip** — `pose.commit` (world rotations → `setWSRot`, one undo step), `pose.state` on desk edits, self-test passes at 0.01°.
- [x] **2b · Grab bones in VR** — self-contained tracked hands, handles from the rig profile (mid-bone spheres, waist ring for the root), aim-based drag with twist, overlay drawing, distance fade.
- [x] **3 · Whole scene** — props/cameras/lights grabbable and synced both ways; camera frustum + picture-in-picture at Daz's lens (vertical frame angle, render-settings aspect), oriented from Daz's own focal point and axes.
- [x] **4 · Posing UX** — world grab/scale (rig-side), carrying the figure by its ring, two-bone IK on arms and legs with Daz joint limits enforced live, forearm-roll twist recovery, prop and figure contact with spatial and haptic feedback, Daz undo/redo, live node-list refresh, and an in-VR settings menu. *Mirror posing remains deferred.*
- [ ] 5 · v2 options

## VR controls

- **Trigger:** grab a bone or scene object. Inside the wheel, a tap applies a toggle or opens a page without closing.
- **Grip:** move the world with one hand; use both hands to rotate and scale it.
- **Both hands on one limb:** hold a hand or foot with one controller and the upper arm, forearm, thigh
  or shin with the other. The second hand steers where the joint points instead of bending that bone
  itself, and turns violet while it does. With the wrist held and the shoulder fixed, a turn about the
  line between them is the limb's only remaining freedom, so all the second hand can do is roll the limb
  about that line.
- **Left B/Y held:** undo a Daz history step. **Left A/X held:** redo. Keep holding to walk back several steps.
  History is a hold rather than a tap because a face button is far too easy to brush while reaching for a bone.
  A ring at the hand fills while the hold charges -- anticlockwise amber for undo, clockwise green for redo --
  and flashes on each step it takes.
- **A/X held while dragging a bone:** pass straight through props and bodies. Contact is an aid, not a law.
- **Right B/Y held:** the settings wheel, anchored where your hand was when you pressed. Move the hand toward a
  chip to highlight it, release to apply, release in the middle to cancel. Posing and world movement pause while
  it is open.

The wheel is meant to be used without reading it: eight chips at fixed clock positions, state shown by colour
rather than the word ON, and continuous settings dialled by pushing the hand further out, with a haptic detent
every 5% and the value applied live. Page one holds what you reach for mid-pose (undo, redo, prop snapping,
figure contact, joint clamping, limit rods, scene reload); page two the things you set once (contact gap,
clavicle share, handle reveal distance, haptic strength, forearm roll assist, contact shapes, life-size reset);
page three the session, which for now is draft mode. Settings are saved locally.

**Draft mode** stops anything being sent to Daz and stops anything arriving from it, so a long experiment
neither fills Daz's undo history with intermediate poses nor gets overwritten halfway through. Leaving it
commits the whole session as one step. Every handle is muted while it is on, and the HUD says so.

**Contact shapes** draws the surface hands actually stop against: a tapered elliptical tube per bone, its
cross-section measured at six stations along the bone from the figure's own skinned vertices. Turn it on when
contact feels wrong -- it says immediately whether the shape is off the skin or the hand is stopping early for
some other reason.

## Backlog

- Mirror posing, selection mirroring, per-node resync, and a Genesis 8 profile.
- A standalone build. Everything so far has been measured in the editor, which is also the only place
  editor-only bugs hide -- the overlay shader was one.
- See the backlog assessment for the rest: https://claude.ai/code/artifact/8f07cacf-be1c-49c6-8ceb-126895047f1f
- Turn on 4x MSAA: cutout materials already ask for alpha-to-coverage, which is inert until then and is
  the real fix for eyebrow fibres sparkling under head motion.
- Exercise and profile the intended two-computer LAN setup.
