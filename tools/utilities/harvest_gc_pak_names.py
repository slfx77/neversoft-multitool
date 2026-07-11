# harvest_gc_pak_names.py — recover names for THAW GC pak entries whose name
# QbKeys (+0x0C) don't resolve. Key derivation rule (validated on all 745
# named GC entries): key = QbKey(lowercased FULL PATH with the last/platform
# extension stripped), e.g. "cutscenes\bh_11\ngc\x\y.qb.ngc" hashes as
# "cutscenes\bh_11\ngc\x\y.qb". A handful of extension-less names hash the
# bare filename minus its last extension.
#
# Candidates come from (1) LE twin paks' in-file filename strings (platform
# dirs/suffixes swapped to ngc) and (2) plain-text strings inside every QB
# payload. A candidate is accepted ONLY if its hash equals a stored key, so
# false positives are impossible.
#
# Writes src/NeversoftMultitool/Core/QbKey/QbKeyNames.ThawGcPaks.txt with the
# newly proven pairs (skipping hashes already in other dictionaries).
#
# Usage: python tools/utilities/harvest_gc_pak_names.py   (from the repo root)

import re
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from extract_qb_corpus import walk_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
GC_BUILD = BUILDS / "Tony Hawk's American Wasteland (2005-8-22, GC - Final)"
LE_BUILDS = [
    (BUILDS / "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "*.pak.ps2"),
    (BUILDS / "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "*.pak.wpc"),
]
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"
OUT_PATH = QBKEY_DIR / "QbKeyNames.ThawGcPaks.txt"

STRING_RE = re.compile(rb"[\x20-\x7e]{4,128}")


def qbkey(s: str) -> int:
    return (zlib.crc32(s.lower().encode("latin1")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def existing_hashes() -> set[int]:
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


def index_by_stem(build: Path, pattern: str) -> dict[str, Path]:
    index: dict[str, Path] = {}
    dupes: set[str] = set()
    for p in build.rglob(pattern):
        stem = p.name.lower()
        for suffix in (".pak.ps2", ".apk.ngc", ".pak.ngc", ".pak.wpc"):
            if stem.endswith(suffix):
                stem = stem[: -len(suffix)]
        if stem in index:
            dupes.add(stem)
        index.setdefault(stem, p)
    for stem in dupes:
        del index[stem]
    return index


def strip_last_ext(path: str) -> str:
    slash = path.rfind("\\")
    dot = path.rfind(".")
    return path[:dot] if dot > slash else path


def name_variants(name: str) -> set[str]:
    """Candidate strings in the GC key's canonical form: lowercased path with
    the last extension stripped, LE platform dirs swapped to ngc."""
    lower = name.lower().replace("/", "\\").strip()
    forms = {lower}
    for a in ("\\ps2\\", "\\wpc\\", "\\xbx\\"):
        if a in lower:
            forms.add(lower.replace(a, "\\ngc\\"))
    for form in list(forms):
        if form.endswith((".ps2", ".wpc", ".xbx")):
            forms.add(form[:-4] + ".ngc")
    variants = set()
    for form in forms:
        variants.add(strip_last_ext(form))
        base = form.rsplit("\\", 1)[-1]
        variants.add(strip_last_ext(base))
    return variants


def main() -> None:
    have = existing_hashes()

    # Collect every unresolved GC key.
    gc_keys: set[int] = set()
    gc_by_stem: dict[str, list[tuple[int, int]]] = {}  # stem -> [(entryIdx, key)]
    gc_index = index_by_stem(GC_BUILD, "*.apk.ngc")
    for stem, path in gc_index.items():
        data = path.read_bytes()
        entries = []
        for i, (hpos, off, size, flags, thash, name) in enumerate(walk_entries(data, True)):
            key = struct.unpack_from(">I", data, hpos + 0x0C)[0]
            entries.append((i, key))
            if not name and key and key not in have:
                gc_keys.add(key)
        gc_by_stem[stem] = entries
    print(f"unresolved GC keys: {len(gc_keys):,}")

    proven: dict[int, str] = {}

    # Source 1: in-file names from ALL paks on every platform. Hash validation
    # is the filter, so no entry pairing is needed — any name whose canonical
    # form hashes to an unresolved key is proof.
    le_names: set[str] = set()
    for le_build, pattern in LE_BUILDS:
        for pak in le_build.rglob(pattern):
            for *_e, name in walk_entries(pak.read_bytes(), False):
                if name:
                    le_names.add(name)
    for name in le_names:
        for cand in name_variants(name):
            h = qbkey(cand)
            if h in gc_keys and h not in proven:
                proven[h] = cand
    print(f"after LE-name propagation ({len(le_names):,} names): {len(proven):,} proven")

    # Source 2: strings from every QB payload in the corpus (all platforms),
    # including strings hidden inside LZSS-compressed script bodies.
    from thaw_qb_probe import lzss_decompress  # noqa: PLC0415

    candidates: set[str] = set()

    def collect_strings(blob: bytes) -> None:
        for m in STRING_RE.finditer(blob):
            candidates.add(m.group().decode("latin1"))

    for build, pattern in [(GC_BUILD, "*.qb.ngc")] + [(b, p.replace(".pak.", ".qb.")) for b, p in LE_BUILDS]:
        for qb in build.rglob(pattern):
            data = qb.read_bytes()
            collect_strings(data)
            # scripts: u32 unk + u32 decompSize + u32 compSize + LZSS blob;
            # cheap heuristic scan for plausible headers is overkill — just
            # try LZSS at every 0x010C0100/old-encoding script marker is
            # fragile, so decompress opportunistically: any window that
            # inflates cleanly adds candidate strings.
            pos = 0
            while True:
                pos = data.find(b"\x01\x00\x00\x00", pos)
                if pos < 0 or pos + 12 > len(data):
                    break
                decomp = int.from_bytes(data[pos + 4:pos + 8], "little")
                comp = int.from_bytes(data[pos + 8:pos + 12], "little")
                if 0 < comp < decomp < 0x100000 and pos + 12 + comp <= len(data):
                    try:
                        collect_strings(lzss_decompress(data[pos + 12:pos + 12 + comp])[:decomp])
                    except Exception:
                        pass
                pos += 4

    # Source 3: every name in the existing dictionaries (dbg harvests carry
    # scripty identifiers AND asset paths).
    for txt in QBKEY_DIR.glob("QbKeyNames*.txt"):
        if txt.name == OUT_PATH.name:
            continue
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                candidates.add(line[:eq])

    print(f"string candidates: {len(candidates):,}")
    remaining = gc_keys - set(proven)
    for s in candidates:
        for cand in name_variants(s):
            h = qbkey(cand)
            if h in remaining and h not in proven:
                proven[h] = cand
    print(f"after QB-string + dictionary harvest: {len(proven):,} proven")

    # Source 4: QTex zip debug.log vocabulary × path templates. The zips carry
    # `texturetool "...\<Name>.png"` build lines whose <Name> stems are the CAS/skater
    # part vocabulary that ships in no other wordlist. Crossing those stems with the
    # (prefix, extension) templates mined from already-resolved GC keys reconstructs
    # sibling keys like `models\skater_male\<part>.tex/.skin` — still hash-proven.
    templates: set[tuple[str, str]] = set()
    for name in candidates | set(proven.values()):
        n = name.lower().replace("/", "\\")
        slash, dot = n.rfind("\\"), n.rfind(".")
        if slash > 0 and dot > slash:
            templates.add((n[: slash + 1], n[dot:]))
    zip_stems: set[str] = set()
    tool_re = re.compile(rb'"([^"]+\.(?:png|tif|tga))"', re.IGNORECASE)
    zip_builds = [(GC_BUILD, "*.zip.ngc")] + [(b, "*.zip.wpc") for b, _ in LE_BUILDS]
    for build, pattern in zip_builds:
        for zp in build.rglob(pattern):
            for m in tool_re.finditer(zp.read_bytes()):
                stem = m.group(1).decode("latin1").lower().replace("/", "\\").rsplit("\\", 1)[-1]
                zip_stems.add(stem.rsplit(".", 1)[0])
    remaining = gc_keys - set(proven)
    for pre, ext in templates:
        for stem in zip_stems:
            h = qbkey(pre + stem + ext)
            if h in remaining and h not in proven:
                proven[h] = pre + stem + ext
    print(f"after zip-vocab × template harvest ({len(zip_stems):,} stems, "
          f"{len(templates):,} templates): {len(proven):,} proven")

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in proven.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(lines)} entries to {OUT_PATH.relative_to(REPO)}")


if __name__ == "__main__":
    main()
