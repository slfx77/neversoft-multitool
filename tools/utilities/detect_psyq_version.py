#!/usr/bin/env python3
"""
Detect PSY-Q SDK version by matching library function signatures against a PSX executable.
Uses the ghidra_psx_ldr signature database (JSON files with hex byte patterns + wildcards).

Usage:
    python detect_psyq_version.py <psx_executable> [--sigs <signature_dir>]
    python detect_psyq_version.py "Sample/Builds/Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)/SLUS_900.86"

Requires: ghidra_psx_ldr extension installed at GHIDRA_HOME (default: C:/tools/ghidra_12.0.2_PUBLIC)
"""

import json
import os
import re
import struct
import sys
from pathlib import Path
from collections import defaultdict


def load_signatures(sig_dir: Path) -> dict[str, list[dict]]:
    """Load all PSY-Q version signature files. Returns {version: [{'name': ..., 'sig': ...}, ...]}"""
    versions = {}
    for version_dir in sorted(sig_dir.iterdir()):
        if not version_dir.is_dir() or version_dir.name in ('generator', 'syscalls', 'til'):
            continue
        sigs = []
        for json_file in version_dir.glob('*.json'):
            with open(json_file, 'r') as f:
                entries = json.load(f)
            for entry in entries:
                if 'sig' in entry and 'name' in entry:
                    sigs.append({
                        'name': entry['name'],
                        'lib': json_file.stem,
                        'sig': entry['sig']
                    })
        if sigs:
            versions[version_dir.name] = sigs
    return versions


def compile_pattern(hex_sig: str) -> tuple[bytes, bytes]:
    """Convert hex signature string to (pattern_bytes, mask_bytes).
    '??' means wildcard (mask=0x00), anything else is literal (mask=0xFF)."""
    tokens = hex_sig.strip().split()
    pattern = bytearray()
    mask = bytearray()
    for token in tokens:
        if token == '??':
            pattern.append(0)
            mask.append(0)
        else:
            pattern.append(int(token, 16))
            mask.append(0xFF)
    return bytes(pattern), bytes(mask)


def compile_search_key(pattern: bytes, mask: bytes) -> tuple[bytes, int] | None:
    """Extract the longest consecutive fixed-byte sequence for fast pre-filtering.
    Returns (key_bytes, key_offset) or None if no usable key."""
    best_start = -1
    best_len = 0
    cur_start = -1
    cur_len = 0

    for i, m in enumerate(mask):
        if m == 0xFF:
            if cur_start == -1:
                cur_start = i
                cur_len = 1
            else:
                cur_len += 1
            if cur_len > best_len:
                best_start = cur_start
                best_len = cur_len
        else:
            cur_start = -1
            cur_len = 0

    if best_len >= 4:
        return pattern[best_start:best_start + best_len], best_start
    elif best_len >= 2:
        return pattern[best_start:best_start + best_len], best_start
    return None


def find_pattern(data: bytes, pattern: bytes, mask: bytes, max_matches: int = 1) -> list[int]:
    """Find byte pattern in data with mask. Uses fast pre-filter on longest fixed sequence."""
    matches = []
    plen = len(pattern)
    if plen == 0 or plen > len(data):
        return matches

    # Get a fast search key from consecutive fixed bytes
    key_info = compile_search_key(pattern, mask)
    if key_info is None:
        return matches

    key_bytes, key_offset = key_info
    pos = 0

    while pos <= len(data) - plen:
        # Fast search for the key sequence
        found = data.find(key_bytes, pos + key_offset)
        if found == -1:
            break

        start = found - key_offset
        if start < 0:
            pos = found + 1
            continue
        if start + plen > len(data):
            break

        # Verify full pattern with mask
        match = True
        for j in range(plen):
            if mask[j] and data[start + j] != pattern[j]:
                match = False
                break

        if match:
            matches.append(start)
            if len(matches) >= max_matches:
                return matches

        pos = start + 4  # MIPS instructions are 4-byte aligned

    return matches


def detect_psyq_version_primary(data: bytes) -> str | None:
    """Primary detection: look for 'Ps' marker with version byte (ghidra_psx_ldr method).

    From DetectPsyQ.java: searches for pattern 0x50 0x73 with mask 0xFF 0xFF 0xE0,
    then reads a short at offset -6 from match and formats as hex version string."""
    # The detection looks for the PSY-Q library version stamp embedded by the linker
    # Format: [version_short LE] ... [0x50 0x73 XX] where XX & 0xE0 match
    # Actually, let's search for the pattern more carefully

    # From the Java source: pattern bytes = {0x50, 0x73, 0x00, ...} mask = {0xFF, 0xFF, 0xE0, ...}
    # At offset 0x06 from the found position, extract short = version
    # Actually it searches starting from the beginning and the version is at a fixed offset

    # Let's try a simpler approach: look for "Ps" followed by specific byte patterns
    # that indicate PSY-Q library stamps
    idx = 0
    stamps = []
    while idx < len(data) - 8:
        idx = data.find(b'Ps', idx)
        if idx == -1:
            break
        # Check if this looks like a PSY-Q stamp: "Ps" at aligned position
        # followed by a small value and 0x45 0x00
        if idx + 6 <= len(data):
            after = data[idx+2:idx+6]
            # Common pattern: Ps XX 00 00 00 45 00  (where XX is a param count/type)
            if len(after) >= 4 and after[1] == 0x00 and after[2] == 0x00 and after[3] == 0x45:
                stamps.append((idx, after[0]))
        idx += 2

    if stamps:
        return stamps  # Return raw stamps for analysis
    return None


def detect_psyq_version_signatures(data: bytes, versions: dict[str, list[dict]],
                                     text_offset: int = 0x800) -> dict[str, dict]:
    """Signature-based detection: match library function patterns against binary.
    Returns {version: {'total': N, 'matched': N, 'matches': [...]}}"""
    # Only search the text section (skip PS-X EXE header)
    text_data = data[text_offset:]

    # Pre-compile all patterns once
    compiled_sigs: dict[str, list[tuple[dict, bytes, bytes]]] = {}
    for version, sigs in versions.items():
        compiled = []
        for sig in sigs:
            pattern, mask = compile_pattern(sig['sig'])
            # Use first 32 bytes — enough to uniquely identify, much faster
            max_len = min(32, len(pattern))
            compiled.append((sig, pattern[:max_len], mask[:max_len]))
        compiled_sigs[version] = compiled

    results = {}
    total_versions = len(compiled_sigs)
    for idx, (version, compiled) in enumerate(sorted(compiled_sigs.items())):
        print(f"  Scanning PSY-Q {version} ({idx+1}/{total_versions})... ", end='', flush=True)
        matched = []
        for sig, pattern, mask in compiled:
            offsets = find_pattern(text_data, pattern, mask, max_matches=1)
            if offsets:
                matched.append({
                    'name': sig['name'],
                    'lib': sig['lib'],
                    'offset': offsets[0] + text_offset
                })
        pct = len(matched) / len(compiled) * 100 if compiled else 0
        print(f"{len(matched)}/{len(compiled)} ({pct:.1f}%)")
        results[version] = {
            'total': len(compiled),
            'matched': len(matched),
            'pct': pct,
            'matches': matched
        }
    return results


def detect_aspsx_patterns(data: bytes, text_offset: int = 0x800) -> dict:
    """Analyze instruction patterns to fingerprint ASPSX assembler version.

    Key differences between ASPSX versions (from maspsx documentation):
    - li $reg, 1: ori vs addiu (threshold at ASPSX 2.56)
    - div expansion: tge vs break (threshold at ASPSX 2.21)
    - NOP before $at expansion (versions <= 2.21)
    """
    text_data = data[text_offset:]

    # MIPS instruction patterns (little-endian)
    # ori $reg, $zero, 1 = 0x3400XX01 where XX is register
    # addiu $reg, $zero, 1 = 0x2400XX01 where XX is register

    ori_imm1_count = 0
    addiu_imm1_count = 0
    tge_count = 0
    break_count = 0

    for i in range(0, len(text_data) - 4, 4):
        insn = struct.unpack_from('<I', text_data, i)[0]

        # Check for ori $rt, $zero, small_imm (opcode 0x0D, rs=0)
        # Format: 001101 00000 TTTTT iiiiiiiiiiiiiiii
        opcode = (insn >> 26) & 0x3F
        rs = (insn >> 21) & 0x1F
        imm = insn & 0xFFFF

        if opcode == 0x0D and rs == 0 and imm == 1:  # ori $rt, $zero, 1
            ori_imm1_count += 1
        elif opcode == 0x09 and rs == 0 and imm == 1:  # addiu $rt, $zero, 1
            addiu_imm1_count += 1

        # Check for tge (opcode=SPECIAL, funct=0x30) vs break (opcode=SPECIAL, funct=0x0D)
        if opcode == 0x00:
            funct = insn & 0x3F
            if funct == 0x30:  # tge
                tge_count += 1
            elif funct == 0x0D:  # break
                # Check if it's the div-by-zero break (code 0x1C00 = 7 << 10)
                code = (insn >> 6) & 0xFFFFF
                if code == 7:  # break 7 = div overflow check
                    break_count += 1

    return {
        'ori_zero_1': ori_imm1_count,
        'addiu_zero_1': addiu_imm1_count,
        'tge_count': tge_count,
        'break_div_count': break_count,
    }


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    exe_path = sys.argv[1]

    # Find signature directory
    ghidra_home = os.environ.get('GHIDRA_HOME', 'C:/tools/ghidra_12.0.2_PUBLIC')
    sig_dir_default = Path(ghidra_home) / 'Ghidra/Extensions/ghidra_psx_ldr/ghidra_psx_ldr/data/psyq'

    sig_dir = None
    for i, arg in enumerate(sys.argv):
        if arg == '--sigs' and i + 1 < len(sys.argv):
            sig_dir = Path(sys.argv[i + 1])
    if sig_dir is None:
        sig_dir = sig_dir_default

    if not sig_dir.exists():
        print(f"ERROR: Signature directory not found: {sig_dir}")
        print(f"Install ghidra_psx_ldr or specify --sigs <path>")
        sys.exit(1)

    # Load binary
    print(f"Loading: {exe_path}")
    with open(exe_path, 'rb') as f:
        data = f.read()

    # Parse PS-X EXE header
    if data[:8] == b'PS-X EXE':
        pc = struct.unpack_from('<I', data, 0x10)[0]
        text_start = struct.unpack_from('<I', data, 0x18)[0]
        text_size = struct.unpack_from('<I', data, 0x1C)[0]
        print(f"PS-X EXE: text=0x{text_start:08X}, size=0x{text_size:X} ({text_size} bytes), PC=0x{pc:08X}")
        text_offset = 0x800
    else:
        print("WARNING: Not a PS-X EXE, scanning from offset 0")
        text_offset = 0

    print()

    # === Primary detection (version stamp) ===
    print("=" * 70)
    print("PRIMARY DETECTION: PSY-Q version stamp scan")
    print("=" * 70)
    stamps = detect_psyq_version_primary(data)
    if stamps:
        print(f"Found {len(stamps)} 'Ps' stamps with 0x45 marker:")
        for offset, param in stamps:
            addr = offset - text_offset + 0x80010000 if offset >= text_offset else offset
            print(f"  0x{offset:06X} (0x{addr:08X}): Ps param={param:02X}")
    else:
        print("No PSY-Q version stamps found via primary method.")
    print()

    # === Signature-based detection ===
    print("=" * 70)
    print("SIGNATURE DETECTION: Library function pattern matching")
    print("=" * 70)
    print(f"Signature database: {sig_dir}")

    versions = load_signatures(sig_dir)
    print(f"Loaded {sum(len(v) for v in versions.values())} signatures across {len(versions)} PSY-Q versions")
    print()

    results = detect_psyq_version_signatures(data, versions, text_offset)

    # Sort by match percentage
    sorted_results = sorted(results.items(), key=lambda x: x[1]['pct'], reverse=True)

    print(f"{'Version':>8s}  {'Matched':>8s}  {'Total':>6s}  {'Match%':>7s}  {'Verdict':s}")
    print("-" * 55)
    best_version = None
    best_pct = 0
    for version, r in sorted_results:
        verdict = ""
        if r['pct'] > best_pct:
            best_pct = r['pct']
            best_version = version
        if r['pct'] == best_pct and r['matched'] > 0:
            verdict = " <== BEST"
        print(f"  {version:>6s}  {r['matched']:>8d}  {r['total']:>6d}  {r['pct']:>6.1f}%{verdict}")

    print()
    if best_version and best_pct > 0:
        # Format version: "460" -> "4.6", "3610" -> "3.6.10"
        v = best_version
        if len(v) == 3:
            formatted = f"{v[0]}.{v[1]}{v[2]}"
        elif len(v) == 4:
            formatted = f"{v[0]}.{v[1]}.{v[2:]}"
        else:
            formatted = v
        print(f"BEST MATCH: PSY-Q {formatted} ({best_pct:.1f}% signature match)")

        # Show matched libraries for best version
        best_matches = results[best_version]['matches']
        libs = defaultdict(list)
        for m in best_matches:
            libs[m['lib']].append(m['name'])
        print(f"\nMatched libraries ({len(best_matches)} functions):")
        for lib, funcs in sorted(libs.items()):
            print(f"  {lib}: {len(funcs)} functions")
            for func in sorted(funcs)[:5]:
                print(f"    - {func}")
            if len(funcs) > 5:
                print(f"    ... and {len(funcs)-5} more")

    print()

    # === ASPSX assembler fingerprinting (also runs deep analyses) ===
    print("=" * 70)
    print("ASPSX ASSEMBLER FINGERPRINTING: Instruction pattern analysis")
    print("=" * 70)
    patterns = detect_aspsx_patterns(data, text_offset)
    sig_results = results  # Save for deep analysis

    print(f"  ori  $rt, $zero, 1  count: {patterns['ori_zero_1']:>5d}  (ASPSX <= 2.34 uses this for 'li 1')")
    print(f"  addiu $rt, $zero, 1 count: {patterns['addiu_zero_1']:>5d}  (ASPSX >= 2.56 uses this for 'li 1')")
    print(f"  tge  instructions:         {patterns['tge_count']:>5d}  (ASPSX 2.05/2.08 div expansion)")
    print(f"  break 7 (div overflow):    {patterns['break_div_count']:>5d}  (ASPSX >= 2.21 div expansion)")
    print()

    ori = patterns['ori_zero_1']
    addiu = patterns['addiu_zero_1']
    if ori > 0 and addiu == 0:
        print("  VERDICT: 'li 1' -> ori exclusively => ASPSX <= 2.34")
    elif ori == 0 and addiu > 0:
        print("  VERDICT: 'li 1' -> addiu exclusively => ASPSX >= 2.56")
    elif ori > addiu * 3:
        print(f"  VERDICT: 'li 1' -> predominantly ori ({ori} vs {addiu}) => likely ASPSX <= 2.34")
        print(f"           (addiu instances may be compiler-generated, not assembler)")
    elif addiu > ori * 3:
        print(f"  VERDICT: 'li 1' -> predominantly addiu ({addiu} vs {ori}) => likely ASPSX >= 2.56")
        print(f"           (ori instances may be compiler-generated, not assembler)")
    else:
        print(f"  VERDICT: Mixed ori/addiu ({ori}/{addiu}) => inconclusive")

    if patterns['tge_count'] > 0 and patterns['break_div_count'] == 0:
        print("  VERDICT: div uses tge => ASPSX 2.05 or 2.08")
    elif patterns['break_div_count'] > 0 and patterns['tge_count'] == 0:
        print("  VERDICT: div uses break => ASPSX >= 2.21")
    elif patterns['tge_count'] > 0 and patterns['break_div_count'] > 0:
        print(f"  VERDICT: Mixed tge/break ({patterns['tge_count']}/{patterns['break_div_count']}) => multiple libraries linked")

    # === Deep analyses ===
    analyze_exclusive_matches(sig_results)
    analyze_tge_context(data, text_offset)


def analyze_exclusive_matches(results: dict[str, dict]) -> None:
    """Find functions that match ONLY in specific versions — these are the most diagnostic."""
    print("=" * 70)
    print("EXCLUSIVE MATCH ANALYSIS: Functions unique to specific versions")
    print("=" * 70)

    # Build map of function_name -> set of versions it matches in
    func_versions: dict[str, set[str]] = defaultdict(set)
    func_info: dict[str, dict] = {}  # Store first match info

    for version, r in results.items():
        for m in r['matches']:
            key = f"{m['lib']}:{m['name']}"
            func_versions[key].add(version)
            if key not in func_info:
                func_info[key] = m

    # Find functions exclusive to one version
    exclusive: dict[str, list[str]] = defaultdict(list)
    for func, vers in func_versions.items():
        if len(vers) == 1:
            exclusive[list(vers)[0]].append(func)

    if exclusive:
        for version in sorted(exclusive.keys()):
            funcs = exclusive[version]
            print(f"  PSY-Q {version}: {len(funcs)} exclusive matches")
            for f in sorted(funcs)[:8]:
                info = func_info[f]
                print(f"    {f} at 0x{info['offset']:06X}")
            if len(funcs) > 8:
                print(f"    ... and {len(funcs)-8} more")
    else:
        print("  No exclusively-matching functions found.")

    # Find functions that match in a contiguous range of versions (version floor)
    print()
    print("VERSION FLOOR ANALYSIS: Functions matching from version X onward")
    all_versions = sorted(results.keys())

    # For each function, find the earliest version it matches
    earliest: dict[str, str] = {}
    latest: dict[str, str] = {}
    for func, vers in func_versions.items():
        sorted_vers = sorted(vers)
        earliest[func] = sorted_vers[0]
        latest[func] = sorted_vers[-1]

    # Count functions by their earliest matching version
    floor_counts: dict[str, int] = defaultdict(int)
    for func, ver in earliest.items():
        floor_counts[ver] += 1

    print(f"  {'First appears in':>20s}  {'Count':>6s}")
    print(f"  {'-'*20}  {'-'*6}")
    for ver in all_versions:
        if ver in floor_counts:
            print(f"  PSY-Q {ver:>14s}  {floor_counts[ver]:>6d}")


def analyze_tge_context(data: bytes, text_offset: int = 0x800) -> None:
    """Examine the context around tge instructions to determine if they're
    compiler-generated (div overflow) or assembler-generated (pseudo-instruction expansion)."""
    print()
    print("=" * 70)
    print("TGE CONTEXT ANALYSIS: Compiler vs assembler origin")
    print("=" * 70)

    text_data = data[text_offset:]
    tge_contexts = []

    for i in range(0, len(text_data) - 4, 4):
        insn = struct.unpack_from('<I', text_data, i)[0]
        opcode = (insn >> 26) & 0x3F
        funct = insn & 0x3F

        if opcode == 0x00 and funct == 0x30:  # tge
            rs = (insn >> 21) & 0x1F
            rt = (insn >> 16) & 0x1F
            # Get surrounding instructions
            prev2 = struct.unpack_from('<I', text_data, i-8)[0] if i >= 8 else 0
            prev1 = struct.unpack_from('<I', text_data, i-4)[0] if i >= 4 else 0
            next1 = struct.unpack_from('<I', text_data, i+4)[0] if i+4 < len(text_data) else 0
            next2 = struct.unpack_from('<I', text_data, i+8)[0] if i+8 < len(text_data) else 0

            # Check if preceded by div/divu
            prev1_op = (prev1 >> 26) & 0x3F
            prev1_fn = prev1 & 0x3F
            prev2_op = (prev2 >> 26) & 0x3F
            prev2_fn = prev2 & 0x3F

            is_div_related = False
            if prev1_op == 0 and prev1_fn in (0x1A, 0x1B):  # div, divu
                is_div_related = True
            if prev2_op == 0 and prev2_fn in (0x1A, 0x1B):
                is_div_related = True

            # Check if next instruction is mflo/mfhi
            next1_op = (next1 >> 26) & 0x3F
            next1_fn = next1 & 0x3F
            has_mf = next1_op == 0 and next1_fn in (0x10, 0x12)  # mfhi, mflo

            addr = i + text_offset - 0x800 + 0x80010000
            tge_contexts.append({
                'addr': addr,
                'offset': i + text_offset,
                'rs': rs, 'rt': rt,
                'div_related': is_div_related,
                'has_mf_after': has_mf,
            })

    div_related = sum(1 for t in tge_contexts if t['div_related'])
    standalone = sum(1 for t in tge_contexts if not t['div_related'])

    print(f"  Total tge instructions: {len(tge_contexts)}")
    print(f"  Near div/divu:          {div_related} (assembler div expansion)")
    print(f"  Standalone:             {standalone} (compiler-generated or other)")
    print()

    # Show first few of each type
    if div_related > 0:
        print("  Examples near div/divu:")
        for t in [c for c in tge_contexts if c['div_related']][:3]:
            print(f"    0x{t['addr']:08X}: tge ${t['rs']}, ${t['rt']}")

    if standalone > 0:
        print("  Examples standalone:")
        for t in [c for c in tge_contexts if not c['div_related']][:3]:
            print(f"    0x{t['addr']:08X}: tge ${t['rs']}, ${t['rt']}")

    print()
    if div_related > standalone * 2:
        print(f"  VERDICT: tge is primarily from div pseudo-instruction expansion")
        print(f"           => ASPSX 2.05 or 2.08 was used for libraries")
    elif standalone > div_related * 2:
        print(f"  VERDICT: tge is primarily compiler-generated (not assembler)")
        print(f"           => Does NOT indicate old ASPSX version")
    else:
        print(f"  VERDICT: Mixed origin — some library code, some compiler code")


if __name__ == '__main__':
    main()
