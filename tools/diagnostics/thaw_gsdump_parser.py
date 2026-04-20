#!/usr/bin/env python3
"""
Minimal PCSX2 GS dump (.gs.xz / .gs.zst) parser.

Extracts the stream of GIF packets / register writes that the PS2 engine
produces after VIF/VU1 decodes the level MDL. This is what actually hits
the GS and gets drawn on screen — ground truth for "what is the engine
really rendering for BH".

Wire format (after xz/zstd decompression):
  u32 crc_or_sentinel (if 0xFFFFFFFF, new-format dump with GSDumpHeader)
  u32 header_block_size
  [header_block_size bytes of state block, starting with GSDumpHeader]
    GSDumpHeader: state_version, state_size, serial_offset, serial_size,
                  crc, screenshot_{width,height,offset,size}
  [header.state_size bytes of real state data]
  u8[8192]   (GS regs_data snapshot)
  Packet stream until EOF:
    u8 packet_id (GSType: 0=Transfer, 1=VSync, 2=ReadFIFO2, 3=Registers)
    if Transfer:  u8 path (0=Path1=VU1, 1=Path2=VIF1, 2=Path3=GIF, 3=Dummy) + u32 length + length bytes
    if VSync:     1 byte
    if ReadFIFO2: 4 bytes
    if Registers: 8192 bytes

Usage:
  python thaw_gsdump_parser.py <path.gs.xz> [--summary] [--dump-packets N]
"""

import argparse
import lzma
import struct
import sys
from pathlib import Path


def decompress(path: Path) -> bytes:
    name = path.name.lower()
    if name.endswith(".xz"):
        return lzma.decompress(path.read_bytes())
    if name.endswith(".zst"):
        import zstandard
        return zstandard.ZstdDecompressor().decompress(path.read_bytes())
    return path.read_bytes()


GS_TYPE_NAMES = {0: "Transfer", 1: "VSync", 2: "ReadFIFO2", 3: "Registers"}
# PCSX2 transfer-path enum: Path1Old=0, Path2=1, Path3=2, Path1New=3, Dummy=4.
GS_PATH_NAMES = {0: "Path1Old", 1: "Path2=VIF1", 2: "Path3=GIF", 3: "Path1New=VU1", 4: "Dummy"}

# GS register IDs (used as REGS descriptor in GIF tag + as A+D addr in A+D mode)
GS_REG_NAMES = {
    0x00: "PRIM", 0x01: "RGBAQ", 0x02: "ST", 0x03: "UV",
    0x04: "XYZF2", 0x05: "XYZ2",
    0x06: "TEX0_1", 0x07: "TEX0_2",
    0x08: "CLAMP_1", 0x09: "CLAMP_2",
    0x0A: "FOG",
    0x0C: "XYZF3", 0x0D: "XYZ3",
    0x0E: "A+D", 0x0F: "NOP",
}

# Registers addressable via A+D mode
GS_AD_REG_NAMES = {
    0x00: "PRIM", 0x01: "RGBAQ", 0x02: "ST", 0x03: "UV",
    0x04: "XYZF2", 0x05: "XYZ2",
    0x06: "TEX0_1", 0x07: "TEX0_2",
    0x08: "CLAMP_1", 0x09: "CLAMP_2",
    0x0A: "FOG",
    0x0C: "XYZF3", 0x0D: "XYZ3",
    0x14: "TEX1_1", 0x15: "TEX1_2",
    0x16: "TEX2_1", 0x17: "TEX2_2",
    0x18: "XYOFFSET_1", 0x19: "XYOFFSET_2",
    0x1A: "PRMODECONT", 0x1B: "PRMODE",
    0x1C: "TEXCLUT",
    0x22: "SCANMSK",
    0x34: "MIPTBP1_1", 0x35: "MIPTBP1_2",
    0x36: "MIPTBP2_1", 0x37: "MIPTBP2_2",
    0x3B: "TEXA", 0x3D: "FOGCOL",
    0x3F: "TEXFLUSH",
    0x40: "SCISSOR_1", 0x41: "SCISSOR_2",
    0x42: "ALPHA_1", 0x43: "ALPHA_2",
    0x44: "DIMX", 0x45: "DTHE",
    0x46: "COLCLAMP",
    0x47: "TEST_1", 0x48: "TEST_2",
    0x49: "PABE", 0x4A: "FBA_1", 0x4B: "FBA_2",
    0x4C: "FRAME_1", 0x4D: "FRAME_2",
    0x4E: "ZBUF_1", 0x4F: "ZBUF_2",
    0x50: "BITBLTBUF", 0x51: "TRXPOS", 0x52: "TRXREG", 0x53: "TRXDIR",
    0x54: "HWREG",
    0x60: "SIGNAL", 0x61: "FINISH", 0x62: "LABEL",
}

PRIM_NAMES = {
    0: "POINT", 1: "LINE", 2: "LINE_STRIP",
    3: "TRIANGLE", 4: "TRIANGLE_STRIP", 5: "TRIANGLE_FAN",
    6: "SPRITE", 7: "INVALID",
}


def parse_dump(raw: bytes) -> dict:
    """Return a dict with crc, serial, state, regs, and packets list."""
    result = {"crc": None, "serial": "", "state": b"", "regs": b"", "packets": []}
    off = 0

    def read(n):
        nonlocal off
        if off + n > len(raw):
            raise EOFError(f"short read at {off}: wanted {n}, have {len(raw) - off}")
        b = raw[off:off + n]
        off += n
        return b

    crc_or_sentinel = struct.unpack_from("<I", read(4))[0]
    header_block_size = struct.unpack_from("<I", read(4))[0]
    header_block = read(header_block_size)

    if crc_or_sentinel == 0xFFFFFFFF:
        # GSDumpHeader: 9 × u32, packed 4
        (state_version, state_size, serial_off, serial_size, crc,
         sw, sh, so, ss_sz) = struct.unpack_from("<9I", header_block, 0)
        result["crc"] = crc
        result["state_version"] = state_version
        result["screenshot"] = (sw, sh, so, ss_sz)
        if serial_size > 0:
            result["serial"] = header_block[serial_off:serial_off + serial_size].decode("ascii", errors="replace")
        # Real state block follows after the header block.
        result["state"] = read(state_size)
    else:
        # Legacy: crc already consumed (4 bytes), state follows 4-byte length
        result["crc"] = crc_or_sentinel
        # header_block is actually the legacy state of size header_block_size.
        result["state"] = header_block

    result["regs"] = read(8192)

    # Packet stream
    while off < len(raw):
        packet_id = raw[off]
        off += 1
        if packet_id == 0:  # Transfer
            path = raw[off]
            length = struct.unpack_from("<I", raw, off + 1)[0]
            off += 5
            data = raw[off:off + length]
            off += length
            result["packets"].append(("Transfer", path, data))
        elif packet_id == 1:  # VSync
            off += 1
            result["packets"].append(("VSync", None, None))
        elif packet_id == 2:  # ReadFIFO2
            val = raw[off:off + 4]
            off += 4
            result["packets"].append(("ReadFIFO2", None, val))
        elif packet_id == 3:  # Registers
            off += 8192
            result["packets"].append(("Registers", None, None))
        else:
            print(f"  ! Unknown packet id={packet_id} at offset {off - 1}; stopping", file=sys.stderr)
            break

    return result


def summarise(dump: dict) -> None:
    print(f"CRC: 0x{dump['crc']:08X}")
    if dump.get("serial"):
        print(f"Serial: {dump['serial']}")
    print(f"State size: {len(dump['state']):,} bytes")
    print(f"Regs size:  {len(dump['regs']):,} bytes")
    packets = dump["packets"]
    print(f"Packets: {len(packets):,}")

    # Per-type count
    type_counts = {}
    transfer_by_path = {}
    transfer_byte_by_path = {}
    for p in packets:
        type_counts[p[0]] = type_counts.get(p[0], 0) + 1
        if p[0] == "Transfer":
            path = p[1]
            transfer_by_path[path] = transfer_by_path.get(path, 0) + 1
            transfer_byte_by_path[path] = transfer_byte_by_path.get(path, 0) + len(p[2] or b"")
    print("  By type:")
    for k, v in type_counts.items():
        print(f"    {k}: {v:,}")
    if transfer_by_path:
        print("  Transfer by path:")
        for p, c in sorted(transfer_by_path.items()):
            name = GS_PATH_NAMES.get(p, f"?{p}")
            bytes_ = transfer_byte_by_path[p]
            print(f"    path {p} ({name}): {c:,} packets, {bytes_:,} bytes")


def hex_dump(data: bytes, off: int, n: int) -> None:
    for i in range(0, min(n, len(data)), 16):
        row = data[i:i + 16]
        hx = " ".join(f"{b:02x}" for b in row)
        asc = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        print(f"  {off + i:06x}  {hx:<48}  {asc}")


def parse_gif_packets(data: bytes, state: dict) -> dict:
    """Walk a Path1 transfer (VU1 output) and tally register writes, maintaining
    the rolling TEX0 + PRIM state so we can attribute XYZ writes to the currently
    active texture."""
    reg_writes = state.setdefault("reg_writes", {})
    tex0_values = state.setdefault("tex0_values", {})
    prim_counts = state.setdefault("prim_counts", {})
    xyz_by_tex0 = state.setdefault("xyz_by_tex0", {})
    xyz_by_prim = state.setdefault("xyz_by_prim", {})
    current_tex0 = state.setdefault("current_tex0", 0)
    current_prim = state.setdefault("current_prim", 0)
    state["xyz_count"] = state.get("xyz_count", 0)
    state["tag_count"] = state.get("tag_count", 0)

    off = 0
    N = len(data)

    while off + 16 <= N:
        tag_lo = struct.unpack_from("<Q", data, off)[0]
        tag_hi = struct.unpack_from("<Q", data, off + 8)[0]
        nloop = tag_lo & 0x7FFF
        pre = (tag_lo >> 46) & 1
        prim = (tag_lo >> 47) & 0x7FF
        flg = (tag_lo >> 58) & 3
        nreg = (tag_lo >> 60) & 0xF
        regs_desc = tag_hi
        if nreg == 0:
            nreg = 16
        state["tag_count"] += 1
        if pre:
            current_prim = prim & 0x7
            p_name = PRIM_NAMES.get(current_prim, "?")
            prim_counts[p_name] = prim_counts.get(p_name, 0) + 1
        off += 16

        if flg == 3:  # DISABLE
            continue

        if flg == 0:  # PACKED: nloop × nreg × 16 bytes
            body_bytes = nloop * nreg * 16
            if off + body_bytes > N:
                break
            for _ in range(nloop):
                for reg_idx in range(nreg):
                    reg_code = (regs_desc >> (reg_idx * 4)) & 0xF
                    name = GS_REG_NAMES.get(reg_code, f"?{reg_code:X}")
                    reg_writes[name] = reg_writes.get(name, 0) + 1
                    if reg_code in (0x04, 0x05):
                        state["xyz_count"] += 1
                        xyz_by_tex0[current_tex0] = xyz_by_tex0.get(current_tex0, 0) + 1
                        p_name = PRIM_NAMES.get(current_prim, "?")
                        xyz_by_prim[p_name] = xyz_by_prim.get(p_name, 0) + 1
                    if reg_code == 0x0E:  # A+D
                        data_lo = struct.unpack_from("<Q", data, off)[0]
                        addr = struct.unpack_from("<B", data, off + 8)[0]
                        ad_name = GS_AD_REG_NAMES.get(addr, f"?AD{addr:02X}")
                        reg_writes[f"AD:{ad_name}"] = reg_writes.get(f"AD:{ad_name}", 0) + 1
                        if ad_name == "TEX0_1":
                            current_tex0 = data_lo
                            tex0_values[data_lo] = tex0_values.get(data_lo, 0) + 1
                        elif ad_name == "TEX0_2":
                            tex0_values[data_lo] = tex0_values.get(data_lo, 0) + 1
                        elif ad_name == "PRIM":
                            current_prim = data_lo & 0x7
                            p_name = PRIM_NAMES.get(current_prim, "?")
                            prim_counts[p_name] = prim_counts.get(p_name, 0) + 1
                    off += 16
        elif flg == 1:  # REGLIST: nloop × nreg × 8 bytes, padded to 16
            body_bytes = ((nloop * nreg * 8) + 15) & ~15
            for i in range(nloop * nreg):
                reg_idx = i % nreg
                reg_code = (regs_desc >> (reg_idx * 4)) & 0xF
                name = GS_REG_NAMES.get(reg_code, f"?{reg_code:X}")
                reg_writes[name] = reg_writes.get(name, 0) + 1
                if reg_code in (0x04, 0x05):
                    state["xyz_count"] += 1
                    xyz_by_tex0[current_tex0] = xyz_by_tex0.get(current_tex0, 0) + 1
            off += body_bytes
        elif flg == 2:  # IMAGE: nloop × 16 bytes of image data
            off += nloop * 16

    state["current_tex0"] = current_tex0
    state["current_prim"] = current_prim
    return state


def analyse_gif(dump: dict, limit: int | None = None) -> dict:
    print("\n=== GIF analysis (all Path1/Path1New transfer packets) ===")
    state = {}
    processed = 0
    skipped = 0
    for p in dump["packets"]:
        if p[0] != "Transfer":
            continue
        path = p[1]
        # Path=0 (Path1Old) and 3 (Path1New) are VU1-output. Games use 3 on modern hw.
        if path not in (0, 3):
            skipped += 1
            continue
        if limit is not None and processed >= limit:
            break
        parse_gif_packets(p[2], state)
        processed += 1

    print(f"  Processed {processed:,} VU1-output transfer packet(s), {state['tag_count']:,} GIF tags")
    print(f"  Total XYZ writes: {state['xyz_count']:,}")
    print(f"  Top register writes:")
    for k, v in sorted(state["reg_writes"].items(), key=lambda x: -x[1])[:20]:
        print(f"    {k}: {v:,}")
    print(f"  Prim types seen (current-prim at XYZ-write time):")
    for k, v in sorted(state["xyz_by_prim"].items(), key=lambda x: -x[1]):
        print(f"    {k}: {v:,} XYZ writes")
    print(f"  Unique TEX0 values: {len(state['tex0_values']):,}")
    print(f"  Top 15 TEX0 values by XYZ-write count:")
    for v, cnt in sorted(state["xyz_by_tex0"].items(), key=lambda x: -x[1])[:15]:
        tbp = v & 0x3FFF
        tbw = (v >> 14) & 0x3F
        psm = (v >> 20) & 0x3F
        tw = (v >> 26) & 0xF
        th = (v >> 30) & 0xF
        cbp = (v >> 37) & 0x3FFF
        cpsm = (v >> 51) & 0xF
        print(f"    TEX0 0x{v:016X}  TBP=0x{tbp:04X} TBW={tbw} PSM=0x{psm:02X} "
              f"W=2^{tw} H=2^{th} CBP=0x{cbp:04X} CPSM=0x{cpsm:02X}  XYZ={cnt}")
    return state


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("path", type=Path, help="PCSX2 .gs.xz / .gs.zst / uncompressed dump")
    ap.add_argument("--dump-packets", type=int, default=0, help="hex-dump the first N transfer packets (default 0)")
    ap.add_argument("--skip-first", type=int, default=0, help="skip the first N transfer packets before dumping")
    ap.add_argument("--gif", action="store_true", help="decode GIF packets from VU1-output transfers and tally register writes")
    ap.add_argument("--gif-limit", type=int, default=None, help="limit GIF analysis to the first N VU1 packets (for speed)")
    args = ap.parse_args()

    if not args.path.exists():
        sys.exit(f"missing file: {args.path}")

    print(f"Loading {args.path.name} ({args.path.stat().st_size:,} bytes compressed) …")
    raw = decompress(args.path)
    print(f"  Decompressed to {len(raw):,} bytes")

    dump = parse_dump(raw)
    summarise(dump)

    if args.gif:
        analyse_gif(dump, limit=args.gif_limit)

    if args.dump_packets > 0:
        shown = 0
        skipped = 0
        print()
        for p in dump["packets"]:
            if p[0] != "Transfer":
                continue
            if skipped < args.skip_first:
                skipped += 1
                continue
            if shown >= args.dump_packets:
                break
            _, path, data = p
            print(f"--- Transfer packet #{skipped + shown + 1} path={path} ({GS_PATH_NAMES.get(path, '?')}) len={len(data):,}")
            hex_dump(data, 0, 64)
            shown += 1


if __name__ == "__main__":
    main()
