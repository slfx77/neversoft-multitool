#!/usr/bin/env python3
r"""Run a SafeDisc-wrapped Win32 executable's loader under emulation until it
decrypts itself, then dump the plaintext image.

Same idea as `erz_emu_decode.py`, which recovers the N64 ERZ codec by running
the ROM's own decompressor under a MIPS interpreter rather than reimplementing
it. Here the target is x86: THUG2.exe's `.text` is encrypted (uniformly high
entropy, no exploitable cipher structure), the SafeDisc loader stages are
themselves obfuscated, and every API and driver call is resolved dynamically -
so there is no algorithm in the file to transcribe. The only way to see the
plaintext is to let the loader produce it.

The key instrumentation is a WRITE WATCH over `.text`. Decryption is, by
definition, the loader writing plaintext into that range; the watch reports
exactly when, from where, and how much, and the image is dumped once coverage
crosses a threshold.

This does not require the disc, the driver, or a VM. If the decryption key
turns out to be derived from the SafeDisc driver's challenge/response, that
shows up here as a `DeviceIoControl` on the secdrv handle whose output feeds
the key schedule - which answers the "is it disc-derived?" question that
nothing static can.

Usage:
    python tools/diagnostics/safedisc_emu.py <exe>
    python tools/diagnostics/safedisc_emu.py <exe> --max-instructions 50000000
    python tools/diagnostics/safedisc_emu.py <exe> --trace 200      # first 200 insns
    python tools/diagnostics/safedisc_emu.py <exe> --dump out.bin

Requires: pip install unicorn capstone pefile

STATUS (2026-08-07, second pass): RUNS THE PROTECTION CORE, NOT YET DECRYPTING.

Depth went from ~155,900 instructions to ~18,870,000 (121x) and the run now
executes SafeDisc's own protection DLL rather than its outermost stub.

What the harness established about the loader, all observed rather than assumed:

  1. The stub resolves its API set one function at a time (34 iterations of
     GlobalAlloc -> GetProcAddress -> GlobalFree), takes a single-instance
     mutex, then builds paths.
  2. It probes C:\WINDOWS\Temp, tests write permission by creating and removing
     a scratch directory, and creates its signature temp tree
     `~e5.0001.dir.0000`.
  3. It OPENS ITS OWN EXECUTABLE and reads 913,910 bytes out of it. This is the
     step that makes a real file layer mandatory -- while CreateFile failed, the
     loader just fell into error handling.
  4. It writes an 805,916-byte DLL out of that data into `~df394b.tmp` and
     LoadLibrary's it. THE DECRYPTION IS NOT IN THE EXE. `--dump-temp-files`
     writes this out; its `.text` entropy is 6.871, i.e. plaintext code, and its
     C++ mangled exports name the whole architecture:
        CTransformXor::PerformTransform, CTransformRandomAccumulate::PerformTransform
        CKeyBasic::GetKeyData, CKeyMngr::InputTransformAfterPfnActivation
        CJumpRun::InstallJumpSystem, CModuleMonitor::IsModuleChecksumOkay
        CAltAsc::SetupInterruptHandler, CAltExceptions::Reset
     That extracted DLL is the most valuable artefact here and may be a better
     route to the answer than finishing the emulation.
  5. Inside that DLL, SafeDisc patches code into ITSELF through
     GetCurrentProcess -> VirtualProtect -> WriteProcessMemory ->
     FlushInstructionCache. A stub that returns success without copying leaves
     the target zeroed. NOTE: that channel bypasses the instruction-level write
     hook entirely, so `note_text_write` accounts for it by hand -- otherwise a
     successful decrypt would still report ".text writes: 0".
  6. The patched-in payload is a hand-written trampoline that pushes THUG2.exe's
     image base (0x00400000) and a jump-record pointer, then calls back into the
     DLL. So the decryption target is the main image, as expected.

CURRENT BLOCKER: after InstallJumpSystem's `ret 0xC`, execution resumes at
0x100F2634, which is a zero-filled .data structure (the jump record the
trampoline passes as an ARGUMENT), not code. Verified with `--watch`: the DLL
itself explicitly zeroes that address from 0x10043D98, so the memory is correct
and the RETURN ADDRESS is wrong -- some caller's stack is off. That is a
harness-fidelity bug, not a protection defence. `--trail` plus `--watch` are the
tools for it; both were added for exactly this.

RESOLVED THIS PASS (do not re-investigate):
  * `UC_X86_REG_FS_BASE` is a NO-OP on x86-32 in Unicorn 2. Replaced with a real
    GDT (see `install_gdt`).
  * Installing a GDT means EVERY segment register must be loaded explicitly.
    Unicorn's defaults are all selector 0x0000, which is flat while no GDT
    exists but becomes the NULL DESCRIPTOR once one does -- clearing SS's D/B
    bit, so `push ebp` truncated ESP to 16 bits and the first GDT run died on
    instruction 1. CS and SS must use ring-0 descriptors (Unicorn is at CPL 0);
    only FS can carry the realistic ring-3 0x3B.
  * Registry APIs return LSTATUS where ERROR_SUCCESS is ZERO, and
    GetFileAttributes returns a bitmask. The generic "return 1" reported failure
    for both.
  * The MSVC CRT startup surface must be real: the extracted DLL runs a full CRT
    init in its DllMain, which walks the environment block as a genuine string.

STILL MISSING, in rough order of likely need:
  * whatever is corrupting the stack around InstallJumpSystem (the blocker)
  * a synthetic export table for kernel32/ntdll, if the DLL ever resolves
    imports by parsing them rather than through our GetProcAddress
  * the eventual secdrv `DeviceIoControl`. The harness deliberately FAILS it
    rather than fabricating a challenge response: a made-up answer would produce
    a wrong key and a plausible-looking garbage decrypt, which is worse than a
    visible stop. If the key proves disc-derived, this is where it shows up.
"""

from __future__ import annotations

import argparse
import math
import struct
import sys
from collections import Counter, defaultdict, deque
from pathlib import Path

try:
    import pefile
    from unicorn import (
        UC_ARCH_X86, UC_HOOK_CODE, UC_HOOK_INTR, UC_HOOK_MEM_INVALID,
        UC_HOOK_MEM_WRITE, UC_MODE_32, UC_PROT_ALL, Uc, UcError,
    )
    from unicorn.x86_const import (
        UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_EBX, UC_X86_REG_ECX,
        UC_X86_REG_EDI, UC_X86_REG_EDX, UC_X86_REG_EFLAGS, UC_X86_REG_EIP,
        UC_X86_REG_CS, UC_X86_REG_DS, UC_X86_REG_ES, UC_X86_REG_ESI, UC_X86_REG_ESP,
        UC_X86_REG_FS, UC_X86_REG_GDTR, UC_X86_REG_SS,
    )
except ImportError as exc:  # pragma: no cover
    print(f"missing dependency: {exc}\n  pip install unicorn capstone pefile", file=sys.stderr)
    raise SystemExit(2) from exc

try:
    import capstone
except ImportError:
    capstone = None


# Emulated address-space layout, chosen to sit clear of a 0x400000 image base.
STACK_BASE = 0x00200000
STACK_SIZE = 0x00100000
TEB_ADDR = 0x00320000
PEB_ADDR = 0x00321000
LDR_ADDR = 0x00322000           # PEB_LDR_DATA + the LDR_DATA_TABLE_ENTRY array
GDT_ADDR = 0x00330000
GDT_SIZE = 0x1000
HEAP_BASE = 0x01000000
HEAP_SIZE = 0x04000000
STUB_BASE = 0x70000000          # one byte per stubbed export, hooked on execute
STUB_SIZE = 0x00010000
FAKE_MODULE_BASE = 0x71000000   # handles handed back by LoadLibrary for unknown DLLs
FAKE_MODULE_STEP = 0x00010000
LOADED_MODULE_BASE = 0x10000000  # where PEs the loader extracts are really mapped

# Windows XP-era load addresses. These are the values a packer expects to see
# when it walks the loader list, and using the real ones costs nothing.
NTDLL_BASE = 0x7C900000
KERNEL32_BASE = 0x7C800000
USER32_BASE = 0x7E410000
SYSTEM_MODULE_SIZE = 0x00100000

# Selectors. FS gets the real Win32 user-mode value 0x3B (GDT index 7, RPL 3)
# because that one is occasionally read as an environment check, and a ring-3
# DATA segment loads fine at CPL 0: the rule is max(CPL, RPL) <= DPL, so 3 <= 3.
#
# CS and SS cannot follow suit. Unicorn's emulated CPU runs at CPL 0, loading CS
# with a DPL-3 selector is a privilege transition and raises UC_ERR_EXCEPTION,
# and SS additionally requires RPL == CPL == 0. So those two use ring-0
# descriptors; their base is 0 either way, so only the selector VALUE differs
# from real Windows (0x1B/0x23), which nothing in a loader stub inspects.
SEL_CODE = 0x08
SEL_DATA = 0x10
SEL_TEB_R3 = 0x3B

# The end-of-chain sentinel in TEB.ExceptionList (fs:[0]). A SEH walk that never
# finds this runs off into unmapped memory instead of reporting "unhandled".
SEH_END_OF_CHAIN = 0xFFFFFFFF

# A distinguishable pseudo-handle so a DeviceIoControl on the SafeDisc driver is
# obvious in the trace rather than blending in with file handles.
SECDRV_HANDLE = 0x100

# Returning to this address means the entry point returned; returning to the
# other means an SEH handler returned and we must decide whether to resume.
# The SEH one must live in MAPPED memory -- UC_HOOK_CODE fires only after the
# instruction fetch succeeds -- so it sits at the top of the stub page.
MAGIC_RETURN = 0xDEADF00D
MAGIC_SEH_RETURN = STUB_BASE + STUB_SIZE - 0x10
MAGIC_CALL_RETURN = STUB_BASE + STUB_SIZE - 0x20

# NTSTATUS values the harness raises through the SEH chain.
STATUS_BREAKPOINT = 0x80000003
STATUS_SINGLE_STEP = 0x80000004
STATUS_ACCESS_VIOLATION = 0xC0000005
STATUS_INTEGER_DIVIDE_BY_ZERO = 0xC0000094
STATUS_ILLEGAL_INSTRUCTION = 0xC000001D

# The loader looks for companion files (00000001.TMP) beside the exe, so give
# it a realistic path rather than an empty buffer.
EMULATED_EXE_PATH = r"C:\Games\THUG2\THUG2.exe"
EMULATED_TEMP_PATH = r"C:\WINDOWS\Temp" + "\\"

# x86 CONTEXT field offsets (32-bit). Only the integer/control block matters
# here; the FloatSave area at 0x1C..0x8B is left zeroed.
CTX_SIZE = 0x2CC
CTX_FLAGS, CTX_GS, CTX_FS, CTX_ES, CTX_DS = 0x00, 0x8C, 0x90, 0x94, 0x98
CTX_EDI, CTX_ESI, CTX_EBX, CTX_EDX, CTX_ECX, CTX_EAX = 0x9C, 0xA0, 0xA4, 0xA8, 0xAC, 0xB0
CTX_EBP, CTX_EIP, CTX_CS, CTX_EFLAGS, CTX_ESP, CTX_SS = 0xB4, 0xB8, 0xBC, 0xC0, 0xC4, 0xC8
CONTEXT_FULL = 0x00010007

# EXCEPTION_RECORD field offsets (32-bit).
EXR_SIZE = 0x50
EXR_CODE, EXR_FLAGS, EXR_RECORD, EXR_ADDRESS, EXR_NPARAMS = 0x00, 0x04, 0x08, 0x0C, 0x10


def gdt_entry(base: int, limit: int, access: int, flags: int) -> bytes:
    """Pack one 8-byte GDT descriptor.

    The field split is the awkward part of x86 segmentation: base and limit are
    each stored in two discontiguous pieces around the access byte.
    """
    value = limit & 0xFFFF
    value |= (base & 0xFFFFFF) << 16
    value |= (access & 0xFF) << 40
    value |= ((limit >> 16) & 0xF) << 48
    value |= (flags & 0xF) << 52
    value |= ((base >> 24) & 0xFF) << 56
    return struct.pack("<Q", value)


# Access byte: Present | DPL | S(code/data) | type.
ACCESS_CODE_R0 = 0x80 | (0 << 5) | 0x10 | 0x0A   # present, ring 0, exec+read
ACCESS_DATA_R0 = 0x80 | (0 << 5) | 0x10 | 0x02   # present, ring 0, read+write
ACCESS_DATA_R3 = 0x80 | (3 << 5) | 0x10 | 0x02   # present, ring 3, read+write
FLAGS_PAGE_32 = 0xC   # 4K granularity, 32-bit (the D/B bit is what makes ESP
                      # 32-bit wide; without it a stack push wraps at 16 bits)
FLAGS_BYTE_32 = 0x4   # byte granularity, 32-bit


class EmulatedFile:
    """One open file handle.

    Host-backed files are read into memory ONCE and served from there. Writes go
    to that buffer and are never flushed: the loader creates and writes temp
    files as a matter of course, and this harness must not put anything on the
    real filesystem.
    """

    def __init__(self, name: str, data: bytes, host_path: Path | None = None):
        self.name = name
        self.data = bytearray(data)
        self.host_path = host_path
        self.pos = 0

    def read(self, count: int) -> bytes:
        chunk = bytes(self.data[self.pos : self.pos + count])
        self.pos += len(chunk)
        return chunk

    def write(self, payload: bytes) -> int:
        end = self.pos + len(payload)
        if end > len(self.data):
            self.data.extend(b"\x00" * (end - len(self.data)))
        self.data[self.pos : end] = payload
        self.pos = end
        return len(payload)

    def seek(self, offset: int, whence: int) -> int:
        base = {0: 0, 1: self.pos, 2: len(self.data)}.get(whence, 0)
        self.pos = max(0, base + offset)
        return self.pos


class HostFileMap:
    """Maps the emulated Win32 paths onto real files in the build tree.

    SafeDisc reads its OWN executable back off disk (that is where the encrypted
    payload lives) and looks for companions like 00000001.TMP, so a harness that
    fails every CreateFile cannot get past the loader's first real step. The map
    is basename-keyed and case-insensitive because the emulated path prefix is
    invented anyway.
    """

    COMPANION_NAMES = {"00000001.tmp", "drvmgt.dll", "secdrv.sys", "00000002.tmp"}

    def __init__(self, exe: Path, extra_dirs: list[Path] | None = None):
        self.by_name: dict[str, Path] = {}
        search = [exe.parent]
        # Walk up to the build root: on a disc layout the SafeDisc companions sit
        # beside the installer, not beside the game exe.
        parent = exe.parent
        for _ in range(5):
            parent = parent.parent
            if parent == parent.parent:
                break
            search.append(parent)
        search.extend(extra_dirs or [])

        for directory in search:
            if not directory.is_dir():
                continue
            try:
                for child in directory.iterdir():
                    if not child.is_file():
                        continue
                    key = child.name.lower()
                    # First directory wins, so the exe's own folder beats the root.
                    if key not in self.by_name:
                        self.by_name[key] = child
            except OSError:
                continue
        self.by_name[exe.name.lower()] = exe

    def resolve(self, emulated_path: str) -> Path | None:
        name = emulated_path.replace("/", "\\").rsplit("\\", 1)[-1].lower()
        return self.by_name.get(name)

    def companions_present(self) -> list[str]:
        return sorted(n for n in self.COMPANION_NAMES if n in self.by_name)


class LoadedModule:
    """A PE the loader extracted and LoadLibrary'd, mapped for real.

    SafeDisc does not decrypt in the exe stub. It writes an ~800 KB DLL into a
    temp file and loads that; the DLL exports the transform and key classes
    (CTransformXor::PerformTransform, CKeyBasic::GetKeyData, CJumpRun, ...) and
    is where the actual work happens. Handing back a fake module handle stops the
    run dead, so the harness maps the image, relocates it, stubs its imports, and
    resolves its exports by name.
    """

    def __init__(self, name: str, base: int, size: int, exports: dict[str, int], entry: int):
        self.name = name
        self.base = base
        self.size = size
        self.exports = exports    # export name -> absolute address
        self.entry = entry        # DllMain, absolute; 0 if none


def entropy(data: bytes) -> float:
    if not data:
        return 0.0
    counts = Counter(data)
    n = len(data)
    return -sum(v / n * math.log2(v / n) for v in counts.values())


class SafeDiscEmulator:
    def __init__(self, path: Path, verbose: bool = False):
        self.path = path
        self.verbose = verbose
        self.pe = pefile.PE(str(path))
        self.image_base = self.pe.OPTIONAL_HEADER.ImageBase
        self.entry = self.image_base + self.pe.OPTIONAL_HEADER.AddressOfEntryPoint

        self.uc = Uc(UC_ARCH_X86, UC_MODE_32)
        self.stubs: dict[int, tuple[str, int]] = {}   # address -> (name, argc)
        self.stub_by_name: dict[str, int] = {}
        self.next_stub = STUB_BASE
        self.next_module = FAKE_MODULE_BASE
        self.modules: dict[str, int] = {}
        self.heap_next = HEAP_BASE

        self.api_calls: Counter = Counter()
        self.unknown_apis: set[str] = set()
        self.api_tail: list[str] = []
        self.api_order: list[str] = []
        self.text_writes: dict[int, int] = {}          # page -> bytes written
        self.write_sources: Counter = Counter()        # EIP of writer -> count
        self.instructions = 0
        self.stop_reason = "not started"
        self.faults: Counter = Counter()               # EIP -> access violations
        self.interrupts: Counter = Counter()           # intno -> count
        self.seh_dispatches = 0
        self.seh_limit = 10_000
        self.seh_stack: list[tuple[int, int]] = []
        self.trace_remaining = 0
        self.next_reg_key = 0x80000100
        self.stop_on_unknown_api = True
        self.host_files = HostFileMap(path)
        self.files: dict[int, EmulatedFile] = {}
        self.next_handle = 0x200
        self.file_reads: Counter = Counter()
        self.driver_opens: list[str] = []
        self.driver_calls: list[tuple[int, int]] = []
        self.created_files: list[EmulatedFile] = []
        self.emulated_by_name: dict[str, EmulatedFile] = {}
        self.temp_dump_dir: Path | None = None
        self.loaded_modules: dict[str, LoadedModule] = {}
        self.modules_by_base: dict[int, LoadedModule] = {}
        self.next_loaded_base = LOADED_MODULE_BASE
        self.resolved_exports: Counter = Counter()
        self.heap_sizes: dict[int, int] = {}
        self.deferred_call: tuple[int, list[int]] | None = None
        self.pending_returns: list[tuple[int, int, int]] = []
        self.env_block_ansi = 0
        self.env_block_wide = 0
        self.next_tls_slot = 0
        self.tls: dict[int, int] = {}
        self.restart_pending = False
        self.fault_repeats: Counter = Counter()
        self.write_channels: Counter = Counter()
        self.watch_lo = 0
        self.watch_hi = 0
        self.process_writes: list[tuple[int, int]] = []
        self.trail: deque[int] | None = None
        self.module_sections: dict[str, list[tuple[str, int, int, int]]] = {}
        # Paths GetFileAttributes should report as existing directories. The
        # loader probes for its own directory and for Windows before deciding
        # where to put its temp files.
        self.known_directories = {
            r"c:\windows", r"c:\windows\system32", r"c:\windows\temp",
            r"c:\games\thug2", r"c:", r"c:\\",
        }

        self.text_start = 0
        self.text_end = 0
        for section in self.pe.sections:
            if section.Name.rstrip(b"\0") == b".text":
                self.text_start = self.image_base + section.VirtualAddress
                self.text_end = self.text_start + max(
                    section.Misc_VirtualSize, section.SizeOfRawData
                )
                break

    # --- setup ----------------------------------------------------------

    def map_image(self) -> None:
        size = (self.pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF
        self.uc.mem_map(self.image_base, size, UC_PROT_ALL)
        self.uc.mem_write(self.image_base, self.pe.header)
        for section in self.pe.sections:
            data = section.get_data()
            if data:
                self.uc.mem_write(self.image_base + section.VirtualAddress, data)
        log(f"image mapped at 0x{self.image_base:08X} ({size // 1024} KB), entry 0x{self.entry:08X}")
        if self.text_start:
            log(f".text 0x{self.text_start:08X}-0x{self.text_end:08X}, entropy {self.text_entropy():.3f}")

    def map_support(self) -> None:
        self.uc.mem_map(STACK_BASE, STACK_SIZE, UC_PROT_ALL)
        self.uc.mem_map(TEB_ADDR, 0x3000, UC_PROT_ALL)     # TEB, PEB, PEB_LDR_DATA
        self.uc.mem_map(GDT_ADDR, GDT_SIZE, UC_PROT_ALL)
        self.uc.mem_map(HEAP_BASE, HEAP_SIZE, UC_PROT_ALL)
        self.uc.mem_map(STUB_BASE, STUB_SIZE, UC_PROT_ALL)
        self.uc.mem_map(FAKE_MODULE_BASE, 0x01000000, UC_PROT_ALL)
        for base in (NTDLL_BASE, KERNEL32_BASE, USER32_BASE):
            self.uc.mem_map(base, SYSTEM_MODULE_SIZE, UC_PROT_ALL)

        esp = STACK_BASE + STACK_SIZE - 0x1000
        self.uc.reg_write(UC_X86_REG_ESP, esp)
        self.uc.reg_write(UC_X86_REG_EBP, esp)

        self.install_gdt()
        self.build_teb_peb(esp)
        self.build_loader_list()
        for base, name in (
            (NTDLL_BASE, "ntdll.dll"),
            (KERNEL32_BASE, "kernel32.dll"),
            (USER32_BASE, "user32.dll"),
        ):
            self.write_fake_pe_header(base)
            self.modules[name.lower()] = base

        # Returning to this address is the signal that the entry point returned.
        self.uc.mem_write(esp, struct.pack("<I", MAGIC_RETURN))

    def install_gdt(self) -> None:
        """Give FS a real segment descriptor.

        `UC_X86_REG_FS_BASE` is a NO-OP on x86-32 in Unicorn 2 -- it warns and
        does nothing -- so `fs:[0x30]` reads linear address 0x30 and every TEB
        and PEB access silently reads unmapped memory. The only way to get a
        non-flat FS on 32-bit is genuine segmentation: build descriptors in
        emulated memory, point GDTR at them, and load a selector.

        Installing a GDT means EVERY segment register must then be loaded
        explicitly. Unicorn's defaults are all selector 0x0000, which is flat and
        32-bit while no GDT exists but resolves to the NULL DESCRIPTOR the moment
        one does. That silently clears the stack segment's D/B bit, so `push ebp`
        truncates ESP to 16 bits: the first run after adding the GDT died on
        instruction 1 writing 0x0000EFFC instead of 0x002FEFFC.
        """
        entries = {
            0: gdt_entry(0, 0, 0, 0),
            SEL_CODE >> 3: gdt_entry(0, 0xFFFFF, ACCESS_CODE_R0, FLAGS_PAGE_32),
            SEL_DATA >> 3: gdt_entry(0, 0xFFFFF, ACCESS_DATA_R0, FLAGS_PAGE_32),
            # Byte granularity with limit 0xFFF, exactly as Windows maps the TEB:
            # one page, so `lsl fs` reports 0xFFF the way real user mode does.
            SEL_TEB_R3 >> 3: gdt_entry(TEB_ADDR, 0xFFF, ACCESS_DATA_R3, FLAGS_BYTE_32),
        }
        for index, entry in entries.items():
            self.uc.mem_write(GDT_ADDR + index * 8, entry)

        self.uc.reg_write(UC_X86_REG_GDTR, (0, GDT_ADDR, GDT_SIZE - 1, 0))
        self.uc.reg_write(UC_X86_REG_CS, SEL_CODE)
        for reg in (UC_X86_REG_DS, UC_X86_REG_ES, UC_X86_REG_SS):
            self.uc.reg_write(reg, SEL_DATA)
        self.uc.reg_write(UC_X86_REG_FS, SEL_TEB_R3)
        log(f"GDT at 0x{GDT_ADDR:08X}: CS=0x{SEL_CODE:02X} DS/ES/SS=0x{SEL_DATA:02X} "
            f"FS=0x{SEL_TEB_R3:02X} -> TEB 0x{TEB_ADDR:08X}")

    def build_teb_peb(self, esp: int) -> None:
        """Populate the TEB and PEB fields a protected loader actually reads.

        BeingDebugged and NtGlobalFlag are the two classic anti-debug reads; the
        OS version fields gate feature checks; and ExceptionList must hold the
        end-of-chain sentinel or a SEH walk runs off into unmapped memory.
        """
        def poke(base: int, offset: int, value: int, size: int = 4) -> None:
            fmt = {1: "<B", 2: "<H", 4: "<I"}[size]
            self.uc.mem_write(base + offset, struct.pack(fmt, value))

        # NT_TIB
        poke(TEB_ADDR, 0x00, SEH_END_OF_CHAIN)          # ExceptionList
        poke(TEB_ADDR, 0x04, STACK_BASE + STACK_SIZE)   # StackBase (high address)
        poke(TEB_ADDR, 0x08, STACK_BASE)                # StackLimit (low address)
        poke(TEB_ADDR, 0x18, TEB_ADDR)                  # Self
        poke(TEB_ADDR, 0x20, 0x1234)                    # ClientId.UniqueProcess
        poke(TEB_ADDR, 0x24, 0x5678)                    # ClientId.UniqueThread
        poke(TEB_ADDR, 0x2C, TEB_ADDR + 0xE10)          # ThreadLocalStoragePointer
        poke(TEB_ADDR, 0x30, PEB_ADDR)                  # ProcessEnvironmentBlock
        poke(TEB_ADDR, 0x34, 0)                         # LastErrorValue

        # PEB
        poke(PEB_ADDR, 0x00, 0, 1)                      # InheritedAddressSpace
        poke(PEB_ADDR, 0x01, 0, 1)                      # ReadImageFileExecOptions
        poke(PEB_ADDR, 0x02, 0, 1)                      # BeingDebugged  <-- anti-debug
        poke(PEB_ADDR, 0x03, 0, 1)                      # SpareBool
        poke(PEB_ADDR, 0x08, self.image_base)           # ImageBaseAddress
        poke(PEB_ADDR, 0x0C, LDR_ADDR)                  # Ldr
        poke(PEB_ADDR, 0x18, HEAP_BASE)                 # ProcessHeap
        poke(PEB_ADDR, 0x64, 1)                         # NumberOfProcessors
        poke(PEB_ADDR, 0x68, 0)                         # NtGlobalFlag  <-- anti-debug
        poke(PEB_ADDR, 0xA4, 5)                         # OSMajorVersion   (XP)
        poke(PEB_ADDR, 0xA8, 1)                         # OSMinorVersion
        poke(PEB_ADDR, 0xAC, 2600, 2)                   # OSBuildNumber
        poke(PEB_ADDR, 0xB0, 2)                         # OSPlatformId (VER_PLATFORM_WIN32_NT)
        poke(PEB_ADDR, 0xB4, 2)                         # ImageSubsystem (WINDOWS_GUI)

        self.uc.mem_write(esp - 0x40, b"\x00" * 0x40)

    def build_loader_list(self) -> None:
        """Build a walkable PEB_LDR_DATA with three modules.

        Packers routinely locate kernel32 by walking
        `fs:[0x30] -> PEB.Ldr -> InMemoryOrderModuleList` rather than calling
        GetModuleHandle, so an empty or self-referential list is a dead end.

        The subtle part is that LIST_ENTRY links point at the *field*, not at the
        start of the record: InLoadOrder links point to entry+0x00, InMemoryOrder
        to entry+0x08, InInitializationOrder to entry+0x10. A walker recovers the
        record by subtracting the matching offset, so all three chains must be
        threaded through their own field or the walk reads garbage.
        """
        entries_addr = LDR_ADDR + 0x100
        entry_size = 0x48
        modules = [
            (self.image_base, self.pe.OPTIONAL_HEADER.SizeOfImage,
             EMULATED_EXE_PATH, "THUG2.exe"),
            (NTDLL_BASE, SYSTEM_MODULE_SIZE, r"C:\WINDOWS\system32\ntdll.dll", "ntdll.dll"),
            (KERNEL32_BASE, SYSTEM_MODULE_SIZE, r"C:\WINDOWS\system32\kernel32.dll", "kernel32.dll"),
        ]

        # PEB_LDR_DATA: Length, Initialized, SsHandle, then three LIST_ENTRY heads.
        self.uc.mem_write(LDR_ADDR + 0x00, struct.pack("<I", 0x28))
        self.uc.mem_write(LDR_ADDR + 0x04, struct.pack("<I", 1))
        self.uc.mem_write(LDR_ADDR + 0x08, struct.pack("<I", 0))

        for chain, head_off, link_off in (
            ("load", 0x0C, 0x00),
            ("memory", 0x14, 0x08),
            ("init", 0x1C, 0x10),
        ):
            head = LDR_ADDR + head_off
            nodes = [head] + [entries_addr + i * entry_size + link_off for i in range(len(modules))]
            # Circular doubly-linked list: each node's Flink is the next, Blink
            # the previous, wrapping through the head.
            for i, node in enumerate(nodes):
                flink = nodes[(i + 1) % len(nodes)]
                blink = nodes[(i - 1) % len(nodes)]
                self.uc.mem_write(node, struct.pack("<II", flink, blink))

        for i, (base, size, full_name, base_name) in enumerate(modules):
            entry = entries_addr + i * entry_size
            full_ptr = self.static_string(full_name, wide=True)
            base_ptr = self.static_string(base_name, wide=True)
            self.uc.mem_write(entry + 0x18, struct.pack("<I", base))          # DllBase
            self.uc.mem_write(entry + 0x1C, struct.pack("<I", base + 0x1000))  # EntryPoint
            self.uc.mem_write(entry + 0x20, struct.pack("<I", size))          # SizeOfImage
            self.write_unicode_string(entry + 0x24, full_name, full_ptr)      # FullDllName
            self.write_unicode_string(entry + 0x2C, base_name, base_ptr)      # BaseDllName
            self.uc.mem_write(entry + 0x34, struct.pack("<I", 0x4000))        # Flags
            self.uc.mem_write(entry + 0x38, struct.pack("<H", 0xFFFF))        # LoadCount
        log(f"PEB_LDR_DATA at 0x{LDR_ADDR:08X} with {len(modules)} walkable modules")

    def write_unicode_string(self, addr: int, text: str, buffer_ptr: int) -> None:
        """UNICODE_STRING is {USHORT Length, USHORT MaximumLength, PWSTR Buffer},
        where Length is in BYTES and excludes the terminator."""
        byte_len = len(text) * 2
        self.uc.mem_write(addr, struct.pack("<HHI", byte_len, byte_len + 2, buffer_ptr))

    def write_fake_pe_header(self, base: int) -> None:
        """A minimal but structurally valid PE header at a fake module base.

        A packer that finds kernel32 through the loader list usually then parses
        its PE header to reach the export directory. Without a header there, that
        walk faults immediately and the reason is invisible; with one, it either
        proceeds or fails somewhere specific enough to diagnose.
        """
        self.uc.mem_write(base, b"MZ" + b"\x00" * 0x3A + struct.pack("<I", 0x40))
        nt = base + 0x40
        self.uc.mem_write(nt, b"PE\x00\x00")
        self.uc.mem_write(nt + 4, struct.pack("<HHIIIHH", 0x014C, 0, 0, 0, 0, 0xE0, 0x210E))
        self.uc.mem_write(nt + 0x18, struct.pack("<H", 0x010B))            # PE32 magic
        self.uc.mem_write(nt + 0x18 + 0x1C, struct.pack("<I", base))       # ImageBase
        self.uc.mem_write(nt + 0x18 + 0x38, struct.pack("<I", SYSTEM_MODULE_SIZE))
        self.uc.mem_write(nt + 0x18 + 0x5C, struct.pack("<I", 16))         # NumberOfRvaAndSizes

    def alloc_stub(self, name: str, argc: int = 0) -> int:
        if name in self.stub_by_name:
            return self.stub_by_name[name]
        addr = self.next_stub
        self.next_stub += 1
        self.stubs[addr] = (name, argc)
        self.stub_by_name[name] = addr
        return addr

    def fill_imports(self) -> None:
        """Point every IAT slot at a stub. SafeDisc rewrites the real imports at
        runtime, but the static table still names what the loader stage needs."""
        if not hasattr(self.pe, "DIRECTORY_ENTRY_IMPORT"):
            log("no static import directory")
            return
        count = 0
        for entry in self.pe.DIRECTORY_ENTRY_IMPORT:
            dll = entry.dll.decode("latin1")
            for imp in entry.imports:
                name = imp.name.decode("latin1") if imp.name else f"{dll}#{imp.ordinal}"
                addr = self.alloc_stub(name, ARG_COUNTS.get(name, 0))
                if imp.address:
                    self.uc.mem_write(imp.address, struct.pack("<I", addr))
                count += 1
        log(f"IAT filled: {count} imports across {len(self.pe.DIRECTORY_ENTRY_IMPORT)} DLLs")

    # --- API dispatch ---------------------------------------------------

    def handle_api(self, name: str, args: list[int]) -> int:
        self.api_calls[name] += 1
        if len(self.api_order) < 400:
            self.api_order.append(name)
        self.api_tail.append(name)
        if len(self.api_tail) > 20:
            self.api_tail.pop(0)

        # Log the arguments that reveal INTENT: which file, which key, which
        # string. Without this the trace only says "it called GetFileAttributesA",
        # not what it was hunting for.
        if name in STRING_ARG0:
            log(f"    {name}('{self.read_cstr(args[0], wide=name.endswith('W'))}')", always=True)
        elif name in STRING_ARG1 and len(args) > 1:
            log(f"    {name}(.., '{self.read_cstr(args[1], wide=name.endswith('W'))}')", always=True)

        if name == "GetProcAddress":
            proc = self.read_cstr(args[1]) if args[1] > 0xFFFF else f"#{args[1]}"
            # A real export of a real loaded module resolves to real code. Only
            # fall back to a stub for the system DLLs we do not have.
            module = self.modules_by_base.get(args[0])
            if module is not None:
                target = module.exports.get(proc)
                if target:
                    self.resolved_exports[f"{module.name}!{proc}"] += 1
                    return target
                log(f"    GetProcAddress({module.name}, '{proc}') -> NOT EXPORTED", always=True)
                return 0
            return self.alloc_stub(proc, ARG_COUNTS.get(proc, 0))

        if name in ("LoadLibraryA", "LoadLibraryW", "LoadLibraryExA", "LoadLibraryExW"):
            module = self.read_cstr(args[0], wide=name.endswith("W")) if args[0] else "self"
            if module == "self":
                return self.image_base
            base = self.load_emulated_module(module)
            if base:
                return base
            key = module.lower()
            if key not in self.modules:
                self.modules[key] = self.next_module
                self.next_module += FAKE_MODULE_STEP
            return self.modules[key]

        if name in ("GetModuleHandleA", "GetModuleHandleW"):
            module = self.read_cstr(args[0], wide=name.endswith("W")) if args[0] else "self"
            if module == "self":
                return self.image_base
            key = module.replace("/", "\\").rsplit("\\", 1)[-1].lower()
            if key in self.loaded_modules:
                return self.loaded_modules[key].base
            if key not in self.modules:
                self.modules[key] = self.next_module
                self.next_module += FAKE_MODULE_STEP
            return self.modules[key]

        if name in ("VirtualAlloc", "VirtualAllocEx", "HeapAlloc", "LocalAlloc", "GlobalAlloc"):
            size = args[1] if name.startswith("Virtual") else args[2] if name == "HeapAlloc" else args[1]
            zero = name == "HeapAlloc" and len(args) > 1 and (args[1] & 0x08)  # HEAP_ZERO_MEMORY
            return self.alloc_heap(max(size, 0x1000), zero=zero or name.startswith("Virtual"))

        if name in ("HeapReAlloc", "LocalReAlloc", "GlobalReAlloc"):
            # These return a POINTER, not a boolean. Returning 1 here handed the
            # DLL's CRT the address 0x1 and it faulted fetching from 0x11.
            old = args[2] if name == "HeapReAlloc" else args[0]
            size = args[3] if name == "HeapReAlloc" else args[1]
            block = self.alloc_heap(max(size, 0x1000), zero=True)
            if old and block:
                copy = min(size, self.heap_sizes.get(old, size))
                try:
                    self.uc.mem_write(block, bytes(self.uc.mem_read(old, copy)))
                except UcError:
                    pass
            return block

        if name in ("HeapSize", "GlobalSize", "LocalSize"):
            target = args[2] if name == "HeapSize" else args[0]
            return self.heap_sizes.get(target, 0x1000)

        if name in ("GlobalLock", "LocalLock", "GlobalHandle"):
            return args[0] if args else 0

        if name in ("VirtualProtect", "VirtualProtectEx"):
            # Report the OLD protection the caller asked for; some code checks it.
            if len(args) > 3 and args[3]:
                self.uc.mem_write(args[3], struct.pack("<I", 0x20))  # PAGE_EXECUTE_READ
            return 1

        if name in ("VirtualFree", "HeapFree", "FlushInstructionCache"):
            return 1

        if name in ("WriteProcessMemory",):
            # (hProcess, lpBaseAddress, lpBuffer, nSize, lpNumberOfBytesWritten)
            #
            # SafeDisc patches code into ITSELF through this, so a stub that
            # returns success without copying leaves the target region zeroed and
            # the subsequent jump lands in BSS. That was the observed failure:
            # GetCurrentProcess -> VirtualProtect -> WriteProcessMemory ->
            # FlushInstructionCache -> jump into zero-filled .data.
            dest, source, count = args[1], args[2], args[3]
            try:
                payload = bytes(self.uc.mem_read(source, count)) if count else b""
                self.uc.mem_write(dest, payload)
            except UcError:
                return 0
            if len(args) > 4 and args[4]:
                self.uc.mem_write(args[4], struct.pack("<I", len(payload)))
            # This bypasses the instruction-level write hook entirely, so account
            # for it by hand -- otherwise a successful decrypt through this path
            # would still report ".text writes: 0".
            self.note_text_write(dest, len(payload), via="WriteProcessMemory")
            self.process_writes.append((dest, len(payload)))
            log(f"    WriteProcessMemory(dest=0x{dest:08X}, src=0x{source:08X}, "
                f"{count} bytes){self.describe_address(dest)}", always=True)
            if payload:
                log(f"      payload: {payload[:48].hex(' ')}", always=True)
                if capstone is not None:
                    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
                    for insn in list(md.disasm(payload, dest))[:8]:
                        log(f"        0x{insn.address:08X}  {insn.mnemonic:<7} {insn.op_str}",
                            always=True)
            return 1

        if name in ("ReadProcessMemory",):
            source, dest, count = args[1], args[2], args[3]
            try:
                payload = bytes(self.uc.mem_read(source, count)) if count else b""
                self.uc.mem_write(dest, payload)
            except UcError:
                return 0
            if len(args) > 4 and args[4]:
                self.uc.mem_write(args[4], struct.pack("<I", len(payload)))
            return 1

        if name in ("CreateFileA", "CreateFileW"):
            return self.api_create_file(self.read_cstr(args[0], wide=name.endswith("W")), args)

        if name in ("ReadFile",):
            handle, buffer, count, out_read = args[0], args[1], args[2], args[3]
            handle_file = self.files.get(handle)
            if handle_file is None:
                return 0
            chunk = handle_file.read(count)
            if buffer and chunk:
                self.uc.mem_write(buffer, chunk)
            if out_read:
                self.uc.mem_write(out_read, struct.pack("<I", len(chunk)))
            self.file_reads[handle_file.name] += len(chunk)
            return 1

        if name in ("WriteFile",):
            handle, buffer, count, out_written = args[0], args[1], args[2], args[3]
            handle_file = self.files.get(handle)
            if handle_file is None:
                return 0
            payload = bytes(self.uc.mem_read(buffer, count)) if buffer and count else b""
            written = handle_file.write(payload)
            if out_written:
                self.uc.mem_write(out_written, struct.pack("<I", written))
            return 1

        if name == "SetFilePointer":
            handle_file = self.files.get(args[0])
            if handle_file is None:
                return 0xFFFFFFFF
            distance = args[1] if args[1] < 0x80000000 else args[1] - 0x100000000
            return handle_file.seek(distance, args[3]) & 0xFFFFFFFF

        if name == "GetFileSize":
            handle_file = self.files.get(args[0])
            if handle_file is None:
                return 0xFFFFFFFF
            if len(args) > 1 and args[1]:
                self.uc.mem_write(args[1], struct.pack("<I", 0))
            return len(handle_file.data)

        if name == "CloseHandle":
            self.files.pop(args[0], None)
            return 1

        if name == "DeviceIoControl":
            self.driver_calls.append((args[0], args[1]))
            log(f"    DeviceIoControl(handle=0x{args[0]:X}, code=0x{args[1]:X}) "
                f"<-- DRIVER CALL: this is where a disc-derived key would come from",
                always=True)
            # Report failure rather than fabricating a response: a made-up
            # challenge answer would produce a WRONG key and a plausible-looking
            # but garbage decrypt, which is far worse than a visible stop.
            if len(args) > 6 and args[6]:
                self.uc.mem_write(args[6], struct.pack("<I", 0))
            return 0

        # --- APIs that must FILL AN OUTPUT BUFFER -------------------------
        # Returning only a length leaves the caller parsing stack garbage,
        # which is how the loader ended up executing off the stack.
        if name in ("GetModuleFileNameA", "GetModuleFileNameW"):
            return self.write_cstr(args[1], EMULATED_EXE_PATH, args[2], name.endswith("W"))

        if name in ("GetTempPathA", "GetTempPathW"):
            return self.write_cstr(args[1], EMULATED_TEMP_PATH, args[0], name.endswith("W"))

        if name in ("GetSystemDirectoryA", "GetSystemDirectoryW"):
            return self.write_cstr(args[0], r"C:\WINDOWS\system32", args[1], name.endswith("W"))

        if name in ("GetWindowsDirectoryA", "GetWindowsDirectoryW"):
            return self.write_cstr(args[0], r"C:\WINDOWS", args[1], name.endswith("W"))

        if name in ("GetCommandLineA", "GetCommandLineW"):
            return self.static_string(f'"{EMULATED_EXE_PATH}"', name.endswith("W"))

        # --- MSVC CRT startup ---------------------------------------------
        # The extracted DLL is a normal MSVC binary, so its DllMain runs the CRT
        # initialiser before any SafeDisc code. That walks the environment block
        # as a real string, so a placeholder return value faults immediately.
        if name in ("GetEnvironmentStrings", "GetEnvironmentStringsA", "GetEnvironmentStringsW"):
            return self.environment_block(wide=name.endswith("W"))

        if name in ("FreeEnvironmentStringsA", "FreeEnvironmentStringsW"):
            return 1

        if name == "WideCharToMultiByte":
            # (CodePage, Flags, lpWideCharStr, cchWideChar, lpMultiByteStr,
            #  cbMultiByte, lpDefaultChar, lpUsedDefaultChar)
            source, cch, dest, cb = args[2], args[3], args[4], args[5]
            text = self.read_wide(source, cch)
            raw = text.encode("latin1", "replace")
            if cch != 0xFFFFFFFF:
                pass  # caller gave an explicit length; raw already matches
            if cb == 0:
                return len(raw)
            if dest:
                self.uc.mem_write(dest, raw[:cb])
            return min(len(raw), cb)

        if name == "MultiByteToWideChar":
            source, cb, dest, cch = args[2], args[3], args[4], args[5]
            text = self.read_cstr(source) if cb == 0xFFFFFFFF else self.read_bytes_str(source, cb)
            raw = text.encode("utf-16-le")
            if cch == 0:
                return len(raw) // 2
            if dest:
                self.uc.mem_write(dest, raw[: cch * 2])
            return min(len(raw) // 2, cch)

        if name == "GetStartupInfoA" or name == "GetStartupInfoW":
            if args and args[0]:
                self.uc.mem_write(args[0], b"\x00" * 0x44)
                self.uc.mem_write(args[0], struct.pack("<I", 0x44))  # cb
            return 1

        if name in ("GetACP", "GetOEMCP"):
            return 1252

        if name == "GetCPInfo":
            # {MaxCharSize, DefaultChar[2], LeadByte[12]} - single-byte codepage.
            if len(args) > 1 and args[1]:
                self.uc.mem_write(args[1], struct.pack("<I", 1) + b"?\x00" + b"\x00" * 12)
            return 1

        if name in ("GetStringTypeA", "GetStringTypeW"):
            return 1

        if name in ("IsBadReadPtr", "IsBadWritePtr", "IsBadCodePtr", "IsBadStringPtrA"):
            return 0  # 0 means the pointer is GOOD

        if name in ("GetEnvironmentVariableA", "GetEnvironmentVariableW"):
            return 0  # not found; ERROR_ENVVAR_NOT_FOUND is a normal outcome

        if name == "GetStdHandle":
            return 0xFFFFFFFF  # INVALID_HANDLE_VALUE: no console

        if name in ("GetCurrentProcess", "GetCurrentThread"):
            return 0xFFFFFFFF  # the real pseudo-handles

        if name in ("TlsAlloc",):
            self.next_tls_slot += 1
            return self.next_tls_slot

        if name == "TlsSetValue":
            self.tls[args[0]] = args[1]
            return 1

        if name == "TlsGetValue":
            return self.tls.get(args[0], 0)

        if name == "IsDBCSLeadByte":
            return 0

        # --- APIs whose SUCCESS value is not 1 ---------------------------
        # Registry functions return LSTATUS, where ERROR_SUCCESS is ZERO. The
        # generic "return 1" reported ERROR_INVALID_FUNCTION for every call and
        # left the out-parameter handle uninitialised, which is what sent the
        # first version of this harness off to EIP 0x10.
        if name.startswith("Reg"):
            if name in ("RegCreateKeyA", "RegCreateKeyW", "RegOpenKeyA", "RegOpenKeyW"):
                if len(args) > 2 and args[2]:
                    self.uc.mem_write(args[2], struct.pack("<I", self.alloc_registry_key()))
            elif name in ("RegCreateKeyExA", "RegCreateKeyExW"):
                if len(args) > 7 and args[7]:
                    self.uc.mem_write(args[7], struct.pack("<I", self.alloc_registry_key()))
                if len(args) > 8 and args[8]:
                    self.uc.mem_write(args[8], struct.pack("<I", 1))  # REG_CREATED_NEW_KEY
            elif name in ("RegOpenKeyExA", "RegOpenKeyExW"):
                if len(args) > 4 and args[4]:
                    self.uc.mem_write(args[4], struct.pack("<I", self.alloc_registry_key()))
            elif name in ("RegQueryValueExA", "RegQueryValueExW", "RegQueryValueA"):
                return 2  # ERROR_FILE_NOT_FOUND - honest, and a normal path
            return 0  # ERROR_SUCCESS

        if name in ("GetFileAttributesA", "GetFileAttributesW"):
            # A bitmask, not a boolean. Returning 1 claimed FILE_ATTRIBUTE_READONLY
            # for directories the loader was probing.
            target = self.read_cstr(args[0], wide=name.endswith("W")) if args else ""
            if not target or target.rstrip("\\").lower() in self.known_directories:
                return 0x10  # FILE_ATTRIBUTE_DIRECTORY
            if self.host_files.resolve(target) is not None:
                return 0x20  # FILE_ATTRIBUTE_ARCHIVE - a real file the loader wants
            return 0xFFFFFFFF  # INVALID_FILE_ATTRIBUTES

        if name in ("FindFirstFileA", "FindFirstFileW"):
            return 0xFFFFFFFF  # INVALID_HANDLE_VALUE

        if name in ("MessageBoxA", "MessageBoxW"):
            caption = self.read_cstr(args[2], wide=name.endswith("W")) if len(args) > 2 else ""
            body = self.read_cstr(args[1], wide=name.endswith("W")) if len(args) > 1 else ""
            log(f"    MessageBox['{caption}']: {body}  <-- the loader is REPORTING AN ERROR",
                always=True)
            return 1  # IDOK

        if name == "FormatMessageA" or name == "FormatMessageW":
            if len(args) > 4 and args[4]:
                return self.write_cstr(args[4], "The operation completed successfully.",
                                       args[5] if len(args) > 5 else 260, name.endswith("W"))
            return 0

        if name in ("GetVersion",):
            # Windows XP: build 2600, major 5, minor 1, NT platform in the high bit.
            return 0x0A280105

        if name in ("GetVersionExA", "GetVersionExW"):
            if args and args[0]:
                self.uc.mem_write(args[0] + 4, struct.pack("<IIII", 5, 1, 2600, 2))
            return 1

        if name in ("HeapCreate", "GetProcessHeap"):
            return HEAP_BASE

        if name in ("VirtualQuery", "VirtualQueryEx"):
            return 0

        if name == "GetVolumeInformationA":
            # Root path in, volume name/serial out. Give it a plausible CD volume.
            if args[1]:
                self.write_cstr(args[1], "THUG2", args[2])
            if args[3]:
                self.uc.mem_write(args[3], struct.pack("<I", 0x12345678))
            return 1

        if name in ("GetDriveTypeA", "GetDriveTypeW"):
            return 5  # DRIVE_CDROM - keeps a disc-presence scan moving

        if name == "GetLogicalDrives":
            return 0b0000_0000_0000_0000_0000_0000_0001_1100  # C:, D:, E:

        if name in ("ExitProcess", "TerminateProcess"):
            self.stop_reason = (
                f"loader called {name}({args[0] if args else '?'}) - it decided to give up. "
                "Look at the API calls just before this."
            )
            self.uc.emu_stop()
            return 0

        if name == "WaitForSingleObject":
            return 0  # WAIT_OBJECT_0. Returning 1 is WAIT_ABANDONED, which the
                      # loader reads as "another instance holds the mutex" and exits.

        if name == "GetLastError":
            return 0  # ERROR_SUCCESS; a stale ERROR_ALREADY_EXISTS also aborts it.

        if name in ("GetTickCount", "timeGetTime"):
            return 0x00100000

        if name == "QueryPerformanceCounter":
            self.uc.mem_write(args[0], struct.pack("<Q", 0x1000000))
            return 1

        if name == "IsDebuggerPresent":
            return 0

        # Unknown API. An API missing from ARG_COUNTS is cleaned up as if it took
        # ZERO stdcall arguments, so ESP drifts by 4 per real argument and the
        # damage surfaces much later as a wild pointer -- which is how three
        # separate dead ends started (CreateMutexA, RegCreateKeyA,
        # RemoveDirectoryA, each costing a full run to diagnose).
        #
        # Stopping HERE turns that into a one-line diagnosis, so it is the
        # default; --allow-unknown-api restores best-effort behaviour for a
        # function that is genuinely cdecl or genuinely nullary.
        if name not in ARG_COUNTS:
            self.unknown_apis.add(name)
            if self.stop_on_unknown_api:
                self.stop_reason = (
                    f"UNKNOWN API '{name}': no stdcall argument count, so the stack "
                    "would drift by 4 bytes per argument and the failure would "
                    "surface somewhere unrelated. Add it to ARG_COUNTS (or pass "
                    "--allow-unknown-api if it is cdecl/nullary)."
                )
                self.uc.emu_stop()
                return 0
            log(f"    UNKNOWN API '{name}' - assuming 0 stdcall args", always=True)
        return 1

    def write_cstr(self, addr: int, text: str, limit: int = 260, wide: bool = False) -> int:
        """Writes a NUL-terminated string into an emulated output buffer and
        returns its length, the way the real Win32 APIs do."""
        if not addr:
            return 0
        raw = text.encode("utf-16-le" if wide else "latin1")
        terminator = b"\x00\x00" if wide else b"\x00"
        cap = max(0, (limit * (2 if wide else 1)) - len(terminator))
        raw = raw[:cap]
        try:
            self.uc.mem_write(addr, raw + terminator)
        except UcError:
            return 0
        return len(raw) // (2 if wide else 1)

    def static_string(self, text: str, wide: bool = False) -> int:
        """Allocates a persistent string and returns a pointer to it."""
        raw = text.encode("utf-16-le" if wide else "latin1") + (b"\x00\x00" if wide else b"\x00")
        addr = self.alloc_heap(len(raw) + 16)
        self.uc.mem_write(addr, raw)
        return addr

    def alloc_heap(self, size: int, zero: bool = False) -> int:
        addr = self.heap_next
        self.heap_next = (self.heap_next + size + 0xFFF) & ~0xFFF
        if self.heap_next >= HEAP_BASE + HEAP_SIZE:
            return 0
        self.heap_sizes[addr] = size
        if zero:
            try:
                self.uc.mem_write(addr, b"\x00" * size)
            except UcError:
                pass
        return addr

    def api_create_file(self, target: str, args: list[int]) -> int:
        """Open a file the loader asks for, backed by the real build tree.

        The pivotal case is the loader opening its OWN executable: that is where
        the encrypted payload lives, so failing it puts the loader straight into
        error handling. Directory-relative paths are matched by basename, since
        the emulated path prefix is invented anyway.
        """
        lowered = target.lower()
        if "secdrv" in lowered or target.startswith("\\\\.\\"):
            self.driver_opens.append(target)
            log(f"    CreateFile({target})  <-- SafeDisc DRIVER handle", always=True)
            return SECDRV_HANDLE

        host = self.host_files.resolve(target)
        disposition = args[4] if len(args) > 4 else 3
        if host is not None:
            try:
                data = host.read_bytes()
            except OSError:
                return 0xFFFFFFFF
            handle = self.alloc_handle(EmulatedFile(target, data, host))
            log(f"    CreateFile({target}) -> {host.name} ({len(data):,} bytes), handle 0x{handle:X}",
                always=True)
            return handle

        # CREATE_NEW(1) / CREATE_ALWAYS(2) / OPEN_ALWAYS(4) create; the rest fail.
        if disposition in (1, 2, 4):
            handle = self.alloc_handle(EmulatedFile(target, b""))
            log(f"    CreateFile({target}) -> new in-memory file, handle 0x{handle:X}", always=True)
            return handle

        log(f"    CreateFile({target}) -> NOT FOUND", always=True)
        return 0xFFFFFFFF

    def load_emulated_module(self, path: str) -> int:
        """Map an in-memory PE the loader just wrote, and return its base.

        Returns 0 if the path is not one of our emulated files or is not a PE,
        so the caller can fall back to a fake handle.
        """
        key = path.replace("/", "\\").rsplit("\\", 1)[-1].lower()
        handle_file = self.emulated_by_name.get(key)
        if handle_file is None:
            host = self.host_files.resolve(path)
            if host is None:
                return 0
            payload = host.read_bytes()
        else:
            payload = bytes(handle_file.data)
        if payload[:2] != b"MZ":
            return 0
        if key in self.loaded_modules:
            return self.loaded_modules[key].base

        try:
            pe = pefile.PE(data=payload)
        except Exception as exc:  # pefile raises a bare PEFormatError
            log(f"    LoadLibrary('{path}'): not a loadable PE ({exc})", always=True)
            return 0

        size = (pe.OPTIONAL_HEADER.SizeOfImage + 0xFFF) & ~0xFFF
        base = self.next_loaded_base
        self.next_loaded_base += (size + 0xFFFFF) & ~0xFFFFF
        self.uc.mem_map(base, size, UC_PROT_ALL)
        self.uc.mem_write(base, pe.header)
        for section in pe.sections:
            data = section.get_data()
            if data:
                self.uc.mem_write(base + section.VirtualAddress, data)

        delta = base - pe.OPTIONAL_HEADER.ImageBase
        relocated = self.apply_relocations(pe, base, delta) if delta else 0
        imported = self.fill_module_imports(pe, base)
        exports = self.collect_exports(pe, base)

        entry = base + pe.OPTIONAL_HEADER.AddressOfEntryPoint if pe.OPTIONAL_HEADER.AddressOfEntryPoint else 0
        module = LoadedModule(key, base, size, exports, entry)
        self.module_sections[key] = [
            (s.Name.rstrip(b"\x00").decode("latin1", "replace"), s.VirtualAddress,
             s.VirtualAddress + max(s.Misc_VirtualSize, s.SizeOfRawData),
             s.VirtualAddress + s.SizeOfRawData)
            for s in pe.sections
        ]
        self.loaded_modules[key] = module
        self.modules_by_base[base] = module
        log(f"    LOADED {key} at 0x{base:08X} ({size // 1024} KB): "
            f"{len(exports)} exports, {imported} imports stubbed, {relocated} relocs",
            always=True)

        # Run DllMain(hinstDLL, DLL_PROCESS_ATTACH, NULL) before the caller
        # resumes. Skipping it leaves the module's CRT uninitialised, which shows
        # up as a call through a null function pointer somewhere inside it.
        if entry:
            self.deferred_call = (entry, [base, 1, 0])
        return base

    def apply_relocations(self, pe, base: int, delta: int) -> int:
        """IMAGE_REL_BASED_HIGHLOW only -- the only type a 32-bit PE emits."""
        count = 0
        if not hasattr(pe, "DIRECTORY_ENTRY_BASERELOC"):
            return 0
        for block in pe.DIRECTORY_ENTRY_BASERELOC:
            for entry in block.entries:
                if entry.type != 3 or entry.rva == 0:
                    continue
                addr = base + entry.rva
                try:
                    value = struct.unpack("<I", self.uc.mem_read(addr, 4))[0]
                    self.uc.mem_write(addr, struct.pack("<I", (value + delta) & 0xFFFFFFFF))
                    count += 1
                except UcError:
                    continue
        return count

    def fill_module_imports(self, pe, base: int) -> int:
        count = 0
        if not hasattr(pe, "DIRECTORY_ENTRY_IMPORT"):
            return 0
        for entry in pe.DIRECTORY_ENTRY_IMPORT:
            dll = entry.dll.decode("latin1")
            for imp in entry.imports:
                name = imp.name.decode("latin1") if imp.name else f"{dll}#{imp.ordinal}"
                stub = self.alloc_stub(name, ARG_COUNTS.get(name, 0))
                # imp.address is the absolute IAT slot at the PE's PREFERRED base,
                # so rebase it onto where we actually mapped the image.
                slot = base + (imp.address - pe.OPTIONAL_HEADER.ImageBase)
                try:
                    self.uc.mem_write(slot, struct.pack("<I", stub))
                    count += 1
                except UcError:
                    continue
        return count

    def collect_exports(self, pe, base: int) -> dict[str, int]:
        exports: dict[str, int] = {}
        if not hasattr(pe, "DIRECTORY_ENTRY_EXPORT"):
            return exports
        for symbol in pe.DIRECTORY_ENTRY_EXPORT.symbols:
            if symbol.address is None:
                continue
            key = symbol.name.decode("latin1") if symbol.name else f"#{symbol.ordinal}"
            exports[key] = base + symbol.address
            exports[f"#{symbol.ordinal}"] = base + symbol.address
        return exports

    def alloc_handle(self, handle_file: EmulatedFile) -> int:
        self.next_handle += 4
        self.files[self.next_handle] = handle_file
        self.created_files.append(handle_file)
        self.emulated_by_name[handle_file.name.replace("/", "\\").rsplit("\\", 1)[-1].lower()] = handle_file
        return self.next_handle

    def alloc_registry_key(self) -> int:
        """A distinct pseudo-HKEY per open, so a close/reopen pattern behaves."""
        self.next_reg_key += 4
        return self.next_reg_key

    def environment_block(self, wide: bool) -> int:
        """A real, double-NUL-terminated environment block.

        The CRT scans this before anything else runs, so it must be a genuine
        string list rather than a placeholder pointer.
        """
        cached = self.env_block_wide if wide else self.env_block_ansi
        if cached:
            return cached
        variables = [
            r"SystemRoot=C:\WINDOWS",
            r"windir=C:\WINDOWS",
            r"TEMP=C:\WINDOWS\Temp",
            r"TMP=C:\WINDOWS\Temp",
            r"PATH=C:\WINDOWS\system32;C:\WINDOWS",
            "OS=Windows_NT",
            "NUMBER_OF_PROCESSORS=1",
        ]
        text = "\0".join(variables) + "\0\0"
        raw = text.encode("utf-16-le" if wide else "latin1")
        addr = self.alloc_heap(len(raw) + 16, zero=True)
        self.uc.mem_write(addr, raw)
        if wide:
            self.env_block_wide = addr
        else:
            self.env_block_ansi = addr
        return addr

    def read_wide(self, addr: int, count: int, limit: int = 4096) -> str:
        """Read a UTF-16LE string; count == -1 means NUL-terminated."""
        if not addr:
            return ""
        try:
            if count == 0xFFFFFFFF:
                raw = bytes(self.uc.mem_read(addr, limit * 2))
                return raw.decode("utf-16-le", "ignore").split("\0")[0]
            raw = bytes(self.uc.mem_read(addr, min(count, limit) * 2))
            return raw.decode("utf-16-le", "ignore")
        except UcError:
            return ""

    def read_bytes_str(self, addr: int, count: int, limit: int = 4096) -> str:
        if not addr:
            return ""
        try:
            return bytes(self.uc.mem_read(addr, min(count, limit))).decode("latin1", "ignore")
        except UcError:
            return ""

    def read_cstr(self, addr: int, wide: bool = False, limit: int = 260) -> str:
        try:
            raw = self.uc.mem_read(addr, limit * (2 if wide else 1))
        except UcError:
            return f"<unreadable 0x{addr:X}>"
        if wide:
            text = raw.decode("utf-16-le", "ignore")
        else:
            text = raw.decode("latin1", "ignore")
        return text.split("\0")[0]

    # --- hooks ----------------------------------------------------------

    def hook_code(self, uc, address, size, _user):
        self.instructions += 1
        if self.trail is not None:
            self.trail.append(address)
        if address == MAGIC_SEH_RETURN:
            self.resume_from_seh(uc)
            return
        if address == MAGIC_CALL_RETURN:
            resume_eip, resume_esp, resume_eax = self.pending_returns.pop()
            uc.reg_write(UC_X86_REG_EAX, resume_eax & 0xFFFFFFFF)
            uc.reg_write(UC_X86_REG_ESP, resume_esp)
            uc.reg_write(UC_X86_REG_EIP, resume_eip)
            return
        if address in self.stubs:
            name, argc = self.stubs[address]
            esp = uc.reg_read(UC_X86_REG_ESP)
            ret = struct.unpack("<I", uc.mem_read(esp, 4))[0]
            args = [struct.unpack("<I", uc.mem_read(esp + 4 + 4 * i, 4))[0] for i in range(argc)]
            result = self.handle_api(name, args)
            resume_esp = esp + 4 + 4 * argc  # stdcall cleanup

            # A stub may need to run emulated code before its caller resumes --
            # LoadLibrary must call the new module's DllMain. Divert to that
            # function now and deliver `result` when it returns.
            if self.deferred_call is not None:
                target, call_args = self.deferred_call
                self.deferred_call = None
                self.pending_returns.append((ret, resume_esp, result))
                frame = resume_esp - 4 * (len(call_args) + 1)
                uc.mem_write(frame, struct.pack("<I", MAGIC_CALL_RETURN))
                for i, value in enumerate(call_args):
                    uc.mem_write(frame + 4 + 4 * i, struct.pack("<I", value & 0xFFFFFFFF))
                uc.reg_write(UC_X86_REG_ESP, frame)
                uc.reg_write(UC_X86_REG_EIP, target)
                return

            uc.reg_write(UC_X86_REG_EAX, result & 0xFFFFFFFF)
            uc.reg_write(UC_X86_REG_ESP, resume_esp)
            uc.reg_write(UC_X86_REG_EIP, ret)
            return

        if self.trace_remaining > 0:
            self.trace_remaining -= 1
            self.disassemble_one(address, size)

    def note_text_write(self, address: int, size: int, via: str, eip: int | None = None) -> None:
        """Record a write into the encrypted .text range, whatever its channel.

        Instruction-level stores arrive through hook_write; WriteProcessMemory
        arrives from the API layer and would otherwise be invisible, which would
        make a successful decrypt look like no decrypt at all.
        """
        if not (self.text_start <= address < self.text_end) or size <= 0:
            return
        for offset in range(0, size, 0x1000):
            page = (address + offset) & ~0xFFF
            self.text_writes[page] = self.text_writes.get(page, 0) + min(0x1000, size - offset)
        self.write_sources[eip if eip is not None else self.uc.reg_read(UC_X86_REG_EIP)] += 1
        self.write_channels[via] += size

    def hook_write(self, uc, _access, address, size, value, _user):
        if self.text_start <= address < self.text_end:
            self.note_text_write(address, size, via="store", eip=uc.reg_read(UC_X86_REG_EIP))
        if self.watch_lo <= address < self.watch_hi:
            eip = uc.reg_read(UC_X86_REG_EIP)
            log(f"    WATCH write 0x{address:08X} <- 0x{value:X} ({size}B) from "
                f"0x{eip:08X}{self.describe_address(eip)}", always=True)

    def hook_invalid(self, uc, access, address, size, value, _user):
        """An access violation is an EXCEPTION, not necessarily the end.

        Protected loaders deliberately touch bad addresses and continue inside
        their own handler. Try to dispatch through the SEH chain first; only if
        nothing handles it is this genuinely fatal.
        """
        eip = uc.reg_read(UC_X86_REG_EIP)
        self.faults[eip] += 1
        if self.dispatch_exception(STATUS_ACCESS_VIOLATION, eip, address):
            # Do NOT return True here. True means "the hook mapped the memory,
            # retry the access", so Unicorn re-runs the faulting instruction
            # regardless of the EIP we just set (observed as UC_ERR_MAP). Stop
            # instead and let run()'s loop resume at the handler.
            self.restart_pending = True
            uc.emu_stop()
            return False
        self.stop_reason = (
            f"unhandled access violation: {['read','write','fetch'][min(access % 3, 2)]} "
            f"at 0x{address:08X} from EIP 0x{eip:08X}"
        )
        return False

    def hook_intr(self, uc, intno, _user):
        """Software interrupts.

        int 3 / int 0x2D are the classic anti-debug pair: with no debugger
        attached both raise an exception the program handles itself, so a harness
        that cannot dispatch one either dies here or -- worse -- looks like a
        debugger to the loader.
        """
        eip = uc.reg_read(UC_X86_REG_EIP)
        self.interrupts[intno] += 1
        code = {
            0x00: STATUS_INTEGER_DIVIDE_BY_ZERO,
            0x01: STATUS_SINGLE_STEP,
            0x03: STATUS_BREAKPOINT,
            0x06: STATUS_ILLEGAL_INSTRUCTION,
            0x2D: STATUS_BREAKPOINT,
        }.get(intno, STATUS_ILLEGAL_INSTRUCTION)
        if self.dispatch_exception(code, eip, 0):
            self.restart_pending = True
            uc.emu_stop()
            return
        self.stop_reason = f"unhandled interrupt 0x{intno:02X} at EIP 0x{eip:08X}"
        uc.emu_stop()

    # --- SEH ------------------------------------------------------------

    def read_u32(self, addr: int) -> int | None:
        try:
            return struct.unpack("<I", self.uc.mem_read(addr, 4))[0]
        except UcError:
            return None

    def dispatch_exception(self, code: int, address: int, fault_address: int) -> bool:
        """Deliver an exception to the first handler on the fs:[0] chain.

        Win32 SEH is a linked list of {Next, Handler} records rooted at
        TEB.ExceptionList. The handler is called as
            handler(ExceptionRecord, EstablisherFrame, ContextRecord, Dispatcher)
        and returns ExceptionContinueExecution(0) to resume from the (possibly
        edited) CONTEXT, or ExceptionContinueSearch(1) to defer.

        Rather than emulate the full dispatcher, this transfers control to the
        handler with a magic return address; `hook_code` picks that up and
        applies the handler's verdict. Editing CONTEXT.Eip inside the handler is
        exactly how packers use exceptions as jumps, so the resume path must read
        the register block back rather than restoring what we saved.
        """
        uc = self.uc
        head = self.read_u32(TEB_ADDR + 0x00)
        if head is None or head == SEH_END_OF_CHAIN or head == 0:
            return False
        handler = self.read_u32(head + 4)
        if not handler:
            return False

        # Repeatedly faulting at the SAME address means the handler is not
        # resolving anything and we are spinning. Report that as its own
        # condition -- it points at the ROOT fault, not at the SEH machinery.
        self.fault_repeats[address] += 1
        if self.fault_repeats[address] > 16:
            self.stop_reason = (
                f"exception loop: 0x{address:08X} faulted "
                f"{self.fault_repeats[address]} times and the handler never "
                "resolved it, so the real defect is whatever produced that address"
            )
            uc.emu_stop()
            return False

        self.seh_dispatches += 1
        if self.seh_dispatches > self.seh_limit:
            self.stop_reason = (
                f"SEH dispatch limit ({self.seh_limit}) hit - the loader is "
                "probably faulting in a loop; raise --seh-limit to see further"
            )
            uc.emu_stop()
            return False

        record = self.alloc_heap(EXR_SIZE)
        context = self.alloc_heap(CTX_SIZE)
        uc.mem_write(record, b"\x00" * EXR_SIZE)
        uc.mem_write(context, b"\x00" * CTX_SIZE)

        uc.mem_write(record + EXR_CODE, struct.pack("<I", code))
        uc.mem_write(record + EXR_FLAGS, struct.pack("<I", 0))
        uc.mem_write(record + EXR_ADDRESS, struct.pack("<I", address))
        if fault_address:
            uc.mem_write(record + EXR_NPARAMS, struct.pack("<I", 2))
            uc.mem_write(record + 0x14, struct.pack("<II", 0, fault_address))

        esp = uc.reg_read(UC_X86_REG_ESP)
        for offset, reg in (
            (CTX_EDI, UC_X86_REG_EDI), (CTX_ESI, UC_X86_REG_ESI),
            (CTX_EBX, UC_X86_REG_EBX), (CTX_EDX, UC_X86_REG_EDX),
            (CTX_ECX, UC_X86_REG_ECX), (CTX_EAX, UC_X86_REG_EAX),
            (CTX_EBP, UC_X86_REG_EBP), (CTX_EFLAGS, UC_X86_REG_EFLAGS),
        ):
            uc.mem_write(context + offset, struct.pack("<I", uc.reg_read(reg) & 0xFFFFFFFF))
        uc.mem_write(context + CTX_FLAGS, struct.pack("<I", CONTEXT_FULL))
        uc.mem_write(context + CTX_EIP, struct.pack("<I", address))
        uc.mem_write(context + CTX_ESP, struct.pack("<I", esp))
        uc.mem_write(context + CTX_CS, struct.pack("<I", SEL_CODE))
        uc.mem_write(context + CTX_SS, struct.pack("<I", SEL_DATA))
        uc.mem_write(context + CTX_FS, struct.pack("<I", SEL_TEB_R3))

        # handler(record, establisher, context, dispatcher), cdecl-style push
        # order with our magic return underneath.
        new_esp = (esp - 0x80) & ~0xF
        uc.mem_write(new_esp, struct.pack("<IIIII", MAGIC_SEH_RETURN, record, head, context, 0))
        uc.reg_write(UC_X86_REG_ESP, new_esp)
        uc.reg_write(UC_X86_REG_EIP, handler)
        self.seh_stack.append((head, context))
        log(f"    SEH: dispatching 0x{code:08X} at 0x{address:08X} to handler 0x{handler:08X}",
            always=True)
        return True

    def resume_from_seh(self, uc) -> None:
        """The handler returned; EAX is its disposition."""
        disposition = uc.reg_read(UC_X86_REG_EAX)
        head, context = self.seh_stack.pop() if self.seh_stack else (None, None)
        if context is None:
            self.stop_reason = "SEH return with no pending dispatch"
            uc.emu_stop()
            return

        if disposition == 0:  # ExceptionContinueExecution
            for offset, reg in (
                (CTX_EDI, UC_X86_REG_EDI), (CTX_ESI, UC_X86_REG_ESI),
                (CTX_EBX, UC_X86_REG_EBX), (CTX_EDX, UC_X86_REG_EDX),
                (CTX_ECX, UC_X86_REG_ECX), (CTX_EAX, UC_X86_REG_EAX),
                (CTX_EBP, UC_X86_REG_EBP),
            ):
                value = self.read_u32(context + offset)
                if value is not None:
                    uc.reg_write(reg, value)
            esp = self.read_u32(context + CTX_ESP)
            eip = self.read_u32(context + CTX_EIP)
            if esp is not None:
                uc.reg_write(UC_X86_REG_ESP, esp)
            if eip is not None:
                uc.reg_write(UC_X86_REG_EIP, eip)
            return

        # ExceptionContinueSearch: try the next record on the chain.
        nxt = self.read_u32(head + 0) if head is not None else None
        if nxt in (None, 0, SEH_END_OF_CHAIN):
            self.stop_reason = "SEH chain exhausted - no handler took the exception"
            uc.emu_stop()
            return
        uc.mem_write(TEB_ADDR + 0x00, struct.pack("<I", nxt))
        eip = self.read_u32(context + CTX_EIP) or 0
        if not self.dispatch_exception(STATUS_ACCESS_VIOLATION, eip, 0):
            self.stop_reason = "SEH chain exhausted while unwinding"
            uc.emu_stop()

    def disassemble_one(self, address: int, size: int) -> None:
        if capstone is None:
            return
        try:
            code = self.uc.mem_read(address, size)
            md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
            for insn in md.disasm(bytes(code), address):
                print(f"    0x{insn.address:08X}  {insn.mnemonic:<8} {insn.op_str}")
        except (UcError, capstone.CsError):
            pass

    # --- run ------------------------------------------------------------

    def describe_address(self, addr: int) -> str:
        """Name an address so a trail reads as a story rather than hex.

        Distinguishing "inside the extracted DLL's .text" from "in its zeroed
        BSS" is the whole difference between working code and a call through an
        uninitialised pointer.
        """
        if addr in self.stubs:
            return f"  <stub {self.stubs[addr][0]}>"
        for module in self.loaded_modules.values():
            if module.base <= addr < module.base + module.size:
                return f"  <{module.name}+0x{addr - module.base:X}{self.section_of(module, addr)}>"
        if self.image_base <= addr < self.image_base + self.pe.OPTIONAL_HEADER.SizeOfImage:
            return f"  <exe+0x{addr - self.image_base:X}>"
        if STACK_BASE <= addr < STACK_BASE + STACK_SIZE:
            return "  <STACK - executing off the stack>"
        if HEAP_BASE <= addr < HEAP_BASE + HEAP_SIZE:
            return "  <heap>"
        return ""

    def section_of(self, module: LoadedModule, addr: int) -> str:
        info = self.module_sections.get(module.name)
        if not info:
            return ""
        rva = addr - module.base
        for name, start, end, raw_end in info:
            if start <= rva < end:
                if rva >= raw_end:
                    return f" {name} ZERO-FILLED BSS"
                return f" {name}"
        return ""

    def dump_emulated_files(self, directory: Path) -> list[tuple[str, int, str]]:
        """Write out every file the loader CREATED in emulated memory.

        This is the highest-value artefact the harness produces short of a
        decrypted image: SafeDisc extracts a helper DLL to a temp file and
        LoadLibrary's it, so the extracted payload is the code that does the
        actual decryption -- readable statically, unlike anything in the exe.
        """
        directory.mkdir(parents=True, exist_ok=True)
        written: list[tuple[str, int, str]] = []
        seen: set[str] = set()
        # Iterate the CREATED list, not the open-handle table: the loader closes
        # the extracted DLL before loading it, so by exit the handle is gone.
        for handle_file in self.created_files:
            if handle_file.host_path is not None or not handle_file.data:
                continue  # host-backed: we already have it on disk
            base = handle_file.name.replace("/", "\\").rsplit("\\", 1)[-1] or "unnamed"
            name = base
            index = 1
            while name in seen:
                name = f"{base}.{index}"
                index += 1
            seen.add(name)
            payload = bytes(handle_file.data)
            (directory / name).write_bytes(payload)
            note = ""
            if payload[:2] == b"MZ":
                note = "   <-- PE image (this is the extracted SafeDisc stage)"
            written.append((name, len(payload), note))
        return written

    def text_entropy(self) -> float:
        if not self.text_start:
            return 0.0
        sample = min(self.text_end - self.text_start, 0x40000)
        return entropy(bytes(self.uc.mem_read(self.text_start, sample)))

    def run(self, max_instructions: int, trace: int) -> None:
        self.trace_remaining = trace
        self.uc.hook_add(UC_HOOK_CODE, self.hook_code)
        self.uc.hook_add(UC_HOOK_MEM_WRITE, self.hook_write)
        self.uc.hook_add(UC_HOOK_MEM_INVALID, self.hook_invalid)
        self.uc.hook_add(UC_HOOK_INTR, self.hook_intr)

        log(f"starting at entry 0x{self.entry:08X}, budget {max_instructions:,} instructions")

        # Emulation runs as a RESTART LOOP rather than one emu_start call.
        # Delivering an exception means transferring control to a handler, and a
        # memory-fault hook cannot do that in place -- Unicorn would retry the
        # faulting access. So the hook records the new state, stops, and this
        # loop resumes at whatever EIP the dispatcher installed.
        eip = self.entry
        restarts = 0
        while self.instructions < max_instructions:
            self.restart_pending = False
            budget = max_instructions - self.instructions
            try:
                self.uc.emu_start(eip, MAGIC_RETURN, count=budget)
                if not self.restart_pending:
                    if self.stop_reason == "not started":
                        self.stop_reason = ("returned from entry point or hit the "
                                            "instruction budget")
                    return
            except UcError as exc:
                if not self.restart_pending:
                    if self.stop_reason == "not started":
                        self.stop_reason = (f"{exc} at EIP "
                                            f"0x{self.uc.reg_read(UC_X86_REG_EIP):08X}")
                    return
            restarts += 1
            if restarts > self.seh_limit:
                self.stop_reason = f"exceeded {self.seh_limit} exception restarts"
                return
            eip = self.uc.reg_read(UC_X86_REG_EIP)
        if self.stop_reason == "not started":
            self.stop_reason = "hit the instruction budget"

    def report(self, dump_path: Path | None) -> None:
        print()
        print("=" * 70)
        print(f"stopped after {self.instructions:,} instructions")
        print(f"reason: {self.stop_reason}")
        print()
        print(f"API calls: {sum(self.api_calls.values())} across {len(self.api_calls)} distinct")
        for name, count in self.api_calls.most_common(20):
            print(f"    {count:6d}  {name}")
        if self.api_order:
            print(f"  first calls: {' -> '.join(self.api_order[:12])}")
        if self.api_tail:
            print(f"  last calls:  {' -> '.join(self.api_tail)}")
        if self.unknown_apis:
            print(f"  !! {len(self.unknown_apis)} API(s) with UNKNOWN stdcall arg count "
                  f"(stack drift): {', '.join(sorted(self.unknown_apis))}")

        if self.file_reads:
            print()
            print("files the loader READ (this is where the encrypted payload comes from):")
            for fname, total in self.file_reads.most_common(10):
                print(f"    {total:10,} bytes  {fname}")
        if self.driver_opens or self.driver_calls:
            print()
            print("SafeDisc DRIVER interaction:")
            for target in self.driver_opens:
                print(f"    opened {target}")
            for handle, code in self.driver_calls:
                print(f"    DeviceIoControl(handle=0x{handle:X}, code=0x{code:08X})")
            print("    ^ if the key is disc-derived, it is fetched through these")

        if self.interrupts:
            print()
            print("software interrupts (anti-debug probes are int 3 / int 0x2D):")
            for intno, count in sorted(self.interrupts.items()):
                print(f"    int 0x{intno:02X}  x{count}")
        if self.seh_dispatches:
            print(f"SEH dispatches: {self.seh_dispatches}")
        if self.trail:
            print()
            print(f"execution trail (last {len(self.trail)} instructions before the stop):")
            for addr in list(self.trail):
                print(f"    0x{addr:08X}{self.describe_address(addr)}")
        if self.faults:
            print(f"access violations: {sum(self.faults.values())} "
                  f"at {len(self.faults)} distinct EIPs")
            for eip, count in self.faults.most_common(5):
                print(f"    0x{eip:08X}  x{count}")

        written = sum(self.text_writes.values())
        span = self.text_end - self.text_start
        print()
        print(f".text writes: {written:,} bytes across {len(self.text_writes)} pages "
              f"({100 * written / span:.2f}% of the section)" if span else "no .text")
        if self.write_sources:
            print("  writer EIPs (the decryption loop):")
            for eip, count in self.write_sources.most_common(5):
                print(f"    0x{eip:08X}  {count:,} writes")
        print(f".text entropy now: {self.text_entropy():.3f} (encrypted ~7.999, real code ~6.3)")

        if self.temp_dump_dir is not None:
            written = self.dump_emulated_files(self.temp_dump_dir)
            if written:
                print()
                print(f"wrote {len(written)} emulated file(s) to {self.temp_dump_dir}:")
                for name, size, note in written:
                    print(f"    {size:10,} bytes  {name}{note}")

        if dump_path and self.text_writes:
            size = self.pe.OPTIONAL_HEADER.SizeOfImage
            dump_path.write_bytes(bytes(self.uc.mem_read(self.image_base, size)))
            print(f"\ndumped {size:,} bytes to {dump_path}")
        print("=" * 70)


# APIs whose first (or second) argument is a path/name worth seeing.
STRING_ARG0 = {
    "GetFileAttributesA", "GetFileAttributesW", "DeleteFileA", "CreateDirectoryA",
    "FindFirstFileA", "SetFileAttributesA", "GetDriveTypeA", "GetDriveTypeW",
    "GetVolumeInformationA", "GetDiskFreeSpaceA", "OutputDebugStringA",
    "LoadLibraryA", "LoadLibraryW", "GetModuleHandleA", "GetModuleHandleW",
    "lstrlenA", "SearchPathA", "GetFullPathNameA", "GetShortPathNameA",
}
STRING_ARG1 = {
    "RegCreateKeyA", "RegOpenKeyA", "RegCreateKeyExA", "RegOpenKeyExA",
    "RegQueryValueA", "RegQueryValueExA", "RegSetValueExA", "RegDeleteKeyA",
    "CopyFileA", "MoveFileA",
}


def log(message: str, always: bool = False) -> None:
    print(f"[emu] {message}")


# stdcall argument counts, so the stub can clean up the stack correctly. Only
# the ones the loader is likely to touch need to be right; unknown APIs default
# to 0 and are handled by the caller's own stack discipline in cdecl cases.
ARG_COUNTS: dict[str, int] = {
    "GetProcAddress": 2, "LoadLibraryA": 1, "LoadLibraryW": 1,
    "GetModuleHandleA": 1, "GetModuleHandleW": 1, "FreeLibrary": 1,
    "VirtualAlloc": 4, "VirtualAllocEx": 5, "VirtualFree": 3, "VirtualProtect": 4,
    "VirtualProtectEx": 5, "VirtualQuery": 3,
    "HeapAlloc": 3, "HeapFree": 3, "HeapCreate": 3, "GetProcessHeap": 0,
    "LocalAlloc": 2, "LocalFree": 1, "GlobalAlloc": 2, "GlobalFree": 1,
    "CreateFileA": 7, "CreateFileW": 7, "ReadFile": 5, "WriteFile": 5,
    "CloseHandle": 1, "SetFilePointer": 4, "GetFileSize": 2,
    "DeviceIoControl": 8,
    "GetTickCount": 0, "timeGetTime": 0, "QueryPerformanceCounter": 1,
    "QueryPerformanceFrequency": 1, "Sleep": 1,
    "IsDebuggerPresent": 0, "GetCurrentProcess": 0, "GetCurrentProcessId": 0,
    "GetCurrentThreadId": 0, "GetLastError": 0, "SetLastError": 1,
    "GetSystemInfo": 1, "GetVersion": 0, "GetVersionExA": 1,
    "GetModuleFileNameA": 3, "GetModuleFileNameW": 3,
    "GetCommandLineA": 0, "GetCommandLineW": 0,
    "GetStartupInfoA": 1, "ExitProcess": 1, "TerminateProcess": 2,
    "InitializeCriticalSection": 1, "EnterCriticalSection": 1,
    "LeaveCriticalSection": 1, "DeleteCriticalSection": 1,
    "TlsAlloc": 0, "TlsGetValue": 1, "TlsSetValue": 2,
    "UnhandledExceptionFilter": 1, "SetUnhandledExceptionFilter": 1,
    "GetSystemTimeAsFileTime": 1, "GetEnvironmentStringsW": 0,
    "GetEnvironmentStringsA": 0, "GetStartupInfoW": 1,
    "WideCharToMultiByte": 8, "MultiByteToWideChar": 6,
    # Synchronisation - their absence killed the first run.
    "CreateMutexA": 3, "CreateMutexW": 3, "OpenMutexA": 3, "ReleaseMutex": 1,
    "WaitForSingleObject": 2, "WaitForMultipleObjects": 4,
    "CreateEventA": 4, "CreateEventW": 4, "SetEvent": 1, "ResetEvent": 1,
    "CreateSemaphoreA": 4, "ReleaseSemaphore": 3,
    "CreateThread": 6, "ResumeThread": 1, "SuspendThread": 1,
    "GetCurrentThread": 0, "SetThreadPriority": 2, "ExitThread": 1,
    "InterlockedIncrement": 1, "InterlockedDecrement": 1, "InterlockedExchange": 2,
    # Process / module / system
    "OpenProcess": 3, "GetExitCodeProcess": 2, "GetModuleHandleExA": 3,
    "GetSystemDirectoryA": 2, "GetWindowsDirectoryA": 2, "GetTempPathA": 2,
    "GetDriveTypeA": 1, "GetLogicalDrives": 0, "GetVolumeInformationA": 8,
    "GetDiskFreeSpaceA": 5, "SetErrorMode": 1, "OutputDebugStringA": 1,
    "FormatMessageA": 7, "LoadLibraryExA": 3,
    # File / registry
    "FindFirstFileA": 2, "FindNextFileA": 2, "FindClose": 1,
    "GetFileAttributesA": 1, "DeleteFileA": 1, "CreateDirectoryA": 2,
    "RegOpenKeyExA": 5, "RegQueryValueExA": 6, "RegCloseKey": 1,
    "RegCreateKeyExA": 9, "RegSetValueExA": 6,
    # CRT-ish helpers a packer stub often pulls in
    "lstrlenA": 1, "lstrcpyA": 2, "lstrcatA": 2, "lstrcmpA": 2, "lstrcmpiA": 2,
    "RtlMoveMemory": 3, "RtlZeroMemory": 2, "RtlFillMemory": 3,
    "GetSystemTime": 1, "GetLocalTime": 1, "SystemTimeToFileTime": 2,
    "IsDBCSLeadByte": 1, "GetTempPathW": 2, "GetSystemDirectoryW": 2,
    "GetWindowsDirectoryW": 2, "GetDriveTypeW": 1,
    # Registry: the legacy Reg*Key (no Ex) forms take fewer arguments than
    # their Ex counterparts, and getting that wrong jumps straight to NULL.
    "RegCreateKeyA": 3, "RegCreateKeyW": 3, "RegOpenKeyA": 3, "RegOpenKeyW": 3,
    "RegQueryValueA": 4, "RegSetValueA": 5, "RegDeleteKeyA": 2,
    "RegDeleteValueA": 2, "RegEnumKeyA": 4, "RegEnumKeyExA": 8,
    "RegEnumValueA": 8, "RegFlushKey": 1, "RegConnectRegistryA": 3,
    "FormatMessageW": 7, "LocalSize": 1, "GetACP": 0, "GetOEMCP": 0,
    "GetCPInfo": 2, "GetStringTypeA": 5, "GetStringTypeW": 4,
    "SetFileAttributesA": 2, "MoveFileA": 2, "CopyFileA": 3,
    "GetShortPathNameA": 3, "GetFullPathNameA": 4, "SearchPathA": 6,
    # Directory / temp-file probing. The loader creates a scratch directory to
    # test write permission, then removes it; RemoveDirectoryA's absence here
    # drifted the stack and jumped to NULL.
    "RemoveDirectoryA": 1, "RemoveDirectoryW": 1, "CreateDirectoryW": 2,
    "GetTempFileNameA": 4, "GetTempFileNameW": 4,
    "SetCurrentDirectoryA": 1, "GetCurrentDirectoryA": 2,
    "GetDiskFreeSpaceExA": 4, "QueryDosDeviceA": 3, "GetLogicalDriveStringsA": 2,
    # File mapping - how a packer usually reads its own companion files.
    "CreateFileMappingA": 6, "CreateFileMappingW": 6, "OpenFileMappingA": 3,
    "MapViewOfFile": 5, "MapViewOfFileEx": 6, "UnmapViewOfFile": 1,
    "SetEndOfFile": 1, "GetFileType": 1, "FlushFileBuffers": 1,
    "SetFilePointerEx": 5, "GetFileSizeEx": 2, "GetFileTime": 4,
    "DuplicateHandle": 7, "GetStdHandle": 1,
    # Heap / global memory beyond the basics.
    "HeapReAlloc": 4, "HeapSize": 3, "HeapDestroy": 1, "HeapValidate": 3,
    "GlobalLock": 1, "GlobalUnlock": 1, "GlobalReAlloc": 3, "GlobalSize": 1,
    "GlobalHandle": 1, "GlobalFlags": 1,
    "LocalLock": 1, "LocalUnlock": 1, "LocalReAlloc": 3,
    "VirtualLock": 2, "VirtualUnlock": 2,
    "IsBadReadPtr": 2, "IsBadWritePtr": 2, "IsBadCodePtr": 1, "IsBadStringPtrA": 2,
    "InterlockedCompareExchange": 3, "InterlockedExchangeAdd": 2, "TlsFree": 1,
    # Environment and process.
    "GetEnvironmentVariableA": 3, "SetEnvironmentVariableA": 2,
    "ExpandEnvironmentStringsA": 3, "GetEnvironmentStrings": 0,
    "FreeEnvironmentStringsA": 1, "FreeEnvironmentStringsW": 1,
    "CreateProcessA": 10, "CreateProcessW": 10, "GetProcessAffinityMask": 3,
    "SetProcessAffinityMask": 2, "GetComputerNameA": 2, "SetConsoleCtrlHandler": 2,
    "GetSystemMetrics": 1, "GetSystemTimes": 3, "GlobalMemoryStatus": 1,
    # Exception / anti-debug surface. RaiseException and RtlUnwind matter because
    # SafeDisc uses exceptions as control flow.
    "RaiseException": 4, "RtlUnwind": 4, "CheckRemoteDebuggerPresent": 2,
    "GetThreadContext": 2, "SetThreadContext": 2, "DebugBreak": 0,
    "NtQueryInformationProcess": 5, "NtSetInformationThread": 4,
    "ZwQueryInformationProcess": 5, "ZwSetInformationThread": 4,
    "CreateToolhelp32Snapshot": 2, "Process32First": 2, "Process32Next": 2,
    "OutputDebugStringW": 1,
    # Resources / version info, both of which this exe statically imports.
    "FindResourceA": 3, "LoadResource": 2, "SizeofResource": 2, "LockResource": 1,
    "FreeResource": 1, "GetFileVersionInfoA": 4, "GetFileVersionInfoSizeA": 2,
    "VerQueryValueA": 4,
    # The one-per-DLL anchor imports SafeDisc uses to force the loader to bind
    # each library. They should never actually be CALLED before decryption, but
    # if one is, an accurate count keeps the stack straight.
    "MessageBoxA": 4, "MessageBoxW": 4, "CreateCompatibleDC": 1, "DeleteDC": 1,
    "WSAStartup": 2, "recvfrom": 6, "Direct3DCreate9": 1, "timeSetEvent": 5,
    "DirectInput8Create": 5, "_BinkOpen@8": 2,
    "CharNextA": 1, "CharPrevA": 2, "lstrcpynA": 3,
    "CoInitialize": 1, "CoUninitialize": 0, "CoCreateInstance": 5,
    "RegQueryInfoKeyA": 12, "SHGetSpecialFolderPathA": 4,
    # The rest of the MSVC CRT startup surface. The extracted SafeDisc DLL is an
    # ordinary MSVC binary, so its DllMain runs a full CRT init before any
    # protection code executes, and every one of these gets called.
    "SetHandleCount": 1, "SetStdHandle": 2, "GetConsoleMode": 2,
    "GetConsoleCP": 0, "GetConsoleOutputCP": 0, "WriteConsoleA": 5, "WriteConsoleW": 5,
    "InitializeCriticalSectionAndSpinCount": 2, "SetCriticalSectionSpinCount": 2,
    "GetLocaleInfoA": 4, "GetLocaleInfoW": 4, "SetLocaleInfoA": 3,
    "GetUserDefaultLCID": 0, "GetSystemDefaultLCID": 0, "GetThreadLocale": 0,
    "SetThreadLocale": 1, "IsValidCodePage": 1, "IsValidLocale": 2,
    "EnumSystemLocalesA": 2, "EnumSystemLocalesW": 2,
    "LCMapStringA": 6, "LCMapStringW": 6, "CompareStringA": 6, "CompareStringW": 6,
    "GetTimeZoneInformation": 1, "GetDateFormatA": 6, "GetTimeFormatA": 6,
    "FileTimeToLocalFileTime": 2, "FileTimeToSystemTime": 2,
    "GetCurrentDirectoryW": 2, "SetEnvironmentVariableW": 2,
    "GetEnvironmentVariableW": 3, "GetModuleFileNameExA": 4,
    "GetProcessTimes": 5, "GetSystemTime": 1,
    "DecodePointer": 1, "EncodePointer": 1,
    "IsProcessorFeaturePresent": 1, "GetNativeSystemInfo": 1, "HeapSetInformation": 4,
    # Every remaining import of the extracted SafeDisc DLL, enumerated from its
    # own import table rather than discovered one crash at a time.
    "WriteProcessMemory": 5, "ReadProcessMemory": 5, "GetHandleInformation": 2,
    "OpenEventA": 3, "TerminateThread": 2, "FatalAppExitA": 2,
    "ReportEventA": 9, "DeregisterEventSource": 1, "RegisterEventSourceA": 2,
    "IsValidSecurityDescriptor": 1, "InitializeSecurityDescriptor": 2,
    "SetSecurityDescriptorDacl": 4,
    # wsprintfA is CDECL and variadic, so the CALLER cleans the stack: zero is
    # the correct stdcall-cleanup count here, not a missing entry.
    "wsprintfA": 0, "wsprintfW": 0,
    "GetKeyboardType": 1, "DefWindowProcA": 4, "DestroyWindow": 1,
    "BeginPaint": 2, "EndPaint": 2, "PostQuitMessage": 1, "CreateWindowExA": 12,
    "ShowWindow": 2, "UpdateWindow": 1, "LoadIconA": 2, "LoadCursorA": 2,
    "RegisterClassA": 1, "LoadStringA": 4, "LoadAcceleratorsA": 2,
    "GetMessageA": 4, "TranslateAcceleratorA": 3, "TranslateMessage": 1,
    "DispatchMessageA": 1, "PostMessageA": 4,
}


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("exe", type=Path)
    ap.add_argument("--max-instructions", type=int, default=20_000_000)
    ap.add_argument("--trace", type=int, default=0, help="Disassemble the first N instructions")
    ap.add_argument("--dump", type=Path, help="Write the emulated image here if .text was written")
    ap.add_argument("--allow-unknown-api", action="store_true",
                    help="Continue past an API with no known stdcall arg count "
                         "instead of stopping (the stack will drift)")
    ap.add_argument("--dump-temp-files", type=Path,
                    help="Write every file the loader created in emulated memory here. "
                         "SafeDisc extracts a helper DLL this way, and that DLL is the "
                         "code that actually decrypts .text")
    ap.add_argument("--watch", metavar="LO-HI",
                    help="Log every write into this hex address range with the writing "
                         "EIP, e.g. --watch 100F2600-100F2700")
    ap.add_argument("--trail", type=int, default=0, metavar="N",
                    help="Keep the last N executed addresses and print them on stop, "
                         "annotated with module and section. The fastest way to see "
                         "HOW execution reached a bad address")
    ap.add_argument("--seh-limit", type=int, default=10_000,
                    help="Stop after this many SEH dispatches (catches fault loops)")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if not args.exe.is_file():
        print(f"not found: {args.exe}", file=sys.stderr)
        return 2

    emu = SafeDiscEmulator(args.exe, args.verbose)
    emu.stop_on_unknown_api = not args.allow_unknown_api
    emu.seh_limit = args.seh_limit
    emu.temp_dump_dir = args.dump_temp_files
    if args.trail:
        emu.trail = deque(maxlen=args.trail)
    if args.watch:
        lo, _, hi = args.watch.partition("-")
        emu.watch_lo, emu.watch_hi = int(lo, 16), int(hi or lo, 16)
    emu.map_image()
    emu.map_support()
    emu.fill_imports()
    emu.run(args.max_instructions, args.trace)
    emu.report(args.dump)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
