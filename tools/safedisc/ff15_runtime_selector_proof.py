"""Read-only proof of THUG2 SafeDisc's protected FF15 operand permutation.

Inputs are the keyfix7 OEP snapshot plus the CD3 executable as a validation-only
oracle.  The reconstruction itself uses only the OEP main/SecServ/heap state.
"""

from pathlib import Path
import struct

import pefile


ROOT = Path(__file__).resolve().parents[2]
# Retained OEP runtime evidence named by docs/backlog/safedisc-emulation-handoff.md.
# Its main.runtime.bin/heap.bin/~df394b.tmp.runtime.bin are byte-identical to the
# historical keyfix7 snapshot this proof was first written against.
WORK = ROOT / "TestOutput" / "THUG2_decrypted_end_to_end_v2.safedisc-work"
# Validation-only CD3 comparison image; retained beside the work dir rather than
# tracked, because it is a derived game binary. Regenerate with thug2_cd3_recover.py.
ORACLE = ROOT / "TestOutput" / "thug2_cd3_crack_oracle.exe"
MAIN_BASE = 0x00400000
HEAP_BASE = 0x01000000
SECSERV_BASE = 0x10000000
MASK32 = 0xFFFFFFFF


def u32(buf: bytes | bytearray, offset: int) -> int:
    return struct.unpack_from("<I", buf, offset)[0]


def rol32(value: int, count: int) -> int:
    count &= 31
    return ((value << count) | (value >> (32 - count if count else 32))) & MASK32


def ror32(value: int, count: int) -> int:
    count &= 31
    return ((value >> count) | (value << (32 - count if count else 32))) & MASK32


def hash_round_first(value: int) -> int:
    """Runtime code block 100929BD..10092AD1 (first 0x115-byte round)."""
    value = (value + 1) & MASK32
    value = rol32(value, 0x78)
    value = (value + 0x03BA347A) & MASK32
    value ^= 0x2897128B
    value = ror32(value, 0xFD)
    value = (value + 0x2CF4138B) & MASK32
    value ^= 0x56C33681
    value ^= 0x2843792C
    value = rol32(value, 0xB0)
    value = (value - 0x30A620F9) & MASK32
    value = rol32(value, 0x46)
    value = rol32(value, 0x6E)
    value = -value & MASK32
    value = (value - 0x0ED0507D) & MASK32
    value = -value & MASK32
    value = ror32(value, 0x48)
    value = ror32(value, 0xC4)
    value = -value & MASK32
    value = ror32(value, 0x7F)
    value = (value + 0x3D117208) & MASK32
    value = ror32(value, 0x41)
    value ^= 0x261B5787
    value = (value - 0x28B62BAD) & MASK32
    value = -value & MASK32
    value = ror32(value, 0x45)
    value = (value - 0x40E45916) & MASK32
    value = -value & MASK32
    value = (value + 0x50CE75A1) & MASK32
    value = (value - 0x44D75305) & MASK32
    value = (value + 0x45615407) & MASK32
    value = -value & MASK32
    value = -value & MASK32
    value = rol32(value, 0xDA)
    value = -value & MASK32
    value ^= 0x0609446C
    value = (value - 0x21670A53) & MASK32
    value = (value + 0x2A6E140D) & MASK32
    value = rol32(value, 0x53)
    value = (value + 1) & MASK32
    value ^= 0x199C1FF8
    value = (value + 1) & MASK32
    value = (value + 0x1E4F241B) & MASK32
    value = ror32(value, 0x5C)
    return -value & MASK32


def hash_round_second(value: int) -> int:
    """Runtime code block 100928A8..100929BC (second 0x115-byte round)."""
    value = rol32(value, 0x54)
    value = (value + 0x03BA347A) & MASK32
    value ^= 0x2E24717E
    value = (value + 0x2897128B) & MASK32
    value = rol32(value, 0x81)
    value = ror32(value, 0x2C)
    value = ror32(value, 0x76)
    value ^= 0x7AA45DB0
    value = (value - 0x15176D56) & MASK32
    value = rol32(value, 0x46)
    value = ror32(value, 0x6E)
    value = ror32(value, 0xF1)
    value = (value - 0x61121C46) & MASK32
    value = ror32(value, 0x6E)
    value = (value - 0x3C6A1159) & MASK32
    value = (value - 0x6A5822DB) & MASK32
    value = (value - 0x3D117208) & MASK32
    value = rol32(value, 0x41)
    value = (value - 0x261B5787) & MASK32
    value = (value + 0x562E2B95) & MASK32
    value = (value + 0x40E45916) & MASK32
    value ^= 0x17815F55
    value = (value + 0x50CE75A1) & MASK32
    value = rol32(value, 0x05)
    value = ror32(value, 0x07)
    value = ror32(value, 0x94)
    value = rol32(value, 0xD2)
    value = ror32(value, 0x49)
    value = ror32(value, 0xDA)
    value = (value + 0x3FA614A2) & MASK32
    value = rol32(value, 0x3B)
    value = ror32(value, 0x6C)
    value ^= 0x21670A53
    value = ror32(value, 0x96)
    value ^= 0x2A6E140D
    value = (value - 0x04E37453) & MASK32
    value = rol32(value, 0xC2)
    value = rol32(value, 0xF8)
    value ^= 0x5A0F04F5
    value = rol32(value, 0x1B)
    value = (value - 0x34EF137E) & MASK32
    value = (value + 0x52D47E52) & MASK32
    value ^= 0x6C4713EC
    return rol32(value, 0x67)


def selected(section_offset: int) -> bool:
    value = section_offset & MASK32
    value = (value ^ hash_round_first(value)) & MASK32
    value = (value ^ hash_round_second(value)) & MASK32
    return (value & 3) < 2


def mapped_oracle(path: Path) -> bytearray:
    raw = path.read_bytes()
    pe = pefile.PE(data=raw)
    size = max(
        section.VirtualAddress + max(section.Misc_VirtualSize, section.SizeOfRawData)
        for section in pe.sections
    )
    image = bytearray(size)
    image[: pe.OPTIONAL_HEADER.SizeOfHeaders] = raw[: pe.OPTIONAL_HEADER.SizeOfHeaders]
    for section in pe.sections:
        start = section.VirtualAddress
        image[start : start + section.SizeOfRawData] = raw[
            section.PointerToRawData : section.PointerToRawData + section.SizeOfRawData
        ]
    return image


def main() -> None:
    main = (WORK / "main.runtime.bin").read_bytes()
    heap = (WORK / "heap.bin").read_bytes()
    secserv = (WORK / "~df394b.tmp.runtime.bin").read_bytes()
    oracle = mapped_oracle(ORACLE)

    heap_u32 = lambda address: u32(heap, address - HEAP_BASE)
    secserv_u32 = lambda address: u32(secserv, address - SECSERV_BASE)

    manager = secserv_u32(0x1012CE98)
    masks = secserv_u32(0x1012CE94)
    seed = heap_u32(manager + 0x26)
    descriptors = {
        7: (0x245208, 22),
        8: (0x245034, 1),
        9: (0x245000, 7),
    }

    selected_sites = []
    changed_sites = []
    failures = []
    candidates = 0
    dispatcher_candidates = 0
    for site_rva in range(len(main) - 6):
        if main[site_rva : site_rva + 2] != b"\xff\x15":
            continue
        operand = u32(main, site_rva + 2)
        for descriptor, (first_thunk, count) in descriptors.items():
            first_thunk_va = MAIN_BASE + first_thunk
            if not (first_thunk_va <= operand < first_thunk_va + count * 4):
                continue
            if (operand - first_thunk_va) & 3:
                continue
            candidates += 1
            position = (operand - first_thunk_va) // 4
            object_base = heap_u32(manager + descriptor * 0x8D + 0xC3)
            iat_value = u32(main, first_thunk + position * 4)
            dispatcher_thunk = object_base + position * 0x4C3 + 0x477
            # 100923BA is reached only through this generated thunk.  Some IAT
            # entries have already been resolved to direct APIs at the OEP and
            # therefore cannot take the conditional permutation path.
            if iat_value != dispatcher_thunk:
                actual_oracle = u32(oracle, site_rva + 2)
                if operand != actual_oracle:
                    failures.append((site_rva, operand, actual_oracle))
                continue
            dispatcher_candidates += 1
            section_offset = site_rva - 0x1000
            if selected(section_offset):
                selected_sites.append(site_rva)
                mask_va = heap_u32(masks + descriptor * 4)
                mask = main[mask_va - MAIN_BASE : mask_va - MAIN_BASE + (count + 7) // 8]
                for _ in range(count):
                    position = (position - (seed + section_offset)) % count
                    if mask[position >> 3] & (1 << (position & 7)):
                        break
                else:
                    raise RuntimeError(f"descriptor {descriptor} has no allowed position")

            expected = first_thunk_va + position * 4
            if expected != operand:
                changed_sites.append(site_rva)
            actual_oracle = u32(oracle, site_rva + 2)
            if expected != actual_oracle:
                failures.append((site_rva, expected, actual_oracle))

    print(f"manager={manager:08X} masks={masks:08X} seed={seed:08X}")
    print(
        f"candidates={candidates} dispatcher={dispatcher_candidates} "
        f"selected={len(selected_sites)} changed={len(changed_sites)}"
    )
    print("selected=" + ",".join(f"{rva:06X}" for rva in selected_sites))
    print("changed=" + ",".join(f"{rva:06X}" for rva in changed_sites))
    print(f"oracle mismatches={len(failures)}")
    for site_rva, expected, actual in failures:
        print(f"  {site_rva:06X}: expected {expected:08X}, oracle {actual:08X}")


if __name__ == "__main__":
    main()
