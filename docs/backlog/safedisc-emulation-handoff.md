# SafeDisc emulation — handoff

Written 2026-08-08. Everything below is measured unless marked otherwise.

## The actual goal

Recover THUG2 PC's undocumented `.snd` audio codec. It lives in `THUG2.exe`'s
`.text`, which is SafeDisc-encrypted (entropy 7.999). The plan is to run the
binary's own loader under emulation until it decrypts itself, dump, then find
the codec.

Everything about SafeDisc here is a means to that end. If a shorter route to the
codec appears, take it.

## What exists

| File | What it is |
|---|---|
| `tools/diagnostics/safedisc_emu.py` | The harness. Unicorn x86-32 + enough Win32 to run the loader. Read its module docstring first — it carries the full STATUS block. |
| `tools/diagnostics/safedisc_string_decrypt.py` | SafeDisc's string cipher, recovered and verified. Seed `0xC612DB4E` for this build. Dumps 52 strings incl. the whole anti-debug surface. |
| `tools/diagnostics/safedisc_deobfuscate.py` | Linearises SafeDisc's junk-jump obfuscation (complementary conditional pairs to one target are unconditional). `--calls-only` shows what a routine *measures*. |
| `tools/diagnostics/iso9660_reader.py` | MODE1/2352 disc image reader. LBA *n* is at `n*2352 + 16`. Also walks the filesystem. |

`tools/diagnostics/` is gitignored — new files need `git add -f`.

Run it:

```
python tools/diagnostics/safedisc_emu.py \
    "Sample/Builds/Tony Hawks Underground 2 (2004-10-4, Windows - Final)/Setup/Data/Game/THUG2.exe" \
    --max-instructions 400000000 --fake-secdrv \
    --disc "<CD1>/rld-thua.bin" \
    --dump-temp-files TestOutput/safedisc_temp --dump TestOutput/thug2_unpacked.exe
```

Disc images (user-supplied, verified same release — CD1's `00000001.TMP` is
byte-identical to the build tree's, sha256 `bbcb35e6…`):
`C:\Users\mmc99\Desktop\Games\TCRF\Spider-Man Research\Media\Tony Hawk's Underground 2 (2004-10-4, PC - Final)\CD{1,2,3}\`

## Where it stands

**155,910 → 112,893,507 instructions.** `.text` writes: **0**. Entropy still
7.998 (real code would be ~6.3). **The binary is NOT decrypted.**

The loader now: resolves its API set, extracts and loads SecServ
(`~df394b.tmp`), installs the secdrv service, passes the driver handshake,
extracts and loads AuthServ (`~de36b4.tmp`), passes AuthServ's Init
(returns `0x01020050`, exactly what SecServ requires), locates the disc, and
runs the media authentication against real disc sectors.

It fails there. The media handler (AuthServ `handler[1]` = `0x10316EB0`) runs to
completion and returns **`0x48`**, decided by:

```
0x1031718C  push 0x10342F00
0x10317191  call 0x103057C0      ; must return NONZERO
0x10317199  test eax, eax        ; measured eax = 0
0x1031719B  jne  <success>
0x103171A7  mov  eax, 0x48       ; failure
```

## The blocker, precisely

`0x103057C0` is CJumpRun-virtualised: the site is patched to `jmp <heap>`, the
body is relocated, and — measured with `--stop-at 0x10317199 --trail` — the
instructions executing immediately before the verdict are at
`0x002FE8AC-0x002FE8B4`, i.e. **the stack**. The optical IOCTL constants
(`0x0004D004`, `0x00024804`, `0x00024000`, `0x00041018`) appear in **no dumped
module**. So the code computing the verdict is generated per call and can be
observed, not read.

Every earlier blocker yielded to "read the code, fix the Win32 answer". This one
will not. It needs dynamic work.

## Next step

Trace the generated block instruction by instruction and find the comparison.
Concretely: add a `--trace-range LO-HI` to `safedisc_emu.py` that disassembles
every instruction executed inside an address range (with a line cap), point it
at the stack region during the verification, and read off what it compares.
`--stop-at` already exists to bound the window.

The disc conversation it is judging is fully characterised and already answered
from the retail image: 37 `SCSI_PASS_THROUGH` covering `INQUIRY`,
`MODE SENSE(10)` page 0x2A, and `READ(10)` of LBA 16/17/18 (the ISO9660 volume
descriptor set), plus `ScsiStatus` reported GOOD. So the question is not "what
does it read" — it is "what does it compute from what it read".

## Settled — do not re-litigate

* **The disc check is NOT weak sectors / subchannel / sector timing.** It reads
  standard filesystem metadata and drive identity. All of it is answerable from
  a normal image, and is.
* **secdrv command `0x3F` is not key material.** Indexed 4-byte query, N = 0..95.
  Made seed-dependent and diffed: two very different seeds give bit-identical
  runs.
* **SECDRV.SYS supplies no disc data.** 19 imports, all ntoskrnl, no SCSI/disc/
  file/crypto API.
* **`0x3E → 0x5278D11B`** is confirmed twice over: the literature documents it,
  and `DrvMgt.Setup` literally compares against it.

## Retracted — I got these wrong, do not build on them

* **"SecServ's `.text` is decrypted at runtime."** FALSE. A diff (disk +
  relocations vs runtime) shows only 39 five-byte `jmp` patches. I measured a
  64-byte window that *started* at a patch and read its low entropy as
  plaintext.
* **"The key is file-local (`0xABADDADA`)."** FALSE as stated. There are TWO
  feeders into `CKeyMngr::Input`: the constant at `0x10003427`, and
  `0x100038D0` whose arguments come from three AuthServ channel reads. The run
  dies before the second, so only 1 of 6 key slots fills. Treating the constant
  as "the key" yields a confident offline decryptor that emits garbage.
* **"`0x7E210F` is the failure branch."** Backwards — it is the SUCCESS path,
  falling through to `jmp 0x62583d`.

## Two rules worth keeping

**Forcing a gate does not work.** The success paths have SIDE EFFECTS — they
register objects and populate tables later stages read (`--watch` proves
`[0x100BFDB0]`, which the next gate needs, is never written), and one of them
feeds the key. `--set-reg` exists for *diagnosis only*; the report prints
`REGISTER OVERRIDES APPLIED` so any run under one is visibly conditional. Never
ship an artifact produced that way.

**Acceptance is not "the run completed".** A dump is correct only if `.text`
disassembles as valid x86 and yields recognisable strings. Entropy dropping is
necessary, not sufficient.

## The reusable part

Independent of THUG2, the harness is a generic emulation-based unpacker:
protection-agnostic OEP detection (tail jump into a written page of the original
image, qualified by three conditions — the naive rule fires 16 instructions in,
because SafeDisc's opening move patches a `jmp` over its own entry point), a PE
dump writer, and **exact** import rebuilding. That last is a real advantage over
Scylla/ImpREC: every IAT slot holds one of our stubs and the emulator recorded
the name and DLL behind each, so the rebuild is a lookup, not a heuristic.
Verified end to end — the dump parses and pefile reads the rebuilt directory
back.

## Practical notes

* Every blocker so far but the last was a **fidelity gap in the emulated
  Windows**, not a protection defence. Suspect the harness before the protection.
* A patch containing escape sequences must go through a script FILE, never an
  inline heredoc — a mangled `\0` became a literal NUL four separate times.
* `--break` dumps registers, the stack top, and the frame below EBP. Breaking on
  a function's entry AND its `ret` and diffing ESP is how a stdcall
  argument-count bug was found (12 bytes adrift = 3 missing args).
* Stage only exact files. A parallel session has uncommitted work in
  `src/NeversoftMultitool/App/Tabs/TextureTab.xaml` and elsewhere.
