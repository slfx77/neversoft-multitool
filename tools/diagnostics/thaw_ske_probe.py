# thaw_ske_probe.py — structural probe for THAW .ske skeletons (LE PS2/PC, BE GC).
# Reads payloads directly out of the source paks via the pairing manifest
# (TestOutput/thaw_anim_pairs.csv, built by thaw_anim_pairs.py), so no extraction
# step is needed and PS2<->GC mirror checks are exact.
#
# Header hypothesis under test (0x30 bytes):
#   u16 version=1, u16 headerSize=0x30, u32 numBones, u32 zero, u32 zero,
#   u32 offsets[6], u32 unknownA, u32 unknownB(=0x30?)
# followed by a pose region (0x30..offsets[0], stride TBD) and regions at each
# offset. The probe measures region sizes, classifies content (bone-name QbKeys
# via the shipped dbg dictionaries / small ints / floats), and diff-checks the
# PS2 vs GC bytes under u16/u32 swap hypotheses.
#
# Usage (from repo root):
#   python tools/diagnostics/thaw_ske_probe.py                 # detail-dump first pair
#   python tools/diagnostics/thaw_ske_probe.py --stem bh_11_main --idx 0
#   python tools/diagnostics/thaw_ske_probe.py --batch         # arithmetic sweep, all ske pairs
#   python tools/diagnostics/thaw_ske_probe.py --batch --all-ska-too   # (reserved)

import csv
import math
import struct
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"
MANIFEST = REPO / "TestOutput" / "thaw_anim_pairs.csv"


def load_names() -> dict[int, str]:
    names: dict[int, str] = {}
    for txt in (REPO / "src/NeversoftMultitool/Core/QbKey").glob("QbKeyNames*.txt"):
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    names.setdefault(int(line[eq + 3:], 16), line[:eq])
                except ValueError:
                    pass
    return names


def read_payload(pak_rel: str, pos: int, size: int, src: str = "pak") -> bytes:
    pak_path = BUILDS / pak_rel
    if src == "mpk":
        pak_path = pak_path.with_name(pak_path.name.replace(".apk.", ".mpk."))
    elif src == "companion":
        pak_path = pak_path.with_name(pak_path.name.replace(".pak.", ".pab."))
    with open(pak_path, "rb") as f:
        f.seek(pos)
        return f.read(size)


def ske_pairs():
    with open(MANIFEST, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            if row["type"] == "ske":
                yield row


class Ske:
    def __init__(self, data: bytes, big: bool):
        self.data = data
        self.big = big
        fmt = ">" if big else "<"
        self.ver, self.hdr_size = struct.unpack_from(fmt + "HH", data, 0)
        self.num_bones = struct.unpack_from(fmt + "I", data, 4)[0]
        self.zero = struct.unpack_from(fmt + "II", data, 8)
        self.offsets = list(struct.unpack_from(fmt + "6I", data, 0x10))
        self.unk_a, self.unk_b = struct.unpack_from(fmt + "II", data, 0x28)

    def regions(self):
        """(label, start, end) for pose region + each offset region."""
        bounds = sorted(set(self.offsets + [len(self.data)]))
        out = [("pose", self.hdr_size, min(self.offsets))]
        for i, off in enumerate(self.offsets):
            nxt = min(b for b in bounds if b > off)
            out.append((f"arr{i}@0x{off:X}", off, nxt))
        return out


def classify(values: list[int], names: dict[int, str]) -> str:
    if not values:
        return "empty"
    named = sum(1 for v in values if v in names)
    small = sum(1 for v in values if v < 0x10000)
    floats = 0
    for v in values:
        f = struct.unpack("<f", struct.pack("<I", v))[0]
        if v != 0 and math.isfinite(f) and 1e-6 < abs(f) < 1e6:
            floats += 1
    n = len(values)
    return f"named={named}/{n} small={small}/{n} floatish={floats}/{n}"


def detail(row, names):
    ps2 = read_payload(row["ps2_pak"], int(row["ps2_pos"]), int(row["ps2_size"]), row.get("ps2_src", "pak"))
    gc = read_payload(row["gc_pak"], int(row["gc_pos"]), int(row["gc_size"]), row["gc_src"])
    print(f"pair: {row['stem']} idx={row['idx']} name={row['name'] or row['gc_key']} "
          f"size={len(ps2)}/{len(gc)}")

    for label, data, big in (("PS2", ps2, False), ("GC ", gc, True)):
        s = Ske(data, big)
        print(f"\n[{label}] ver={s.ver} hdrSize=0x{s.hdr_size:X} bones={s.num_bones} "
              f"zero={s.zero} unkA=0x{s.unk_a:X} unkB=0x{s.unk_b:X}")
        pose_start, pose_end = s.hdr_size, min(s.offsets)
        pose_len = pose_end - pose_start
        stride = pose_len / s.num_bones if s.num_bones else 0
        print(f"    pose region 0x{pose_start:X}..0x{pose_end:X} len=0x{pose_len:X} "
              f"stride={stride:.2f} B/bone")
        fmt = ">" if big else "<"
        for lab, start, end in s.regions()[1:]:
            count = (end - start) // 4
            vals = list(struct.unpack_from(f"{fmt}{count}I", data, start))
            per_bone = (end - start) / s.num_bones if s.num_bones else 0
            print(f"    {lab:14s} len=0x{end - start:X} ({per_bone:.2f} B/bone)  {classify(vals, names)}")
            shown = ", ".join(
                names.get(v, f"{v:08X}") for v in vals[:6])
            print(f"        head: {shown}")

    # pose hypothesis: vec4[N] @0x30 (local translations, w=1), then unkA points
    # at mat4[N] (precomputed neutral/inverse-bind matrices).
    s0 = Ske(ps2, False)
    n = s0.num_bones
    arr0 = struct.unpack_from(f"<{n}I", ps2, s0.offsets[0])
    vec_end = s0.hdr_size + n * 16
    print(f"\n[pose] vecA @0x30..0x{vec_end:X} (N*16={n * 16}); unkA=0x{s0.unk_a:X} "
          f"({'== vecA end' if s0.unk_a == vec_end else 'MISMATCH'}); "
          f"matB @0x{s0.unk_a:X}..0x{min(s0.offsets):X} "
          f"({(min(s0.offsets) - s0.unk_a) / n:.1f} B/bone)")
    print("  vecA first 6 bones:")
    for b in range(min(6, n)):
        v = struct.unpack_from("<4f", ps2, s0.hdr_size + b * 16)
        bone = names.get(arr0[b], f"{arr0[b]:08X}")
        print(f"    [{b:2d}] ({v[0]:>10.4f} {v[1]:>10.4f} {v[2]:>10.4f} {v[3]:>6.3f})  {bone}")
    print("  matB first 3 bones (4x4 rows):")
    for b in range(min(3, n)):
        base = s0.unk_a + b * 64
        bone = names.get(arr0[b], f"{arr0[b]:08X}")
        print(f"    [{b}] {bone}:")
        for r in range(4):
            row = struct.unpack_from("<4f", ps2, base + r * 16)
            print(f"        ({row[0]:>10.5f} {row[1]:>10.5f} {row[2]:>10.5f} {row[3]:>10.5f})")
        m = [struct.unpack_from("<4f", ps2, base + r * 16) for r in range(4)]
        r0n = math.sqrt(sum(m[0][c] ** 2 for c in range(3)))
        r1n = math.sqrt(sum(m[1][c] ** 2 for c in range(3)))
        r2n = math.sqrt(sum(m[2][c] ** 2 for c in range(3)))
        print(f"        |r0|={r0n:.4f} |r1|={r1n:.4f} |r2|={r2n:.4f} "
              f"lastcol=({m[0][3]:.3g},{m[1][3]:.3g},{m[2][3]:.3g},{m[3][3]:.3g})")

    # mirror check: u32-swap and u16-swap agreement per region
    s = Ske(ps2, False)
    print("\n[mirror] per-region byte agreement GC vs PS2 under swap hypotheses:")
    for lab, start, end in [("header", 0, s.hdr_size)] + s.regions():
        seg_p, seg_g = ps2[start:end], gc[start:end]
        n4 = len(seg_p) // 4 * 4
        sw32 = sum(1 for i in range(0, n4, 4) if seg_p[i:i + 4] == seg_g[i:i + 4][::-1])
        sw16 = sum(1 for i in range(0, n4, 2) if seg_p[i:i + 2] == seg_g[i:i + 2][::-1])
        raw = sum(1 for i in range(len(seg_p)) if seg_p[i] == seg_g[i])
        print(f"    {lab:14s} u32swap {sw32}/{n4 // 4}  u16swap {sw16 // 2}/{n4 // 4}  "
              f"rawbytes {raw}/{len(seg_p)}")


def batch(names):
    stats = Counter()
    stride_hist = Counter()
    unk_a_rel = Counter()
    arr5_per_bone = Counter()
    fails = []
    for row in ske_pairs():
        for which, big in (("ps2", False), ("gc", True)):
            if which == "ps2":
                data = read_payload(row["ps2_pak"], int(row["ps2_pos"]), int(row["ps2_size"]), row.get("ps2_src", "pak"))
            else:
                data = read_payload(row["gc_pak"], int(row["gc_pos"]), int(row["gc_size"]), row["gc_src"])
            try:
                s = Ske(data, big)
                n = s.num_bones
                fmt = ">" if big else "<"
                stats["files"] += 1
                stats["hdr_ok"] += s.ver == 1 and s.hdr_size == 0x30 and s.zero == (0, 0)
                # structural model: hdr(0x30) + vec4[N] + mat4[N] + 5*u32[N] arrays + tail
                stats["unkA_is_vec_end"] += s.unk_a == 0x30 + n * 16
                stats["matB_extent_ok"] += s.offsets[0] == s.unk_a + n * 64
                arr_gaps = [b - a for a, b in zip(s.offsets, s.offsets[1:])]
                stats["gaps_4perbone"] += {g / n for g in arr_gaps} == {4.0}
                stats["unkB_0x30"] += s.unk_b == 0x30
                stats["in_bounds"] += all(o < len(data) for o in s.offsets)
                tail_len = len(data) - (s.offsets[5] + n * 4)
                stride_hist[tail_len] += 1  # bytes after the 6th per-bone array
                # vecA w==1 and matB last column (0,0,0,1)
                w_ok = all(
                    abs(struct.unpack_from(fmt + "f", data, 0x30 + b * 16 + 12)[0] - 1.0) < 1e-4
                    for b in range(n))
                stats["vecA_w1"] += w_ok
                col_ok = True
                for b in range(0, n, max(1, n // 8)):
                    base = s.unk_a + b * 64
                    m = [struct.unpack_from(fmt + "4f", data, base + r * 16) for r in range(4)]
                    if any(abs(m[r][3]) > 1e-4 for r in range(3)) or abs(m[3][3] - 1) > 1e-4:
                        col_ok = False
                        break
                stats["matB_col_0001"] += col_ok
                # arr1 parent keys must be 0 (root) or appear in arr0
                names_arr = struct.unpack_from(f"{fmt}{n}I", data, s.offsets[0])
                parents = struct.unpack_from(f"{fmt}{n}I", data, s.offsets[1])
                name_set = set(names_arr)
                stats["parents_resolve"] += all(p == 0 or p in name_set for p in parents)
                # root-first ordering (parents precede children)?
                order_ok = True
                seen = set()
                for b in range(n):
                    if parents[b] != 0 and parents[b] not in seen:
                        order_ok = False
                        break
                    seen.add(names_arr[b])
                stats["parents_precede"] += order_ok
                unk_a_rel[round(s.unk_a / n, 3)] += 1
            except Exception as ex:
                fails.append((row["stem"], which, str(ex)))
    print(f"batch over {stats['files']} files (both platforms of every pair):")
    for k in ("hdr_ok", "unkA_is_vec_end", "matB_extent_ok", "gaps_4perbone", "unkB_0x30",
              "in_bounds", "vecA_w1", "matB_col_0001", "parents_resolve", "parents_precede"):
        print(f"  {k}: {stats[k]}/{stats['files']}")
    print(f"  tail bytes after arr5 histogram: {dict(sorted(stride_hist.items(), key=lambda kv: -kv[1])[:8])}")
    if fails:
        print(f"  FAILURES ({len(fails)}):")
        for stem, which, msg in fails[:10]:
            print(f"    {stem} [{which}]: {msg}")


def main() -> None:
    names = load_names()
    args = sys.argv[1:]
    if "--batch" in args:
        batch(names)
        return
    stem = args[args.index("--stem") + 1] if "--stem" in args else None
    idx = args[args.index("--idx") + 1] if "--idx" in args else "0"
    for row in ske_pairs():
        if stem is None or (row["stem"] == stem and row["idx"] == idx):
            detail(row, names)
            return
    print("pair not found")


if __name__ == "__main__":
    main()
