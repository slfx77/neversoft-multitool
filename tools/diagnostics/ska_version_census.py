# ska_version_census.py — histogram the .ska header version field across every
# build in Sample/Builds, in both endian readings. Purpose: prove the THAW
# "version 0x28" detection gate is collision-free against the THPS4/THUG/THUG2
# corpora the existing SkaFile parser already handles, and show which builds
# (THAW/P8/THPG) share the THAW container. Also reports the prevalence of the
# 20-byte 0xFF run at +0x14 (the THAW discriminator fallback) per version.
#
# Usage: python tools/diagnostics/ska_version_census.py   (from the repo root)

import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"

SKA_EXTS = (".ska", ".ska.ps2", ".ska.xbx", ".ska.wpc", ".ska.ngc")


def is_ska(path: Path) -> bool:
    name = path.name.lower()
    return any(name.endswith(ext) for ext in SKA_EXTS)


def main() -> None:
    # build -> version-le -> Counter of (flags-class, ff-run) prevalence
    per_build: dict[str, Counter] = defaultdict(Counter)
    ff_by_version: dict[tuple[str, int], int] = defaultdict(int)

    for build in sorted(BUILDS.iterdir()):
        if not build.is_dir():
            continue
        files = [p for p in build.rglob("*") if p.is_file() and is_ska(p)]
        if not files:
            continue
        for p in files:
            with open(p, "rb") as f:
                head = f.read(0x28)
            if len(head) < 0x28:
                per_build[build.name]["<too-small>"] += 1
                continue
            ver_le = struct.unpack_from("<I", head, 0)[0]
            ver_be = struct.unpack_from(">I", head, 0)[0]
            # Report under the plausible reading: prefer the smaller value
            # (versions are tiny; the other reading is byte-swap noise).
            if ver_le <= ver_be:
                key, ver = f"LE 0x{ver_le:X}", ver_le
            else:
                key, ver = f"BE 0x{ver_be:X}", ver_be
            per_build[build.name][key] += 1
            if head[0x14:0x28] == b"\xff" * 20:
                ff_by_version[(build.name, ver)] += 1

    print(f"{'build':60s} {'version':>12s} {'count':>7s} {'ff@0x14':>8s}")
    for build, versions in per_build.items():
        for key, count in sorted(versions.items(), key=lambda kv: -kv[1]):
            ver = int(key.split("0x")[1], 16) if "0x" in key else -1
            ff = ff_by_version.get((build, ver), 0)
            print(f"{build:60s} {key:>12s} {count:7d} {ff:8d}")

    # The gate check: any non-THAW-era build with version 0x28 in either reading?
    print("\n--- collision check: version 0x28 outside THAW/P8/THPG ---")
    thaw_era = ("American Wasteland", "Project 8", "Proving Ground")
    collisions = [
        (b, k, c)
        for b, versions in per_build.items()
        for k, c in versions.items()
        if k.endswith(" 0x28") and not any(t in b for t in thaw_era)
    ]
    if collisions:
        for b, k, c in collisions:
            print(f"COLLISION: {b}: {k} x{c}")
        sys.exit(1)
    print("none — version 0x28 gate is collision-free")


if __name__ == "__main__":
    main()
