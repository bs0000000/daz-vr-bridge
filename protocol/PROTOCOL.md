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
| `scene.request` | control | `textures: none\|opacity\|full, tex_max, influences: 4\|8, include_hidden, meshes` | **Phase 1b ✓** |
| `asset.request` | bulk | `hashes: [...]` | **Phase 1b ✓** |
| `pose.commit` | control | `figure, bones: [[id, x, y, z, w], …], label, selftest?` — each bone's target **world** rotation in Daz's quaternion sense; applied parents-first via `DzNode::setWSRot` as one undo step named `label`. Confirmation is the `pose.state` that follows (~100 ms), carrying whatever limits clamped. | **Phase 2a ✓** |
| `selftest.begin` | control | `figure` | **Phase 2a ✓** — plugin snapshots the figure's Euler controls and sends a `pose.state` with `selftest: true`; the client echoes it as `pose.commit {selftest: true}`; plugin applies it without undo, compares, restores, answers `selftest.result`. |
| `select` | control | `node, bone?` | Phase 4 |
| `node.transform` | control | `node, pos[3] cm, rot[4] (Daz sense, world), commit, label` — applied via `setWSPos`/`setWSRot`; scale untouched; `commit:false` applies without an undo entry. Not for bones. | **Phase 3 ✓** |
| `camera.set` | control | `camera, pos, rot, focal_mm, commit, label` — as above plus `setFocalLength` | **Phase 3 ✓** |
| `pose.preview` | control | `figure, bones` | **reserved, v2** — v1 plugin answers `error deferred_v2` |

## Messages · plugin → client

| Type | Conn | Fields | Status |
|---|---|---|---|
| `welcome` | both | `protocol, session, daz_version, plugin_version, scene:{path, nodes}` | **Phase 0 ✓** |
| `pong` | control | `ref_seq` | **Phase 0 ✓** |
| `error` | both | `code, msg, ref_seq` | **Phase 0 ✓** |
| `scene.changed` | control | `reason: loaded\|cleared\|renamed\|…, scene:{path, nodes}` | **Phase 0 ✓** (structure diff fields arrive in Phase 3) |
| `progress` | control | `op, done, total, label` | Phase 1b |
| `scene.manifest` | control | `manifest: {…}` — see below | **Phase 1b ✓** |
| `asset.data` | bulk | `hash, kind, size` + payload | **Phase 1b ✓** (unknown hash → `error asset_unknown` with `hash`) |
| `pose.state` | control | `figure, bones: [ { id, ws: { pos, rot } } ], selftest?` — every bone's Daz world transform. Sent whenever any bone of that figure moves (debounced 100 ms), after a `pose.commit`, and for `selftest.begin`. | **Phase 2a ✓** |
| `selftest.result` | control | `figure, pass, bones, max_error_deg, worst, error?` | **Phase 2a ✓** (pass = every Euler control back within 0.01°) |
| `node.state` | control | `node, transform: { pos, rot, scale }, focal_mm?` — any prop/camera/light moved at the desk (debounced 100 ms) and the confirmation after `node.transform`/`camera.set` | **Phase 3 ✓** |

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
      origin[3], end[3]   — the pivot's actual ZERO-POSE position, figure space (unscaled),
                            including any ERC-driven bone translation a shape morph adds
                            (e.g. a hip lift). This is what the bind mesh was baked against.
      rest_origin[3]      — Daz's untranslated center point (getOrigin), for reference
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

Nodes with geometry also carry `mesh`, `skin` (figures/followers) and `materials` — each a
`sha1:<hex>` content hash listed in `assets` — plus `vertices` and `triangles` counts.
Followers' skin indices refer to the **figure's** bone list (their own bones are remapped by
name, follower-only bones fold into the nearest figure ancestor).

## Asset chunks (`asset.data` payloads) — all little-endian

**`mesh` — `DZM1`.** Positions are node-local, centimeters, Daz handedness; vertices are
already split on UV seams so UVs are per-vertex. Triangles are grouped by material.

```
char[4] "DZM1"
u32     vertex_count
u32     triangle_count
u32     flags            bit0: uvs present
u32     group_count
f32[3]  position   × vertex_count
f32[2]  uv         × vertex_count        (only if flags & 1)
u32[3]  triangle   × triangle_count      Daz winding (flip for Unity)
{ u32 start_tri, u32 tri_count, u16 material_index, u16 pad } × group_count
```

**`skin` — `DZS1`.** Same vertex order as the mesh chunk. Influences are top-N, normalized,
padded with weight 0.

```
char[4] "DZS1"
u32     vertex_count
u8      influences (4 | 8), u8[3] pad
per vertex: u16[influences] bone_index, f32[influences] weight
```

**`materials` — JSON.** `{ materials: [ { index, name, base_color:[r,g,b] 0–1,
opacity_map: path|null, color_map: path|null } ] }`. Map entries are paths on the Daz
machine, present only when the requested texture mode includes them; texture bytes
are not shipped yet.

## Error codes

`hello_required`, `protocol_mismatch`, `bad_role`, `bad_pairing_code`, `unknown_session`,
`wrong_connection`, `asset_unknown`, `commit_failed`, `selftest_state`, `not_implemented`,
`deferred_v2`, `unknown_type`.

## Conventions the client must honor

- Units: centimeters. Up: +Y. Handedness: right (DAZ). Convert on import, once.
- **Daz quaternions are the conjugate of the Hamilton "rotate a vector" convention**
  (DzQuat rotates as `q* v q`). Verified on a posed Genesis 9 arm chain, 0.000 cm residual:
  `ws_child.pos = ws_parent.pos + S · R(conj(ws.rot)) · (origin_child − origin_parent)`
  where `S` is the figure node's scale, and `ws_child.rot = q_local_child ⊗ ws_parent.rot`
  (Hamilton product, local on the left — i.e. standard `world = parent · local` once
  conjugated). Bone skinning is `p' = ws.pos + S · R(conj(ws.rot)) · (p − origin)` with `p`
  in figure space.
- `q_local` relates to the Euler controls as `o⁻¹ · (q₁q₂q₃)⁻¹ · o` (o = `orient`, qᵢ = axis
  rotations in `rot_order` order, System.Numerics product semantics). Clients should pose
  from `ws` (no frame ambiguity) and let the plugin do Euler conversion on commit.
- Never more than one `pose.commit` in flight.
