# Generates docs/tickets/backlog.csv from the list below.
#
# The list is the source: edit here and regenerate, rather than hand-editing a CSV.
# Linear's importer has a column-mapping step, so the header names only have to be
# recognisable, not exact.

import csv, io, os

os.chdir(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

POSING = "Posing"
SCENE = "Scene & performance"
UI = "UI & controls"
NET = "Session & networking"
RELEASE = "Release readiness"


def t(title, project, labels, priority, why, today, done, where=""):
    body = f"**Why**\n{why}\n\n**Today**\n{today}\n\n**Done when**\n"
    body += "\n".join(f"- [ ] {d}" for d in done)
    if where:
        body += f"\n\n**Where**\n{where}"
    return {
        "Title": title,
        "Description": body,
        "Status": "Backlog",
        "Priority": priority,
        "Labels": labels,
        "Project": project,
    }


tickets = [
    # ---- the thing that was just built and missed the mark
    t("Takes: say what the photoshoot loop should actually feel like",
      POSING, "type:feature,area:ux,blocked:needs-input", "High",
      "The loop was built from one sentence of description and landed working but wrong -- "
      "\"it's working but not the way I was thinking\". Nothing more should be built on it until "
      "the intended shape is written down.",
      "Capture stores the whole scene and persists per Daz scene. The panel's Takes tab lists them "
      "newest-first with a page each: recall, delete, save as a Daz pose preset. The wheel keeps "
      "three quick slots counting back from the newest.",
      ["The expected flow is written in this ticket: what you press, what you see, what you get",
       "Each way the current build differs from that is listed",
       "Each difference is either fixed or spun into its own ticket"],
      "`client/Daz VR Bridge/Assets/DazVrBridge/PoseTakes.cs`, `PanelMenu.cs` (TakeRows/TakePage), "
      "`plugin/pose_export.cpp`"),

    t("Thumbnails on takes",
      POSING, "type:feature,area:client", "Medium",
      "A list of times is not a review. Picking the take you meant means seeing it.",
      "Takes show a capture time and a figure count. Choosing between six of them means recalling "
      "each in turn.",
      ["Each take stores a small render of the scene, captured at the moment of capture",
       "The Takes tab shows it beside the name",
       "Thumbnails live with the take on disk and survive a restart"],
      "`PoseTakes.cs` (Capture, the disk format), `PanelMenu.cs` (TakeRows)"),

    t("Finger posing from the Index controllers",
      POSING, "type:feature,area:client", "High",
      "The controllers already feel every finger and nothing here reads it. Hand poses are the "
      "fiddliest thing to do at a desk and the most natural thing to do in a headset.",
      "Hands are posed bone by bone like any other limb. Finger bones are small and the handles "
      "crowd each other.",
      ["Skeletal input is read from the runtime where it is offered",
       "Finger curls drive the figure's finger bones live",
       "A grip on the hand switches finger tracking off, so grabbing does not close the figure's fist",
       "It degrades quietly on controllers that do not report fingers"],
      "`VrHand.cs` (input actions), `BoneHandles.cs`, a new finger solver"),

    t("A pose library: a few favourites, reachable in VR",
      POSING, "type:feature,area:plugin,area:client", "Medium",
      "Not all of Daz's poses -- a chosen few. One sitting, one standing, one lying down, as a "
      "starting point to work away from rather than building every pose from bind.",
      "Poses are applied at the desk before putting the headset on.",
      ["A way to mark poses as favourites (a folder under the library is enough)",
       "The plugin lists them",
       "A panel tab shows them and applies one to the aimed figure",
       "Applying is one Daz undo step"],
      "New plugin message alongside `pose.export`; `PanelMenu.cs` for the tab"),

    t("Mirror posing",
      POSING, "type:feature,area:client,blocked:needs-input", "Medium",
      "Deferred since phase 4. Mirroring a pose left-to-right is a standard posing operation and "
      "the one thing the tool still cannot do that a desk can.",
      "Nothing. The reflection convention is unknown: which axis Daz mirrors about, and how bone "
      "orientations and joint limits transform with it.",
      ["A scene with a deliberately mirrored pose is supplied, so the convention is measured rather than guessed",
       "Mirroring a figure's pose produces the same result Daz's own mirror does, within a degree",
       "It is one undo step"],
      "`client/.../DazEuler.cs`, `RigProfile.cs`"),

    t("Genesis 8 profile",
      POSING, "type:feature,area:client", "Medium",
      "Genesis 9 was first on purpose. Genesis 8 is still most of the content people own.",
      "`RigProfile` describes Genesis 9's chains and limits. A G8 figure loads and poses by FK but "
      "its IK chains and clavicle behaviour are unverified.",
      ["A Genesis 8 profile with its IK chains, twist bones and clavicle share",
       "A G8 figure poses with the same feel as a G9 one",
       "The self-test passes on a G8 figure"],
      "`client/.../RigProfile.cs`"),

    # ---- the new round
    t("Timeline navigation",
      POSING, "type:feature,area:plugin,area:client", "High",
      "Posing is currently a single moment. Daz has a timeline and this tool ignores it.",
      "Every commit writes the current frame. There is no way to move between frames from VR.",
      ["Move backward and forward a frame from in VR",
       "Save a key at the current frame",
       "A key registers the WHOLE object by default, not one channel",
       "The current frame is visible without taking the headset off"],
      "New plugin messages for frame position and keying; `DeskSync.cs` and a panel tab or wheel page"),

    t("Tools management: decide what a button means",
      UI, "type:feature,area:ux", "High",
      "There are more things to do than there are buttons, and each new feature has been squeezed "
      "into whatever was still free. That does not scale, and it is why bindings are getting hard "
      "to remember.",
      "Trigger grabs, grip moves the world, A opens the panel, B holds the wheel, X and Y undo and "
      "redo, A-while-holding passes through bodies. Every one of those is fixed forever.",
      ["A way to say what you are trying to do right now, so the same button can mean different things on purpose",
       "The current mode is visible without reading anything",
       "Switching mode is faster than opening the panel",
       "Existing bindings keep working in the mode they belong to"],
      "`VrHand.cs`, `VrMenu.cs`, `PanelMenu.cs`, `BindingLabels.cs`"),

    t("Prop parameters: open the doors",
      SCENE, "type:feature,area:plugin,area:client", "Medium",
      "Props carry their own properties in Daz -- a door's open angle, a drawer's slide. They are "
      "how a set is actually used, and none of them are reachable from VR.",
      "A prop can be moved and rotated. Anything the prop itself drives is invisible.",
      ["The plugin lists a node's own numeric properties with their ranges",
       "The object panel shows them as sliders",
       "Changing one is one Daz undo step",
       "Properties that are driven by something else are shown but not editable"],
      "Plugin: a node.properties message; `PanelMenu.cs` NodeTabs"),

    t("A scene with an identity",
      SCENE, "type:feature,area:client", "Medium",
      "The Unity side is an empty scene with a grid in it. It reads as a void with people standing "
      "in it, which makes judging a pose harder and the product look unfinished.",
      "No ground, no horizon, no lighting of its own beyond what the Daz scene brings.",
      ["A ground and a horizon that read as a place",
       "Lighting that flatters a figure without fighting the Daz scene's own lights",
       "It costs nothing measurable in frame rate",
       "It can be turned off for a scene that brings its own environment"],
      "The Unity scene, and `SceneLoader.cs` for the switch"),

    t("A UI pass: icons, and a look at the whole thing",
      UI, "type:chore,area:ux", "Medium",
      "There is now enough UI to judge as a whole, and it grew a piece at a time. Some of it is "
      "words where an icon would read faster, and none of it was designed together.",
      "A radial wheel, a pointed panel with four tabs, floating binding labels, a HUD, and a "
      "desktop setup screen. All built in code, all styled independently.",
      ["Icons where a word is slower to read than a shape",
       "One set of colours and sizes across the wheel, the panel and the HUD",
       "A pass for legibility at arm's length in a headset, not on a monitor"],
      "`VrMenu.cs`, `VrPanel.cs`, `BindingLabels.cs`, `BridgeHud.cs`, `StartScreen.cs`"),

    # ---- older ideas, kept
    t("Scoped scene: work on a slice, in miniature",
      SCENE, "type:idea,area:client", "Low",
      "A thousand-prop set is five frames a second in Daz and no better in VR. Seeing a chosen "
      "volume of it, on a table in front of you, would make heavy scenes workable -- and would be "
      "a genuinely different way to pose.",
      "The region radius already withholds geometry outside a volume, which solves the performance "
      "half. The miniature and the third-person view do not exist.",
      ["A chosen volume of the scene, shown at a workable size in front of you",
       "A way to move between the miniature and standing in the scene",
       "Posing works the same in both"],
      "`plugin/scene_bake.cpp` already has the region filter"),

    t("Better hands: the controller's own model",
      UI, "type:feature,area:client", "Low",
      "The hand is a small box. A recognisable controller with its buttons marked would make the "
      "bindings legible without a separate label floating beside it.",
      "A 3 cm box, drawn as an overlay so it never disappears inside a limb.",
      ["The runtime's own controller model where it offers one",
       "What each button does, written on the button",
       "It still draws over the figure rather than inside it"],
      "`VrHand.cs`, `BindingLabels.cs`"),

    t("Daz preview streaming",
      SCENE, "type:idea,area:plugin", "Low",
      "Kept for the record, and deliberately not started: judged the wrong shape for this tool. A "
      "streamed viewport is fine for a glance and wrong as the way to see your work.",
      "Not started. The camera panel renders properly through Daz instead, which is the answer for "
      "anything that matters.",
      ["Only if a use appears that the camera render does not cover"],
      ""),

    # ---- release readiness
    t("A standalone build, and an honest frame rate",
      RELEASE, "type:chore,area:client", "High",
      "Everything has been measured in the editor. The editor is also the only place editor-only "
      "bugs hide -- the overlay shader was one of those, found because a shader that was not in "
      "Resources would have vanished in a player.",
      "No player build has ever been made.",
      ["A standalone player build that runs",
       "A frame-rate number from the two-machine setup, measured in the player",
       "Whatever the build turns up, fixed or ticketed"],
      "Unity build settings; `client/.../Resources/` for anything loaded by name"),

    t("Hull texturing for strand hair",
      SCENE, "type:feature,area:plugin", "Low",
      "The hair approximation shows as a flat shape. It reads as hair in outline and as plastic up "
      "close.",
      "Hulls are built from the strand cloud and take the surface colour, but their UVs are "
      "spherical so textures are dropped on purpose.",
      ["Either a UV scheme the hair's own texture survives, or a shading treatment that reads as hair without one",
       "It stays optional, as the hulls themselves are"],
      "`plugin/hull_proxy.cpp`"),

    t("Relinking the plugin needs Daz closed",
      RELEASE, "type:chore,area:plugin", "Low",
      "Every plugin change needs Daz shut down before it can be linked, because Daz holds the DLL "
      "open. It has cost a rebuild cycle on nearly every plugin commit this project has made.",
      "The build compiles fine and fails at LNK1168 whenever Daz is running.",
      ["Either a build step that stops and restarts Daz, or a documented one-key routine",
       "Written down where whoever builds this next will find it"],
      "`plugin/CMakeLists.txt`, the README's build section"),
]

os.makedirs("docs/tickets", exist_ok=True)
with io.open("docs/tickets/backlog.csv", "w", encoding="utf-8", newline="") as fh:
    writer = csv.DictWriter(fh, fieldnames=["Title", "Description", "Status", "Priority", "Labels", "Project"])
    writer.writeheader()
    for row in tickets:
        writer.writerow(row)

print(f"wrote {len(tickets)} tickets")
for row in tickets:
    print(f"  [{row['Priority']:6}] {row['Project']:22} {row['Title']}")
