# daz-vr-bridge

Pose whole Daz Studio 6 scenes in SteamVR, from the same PC or another one on the LAN.
Daz Studio stays the source of truth; the headset gets a lightweight copy of the scene
and sends poses back as undo steps.

Design doc: https://claude.ai/code/artifact/bd7f02aa-d311-43f6-9e9a-a20a8f9e15b9
**Readable only when signed in as its author — not from a checkout, and not by an agent.**
Where something here says "see the design doc", that rationale is not in the repository.
`protocol/PROTOCOL.md` is the reference card for the wire format and is complete on its own;
for anything else, ask rather than go looking.

```
plugin/     Daz Studio 6 plugin (C++/Qt 6). Pane + TCP server. Built from the DS6 SDK tree.
client/     Unity 6 project (OpenXR → SteamVR). Socket client, scene loader, posing UX.
protocol/   PROTOCOL.md — the wire format and message catalog both sides code against.
profiles/   Rig profiles (IK chains, twist bones, grabbable/hidden bones) per Genesis generation.
tools/      Test harnesses. tools/check.py runs anywhere; the rest need Windows, and say so.
docs/       How the Linear tracker is set up and how work moves through it.
CLAUDE.md   Orientation for anyone — person or agent — arriving at a checkout cold.
```

`python3 tools/check.py` is the one check that needs no Daz, no Unity and no Windows: it
holds the two rig-profile copies to each other and `PROTOCOL.md` to both sides' source.

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

Traffic is encrypted. The handshake hands over an ephemeral RSA key signed under the pairing code, and
everything after it is AES-256 with an HMAC, so nobody else on the network can read a scene going past or
inject a command into a session. Pair once and the plugin issues a signed token, good for seven days by
default, that the headset sends instead of the code next time -- only ever inside the encrypted channel, since
a token in the clear would be worse than no token. *Remember headsets* in the pane sets the days or turns it
off, *Forget paired* invalidates every token at once, and the start screen has the same switch from the other
side. `tools/crypto_interop/run.ps1` checks the plugin's and the client's crypto against the published test
vectors and against each other; neither end can be exercised in place, so that harness is the test.

The setup screen finds Daz by itself: it broadcasts on the LAN every couple of seconds and lists whatever
answers, by machine name and open scene. One Daz answers on every interface it has, so the answers are folded
together by the instance id the plugin sends, and the address dialled is the best route to it -- loopback
first, then a network of yours with a gateway, then one without, which is what a container bridge looks like. One answer fills the address in; two or more and the choice is yours,
because guessing which Daz someone meant is how a scene gets posed on the wrong machine. Typing an address by
hand turns the filling-in off. Daz answers only when asked, and the pane can stop it answering at all.

The setup screen is on the monitor, not in the headset: a host address and a six-digit pairing code are
typed, and this machine has a keyboard attached to it already. It carries the connection details and the
options that decide what gets baked -- textures, texture size, bones per vertex, strand-hair approximation,
and the region radius -- which are deliberately *not* on the wheel: everything there is live, while these
cost a full re-bake and re-download, and sitting beside a Connect button says so. Settings are remembered.

The screen waits for Connect rather than dialling on its own: a setup screen that connects before anyone has
read it is not a setup screen. Nothing is dialled until someone asks; after that, reconnecting is automatic,
so a Daz that restarts is picked up without anyone pressing anything again. It gets out of the way once a scene is up, and comes back if the connection
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
- **Right A/X, empty hand:** hold to aim -- a ray, a spot where it lands, and the name of whatever is under it
  floating there -- and release to open that thing's panel. A quick press is the same gesture without the
  holding. Pointing at nothing opens the session panel. Press again to put it away.
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

Hold A with an empty hand and a ray comes out of it, with a spot where it lands and the name of what it has
found floating above that: a character reads as the character, a prop as its label, empty space as "Session
settings". Pointing anywhere on a figure means that figure -- nothing on the panel is about one bone, and
until something is, making someone aim at a joint to reach a character asks them to know how the rig is put
together in order to use the tool. The bone under the ray is still remembered for one thing, because it is
more use at the desk than the figure: it is what **Select in Daz** reaches for. Release and the board appears
at arm's length, off to the side of whatever it is about so the
subject stays in view. Point at it with the controller's aim pose and pull the trigger; sliders drag, and a
choice steps forward or back depending on which half of the readout you click. It hangs from its top edge, so
switching tab never moves the title out from under the cursor, and it comes back in front of you if you walk
off or turn around.

Where it opens is a guess, and guesses are sometimes wrong -- behind the figure, off to one side, in the way
of the shot. The blue bar along the top is the handle: pull the trigger on it and the board follows the ray,
pushing away and pulling in as your hand does, always turned to face you. Placing it deliberately also stops
it coming back to you, because a board that slides away from where it was just put is worse than one that
opened in the wrong place. Closing and reopening gives you a fresh guess.

Props up to two metres along their longest side arrive movable; a room, a backdrop or a set does not, because
dragging the room while reaching for a cup standing in it is worse than walking to the cup. That is a default
and not a verdict: **Movable** on the prop's own panel switches it on for anything, with a size penalty so a
big box cannot steal a grab from something small and close. The Scene tab says how many props came out which
way whenever there is something to say, so a set where nothing can be picked up explains itself.

Pointing at a scene object opens that object's panel: select it in Daz, resync it (a figure resyncs its pose,
everything else its transform), walk over to it, hide it, delete it (twice, and Daz's undo covers it). Hiding
takes an object's colliders with it, which also takes away the only way to point at it again, so the session
panel grows a *Show what is hidden* row whenever there is something to come back from. A camera also gets **Render** and a **focal length** slider --
which is the point, because taking a render used to mean holding the camera steady in one hand while flicking
a wheel with the other. Frame the shot, let go, press the button; the lens is sent once, when the drag ends,
rather than forty times on the way there.

Pointing at nothing opens the session panel: **Contact** (snapping, body contact, joint limits, contact
shapes), **Session** (connect, draft, haptics, life size, resync, takes), **Scene** (textures, texture size,
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
already sends. Mimic the pose, press the button, move on.

They live on disk, filed under the Daz scene they were taken in, so a photoshoot does not end when the headset
comes off; a take only ever comes back into the scene it belongs to, and a figure that has been re-rigged
since is skipped rather than mangled. The panel's **Takes** tab is the review: newest first, one page each,
with recall, delete, and **Save as a Daz pose**.

That last one writes a real `preset_pose` .duf into `<content>/Poses/VR Bridge/`, addressed to `@selection`
the way Daz writes its own, so it applies to any figure later rather than only the one it came from. The take
is recalled first and the preset is written from the figure's current pose, down the same ordered connection,
so what lands in the library is what you were just looking at. The channel values are the undriven ones --
writing what ERC is contributing would bake a controller's work into the base and count it twice.

The wheel keeps three quick slots, counting back from the newest, because a flick is for the take you just
caught; choosing among twenty is the panel's job.

**Draft mode** stops anything being sent to Daz and stops anything arriving from it, so a long experiment
neither fills Daz's undo history with intermediate poses nor gets overwritten halfway through. Leaving it
commits the whole session as one step. Every handle is muted while it is on, and the HUD says so.

**Contact shapes** draws the surface hands actually stop against: a tapered elliptical tube per bone, its
cross-section measured at six stations along the bone from the figure's own skinned vertices. Turn it on when
contact feels wrong -- it says immediately whether the shape is off the skin or the hand is stopping early for
some other reason.

## Backlog

- **The photoshoot loop.** Takes capture and recall the whole scene, and that is half of it. The half that is
  missing is the half that makes it worth having: takes that survive the session, something to review them
  in, and an export that lands back in Daz as a pose preset rather than only as the scene's current pose.
- **A pose library.** Not all of Daz's, a chosen few -- one sitting, one standing, one lying down -- reachable
  from the panel without taking the headset off.
- **Finger posing**, which the Index controllers can already feel and nothing here reads.
- **A standalone build.** Everything so far has been measured in the editor, which is also the only place
  editor-only bugs hide -- the overlay shader was one. It is also the only honest frame-rate number.
- **Mirror posing**, still waiting on a scene with a deliberately mirrored pose in it, so the convention can be
  settled by measurement rather than by guessing which way Daz means it.
- **A Genesis 8 profile.**
- A better hand: the controller's own model, with what each button does written on it.
- See the backlog assessment for the rest: https://claude.ai/code/artifact/8f07cacf-be1c-49c6-8ceb-126895047f1f
  — as with the design doc, readable only by its author. Superseded in any case: the backlog
  moved to Linear, and nothing needs to go looking for this.

The backlog above is kept here as a summary. The tickets themselves live in Linear -- `docs/tickets/README.md`
has the workspace shape, the working loop, and the backlog as an importable file.
