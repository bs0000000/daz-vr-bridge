# DAZ–VR Bridge wire protocol — v1

Single source of truth for both sides. Change this file first, then the code.
Full rationale lives in the design doc; this is the reference card.

## Transport

- TCP. The **plugin is the server** (default port `41427`, all interfaces).
- Two connections per session, both opened by the client:
  - **control** — small, frequent frames (poses, transforms, camera, selection). Nagle off.
  - **bulk** — scene manifest assets. Big frames are fine here.
- Little-endian throughout.

## Framing

```
u32   frame_len     bytes that follow this field
u32   header_len
u8[]  header        UTF-8 JSON object; always has "t" (type) and "seq"
u8[]  payload       frame_len - 4 - header_len bytes; empty for most messages
```

`seq` is a per-sender counter. Replies carry `ref_seq` = the `seq` they answer.
Max frame is 512 MB (plugin closes the connection on anything larger or malformed).

## Lifecycle

1. Client opens TCP → sends `hello {role:"control"}` → gets `welcome {session,…}` or `error` + close.
2. Client opens a second TCP → `hello {role:"bulk", session}` → `welcome`.
3. `scene.request` on control → `progress`… → `scene.manifest`.
4. `asset.request {hashes}` on bulk → one `asset.data` per hash.
5. Steady state: gestures → control messages; desk-side edits → `pose.state` / `node.state`; structure changes → `scene.changed`.

Pairing: a client whose peer address is not loopback must send `code` (six digits shown in the VR Bridge pane) in `hello`, unless the pane has pairing turned off.

## Messages · client → plugin

| Type | Conn | Fields | Status |
|---|---|---|---|
| `hello` | both | `protocol:1, role, client, session?, code?` | **Phase 0 ✓** |
| `ping` | control | — | **Phase 0 ✓** |
| `scene.request` | control | `textures: none\|opacity\|full, tex_max, influences: 4\|8, include_hidden` | **Phase 1a ✓** (node graph + skeletons; no meshes yet) |
| `asset.request` | bulk | `hashes: [...]` | Phase 1b |
| `pose.commit` | control | `figure, bones: [[id, qx, qy, qz, qw], …], label` | Phase 2 |
| `select` | control | `node, bone?` | Phase 2 |
| `node.transform` | control | `node, pos, rot, scale, commit` | Phase 3 |
| `camera.set` | control | `camera, pos, rot, focal_mm, commit` | Phase 3 |
| `pose.preview` | control | `figure, bones` | **reserved, v2** — v1 plugin answers `error deferred_v2` |

## Messages · plugin → client

| Type | Conn | Fields | Status |
|---|---|---|---|
| `welcome` | both | `protocol, session, daz_version, plugin_version, scene:{path, nodes}` | **Phase 0 ✓** |
| `pong` | control | `ref_seq` | **Phase 0 ✓** |
| `error` | both | `code, msg, ref_seq` | **Phase 0 ✓** |
| `scene.changed` | control | `reason: loaded\|cleared\|renamed\|…, scene:{path, nodes}` | **Phase 0 ✓** (structure diff fields arrive in Phase 3) |
| `progress` | control | `op, done, total, label` | Phase 1b |
| `scene.manifest` | control | `manifest: {…}` — see below | **Phase 1a ✓** |
| `asset.data` | bulk | `hash, kind, size` + payload | Phase 1b |
| `pose.state` | control | `figure, bones` | Phase 2 |
| `node.state` | control | `node, pos, rot, scale` | Phase 3 |

## Manifest (`scene.manifest.manifest`)

```
protocol, scene (path), units "cm", up "y", handedness "right"
bake:   { textures, tex_max, influences, meshes: bool }
assets: [ { hash, kind, size } ]                      — empty until Phase 1b
nodes:  [ {
  id "n_<hex>", name, label, type, visible, asset_id,
  type:        figure | follower | prop | camera | light | null
  parent:      node id of nearest non-bone ancestor, or null
  parent_bone: bone id when parented to a bone (prop in a hand), else null
  transform:   { pos[3] cm, rot[4] quat xyzw, scale[3] }   — world space, current pose
  figure/follower: rig ("genesis9" | "genesis8" | …), follower_of, skeleton: { bones: [ {
      id (bone name), label, parent (bone id | null), rot_order ("XYZ"…),
      origin[3], end[3]   — figure space, this character's shape morphs included
      orient[4]           — the bone's frame, ABSOLUTE in figure space (not parent-relative)
      ws: { pos, rot, scale }  — world space, current pose. NOTE: ws.rot is the accumulated
                                 pose rotation and is identity at zero pose; it does not
                                 include orient.
      limits_deg: { x:[min,max], y:[…], z:[…] }, clamped: bool
      rot_deg: [x, y, z]  — current local Euler values in rot_order
  } ] }
  camera/light: focal_mm, frame_width_mm, aspect; light also kind (spot|point|distant), intensity
} ]
```

Bone ids are DAZ bone *names* (unique within a figure). Node ids are session-stable element ids.

## Error codes

`hello_required`, `protocol_mismatch`, `bad_role`, `bad_pairing_code`, `unknown_session`,
`not_implemented`, `deferred_v2`, `unknown_type`.

## Conventions the client must honor

- Units: centimeters. Up: +Y. Handedness: right (DAZ). Convert on import, once.
- Bone rotations are quaternions in the bone's oriented local frame, relative to zero pose.
- Never more than one `pose.commit` in flight.
