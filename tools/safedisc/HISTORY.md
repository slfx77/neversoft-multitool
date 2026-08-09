# SafeDisc emulator development history

This is the superseded chronological debugging record formerly embedded in
`safedisc_emu.py`. It is retained for emulator-regression provenance; statements
about a current blocker or incomplete decrypt describe intermediate states and do
not override the current status in the emulator or its README.

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

THE TARGET IS KNOWN: the original entry point is 0x0062583D, reached by
`jmp 0x62583d` at 0x007E2159. That address is inside the encrypted .text
(0x00401000-0x00645000), so arriving there IS the unpack succeeding.

CURRENT BLOCKER, located to a single comparison. The gate is one boolean: the
dispatcher at 0x007E2160 must return ZERO.

    0x7E20E5  call 0x7e2160
    0x7E20EE  cmp  eax, 0
    0x7E20F1  je   0x7e210f        ; SUCCESS -> falls through to jmp 0x62583d
    0x7E20F3  mov  byte [ebx], 0xC2 ; failure: patch `ret 0xC` over its own entry
    0x7E210D  call [0x7e2029]      ; -> ExitProcess(1)

(Note the polarity: an earlier reading of 0x7E210F as the failure branch was
backwards. It is the path to the OEP.)

0x007E2160 returns 1 from any of ~14 early-outs. Thirteen now pass; the last one
does not:

    0x7E2433  call eax             ; eax = 0x1000322B = SecServ export Ox12121212
    0x7E2438  test eax, eax        ; measured eax = 1, needs 0
    0x7E243A  je   0x7e244c        ; -> xor eax,eax -> return 0 -> OEP

Inside that export the failure is one comparison deep, and the FIRST gate there
already passes:

    0x10003337  cmp eax, 0x82       ; PASSES (measured eax = 0x82)
    0x1000339A  call 0x10003060     ; (dllBase, 3, 0, &[ebp-0x28], &[ebp-0x2c])
    0x100033AA  cmp dword [ebp-0x2c], 0xFA   ; FAILS: measured 0x64 (100), wants 250
    0x100033B3  jne 0x1000360b      ; -> error return

That status is now traced end to end, and it bottoms out in the DRIVER, not in
anything we can stub away:

    0x1000304C  mov [0x667adc74], ecx    ; the status global; measured 100, wants 250
                                          ; ecx = 250 iff the classifier returns 0x10000
    0x1000138D  classifier -> returns whatever 0x100010C7 gives (measured 0x8000)
    0x100010C7  decrypts the string 'drvmgt.dll', builds a sibling path,
                LoadLibrary's it, GetProcAddress, then:
    0x100011E2  call [drvmgt export]      ; == DrvMgt.Setup(path, path)
    0x100011EB  sub eax, 0x64             ; 100 -> 0x10000 (AUTHENTIC)
                                          ; else -> 0x8000  (what we get)

DrvMgt.dll exports only Setup / Remove / _DllMain@12, and Setup is tiny:

    0x10001435  push 0x3E                        ; secdrv command 0x3E
    0x10001437  call <ioctl wrapper 0x1000135D>
    0x1000143F  cmp  eax, 0x64                   ; wrapper must return 100
    0x10001444  cmp  dword [ebp+0xc], 0x5278D11B ; and yield exactly this
    0x10001465  push 0x64                        ; -> 100 = AUTHENTIC

0x5278D11B is exactly what SafeDiscShim documents for command 0x3E
("SetupVerification"), so the response table and this binary agree
INDEPENDENTLY -- that constant is not taken on faith. `--fake-secdrv` implements
the table (default OFF; see the flag's help for why, and for the differential
seed test that validates any dump made with it on).

THE DRIVER HANDSHAKE NOW PASSES. What unblocked it was not the protection at
all: DrvMgt builds its device name with `sprintf(buf, "\\\\.\\Global\\%s",
"SecDrv")`, and the wsprintfA stub returned a length WITHOUT WRITING THE BUFFER,
so CreateFile was handed uninitialised stack -- a path of literal garbage
("LMNOPQRSTUVWXYZ[\]^_`ab..."). With a real formatter the device opens, and
command 0x3E returns 0x5278D11B, which DrvMgt.Setup accepts.

The secdrv wire protocol, read off DrvMgt.dll rather than assumed:
    request   +0x00 major(3)  +0x04 minor(0x16)  +0x08 0
              +0x0C COMMAND   +0x10 VerificationData[4]   +0x410 argument
    response  validator 0x10001000: out[0] >= 3, and if == 3 then out[4] >= 0x16
              freshness 0x10001203: GetTickCount - out[0xC] <= 400
              payload   0x10001258: delivered from out + 0x410
    ioctl code 0xEF002407, in 1300B / out 3096B, one allocation (out = in+0x514)

CURRENT STATE: 33,616,568 instructions. The sequence is now
0x3E (SetupVerification) -> 0x3C (GetDebugRegisterInfo) -> 0x3F x96, i.e. it is
doing bulk work through the driver, then faults reading 0x05000084 from SecServ
0x100115AD (RVA 0x115AD).

0x3F IS RESOLVED, and it is not key material. It is an indexed 4-byte query
(argument = {len:4, index:N}, N = 0..95, so 384 bytes total), matching
SafeDiscShim's "reject if in[0] > 0x60" rule. Made seed-dependent and diffed:
seeds 0x11111111 and 0x77777777 both stop at exactly 56,629,645 instructions,
BIT-IDENTICAL. Nothing on the reachable path consumes those bytes.

THE REAL BLOCKER, and it is structural rather than another missing API:

The media authentication lives in AuthServ, and its SUCCESS PATH HAS SIDE
EFFECTS. It does not merely return a verdict:

  * It POPULATES STATE that later stages read. --watch proves the object at
    [0x100BFDB0], which the gate after the forced one requires, is NEVER
    WRITTEN. So forcing a verdict just moves the failure: each success path
    registers objects and fills tables that the next gate consumes.
  * It FEEDS THE KEY. Scanning the decrypted SecServ image for calls to
    CKeyMngr::Input (0x10036F2F) finds exactly two, and only one is the
    file-local constant 0xABADDADA. The other, at 0x100038D0, sits in SecServ's
    command dispatcher and takes its (object, length, data) arguments straight
    from three 0x1001E3F4 channel reads -- i.e. AuthServ calls BACK into SecServ
    to deliver key bytes.

So --set-reg can advance the run (the gate at 0x10003665 passes once the verdict
at 0x10323C88 is forced, reaching 0x10003670) but cannot produce a correct key.
A forced pass would yield a plausible-looking garbage decrypt, which is exactly
the outcome this harness is built to avoid.

The later opt-in --thug2-sd3-key-repair is deliberately narrower than that
diagnostic register override: it reproduces SafeDiscLoader2's published SD3
HookCDCheck contract, including the raw/derived storage-page side effects and
the exact return value, and fails closed if those storage objects do not match.

WHAT IS STILL UNRESOLVED, and it decides everything: whether the bytes AuthServ
delivers originate from the DISC or from the FILES. Evidence both ways --
AuthServ reads THUG2.exe itself (919,858 bytes) and the driver's own data
demonstrably is not consumed, which points at file-derived; but the media check
that gates the delivery is genuine.

NEXT STEP, concrete: AuthServ's handler[0] at 0x10316AE0 is junk-jump obfuscated
(complementary conditional pairs to one target, xchg/nop filler) and cannot be
read linearly -- but the DECRYPTED image is now dumped, so it is analysable. The
obfuscation is mechanical and a linearising pass over it would expose what the
media check actually measures, and therefore whether it can be satisfied without
a disc. That is the highest-value remaining work; gate-by-gate forcing is a dead
end for the reason above.

Ruled out as the cause, both fixed anyway because both were indefensible:
  * A constant GetTickCount. Any elapsed-time measurement computed zero. The
    clock is now monotonic (instructions + requested sleeps); the run is
    bit-identical, so this gate is not a timing check.
  * CreateFile returned a valid handle for ANY \\.\ path, including \\.\NTICE,
    \\.\SICE and \\.\SIWVID -- i.e. it reported that SoftICE was installed.
    Those probes are not reached on the current path, but a successful open
    there is a debugger detection and would have fired later. The device list
    comes from decrypting the DLL's own strings, not from guesswork.

THE STATIC ROUTE IS CLOSED -- do not re-propose it. The four functions that
would let us reimplement the decryption offline (both CTransformXor and
CTransformRandomAccumulate PerformTransform overrides, CKeyBasic::GetKeyData,
CKeyMngr::InputTransformAfterPfnActivation) are INDIVIDUALLY ENCRYPTED in the
extracted DLL, even though ~84% of its .text is plaintext. Four independent
measurements agree: garbage disassembly at each export RVA, a hole in the
relocation table exactly spanning each body, page entropy 7.6-7.8 against 6.2-6.5
for ordinary code, and a known-plaintext attack that recovers only ~7 bytes
before the keystream diverges per function. (An earlier claim in this file that
the DLL's plaintext .text made the transform statically readable was WRONG.)

What the same analysis DID recover, and what it is good for:
  * The SafeDisc string cipher, verified: plain[i] = cipher[i] XOR (key & 0xFF),
    key = (0xA065432A - 0x22BC897F*key) mod 2^32, seed 0xC612DB4E for THIS build
    (the published 0x522CFDD0 is absent). See safedisc_string_decrypt.py. It
    yields the loader's anti-debug surface: \\.\NTICE, \\.\SICE, \\.\SIWVID,
    IsDebuggerPresent, NtQueryInformationProcess, ZwQuerySystemInformation,
    plus the ASPI/CD path (Wnaspi32.dll, GetASPI32SupportInfo, SendASPI32Command)
    and the self-debugging set (DebugActiveProcess, WaitForDebugEvent,
    SetThreadContext, ContinueDebugEvent).
  * A byte-exact oracle for validating a future dump: SafeDisc leaves relocated
    dwords in the clear, and 662 plaintext functions share the MSVC EH prologue
    `B8 <ehhandler> E8 <__EH_prolog>`, which pins the first 10 bytes of four
    encrypted bodies. Independently derived key bytes agree across all four
    (key[0]=key[5]=0x9A, key[6]=0xA5), so the model is sound.

PROGRESSION, so the next session can tell movement from noise. Each number is a
distinct defect fixed, and EVERY ONE was a harness fidelity bug -- an emulated
Windows that was not faithful enough -- rather than a protection defence. That
is the strongest evidence available that the approach works:
     155,910  session start
  18,866,801  GDT + SEH + file layer + PE loading of the extracted DLL
  19,911,363  FlushInstructionCache's missing ARG_COUNTS entry (see below)
  19,934,677  stub page filled with 0xC3 instead of zeros
  20,747,397  CreateFile can reopen a file the loader itself created
  24,144,845  named shared sections are real (see below) -> DrvMgt.dll loads
  48,758,772  KUSER_SHARED_DATA mapped at 0x7FFE0000
  48,767,115  a real DLL export no longer degrades to a stub

Three more worth calling out, all of the same family -- an API that reported
success without doing the thing:

  * OpenFileMappingA shared a branch with CreateFileMappingA, so it returned a
    valid handle for a section NOBODY HAD CREATED. SafeDisc opens its shared
    section first and creates it only if the open fails, so a phantom success
    made it skip its own initialisation and then read a zeroed header. (The two
    APIs also take lpName in different positions -- args[2] vs args[5] -- so the
    shared branch was reading the access mask as a file handle.) With named
    sections implemented, the loader creates exactly the three its own .data
    descriptor table describes, at sizes 2,992 / 12,500 / 49,168.
  * MapViewOfFile handed out a fresh zeroed block per call, so two views of one
    section did not alias -- which defeats the entire purpose of a shared
    section.
  * KUSER_SHARED_DATA. Windows maps this page into EVERY process at a fixed
    address, so code reads it without any API call or import to notice; its
    absence looks like a wild pointer. It also carries KdDebuggerEnabled, a
    standard anti-debug field.

Two of those deserve calling out because both hid behind an unrelated symptom:

  * FlushInstructionCache is HANDLED explicitly but had no ARG_COUNTS entry, so
    its 3 arguments were cleaned as 0 and the stack drifted 12 bytes --
    InstallJumpSystem's `ret 0xC` then returned into a data structure 16 bytes
    from where it should have gone. The unknown-API guard could not catch it
    because a handled name returns early and never reaches that check; the guard
    now runs in `alloc_stub`, at resolution time, which covers every API.
  * The emulated filesystem was write-only. SafeDisc wrote PfdRun.pfd, reopened
    it moments later, got "not found", and gave up.

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

IS THIS WINNABLE? The evidence says yes, and it is stronger than the published
literature because this harness produced it: with NO disc, NO driver, and ZERO
DeviceIoControl calls, the loader has already decrypted and written out its own
staged payloads, loaded SecServ, run its CRT, and reached its code-injection
stage. Every documented v3.20 step executed so far is file-local. `secdrv.sys`
supplies no key material -- SafeDiscShim answers it with hardcoded constants,
which only works if nothing downstream depends on their entropy.

What remains genuinely open is AuthServ (the media-authentication stage);
`~efe2.tmp` is created but never written in any run so far, and the public
writeups trail off exactly at the hop from decrypted SecServ sections to the
original .text. So do not treat "file-local" as proven for the FINAL hop. The
cheap way to settle it is a differential taint test: add a --fake-secdrv
responder, run twice with different seeds, and diff the resulting .text. Identical
output proves no driver value reaches the key schedule.

STILL MISSING, in rough order of likely need:
  * why the loader calls ExitProcess(1) (the blocker)
  * a real export directory in the synthetic kernel32/ntdll headers, for code
    that parses exports itself instead of calling GetProcAddress
  * the secdrv `DeviceIoControl`. The harness deliberately FAILS it rather than
    fabricating a challenge response: a made-up answer would produce a wrong key
    and a plausible-looking garbage decrypt, which is worse than a visible stop.
    Any responder must be opt-in behind a flag for that reason.
  * expect the dumped .text to still need import rebuilding even on success --
    CJumpRun redirects cross-module calls through runtime thunks.
