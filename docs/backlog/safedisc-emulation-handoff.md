# Completed reference — THUG2 SafeDisc protected-input decryptor

Updated 2026-08-09. Facts below are measured from the exact protected build or
its loader runtime unless a sentence is explicitly labelled **Inference**.

> **Status: ✅ Done.** The end-to-end protected-input implementation, standalone
> PE writer, reproducibility gates, and smoke record are complete. This file is
> retained under `docs/backlog` as a handoff/evidence record, not as active work.

## Current result

The protected-input solution is now mapped end to end. The production entry
point is `tools/safedisc/thug2_safedisc_decrypt.py`: it takes the exact
protected `THUG2.exe` and the owner's CD1 BIN, runs the protected loader to the
game OEP, restores every remaining lazy SafeDisc record from protected/runtime
state, and emits a five-section standalone PE. It does not extract or copy the
CD3 no-CD executable.

The final artifact below came from a fresh format-2, one-command run of the
protected executable plus CD1—not from the historical CD3 extraction:

> **FINAL BUILD RECORD**
>
> - emitted file: `TestOutput/THUG2_decrypted_from_protected.exe`
> - size: `2,695,168` bytes (`0x292000`)
> - SHA-256: `f7ca9c1d0e4eed40808ce3dec6a9df854c0236c4916aa6d09cb1a3405d2676ae`
> - fresh loader run: natural OEP after `231,636,087` instructions; format-2
>   checkpoint is unconditional, has no register overrides, and matches its
>   `main.runtime.bin` SHA-256
> - structure: x86, five core sections, OEP RVA `0x22583D`, 11 import
>   descriptors/193 thunks, zero SafeDisc control-transfer residue
> - reproducibility: fresh checkpoint replay and optional-oracle replay are
>   byte-identical to the one-command result; normalized oracle comparison has
>   zero core-section differences
> - smoke: remained alive for 12 seconds with game window title, loaded
>   `binkw32` plus all expected DirectX/Win32 DLL families, then the exact PID
>   was terminated; no matching process remained

The retained fresh runtime evidence is in
`TestOutput/THUG2_decrypted_end_to_end_v2.safedisc-work`; the complete tee is
`TestOutput/THUG2_decrypted_end_to_end_v2_driver.log`.

The exact supported inputs are:

| Input | Size | SHA-256 |
|---|---:|---|
| protected `Setup/Data/Game/THUG2.exe` | 3,926,726 | `c34ea46e041d08d7d85565a262473c29b90ed8a4d5b740d6cc04d4fe48d52347` |
| reauthored scene CD1 `rld-thua.bin` | verified by the profile | `5e8b570d999b88ad9ffad1ffe152b9af9cd342fbde6aeba561b9ff504183e68f` |
| scene CD1 PVD user data | 2,048 | `aeafd7863d68d0ae1aca3652177f57a5e0e0213c179839c18783579be00520e1` |

Neither input is modified. The tool refuses a different protected hash, disc
hash/PVD, section layout, an existing output, or an existing work directory.

## Exact decryptor pipeline

The default command is one fail-closed pipeline:

```powershell
$exe = "Sample/Builds/Tony Hawks Underground 2 (2004-10-4, Windows - Final)/Setup/Data/Game/THUG2.exe"
$disc = "C:/path/to/THUG2/CD1/rld-thua.bin"

python tools/safedisc/thug2_safedisc_decrypt.py $exe `
  --disc $disc `
  --output TestOutput/THUG2_decrypted_from_protected.exe
```

The default work directory is
`TestOutput/THUG2_decrypted_from_protected.safedisc-work`. It preserves the
complete emulator log, `checkpoint.json`, `main.runtime.bin`, `heap.bin`,
`PfdRun.pfd`, and the extracted/runtime SecServ and AuthServ modules.

The stages are deliberately ordered:

1. Verify the exact protected hash, seven-section layout, image base
   `0x00400000`, and the complete CD1/profile identities.
2. Run `safedisc_emu.py` with `--thug2-retail-disc-profile`,
   `--thug2-sd3-key-repair`, `--fake-secdrv`, and an explicit stop at the game
   OEP `0x0062583D`. The published SD3 v40 repair described below is part of
   this loader run, not a post-hoc plaintext patch.
3. Accept only a format-2 checkpoint whose stop reason says it reached that
   OEP, whose SHA-256 binds the exact `main.runtime.bin`, and whose conditional
   metadata proves that no diagnostic register override was configured. The
   earlier SecServ return at `0x100160B9`/`EAX=0x258` is not sufficient.
4. Use the checkpoint-attested SecServ key to reconstruct the three protected
   import vectors and all 30 encoded API-name records from the protected EXE.
5. Restore three `stxt` import calls, 77 redirect records, 18
   permuted `FF 15` operands, and 113 Alt/INT3 fragments from the exact
   SecServ heap/module state and `PfdRun.pfd`.
6. Require 11 import descriptors/193 thunks, the complete 11-record SafeDisc
   restoration table, a valid CRT initializer array, and zero transfers into
   either SafeDisc `stxt` section or RVA `0x1D79`.
7. Normalize each disk IAT from its recovered INT, retain the original five
   game sections, remove both protection-added `stxt` sections, set OEP RVA
   `0x22583D`, set the import directory to RVA/size `0x27A564/0xF0`, and emit
   a `0x292000`-byte standalone PE. Require the deterministic protected-input
   SHA-256 before creating the output with exclusive, no-overwrite semantics.

Advanced checkpoint finalization is available with `--memory` and the required
`--checkpoint`. Legacy/conditional manifests are rejected. Those files must be
siblings of the matching `heap.bin`, `PfdRun.pfd`, and signature-matched
SecServ runtime image. An explicit `--import-key-hex` is diagnostic-only and
can only cross-check the live checkpoint attestation; it cannot replace it.

## SafeDisc 3 v40 key repair

This was the key correction that made the loader's broad `.text` and `.data`
passes produce plaintext.

THUG2 AuthServ has the unique v40 direct-TablePtr signature at runtime RVA
`0x23B04`; SecServ has no HookDecodeTable signature. Therefore the applicable
published SafeDiscLoader2 branch does **not** call `CallDecrypt(3/2/4)` and
does **not** copy 1,014 bytes. Its exact contract is:

- AuthServ raw-key base is RVA `0x3E6F0`;
- table 2 is at `base+0x555`, table 3 at `base+0xA82`;
- the build copies all 1,024 table-3 bytes through direct storage-page
  pointers;
- after AuthServ `DllMain`, raw table 3 is copied to SecServ `SecondCopy`
  slot 2 and `ThirdCopy` slot 4;
- the derived page is table 3 with each little-endian dword XORed by
  `first_dword(table1) ^ first_dword(table2) = 0x2F94504C`;
- at AuthServ RVA `0x23CA6`, the HookCDCheck replacement writes that complete
  derived page to `FirstCopy` slot 3, sets `EAX=0x01020050`, resumes at RVA
  `0x23CAF`, and skips the original virtual media-check call.

The full derived storage page has SHA-256
`eb9a171da0255bf122fd0aa5c172680db5b0d8a5b7368d0ca36de4c14dfb7009`.
The hook validates that slot 4 still holds the raw source; slot 2 is allowed to
have passed through SafeDisc's expected CRC transforms. The corrected run
reached the OEP after 231,636,087 instructions and captured the broad loader
transforms before any offline repair.

## Import vectors and names

The runtime restoration table at main RVA `0x3E1000` begins with **11**, not
3. It contains one six-byte header for every import descriptor. Exactly three
headers carry the protected flag:

| Index | DLL | Imports | SecServ seed |
|---:|---|---:|---:|
| 7 | `USER32.dll` | 22 | `0x48351DEF` |
| 8 | `GDI32.dll` | 1 | `0x0027B0F8` |
| 9 | `ADVAPI32.dll` | 7 | `0x99CA2F1D` |

The separate SecServ value 3 counts protected vectors, not table records. The
other eight descriptors are already represented in plaintext in the protected
input.

At SecServ `0x100454C6`, the loader exposes the exact three ciphertext-vector
inputs and a single 16-byte key:
`0192892b9117f0c3718e9fcebf140a37`. The checkpoint records input and
pre-scatter output hashes for counts 22, 1, and 7. CJump retries are collapsed
only when every attested field agrees.

The offline reconstruction reproduces SecServ rather than guessing names:

- `0x454C6`: LCG whitening plus 32-round little-endian TEA pair decryption;
- `0x4596E`: the keyed permutation/scatter of the decoded RVAs;
- `0x453CB`: overlapping trailing-block handling, whitening, and TEA decode of
  each encoded API-name buffer (including its NUL);
- inverse `0x45300`: every decoded name must re-encrypt byte-for-byte to the
  protected source before any write occurs.

The restored descriptors and counts are `binkw32` 15, `WS2_32` 17, `d3d9` 1,
`WINMM` 2, `DINPUT8` 1, `DSOUND` 2, `KERNEL32` 114, `USER32` 22, `GDI32` 1,
`ADVAPI32` 7, and `WSOCK32` 11: 11 descriptors and 193 imports total.

## Three stxt import-call repairs

SecServ's static table at runtime RVA `0xB0444` contains three
`(return_rva, iat_rva)` pairs and a null terminator. It restores these exact
six-byte calls:

| Main site RVA | Restored call |
|---:|---|
| `0x0E1913` | `FF 15 08 50 64 00` — `ADVAPI32!RegQueryValueExA` |
| `0x0E2C29` | `FF 15 30 52 64 00` — `USER32!InvalidateRect` |
| `0x0E2C37` | `FF 15 28 52 64 00` — `USER32!ShowWindow` |

The input must be either the exact protected `E9` residue targeting an `stxt`
RVA or the already-restored call. No CD3 bytes supply this table.

## Seventy-seven redirect records

At the OEP there are exactly 77 `E8 rel32` markers targeting main RVA
`0x1D79`. SecServ runtime `[base+0xAF484]` points into `heap.bin` at the
corresponding dictionary:

- header count 77 and serialized size `0x125B`;
- 128 `u16` occupancy flags at `+8`;
- records at `+0x108 + slot*0xFC`;
- record key `u32(+0x28) ^ 0x2EF77DB9`;
- payload length `u32(+0xC8)+1`, exactly 5;
- special flags at `+0xCC:+0xCE`;
- payload bytes at `+0xCE`.

For a marker at RVA `r`, its lookup key is the little-endian first dword of
`MD5(<I r+5>)`. All 77 marker keys and all 77 dictionary records match
one-to-one. Exactly 76 occupied records carry flags `00 00`; the sole special
record is slot 18/key `0x61065B12`, carries flags `01 01`, and materializes its
five-byte payload plus one trailing zero. That protected-only distinction
restores `81 C6 B8 1E 00 00` at RVA `0x56AAB` rather than leaving a live
`0xCC` byte in the immediate operand. The finalizer validates all records and
386 output bytes before mutating the image; unused/duplicate records, changed
flags, a non-five-byte payload, or a remaining marker is fatal.

## Eighteen permuted FF15 operands

SafeDisc separately permutes selected `FF 15` IAT operands. The finalizer
validates SecServ's live manager, per-descriptor records, masks, item objects,
and three registered main-image ranges before selecting a site.

For a site offset `x`, the exact selector is:

```text
x ^= SecServ_929BD(x)
x ^= SecServ_928A8(x)
selected iff (x & 3) < 2
```

Both `0x115`-byte selector bodies are hash-pinned. A candidate must reference
one of the three protected FirstThunk arrays and its live IAT value must equal
that item's dispatcher thunk; this excludes direct API entries. With manager
seed `u32(manager+0x26)`, the selected position is advanced as follows:

```text
step = (seed + site_offset) % descriptor_import_count
do position = (position - step) % descriptor_import_count
while descriptor_mask[position] is clear
```

The replacement operand is the mapped item's validated IAT address. Exact
provenance counts are 49 protected-IAT candidates, 48 dispatcher-backed
candidates, 19 selector-true sites, and 18 changed operands; one selected site
cycles to itself. All four counts are required.

## Alt/INT3 fragments are solved protected-only

The former “about 90 unresolved CC holes” conclusion is obsolete. They are
SafeDisc Alt fragments whose typed rows live in protected `PfdRun.pfd`.

Four PFD payload chunks (`0x600C..0x700C`, `0x7018..0x8018`,
`0x8024..0x9024`, and `0x9030..0x9104`) are decoded with the native
overlapping TEA operation, the PFD password whitening, and the inner TEA pass.
They concatenate to 625 20-byte rows (`0x30D4` bytes), SHA-256
`6183dd3faba576114e6987368e9d6e1b0ea811a7821efbdcc76d7c42651dc4f7`.

The exact selector is cheap enough to scan the full `.text` on one CPU:

```text
context = (site_rva * 0x215D7FC6) mod 2^32
digest  = MD5(<II site_rva, context>)
row key = big_endian_u32(digest[0:4])
decoded = row.encoded16 XOR repeat(digest[4:8])
```

An authenticated row has this decoded schema:

```text
control, 00, 00, payload[8], 00, digest[12:16]
```

`control` must be 1 through 8 and the target must still be exactly
`CC * control`. The restored bytes are
`payload[:control] XOR 0xFA`. Sites and rows must be unique and writes may not
overlap.

The build-specific protected-only census is exact:

- 291 typed row matches in `.text`;
- 178 inactive type-7 padding records (176 `CC CC -> CC CC` shapes and two
  `CC 90 -> 90 90` shapes);
- 113 active unique fragments touching 89 original maximal CC runs;
- 287 restored bytes;
- controls `{2: 92, 3: 8, 6: 12, 7: 1}`.

`tools/safedisc/pfd_alt_cpu_recover.py` reproduces the scan in about three
seconds. It writes `TestOutput/pfd_alt_cpu_manifest.json` and compares it to the
pinned `tools/safedisc/pfd_alt_cpu_manifest.json`, SHA-256
`0ec909193fc5c9f78a8acebc38e2a3456e988d0f82cdcf2114b965e08d5654cc`.
Applying only this Alt layer to the OEP runtime image yields SHA-256
`68da0ff4e983274259b32afce67cc4cc815c1551175e6266c9c0a24abfc4616a`.
The proof uses no plaintext executable; the CD3 comparison reports zero
mismatches only after reconstruction.

## Completion gates and standalone layout

The finalizer must pass all of these before creating the output path:

- exact protected input plus a format-2, unconditional OEP checkpoint whose
  SHA-256 binds `main.runtime.bin`;
- exact sibling `heap.bin`, `PfdRun.pfd`, and one signature-matched SecServ
  runtime image;
- 30 protected imports reconstructed and re-encryption-verified;
- exactly 3 stxt calls, 77 redirect records/386 bytes, 18 FF15 operand changes,
  and 113 Alt fragments/287 bytes;
- import-restoration table count 11, with protected records exactly
  `(index,count) = (7,22),(8,1),(9,7)` and payload ending at RVA `0x3E1063`;
- 11 import descriptors and 193 valid name/ordinal thunks plus a null
  descriptor;
- zero rel32 transfers into RVA `0x3DF000..0x3E5FFF` and zero `E8` transfers
  to RVA `0x1D79`;
- CRT initializer range RVA `0x27C000..0x27C13B`: one zero sentinel followed
  by 78 pointers into the recovered `.text`;
- final five-section layout, OEP, import directory, output size, and canonical
  SHA-256 `f7ca9c1d0e4eed40808ce3dec6a9df854c0236c4916aa6d09cb1a3405d2676ae`.

The standalone sections are `.text`, `.rdata`, `.data`, `.tls`, and `.rsrc`.
The two protection sections are omitted, their section headers are zeroed, the
IAT directory is cleared for normalization, and `SizeOfImage` becomes
`0x3DF000`.

## CD3 is validation-only

The supplied CD3 contains a same-build Reloaded no-CD executable, SHA-256
`52fc88849654b34839ec2f96bff3a8c0b7a855df9a207aab9f2fca2e6bd440f3`.
It was invaluable for falsifying incomplete intermediate dumps and validating
the independently recovered transforms. It is not a production input and
does not complete the protected-input decryption goal by itself.

`--oracle` is optional. It hash-checks that exact CD3 executable, maps it for a
comparison, changes its 264 `RLD!\0` padding tags to `CC`, and normalizes only
two structurally guarded inter-function padding gaps (`125B:1260` and
`3409:3410`) in the temporary comparison buffer. The five core sections then
match byte-for-byte. Oracle bytes are never returned by a decoder or copied to
the output.

`tools/safedisc/thug2_cd3_recover.py` remains a strict historical
extraction/validation helper. Its output is a scene no-CD executable, not the
output of the protected loader and not a substitute for this pipeline.

## Retail-disc profile retained by the emulator

The supplied scene CD1 is the correct release but was reauthored. The in-memory
retail profile supplies the measured master view without modifying the BIN:

- descriptor user-data sectors at LBAs 16–19;
- volume ID `THUG2_1`, volume size 293,015 sectors, retail ISO/Joliet roots
  24/58 aliased to scene roots 27/61, and creation timestamp
  `2004091416305100`;
- the decoded application-use record `C-Dilla\0`, protected band
  `0x3C4..0x2850`, seed `0xB7EC1EFB`, and low/high margins 7/7;
- 584 deliberately invalid Mode-1 sectors, classified by:

```text
h = (((lba ^ 0xB7EC1EFB) * 0x5A6D) + 0x6A7F) mod 2^32
bad iff (((h >> 8) & 0xFF) % 16) == 0
```

Bad `READ(10)` requests report SCSI `CHECK CONDITION`, `MEDIUM ERROR
03/11/00`. AuthServ's raw-block `MODE SELECT(6)` reports transport success
with `ILLEGAL REQUEST 05/26/00`, selecting the designed 2,048-byte fallback.
On the emulated Windows XP predicates, the decrypted timing helper bypasses
the legacy timing verdict, so no invented distance delay is required.

## Corrected conclusions and guardrails

- The runtime table count at main RVA `0x3E1000` is 11. The value 3 is the
  number of protected import vectors, not the record count.
- THUG2 uses the v40 direct-TablePtr SD3 branch: 1,024-byte storage pages and
  no `CallDecrypt(3/2/4)` sequence. The earlier 1,014-byte/manual-selector
  interpretation was the wrong SafeDiscLoader2 branch.
- AuthServ's original CD-check virtual call must be skipped by HookCDCheck.
  Calling it after installing the derived page overwrites `FirstCopy` and
  reproduces the old parser failure.
- A SafeDisc CKey storage page is encoded storage, not a raw logical key.
  Directly writing a guessed logical seed produces a plausible but wrong
  transform.
- The CC/INT3 population is resolved: 113 authenticated Alt fragments, not an
  oracle-selected patch list.
- The 77 redirects and 18 FF15 changes are selected by runtime dictionaries,
  masks, objects, and hash functions, not a CD3-derived site list.
- Do not force verdict registers or use `--set-reg` to make a deliverable.
  Success paths populate state consumed by later transforms. `--set-reg` is
  diagnosis-only, and the report labels every run configured with it as
  conditional even if the requested address is never reached.
- `0xABADDADA` is only one CKey input feeder, not a standalone file-local
  decrypt key.
- SecServ `.text` was not wholesale decrypted at runtime; the observed
  changes were CJumpRun patches.
- `0x007E210F` is on the success route to the game OEP, not the failure branch.

## Tools and evidence

| File | Purpose |
|---|---|
| `tools/safedisc/thug2_safedisc_decrypt.py` | One-command protected-input loader run, protected/runtime restoration, completion gates, and standalone PE writer |
| `tools/safedisc/safedisc_emu.py` | Unicorn loader/Win32/SCSI model, retail-disc profile, published v40 key repair, OEP checkpoint, and import-vector capture |
| `tools/safedisc/safedisc_emu_selftest.py` | Focused emulator, disc, timing, SCSI, and SD3 regression tests |
| `tools/safedisc/pfd_alt_cpu_recover.py` | Pure-CPU proof of the exact PFD Alt selector and 113-fragment population |
| `tools/safedisc/pfd_query_bb8_3fc.bin` | The 625-row PFD 3FC capture that proof authenticates against (SHA-gated) |
| `tools/safedisc/pfd_alt_cpu_manifest.json` | Pinned expected manifest for that proof |
| `tools/safedisc/ff15_runtime_selector_proof.py` | Runtime-only FF15 selector/permutation proof |
| `tools/safedisc/thug2_cd3_recover.py` | Historical strict CD3 extraction for validation; not the decryptor |
| `tools/safedisc/safedisc_string_decrypt.py` | Recovered SafeDisc string cipher |
| `tools/safedisc/safedisc_deobfuscate.py` | Junk-jump linearizer and call-site analysis |
| `tools/safedisc/iso9660_reader.py` | MODE1/2352 reader and ISO9660 walker |

The maintained suite is tracked under `tools/safedisc/`; runtime captures and
work directories remain ignored under `TestOutput/`. Keep new regression
probes fixture-free when possible.

Both proofs read runtime state that is deliberately not tracked. They resolve it
from exactly two retained paths, and a `TestOutput` sweep must preserve them:

| Retained path | Needed by | Regenerate with |
|---|---|---|
| `TestOutput/THUG2_decrypted_end_to_end_v2.safedisc-work` | both proofs (`main.runtime.bin`, `heap.bin`, `~df394b.tmp.runtime.bin`) | `thug2_safedisc_decrypt.py` (a fresh run's work dir) |
| `TestOutput/thug2_cd3_crack_oracle.exe` | `ff15_runtime_selector_proof.py` (required); `pfd_alt_cpu_recover.py` (optional) | `thug2_cd3_recover.py` against the CD3 image |

Verified 2026-08-23 from the relocated scripts: the PFD proof reproduces
`output_sha256 68da0ff4…4616a` with `reference_manifest_matches=True` and zero
oracle mismatches, and the FF15 proof reports 19 selected / 18 changed sites
with zero oracle mismatches.
