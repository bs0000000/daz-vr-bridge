#!/usr/bin/env python3
"""Everything about this repo that can be checked without Daz, Unity or Windows.

    python3 tools/check.py

Runs anywhere Python 3.8+ runs, needs no packages, touches no network, and
starts nothing. It is the only check an agent on a Linux worktree can run, so
it deliberately covers the things that have no other guard: the two rig-profile
copies that are kept identical by hand, the profile's own references into the
skeleton dump beside it, and the agreement between PROTOCOL.md's message
catalog and the string literals both sides actually send.

What it cannot do is run a line of the plugin or the client. The framing code
is Qt, the client is Unity, and the only real verification is the headset --
see CLAUDE.md. A pass here means "nothing is contradicting itself on disk",
which is worth exactly that much and no more.
"""

import json
import re
import sys
from fnmatch import fnmatch
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROFILES = ROOT / "profiles"
RIG_PROFILES = ROOT / "client" / "Daz VR Bridge" / "Assets" / "DazVrBridge" / "Resources" / "RigProfiles"
PROTOCOL = ROOT / "protocol" / "PROTOCOL.md"
PLUGIN = ROOT / "plugin"
CLIENT = ROOT / "client" / "Daz VR Bridge" / "Assets" / "DazVrBridge"

failures = []
warnings = []


def fail(check, message):
    failures.append((check, message))


def warn(check, message):
    warnings.append((check, message))


def rel(path):
    return path.relative_to(ROOT).as_posix()


# --------------------------------------------------------------------------
# 1. The two copies of each rig profile are identical.
#
# `profiles/<rig>.json` is the source; Unity can only load what is under
# Resources/, so a byte copy lives there too and they are synced by hand.
# Nothing else in the repo notices when they drift apart.
# --------------------------------------------------------------------------

def check_profile_copies():
    check = "rig profile copies"
    sources = sorted(PROFILES.glob("*.json"))
    if not sources:
        fail(check, f"no profiles found in {rel(PROFILES)}")
        return

    for source in sources:
        copy = RIG_PROFILES / source.name
        if not copy.exists():
            fail(check, f"{rel(source)} has no counterpart at {rel(copy)}")
        elif source.read_bytes() != copy.read_bytes():
            fail(check, f"{rel(source)} and {rel(copy)} differ -- they are kept byte-identical by hand")

    for copy in sorted(RIG_PROFILES.glob("*.json")):
        if not (PROFILES / copy.name).exists():
            fail(check, f"{rel(copy)} has no source at {rel(PROFILES / copy.name)}")

    print(f"  {len(sources)} profile(s), each matched against its Resources copy")


# --------------------------------------------------------------------------
# 2. Each profile agrees with the skeleton dump beside it.
#
# Every bone a profile names has to exist in the rig. A typo here is invisible
# until a handle silently fails to appear in the headset, which is the most
# expensive place in this project to discover anything.
# --------------------------------------------------------------------------

BONE_LINE = re.compile(r"^(\S+)\s+<-\s*(\S+)?\s*\[(\w+)\]\s+(.*)$")
LIMIT = re.compile(r"([xyz])\[(-?[\d.]+),(-?[\d.]+)\]")


def read_bone_dump(path):
    """name -> {'order': 'YZX', 'limits': {'x': (lo, hi), ...}}"""
    bones = {}
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        match = BONE_LINE.match(line)
        if not match:
            continue
        name, _parent, order, rest = match.groups()
        limits = {axis: (float(lo), float(hi)) for axis, lo, hi in LIMIT.findall(rest)}
        bones[name] = {"order": order, "limits": limits}
    return bones


def check_profile_contents():
    check = "rig profile contents"
    checked = 0

    for source in sorted(PROFILES.glob("*.json")):
        try:
            profile = json.loads(source.read_text(encoding="utf-8-sig"))
        except json.JSONDecodeError as error:
            fail(check, f"{rel(source)} is not valid JSON: {error}")
            continue

        dump_path = source.with_suffix(".bones.txt")
        if not dump_path.exists():
            print(f"  {rel(source)}: no {rel(dump_path)}, bone names not checked")
            continue

        bones = read_bone_dump(dump_path)
        if not bones:
            fail(check, f"{rel(dump_path)} parsed to zero bones")
            continue

        where = rel(source)

        def known(name, context):
            if name not in bones:
                fail(check, f"{where}: {context} names '{name}', which is not in {rel(dump_path)}")
                return False
            return True

        known(profile.get("root", ""), "root")

        forward = profile.get("forward_daz")
        if not (isinstance(forward, list) and len(forward) == 3):
            fail(check, f"{where}: forward_daz must be a 3-element vector, got {forward!r}")

        for bone in profile.get("grabbable", []):
            known(bone, "grabbable")

        for hand, fingers in profile.get("fingers", {}).items():
            if hand.startswith("_"):
                continue
            known(hand, "fingers")
            for bone in fingers:
                known(bone, f"fingers[{hand}]")

        for name, chain in profile.get("chains", {}).items():
            if name.startswith("_"):
                continue
            chain_bones = chain.get("bones", [])
            for bone in chain_bones:
                known(bone, f"chains[{name}]")

            # The solver is analytic two-bone plus an effector: exactly three.
            if chain.get("ik") and len(chain_bones) != 3:
                fail(check, f"{where}: chains[{name}] is ik but has {len(chain_bones)} bones, expected 3")

            if "shoulder" in chain:
                known(chain["shoulder"], f"chains[{name}].shoulder")

            pole = chain.get("pole")
            if pole is not None and pole not in ("front", "back"):
                fail(check, f"{where}: chains[{name}].pole is '{pole}', expected 'front' or 'back'")

            for bone, axis in chain.get("hinge", {}).items():
                if not known(bone, f"chains[{name}].hinge"):
                    continue
                if axis not in ("x", "y", "z"):
                    fail(check, f"{where}: chains[{name}].hinge[{bone}] is '{axis}', expected x, y or z")
                    continue
                # A hinge about an axis Daz has locked shut would never bend.
                low, high = bones[bone]["limits"].get(axis, (0.0, 0.0))
                if low == high:
                    fail(check, f"{where}: chains[{name}] hinges {bone} about {axis}, "
                                f"but the rig locks that axis at [{low:g},{high:g}]")

            for driven, twists in chain.get("twist", {}).items():
                known(driven, f"chains[{name}].twist")
                for bone in twists:
                    if not known(bone, f"chains[{name}].twist[{driven}]"):
                        continue
                    # A twist bone turns about one axis and is locked on the other two.
                    free = [a for a, (lo, hi) in bones[bone]["limits"].items() if lo != hi]
                    if len(free) != 1:
                        fail(check, f"{where}: twist bone {bone} has {len(free)} free axes "
                                    f"({', '.join(free) or 'none'}), expected exactly 1")

        # Only a warning: RigProfile.IsGrabbable consults these patterns just
        # when `grabbable` is empty, so for a profile with an explicit list
        # they are a fallback, and may name bones this rig has never had.
        for pattern in profile.get("hidden", {}).get("patterns", []):
            if not any(fnmatch(bone, pattern) for bone in bones):
                warn(check, f"{where}: hidden pattern '{pattern}' matches no bone in {rel(dump_path)}")

        mirror = profile.get("mirror", {})
        left, right = mirror.get("prefix_left"), mirror.get("prefix_right")
        if left and right:
            for bone in profile.get("grabbable", []):
                if bone.startswith(left):
                    partner = right + bone[len(left):]
                    if partner not in profile.get("grabbable", []):
                        fail(check, f"{where}: grabbable '{bone}' has no mirror '{partner}'")

        checked += 1
        print(f"  {where}: {len(bones)} bones in the dump, all references resolve")

    if checked == 0 and not failures:
        print("  nothing to check")


# --------------------------------------------------------------------------
# 3. PROTOCOL.md and the code name the same messages.
#
# PROTOCOL.md says "change this file first, then the code" (line 3). Nothing
# enforced that until now: a message added to both ends and never written down
# read as documented, and a message renamed in the doc read as shipped.
#
# Only dotted types are checked. The rest -- hello, ping, welcome, error -- are
# ordinary English words and grepping for them finds prose, not messages.
# --------------------------------------------------------------------------

DOTTED = re.compile(r"`([a-z]+\.[a-z]+)`")

# A message type as it appears in source: a bare "noun.verb" string literal. Deliberately
# not anchored to the namespaces PROTOCOL.md already knows -- a message invented under a
# brand new namespace is exactly the one most likely to go undocumented.
MESSAGE_LITERAL = re.compile(r'"([a-z][a-z]{1,11}\.[a-z][a-z]{1,15})"')

# ...which also matches short filenames, and nothing else so far.
NOT_MESSAGES = (".json", ".duf", ".txt", ".png", ".bin", ".asset", ".unity",
                ".shader", ".mat", ".meta", ".dll", ".exe", ".log", ".tmp")


def documented_types():
    """dotted type -> True if the catalog marks it reserved for a later version."""
    types = {}
    for line in PROTOCOL.read_text(encoding="utf-8").splitlines():
        if not line.startswith("| `"):
            continue
        cells = line.split("|")
        if len(cells) < 3:
            continue
        reserved = "reserved" in line.lower()
        for name in DOTTED.findall(cells[1]):
            types[name] = reserved
    return types


def source_files():
    for path in sorted(PLUGIN.glob("*.cpp")) + sorted(PLUGIN.glob("*.h")):
        yield "plugin", path
    for path in sorted(CLIENT.rglob("*.cs")):
        yield "client", path


def check_protocol_catalog():
    check = "protocol catalog"
    if not PROTOCOL.exists():
        fail(check, f"{rel(PROTOCOL)} is missing")
        return

    types = documented_types()
    if not types:
        fail(check, f"no message types parsed out of {rel(PROTOCOL)} -- has the table format changed?")
        return

    seen = {"plugin": set(), "client": set()}
    for side, path in source_files():
        for name in MESSAGE_LITERAL.findall(path.read_text(encoding="utf-8", errors="replace")):
            if not name.endswith(NOT_MESSAGES):
                seen[side].add(name)

    # Documented and shipped: both sides have to know the word.
    for name, reserved in sorted(types.items()):
        if reserved:
            continue
        missing = [side for side in ("plugin", "client") if name not in seen[side]]
        if missing:
            fail(check, f"'{name}' is in {rel(PROTOCOL)} but no {' or '.join(missing)} source sends or handles it")

    # Shipped and undocumented: the rule this repo already states, enforced.
    for side in ("plugin", "client"):
        for name in sorted(seen[side] - set(types)):
            fail(check, f"the {side} uses '{name}', which is not in {rel(PROTOCOL)} "
                        f"-- change the protocol first, then the code")

    live = sum(1 for reserved in types.values() if not reserved)
    held = len(types) - live
    print(f"  {live} live message type(s) agreed between {rel(PROTOCOL)}, the plugin and the client"
          + (f", {held} reserved" if held else ""))


# --------------------------------------------------------------------------

CHECKS = [
    ("Rig profile copies are identical", check_profile_copies),
    ("Rig profiles match the skeleton dump", check_profile_contents),
    ("PROTOCOL.md matches the code", check_protocol_catalog),
]


def main():
    for title, run in CHECKS:
        print(f"{title}:")
        before = len(failures)
        try:
            run()
        except Exception as error:  # a broken check is a failed check
            fail(title, f"the check itself raised {type(error).__name__}: {error}")
        if len(failures) > before:
            for _, message in failures[before:]:
                print(f"  FAIL  {message}")
        print()

    for _, message in warnings:
        print(f"WARN  {message}")
    if warnings:
        print()

    if failures:
        print(f"{len(failures)} problem(s) found.")
        return 1

    print("All checks passed." + (f" {len(warnings)} warning(s), which do not fail the run." if warnings else ""))
    print("This says nothing about the plugin or the client running: neither can be")
    print("built or exercised here. See CLAUDE.md, 'What cannot be verified'.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
