#!/usr/bin/env python3
"""Name carved N64 model slots from the TRIGGERS, which spell filenames literally.

THE LEAD
--------
A TRG script has to tell the engine which files to load, so it carries the names
as inline NUL-terminated strings — nothing hashed, nothing indexed:

    0x8E SetObjFile   -> the level's OBJECT BANK      ("SkVans_O")
    0x80 SpoolEnv     -> a GEOMETRY REGION            ("SkVans", "SkVans_2")
    0x7E SpoolIn      -> anything else spooled        ("SkVans_L", "C_Taxi")

THE RULE
--------
A level's files occupy ONE CONTIGUOUS RUN of model slots, ordered
case-insensitively by filename. So the TRG's file set, sorted, lines up with the
run one-for-one:

    TRG 001 sorted:  skdown  skdown_2  skdown_h  skdown_l  skdown_o  skdownl2
    slots 4..9:      skdown  skdown_2  skdown_h  (stub)    skdown_o  (stub)

WHY THIS BEATS THE CONTENT DICTIONARY
-------------------------------------
It comes from the ROM, needs no PS1 corpus, and names the two classes content
identity CANNOT reach: the `_l` texture libraries (carved as 24-byte stubs with
no content to key on) and files whose content is shared. It also makes the
trigger->bank association DIRECT — SetObjFile names the bank outright, so the
structural-filter + checksum-argmax heuristic is only needed as a fallback.

It does NOT name characters and props, which no TRG references. That is what the
content dictionary is for; the two are complementary.

Usage:
    python tools/diagnostics/n64_trg_slot_names.py [--carve-root DIR] [--rom NAME]

Requires TRG JSON dumps; produce them with
    dotnet run --project src/NeversoftMultitool -f net10.0 -- trg <carve>/triggers -o <out>
or pass --dump-root pointing at an existing dump tree.
"""

from __future__ import annotations

import argparse
import json
import pathlib
import struct
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
sys.path.insert(0, str(REPO / "tools" / "utilities"))

from harvest_n64_bundle_names import compute_key  # noqa: E402
from n64_trg_bundle_checksum_join import parse_psx_header  # noqa: E402

# Commands whose operand is a FILE name rather than a checksum or a node label.
FILE_OPCODES = {0x80: "SpoolEnv", 0x7E: "SpoolIn", 0x8E: "SetObjFile"}
BANK_OPCODE = 0x8E


def load_dictionary() -> dict[int, list[str]]:
    path = REPO / "src" / "NeversoftMultitool" / "Core" / "Formats" / "Mesh" / "N64" / "N64BundleNames.txt"
    table: dict[int, list[str]] = {}
    if not path.is_file():
        return table
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        table[int(key, 16)] = value.split("|")
    return table


def slot_contents(carve: pathlib.Path, table: dict[int, list[str]]):
    """[(slot, contentNames|None, isStub)] in slot order."""
    rows = []
    for bundle in sorted((carve / "models").iterdir()):
        shells = sorted(bundle.glob("*.psx.n64")) if bundle.is_dir() else []
        if not shells:
            continue
        parsed = parse_psx_header(shells[0].read_bytes(), big_endian=True)
        if parsed is None:
            rows.append((bundle.name, None, True))
            continue
        rows.append((bundle.name, table.get(compute_key(parsed[3])), False))
    return rows


def level_stem(bank: str) -> str:
    """`l1a1_o` / `skjamo2` / `skros_o` -> the level stem the run is built on."""
    stem = bank
    for suffix in ("_o2", "o2", "_o"):
        if stem.endswith(suffix):
            return stem[: -len(suffix)]
    return stem


def trg_file_sets(dump_dir: pathlib.Path):
    """trigger slot -> (family file set sorted, bank name or None, outsiders).

    A TRG names more than its level: Spider-Man's l1a1 spools `henchman`,
    `blackcat`, `THUG`; THPS1's skdown spools `c_taxi`. Those live elsewhere in
    the slot space, so including them breaks the contiguous run. The FAMILY is
    the files sharing the level stem, and that is what the run holds.
    """
    out = {}
    for path in sorted(dump_dir.glob("*.json")):
        data = json.loads(path.read_text(encoding="utf-8"))
        names, bank = set(), None
        for node in data.get("nodes", []):
            for command in node.get("commands") or []:
                opcode = command.get("opcode")
                if opcode not in FILE_OPCODES:
                    continue
                for arg in command.get("args") or []:
                    if not isinstance(arg, str) or not arg or arg.startswith("0x"):
                        continue
                    names.add(arg.lower())
                    if opcode == BANK_OPCODE and bank is None:
                        bank = arg.lower()

        if bank is None:
            out[path.name.split(".")[0]] = ([], None, sorted(names))
            continue

        stem = level_stem(bank)
        family = sorted(n for n in names if n.startswith(stem))
        outsiders = sorted(n for n in names if not n.startswith(stem))
        out[path.name.split(".")[0]] = (family, bank, outsiders)
    return out


def align(rows, files: list[str]) -> dict[str, str] | None:
    """Anchor a sorted file set onto a contiguous slot run, via a content match.

    Anchoring on a CONTENT-NAMED slot rather than on position is what makes this
    safe: it needs one confirmed correspondence, then the contiguity and the
    shared ordering carry the rest — including the stubs, which have no content
    of their own and could never be anchored directly.
    """
    index = {name: i for i, name in enumerate(files)}
    for slot_index, (_, content, _) in enumerate(rows):
        if not content:
            continue
        for candidate in content:
            position = index.get(candidate.lower())
            if position is None:
                continue
            start = slot_index - position
            if start < 0 or start + len(files) > len(rows):
                continue
            # Every content-named slot inside the run must agree, or this is a
            # coincidence rather than an alignment.
            assignment, ok = {}, True
            for offset, name in enumerate(files):
                slot, slot_content, _ = rows[start + offset]
                if slot_content and not any(c.lower() == name for c in slot_content):
                    ok = False
                    break
                assignment[slot] = name
            if ok:
                return assignment
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--carve-root", type=pathlib.Path, default=REPO / "TestOutput" / "n64carve")
    ap.add_argument("--rom")
    ap.add_argument("--dump-root", type=pathlib.Path,
                    help="existing TRG JSON dumps (<root>/<rom>/*.json); dumped to a temp dir if absent")
    args = ap.parse_args()

    table = load_dictionary()
    total_slots = total_content = total_trg = total_new = total_stubs = 0

    for carve in sorted(p for p in args.carve_root.iterdir() if p.is_dir()):
        if args.rom and carve.name != args.rom:
            continue
        triggers = carve / "triggers"
        if not triggers.is_dir():
            continue

        if args.dump_root:
            dump = args.dump_root / carve.name
        else:
            dump = pathlib.Path(tempfile.mkdtemp(prefix="trgdump-"))
            subprocess.run(
                ["dotnet", "run", "--project", str(REPO / "src" / "NeversoftMultitool"),
                 "-f", "net10.0", "--no-build", "--", "trg", str(triggers), "-o", str(dump)],
                cwd=REPO, check=True, capture_output=True)

        rows = slot_contents(carve, table)
        sets = trg_file_sets(dump)

        named: dict[str, str] = {}
        unaligned = []
        outsider_names: set[str] = set()
        for trigger, (files, bank, outsiders) in sorted(sets.items()):
            outsider_names.update(outsiders)
            if not files:
                unaligned.append((trigger, bank, 0))
                continue
            assignment = align(rows, files)
            if assignment is None:
                unaligned.append((trigger, bank, len(files)))
                continue
            named.update(assignment)

        content_named = sum(1 for _, c, _ in rows if c)
        stubs = sum(1 for _, _, s in rows if s)
        new = sum(1 for slot, _, _ in rows
                  if slot in named and not next(c for s, c, _ in rows if s == slot))
        print(f"\n{carve.name}")
        print(f"  slots {len(rows)}  (stubs {stubs})   content-named {content_named}"
              f"   TRG-named {len(named)}   TRG names content cannot: {new}")
        if unaligned:
            print(f"  unaligned: {[(t, b) for t, b, _ in unaligned][:8]}"
                  f"{' ...' if len(unaligned) > 8 else ''}  ({len(unaligned)} of {len(sets)})")
        print(f"  non-level files the TRGs also name (characters, vehicles): {len(outsider_names)}")

        total_slots += len(rows)
        total_content += content_named
        total_trg += len(named)
        total_new += new
        total_stubs += stubs

    print(f"\nTOTAL slots {total_slots} (stubs {total_stubs}); content-named {total_content}; "
          f"TRG-named {total_trg}; TRG adds {total_new} content cannot reach")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
