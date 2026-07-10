# thaw_ska_probe.py — structural probe for THAW SKA v0x28 animations (LE PS2/PC,
# BE GC). Uses the pairing manifest (TestOutput/thaw_anim_pairs.csv) to read
# payloads straight from the paks and, crucially, to MIRROR-MAP paired files:
# for every 4-byte cell we test whether GC bytes equal PS2 bytes u32-swapped,
# u16-swapped, or raw, which reveals scalar widths across the whole container
# without knowing the layout (u32/f32 fields -> u32swap, u16 fields -> u16swap,
# byte streams -> raw).
#
# Known so far: u32 version=0x28 + u32 flags + f32 duration + four u16s @0x0C +
# 20 bytes 0xFF @0x14 + two u32s @0x28 + u16 stream + key data.
#
# Usage (from repo root):
#   python tools/diagnostics/thaw_ska_probe.py --detail [--stem X --idx N]
#   python tools/diagnostics/thaw_ska_probe.py --mirror [--stem X --idx N]
#   python tools/diagnostics/thaw_ska_probe.py --batch    # arithmetic sweep

import csv
import struct
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from thaw_ske_probe import read_payload  # noqa: E402

MANIFEST = REPO / "TestOutput" / "thaw_anim_pairs.csv"


def ska_pairs(size_match_only=True):
    with open(MANIFEST, newline="", encoding="utf-8") as f:
        for row in csv.DictReader(f):
            if row["type"] != "ska":
                continue
            if size_match_only and row["size_match"] != "1":
                continue
            yield row


def load_pair(row):
    ps2 = read_payload(row["ps2_pak"], int(row["ps2_pos"]), int(row["ps2_size"]), row.get("ps2_src", "pak"))
    gc = read_payload(row["gc_pak"], int(row["gc_pos"]), int(row["gc_size"]), row["gc_src"])
    return ps2, gc


def header(data: bytes, big: bool):
    fmt = ">" if big else "<"
    ver, flags = struct.unpack_from(fmt + "II", data, 0)
    duration = struct.unpack_from(fmt + "f", data, 8)[0]
    h = struct.unpack_from(fmt + "4H", data, 0x0C)
    u28, u2c = struct.unpack_from(fmt + "II", data, 0x28)
    return ver, flags, duration, h, u28, u2c


def mirror_map(ps2: bytes, gc: bytes):
    """Per-4-byte-cell classification -> run-length encoded region list."""
    n = min(len(ps2), len(gc)) // 4 * 4
    cells = []
    for i in range(0, n, 4):
        p, g = ps2[i:i + 4], gc[i:i + 4]
        if p == g[::-1]:
            # ambiguous cases: palindromic cells match everything
            cls = "32"
        elif p[0:2] == g[1::-1] and p[2:4] == g[3:1:-1]:
            cls = "16"
        elif p == g:
            cls = "raw"
        else:
            cls = "??"
        cells.append(cls)
    # tidy: cells equal under multiple readings (zeros, 0xFF) — merge into
    # neighbours by preferring the previous class when the cell is symmetric
    for i, c in enumerate(cells):
        seg_p = ps2[i * 4:i * 4 + 4]
        if len(set(seg_p)) == 1 and i > 0:  # all same byte: keep run going
            cells[i] = cells[i - 1]
    runs = []
    for i, c in enumerate(cells):
        if runs and runs[-1][0] == c:
            runs[-1][2] = (i + 1) * 4
        else:
            runs.append([c, i * 4, (i + 1) * 4])
    return runs


def detail(row):
    ps2, gc = load_pair(row)
    print(f"pair: {row['stem']} idx={row['idx']} name={row['name'] or row['gc_key']} size={len(ps2)}")
    for label, data, big in (("PS2", ps2, False), ("GC ", gc, True)):
        ver, flags, duration, h, u28, u2c = header(data, big)
        print(f"  [{label}] ver=0x{ver:X} flags=0x{flags:08X} dur={duration:.4f} "
              f"h16={[hex(x) for x in h]} @0x28=0x{u28:X} @0x2C=0x{u2c:X}")
    # u16 stream after 0x30: dump first 24 values both platforms
    for label, data, big in (("PS2", ps2, False), ("GC ", gc, True)):
        fmt = ">" if big else "<"
        vals = struct.unpack_from(fmt + "24H", data, 0x30)
        print(f"  [{label}] u16s@0x30: {[hex(v) for v in vals]}")


def mirror(row):
    ps2, gc = load_pair(row)
    detail(row)
    print("\n  mirror map (class, start, end, len):")
    for cls, start, end in mirror_map(ps2, gc):
        print(f"    {cls:3s} 0x{start:06X}..0x{end:06X}  ({end - start})")


def batch():
    # Layout hypothesis under test:
    #   0x00 u32 ver=0x28, 0x04 u32 flags, 0x08 f32 duration
    #   0x0C u8 zero?, u8 numBones, 0x0E u16 numQKeys, 0x10 u16 numTKeys,
    #   0x12 u16 numCustomKeys, 0x14 u8[20] bone mask (0xFF = full anim)
    #   0x28 u32 qBytes, 0x2C u32 tBytes
    #   0x30 u16 qSize[numBones], u16 tSize[numBones]
    #   Q blob (qBytes), T blob (tBytes), [custom keys?], pad
    stats = Counter()
    residue_hist = Counter()
    residue_custom = Counter()
    flags_hist = Counter()
    byte0c_hist = Counter()
    mask_partial = 0
    examples = {}
    for row in ska_pairs():
        ps2, gc = load_pair(row)
        ver, flags, duration, h, qbytes, tbytes = header(ps2, False)
        stats["files"] += 1
        stats["ver_0x28"] += ver == 0x28
        flags_hist[f"0x{flags:08X}"] += 1
        byte0c_hist[ps2[0x0C]] += 1
        n, nq, nt, ncustom = ps2[0x0D], h[1], h[2], h[3]
        mask = ps2[0x14:0x28]
        if mask != b"\xff" * 20:
            mask_partial += 1
        qsizes = struct.unpack_from(f"<{n}H", ps2, 0x30)
        tsizes = struct.unpack_from(f"<{n}H", ps2, 0x30 + 2 * n)
        stats["q_sum_ok"] += sum(qsizes) == qbytes
        stats["t_sum_ok"] += sum(tsizes) == tbytes
        blob_start = 0x30 + 4 * n
        end = blob_start + qbytes + tbytes
        residue = len(ps2) - end
        residue_hist[residue] += 1
        if ncustom:
            residue_custom[(ncustom, residue)] += 1
            if residue not in (0, 1, 2, 3) and len(examples) < 4:
                examples[(row["stem"], row["idx"])] = (n, nq, nt, ncustom, residue)
        # blobs byte-identical across platforms?
        stats["blobs_raw_equal"] += ps2[blob_start:end] == gc[blob_start:end]
        stats["dur_mirror"] += struct.unpack_from(">f", gc, 8)[0] == duration
    print(f"batch over {stats['files']} size-matched pairs:")
    for k in ("ver_0x28", "q_sum_ok", "t_sum_ok", "blobs_raw_equal", "dur_mirror"):
        print(f"  {k}: {stats[k]}/{stats['files']}")
    print(f"  byte@0x0C histogram: {dict(byte0c_hist.most_common(6))}")
    print(f"  partial bone masks: {mask_partial}")
    print(f"  size residue after blobs: {dict(sorted(residue_hist.items(), key=lambda kv: -kv[1])[:10])}")
    print(f"  (ncustom, residue) for custom-key files: {dict(sorted(residue_custom.items(), key=lambda kv: -kv[1])[:10])}")
    print(f"  flags histogram: {dict(flags_hist.most_common(10))}")
    for k, v in examples.items():
        print(f"  example custom-key file {k}: n/nq/nt/ncustom/residue = {v}")


def decode_q(blob: bytes, duration: float, compact=False, hires=False):
    """THAW Q key grammar. Base = THUG compressed grammar (u16 header with
    11/14-bit timestamp + width bits). THAW variants:
      hires  (flags bit8):  u16 timestamp prefixed before each u16 header
                            (header ts bits unused); payload widths normal.
      compact(flags bit15): payload is 3 single bytes regardless of width
                            bits (lookup stays 1 byte).
    Returns (keys, lookups) or raises on invariant violation."""
    off, keys, lookups = 0, [], 0
    limit = duration * 60 + 1.5
    while off < len(blob):
        need = 4 if hires else 2
        if off + need > len(blob):
            raise ValueError("Q header overrun")
        if hires:
            ts = blob[off] | (blob[off + 1] << 8)
            off += 2
        header = blob[off] | (blob[off + 1] << 8)
        off += 2
        if header & 0x4000:
            if not hires:
                ts = header & 0x07FF
            if (header & 0x3800) == 0:
                off += 1
                lookups += 1
            elif compact:
                off += 3
            else:
                off += 1 if header & 0x2000 else 2
                off += 1 if header & 0x1000 else 2
                off += 1 if header & 0x0800 else 2
        else:
            if not hires:
                ts = header & 0x3FFF
            off += 3 if compact else 6
        if off > len(blob):
            raise ValueError(f"Q overrun at key {len(keys)}")
        if ts > limit:
            raise ValueError(f"Q timestamp {ts} > {limit}")
        if keys and ts < keys[-1]:
            raise ValueError(f"Q timestamps not increasing: {keys[-1]} -> {ts}")
        keys.append(ts)
    if off != len(blob):
        raise ValueError(f"Q consumed {off} != {len(blob)}")
    return keys, lookups


def decode_t(blob: bytes, duration: float):
    off, keys, lookups = 0, [], 0
    limit = duration * 60 + 1.5
    while off < len(blob):
        flag = blob[off]
        off += 1
        if flag & 0x40:
            ts = flag & 0x3F
        else:
            if off + 2 > len(blob):
                raise ValueError("T timestamp overrun")
            ts = blob[off] | (blob[off + 1] << 8)
            off += 2
        if flag & 0x80:
            off += 1
            lookups += 1
        else:
            off += 6
        if off > len(blob):
            raise ValueError(f"T overrun at key {len(keys)}")
        if ts > limit:
            raise ValueError(f"T timestamp {ts} > {limit}")
        if keys and ts < keys[-1]:
            raise ValueError(f"T timestamps not increasing: {keys[-1]} -> {ts}")
        keys.append(ts)
    if off != len(blob):
        raise ValueError(f"T consumed {off} != {len(blob)}")
    return keys, lookups


def grammar():
    """Run the THUG key grammar over every size-matched pair's PS2 half with
    strict invariants (exact consumption, monotonic bounded timestamps) and
    check total key counts against the header's numQKeys/numTKeys fields."""
    stats = Counter()
    fails = Counter()
    examples = []
    for row in ska_pairs():
        ps2, _ = load_pair(row)
        ver, flags, duration, h, qbytes, tbytes = header(ps2, False)
        if not flags & (1 << 23):  # bit23 family only; bit28 family is separate
            stats["skipped_non_bit23"] += 1
            continue
        n = ps2[0x0D]
        nq, nt = h[1], h[2]
        qsizes = struct.unpack_from(f"<{n}H", ps2, 0x30)
        tsizes = struct.unpack_from(f"<{n}H", ps2, 0x30 + 2 * n)
        blob = 0x30 + 4 * n
        if flags & (1 << 19):
            # PARTIALANIM: u32 origNumBones + u32 masks[ceil/32] sit between
            # the size tables and the key blobs.
            orig = struct.unpack_from("<I", ps2, blob)[0]
            blob += 4 + 4 * ((orig - 1) // 32 + 1)
            stats["partial_files"] += 1
        stats["files"] += 1
        compact = bool(flags & 0x8000)
        hires = bool(flags & 0x100)
        try:
            total_q = total_t = lookups_q = lookups_t = 0
            off = blob
            for qs in qsizes:
                keys, lk = decode_q(ps2[off:off + qs], duration, compact, hires)
                total_q += len(keys)
                lookups_q += lk
                off += qs
            for ts_ in tsizes:
                keys, lk = decode_t(ps2[off:off + ts_], duration)
                total_t += len(keys)
                lookups_t += lk
                off += ts_
            stats["decoded"] += 1
            stats["q_count_match"] += total_q == nq
            stats["t_count_match"] += total_t == nt
            stats["q_lookup_keys"] += lookups_q
            stats["t_lookup_keys"] += lookups_t
            stats["q_keys"] += total_q
            stats["t_keys"] += total_t
        except ValueError as ex:
            fails[str(ex).split(":")[0]] += 1
            if len(examples) < 5:
                examples.append((row["stem"], row["idx"], row["name"], str(ex)))
    print(f"grammar harness over {stats['files']} bit23-family files "
          f"({stats['skipped_non_bit23']} bit28-family skipped):")
    print(f"  decoded clean: {stats['decoded']}/{stats['files']}")
    print(f"  q_count_match: {stats['q_count_match']}/{stats['decoded']}  "
          f"t_count_match: {stats['t_count_match']}/{stats['decoded']}")
    print(f"  total keys: Q={stats['q_keys']} (lookups {stats['q_lookup_keys']}), "
          f"T={stats['t_keys']} (lookups {stats['t_lookup_keys']})")
    if fails:
        print(f"  failures: {dict(fails)}")
        for ex in examples:
            print(f"    {ex}")


def main() -> None:
    args = sys.argv[1:]
    if "--grammar" in args:
        grammar()
        return
    if "--batch" in args:
        batch()
        return
    stem = args[args.index("--stem") + 1] if "--stem" in args else None
    idx = args[args.index("--idx") + 1] if "--idx" in args else "0"
    for row in ska_pairs():
        if stem is None or (row["stem"] == stem and row["idx"] == idx):
            if "--mirror" in args:
                mirror(row)
            else:
                detail(row)
            return
    print("pair not found")


if __name__ == "__main__":
    main()
