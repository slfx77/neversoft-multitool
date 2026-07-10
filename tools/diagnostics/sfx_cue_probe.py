#!/usr/bin/env python3
"""SFX cue-table probe: parse .SFX cue records and resolve them against companion VABs.

Validates the decomp-verified THPS2 PSX cue contract (SFX_ParseSFXFile / playSFX,
thps2-psx-proto docs/sfx_cue_indirection.md) against sample data, mirroring
SfxExtractor.CueResolver.cs:

  - .SFX = flat 16-byte records from offset 0, terminated by a 0xFFFFFFFF word:
    marker(u8, 0xFE = loop), program(u8), category(u8, tone bit 1<<cat), note(u8),
    pitch(u16), volume(u16), alias(u16), 6 zero pad bytes.
  - VAB resolution: toneAttr = vab + program*0x200 + 0x820 + category*0x20,
    VAG index = s16 at toneAttr+0x16 (1-based).

Usage:
  python tools/diagnostics/sfx_cue_probe.py                    # sweep Sample/Builds
  python tools/diagnostics/sfx_cue_probe.py path/to/file.sfx   # dump one bank's cues
"""
import os
import struct
import sys


def parse_cues(data: bytes):
    """Returns (cues, error). Stops at the 0xFFFFFFFF terminator or a zeroed record."""
    cues = []
    off = 0
    while off + 16 <= len(data):
        word0 = struct.unpack_from('<I', data, off)[0]
        if word0 == 0xFFFFFFFF:
            break
        rec = data[off:off + 16]
        if rec == b'\x00' * 16:
            break
        if rec[10:16] != b'\x00' * 6:
            return [], f"nonzero pad at record {len(cues)} (not a cue table)"
        pitch, volume = struct.unpack_from('<HH', rec, 4)
        alias = struct.unpack_from('<H', rec, 8)[0]
        cues.append(dict(index=len(cues), loop=rec[0] == 0xFE, program=rec[1],
                         category=rec[2], note=rec[3], pitch=pitch,
                         volume=volume, alias=alias))
        off += 16
    return cues, None


def vab_resolve(vab: bytes, program: int, category: int):
    """Returns (status, vag_index, center, shift) per the playSFX tone walk."""
    if len(vab) < 0x820 or vab[:4] != b'pBAV':
        return 'notvab', None, None, None
    prog_count = struct.unpack_from('<H', vab, 0x12)[0]
    vag_count = struct.unpack_from('<H', vab, 0x16)[0]
    program &= 0x7F
    tone = 0x820 + program * 0x200 + category * 0x20
    if category > 15 or program >= prog_count or tone + 0x20 > len(vab):
        return 'oob', None, None, None
    wave = struct.unpack_from('<h', vab, tone + 0x16)[0]
    if not 1 <= wave <= vag_count:
        return 'badvag', wave, None, None
    return 'ok', wave, vab[tone + 4], vab[tone + 5]


def dump_bank(path: str):
    data = open(path, 'rb').read()
    cues, err = parse_cues(data)
    if err:
        print(f"parse error: {err}")
        return
    stem = os.path.splitext(path)[0]
    vab_path = next((stem + e for e in ('.vab', '.VAB') if os.path.exists(stem + e)), None)
    vab = open(vab_path, 'rb').read() if vab_path else None
    print(f"{path}: {len(cues)} cues, vab={'yes' if vab else 'no'}")
    for c in cues:
        line = (f"  cue {c['index']:3} alias=0x{c['alias']:04X} prog={c['program']:3} "
                f"cat={c['category']:2} note={c['note']:3} vol={c['volume']:5} "
                f"pitch={c['pitch']:5}{' LOOP' if c['loop'] else ''}")
        if vab:
            status, wave, center, shift = vab_resolve(vab, c['program'], c['category'])
            line += f" -> {status}" + (f" vag={wave} center={center} shift={shift}" if status == 'ok' else '')
        print(line)


def sweep(root: str = "Sample/Builds"):
    stats = dict(files=0, cues=0, ok=0, oob=0, badvag=0, parse_err=0, no_vab=0)
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            if not f.lower().endswith('.sfx'):
                continue
            path = os.path.join(dirpath, f)
            cues, err = parse_cues(open(path, 'rb').read())
            if err:
                stats['parse_err'] += 1
                print(f"PARSE ERROR {path}: {err}")
                continue
            stats['files'] += 1
            stats['cues'] += len(cues)
            stem = os.path.splitext(path)[0]
            vab_path = next((stem + e for e in ('.vab', '.VAB') if os.path.exists(stem + e)), None)
            if vab_path is None:
                stats['no_vab'] += 1
                continue
            vab = open(vab_path, 'rb').read()
            for c in cues:
                status = vab_resolve(vab, c['program'], c['category'])[0]
                stats['ok' if status == 'ok' else ('oob' if status == 'oob' else 'badvag')] += 1
    print(f"files={stats['files']} (no VAB companion: {stats['no_vab']}, parse errors: {stats['parse_err']})")
    print(f"cues={stats['cues']}: resolved={stats['ok']} toneOOB={stats['oob']} badVagIdx={stats['badvag']}")


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1].lower().endswith('.sfx'):
        dump_bank(sys.argv[1])
    else:
        sweep(sys.argv[1] if len(sys.argv) > 1 else "Sample/Builds")
