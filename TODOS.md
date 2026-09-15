# Bugs and missing
- [done] Geoshell are breaking the shapes in Unity (looks like the character has a ghost hallowing costume, and it doesn't render). Daz is running with one of them (Nadine 9 has G9WT Wet Geoshell Map WB6 High Density Drops geoshell)
- [done] Starting screen (needs an option menu, connection informations and different tools if we have)
  - on the monitor: connection + the five bake options. The in-VR panel now carries the same options.
- Positon copying: just mimic the pose, then press a button to save, like a photoshoot. At the end, review your poses, select/edit and export them if you want. Also index has finger tracking, so it is really easy to make hand poses
- Quick access to favorite poses. We might not want to import all daz postures, but being able to select some for quick access, like one siting, one standing, one laying down, etc ...
- [partly] Tutorial -- not a scripted one (you said don't). The panel's Buttons tab lists every binding,
  and the floating per-hand labels are switched on from there.
- Better hands model: display a better model (3d model of the controller?). With also a button, a button combo or a setting to show commands on the controller itself if we go with the 3d model  


# Ideas
- "Scoped scene": render scene in a specific volume (see like a crystal ball, but probably rectangular and without border, maybe on a table). And physical tools to grab to select how we want to work (wrench for posing and moving, hand for finger posing, bomb to delete, stick to pose without moving, that's just ideas). We probably should be able to move between this "third person" and in scene. This would help to pose in really complex scene while maintaining correct FPS when the Unity app is running on a different computer

- Preview from daz in Unity: create a Unity camera in Daz that follows user and generate preview, allow the user to see it as external camera. This will need to some heavy image streaming, so it needs to be an option (and we need to assess faisability)

- [done] Draft mode: this will stop commits to be sent to daz and stop sync. Once definitive, it will commit the poses and resync with Daz

- [done] Start render on Cam: the Unity app will probably need to enter draft mode after this to be able to continue to work as Daz block edits when rendering
  - on the camera's own panel, so the shot is framed, let go of, and then taken. Drafts for the render's duration and commits after.
