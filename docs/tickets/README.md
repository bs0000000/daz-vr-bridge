# Tickets

The backlog outgrew `TODOS.md`. This folder holds the shape of the tracker and the backlog
as something a tracker can swallow, so that moving to one is an import rather than an
afternoon of retyping.

The tracker is **Linear**. It was chosen over GitHub Issues for one reason that matters
later: when this ships, customers file tickets, and Linear's Triage inbox is built for
exactly that, without the code repository having to be public for strangers to write to.

---

## 1 · The workspace, as built

**Team.** `Daz VR Bridge`, key `DVB`. Issue ids come out as `DVB-12`, which is what gets
typed at Claude and written in commit messages.

**Workflow states.**

| Group      | State                | What it means                                            |
|------------|----------------------|----------------------------------------------------------|
| Backlog    | `Backlog`            | Real, not scheduled                                       |
| Unstarted  | `Ready for Claude`   | Written well enough to hand over: has *Done when*         |
| Started    | `In progress`        | Being built right now                                     |
| Started    | `In Review`          | Built and committed, waiting for the headset to say       |
| Completed  | `Done`               | Seen working                                              |
| Cancelled  | `Cancelled`          | Decided against, with the reason in a comment             |

`Ready for Claude` is the one that earns its place. The difference between a ticket I can
take and a note to self is whether the expected behaviour is written down — and this
project has already paid for that lesson twice: a settings screen nobody could find, and a
photoshoot loop that worked and was not what you pictured.

**Labels.** Three axes, so triage can filter on any one:

- **Area** is a label *group*: `Plugin`, `Client`, `Protocol`, `UX`. Linear allows one
  label per group, so every ticket has exactly one area — and `Protocol` is the honest
  answer for anything that changes both ends at once, which is what it means.
- **Kind**: `Bug`, `Feature`, `Improvement` (Linear's own) plus `Idea`, for what is worth
  recording and not yet worth committing to.
- **`Needs input`** — cannot move without something only you can give: a scene, a
  decision, a session in the headset. Worth its own label because these look stalled
  otherwise.

**Projects.** Five, matching how the work actually clusters: `Posing`, `Scene &
performance`, `UI & controls`, `Session & networking`, `Release readiness`. The fourth is
empty on purpose — it is the part that currently works, and it is where "it will not
connect" will land after release.

**Triage.** On. Worth using now, while the only thing arriving is your own thinking:
easier to have been living in it for months than to start when the first angry report
shows up.

## 2 · The backlog

Seventeen tickets, `DVB-1` to `DVB-17`, all in `Backlog`. Promote what you want next to
`Ready for Claude`.

`backlog.csv` and `backlog_source.py` are how they got there and are now history: Linear
is the source of truth. They are kept because the ticket bodies are worth having in the
repo's own history, not because anything should read them again.

## 3 · Working with Claude

Linear's MCP server is connected, so `DVB-12` is enough — I can read a ticket, its
comments and its attachments, and write back to it.

The loop:

1. You say **"do DVB-12"**.
2. I read the ticket and its comments, including anything you attached.
3. If *Done when* is missing or ambiguous, I ask **before** writing code rather than
   building the wrong thing well.
4. I implement, and reference `DVB-12` in the commit message.
5. I run `python3 tools/check.py`, which is the only check that runs without Daz, Unity or
   Windows — and is therefore the only thing I can honestly claim to have run.
6. I move it to **In Review** and comment what changed, what to rebuild (Unity, plugin,
   or both), and what to look at in the headset.
7. You try it and either move it to Done or say what is wrong, in the ticket.

**In Review means waiting for the headset, not waiting for a code review.** Almost nothing
here can be verified from a checkout: the plugin needs the Daz SDK and MSVC, the client needs
Unity, and how a thing *feels* needs you wearing the headset. So a ticket arrives at you
built and argued for, not tested, and I say which of those it is rather than implying a pass.
`CLAUDE.md` at the repo root is the full version of this — what can be checked here, what
cannot, and the conventions that used to live only in the commit log.

**What makes a ticket I can take.** The *Done when* list. Not a description of the
feature — a list of things that are either true or not true once it works. "Takes are
easier to review" is not one; "the Takes tab shows a thumbnail beside each name" is.

**Drafts and context belong in the ticket, not in chat.** A screenshot pasted into
`DVB-12` is there in six weeks. The same screenshot pasted into a conversation is gone
when the conversation is.

## 4 · After release

Triage is where reports land. The useful thing I can do there, which is worth setting up
before it is needed:

- **Sort.** Read what came in, put an area and a kind on it, flag duplicates.
- **Pre-investigate.** Reproduce where a description allows it, find the cause, and
  comment with the file and the line. A ticket that arrives at you already diagnosed is a
  decision, not an afternoon.
- **Draft the reply**, for you to send or rewrite.

What I should not do without you: close a customer's ticket, promise a fix or a date, or
ship anything in response to a report that has not been reproduced.
