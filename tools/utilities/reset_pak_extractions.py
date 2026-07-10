# reset_pak_extractions.py — delete in-place PAK/APK extraction directories under a
# build tree so the `unpack` CLI re-extracts them (its skip-if-exists rule keeps
# non-empty {stem}/ dirs otherwise). Needed after the 2026-07-10 header-relative
# offset fix: every pre-fix extraction of a multi-entry pak is byte-garbled.
#
# Usage:
#   python reset_pak_extractions.py <build-dir>            # dry run (list only)
#   python reset_pak_extractions.py <build-dir> --apply    # actually delete
#
# Only directories named {stem} adjacent to a pak-family archive file
# ({stem}.pak.*, {stem}.apk.*, {stem}.pak) are touched; WAD/PRE/PKR/DDX/BON
# extraction dirs are left alone.

import shutil
import sys
from pathlib import Path

PAK_SUFFIXES = (".pak", ".apk")


def stem_dir_for(archive: Path) -> Path | None:
    """{stem} = filename minus the LAST extension (qb.pak.ps2 -> qb.pak)."""
    name = archive.name
    lower = name.lower()
    # match .pak/.apk either as the final extension or as a double extension
    for suffix in PAK_SUFFIXES:
        if lower.endswith(suffix) or (suffix + ".") in lower:
            stem = name[: name.rfind(".")]
            return archive.parent / stem
    return None


def main() -> None:
    build = Path(sys.argv[1])
    apply_changes = "--apply" in sys.argv[2:]

    targets: list[tuple[Path, Path]] = []
    for archive in build.rglob("*"):
        if not archive.is_file():
            continue
        lower = archive.name.lower()
        if ".pab." in lower or ".mpk." in lower or lower.endswith((".pab", ".mpk")):
            continue
        if not any(s + "." in lower or lower.endswith(s) for s in PAK_SUFFIXES):
            continue
        extract_dir = stem_dir_for(archive)
        if extract_dir is not None and extract_dir.is_dir():
            targets.append((archive, extract_dir))

    print(f"{len(targets)} extraction dirs found under {build}")
    for archive, extract_dir in targets[:10]:
        print(f"  {extract_dir.relative_to(build)}  (from {archive.name})")
    if len(targets) > 10:
        print(f"  ... and {len(targets) - 10} more")

    if not apply_changes:
        print("dry run — pass --apply to delete")
        return

    for _, extract_dir in targets:
        shutil.rmtree(extract_dir)
    print(f"deleted {len(targets)} extraction dirs")


if __name__ == "__main__":
    main()
