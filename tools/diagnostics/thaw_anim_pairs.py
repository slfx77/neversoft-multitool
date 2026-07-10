# thaw_anim_pairs.py — build the PS2<->GC (and PC when present) pairing manifest
# for THAW .ska/.ske payloads. GC extracted files are QbKey-named while PS2/PC
# files are pak-offset-named (LE entries store a zero short-name CRC in the
# cutscene paks), so cross-platform pairs are matched by ENTRY ORDER within
# same-stem archives (bh_11_main.pak.ps2 <-> bh_11_main.apk.ngc), validated by
# payload size equality (the formats are field-for-field endian mirrors).
# GC name QbKeys (entry +0x0C) are annotated with names from the shipped dbg
# dictionaries where resolvable.
#
# Output: TestOutput/thaw_anim_pairs.csv with one row per (stem, type, ordinal):
#   stem,type,idx,name,gc_key,size_match,ps2_pak,ps2_src,ps2_pos,ps2_size,
#   gc_pak,gc_src,gc_pos,gc_size,pc_pak,pc_src,pc_pos,pc_size
# where *_pos is the resolved byte offset inside the *_src file: "pak" = the
# archive itself, "companion" = the sibling .pab (LE overrun), "mpk" = GC .mpk.
#
# Usage: python tools/diagnostics/thaw_anim_pairs.py   (from the repo root)

import csv
import struct
import sys
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from extract_qb_corpus import walk_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
PS2_BUILD = BUILDS / "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)"
GC_BUILD = BUILDS / "Tony Hawk's American Wasteland (2005-8-22, GC - Final)"
PC_BUILD = BUILDS / "Tony Hawk's American Wasteland (2006-2-6, PC - Final)"
OUT = REPO / "TestOutput" / "thaw_anim_pairs.csv"

ANIM_TYPES = {0x745DCD45: "ska", 0x7330095C: "ske"}


def load_names() -> dict[int, str]:
    names: dict[int, str] = {}
    for txt in (REPO / "src/NeversoftMultitool/Core/QbKey").glob("QbKeyNames*.txt"):
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    names.setdefault(int(line[eq + 3:], 16), line[:eq])
                except ValueError:
                    pass
    return names


def index_archives(build: Path, pattern: str) -> dict[str, Path]:
    """stem (lowercased, platform suffix stripped) -> archive path.
    Ambiguous stems (same name in several directories, e.g. 'global') are
    dropped entirely — mis-paired archives would poison the manifest."""
    index: dict[str, Path] = {}
    dupes: set[str] = set()
    for p in build.rglob(pattern):
        stem = p.name.lower()
        for suffix in (".pak.ps2", ".apk.ngc", ".pak.wpc"):
            if stem.endswith(suffix):
                stem = stem[: -len(suffix)]
        if stem in index:
            dupes.add(stem)
        index.setdefault(stem, p)
    for stem in dupes:
        del index[stem]
    return index


def anim_entries(pak_path: Path, big: bool):
    """Ordered (type, name_key, src, pos, size) for ska/ske entries."""
    pak = pak_path.read_bytes()
    fmt = ">I" if big else "<I"
    result = []
    for hpos, off, size, flags, thash, _name in walk_entries(pak, big):
        kind = ANIM_TYPES.get(thash)
        if kind is None:
            continue
        if big:
            name_key = struct.unpack_from(fmt, pak, hpos + 0x0C)[0]
        else:
            name_key = struct.unpack_from(fmt, pak, hpos + 0x14)[0] \
                or struct.unpack_from(fmt, pak, hpos + 0x10)[0]
        in_companion = big and not (flags & 0x80000000)
        if in_companion:
            src, pos = "mpk", off
        else:
            resolved = hpos + off
            src, pos = ("pak", resolved) if resolved + size <= len(pak) else ("companion", resolved - len(pak))
        result.append((kind, name_key, src, pos, size))
    return result


def main() -> None:
    names = load_names()
    ps2 = index_archives(PS2_BUILD, "*.pak.ps2")
    gc = index_archives(GC_BUILD, "*.apk.ngc")
    pc = index_archives(PC_BUILD, "*.pak.wpc") if PC_BUILD.exists() else {}

    common = sorted(set(ps2) & set(gc))
    rows = []
    stats = defaultdict(int)
    mismatched_stems = []

    for stem in common:
        ps2_entries = anim_entries(ps2[stem], big=False)
        if not ps2_entries:
            continue
        gc_entries = anim_entries(gc[stem], big=True)
        pc_entries = anim_entries(pc[stem], big=False) if stem in pc else []

        by_type_ps2 = defaultdict(list)
        by_type_gc = defaultdict(list)
        by_type_pc = defaultdict(list)
        for e in ps2_entries:
            by_type_ps2[e[0]].append(e)
        for e in gc_entries:
            by_type_gc[e[0]].append(e)
        for e in pc_entries:
            by_type_pc[e[0]].append(e)

        for kind in ("ska", "ske"):
            a, b, c = by_type_ps2[kind], by_type_gc[kind], by_type_pc[kind]
            if not a and not b:
                continue
            if len(a) != len(b):
                stats[f"{kind}_count_mismatch"] += 1
                mismatched_stems.append((stem, kind, len(a), len(b)))
                continue
            for i, (pe, ge) in enumerate(zip(a, b)):
                _, _, p_src, p_pos, p_size = pe
                _, g_key, g_src, g_pos, g_size = ge
                pcf = c[i] if i < len(c) else None
                size_match = p_size == g_size
                stats[f"{kind}_pairs"] += 1
                stats[f"{kind}_size_match"] += size_match
                rows.append({
                    "stem": stem, "type": kind, "idx": i,
                    "name": names.get(g_key, ""), "gc_key": f"{g_key:08X}",
                    "size_match": int(size_match),
                    "ps2_pak": str(ps2[stem].relative_to(BUILDS)),
                    "ps2_src": p_src, "ps2_pos": p_pos, "ps2_size": p_size,
                    "gc_pak": str(gc[stem].relative_to(BUILDS)), "gc_src": g_src, "gc_pos": g_pos, "gc_size": g_size,
                    "pc_pak": str(pc[stem].relative_to(BUILDS)) if pcf else "",
                    "pc_src": pcf[2] if pcf else "", "pc_pos": pcf[3] if pcf else "", "pc_size": pcf[4] if pcf else "",
                })

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with open(OUT, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)

    named = sum(1 for r in rows if r["name"])
    print(f"stems in common with anim payloads: paired across {len(set(r['stem'] for r in rows))}")
    for k in sorted(stats):
        print(f"  {k}: {stats[k]}")
    print(f"  named via dbg dictionary: {named}/{len(rows)}")
    if mismatched_stems:
        print("count-mismatched stems (skipped):")
        for stem, kind, na, nb in mismatched_stems[:20]:
            print(f"  {stem} [{kind}]: ps2={na} gc={nb}")
    print(f"wrote {len(rows)} pairs to {OUT.relative_to(REPO)}")


if __name__ == "__main__":
    main()
