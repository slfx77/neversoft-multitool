#!/usr/bin/env python3
"""Which object bank each THPS _2 / _h region gets, filename rule vs TRG.

The two-player and H-O-R-S-E regions of a THPS level share the base level's
<base>_t.trg. The bank they actually run with comes from that TRG's BOOT script
(0x8E SetObjFile), and AUTOEXEC2 (node type 15) REPLACES AUTOEXEC (type 4) when
two players are active -- HORSE included, since GGame == 7 launches with
GNumberOfPlayers == 2. A boot script with no 0x8E means the region genuinely has
no bank.

This reports, per variant, what the retired filename rule picked versus what the
TRG names, so a behaviour change can be read off directly instead of inferred
from triangle counts.

Usage:
    python psx_variant_bank_report.py [--builds Sample/Builds] [--cli <path>]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

AUTOEXEC = 4
AUTOEXEC2 = 15
SET_OBJ_FILE = 0x8E


def trg_json(cli: Path, trg: Path, outdir: Path):
    """Run the shipped TRG parser and read its JSON (no reimplementation here)."""
    subprocess.run([str(cli), 'trg', str(trg), '-o', str(outdir)],
                   capture_output=True, check=False)
    produced = outdir / (trg.stem + '.json')
    if not produced.exists():
        return None
    return json.loads(produced.read_text(encoding='utf-8'))


def boot_bank(doc, two_player: bool):
    """Trig_InitialParseTRGFile's selection, then the last SetObjFile it runs.

    Returns (found_boot_script, bank_name). bank_name '' with found=True is the
    faithful "this region has no bank" answer.
    """
    nodes = doc.get('nodes', [])
    chosen = []
    if two_player:
        chosen = [n for n in nodes if n.get('typeId') == AUTOEXEC2]
    if not chosen:
        chosen = [n for n in nodes if n.get('typeId') == AUTOEXEC]
    if not chosen:
        return False, ''

    bank = ''
    for node in chosen:
        for command in (node.get('commands') or []):
            if command.get('opcode') != SET_OBJ_FILE:
                continue
            args = command.get('args') or []
            if args and isinstance(args[0], str) and args[0]:
                bank = args[0]
    return True, bank


def filename_rule(directory: Path, base: str, two_player: bool):
    """The retired guess, kept only as a fallback in the shipped resolver."""
    candidates = ([base + '_o2.psx', base + 'o2.psx', base + '_o.psx']
                  if two_player else [base + '_o.psx'])
    existing = {p.name.lower(): p.name for p in directory.iterdir() if p.is_file()}
    for candidate in candidates:
        if candidate.lower() in existing:
            return existing[candidate.lower()]
    return ''


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--builds', default='Sample/Builds')
    parser.add_argument('--cli',
                        default='src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.exe')
    args = parser.parse_args()

    cli = Path(args.cli)
    if not cli.exists():
        print(f'CLI not found: {cli}', file=sys.stderr)
        return 2

    rows = []
    with tempfile.TemporaryDirectory() as tmp:
        outdir = Path(tmp)
        for build in sorted(Path(args.builds).iterdir()):
            if not build.is_dir():
                continue
            for directory in sorted({p.parent for p in build.rglob('*_t.trg')}):
                for variant in sorted(directory.glob('*.psx')):
                    stem = variant.stem
                    suffix = stem[-2:].lower()
                    if suffix not in ('_2', '_h'):
                        continue
                    base = stem[:-2]
                    trg = directory / (base + '_t.trg')
                    if not trg.exists():
                        continue
                    # Apocalypse geometry chunks (city_2, grav_2 …) are spelled
                    # like two-player variants but are not; their shared bank
                    # attaches to one primary elsewhere. The shipped resolver
                    # excludes them by the same test, so the report must too or
                    # it reports a change the tool does not make.
                    if any((directory / (base + suffix)).exists()
                           for suffix in ('_obj.psx', 'obj.psx')):
                        continue

                    doc = trg_json(cli, trg, outdir)
                    if doc is None:
                        continue
                    # Both _2 and _h run with GNumberOfPlayers == 2.
                    found, bank = boot_bank(doc, two_player=True)
                    old = filename_rule(directory, base, suffix == '_2')
                    new = (bank + '.psx') if bank else ('' if found else old)
                    rows.append((build.name, stem, old, new, found))

    print(f"{'build':46} {'variant':12} {'filename rule':16} {'TRG says':16} verdict")
    changed = 0
    for build, stem, old, new, found in rows:
        if old.lower() == new.lower():
            verdict = 'same'
        elif not new:
            verdict = 'NO BANK (was ' + (old or 'none') + ')'
            changed += 1
        else:
            verdict = 'RETARGETED'
            changed += 1
        note = '' if found else '  [no boot script - fallback]'
        print(f'{build[:45]:46} {stem:12} {old or "-":16} {new or "-":16} {verdict}{note}')

    print(f'\n{changed} of {len(rows)} variants change behaviour')
    return 0


if __name__ == '__main__':
    sys.exit(main())
