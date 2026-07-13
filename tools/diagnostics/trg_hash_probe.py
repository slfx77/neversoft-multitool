# trg_hash_probe.py — the user's insight applied to the PS1 era: QB scripts aren't the
# only hash-bearing files; .trg trigger files also reference objects by checksum.
# TRG nodes carry BOTH plaintext strings (RESTART names, command string args,
# C_LOAD_MODEL model names) and raw u32 checksums (COMMANDPOINT/TRICKOB/GOALOB/CRATE
# targets, command hex args). This probe measures, per build:
#   * how many TRG checksums already resolve via the QbKeyNames dictionaries
#   * whether TRG plaintext strings hash (case-sensitive AND lowercase CRC — the PS1
#     era is case-sensitive) onto the same build's TRG checksum pool
#   * whether on-disk filenames (bare / stem) hash onto the TRG checksum pool
# with expected-chance (trials × targets / 2^32) next to every observed count, per the
# collision-math ship rule from the filename-harvest work.
#
# Uses the shipped C# TRG parser via the CLI (JSONs cached under TestOutput), so the
# byte-level format knowledge stays in one place.
#
# Usage: python tools/diagnostics/trg_hash_probe.py [builds-root]   (from the repo root;
#        builds CLI JSONs on first run — needs src/NeversoftMultitool built for net10.0
#        Debug; builds-root defaults to Sample/Builds, pass the Desktop full-builds dir
#        to cover the Spider-Man v2.1 TRGs that aren't in the curated sample tree)

import json
import os
import re
import subprocess
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"
CACHE = REPO / "TestOutput" / "trg_probe_json"
CLI = REPO / "src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.exe"

HEX_ARG = re.compile(r"^0x[0-9A-Fa-f]{8}$")


def qb(s: str) -> int:
    return (zlib.crc32(s.encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def qb_lower(s: str) -> int:
    return qb(s.lower())


def known_hashes() -> set[int]:
    have: set[int] = set()
    for txt in QBKEY_DIR.glob("QbKeyNames*.txt"):
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    have.add(int(line[eq + 3:], 16))
                except ValueError:
                    pass
    return have


def convert_build(build_dir: Path, out_dir: Path) -> list[Path]:
    """Convert every .trg under the build to JSON (cached), mirroring the tree."""
    jsons = []
    # NOTE: one case-insensitive glob only — adding rglob("*.TRG") double-counts on Windows.
    for trg in sorted({p.resolve() for p in build_dir.rglob("*.trg")}):
        rel = trg.relative_to(build_dir)
        dest_dir = out_dir / rel.parent
        dest = dest_dir / (trg.stem + ".json")
        if not dest.exists():
            dest_dir.mkdir(parents=True, exist_ok=True)
            subprocess.run([str(CLI), "trg", str(trg), "-o", str(dest_dir)],
                           capture_output=True, check=False)
        if dest.exists():
            jsons.append(dest)
    return jsons


def walk_strings_and_checksums(doc) -> tuple[set[str], set[int]]:
    strings: set[str] = set()
    checksums: set[int] = set()

    def visit(v):
        if isinstance(v, dict):
            for key, item in v.items():
                if key == "checksum" and isinstance(item, int):
                    checksums.add(item & 0xFFFFFFFF)
                elif key in ("name", "value") and isinstance(item, str) and HEX_ARG.match(item):
                    checksums.add(int(item, 16))
                else:
                    visit(item)
        elif isinstance(v, list):
            for item in v:
                visit(item)
        elif isinstance(v, str):
            if HEX_ARG.match(v):
                checksums.add(int(v, 16))
            elif 3 <= len(v) <= 64 and not v.startswith("Unknown_"):
                strings.add(v)

    visit(doc.get("nodes", []))
    return strings, checksums


def build_filename_candidates(build_dir: Path) -> dict[str, str]:
    cands: dict[str, str] = {}
    for _root, _dirs, files in os.walk(build_dir):
        for name in files:
            dot = name.rfind(".")
            cands.setdefault(name, "file-name")
            if dot > 0:
                cands.setdefault(name[:dot], "file-stem")
    return cands


def main() -> None:
    known = known_hashes()
    grand_hits: dict[int, tuple[str, str]] = {}
    total_expected = Counter()
    total_observed = Counter()

    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir() or not any(build_dir.rglob("*.trg")):
            continue
        jsons = convert_build(build_dir, CACHE / build_dir.name)
        strings: set[str] = set()
        checksums: set[int] = set()
        for j in jsons:
            s, c = walk_strings_and_checksums(json.loads(j.read_text(encoding="utf-8")))
            strings |= s
            checksums |= c

        resolved = sum(1 for c in checksums if c in known)
        targets = checksums - known
        print(f"{build_dir.name}")
        print(f"  trg files: {len(jsons)}  strings: {len(strings):,}  checksums: {len(checksums):,} "
              f"(already resolved: {resolved}, unresolved targets: {len(targets):,})")

        classes: dict[str, dict[str, str]] = {
            "trg-string": {s: "trg-string" for s in strings},
            "file": build_filename_candidates(build_dir),
        }
        for label, cands in classes.items():
            for variant, hasher in (("case", qb), ("lower", qb_lower)):
                hits = 0
                for cand in cands:
                    h = hasher(cand)
                    if h in targets:
                        hits += 1
                        grand_hits.setdefault(h, (cand, f"{label}/{variant}"))
                expected = len(cands) * len(targets) / 2 ** 32
                key = f"{label}/{variant}"
                total_expected[key] += expected
                total_observed[key] += hits
                print(f"  {key:18s}: trials {len(cands):7,}  expected-chance {expected:7.3f}  observed {hits:5,}")

    print("\n=== TOTALS (unique hits across builds) ===")
    by_class = Counter(cls for _n, cls in grand_hits.values())
    for key in sorted(set(total_expected) | set(by_class)):
        print(f"  {key:18s}: observed {by_class[key]:5,}  expected-chance {total_expected[key]:7.3f}")
    print("\nsample hits:")
    for i, (h, (n, cls)) in enumerate(sorted(grand_hits.items(), key=lambda kv: kv[1][0].lower())):
        if i >= 25:
            break
        print(f"  [{cls}] 0x{h:08X} = {n}")


if __name__ == "__main__":
    main()
