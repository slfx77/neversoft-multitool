#!/usr/bin/env python3
"""ABR blend-rate survey over psx-mesh-dump JSON files.

PS1 GPU semi-transparent faces (render flag bit6, 0x40) carry a 2-bit ABR blend
rate in flags bits 7-8 (face_flag_semantics.md §3b/4c, byte-verified against
M3dAsm_ProcessPolys @0x80099C5C):

    rate 0: 0.5*Back + 0.5*Front   (average)
    rate 1: 1.0*Back + 1.0*Front   (additive)
    rate 2: 1.0*Back - 1.0*Front   (subtractive)
    rate 3: 1.0*Back + 0.25*Front  (quarter additive)

This script tallies the semi-transparent face population per rate so the
converter's per-rate texture treatment can be sized against real data, and
lists the texture hashes behind each rate for render spot-checks.

Usage:
  1. Generate dumps:  NeversoftMultitool psx-mesh-dump <file.psx> --json out.json
  2. python tools/diagnostics/psx_abr_survey.py <dump-dir-or-json> [more...]
"""
import json
import os
import sys
from collections import Counter, defaultdict


def survey_file(path, agg):
    d = json.load(open(path, encoding='utf-8'))
    meshes = d.get('Meshes', [])
    total = semi = untextured_semi = 0
    rates = Counter()
    textures_by_rate = defaultdict(set)
    for mesh in meshes:
        for face in mesh.get('Faces', []):
            total += 1
            flags = face['Flags']
            if not flags & 0x40:
                continue
            semi += 1
            rate = (flags & 0x180) >> 7
            rates[rate] += 1
            if face.get('IsTextured'):
                textures_by_rate[rate].add(face.get('TextureHash', 0))
            else:
                untextured_semi += 1
    name = os.path.basename(path)
    rate_str = " ".join(f"r{r}={rates.get(r, 0)}" for r in range(4))
    print(f"{name:24} faces={total:6} semiTrans={semi:5}  {rate_str}  untexturedSemi={untextured_semi}")
    for r in range(4):
        if textures_by_rate[r]:
            sample = ", ".join(f"0x{h:08X}" for h in sorted(textures_by_rate[r])[:6])
            print(f"    rate {r} textures ({len(textures_by_rate[r])}): {sample}")
    agg['total'] += total
    agg['semi'] += semi
    agg['untextured'] += untextured_semi
    agg['rates'].update(rates)


def main():
    args = sys.argv[1:] or ['TestOutput/abr_survey']
    paths = []
    for arg in args:
        if os.path.isdir(arg):
            paths += [os.path.join(arg, f) for f in sorted(os.listdir(arg)) if f.endswith('.json')]
        else:
            paths.append(arg)

    agg = {'total': 0, 'semi': 0, 'untextured': 0, 'rates': Counter()}
    for path in paths:
        survey_file(path, agg)

    print()
    print(f"TOTAL: faces={agg['total']} semiTrans={agg['semi']} "
          f"({100.0 * agg['semi'] / max(agg['total'], 1):.1f}%) "
          f"untexturedSemi={agg['untextured']}")
    for r in range(4):
        n = agg['rates'].get(r, 0)
        pct = 100.0 * n / max(agg['semi'], 1)
        print(f"  ABR rate {r}: {n:6}  ({pct:.1f}% of semi-transparent)")


if __name__ == '__main__':
    main()
