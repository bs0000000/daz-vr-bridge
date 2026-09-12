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

## Files

| File | Role |
|---|---|
| `BridgeFrame.cs` | Frame encode/decode. Mirror of `plugin/bridge_protocol.*`. |
| `BridgeClient.cs` | One connection (control or bulk). Background reader, main-thread `Pump()`. |
| `BridgeHud.cs` | Phase 0 smoke test: connect, ping, show the open scene. |
