# corpus_extension_census.py — count files by (normalized) extension across every
# build in Sample/Builds and bucket them by support status, so "what's left?"
# questions get an empirical answer instead of a stale-docs one. Extensions are
# normalized to their last one or two suffixes (.tex.ngc, .skin.ps2, ...);
# conversion artifacts and extraction directories are excluded.
#
# Usage: python tools/diagnostics/corpus_extension_census.py [--per-build]

import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"

# Platform/second-level suffixes that combine with a format suffix.
PLATFORM_SUFFIXES = {".ps2", ".xbx", ".wpc", ".ngc", ".ps3", ".xen", ".wii"}

# Conversion artifacts (outputs, not corpus).
ARTIFACTS = {".png", ".bmp", ".dds", ".wav", ".mp4", ".glb", ".gltf", ".obj", ".json", ".q", ".txt2"}

SUPPORTED = {
    # archives
    ".wad", ".hed", ".pkr", ".pre", ".prx", ".ddx", ".bon", ".pak", ".pab", ".apk", ".mpk", ".sqb",
    # textures
    ".psx", ".pvr", ".rle", ".bmr", ".tex", ".img", ".txd",
    # meshes / scenes / collision
    ".ddm", ".mdl", ".skin", ".iskin", ".scn", ".geom", ".skn", ".bsp", ".col", ".lit",
    # skeletons / animation
    ".ske", ".ska", ".bin.standardkey",  # (standardkey handled via ska)
    # scripts / triggers
    ".qb", ".trg", ".dbg",
    # audio / video
    ".xa", ".vab", ".vag", ".pss", ".adx", ".kat", ".sfx", ".sfd", ".str", ".vid",
}

KNOWN_UNSUPPORTED = {
    ".stex": "raw streaming-texture payloads (metadata lives elsewhere) — backlog",
    ".ppv": "Spider-Man proto runtime container (BVmC) — backlog",
    ".shd": "THAW GC shaders — undocumented",
    ".bik": "Bink video — out of scope",
    ".cam": "cutscene cam data (QbKey-typed pak entries)",
    ".anm": "anim variant (QbKey-typed pak entries)",
}

NON_FORMATS = {
    ".scc", ".bin", ".prk", ".usg", ".psh", ".cas", ".fam", ".sym", ".cfg", ".ini", ".txt", ".md",
    ".exe", ".dll", ".elf", ".irx", ".xml", ".html", ".htm", ".doc", ".pdf", ".bat", ".inf", ".ico",
    ".dat", ".db", ".log", ".lst", ".map", ".h", ".c", ".cpp", ".dol", ".tgc", ".rel", ".mp3",
    ".wma", ".asf", ".css", ".url", ".cab", ".msi", ".hdr", ".bmp2",
}


def normalize(path: Path) -> str:
    suffixes = [s.lower() for s in path.suffixes[-2:]]
    if len(suffixes) == 2 and suffixes[1] in PLATFORM_SUFFIXES:
        return "".join(suffixes)
    return suffixes[-1] if suffixes else "<none>"


def base_format(ext: str) -> str:
    for plat in PLATFORM_SUFFIXES:
        if ext.endswith(plat) and ext.count(".") == 2:
            return ext[: -len(plat)]
    return ext


def main() -> None:
    per_build = "--per-build" in sys.argv
    counts: Counter[str] = Counter()
    builds_for: dict[str, set] = defaultdict(set)
    for build in sorted(BUILDS.iterdir()):
        if not build.is_dir():
            continue
        for p in build.rglob("*"):
            if not p.is_file():
                continue
            ext = normalize(p)
            if base_format(ext) in ARTIFACTS or ext in ARTIFACTS:
                continue
            counts[ext] += 1
            builds_for[ext].add(build.name.split(" (")[0])

    groups: dict[str, list] = defaultdict(list)
    for ext, n in counts.most_common():
        base = base_format(ext)
        if base in SUPPORTED:
            group = "supported"
        elif base in KNOWN_UNSUPPORTED:
            group = "unsupported (known)"
        elif base in NON_FORMATS:
            group = "non-format"
        else:
            group = "UNRECOGNIZED"
        groups[group].append((ext, n))

    for group in ("UNRECOGNIZED", "unsupported (known)", "supported", "non-format"):
        items = groups.get(group, [])
        total = sum(n for _, n in items)
        print(f"\n=== {group} — {len(items)} extensions, {total} files ===")
        if group == "supported" and not per_build:
            continue  # counts only unless asked
        for ext, n in items:
            builds = sorted(builds_for[ext])
            where = ", ".join(builds[:3]) + ("…" if len(builds) > 3 else "")
            print(f"  {ext:16s} {n:7d}   [{where}]")


if __name__ == "__main__":
    main()
