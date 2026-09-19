# Working in this repository

Read this first. It is meant to be enough on its own: what the project is, where its code
is, which document answers which question, and what you can and cannot find out from the
checkout in front of you.

The last part matters most. This project's real test is a person wearing a headset with Daz
Studio open on a Windows machine. You almost certainly have none of that. Most work here
therefore ends with a change that is written and reasoned about but **not run**, and saying
so plainly is the job — see [What you cannot verify](#what-you-cannot-verify).

## The two halves

A Daz Studio plugin and a Unity application talk to each other over TCP. Daz stays the
source of truth for the scene; the headset gets a copy and sends poses back as undo steps.

| | |
|---|---|
| `plugin/` | **Daz Studio 6 plugin**, C++ / Qt 6. A dockable pane and a TCP server. 22 `.cpp`/`.h` files, all first-party. Built inside the Daz Studio 6 SDK's own CMake tree, never standalone. |
| `client/Daz VR Bridge/` | **Unity 6 project** (OpenXR → SteamVR). First-party code is *only* in `Assets/DazVrBridge/` — 35 `.cs` files. Everything else under `client/` is template or vendored. |
| `protocol/PROTOCOL.md` | The wire format and message catalog. Both halves code against it. |
| `profiles/` | Rig profiles per Genesis generation: IK chains, twist bones, which bones get a grab handle. |
| `tools/` | Test harnesses. One runs anywhere; the rest need Windows. |
| `docs/tickets/` | How the Linear tracker is set up and how work flows through it. |

The plugin is the server (default port `41427`). The client opens two connections: *control*
for small frequent frames, *bulk* for scene assets.

## Which document answers which question

| Question | File |
|---|---|
| What is this, how is the plugin built, what phase is it in? | `README.md` |
| What is on the wire? Message fields, framing, crypto, discovery? | `protocol/PROTOCOL.md` — authoritative |
| How is the Unity side wired up? What does each `.cs` file do? What do the VR controls do? | `client/README.md` |
| What do the rig profile's fields mean? | `profiles/genesis9.json` — it is commented inline |
| How do tickets work, what states and labels exist? | `docs/tickets/README.md` |
| Why is it built this way? | **Not in the repository.** See below. |

**When two documents disagree, `protocol/PROTOCOL.md` wins** for anything on the wire. It
says so itself on line 3, and it is the only file both halves are written against.

`TODOS.md` is superseded — the backlog moved to Linear. Do not add to it.

**The design doc is unreachable.** `README.md` and `PROTOCOL.md` both point at `claude.ai`
artifact links for "the full rationale". Those are readable only by their author, not from a
checkout and not by you. Where a file says "see the design doc", that reasoning is not
available — ask, rather than spending a session looking for it.

## What is not ours

807 files are tracked. Fewer than 60 of them are first-party code. A plain search hits the
Unity template long before it hits this project:

| Path | Files | What it is |
|---|---|---|
| `client/Daz VR Bridge/Assets/TextMesh Pro/` | 364 | TMP examples and extras, from the template |
| `client/Daz VR Bridge/Packages/com.unity.xr.management/` | 205 | Embedded Unity package, vendored |
| `client/Daz VR Bridge/Assets/TutorialInfo/` | 17 | Unity's new-project readme widget |
| `client/Daz VR Bridge/Assets/XRI/` | 10 | XR Interaction Toolkit sample settings |

These are **kept deliberately** and are not to be edited or tidied. Deleting them is a Unity
operation — meta-file references, the template's `Readme.asset`, TMP's essential resources —
and nothing here can open Unity to confirm the project still loads afterwards. `.claude/settings.json`
denies edits under all four paths so a stray write cannot land there by accident.

Every `.asmdef` in the repo belongs to the vendored XR package. There is **no first-party
Unity test assembly**, so there are no Unity tests to run or add to.

Search first-party code with the paths narrowed:

```bash
git grep -n "BridgeSession" -- plugin/ "client/Daz VR Bridge/Assets/DazVrBridge/" protocol/ profiles/
```

## Conventions that live nowhere else

**Commit subjects are sentences, and say what changed for the user.** Look at `git log`:
"A bar to carry the panel by", "Say why a prop will not move, and let it move anyway", "Aim
at a character, not at one of its bones". No `feat:` prefixes, no file names, no trailing
full stop. The body is prose explaining *why*, not a bullet list of edits. Reference the
ticket (`DVB-29`) in the body.

**No `Co-Authored-By` trailer.** History before DVB-29 carries one; new commits do not.
`.claude/settings.json` sets `includeCoAuthoredBy: false` to make that automatic.

**Change `PROTOCOL.md` first, then the code.** Not a style preference — it is the only
shared contract between a C++ half and a C# half that cannot be compiled together.
`tools/check.py` enforces both directions: a message in the doc that neither side implements
fails, and a message either side sends that the doc does not list fails.

**The two rig profiles are byte-identical copies.** `profiles/genesis9.json` is the source;
Unity can only load from `Resources/`, so a copy lives at
`client/Daz VR Bridge/Assets/DazVrBridge/Resources/RigProfiles/genesis9.json`. **Edit one,
copy it over the other, in the same commit.** `tools/check.py` fails if they drift.

**Quote every path under `client/`.** The directory is `Daz VR Bridge` — with spaces. An
unquoted path silently becomes three arguments:

```bash
git grep -n "PoseSync" -- "client/Daz VR Bridge/Assets/DazVrBridge/"   # quoted
ls client/Daz VR Bridge/Assets/                                        # broken
```

## What you can verify

One command, and it is the only one that runs with no Daz, no Unity, no Windows and no
network:

```bash
python3 tools/check.py
```

It checks that the two rig-profile copies are identical, that every bone the profile names
exists in the skeleton dump beside it (and that hinges are not declared about axes Daz locks
shut), and that `PROTOCOL.md` and both sides' source agree on the message catalog. Stdlib
Python only. Run it before every commit, and always after touching a profile or the protocol.

A pass means nothing contradicts itself on disk. It does not mean anything works.

## What you cannot verify

Nothing here builds or runs the plugin or the client:

- **The plugin** is C++ against Qt 6 and the Daz Studio 6 SDK, compiled inside the SDK's own
  CMake tree on Windows with MSVC. The SDK is not in this repository and cannot be obtained
  from it. You cannot compile a single file — not even to check that it parses.
- **The client** is Unity 6. There is no Unity here, no headless build, no test assembly.
- **The crypto** has exactly one test, `tools/crypto_interop/run.ps1`, and it needs Windows,
  MSVC, and Qt 6 at a hard-coded path.
- **The protocol end-to-end** has `tools/smoke-test.ps1`, which needs a running Daz with the
  plugin loaded, and `tools/test-smoke.py`, which mocks Daz but still needs PowerShell —
  usually absent on a Linux worktree. Neither exercises real plugin or client code.
- **Everything about how it feels** — reach, contact, whether a handle is findable — is only
  answerable in the headset.

Each harness in `tools/` states at its top what it needs and who can run it. Read that header
before assuming you can run one.

**So report honestly.** When you have written a change you could not run, say which of these
applies, in these terms:

> Written, not verified. `tools/check.py` passes, which covers the profile and protocol
> consistency only. The plugin needs rebuilding in the SDK on Windows; the behaviour needs
> the headset to confirm.

Never write "tested", "verified" or "working" for anything that needed Daz, Unity or the
headset. A claim of a pass that turns out to mean "it compiled in my head" costs far more
than an honest "I could not run this" — it sends someone to put a headset on for a change
that was never going to work.

Say which side needs rebuilding — plugin, Unity, or both — because they are rebuilt
separately and by hand.

## Taking a ticket

`docs/tickets/README.md` has the full loop. In short: the tracker is Linear, team key `DVB`.
A ticket is takeable when it has a *Done when* list of things that are either true or not
true once it works. If that list is missing or ambiguous, **ask before writing code** rather
than building the wrong thing well. Move it to *In Review* when committed, and say what to
rebuild and what to look at in the headset — "In Review" here means "waiting for the headset
to say", not "waiting for a code review".
