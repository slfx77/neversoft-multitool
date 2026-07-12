# harvest_script_ref_names.py — recover names for checksums that QB SCRIPTS
# REFERENCE (Name token 0x16) by hashing the on-disk filenames of the same build.
# Analysis lives in tools/diagnostics/filesystem_script_probe.py (proper token walk,
# within-build matching, lowercase CRC, expected-chance-vs-observed per class);
# this script applies its ship rule and writes the dictionary.
#
# Unlike the pak-key harvests (which validate against a stored key and are
# zero-false-positive), script-reference targets are open-ended, so each
# (build, class) cell is included only when observed >= 50x its expected chance
# count — per-entry confidence >= 98%, with roughly 2-3 expected chance
# collisions across the whole output. The resource name sorts after the other
# QbKeyNames*.txt files, so the first-wins loader gives every proven dictionary
# priority over this one.
#
# Writes src/NeversoftMultitool/Core/QbKey/QbKeyNames.ScriptRefs.txt.
# Usage: python tools/utilities/harvest_script_ref_names.py   (from the repo root)

import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from filesystem_script_probe import (  # noqa: E402
    BUILDS, QBKEY_DIR, build_candidates, build_script_refs, qb_lower,
)

OUT_PATH = QBKEY_DIR / "QbKeyNames.ScriptRefs.txt"
MIN_RATIO = 50.0  # observed >= 50x expected-chance for a cell to ship


def existing_hashes() -> set[int]:
    """Like the probe's, but excludes this harvest's own output so re-runs are stable."""
    have: set[int] = set()
    for txt in QBKEY_DIR.glob("QbKeyNames*.txt"):
        if txt.name == OUT_PATH.name:
            continue
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    have.add(int(line[eq + 3:], 16))
                except ValueError:
                    pass
    return have


def main() -> None:
    have = existing_hashes()
    proven: dict[int, str] = {}
    expected_shipped = 0.0

    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir():
            continue
        refs = build_script_refs(build_dir)
        targets = refs - have
        if not targets:
            continue
        cands = build_candidates(build_dir)

        cells: dict[str, dict[int, str]] = {"path": {}, "name": {}, "stem": {}}
        trials = {"path": 0, "name": 0, "stem": 0}
        for cand, cls in cands.items():
            trials[cls] += 1
            h = qb_lower(cand)
            if h in targets:
                cells[cls].setdefault(h, cand)

        for cls, hits in cells.items():
            expected = trials[cls] * len(targets) / 2 ** 32
            observed = len(hits)
            ship = observed > 0 and observed >= MIN_RATIO * expected
            print(f"{build_dir.name} [{cls}]: observed {observed}, "
                  f"expected {expected:.2f} -> {'SHIP' if ship else 'drop'}")
            if ship:
                expected_shipped += expected
                for h, name in hits.items():
                    proven.setdefault(h, name)

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in proven.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"\nwrote {len(lines):,} names to {OUT_PATH.relative_to(REPO)} "
          f"(~{expected_shipped:.1f} expected chance collisions among them)")


if __name__ == "__main__":
    main()
