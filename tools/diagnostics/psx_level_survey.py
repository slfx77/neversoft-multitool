#!/usr/bin/env python3
"""Aggregate psx-mesh-dump JSONs into a per-file level diagnosis table.

For each dump: version, hierarchy, divisors, counts, world-extent (vertex
scatter detector), face acceptance rate, rejection-reason histogram, and the
disc-side face-flag bit6/bit7 population (draw-enable XOR sanity — characters
ship both clear; a file shipping bit7 SET inverts under the XOR rule and its
faces are dropped as invisible).

Usage:
  python tools/diagnostics/psx_level_survey.py TestOutput/thps1_proto_levels/dumps
"""

from __future__ import annotations

import json
import sys
from collections import Counter
from pathlib import Path


def survey(path: Path):
    d = json.loads(path.read_text())
    meshes = d["Meshes"]
    verts = 0
    ext = [1e30, 1e30, 1e30, -1e30, -1e30, -1e30]
    raw_faces = 0
    accepted = 0
    textured = 0
    reasons = Counter()
    flags67 = Counter()  # (bit6, bit7) of raw disc flags
    for m in meshes:
        for v in m["Vertices"]:
            w = v["WorldPosition"]
            verts += 1
            for i, axis in enumerate(("X", "Y", "Z")):
                ext[i] = min(ext[i], w[axis])
                ext[i + 3] = max(ext[i + 3], w[axis])
        for fr in m.get("FaceReads", []):
            raw_faces += 1
            f = fr["Flags"]
            flags67[(bool(f & 0x40), bool(f & 0x80))] += 1
            if fr["IsAccepted"]:
                accepted += 1
            else:
                reasons[fr["RejectionReason"] or "?"] += 1
        for f in m.get("Faces", []):
            if f["IsTextured"] and f["TextureHash"]:
                textured += 1

    span = max(ext[3] - ext[0], ext[4] - ext[1], ext[5] - ext[2]) if verts else 0
    fl = " ".join(
        f"b6={'1' if k[0] else '0'}b7={'1' if k[1] else '0'}:{c}"
        for k, c in sorted(flags67.items(), key=lambda kv: -kv[1]))
    rej = "; ".join(f"{r}:{c}" for r, c in reasons.most_common(3))
    acc_pct = 100.0 * accepted / raw_faces if raw_faces else 0.0
    tex_pct = 100.0 * textured / accepted if accepted else 0.0
    print(f"{path.stem:10s} v{d['Version']} hier={int(d['HasHierarchy'])} "
          f"div={d['ScaleDivisor']:<5.4g} objs={len(d['Objects']):3d} "
          f"meshes={len(meshes):3d} verts={verts:6d} span={span:9.1f} "
          f"faces={accepted:5d}/{raw_faces:5d} ({acc_pct:5.1f}%) "
          f"tex={tex_pct:5.1f}%  [{fl}]"
          + (f"  REJ: {rej}" if rej else ""))


def main():
    root = Path(sys.argv[1])
    for p in sorted(root.glob("*.json")):
        try:
            survey(p)
        except Exception as e:  # noqa: BLE001 - survey robustness over precision
            print(f"{p.stem:10s} ERROR: {e}")


if __name__ == "__main__":
    main()
