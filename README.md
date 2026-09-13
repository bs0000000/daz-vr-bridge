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
- [ ] **3 · Whole scene** — props/cameras/lights grabbable and synced both ways; camera frustum + picture-in-picture. *Written; verifying.*
- [ ] 4 · Posing UX (IK, snapping, mirror, world scale)
- [ ] 2 · Round trip
- [ ] 3 · Whole scene
- [ ] 4 · Posing UX
- [ ] 5 · v2 options
