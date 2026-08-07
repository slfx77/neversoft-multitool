#!/usr/bin/env python3
"""Run a SafeDisc-wrapped Win32 executable's loader under emulation until it
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

STATUS (2026-08-07): WORKING FOUNDATION, NOT YET DECRYPTING.

Reaches ~155,900 instructions into the SafeDisc loader before losing the thread.
What it demonstrably does: maps the image, fills the IAT, executes real
obfuscated loader code, services 140 API calls across 12 distinct functions, and
reports both the call sequence and the arguments that show intent. The observed
behaviour is a textbook packer stub -- 34 iterations of
GlobalAlloc -> GetProcAddress -> GlobalFree (resolving its API set one at a
time), a single-instance mutex, then path construction
(GetModuleFileNameA, GetTempPathA, 27 x IsDBCSLeadByte walking the string).

KNOWN DEFECT, fix this first: `UC_X86_REG_FS_BASE` is a NO-OP on x86-32 in
Unicorn 2 -- it emits a deprecation warning and does nothing. So `fs:[0x18]`
and `fs:[0x30]` do not reach the TEB/PEB set up in `map_support`, every
anti-debug read returns unmapped-or-garbage, and that is the most likely reason
the loader drifts into error handling (GetFileAttributesA -> FormatMessageA)
instead of proceeding to decrypt. The fix is a real GDT: build descriptor
entries in emulated memory, point GDTR at them with `UC_X86_REG_GDTR`, and load
a selector into FS, rather than writing the segment base register directly.

Also still missing, in rough order of likely need:
  * a populated PEB_LDR_DATA module list (packers walk it to find kernel32
    rather than calling GetModuleHandle)
  * SEH: a working exception chain, since SafeDisc uses exceptions as control
    flow and to detect debuggers
  * argument-accurate stubs for the remaining Win32 surface; every API missing
    from ARG_COUNTS is cleaned up as 0-arg and silently drifts the stack, which
    is what caused the first three dead ends (CreateMutexA, RegCreateKeyA,
    IsDBCSLeadByte). The runner now warns on each one.
  * the eventual secdrv `DeviceIoControl`, whose response may or may not feed
    the key schedule -- that is the question this harness exists to answer.
"""

from __future__ import annotations

import argparse
import math
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

try:
    import pefile
    from unicorn import (
        UC_ARCH_X86, UC_HOOK_CODE, UC_HOOK_MEM_INVALID, UC_HOOK_MEM_WRITE,
        UC_MODE_32, UC_PROT_ALL, Uc, UcError,
    )
    from unicorn.x86_const import (
        UC_X86_REG_EAX, UC_X86_REG_EBP, UC_X86_REG_EIP, UC_X86_REG_ESP,
        UC_X86_REG_FS_BASE,
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
HEAP_BASE = 0x01000000
HEAP_SIZE = 0x04000000
STUB_BASE = 0x70000000          # one byte per stubbed export, hooked on execute
STUB_SIZE = 0x00010000
FAKE_MODULE_BASE = 0x71000000   # handles handed back by LoadLibrary/GetModuleHandle
FAKE_MODULE_STEP = 0x00010000

# The loader looks for companion files (00000001.TMP) beside the exe, so give
# it a realistic path rather than an empty buffer.
EMULATED_EXE_PATH = r"C:\Games\THUG2\THUG2.exe"
EMULATED_TEMP_PATH = r"C:\WINDOWS\Temp" + "\\"


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
        self.uc.mem_map(TEB_ADDR, 0x2000, UC_PROT_ALL)
        self.uc.mem_map(HEAP_BASE, HEAP_SIZE, UC_PROT_ALL)
        self.uc.mem_map(STUB_BASE, STUB_SIZE, UC_PROT_ALL)
        self.uc.mem_map(FAKE_MODULE_BASE, 0x01000000, UC_PROT_ALL)

        esp = STACK_BASE + STACK_SIZE - 0x1000
        self.uc.reg_write(UC_X86_REG_ESP, esp)
        self.uc.reg_write(UC_X86_REG_EBP, esp)

        # Minimal TEB/PEB. SafeDisc reads PEB.BeingDebugged and NtGlobalFlag as
        # anti-debug checks, so both must read clean or the loader bails early.
        self.uc.mem_write(TEB_ADDR + 0x18, struct.pack("<I", TEB_ADDR))   # TEB.Self
        self.uc.mem_write(TEB_ADDR + 0x30, struct.pack("<I", PEB_ADDR))   # TEB.ProcessEnvironmentBlock
        self.uc.mem_write(PEB_ADDR + 0x02, b"\x00")                        # PEB.BeingDebugged
        self.uc.mem_write(PEB_ADDR + 0x08, struct.pack("<I", self.image_base))
        self.uc.mem_write(PEB_ADDR + 0x68, struct.pack("<I", 0))           # NtGlobalFlag
        self.uc.reg_write(UC_X86_REG_FS_BASE, TEB_ADDR)

        # Returning to this address is the signal that the entry point returned.
        self.uc.mem_write(esp, struct.pack("<I", 0xDEADF00D))

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
            proc = self.read_cstr(args[1]) if args[1] > 0xFFFF else f"ordinal#{args[1]}"
            return self.alloc_stub(proc, ARG_COUNTS.get(proc, 0))

        if name in ("LoadLibraryA", "LoadLibraryW", "GetModuleHandleA", "GetModuleHandleW"):
            module = self.read_cstr(args[0], wide=name.endswith("W")) if args[0] else "self"
            if module == "self":
                return self.image_base
            if module not in self.modules:
                self.modules[module] = self.next_module
                self.next_module += FAKE_MODULE_STEP
            return self.modules[module]

        if name in ("VirtualAlloc", "VirtualAllocEx", "HeapAlloc", "LocalAlloc", "GlobalAlloc"):
            size = args[1] if name.startswith("Virtual") else args[2] if name == "HeapAlloc" else args[1]
            return self.alloc_heap(max(size, 0x1000))

        if name in ("VirtualProtect", "VirtualProtectEx", "VirtualFree", "HeapFree", "FlushInstructionCache"):
            return 1

        if name == "CreateFileA" or name == "CreateFileW":
            target = self.read_cstr(args[0], wide=name.endswith("W"))
            log(f"    CreateFile({target})", always=True)
            # A driver handle is the interesting case: if the key is
            # disc-derived it will be fetched through this.
            return 0x100 if "secdrv" in target.lower() or target.startswith("\\\\.\\") else 0xFFFFFFFF

        if name == "DeviceIoControl":
            log(f"    DeviceIoControl(handle=0x{args[0]:X}, code=0x{args[1]:X}) "
                f"<-- DRIVER CALL: the key may be disc-derived", always=True)
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

        if name == "IsDBCSLeadByte":
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

        # Unknown API: succeed, but say so. An API missing from ARG_COUNTS is
        # cleaned up as if it took ZERO arguments, which silently drifts the
        # stack and shows up later as a wild pointer -- exactly how the first
        # run died (CreateMutexA/WaitForSingleObject/ReleaseMutex were absent).
        if name not in ARG_COUNTS and name not in self.unknown_apis:
            self.unknown_apis.add(name)
            log(f"    UNKNOWN API '{name}' - assuming 0 stdcall args; add it to ARG_COUNTS", always=True)
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

    def alloc_heap(self, size: int) -> int:
        addr = self.heap_next
        self.heap_next = (self.heap_next + size + 0xFFF) & ~0xFFF
        return addr if self.heap_next < HEAP_BASE + HEAP_SIZE else 0

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
        if address in self.stubs:
            name, argc = self.stubs[address]
            esp = uc.reg_read(UC_X86_REG_ESP)
            ret = struct.unpack("<I", uc.mem_read(esp, 4))[0]
            args = [struct.unpack("<I", uc.mem_read(esp + 4 + 4 * i, 4))[0] for i in range(argc)]
            result = self.handle_api(name, args)
            uc.reg_write(UC_X86_REG_EAX, result & 0xFFFFFFFF)
            uc.reg_write(UC_X86_REG_ESP, esp + 4 + 4 * argc)  # stdcall cleanup
            uc.reg_write(UC_X86_REG_EIP, ret)
            return

        if self.trace_remaining > 0:
            self.trace_remaining -= 1
            self.disassemble_one(address, size)

    def hook_write(self, uc, _access, address, size, _value, _user):
        if self.text_start <= address < self.text_end:
            page = address & ~0xFFF
            self.text_writes[page] = self.text_writes.get(page, 0) + size
            self.write_sources[uc.reg_read(UC_X86_REG_EIP)] += 1

    def hook_invalid(self, uc, access, address, size, value, _user):
        self.stop_reason = (
            f"unmapped memory {['read','write','fetch'][min(access % 3, 2)]} "
            f"at 0x{address:08X} from EIP 0x{uc.reg_read(UC_X86_REG_EIP):08X}"
        )
        return False

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

        log(f"starting at entry 0x{self.entry:08X}, budget {max_instructions:,} instructions")
        try:
            self.uc.emu_start(self.entry, 0xDEADF00D, count=max_instructions)
            self.stop_reason = "returned from entry point or hit the instruction budget"
        except UcError as exc:
            if self.stop_reason == "not started":
                self.stop_reason = f"{exc} at EIP 0x{self.uc.reg_read(UC_X86_REG_EIP):08X}"

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
}


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("exe", type=Path)
    ap.add_argument("--max-instructions", type=int, default=20_000_000)
    ap.add_argument("--trace", type=int, default=0, help="Disassemble the first N instructions")
    ap.add_argument("--dump", type=Path, help="Write the emulated image here if .text was written")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if not args.exe.is_file():
        print(f"not found: {args.exe}", file=sys.stderr)
        return 2

    emu = SafeDiscEmulator(args.exe, args.verbose)
    emu.map_image()
    emu.map_support()
    emu.fill_imports()
    emu.run(args.max_instructions, args.trace)
    emu.report(args.dump)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
