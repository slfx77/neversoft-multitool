# THUG2 SafeDisc recovery and emulation handoff

Updated 2026-08-08. Facts below are measured unless a sentence is explicitly
labelled **Inference**.

## Completed result

The requested usable unprotected executable is:

| File | Size | SHA-256 |
|---|---:|---|
| `TestOutput/THUG2_decrypted_complete.exe` | 2,695,168 | `52fc88849654b34839ec2f96bff3a8c0b7a855df9a207aab9f2fca2e6bd440f3` |

It was recovered byte-for-byte from `CRACK/THUG2.EXE` at LBA 111 of the
user-supplied CD3 `rld-thuc.bin`. It is the same game build as the protected
input: PE timestamp `0x41477593`, image base `0x00400000`, the same five core
section identities, original entry point RVA `0x22583D` (VA `0x0062583D`), and
import directory RVA/size `0x27A564/0xF0`. It has 11 populated DLL descriptors
and 193 imports.

The provenance matters: this is the no-CD executable bundled with the supplied
scene-release CD3, **not** an output of `safedisc_emu.py`, and its recovery did
not decrypt the protected file. It is also not claimed to be a pristine
publisher pre-SafeDisc link output. The bundled file contains 264 `RLD!\0`
tags in locations which are `0xCC` padding elsewhere; these are the only bytes
currently proven to be crack-only. The filename records that the user's usable
unprotected-binary goal is complete, not that every byte is pristine.

Exact source identities:

| Source | Size | SHA-256 |
|---|---:|---|
| protected `Setup/Data/Game/THUG2.exe` | 3,926,726 | `c34ea46e041d08d7d85565a262473c29b90ed8a4d5b740d6cc04d4fe48d52347` |
| supplied CD3 `rld-thuc.bin` | 801,947,328 | `cc74ac7cfc458c342fdcccd6533c0a67e3102597bbe2f8bf97782ef02786ac5d` |
| CD3 `CRACK/THUG2.EXE` | 2,695,168 | `52fc88849654b34839ec2f96bff3a8c0b7a855df9a207aab9f2fca2e6bd440f3` |

Neither input is modified.

## Reproducing the completed recovery

`tools/diagnostics/thug2_cd3_recover.py` verifies the exact protected EXE and
complete CD3 hashes before extraction. It also requires the `THUG2_3` ISO9660
identity, exact `CRACK/THUG2.EXE` path/LBA/size/hash, protected seven-section
layout, recovered five-section layout, timestamp, image base, OEP, import
directory, all 11 descriptors, and all 193 imports. It writes only after every
check passes and refuses to overwrite either input or any existing output.

The output path in this command must therefore not already exist:

```powershell
$protected = "Sample/Builds/Tony Hawks Underground 2 (2004-10-4, Windows - Final)/Setup/Data/Game/THUG2.exe"
$cd3 = "C:/Users/mmc99/Desktop/Games/TCRF/Spider-Man Research/Media/Tony Hawk's Underground 2 (2004-10-4, PC - Final)/CD3/rld-thuc.bin"

python tools/diagnostics/thug2_cd3_recover.py `
  $cd3 $protected TestOutput/THUG2_decrypted_complete.exe
```

`tools/diagnostics/thug2_cd3_recover_selftest.py` builds a fixture-free
synthetic MODE1/2352 ISO and PE, tests successful extraction, input/output
guards, and verifies that a hash failure writes nothing.

## Exact validation performed

- The recovery helper completed against the full 801,947,328-byte CD3 and
  produced the exact size and SHA-256 above.
- `pefile` parsed the result and the helper verified all five sections, the
  OEP, import directory, 11 descriptors, and 193 import thunks. Import counts
  are: `binkw32` 15, `WS2_32` 17, `d3d9` 1, `WINMM` 2, `DINPUT8` 1,
  `DSOUND` 2, `KERNEL32` 114, `USER32` 22, `GDI32` 1, `ADVAPI32` 7, and
  `WSOCK32` 11.
- Microsoft `DUMPBIN /HEADERS /IMPORTS` accepts the image. All 193 imported
  names/ordinals resolve against the supplied retail `binkw32.dll` and this
  machine's 32-bit system DLL exports.
- A clean emulator smoke run mapped all 193 imports and executed 5,000
  instructions from the real OEP through CRT startup without an invalid
  instruction or malformed-image failure. See
  `TestOutput/thug2_cd3_crack_smoke.log`.
- `python -m py_compile` and both fixture-free recovery self-tests pass.

This proves exact recovery from the supplied CD3, structural PE validity, and
early emulated startup. It does not by itself prove full gameplay or that the
scene no-CD changes are identical to an unpublished pristine executable.

## Emulator-derived artifacts are partial diagnostics

| File | Size | SHA-256 | Meaning |
|---|---:|---|---|
| `TestOutput/thug2_decrypted_candidate.exe` | 4,091,904 | `fbfa75d959a115da71370248b260befe3b456427a5eb534b604494ecbebba38e` | Authentic-loader memory dump with stale SafeDisc headers and incomplete restoration |
| `TestOutput/thug2_decrypted_final.exe` | 4,091,904 | `76d5b0d653045b44b3f8502968f56b36ef651eb4b0f4657cf5d951e7219f82ae` | Historical provisional header-finalized form; despite its name, not a complete deliverable |
| `TestOutput/thug2_decrypted_timefix_profile.exe` | 4,091,904 | `fbfa75d959a115da71370248b260befe3b456427a5eb534b604494ecbebba38e` | Corrected-profile rerun, byte-identical to the candidate |

The CD3 executable made a byte-level completeness audit possible. It disproved
the earlier inference that touching every `.text` page and obtaining coherent
code at the OEP meant the whole payload was restored:

- the first `0x4E20` bytes of `.text`, VA
  `0x00401000..0x00405E1F`, remain wrongly transformed/high-entropy ciphertext
  in the emulator dump;
- the first `0x5DC` bytes of `.data`, VA
  `0x0067C000..0x0067C5DB`, have the same gap (1,486 of 1,500 byte values differ);
- outside that bad `.text` block, the partial dump has 77 five-byte
  `E8 rel32` calls redirected to ciphertext VA `0x00401D79`. Sixty-four
  replace calls to real helpers/import thunks and 13 replace other original
  five-byte sequences. The spans cover 385 bytes, with 316 actual differing
  byte values;
- 18 `FF 15` indirect calls retain permuted IAT operands (18 differing operand
  bytes), and three six-byte stolen-text hooks replace the real imported calls
  (18 bytes);
- about 90 candidate-`0xCC` holes cover another 288 bytes where the bundled
  executable has live instructions;
- the old diagnostic `find_iat_slots` heuristic corrupted 24 bytes at six
  incidental stub-valued dwords: executable VAs `0x00442D78`, `0x004E2824`,
  and `0x0058496C`, plus `.rdata` VAs `0x0066BCB0`, `0x0066BFC0`, and
  `0x0066CAE0`. The scanner now requires a non-executable slot plus an
  executable `FF 15`/`FF 25` reference and excludes all six false positives;
- the provisional import table has only 8 populated descriptors and 163
  imports. The complete file has 11/193; `USER32` contributes 22, `GDI32` 1,
  and `ADVAPI32` 7. The formerly “authentic empty descriptors” conclusion was
  therefore wrong.

The 264 `RLD!\0` padding tags are excluded from the gap accounting above and
remain the only differences proven specific to the bundled crack. Other
differences establish that the emulator dump is incomplete; they do not prove
that every corresponding byte in the no-CD file is pristine publisher output.

`tools/diagnostics/safedisc_finalize_dump.py` remains useful for inspecting a
memory-layout dump: it validates recovered descriptors and can repoint the OEP,
Import, and IAT directories without guessing APIs. Header finalization cannot
repair ciphertext, missing instructions/imports, or protection redirects, so
its output must remain diagnostic for this run.

## Reproducing the authentic retail-disc view (diagnostic)

The supplied Reloaded CD1 BIN is the correct game release, but its ISO was
reauthored. The matching retail master measures as follows:

- exact descriptor user-data sectors at LBAs 16–19;
- volume ID `THUG2_1`, volume size 293,015 sectors, root extent LBA 24, and
  creation timestamp `2004091416305100`;
- a 64-byte encoded PVD application-use record at offset `0x4B3`, which
  AuthServ's fallback permutation decodes to `C-Dilla\0`, protected band LBA
  `0x3C4..0x2850`, seed `0xB7EC1EFB`, and low/high margins `7/7`;
- exactly 584 deliberately invalid Mode-1 sectors in that inclusive band.

For an LBA in the band, the measured bad-sector classifier is:

```text
h = (((lba ^ 0xB7EC1EFB) * 0x5A6D) + 0x6A7F) mod 2^32
bad iff (((h >> 8) & 0xFF) % 16) == 0
```

The bad retail sectors have `0x55` user/EDC/ECC bytes and invalid error
correction; the reauthored scene BIN contains valid sectors there. The four
authentic descriptor sectors are embedded in the harness as compressed
metadata. The title-specific switch aliases the retail ISO/Joliet roots
24/58 to the scene roots 27/61 and applies the verified bad-sector geometry.
All reconstruction is in memory.

Use this command for a clean run against the existing scene BIN:

```powershell
$exe = "Sample/Builds/Tony Hawks Underground 2 (2004-10-4, Windows - Final)/Setup/Data/Game/THUG2.exe"
$disc = "C:/Users/mmc99/Desktop/Games/TCRF/Spider-Man Research/Media/Tony Hawk's Underground 2 (2004-10-4, PC - Final)/CD1/rld-thua.bin"

python tools/diagnostics/safedisc_emu.py $exe `
  --disc $disc --thug2-retail-disc-profile --fake-secdrv `
  --max-instructions 600000000 `
  --dump TestOutput/thug2_decrypted_profile.exe `
  --dump-temp-files TestOutput/safedisc_profile_temp
```

`--thug2-retail-disc-profile` cannot be combined with the lower-level manual
root, marker, bad-sector, or sector-overlay switches. Those switches remain for
diagnosis and other discs. The title profile also verifies the protected EXE
and both the complete source scene BIN and its PVD by SHA-256 before applying
any reconstruction, so it cannot silently be used on a different build or disc
mastering.

## What the media check actually measures

The former conclusion that this check was only ordinary filesystem metadata
and did not involve bad sectors or timing was wrong.

Measured AuthServ behavior alternates a baseline read at LBA 964 with reads of
pseudorandom classifier-clean LBAs in the protected band. It brackets those
reads with `GetSystemTimeAsFileTime` and stores both elapsed times and LBAs for
10–20 samples. The observed path deliberately selects clean sectors; that does
not make the mastered bad-sector geometry irrelevant, because the embedded
record defines the classification and sample population.

The protected post-sampling helper has now been decrypted and replayed. On the
Windows XP state advertised by this emulator (`VER_PLATFORM_WIN32_NT`, major
version 5), two version predicates bypass the legacy timing comparison and the
helper returns zero regardless of the measured deltas. Therefore the faithful
emulator behavior needs no artificial distance-dependent delay for XP
authentication; the deltas have no verdict or key consumer on this path.
This was replayed from decrypted helper `0x103193F0`; predicates
`0x1030A0B0` (NT platform) and `0x1030A0E0` (major version >4) select the XP
bypass. Local boundary evidence is in
`TestOutput/_timing_193f0_trace_{xp,legacy_under,legacy_equal,legacy_over}.txt`.

For completeness, the inactive legacy path compares one read delta `D` with
twice the outer-loop average `A`. `D <= 2*A` enters a seven-read bad-sector
confirmation, while `D > 2*A` sets an internal classification word; both tested
paths still return zero. A downstream legacy media-read/precheck mismatch or
its adjacent internal-error path can produce `0x4D`. Controlled boundary
probes at `A=100`, `D=199/200/201` confirmed the comparison and equality
behavior.

Two fidelity corrections are now in the harness:

- Win32 time uses a deterministic 2020 UTC FILETIME epoch plus monotonic
  virtual milliseconds. `GetSystemTimeAsFileTime`, UTC/local `SYSTEMTIME`,
  timezone, and FILETIME conversion APIs now round-trip consistently.
- AuthServ's raw-block `MODE SELECT(6)` is answered as transport success with
  SCSI `CHECK CONDITION`, `ILLEGAL REQUEST 05/26/00`. AuthServ then takes its
  designed 2,048-byte fallback. The old behavior accepted 2,340/2,352-byte mode
  without actually supplying those blocks and was not faithful.

Profile-classified bad `READ(10)` requests likewise complete at the transport
layer while reporting SCSI `CHECK CONDITION`, `MEDIUM ERROR 03/11/00`, with no
invented transfer data. `tools/diagnostics/safedisc_emu_selftest.py` covers the
time conversions/monotonicity, MODE SELECT fallback, and embedded descriptor
profile hash/structure.

## Corrected full-loader diagnostic cross-check

The corrected run completed after **389,237,134 instructions**. It used the
authentic descriptor sectors, protected-sector geometry, coherent Win32 time
APIs, and MODE SELECT fallback, then stopped on the same read of `0x007E6000`
at SecServ `0x1009286B`. It did not reach the game OEP.

Its dump, `TestOutput/thug2_decrypted_timefix_profile.exe`, is byte-identical to
the earlier candidate: SHA-256
`fbfa75d959a115da71370248b260befe3b456427a5eb534b604494ecbebba38e`.
It recorded the same 2,395,382 bytes of `.text` write events across 580 pages,
the same 6.580 sampled entropy, the same `0xCED7` stolen-table count, and the
same six heuristic stub matches that the CD3 comparison later proved were
false IAT positives. See
`TestOutput/safedisc_timefix_profile_full.log` and
`TestOutput/safedisc_timefix_profile_temp`.

This disproves the missing-FILETIME/media-timing hypothesis for the stolen
text. The process loaded just before the later Joliet `58 -> 61` alias and
whole-BIN guard were added, but neither affects the already-consumed key path.
This path is useful protection research, but is no longer the source of the
completed deliverable.

## Stolen-text findings from the partial emulator path

The old authentic-profile run stopped in SecServ at `0x1009286B` on an
unhandled read of VA `0x007E6000`. Its `stxt774` record table at RVA `0x3E1000`
still had an impossible count `0xCED7`; mapping another page would only hide
the corruption. That run lacked the corrected Win32 time APIs, but the sampled
timing values are telemetry-only on the active XP path and do not feed a gate
or key. The byte-identical corrected rerun confirms those media samples do
**not** explain the bad stolen-text key/table. The unresolved earlier slot-4
transform/key path explains one part of the emulator dump's incompleteness,
not a remaining blocker to CD3 recovery.

The expected count is now statically proven to be **3**: SecServ global
`[0x100B1144]` is 3, `0x10044F2A` bounds a monotonically increasing record
index against it, and the old snapshot exposes exactly three record pairs.
That matches the three game-code jumps one-for-one. Parser `0x100927AD` only
consumes an already-plaintext table; the still-missing step is the earlier
transform that should make RVA `0x3E1000` begin with `03 00`.

An initial linear scan incorrectly reported no references. Instruction-aligned
review found three real near jumps from coherent game code into `stxt774`:

- `0x004E1913 -> 0x007DF5B5`;
- `0x004E2C29 -> 0x007DF800`;
- `0x004E2C37 -> 0x007DF81D`.

All three targets remain high-entropy garbage in the provisional dump. The CD3
executable proves the original six-byte instructions at the source sites:

- `0x004E1913`: `FF 15 08 50 64 00`,
  `call [0x00645008]` (`ADVAPI32!RegQueryValueExA`);
- `0x004E2C29`: `FF 15 30 52 64 00`,
  `call [0x00645230]` (`USER32!InvalidateRect`);
- `0x004E2C37`: `FF 15 28 52 64 00`,
  `call [0x00645228]` (`USER32!ShowWindow`).

The three `stxt774` targets do not exist in the five-section unprotected image,
which ends at VA `0x007DEDA6`; they are protection-added trampolines. Restoring
only these calls would still not make the partial dump complete because of the
larger ciphertext, redirect, instruction-hole, and import gaps documented
above. No near branch into `stxt371` was found. The protection ranges are:

- `stxt774`: VA `0x007DF000..0x007E1063`;
- `stxt371`: VA `0x007E2000..0x007E53D2`.

The SecServ parser evidence and exact same-build comparison together show that
the old parser fault is material to emulator completeness. The recovered OEP
and much of the game code are genuine, but the provisional PE must not be
described as fully decrypted.

## Tools and guardrails

| File | Purpose |
|---|---|
| `tools/diagnostics/thug2_cd3_recover.py` | Strict recovery of the bundled same-build unprotected EXE from the exact supplied CD3 |
| `tools/diagnostics/thug2_cd3_recover_selftest.py` | Fixture-free ISO/PE recovery, hash-failure, and overwrite-guard tests |
| `tools/diagnostics/safedisc_emu.py` | Unicorn x86 loader emulator, Win32/SCSI model, tracing, OEP detection, and memory-layout dump writer |
| `tools/diagnostics/safedisc_finalize_dump.py` | Makes a partial memory dump loader-readable for diagnosis; it does not prove payload completeness |
| `tools/diagnostics/safedisc_emu_selftest.py` | Focused time, SCSI, disc-profile, root-alias, and conservative IAT-scan regression tests |
| `tools/diagnostics/safedisc_string_decrypt.py` | Recovered SafeDisc string cipher |
| `tools/diagnostics/safedisc_deobfuscate.py` | Junk-jump linearizer/call-site analysis |
| `tools/diagnostics/iso9660_reader.py` | MODE1/2352 reader and ISO9660 walker |

Keep these conclusions:

- Do not use `--set-reg` or a patched verdict to make a deliverable. Success
  paths populate later state and key slots; forced gates produce conditional
  evidence at best.
- `0xABADDADA` is only one of two `CKeyMngr::Input` feeders, not a standalone
  file-local decrypt key.
- SecServ `.text` was not wholesale decrypted at runtime; the observed changes
  were CJumpRun jump patches.
- `0x007E210F` is on the success route to the game OEP, not the failure branch.
- The earlier proposed root extent `0x51D92` was not authentic. Matching retail
  metadata proves root extent 24.

`tools/diagnostics/` is gitignored, so new diagnostic files require
`git add -f`. Preserve unrelated worktree changes, especially
`src/NeversoftMultitool/App/Tabs/TextureTab.xaml`.
