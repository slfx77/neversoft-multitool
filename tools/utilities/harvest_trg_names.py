# harvest_trg_names.py — recover names for checksums that TRG trigger files reference,
# from the plaintext strings that OTHER TRG commands in the same build carry (the
# user's ".trg files also use hashes" insight, 2026-07-13). Analysis lives in
# tools/diagnostics/trg_hash_probe.py; this applies its ship rule and writes the
# dictionary.
#
# Signal (per the probe): Spider-Man v2.1 TRGs — strings like "VaultDoor" hash
# (case-sensitive CRC, the PS1-era rule; lowercase CRC scores ZERO, a clean negative
# control) onto the same build's unresolved COMMANDPOINT checksum pool at ~5,000x
# chance. The THPS-era TRG pools are already 52-62% covered by the DDM-derived
# dictionaries and yield nothing new. Yield grows when the Spider-Man PSX builds'
# WAD-nested TRGs are extracted — rerun then.
#
# Ship rule: per (build, class, variant) cell, observed >= 50x expected chance
# (trials x targets / 2^32). Writes
# src/NeversoftMultitool/Core/QbKey/QbKeyNames.TrgStrings.txt (net-new only).
#
# Usage: python tools/utilities/harvest_trg_names.py [builds-root]
#        (defaults to Sample/Builds; pass the Desktop full-builds dir to include the
#        Spider-Man builds, which are not in the curated sample tree)

import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
import trg_hash_probe as probe  # noqa: E402  (argv[1] is consumed by the probe's BUILDS)

OUT_PATH = probe.QBKEY_DIR / "QbKeyNames.TrgStrings.txt"
MIN_RATIO = 50.0


def existing_hashes() -> set[int]:
    have: set[int] = set()
    for txt in probe.QBKEY_DIR.glob("QbKeyNames*.txt"):
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
    import json
    have = existing_hashes()
    proven: dict[int, str] = {}
    expected_shipped = 0.0

    for build_dir in sorted(probe.BUILDS.iterdir()):
        if not build_dir.is_dir() or not any(build_dir.rglob("*.trg")):
            continue
        jsons = probe.convert_build(build_dir, probe.CACHE / build_dir.name)
        strings: set[str] = set()
        checksums: set[int] = set()
        for j in jsons:
            s, c = probe.walk_strings_and_checksums(json.loads(j.read_text(encoding="utf-8")))
            strings |= s
            checksums |= c
        targets = checksums - have
        if not targets:
            continue

        for variant, hasher in (("case", probe.qb), ("lower", probe.qb_lower)):
            hits = {hasher(s): s for s in strings if hasher(s) in targets}
            expected = len(strings) * len(targets) / 2 ** 32
            ship = hits and len(hits) >= MIN_RATIO * expected
            print(f"{build_dir.name} [trg-string/{variant}]: observed {len(hits)}, "
                  f"expected {expected:.3f} -> {'SHIP' if ship else 'drop'}")
            if ship:
                expected_shipped += expected
                for h, name in hits.items():
                    proven.setdefault(h, name)

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in proven.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"\nwrote {len(lines):,} names to {OUT_PATH.relative_to(REPO)} "
          f"(~{expected_shipped:.3f} expected chance collisions)")


if __name__ == "__main__":
    main()
