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

## Getting connected

The setup screen is on the monitor, not in the headset: a host address and a six-digit pairing code are
typed, and this machine has a keyboard attached to it already. It carries the connection details and the
options that decide what gets baked -- textures, texture size, bones per vertex, strand-hair approximation,
and the region radius -- which are deliberately *not* on the wheel: everything there is live, while these
cost a full re-bake and re-download, and sitting beside a Connect button says so. Settings are remembered.

The screen waits for Connect rather than dialling on its own: a setup screen that connects before anyone has
read it is not a setup screen. It gets out of the way once a scene is up, and comes back if the connection
drops. The same five bake options are on the panel's Scene tab in the headset, so a texture size can be
changed without going back to the desk.

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
- **Right A/X, empty hand:** the panel, about whatever you are pointing at -- a prop, a camera, a bone -- or
  about the session when you are pointing at nothing. Press again to put it away.
- **Right B/Y held:** the settings wheel, anchored where your hand was when you pressed. Move the hand toward a
  chip to highlight it, release to apply, release in the middle to cancel. Posing and world movement pause while
  it is open.

The wheel is meant to be used without reading it: eight chips at fixed clock positions, state shown by colour
rather than the word ON, and continuous settings dialled by pushing the hand further out, with a haptic detent
every 5% and the value applied live. Page one holds what you reach for mid-pose (undo, redo, prop snapping,
figure contact, joint clamping, limit rods, scene reload); page two the things you set once (contact gap,
clavicle share, handle reveal distance, haptic strength, forearm roll assist, contact shapes, life-size reset);
page three the session: draft mode, Take, the panel, Render, and an arc of three slots to recall a take
from. Settings are saved locally.

## The panel

The wheel is proprioception: eight fixed directions, learned once, used without looking. That is right for
what you reach for in the middle of a pose and wrong for everything that has to be *read*, which is what the
panel is for -- which object this is, how big its textures are, what this button is about to delete.

Press A with an empty hand and a board appears at arm's length, off to the side of whatever it is about so the
subject stays in view. Point at it with the controller's aim pose and pull the trigger; sliders drag, and a
choice steps forward or back depending on which half of the readout you click. It hangs from its top edge, so
switching tab never moves the title out from under the cursor, and it comes back in front of you if you walk
off or turn around.

Pointing at a scene object opens that object's panel: select it in Daz, resync it, walk over to it, hide it,
delete it (twice, and Daz's undo covers it). A camera also gets **Render** and a **focal length** slider --
which is the point, because taking a render used to mean holding the camera steady in one hand while flicking
a wheel with the other. Frame the shot, let go, press the button; the lens is sent once, when the drag ends,
rather than forty times on the way there.

Pointing at nothing opens the session panel: **Contact** (snapping, body contact, joint limits, contact
shapes), **Session** (draft, haptics, life size, resync, rebuild, takes), **Scene** (textures, texture size,
bones per vertex, strand hair, region radius -- the five that cost a re-bake, with the button that pays for
it), and **Buttons**, which is every binding written down.

**Button labels** float what each button does right now beside the controller that has it, and change with
what the hand is holding -- the trigger reads "grab a bone or object" in space, "release to place" while
holding, "apply, stay open" inside the wheel. Generated from the same state the bindings read, so they cannot
go stale, and off by default because reading while posing is a nuisance. The switch is on the panel's Buttons
tab, beside the same list written out in full, which is where it can be found without knowing it exists.

**Render** starts a Daz render of the camera the panel is about (or, from the wheel, of the camera a hand is
holding). Daz refuses edits while it renders, so the
bridge drafts for the duration and commits when it finishes. **Resync** on page one asks Daz for every
transform, which is a few hundred bytes against a re-bake's whole scene.

**Takes** hold the whole scene's pose -- every figure, and the props they are resting on, since a hand on a
chair is only a pose while the chair is where it was. Capture costs nothing: the data is what pose.commit
already sends. They last for the session and are cleared by a scene rebuild, whose ids may no longer mean the
same thing. Review, thumbnails and export are deliberately absent until taking and recalling has earned them.

**Draft mode** stops anything being sent to Daz and stops anything arriving from it, so a long experiment
neither fills Daz's undo history with intermediate poses nor gets overwritten halfway through. Leaving it
commits the whole session as one step. Every handle is muted while it is on, and the HUD says so.

**Contact shapes** draws the surface hands actually stop against: a tapered elliptical tube per bone, its
cross-section measured at six stations along the bone from the figure's own skinned vertices. Turn it on when
contact feels wrong -- it says immediately whether the shape is off the skin or the hand is stopping early for
some other reason.

## Backlog

- Mirror posing and a Genesis 8 profile.
- A starting screen: host, pairing code and the bake options belong on the desktop window, where typing works.
- A standalone build. Everything so far has been measured in the editor, which is also the only place
  editor-only bugs hide -- the overlay shader was one.
- See the backlog assessment for the rest: https://claude.ai/code/artifact/8f07cacf-be1c-49c6-8ceb-126895047f1f
- Turn on 4x MSAA: cutout materials already ask for alpha-to-coverage, which is inert until then and is
  the real fix for eyebrow fibres sparkling under head motion.
- Exercise and profile the intended two-computer LAN setup.
